using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aedis.Cli.Org;

/// <summary>
///     Política de merge automático de um repositório (<c>.github/aedis-merge-policy.yaml</c>, lida sempre do branch
///     padrão para a PR não poder alterá-la). Tudo que falta aqui exige um humano.
/// </summary>
public sealed class MergePolicy
{
    /// <summary>Schema do arquivo.</summary>
    public string Schema { get; set; } = "aedis.platform/merge-policy/v1";
    /// <summary>Logins cujas PRs podem ser aprovadas pelo portão; vazio desliga a aprovação automática.</summary>
    public List<string> TrustedAuthors { get; set; } = [];
    /// <summary>Globs de arquivos que exigem revisão humana (workflows, rulesets, política, segurança, versão).</summary>
    public List<string> HumanRequiredPaths { get; set; } = [".github/**", "CODEOWNERS", "SECURITY.md", "Directory.Build.props"];
    /// <summary>Artefatos de sumário da run do head da PR que precisam existir com <c>result: pass</c>.</summary>
    public List<string> RequiredSummaries { get; set; } = ["coverage-summary", "sast-summary"];
    /// <summary>Reprova se a cobertura de linhas da PR ficar abaixo da última medida do branch base.</summary>
    public bool CoverageNoRegression { get; set; } = true;
    /// <summary>Tamanho máximo do diff (adições + remoções) aprovável sem humano.</summary>
    public int MaxChangedLines { get; set; } = 2000;
    /// <summary>Permite aprovar PRs marcadas como breaking (<c>!</c> no título ou <c>BREAKING CHANGE</c> no corpo).</summary>
    public bool AllowBreaking { get; set; }
    /// <summary>Método de merge do auto-merge: squash, merge ou rebase.</summary>
    public string MergeMethod { get; set; } = "squash";

    /// <summary>Desserializa a política; nulo/vazio devolve os padrões (sem autores confiáveis, logo sem aprovação).</summary>
    public static MergePolicy Parse(string? yaml) {
        if (string.IsNullOrWhiteSpace(yaml)) return new MergePolicy();
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        return deserializer.Deserialize<MergePolicy>(yaml) ?? new MergePolicy();
    }
}

/// <summary>Tudo que o portão precisa saber de uma PR, já baixado (ou montado nos testes).</summary>
public sealed record PullRequestSnapshot
{
    /// <summary>Número da PR.</summary>
    public required int Number { get; init; }
    /// <summary>Id GraphQL da PR (para habilitar o auto-merge).</summary>
    public string NodeId { get; init; } = "";
    /// <summary>Login do autor.</summary>
    public required string Author { get; init; }
    /// <summary>Head avaliado.</summary>
    public required string HeadSha { get; init; }
    /// <summary>Branch base.</summary>
    public string BaseBranch { get; init; } = "main";
    /// <summary>Título da PR.</summary>
    public string Title { get; init; } = "";
    /// <summary>Corpo da PR.</summary>
    public string Body { get; init; } = "";
    /// <summary>Rascunho.</summary>
    public bool IsDraft { get; init; }
    /// <summary>Vem de um fork.</summary>
    public bool FromFork { get; init; }
    /// <summary><c>mergeable_state</c> da API: clean, blocked, behind, dirty, unstable, unknown.</summary>
    public string MergeableState { get; init; } = "unknown";
    /// <summary>Adições + remoções.</summary>
    public int ChangedLines { get; init; }
    /// <summary>Arquivos tocados.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Mensagens dos commits.</summary>
    public IReadOnlyList<string> CommitMessages { get; init; } = [];
    /// <summary>Checks obrigatórios do ruleset do branch base.</summary>
    public IReadOnlyList<string> RequiredChecks { get; init; } = [];
    /// <summary>Nome do check → conclusão no head (nulo enquanto não concluiu).</summary>
    public IReadOnlyDictionary<string, string?> CheckConclusions { get; init; } = new Dictionary<string, string?>();
    /// <summary>Artefato → sumário JSON da run do head; nulo quando não existe.</summary>
    public IReadOnlyDictionary<string, JsonObject?> Summaries { get; init; } = new Dictionary<string, JsonObject?>();
    /// <summary>Último <c>coverage-summary</c> do branch base, para a regra de não regressão.</summary>
    public JsonObject? BaseCoverage { get; init; }
    /// <summary>Mantenedores humanos (não o autor) cuja revisão mais recente aprova o head avaliado.</summary>
    public IReadOnlyList<string> HumanApprovers { get; init; } = [];
}

