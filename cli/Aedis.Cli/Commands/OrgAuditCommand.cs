using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aedis.Cli.Org;

namespace Aedis.Cli.Commands;

/// <summary>
///     <c>aedis org audit</c>: avalia a organização (configurações de org e do Actions, ruleset de organização) e
///     cada repositório contra a régua do Aedis (Best Practices por tier, proteção viva do branch, Scorecard),
///     grava <c>org-audit-summary.json</c>, <c>org-audit.md</c> e <c>org-audit.html</c> (em inglês, para a
///     organização e seus auditores) e, com <c>--apply</c>, aplica os guardrails do nível pedido: ruleset de
///     organização para todos os repositórios, configurações de segurança da org, permissões do Actions e o
///     ruleset de cada repositório com os checks obrigatórios. Sai com 1 enquanto a organização ou algum
///     repositório não alcançar o nível.
/// </summary>
public static class OrgAuditCommand
{
    /// <summary>Monta o comando; <paramref name="handlerFactory" /> permite simular a API nos testes.</summary>
    public static Command Create(Func<HttpMessageHandler>? handlerFactory = null) {
        var org = new Option<string>("--org") { Description = "GitHub organization", Required = true };
        var level = new Option<int>("--level") { Description = "Target level (1, 2 or 3)", DefaultValueFactory = _ => 3 };
        var repos = new Option<string[]>("--repo") { Description = "Restrict to these repositories", AllowMultipleArgumentsPerToken = true };
        var apply = new Option<bool>("--apply") { Description = "Apply the organization guardrails and create/update the default-branch ruleset of each repository" };
        var approvals = new Option<int?>("--approvals") { Description = "Approvals required by the rulesets (default: 0 at L1, 1 at L2/L3)" };
        var checks = new Option<string[]>("--required-check") { Description = "Required status check contexts for the repository rulesets; mandatory with --apply (only checks that run on every pull request, without path filters)", AllowMultipleArgumentsPerToken = true };
        var noBypass = new Option<bool>("--no-admin-bypass") { Description = "Do not let administrators bypass the rulesets through pull requests" };
        var output = new Option<string>("--out") { Description = "Report directory", DefaultValueFactory = _ => "." };
        var token = new Option<string?>("--token") { Description = "GitHub token (default: GITHUB_TOKEN, GH_TOKEN or `gh auth token`)" };

        var command = new Command("audit", "Audits the organization and its repositories against the Aedis conformance levels and, with --apply, applies the guardrails.") {
            org, level, repos, apply, approvals, checks, noBypass, output, token
        };
        command.SetAction(async (parse, cancellationToken) => {
            var handler = handlerFactory?.Invoke() ?? new HttpClientHandler();
            var api = new GitHubApi(new HttpClient(handler), parse.GetValue(token) ?? GitHubApi.ResolveToken());
            var orgName = parse.GetValue(org)!;
            var targetLevel = Math.Clamp(parse.GetValue(level), 1, 3);
            var requiredApprovals = parse.GetValue(approvals) ?? (targetLevel >= 2 ? 1 : 0);
            var adminBypass = !parse.GetValue(noBypass);
            var only = parse.GetValue(repos) ?? [];
            var outDir = Path.GetFullPath(parse.GetValue(output)!);
            Directory.CreateDirectory(outDir);

            var audits = new List<RepoAudit>();
            var missingChecks = false;
            var practices = RepoAuditor.LoadBestPractices(Directory.GetCurrentDirectory());
            foreach (var (name, branch) in await api.ListRepositoriesAsync(orgName, cancellationToken)) {
                if (only.Length > 0 && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (only.Length == 0 && name == ".github") continue; // repositório de perfil da organização, sem código
                var snapshot = await SnapshotAsync(api, orgName, name, branch, practices, cancellationToken);
                var audit = RepoAuditor.Evaluate(snapshot, targetLevel, Directory.GetCurrentDirectory());
                if (parse.GetValue(apply) && parse.GetValue(checks) is not { Length: > 0 }) {
                    // Derivar da lista de checks verdes em main já exigiu um job filtrado por paths e travou o repositório: a escolha é do operador.
                    missingChecks = true;
                    Console.Error.WriteLine($"{name}: pass --required-check; successful checks on the latest commit of {branch}: " +
                                            string.Join(" | ", await api.GetSuccessfulCheckNamesAsync(orgName, name, branch, cancellationToken)));
                }
                else if (parse.GetValue(apply)) {
                    var required = parse.GetValue(checks)!;
                    var ruleset = RulesetGuardrail.Build(targetLevel, requiredApprovals, required, adminBypass);
                    audit.RulesetApplied = await api.UpsertRulesetAsync(orgName, name, ruleset, cancellationToken);
                    audit.RequiredChecks = required;
                    // O portão de merge (aedis pr gate) habilita auto-merge na PR aprovada; o repositório precisa permitir.
                    await api.PatchRepositoryAsync(orgName, name, new JsonObject { ["allow_auto_merge"] = true, ["delete_branch_on_merge"] = true }, cancellationToken);
                }

                audits.Add(audit);
                Console.WriteLine($"{(audit.LevelAchieved >= targetLevel ? "[ok]  " : "[FAIL]")} {name}: L{audit.LevelAchieved}" +
                                  (audit.RulesetApplied is null ? "" : $" (ruleset {audit.RulesetApplied}, {audit.RequiredChecks.Count} required checks)"));
                foreach (var item in audit.Items.Where(i => i.Status != "pass"))
                    Console.WriteLine($"        {(item.Status == "fail" ? "[FAIL]" : "[ ?  ]")} {item.Id} {item.Criterion}{(string.IsNullOrEmpty(item.Detail) ? "" : " — " + item.Detail)}");
            }

            if (missingChecks) return 2;

            var applied = new List<string>();
            if (parse.GetValue(apply)) {
                // Cada guardrail de organização é independente: um 403 (ex.: rulesets de org fora do plano Free) não impede os demais.
                async Task Try(string label, Func<Task<string>> action) {
                    try {
                        applied.Add($"{label} {await action()}".TrimEnd());
                    }
                    catch (InvalidOperationException ex) {
                        Console.Error.WriteLine($"{orgName}: {label}: {ex.Message}");
                    }
                }

                await Try("actions workflow permissions", async () => { await api.PutWorkflowPermissionsAsync(orgName, RulesetGuardrail.WorkflowPermissions(), cancellationToken); return ""; });
                await Try("organization security settings", async () => { await api.PatchOrganizationAsync(orgName, RulesetGuardrail.OrganizationSettings(targetLevel), cancellationToken); return ""; });
                await Try("organization ruleset", () => api.UpsertOrganizationRulesetAsync(orgName, RulesetGuardrail.Build(targetLevel, requiredApprovals, [], adminBypass, allRepositories: true), cancellationToken));
            }

            var orgAudit = RepoAuditor.EvaluateOrganization(await SnapshotOrganizationAsync(api, orgName, cancellationToken), targetLevel, Directory.GetCurrentDirectory());
            orgAudit.Applied.AddRange(applied);
            Console.WriteLine($"{(orgAudit.LevelAchieved >= targetLevel ? "[ok]  " : "[FAIL]")} organization {orgName}: L{orgAudit.LevelAchieved}" +
                              (applied.Count == 0 ? "" : $" (applied: {string.Join(", ", applied)})"));
            foreach (var item in orgAudit.Items.Where(i => i.Status != "pass"))
                Console.WriteLine($"        {(item.Status == "fail" ? "[FAIL]" : "[ ?  ]")} {item.Id} {item.Criterion}{(string.IsNullOrEmpty(item.Detail) ? "" : " — " + item.Detail)}");

            await File.WriteAllTextAsync(Path.Combine(outDir, "org-audit-summary.json"), ToJson(orgAudit, targetLevel, audits), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outDir, "org-audit.md"), ToMarkdown(orgAudit, targetLevel, audits), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outDir, "org-audit.html"), ToHtml(orgAudit, targetLevel, audits), cancellationToken);
            Directory.CreateDirectory(Path.Combine(outDir, "badges"));
            foreach (var a in audits)
                await File.WriteAllTextAsync(Path.Combine(outDir, "badges", $"{a.Repo}.json"), ToBadge("aedis", a.LevelAchieved), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outDir, "badge.json"),
                ToBadge("aedis", Math.Min(orgAudit.LevelAchieved, audits.Count == 0 ? 0 : audits.Min(a => a.LevelAchieved))), cancellationToken);
            var ok = audits.Count > 0 && audits.All(a => a.LevelAchieved >= targetLevel) && orgAudit.LevelAchieved >= targetLevel;
            Console.WriteLine($"report written to {outDir} — organization L{orgAudit.LevelAchieved}, {audits.Count(a => a.LevelAchieved >= targetLevel)}/{audits.Count} repositories at L{targetLevel}");
            return ok ? 0 : 1;
        });
        return command;
    }

    /// <summary>Baixa arquivos sondados, workflows, regras do branch, Scorecard, assets da release, selo e sumários de run de um repositório.</summary>
    public static async Task<RepoSnapshot> SnapshotAsync(GitHubApi api, string org, string repo, string branch, RepoAuditor.BestPracticesFile practices, CancellationToken ct) {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in RepoAuditor.ProbedFiles)
            if (await api.TryGetFileAsync(org, repo, path, ct) is { } content)
                files[path] = content;

        var workflows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in await api.ListDirectoryAsync(org, repo, ".github/workflows", ct))
            if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                workflows[name] = await api.TryGetFileAsync(org, repo, $".github/workflows/{name}", ct) ?? "";

        var summaries = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (var artifact in practices.SummaryArtifacts)
            summaries[artifact] = await api.TryGetArtifactSummaryAsync(org, repo, artifact, ct, branch: branch);

        return new RepoSnapshot {
            Name = repo,
            DefaultBranch = branch,
            Files = files,
            Workflows = workflows,
            BranchRules = await api.GetBranchRulesAsync(org, repo, branch, ct),
            Scorecard = await api.GetScorecardAsync(org, repo, ct),
            ReleaseAssets = await api.GetLatestReleaseAssetNamesAsync(org, repo, ct),
            BadgeLevel = await api.GetBestPracticesBadgeLevelAsync(org, repo, ct),
            Summaries = summaries
        };
    }

    /// <summary>Badge no formato endpoint do shields.io (<c>img.shields.io/endpoint?url=...</c>) para o nível alcançado.</summary>
    public static string ToBadge(string label, int level) => new JsonObject {
        ["schemaVersion"] = 1,
        ["label"] = label,
        ["message"] = $"L{level}",
        ["color"] = level switch { 3 => "brightgreen", 2 => "green", 1 => "yellow", _ => "red" }
    }.ToJsonString();

    /// <summary>Baixa o objeto da organização, as permissões de workflow do Actions e os rulesets de organização.</summary>
    public static async Task<OrgSnapshot> SnapshotOrganizationAsync(GitHubApi api, string org, CancellationToken ct) => new() {
        Name = org,
        Organization = await api.GetOrganizationAsync(org, ct),
        WorkflowPermissions = await api.GetWorkflowPermissionsAsync(org, ct),
        Rulesets = await api.ListOrganizationRulesetsAsync(org, ct)
    };

    private static JsonArray Items(IEnumerable<AuditItem> items) => new(items.Select(i => (JsonNode)new JsonObject {
        ["id"] = i.Id, ["tier"] = i.Tier, ["criterion"] = i.Criterion, ["status"] = i.Status, ["detail"] = i.Detail,
        ["osps"] = new JsonArray((i.Osps ?? []).Select(o => (JsonNode)o).ToArray())
    }).ToArray());

    /// <summary>Sumário JSON (schema aedis.platform/org-audit/v1) consumível pelo portal.</summary>
    public static string ToJson(OrgAudit org, int targetLevel, IReadOnlyList<RepoAudit> audits) {
        var root = new JsonObject {
            ["schema"] = "aedis.platform/org-audit/v1",
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["organization"] = new JsonObject {
                ["name"] = org.Name,
                ["levelAchieved"] = org.LevelAchieved,
                ["applied"] = new JsonArray(org.Applied.Select(a => (JsonNode)a).ToArray()),
                ["items"] = Items(org.Items)
            },
            ["targetLevel"] = targetLevel,
            ["repositories"] = new JsonArray(audits.Select(a => (JsonNode)new JsonObject {
                ["name"] = a.Repo,
                ["defaultBranch"] = a.DefaultBranch,
                ["levelAchieved"] = a.LevelAchieved,
                ["scorecard"] = a.Scorecard is null ? null : new JsonObject { ["score"] = a.Scorecard.Score },
                ["rulesetApplied"] = a.RulesetApplied,
                ["requiredChecks"] = new JsonArray(a.RequiredChecks.Select(c => (JsonNode)c).ToArray()),
                ["items"] = Items(a.Items)
            }).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Relatório Markdown: organização, tabela por repositório e o que falta em cada um.</summary>
    public static string ToMarkdown(OrgAudit org, int targetLevel, IReadOnlyList<RepoAudit> audits) {
        var sb = new StringBuilder();
        sb.AppendLine($"# Organization audit `{org.Name}` — target L{targetLevel}");
        sb.AppendLine();
        sb.AppendLine($"## Organization — L{org.LevelAchieved}");
        foreach (var i in org.Items.Where(i => i.Status != "pass"))
            sb.AppendLine($"- **{i.Id}** ({i.Status}) {i.Criterion}{(string.IsNullOrEmpty(i.Detail) ? "" : ": " + i.Detail)}");
        sb.AppendLine();
        sb.AppendLine("| Repository | Level | Scorecard | Failures | Ruleset |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var a in audits)
            sb.AppendLine($"| {a.Repo} | L{a.LevelAchieved} | {(a.Scorecard is null ? "not published" : a.Scorecard.Score.ToString("0.0"))} | {a.Items.Count(i => i.Status == "fail")} | {a.RulesetApplied ?? "-"} |");
        foreach (var a in audits.Where(a => a.Items.Any(i => i.Status != "pass"))) {
            sb.AppendLine();
            sb.AppendLine($"## {a.Repo}");
            foreach (var i in a.Items.Where(i => i.Status != "pass"))
                sb.AppendLine($"- **{i.Id}** ({i.Status}) {i.Criterion}{(string.IsNullOrEmpty(i.Detail) ? "" : ": " + i.Detail)}");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Relatório HTML autocontido no estilo dos scanners (ZAP): organização, resumo por repositório e, para
    ///     cada um, todos os critérios avaliados por tier com status e detalhe, checks obrigatórios e notas do
    ///     Scorecard por check.
    /// </summary>
    public static string ToHtml(OrgAudit org, int targetLevel, IReadOnlyList<RepoAudit> audits) {
        static string H(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");
        static string ItemsTable(IEnumerable<AuditItem> items) {
            var rows = new StringBuilder("<table><tr><th>Id</th><th>Tier</th><th>Criterion</th><th>OSPS</th><th>Status</th><th>Detail</th></tr>");
            foreach (var i in items)
                rows.Append($"<tr class=\"{H(i.Status)}\"><td>{H(i.Id)}</td><td>{H(i.Tier)}</td><td>{H(i.Criterion)}</td><td>{H(string.Join(" ", i.Osps ?? []))}</td><td>{H(i.Status)}</td><td>{H(i.Detail)}</td></tr>");
            return rows.Append("</table>").ToString();
        }

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>Audit {H(org.Name)}</title><style>body{{font:14px/1.5 system-ui,sans-serif;margin:24px;color:#1f2328}}table{{border-collapse:collapse;margin:8px 0 24px}}th,td{{border:1px solid #d0d7de;padding:4px 10px;text-align:left;vertical-align:top}}th{{background:#f6f8fa}}.pass{{background:#dafbe1}}.fail{{background:#ffebe9}}.manual{{background:#fff8c5}}h2{{margin-top:32px;border-bottom:1px solid #d0d7de}}.meta{{color:#59636e}}</style></head><body>");
        sb.AppendLine($"<h1>Organization audit: {H(org.Name)} — target L{targetLevel}</h1>");
        sb.AppendLine($"<p class=\"meta\">Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC · ruler: OpenSSF Best Practices (passing/silver/gold tiers) with evidence from releases, the bestpractices.dev badge and CI run summaries, organization settings, live branch protection and OpenSSF Scorecard · OSPS column: OSPS Baseline categories covered (AC access control, BR build &amp; release, DO documentation, GV governance, LE legal, QA quality, SA security assessment, VM vulnerability management) · organization L{org.LevelAchieved} · {audits.Count(a => a.LevelAchieved >= targetLevel)}/{audits.Count} repositories at L{targetLevel}</p>");

        sb.AppendLine($"<h2 id=\"organization\">Organization <small class=\"meta\">— L{org.LevelAchieved}{(org.Applied.Count == 0 ? "" : " · applied: " + H(string.Join(", ", org.Applied)))}</small></h2>");
        sb.AppendLine(ItemsTable(org.Items));

        sb.AppendLine("<h2>Repositories</h2><table><tr><th>Repository</th><th>Level</th><th>Scorecard</th><th>Pass</th><th>Fail</th><th>Manual</th><th>Ruleset</th><th>Required checks</th></tr>");
        foreach (var a in audits)
            sb.AppendLine($"<tr class=\"{(a.LevelAchieved >= targetLevel ? "pass" : "fail")}\"><td><a href=\"#{H(a.Repo)}\">{H(a.Repo)}</a></td><td>L{a.LevelAchieved}</td><td>{(a.Scorecard is null ? "not published" : a.Scorecard.Score.ToString("0.0"))}</td>"
                          + $"<td>{a.Items.Count(i => i.Status == "pass")}</td><td>{a.Items.Count(i => i.Status == "fail")}</td><td>{a.Items.Count(i => i.Status == "manual")}</td><td>{H(a.RulesetApplied ?? "-")}</td><td>{H(string.Join(", ", a.RequiredChecks))}</td></tr>");
        sb.AppendLine("</table>");

        foreach (var a in audits) {
            sb.AppendLine($"<h2 id=\"{H(a.Repo)}\">{H(a.Repo)} <small class=\"meta\">({H(a.DefaultBranch)}) — L{a.LevelAchieved}</small></h2>");
            sb.AppendLine(ItemsTable(a.Items));
            if (a.Scorecard is { } sc) {
                sb.AppendLine($"<h3>OpenSSF Scorecard — score {sc.Score:0.0}</h3><table><tr><th>Check</th><th>Score</th></tr>");
                foreach (var (check, score) in sc.Checks.OrderBy(c => c.Key, StringComparer.Ordinal))
                    sb.AppendLine($"<tr class=\"{(score >= 8 ? "pass" : score < 0 ? "manual" : "fail")}\"><td>{H(check)}</td><td>{score:0}</td></tr>");
                sb.AppendLine("</table>");
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
