using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aedis.Cli.Validation;

/// <summary>Resultado de um controle para um nível.</summary>
/// <param name="Id">Identificador <c>PLT-xx</c>.</param>
/// <param name="Title">Descrição.</param>
/// <param name="Status">pass | fail | unknown.</param>
/// <param name="Detail">Detalhe.</param>
public sealed record ControlResult(string Id, string Title, string Status, string Detail);

/// <summary>Resultado da checagem de um nível.</summary>
public sealed class ConformanceResult
{
    /// <summary>Nome do nível (Baseline, Hardened, Regulated).</summary>
    public required string LevelName { get; init; }

    /// <summary>Controles avaliados (do nível pedido e dos inferiores).</summary>
    public List<ControlResult> Controls { get; } = [];

    /// <summary>Quantidade com status pass.</summary>
    public int Passed => Controls.Count(c => c.Status == "pass");

    /// <summary>Quantidade com status fail.</summary>
    public int Failed => Controls.Count(c => c.Status == "fail");

    /// <summary>Quantidade não verificável localmente.</summary>
    public int Unknown => Controls.Count(c => c.Status == "unknown");
}

/// <summary>
///     Checa os controles de <c>conformance/levels.yaml</c> que podem ser avaliados a partir dos manifestos
///     renderizados e do repo. Controles que dependem de sumários do CI (Scorecard, cobertura, SAST, DAST,
///     imagem assinada) ou do cluster (Kyverno) ficam como <c>unknown</c> nesta fase.
/// </summary>
public static partial class ConformanceChecker
{
    /// <summary>Avalia o nível <paramref name="level" /> (e os inferiores) sobre o resultado da validação.</summary>
    public static ConformanceResult Check(int level, ValidationResult validation, string root) {
        var levels = LoadLevels(root);
        var selected = levels.Where(l => l.Id <= level).ToList();
        var target = levels.FirstOrDefault(l => l.Id == level) ?? throw new ArgumentOutOfRangeException(nameof(level), $"nível {level} não existe em levels.yaml");
        var result = new ConformanceResult { LevelName = target.Name };

        foreach (var control in selected.SelectMany(l => l.Controls))
            result.Controls.Add(Evaluate(control, validation, root));

        return result;
    }

    private static ControlResult Evaluate(ControlDefinition control, ValidationResult validation, string root) {
        var manifests = validation.Rendered.ToDictionary(kv => kv.Key, kv => File.ReadAllText(kv.Value), StringComparer.Ordinal);
        var prod = manifests.Where(kv => IsProd(kv.Value)).ToList();
        return control.Check switch {
            "validate-pass" => Result(control, validation.Passed, validation.Passed ? "" : "validate falhou"),
            "chart-baseline" => Result(control, manifests.Count > 0 && manifests.All(kv => HasBaseline(kv.Value)), Missing(manifests, HasBaseline)),
            "secrets-via-eso" => Result(control, manifests.All(kv => !RawSecret().IsMatch(kv.Value)), Missing(manifests, m => !RawSecret().IsMatch(m))),
            "probes-enabled" => Result(control, manifests.Where(kv => !IsCronJob(kv.Value)).All(kv => kv.Value.Contains("readinessProbe:", StringComparison.Ordinal)), Missing(manifests.Where(kv => !IsCronJob(kv.Value)), m => m.Contains("readinessProbe:", StringComparison.Ordinal))),
            "pdb-present" => Result(control, manifests.Where(kv => IsApi(kv.Value)).All(kv => kv.Value.Contains("kind: PodDisruptionBudget", StringComparison.Ordinal)), Missing(manifests.Where(kv => IsApi(kv.Value)), m => m.Contains("kind: PodDisruptionBudget", StringComparison.Ordinal))),
            "prod-digest" => Result(control, prod.All(kv => ImageByDigest().IsMatch(kv.Value)), Missing(prod, m => ImageByDigest().IsMatch(m))),
            "egress-policy-prod" => Result(control, prod.All(kv => kv.Value.Contains("-egress", StringComparison.Ordinal)), Missing(prod, m => m.Contains("-egress", StringComparison.Ordinal))),
            "waf-declared-prod" => Result(control, prod.Where(kv => kv.Value.Contains("kind: Ingress", StringComparison.Ordinal)).All(kv => WafDeclared(kv.Key, validation)), "APIs expostas em prod sem ingress.edge.waf: true"),
            "prod-by-pr" => Result(control, CodeownersCoverProd(validation), "CODEOWNERS não cobre apps/*/env/prod.yaml"),
            "platform-pins-released" => Result(control, UnreleasedPlatformPins(root).Count == 0, string.Join(", ", UnreleasedPlatformPins(root))),
            _ => new ControlResult(control.Id, control.Title, "unknown", $"depende de {control.Source} (CI/cluster)")
        };
    }

    private static ControlResult Result(ControlDefinition c, bool ok, string detail) => new(c.Id, c.Title, ok ? "pass" : "fail", ok ? "" : detail);

    private static string Missing(IEnumerable<KeyValuePair<string, string>> manifests, Func<string, bool> predicate) =>
        string.Join(", ", manifests.Where(kv => !predicate(kv.Value)).Select(kv => kv.Key));