/// <summary>Um critério do portão: <c>pass</c>, <c>fail</c> (exige humano) ou <c>pending</c> (esperar).</summary>
public sealed record GateCheck(string Id, string Status, string Detail);

/// <summary>Decisão do portão: <c>approve</c>, <c>wait</c> ou <c>block</c>, com os critérios avaliados.</summary>
public sealed record GateResult(string Decision, IReadOnlyList<GateCheck> Checks);

/// <summary>
///     Avalia uma PR contra a <see cref="MergePolicy" />: autor confiável e sem fork, Conventional Commit sem
///     trailers proibidos, nada de breaking, nenhum caminho que exija humano, tamanho, estado mergeável, checks
///     obrigatórios verdes no head, sumários com <c>pass</c> e cobertura sem regressão. Com algo <c>pending</c>
///     espera, mesmo havendo <c>fail</c>: o veredito (e o comentário) só sai quando todos os checks terminaram;
///     sem <c>pending</c>, qualquer <c>fail</c> bloqueia; senão aprova.
/// </summary>
public static partial class MergeGate
{
    /// <summary>Marcador dos comentários do portão (um por head SHA).</summary>
    public const string CommentMarker = "[aedis-compliance merge gate]";

    /// <summary>Avalia a PR.</summary>
    public static GateResult Evaluate(MergePolicy policy, PullRequestSnapshot pr) {
        var checks = new List<GateCheck>();
        void Add(string id, bool ok, string detail) => checks.Add(new GateCheck(id, ok ? "pass" : "fail", ok ? "" : detail));

        Add("author", policy.TrustedAuthors.Count > 0 && policy.TrustedAuthors.Contains(pr.Author, StringComparer.OrdinalIgnoreCase) && !pr.FromFork,
            policy.TrustedAuthors.Count == 0 ? "policy has no trustedAuthors" : pr.FromFork ? "pull request comes from a fork" : $"author '{pr.Author}' is not in trustedAuthors");
        Add("draft", !pr.IsDraft, "pull request is a draft");
        Add("title", ConventionalCommit().IsMatch(pr.Title), $"title is not a Conventional Commit: '{pr.Title}'");
        var trailer = pr.CommitMessages.Append(pr.Body).FirstOrDefault(m => ForbiddenTrailer().IsMatch(m));
        Add("trailers", trailer is null, "forbidden trailer (AI co-authorship) in a commit or in the body");
        var breaking = BreakingTitle().IsMatch(pr.Title) || pr.Body.Contains("BREAKING CHANGE", StringComparison.Ordinal) || pr.CommitMessages.Any(m => m.Contains("BREAKING CHANGE", StringComparison.Ordinal));
        // A revisão humana que a política exige se cumpre com um mantenedor aprovando o head; o portão então dá a
        // segunda aprovação, a do processo automatizado, sem nunca substituir a humana.
        var reviewed = pr.HumanApprovers.Count > 0;
        Add("breaking", policy.AllowBreaking || !breaking || reviewed, "breaking change requires human review");
        var human = pr.Files.Where(f => policy.HumanRequiredPaths.Any(g => GlobMatches(g, f))).ToList();
        Add("paths", human.Count == 0 || reviewed, "human review required for: " + string.Join(", ", human.Take(5)));
        Add("size", pr.ChangedLines <= policy.MaxChangedLines, $"{pr.ChangedLines} changed lines > {policy.MaxChangedLines}");

        switch (pr.MergeableState) {
            case "dirty": checks.Add(new GateCheck("mergeable", "fail", "merge conflict with the base branch")); break;
            case "behind": checks.Add(new GateCheck("mergeable", "fail", "branch is behind the base branch (update it)")); break;
            case "unknown": checks.Add(new GateCheck("mergeable", "pending", "GitHub has not computed mergeability yet")); break;
            default: checks.Add(new GateCheck("mergeable", "pass", "")); break;
        }

        foreach (var required in pr.RequiredChecks) {
            var conclusion = pr.CheckConclusions.TryGetValue(required, out var c) ? c : null;
            checks.Add(conclusion switch {
                "success" => new GateCheck($"check:{required}", "pass", ""),
                null => new GateCheck($"check:{required}", "pending", "not finished on the head commit"),
                _ => new GateCheck($"check:{required}", "fail", $"concluded '{conclusion}'")
            });
        }

        foreach (var artifact in policy.RequiredSummaries) {
            if (!pr.Summaries.TryGetValue(artifact, out var summary) || summary is null) {
                checks.Add(new GateCheck($"summary:{artifact}", "pending", "artifact not available for the head commit yet"));
                continue;
            }

            var result = summary["result"]?.GetValue<string>() ?? "unknown";
            checks.Add(new GateCheck($"summary:{artifact}", result == "pass" ? "pass" : "fail", result == "pass" ? "" : $"result '{result}'"));
        }

        if (policy.CoverageNoRegression && pr.Summaries.TryGetValue("coverage-summary", out var prCoverage) && prCoverage?["total"]?["line"] is { } line && pr.BaseCoverage?["total"]?["line"] is { } baseLine) {
            var (current, baseline) = (line.GetValue<double>(), baseLine.GetValue<double>());
            Add("coverage-regression", current + 0.05 >= baseline, $"line coverage {current:0.00}% is below the base branch ({baseline:0.00}%)");
        }

        var decision = checks.Any(c => c.Status == "pending") ? "wait" : checks.Any(c => c.Status == "fail") ? "block" : "approve";
        return new GateResult(decision, checks);
    }

