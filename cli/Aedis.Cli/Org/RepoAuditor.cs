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

    /// <summary>Nomes dos assets da última release; nulo quando o repositório não tem releases.</summary>
    public IReadOnlyList<string>? ReleaseAssets { get; init; }

    /// <summary>Nível do selo OpenSSF Best Practices (<c>passing</c>, <c>silver</c>, <c>gold</c>, <c>in_progress</c>); nulo quando não registrado.</summary>
    public string? BadgeLevel { get; init; }

    /// <summary>Nome do artefato → sumário JSON da run mais recente do branch padrão; nulo quando não há artefato válido.</summary>
    public Dictionary<string, JsonObject?> Summaries { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Configuração viva da organização, já baixada: objeto da org, permissões de workflow do Actions e rulesets de organização.</summary>
public sealed class OrgSnapshot
{
    /// <summary>Login da organização.</summary>
    public required string Name { get; init; }
    /// <summary><c>GET /orgs/{org}</c> (campos de segurança só aparecem para owners).</summary>
    public JsonObject Organization { get; init; } = new();
    /// <summary><c>GET /orgs/{org}/actions/permissions/workflow</c>.</summary>
    public JsonObject WorkflowPermissions { get; init; } = new();
    /// <summary><c>GET /orgs/{org}/rulesets</c>; nulo quando o plano do GitHub não oferece rulesets de organização.</summary>
    public JsonArray? Rulesets { get; init; } = [];
}

/// <summary>Um critério avaliado: <c>pass</c>, <c>fail</c> ou <c>manual</c>, com os controles do OSPS Baseline que ele cobre.</summary>
public sealed record AuditItem(string Id, string Tier, string Criterion, string Status, string Detail, IReadOnlyList<string>? Osps = null);

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

/// <summary>Resultado da organização: nível alcançado pelos controles de org e itens por critério.</summary>
public sealed class OrgAudit
{
    /// <summary>Login da organização.</summary>
    public required string Name { get; init; }
    /// <summary>Maior nível (0 a 3) cujos critérios de organização passaram todos.</summary>
    public required int LevelAchieved { get; init; }
    /// <summary>Itens avaliados, dos tiers até o nível alvo.</summary>
    public required IReadOnlyList<AuditItem> Items { get; init; }
    /// <summary>O que foi aplicado com --apply (ruleset de organização, configurações).</summary>
    public List<string> Applied { get; } = [];
}

/// <summary>
///     Avalia um repositório contra <c>conformance/best-practices.yaml</c> (critérios do selo OpenSSF Best
///     Practices por tier, com evidência: assets da release, selo registrado, sumários das runs), as regras vivas
///     do branch padrão e os mínimos de Scorecard de <c>conformance/levels.yaml</c>; e a organização contra a
///     seção <c>organization</c> do mesmo arquivo. Itens <c>manual</c> não bloqueiam o nível, mas aparecem no
///     relatório. Textos de saída em inglês: o relatório é para a organização e seus auditores.
/// </summary>
public static partial class RepoAuditor
{
    /// <summary>Caminhos sondados em todo repositório (critérios file-exists mais CODEOWNERS).</summary>
    public static readonly string[] ProbedFiles =
        ["SECURITY.md", "LICENSE", "CONTRIBUTING.md", ".github/dependabot.yml", ".github/rulesets/main.json", "CODEOWNERS", ".github/CODEOWNERS", "REUSE.toml"];

    private static readonly string[] TierByLevel = ["", "passing", "silver", "gold"];
    private static readonly Dictionary<string, int> BadgeRank = new(StringComparer.OrdinalIgnoreCase) { ["in_progress"] = 0, ["passing"] = 1, ["silver"] = 2, ["gold"] = 3 };

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

    /// <summary>Avalia a organização até <paramref name="targetLevel" /> pela seção <c>organization</c> de best-practices.yaml.</summary>
    public static OrgAudit EvaluateOrganization(OrgSnapshot org, int targetLevel, string root) {
        var practices = LoadBestPractices(root);
        var items = new List<AuditItem>();
        var achieved = 0;
        var blocked = false;

        for (var level = 1; level <= Math.Clamp(targetLevel, 1, 3); level++) {
            var tier = TierByLevel[level];
            var levelOk = true;
            foreach (var def in practices.Organization.Where(d => string.Equals(d.Tier, tier, StringComparison.OrdinalIgnoreCase))) {
                var item = EvaluateOrg(def, tier, org);
                items.Add(item);
                if (item.Status == "fail") levelOk = false;
            }

            if (!blocked && levelOk) achieved = level;
            if (!levelOk) blocked = true;
        }

        return new OrgAudit { Name = org.Name, LevelAchieved = achieved, Items = items };
    }

    private static AuditItem EvaluateOrg(PracticeDefinition def, string tier, OrgSnapshot org) {
        AuditItem Result(bool ok, string detail) => new(def.Id, tier, def.Criterion, ok ? "pass" : "fail", ok ? "" : detail, def.Osps);
        switch (def.Check) {
            case "org-field": {
                var source = def.Source == "workflow" ? org.WorkflowPermissions : org.Organization;
                var actual = source[def.Field!]?.ToJsonString().Trim('"') ?? "null";
                return Result(string.Equals(actual, def.Expected, StringComparison.OrdinalIgnoreCase), $"{def.Field} is {actual}, expected {def.Expected}");
            }
            case "org-ruleset":
                if (org.Rulesets is null)
                    return Result(false, "organization rulesets are not available on this GitHub plan (requires GitHub Team); repository rulesets are applied instead");
                return Result(org.Rulesets.OfType<JsonObject>().Any(r =>
                        r["name"]?.GetValue<string>() == RulesetGuardrail.Name && r["enforcement"]?.GetValue<string>() == "active"),
                    $"no active organization ruleset named '{RulesetGuardrail.Name}'");
            default:
                return new AuditItem(def.Id, tier, def.Criterion, "manual", def.Note ?? "manual verification (organization/process)", def.Osps);
        }
    }

    private static AuditItem Evaluate(PracticeDefinition def, string tier, RepoSnapshot repo) {
        AuditItem Result(bool ok, string detail) => new(def.Id, tier, def.Criterion, ok ? "pass" : "fail", ok ? "" : detail, def.Osps);
        bool AnyWorkflow(string pattern) => repo.Workflows.Values.Any(w => Regex.IsMatch(w, pattern, RegexOptions.IgnoreCase));
        switch (def.Check) {
            case "file-exists":
                return Result(repo.Files.ContainsKey(def.Path!) || (def.Path == "CODEOWNERS" && repo.Files.ContainsKey(".github/CODEOWNERS")), $"missing {def.Path}");
            case "ruleset-as-code":
                return Result(repo.Files.ContainsKey(def.Path!), $"missing {def.Path} (ruleset versioned in the repository)");
            case "workflow-exists":
                return Result(repo.Workflows.Count > 0, "no workflows under .github/workflows");
            case "workflow-uses":
                return Result(AnyWorkflow(def.Pattern!), $"no workflow matches '{def.Pattern}'");
            case "workflow-uses-all":
                return Result(def.Patterns.All(AnyWorkflow), $"workflows must match all of: {string.Join(", ", def.Patterns.Where(p => !AnyWorkflow(p)))}");
            case "actions-pinned-by-sha":
                return PinnedBySha(def, tier, repo);
            case "release-asset":
                if (repo.ReleaseAssets is null)
                    return Result(def.WorkflowPattern is not null && AnyWorkflow(def.WorkflowPattern),
                        $"no release published yet and no workflow matches '{def.WorkflowPattern}'");
                return Result(repo.ReleaseAssets.Any(a => Regex.IsMatch(a, def.Pattern!, RegexOptions.IgnoreCase)),
                    $"latest release has no asset matching '{def.Pattern}'");
            case "badge-api": {
                var expected = def.Expected ?? tier;
                var actual = repo.BadgeLevel is { } l && BadgeRank.TryGetValue(l, out var r) ? r : -1;
                return Result(actual >= BadgeRank[expected], repo.BadgeLevel is null
                    ? "project not registered at bestpractices.dev"
                    : $"badge level '{repo.BadgeLevel}' is below '{expected}'");
            }
            case "summary-artifact": {
                if (!repo.Summaries.TryGetValue(def.Artifact!, out var summary) || summary is null)
                    return Result(false, $"no '{def.Artifact}' artifact on the latest default-branch run (workflow never ran or artifact expired)");
                var result = summary["result"]?.GetValue<string>() ?? "unknown";
                return Result(result == "pass", $"latest '{def.Artifact}' reports result '{result}'");
            }
            default:
                return new AuditItem(def.Id, tier, def.Criterion, "manual", def.Note ?? "manual verification (organization/process)", def.Osps);
        }
    }

    private static AuditItem PinnedBySha(PracticeDefinition def, string tier, RepoSnapshot repo) {
        var unpinned = repo.Workflows
            .SelectMany(w => UsesLine().Matches(w.Value).Select(m => (File: w.Key, Uses: m.Groups["uses"].Value)))
            .Where(u => !u.Uses.StartsWith("./", StringComparison.Ordinal) && !u.Uses.StartsWith("docker://", StringComparison.Ordinal))
            .Where(u => !PinnedRef().IsMatch(u.Uses))
            .Select(u => $"{u.File}: {u.Uses}")
            .ToList();
        return new AuditItem(def.Id, tier, def.Criterion, unpinned.Count == 0 ? "pass" : "fail",
            unpinned.Count == 0 ? "" : "not pinned by SHA: " + string.Join("; ", unpinned.Take(5)), def.Osps);
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
        if (!Has("non_fast_forward")) missing.Add("force-push allowed");
        if (!Has("deletion")) missing.Add("branch deletion allowed");
        if (!Has("pull_request")) missing.Add("direct changes without a pull request allowed");
        if (level >= 2) {
            if (!Has("required_linear_history")) missing.Add("non-linear history allowed");
            if (!Has("required_status_checks") || (Params("required_status_checks")?["required_status_checks"] as JsonArray)?.Count is null or 0)
                missing.Add("no required status checks");
        }

        if (level >= 3 && Params("pull_request") is { } pr) {
            if (pr["require_code_owner_review"]?.GetValue<bool>() != true) missing.Add("code owner review not required");
            if (pr["require_last_push_approval"]?.GetValue<bool>() != true) missing.Add("last push approval not required");
        }

        return new AuditItem($"BP-07-L{level}", TierByLevel[level], $"Live default branch protection (level L{level})",
            missing.Count == 0 ? "pass" : "fail", string.Join(", ", missing), ["AC", "BR"]);
    }

    private static AuditItem ScorecardItem(RepoSnapshot repo, int level, ConformanceChecker.ScorecardMinimum minimum) {
        var id = $"SC-L{level}";
        const string criterion = "OpenSSF Scorecard published and within the level minimums";
        if (repo.Scorecard is null)
            return new AuditItem(id, TierByLevel[level], criterion, minimum.MinScore > 0 ? "fail" : "pass",
                minimum.MinScore > 0 ? "not published at api.scorecard.dev" : "", ["SA"]);

        var problems = new List<string>();
        if (repo.Scorecard.Score < minimum.MinScore) problems.Add($"score {repo.Scorecard.Score:0.0} < {minimum.MinScore}");
        foreach (var (check, min) in minimum.Checks)
            if (!repo.Scorecard.Checks.TryGetValue(check, out var score) || score < min)
                problems.Add($"{check} {(repo.Scorecard.Checks.TryGetValue(check, out var s) ? s.ToString("0") : "?")} < {min}");
        return new AuditItem(id, TierByLevel[level], criterion, problems.Count == 0 ? "pass" : "fail", string.Join(", ", problems), ["SA"]);
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
        /// <summary>Critérios por tier (passing, silver, gold) avaliados em cada repositório.</summary>
        public Dictionary<string, List<PracticeDefinition>> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Critérios avaliados uma vez na organização (cada um declara o próprio tier).</summary>
        public List<PracticeDefinition> Organization { get; set; } = [];

        /// <summary>Nomes de artefato que os critérios <c>summary-artifact</c> precisam baixar de cada repositório.</summary>
        public IEnumerable<string> SummaryArtifacts =>
            Tiers.Values.SelectMany(t => t).Where(d => d.Check == "summary-artifact").Select(d => d.Artifact!).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Um critério do selo Best Practices e como verificá-lo.</summary>
    public sealed class PracticeDefinition
    {
        /// <summary>Identificador (BP-xx, ORG-xx).</summary>
        public string Id { get; set; } = "";
        /// <summary>Tier do critério quando ele não está dentro de um tier (seção organization).</summary>
        public string? Tier { get; set; }
        /// <summary>Texto do critério.</summary>
        public string Criterion { get; set; } = "";
        /// <summary>Tipo de verificação: file-exists, ruleset-as-code, workflow-exists, workflow-uses, workflow-uses-all, actions-pinned-by-sha, release-asset, badge-api, summary-artifact, org-field, org-ruleset, manual.</summary>
        public string Check { get; set; } = "";
        /// <summary>Caminho do arquivo para verificações por arquivo.</summary>
        public string? Path { get; set; }
        /// <summary>Regex para verificações por conteúdo de workflow ou por nome de asset da release.</summary>
        public string? Pattern { get; set; }
        /// <summary>Regexes que todos precisam casar em algum workflow (workflow-uses-all).</summary>
        public List<string> Patterns { get; set; } = [];
        /// <summary>Regex de workflow aceito como evidência quando o repositório ainda não tem release (release-asset).</summary>
        public string? WorkflowPattern { get; set; }
        /// <summary>Nome do artefato de run com o sumário JSON (summary-artifact); o arquivo lido é <c>&lt;artifact&gt;.json</c>.</summary>
        public string? Artifact { get; set; }
        /// <summary>Objeto consultado em org-field: <c>organization</c> (padrão) ou <c>workflow</c> (permissões do Actions).</summary>
        public string? Source { get; set; }
        /// <summary>Campo JSON lido em org-field.</summary>
        public string? Field { get; set; }
        /// <summary>Valor esperado: campo em org-field (comparação textual, sem diferenciar maiúsculas) ou nível mínimo do selo em badge-api (padrão: o tier do item).</summary>
        public string? Expected { get; set; }
        /// <summary>Detalhe mostrado em itens manuais (por que não é automático, o que falta integrar).</summary>
        public string? Note { get; set; }
        /// <summary>Categorias do OSPS Baseline cobertas (AC, BR, DO, GV, LE, QA, SA, VM).</summary>
        public List<string> Osps { get; set; } = [];
    }

    [GeneratedRegex(@"^\s*-?\s*uses:\s*(?<uses>\S+)", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"@[0-9a-f]{40}(\s|$)")]
    private static partial Regex PinnedRef();
}
