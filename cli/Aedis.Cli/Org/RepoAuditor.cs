using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aedis.Cli.Validation;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aedis.Cli.Org;

/// <summary>Tudo que o auditor precisa saber de um repositório, já baixado (ou montado nos testes).</summary>
public sealed class RepoSnapshot
{
    /// <summary>Nome do repositório.</summary>
    public required string Name { get; init; }
    /// <summary>Branch padrão auditado.</summary>
    public required string DefaultBranch { get; init; }

    /// <summary>Caminho → conteúdo dos arquivos sondados; ausentes não entram.</summary>
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Nome → conteúdo de cada workflow em <c>.github/workflows</c>.</summary>
    public Dictionary<string, string> Workflows { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Regras efetivas do branch padrão (<c>GET /rules/branches/{branch}</c>).</summary>
    public JsonArray BranchRules { get; init; } = [];

    /// <summary>Resultado publicado do Scorecard; nulo quando não publicado.</summary>
    public ScorecardResult? Scorecard { get; init; }
}

/// <summary>Um critério avaliado: <c>pass</c>, <c>fail</c> ou <c>manual</c>.</summary>
public sealed record AuditItem(string Id, string Tier, string Criterion, string Status, string Detail);

/// <summary>Resultado de um repositório: nível alcançado e itens por critério.</summary>
public sealed class RepoAudit
{
    /// <summary>Nome do repositório.</summary>
    public required string Repo { get; init; }
    /// <summary>Branch padrão auditado.</summary>
    public required string DefaultBranch { get; init; }
    /// <summary>Maior nível (0 a 3) cujos critérios não manuais passaram todos.</summary>
    public required int LevelAchieved { get; init; }
    /// <summary>Itens avaliados, dos tiers até o nível alvo.</summary>
    public required IReadOnlyList<AuditItem> Items { get; init; }
    /// <summary>Nota publicada do Scorecard; nula quando não publicada.</summary>
    public ScorecardResult? Scorecard { get; init; }
    /// <summary>"created"/"updated" quando o ruleset foi aplicado com --apply.</summary>
    public string? RulesetApplied { get; set; }
    /// <summary>Checks tornados obrigatórios pelo ruleset aplicado.</summary>
    public IReadOnlyList<string> RequiredChecks { get; set; } = [];
}

/// <summary>
///     Avalia um repositório contra <c>conformance/best-practices.yaml</c> (critérios do selo OpenSSF Best
///     Practices por tier), as regras vivas do branch padrão e os mínimos de Scorecard de
///     <c>conformance/levels.yaml</c>. Itens <c>manual</c> não bloqueiam o nível, mas aparecem no relatório.
/// </summary>
public static partial class RepoAuditor
{
    /// <summary>Caminhos sondados em todo repositório (critérios file-exists mais CODEOWNERS).</summary>
    public static readonly string[] ProbedFiles =
        ["SECURITY.md", "LICENSE", "CONTRIBUTING.md", ".github/dependabot.yml", ".github/rulesets/main.json", "CODEOWNERS", ".github/CODEOWNERS"];

    private static readonly string[] TierByLevel = ["", "passing", "silver", "gold"];

    /// <summary>Avalia o repositório até <paramref name="targetLevel" />; lê best-practices.yaml e levels.yaml de <paramref name="root" /> ou dos recursos embutidos.</summary>
    public static RepoAudit Evaluate(RepoSnapshot repo, int targetLevel, string root) {
        var practices = LoadBestPractices(root);
        var levels = ConformanceChecker.LoadLevels(root).ToDictionary(l => l.Id);
        var items = new List<AuditItem>();
        var achieved = 0;
        var blocked = false;

        for (var level = 1; level <= Math.Clamp(targetLevel, 1, 3); level++) {
            var tier = TierByLevel[level];
            var tierItems = practices.Tiers.TryGetValue(tier, out var defs) ? defs : [];
            var levelOk = true;
            foreach (var def in tierItems) {
                var item = Evaluate(def, tier, repo);
                items.Add(item);
                if (item.Status == "fail") levelOk = false;
            }

            var protection = BranchProtection(repo, level);
            items.Add(protection);
            if (protection.Status == "fail") levelOk = false;

            if (levels.TryGetValue(level, out var definition) && definition.Scorecard is { } minimum) {
                var score = ScorecardItem(repo, level, minimum);
                items.Add(score);
                if (score.Status == "fail") levelOk = false;
            }

            if (!blocked && levelOk) achieved = level;
            if (!levelOk) blocked = true;
        }

        return new RepoAudit { Repo = repo.Name, DefaultBranch = repo.DefaultBranch, LevelAchieved = achieved, Items = items, Scorecard = repo.Scorecard };
    }

    private static AuditItem Evaluate(PracticeDefinition def, string tier, RepoSnapshot repo) {
        AuditItem Result(bool ok, string detail) => new(def.Id, tier, def.Criterion, ok ? "pass" : "fail", ok ? "" : detail);
        return def.Check switch {
            "file-exists" => Result(repo.Files.ContainsKey(def.Path!) || (def.Path == "CODEOWNERS" && repo.Files.ContainsKey(".github/CODEOWNERS")),
                $"falta {def.Path}"),
            "ruleset-as-code" => Result(repo.Files.ContainsKey(def.Path!), $"falta {def.Path} (ruleset versionado no repositório)"),
            "workflow-exists" => Result(repo.Workflows.Count > 0, "sem workflows em .github/workflows"),
            "workflow-uses" => Result(repo.Workflows.Values.Any(w => Regex.IsMatch(w, def.Pattern!, RegexOptions.IgnoreCase)),
                $"nenhum workflow casa com '{def.Pattern}'"),
            "actions-pinned-by-sha" => PinnedBySha(def, tier, repo),
            _ => new AuditItem(def.Id, tier, def.Criterion, "manual", "verificação manual (organização/processo)")
        };
    }