    /// <summary>Casa um glob simples (<c>**</c> atravessa diretórios, <c>*</c> não) com um caminho da PR.</summary>
    public static bool GlobMatches(string glob, string path) {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase);
    }

    /// <summary>Sumário JSON (schema aedis.platform/merge-gate-summary/v1) com a decisão e os critérios.</summary>
    public static string ToJson(string org, string repo, PullRequestSnapshot pr, GateResult result, string? action) => new JsonObject {
        ["schema"] = "aedis.platform/merge-gate-summary/v1",
        ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        ["repository"] = $"{org}/{repo}",
        ["pullRequest"] = pr.Number,
        ["headSha"] = pr.HeadSha,
        ["decision"] = result.Decision,
        ["action"] = action,
        ["checks"] = new JsonArray(result.Checks.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["status"] = c.Status, ["detail"] = c.Detail }).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Comentário Markdown para a PR, em inglês, com o marcador e o head avaliado.</summary>
    public static string ToComment(PullRequestSnapshot pr, GateResult result, string? action) {
        var sha = pr.HeadSha[..7];
        var sb = new StringBuilder();
        sb.AppendLine($"<!-- {CommentMarker} `{sha}` -->");
        if (result.Decision == "approve") {
            sb.AppendLine("### ✅ Approved by Aedis Compliance");
            sb.AppendLine();
            sb.AppendLine($"Every criterion of the merge policy passed for `{sha}`.");
        }
        else {
            var onlyHuman = result.Checks.Where(c => c.Status == "fail").All(c => c.Id is "paths" or "breaking");
            sb.AppendLine(onlyHuman ? "### 👀 Needs a maintainer review" : "### ⛔ Not approved by Aedis Compliance");
            sb.AppendLine();
            sb.AppendLine(onlyHuman
                ? $"This change touches what the merge policy reserves for a human, so `{sha}` waits for a maintainer approval."
                : $"Fix the items below and push again; the gate re-evaluates every new commit (`{sha}`).");
            sb.AppendLine();
            sb.AppendLine("| | Criterion | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in result.Checks.Where(c => c.Status != "pass"))
                sb.AppendLine($"| {(c.Status == "fail" ? "⛔" : "⏳")} | {Label(c.Id)} | {c.Detail.Replace("|", "\\|")} |");
        }

        if (action is not null) {
            sb.AppendLine();
            sb.AppendLine($"> ℹ️ {action}");
        }

        sb.AppendLine();
        sb.AppendLine("<sub>Policy: <code>.github/aedis-merge-policy.yaml</code> on the default branch.</sub>");
        return sb.ToString();
    }

    /// <summary>
    ///     Logins dos mantenedores humanos cuja revisão mais recente aprova <paramref name="headSha" />: conta só
    ///     usuário (não bot) com associação OWNER, MEMBER ou COLLABORATOR, diferente do autor; uma revisão posterior
    ///     pedindo mudanças ou dispensada anula a aprovação anterior da mesma pessoa.
    /// </summary>
    public static IReadOnlyList<string> HumanApprovers(JsonArray reviews, string author, string headSha) =>
        reviews.OfType<JsonObject>()
            .Where(r => r["user"]?["type"]?.GetValue<string>() == "User"
                        && r["state"]?.GetValue<string>() is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")
            .GroupBy(r => r["user"]!["login"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .Where(r => r["state"]!.GetValue<string>() == "APPROVED"
                        && r["commit_id"]?.GetValue<string>() == headSha
                        && !string.Equals(r["user"]!["login"]!.GetValue<string>(), author, StringComparison.OrdinalIgnoreCase)
                        && r["author_association"]?.GetValue<string>() is "OWNER" or "MEMBER" or "COLLABORATOR")
            .Select(r => r["user"]!["login"]!.GetValue<string>())
            .ToList();

    /// <summary>Nome legível de um critério do portão, para o comentário na PR.</summary>
    public static string Label(string id) => id switch {
        "author" => "Trusted author",
        "draft" => "Ready for review",
        "title" => "Conventional Commit title",
        "trailers" => "No AI co-authorship trailers",
        "breaking" => "No breaking change",
        "paths" => "Human-reviewed paths",
        "size" => "Change size",
        "mergeable" => "Mergeable",
        "coverage-regression" => "Coverage does not drop",
        _ when id.StartsWith("check:", StringComparison.Ordinal) => $"Check `{id[6..]}`",
        _ when id.StartsWith("summary:", StringComparison.Ordinal) => $"Report `{id[8..]}`",
        _ => id
    };

    /// <summary>
    ///     Explicação curta para o erro do GitHub ao habilitar o auto-merge; o erro cru fica no log e no
    ///     sumário JSON do run.
    /// </summary>
    public static string AutoMergeNote(string error) => error switch {
        _ when error.Contains("stacked pull requests", StringComparison.OrdinalIgnoreCase) =>
            "Approved. Auto-merge turns on when this pull request targets the default branch: GitHub does not support it on stacked pull requests.",
        _ when error.Contains("clean status", StringComparison.OrdinalIgnoreCase) =>
            "Approved and ready: the pull request can be merged now.",
        _ when error.Contains("not allowed", StringComparison.OrdinalIgnoreCase) =>
            "Approved. Auto-merge is disabled in this repository's settings; merge it manually.",
        _ => "Approved. Auto-merge could not be enabled; see the merge gate run for the details."
    };

    [GeneratedRegex(@"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: [^ ].{0,99}$")]
    private static partial Regex ConventionalCommit();

    [GeneratedRegex(@"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!:")]
    private static partial Regex BreakingTitle();

    [GeneratedRegex(@"Co-Authored-By:\s*Claude|Generated with \[?Claude", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTrailer();
}

/// <summary>Autenticação como GitHub App: JWT RS256 assinado com a chave privada da App, trocado por um token de instalação.</summary>
public static class GitHubAppAuth
{
    /// <summary>JWT de 9 minutos para a App (<c>iss</c> = app id), conforme a documentação do GitHub.</summary>
    public static string CreateJwt(string appId, string privateKeyPem, DateTimeOffset? now = null) {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var epoch = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = epoch - 60, exp = epoch + 540, iss = appId }));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