    private static bool HasBaseline(string m) =>
        m.Contains("runAsNonRoot: true", StringComparison.Ordinal) && m.Contains("readOnlyRootFilesystem: true", StringComparison.Ordinal)
        && m.Contains("allowPrivilegeEscalation: false", StringComparison.Ordinal) && m.Contains("type: RuntimeDefault", StringComparison.Ordinal);

    private static bool IsProd(string m) => m.Contains("aedis.build/environment-class: prod", StringComparison.Ordinal);
    private static bool IsApi(string m) => m.Contains("app.kubernetes.io/component: api", StringComparison.Ordinal);
    private static bool IsCronJob(string m) => m.Contains("kind: CronJob", StringComparison.Ordinal);

    private static bool WafDeclared(string key, ValidationResult validation) =>
        validation.ValueFiles.TryGetValue(key, out var files) && files.Any(f => WafTrue().IsMatch(File.ReadAllText(f)));

    private static bool CodeownersCoverProd(ValidationResult validation) {
        var first = validation.ValueFiles.Values.FirstOrDefault()?.LastOrDefault();
        if (first is null) return false;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(first)!, "..", "..", ".."));
        var codeowners = Path.Combine(root, "CODEOWNERS");
        return File.Exists(codeowners) && File.ReadAllText(codeowners).Contains("/apps/*/env/prod.yaml", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Linhas <c>uses: aedis-build/aedis-platform/...</c> dos workflows do repo que não pinam o SHA de uma tag de
    ///     release (<c>@&lt;sha&gt; # vX.Y.Z</c>): uma tag solta, uma branch ou um SHA sem a tag no comentário, que o
    ///     Dependabot não acompanha. Vazio quando todos cumprem, ou quando não há workflows.
    /// </summary>
    public static IReadOnlyList<string> UnreleasedPlatformPins(string root) {
        var dir = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(dir)) return [];
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".yaml", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++) {
                var match = PlatformUses().Match(lines[i]);
                if (!match.Success) continue;
                if (Sha40().IsMatch(match.Groups["ref"].Value) && ReleaseTag().IsMatch(match.Groups["comment"].Value)) continue;
                offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1} @{match.Groups["ref"].Value}");
            }
        }

        return offenders;
    }

    /// <summary><c>conformance/levels.yaml</c> do tenant, se existir, senão o embarcado.</summary>
    public static IReadOnlyList<LevelDefinition> LoadLevels(string root) {
        var local = Path.Combine(root, "conformance", "levels.yaml");
        string yaml;
        if (File.Exists(local)) {
            yaml = File.ReadAllText(local);
        }
        else {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("conformance/levels.yaml")!;
            using var reader = new StreamReader(stream);
            yaml = reader.ReadToEnd();
        }

        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        return deserializer.Deserialize<LevelsFile>(yaml).Levels;
    }

    /// <summary>Arquivo de níveis.</summary>
    public sealed class LevelsFile
    {
        /// <summary>Níveis declarados.</summary>
        public List<LevelDefinition> Levels { get; set; } = [];
    }

    /// <summary>Um nível.</summary>
    public sealed class LevelDefinition
    {
        /// <summary>1, 2 ou 3.</summary>
        public int Id { get; set; }

        /// <summary>Baseline, Hardened, Regulated.</summary>
        public string Name { get; set; } = "";

        /// <summary>Mínimos do OpenSSF Scorecard exigidos pelo nível; nulo quando o nível não os define.</summary>
        public ScorecardMinimum? Scorecard { get; set; }

        /// <summary>Controles do nível.</summary>
        public List<ControlDefinition> Controls { get; set; } = [];
    }

    /// <summary>Um controle.</summary>
    public sealed class ControlDefinition
    {
        /// <summary>PLT-xx.</summary>
        public string Id { get; set; } = "";

        /// <summary>Descrição.</summary>
        public string Title { get; set; } = "";

        /// <summary>Origem da evidência (chart, validate-summary, sast-summary...).</summary>
        public string Source { get; set; } = "";

        /// <summary>Nome da checagem implementada.</summary>
        public string Check { get; set; } = "";
    }

    [GeneratedRegex(@"^kind:\s*Secret\s*$", RegexOptions.Multiline)]
    private static partial Regex RawSecret();

    [GeneratedRegex(@"image:\s*\S+@sha256:[a-f0-9]{64}")]
    private static partial Regex ImageByDigest();

    [GeneratedRegex(@"waf:\s*true")]
    private static partial Regex WafTrue();

    [GeneratedRegex(@"uses:\s*aedis-build/aedis-platform/\S+@(?<ref>\S+)(?:\s+#\s*(?<comment>\S+))?")]
    private static partial Regex PlatformUses();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha40();

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex ReleaseTag();

    /// <summary>Mínimos do OpenSSF Scorecard exigidos por um nível: nota agregada e notas por check.</summary>
    public sealed class ScorecardMinimum
    {
        /// <summary>Nota agregada mínima (0 a 10).</summary>
        public double MinScore { get; set; }

        /// <summary>Nota mínima por check do Scorecard.</summary>
        public Dictionary<string, double> Checks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