    private static AuditItem PinnedBySha(PracticeDefinition def, string tier, RepoSnapshot repo) {
        var unpinned = repo.Workflows
            .SelectMany(w => UsesLine().Matches(w.Value).Select(m => (File: w.Key, Uses: m.Groups["uses"].Value)))
            .Where(u => !u.Uses.StartsWith("./", StringComparison.Ordinal) && !u.Uses.StartsWith("docker://", StringComparison.Ordinal))
            .Where(u => !PinnedRef().IsMatch(u.Uses))
            .Select(u => $"{u.File}: {u.Uses}")
            .ToList();
        return new AuditItem(def.Id, tier, def.Criterion, unpinned.Count == 0 ? "pass" : "fail",
            unpinned.Count == 0 ? "" : "sem SHA: " + string.Join("; ", unpinned.Take(5)));
    }

    /// <summary>
    ///     Regras vivas exigidas por nível: L1 proíbe force-push/exclusão e exige PR; L2 soma histórico linear e
    ///     checks obrigatórios; L3 soma revisão de code owner e aprovação do último push.
    /// </summary>
    private static AuditItem BranchProtection(RepoSnapshot repo, int level) {
        var rules = repo.BranchRules.OfType<JsonObject>().ToList();
        bool Has(string type) => rules.Any(r => r["type"]?.GetValue<string>() == type);
        JsonObject? Params(string type) => rules.FirstOrDefault(r => r["type"]?.GetValue<string>() == type)?["parameters"] as JsonObject;

        var missing = new List<string>();
        if (!Has("non_fast_forward")) missing.Add("força-push permitido");
        if (!Has("deletion")) missing.Add("exclusão do branch permitida");
        if (!Has("pull_request")) missing.Add("mudança direta sem PR permitida");
        if (level >= 2) {
            if (!Has("required_linear_history")) missing.Add("histórico não linear");
            if (!Has("required_status_checks") || (Params("required_status_checks")?["required_status_checks"] as JsonArray)?.Count is null or 0)
                missing.Add("sem checks obrigatórios");
        }

        if (level >= 3 && Params("pull_request") is { } pr) {
            if (pr["require_code_owner_review"]?.GetValue<bool>() != true) missing.Add("revisão de code owner não exigida");
            if (pr["require_last_push_approval"]?.GetValue<bool>() != true) missing.Add("aprovação do último push não exigida");
        }

        return new AuditItem($"BP-07-L{level}", TierByLevel[level], $"Proteção viva do branch padrão (nível L{level})",
            missing.Count == 0 ? "pass" : "fail", string.Join(", ", missing));
    }

    private static AuditItem ScorecardItem(RepoSnapshot repo, int level, ConformanceChecker.ScorecardMinimum minimum) {
        var id = $"SC-L{level}";
        const string criterion = "OpenSSF Scorecard publicado e dentro dos mínimos do nível";
        if (repo.Scorecard is null)
            return new AuditItem(id, TierByLevel[level], criterion, minimum.MinScore > 0 ? "fail" : "pass",
                minimum.MinScore > 0 ? "resultado não publicado em api.scorecard.dev" : "");

        var problems = new List<string>();
        if (repo.Scorecard.Score < minimum.MinScore) problems.Add($"nota {repo.Scorecard.Score:0.0} < {minimum.MinScore}");
        foreach (var (check, min) in minimum.Checks)
            if (!repo.Scorecard.Checks.TryGetValue(check, out var score) || score < min)
                problems.Add($"{check} {(repo.Scorecard.Checks.TryGetValue(check, out var s) ? s.ToString("0") : "?")} < {min}");
        return new AuditItem(id, TierByLevel[level], criterion, problems.Count == 0 ? "pass" : "fail", string.Join(", ", problems));
    }

    /// <summary>Carrega conformance/best-practices.yaml do repositório local ou do recurso embutido.</summary>
    public static BestPracticesFile LoadBestPractices(string root) {
        var local = Path.Combine(root, "conformance", "best-practices.yaml");
        string yaml;
        if (File.Exists(local)) {
            yaml = File.ReadAllText(local);
        }
        else {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("conformance/best-practices.yaml")!;
            using var reader = new StreamReader(stream);
            yaml = reader.ReadToEnd();
        }

        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        return deserializer.Deserialize<BestPracticesFile>(yaml);
    }

    /// <summary>Arquivo best-practices.yaml desserializado.</summary>
    public sealed class BestPracticesFile
    {
        /// <summary>Critérios por tier (passing, silver, gold).</summary>
        public Dictionary<string, List<PracticeDefinition>> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Um critério do selo Best Practices e como verificá-lo.</summary>
    public sealed class PracticeDefinition
    {
        /// <summary>Identificador (BP-xx).</summary>
        public string Id { get; set; } = "";
        /// <summary>Texto do critério.</summary>
        public string Criterion { get; set; } = "";
        /// <summary>Tipo de verificação (file-exists, workflow-uses, actions-pinned-by-sha, ruleset-as-code, manual).</summary>
        public string Check { get; set; } = "";
        /// <summary>Caminho do arquivo para verificações por arquivo.</summary>
        public string? Path { get; set; }
        /// <summary>Regex para verificações por conteúdo de workflow.</summary>
        public string? Pattern { get; set; }
    }

    [GeneratedRegex(@"^\s*-?\s*uses:\s*(?<uses>\S+)", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"@[0-9a-f]{40}(\s|$)")]
    private static partial Regex PinnedRef();
}
