using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aedis.Cli.Org;

namespace Aedis.Cli.Commands;

/// <summary>
///     <c>aedis org audit</c>: percorre os repositórios de uma organização GitHub, avalia cada um contra a régua
///     do Aedis (Best Practices por tier, proteção viva do branch, Scorecard), grava
///     <c>org-audit-summary.json</c> + <c>org-audit.md</c> e, com <c>--apply</c>, cria/atualiza o ruleset do
///     branch padrão no nível pedido. Sai com 1 enquanto algum repositório não alcançar o nível.
/// </summary>
public static class OrgAuditCommand
{
    /// <summary>Monta o comando; <paramref name="handlerFactory" /> permite simular a API nos testes.</summary>
    public static Command Create(Func<HttpMessageHandler>? handlerFactory = null) {
        var org = new Option<string>("--org") { Description = "Organização GitHub", Required = true };
        var level = new Option<int>("--level") { Description = "Nível alvo (1, 2 ou 3)", DefaultValueFactory = _ => 3 };
        var repos = new Option<string[]>("--repo") { Description = "Restringe a estes repositórios", AllowMultipleArgumentsPerToken = true };
        var apply = new Option<bool>("--apply") { Description = "Cria/atualiza o ruleset do branch padrão em cada repositório" };
        var approvals = new Option<int?>("--approvals") { Description = "Aprovações exigidas no ruleset (padrão: 0 no L1, 1 no L2/L3)" };
        var checks = new Option<string[]>("--required-check") { Description = "Contextos de check obrigatórios no ruleset; exigido com --apply (só use checks que rodam em toda PR, sem filtro de paths)", AllowMultipleArgumentsPerToken = true };
        var noBypass = new Option<bool>("--no-admin-bypass") { Description = "Não permite que administradores contornem o ruleset via PR" };
        var output = new Option<string>("--out") { Description = "Diretório do relatório", DefaultValueFactory = _ => "." };
        var token = new Option<string?>("--token") { Description = "Token do GitHub (padrão: GITHUB_TOKEN, GH_TOKEN ou `gh auth token`)" };

        var command = new Command("audit", "Audita os repositórios da organização contra a régua do Aedis e, com --apply, aplica os guardrails.") {
            org, level, repos, apply, approvals, checks, noBypass, output, token
        };
        command.SetAction(async (parse, cancellationToken) => {
            var handler = handlerFactory?.Invoke() ?? new HttpClientHandler();
            var api = new GitHubApi(new HttpClient(handler), parse.GetValue(token) ?? GitHubApi.ResolveToken());
            var targetLevel = Math.Clamp(parse.GetValue(level), 1, 3);
            var only = parse.GetValue(repos) ?? [];
            var outDir = Path.GetFullPath(parse.GetValue(output)!);
            Directory.CreateDirectory(outDir);

            var audits = new List<RepoAudit>();
            var missingChecks = false;
            foreach (var (name, branch) in await api.ListRepositoriesAsync(parse.GetValue(org)!, cancellationToken)) {
                if (only.Length > 0 && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (only.Length == 0 && name == ".github") continue; // repositório de perfil da organização, sem código
                var snapshot = await SnapshotAsync(api, parse.GetValue(org)!, name, branch, cancellationToken);
                var audit = RepoAuditor.Evaluate(snapshot, targetLevel, Directory.GetCurrentDirectory());
                if (parse.GetValue(apply) && parse.GetValue(checks) is not { Length: > 0 }) {
                    // Derivar da lista de checks verdes em main já exigiu um job filtrado por paths e travou o repositório: a escolha é do operador.
                    missingChecks = true;
                    Console.Error.WriteLine($"{name}: informe --required-check; checks verdes no último commit de {branch}: " +
                                            string.Join(" | ", await api.GetSuccessfulCheckNamesAsync(parse.GetValue(org)!, name, branch, cancellationToken)));
                }
                else if (parse.GetValue(apply)) {
                    var required = parse.GetValue(checks)!;
                    var ruleset = RulesetGuardrail.Build(targetLevel, parse.GetValue(approvals) ?? (targetLevel >= 2 ? 1 : 0), required, !parse.GetValue(noBypass));
                    audit.RulesetApplied = await api.UpsertRulesetAsync(parse.GetValue(org)!, name, ruleset, cancellationToken);
                    audit.RequiredChecks = required;
                }

                audits.Add(audit);
                Console.WriteLine($"{(audit.LevelAchieved >= targetLevel ? "[ok]  " : "[FAIL]")} {name}: L{audit.LevelAchieved}" +
                                  (audit.RulesetApplied is null ? "" : $" (ruleset {audit.RulesetApplied}, {audit.RequiredChecks.Count} checks obrigatórios)"));
                foreach (var item in audit.Items.Where(i => i.Status != "pass"))
                    Console.WriteLine($"        {(item.Status == "fail" ? "[FAIL]" : "[ ?  ]")} {item.Id} {item.Criterion}{(string.IsNullOrEmpty(item.Detail) ? "" : " — " + item.Detail)}");
            }

            if (missingChecks) return 2;
            await File.WriteAllTextAsync(Path.Combine(outDir, "org-audit-summary.json"), ToJson(parse.GetValue(org)!, targetLevel, audits), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outDir, "org-audit.md"), ToMarkdown(parse.GetValue(org)!, targetLevel, audits), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outDir, "org-audit.html"), ToHtml(parse.GetValue(org)!, targetLevel, audits), cancellationToken);
            var ok = audits.Count > 0 && audits.All(a => a.LevelAchieved >= targetLevel);
            Console.WriteLine($"relatório em {outDir} — {audits.Count(a => a.LevelAchieved >= targetLevel)}/{audits.Count} repositórios no L{targetLevel}");
            return ok ? 0 : 1;
        });
        return command;
    }

    /// <summary>Baixa arquivos sondados, workflows, regras do branch e Scorecard de um repositório.</summary>
    public static async Task<RepoSnapshot> SnapshotAsync(GitHubApi api, string org, string repo, string branch, CancellationToken ct) {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in RepoAuditor.ProbedFiles)
            if (await api.TryGetFileAsync(org, repo, path, ct) is { } content)
                files[path] = content;

        var workflows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in await api.ListDirectoryAsync(org, repo, ".github/workflows", ct))
            if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                workflows[name] = await api.TryGetFileAsync(org, repo, $".github/workflows/{name}", ct) ?? "";

        return new RepoSnapshot {
            Name = repo,
            DefaultBranch = branch,
            Files = files,
            Workflows = workflows,
            BranchRules = await api.GetBranchRulesAsync(org, repo, branch, ct),
            Scorecard = await api.GetScorecardAsync(org, repo, ct)
        };
    }

    /// <summary>Sumário JSON (schema aedis.platform/org-audit/v1) consumível pelo portal.</summary>
    public static string ToJson(string org, int targetLevel, IReadOnlyList<RepoAudit> audits) {
        var root = new JsonObject {
            ["schema"] = "aedis.platform/org-audit/v1",
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["organization"] = org,
            ["targetLevel"] = targetLevel,
            ["repositories"] = new JsonArray(audits.Select(a => (JsonNode)new JsonObject {
                ["name"] = a.Repo,
                ["defaultBranch"] = a.DefaultBranch,
                ["levelAchieved"] = a.LevelAchieved,
                ["scorecard"] = a.Scorecard is null ? null : new JsonObject { ["score"] = a.Scorecard.Score },
                ["rulesetApplied"] = a.RulesetApplied,
                ["requiredChecks"] = new JsonArray(a.RequiredChecks.Select(c => (JsonNode)c).ToArray()),
                ["items"] = new JsonArray(a.Items.Select(i => (JsonNode)new JsonObject {
                    ["id"] = i.Id, ["tier"] = i.Tier, ["criterion"] = i.Criterion, ["status"] = i.Status, ["detail"] = i.Detail
                }).ToArray())
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Relatório Markdown: tabela por repositório e lista do que falta em cada um.</summary>
    public static string ToMarkdown(string org, int targetLevel, IReadOnlyList<RepoAudit> audits) {
        var sb = new StringBuilder();
        sb.AppendLine($"# Auditoria da organização `{org}` — alvo L{targetLevel}");
        sb.AppendLine();
        sb.AppendLine("| Repositório | Nível | Scorecard | Falhas | Ruleset |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var a in audits)
            sb.AppendLine($"| {a.Repo} | L{a.LevelAchieved} | {(a.Scorecard is null ? "não publicado" : a.Scorecard.Score.ToString("0.0"))} | {a.Items.Count(i => i.Status == "fail")} | {a.RulesetApplied ?? "-"} |");
        foreach (var a in audits.Where(a => a.Items.Any(i => i.Status != "pass"))) {
            sb.AppendLine();
            sb.AppendLine($"## {a.Repo}");
            foreach (var i in a.Items.Where(i => i.Status != "pass"))
                sb.AppendLine($"- **{i.Id}** ({i.Status}) {i.Criterion}{(string.IsNullOrEmpty(i.Detail) ? "" : ": " + i.Detail)}");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Relatório HTML autocontido no estilo dos scanners (ZAP): resumo por repositório e, para cada um, todos os
    ///     critérios avaliados por tier com status e detalhe, checks obrigatórios e notas do Scorecard por check.
    /// </summary>
    public static string ToHtml(string org, int targetLevel, IReadOnlyList<RepoAudit> audits) {
        static string H(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>Auditoria {H(org)}</title><style>body{{font:14px/1.5 system-ui,sans-serif;margin:24px;color:#1f2328}}table{{border-collapse:collapse;margin:8px 0 24px}}th,td{{border:1px solid #d0d7de;padding:4px 10px;text-align:left;vertical-align:top}}th{{background:#f6f8fa}}.pass{{background:#dafbe1}}.fail{{background:#ffebe9}}.manual{{background:#fff8c5}}h2{{margin-top:32px;border-bottom:1px solid #d0d7de}}.meta{{color:#59636e}}</style></head><body>");
        sb.AppendLine($"<h1>Auditoria da organização {H(org)} — alvo L{targetLevel}</h1>");
        sb.AppendLine($"<p class=\"meta\">Gerado em {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC · régua: OpenSSF Best Practices (tiers passing/silver/gold), proteção viva do branch e OpenSSF Scorecard · {audits.Count(a => a.LevelAchieved >= targetLevel)}/{audits.Count} repositórios no L{targetLevel}</p>");
        sb.AppendLine("<table><tr><th>Repositório</th><th>Nível</th><th>Scorecard</th><th>Passa</th><th>Falha</th><th>Manual</th><th>Ruleset</th><th>Checks obrigatórios</th></tr>");
        foreach (var a in audits)
            sb.AppendLine($"<tr class=\"{(a.LevelAchieved >= targetLevel ? "pass" : "fail")}\"><td><a href=\"#{H(a.Repo)}\">{H(a.Repo)}</a></td><td>L{a.LevelAchieved}</td><td>{(a.Scorecard is null ? "não publicado" : a.Scorecard.Score.ToString("0.0"))}</td>"
                          + $"<td>{a.Items.Count(i => i.Status == "pass")}</td><td>{a.Items.Count(i => i.Status == "fail")}</td><td>{a.Items.Count(i => i.Status == "manual")}</td><td>{H(a.RulesetApplied ?? "-")}</td><td>{H(string.Join(", ", a.RequiredChecks))}</td></tr>");
        sb.AppendLine("</table>");

        foreach (var a in audits) {
            sb.AppendLine($"<h2 id=\"{H(a.Repo)}\">{H(a.Repo)} <small class=\"meta\">({H(a.DefaultBranch)}) — L{a.LevelAchieved}</small></h2>");
            sb.AppendLine("<table><tr><th>Id</th><th>Tier</th><th>Critério</th><th>Status</th><th>Detalhe</th></tr>");
            foreach (var i in a.Items)
                sb.AppendLine($"<tr class=\"{H(i.Status)}\"><td>{H(i.Id)}</td><td>{H(i.Tier)}</td><td>{H(i.Criterion)}</td><td>{H(i.Status)}</td><td>{H(i.Detail)}</td></tr>");
            sb.AppendLine("</table>");
            if (a.Scorecard is { } sc) {
                sb.AppendLine($"<h3>OpenSSF Scorecard — nota {sc.Score:0.0}</h3><table><tr><th>Check</th><th>Nota</th></tr>");
                foreach (var (check, score) in sc.Checks.OrderBy(c => c.Key, StringComparer.Ordinal))
                    sb.AppendLine($"<tr class=\"{(score >= 8 ? "pass" : score < 0 ? "manual" : "fail")}\"><td>{H(check)}</td><td>{score:0}</td></tr>");
                sb.AppendLine("</table>");
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
