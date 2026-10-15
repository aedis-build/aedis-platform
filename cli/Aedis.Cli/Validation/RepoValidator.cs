using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aedis.Cli.Validation;

/// <summary>Um item do sumário de validação (contrato <c>aedis.platform/validate-summary/v1</c>).</summary>
/// <param name="App">Nome do app.</param>
/// <param name="Environment">Ambiente ou "-".</param>
/// <param name="Check">Nome da checagem.</param>
/// <param name="Status">pass | fail | warn.</param>
/// <param name="Detail">Detalhe legível (vazio quando ok).</param>
public sealed record ValidationCheck(string App, string Environment, string Check, string Status, string Detail);

/// <summary>Resultado da validação do repo: checagens, manifestos renderizados por app/ambiente e o veredito.</summary>
public sealed class ValidationResult
{
    /// <summary>Checagens executadas.</summary>
    public List<ValidationCheck> Checks { get; } = [];

    /// <summary>Caminho do manifesto renderizado por <c>app/env</c>.</summary>
    public Dictionary<string, string> Rendered { get; } = new(StringComparer.Ordinal);

    /// <summary>Values mesclados (texto YAML das três camadas, na ordem) por <c>app/env</c>, para checagens de conformidade.</summary>
    public Dictionary<string, IReadOnlyList<string>> ValueFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>Verdadeiro quando nenhuma checagem falhou.</summary>
    public bool Passed => Checks.All(c => c.Status != "fail");

    /// <summary>Serializa no contrato JSON do sumário.</summary>
    public string ToJson() => JsonSerializer.Serialize(new {
        schema = "aedis.platform/validate-summary/v1",
        generatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        result = Passed ? "pass" : "fail",
        checks = Checks.Select(c => new { app = c.App, environment = c.Environment, check = c.Check, status = c.Status, detail = c.Detail })
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never });
}

/// <summary>Executa <c>helm template</c> com as três camadas de values.</summary>
public class HelmRunner
{
    /// <summary>Renderiza o chart; devolve (código de saída, stdout, stderr).</summary>
    public virtual async Task<(int ExitCode, string Stdout, string Stderr)> TemplateAsync(string release, string chart, string? chartVersion, IReadOnlyList<string> valueFiles, CancellationToken ct) {
        var args = new List<string> { "template", release, chart };
        if (!string.IsNullOrEmpty(chartVersion)) { args.Add("--version"); args.Add(chartVersion); }
        foreach (var f in valueFiles) { args.Add("-f"); args.Add(f); }
        return await ProcessRunner.RunAsync("helm", args, ct);
    }
}

/// <summary>Executa o kubeconform quando instalado.</summary>
public class KubeconformRunner
{
    private const string CrdCatalog = "https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json";

    /// <summary>Verdadeiro quando o binário está no PATH.</summary>
    public virtual bool IsAvailable() => ProcessRunner.Exists("kubeconform");

    /// <summary>Valida um manifesto; devolve (código, saída combinada).</summary>
    public virtual async Task<(int ExitCode, string Output)> ValidateAsync(string manifest, string kubernetesVersion, CancellationToken ct) {
        var (code, stdout, stderr) = await ProcessRunner.RunAsync("kubeconform",
            ["-strict", "-summary", "-kubernetes-version", kubernetesVersion, "-schema-location", "default", "-schema-location", CrdCatalog, manifest], ct);
        return (code, stdout + stderr);
    }
}

/// <summary>Valida um repositório GitOps do Aedis Platform (mesmas regras de <c>scripts/validate-apps.sh</c>).</summary>
public sealed partial class RepoValidator(HelmRunner helm, KubeconformRunner kubeconform)
{
    /// <summary>Renderiza e valida todos os apps; grava manifestos e <c>validate-summary.json</c> em <paramref name="outDir" />.</summary>
    public async Task<ValidationResult> ValidateAsync(string root, string? chart, string? chartVersion, string outDir, string kubernetesVersion, CancellationToken ct) {
        Directory.CreateDirectory(outDir);
        var result = new ValidationResult();
        var (resolvedChart, resolvedVersion) = ResolveChart(root, chart, chartVersion);
        var appsDir = Path.Combine(root, "apps");
        var appDirs = Directory.Exists(appsDir)
            ? Directory.GetDirectories(appsDir).Where(d => File.Exists(Path.Combine(d, "app.yaml"))).Order(StringComparer.Ordinal).ToList()
            : [];
        if (appDirs.Count == 0) {
            result.Checks.Add(new ValidationCheck("-", "-", "apps-present", "fail", $"nenhum apps/<app>/app.yaml em {root}"));
            await WriteSummaryAsync(outDir, result, ct);
            return result;
        }

        foreach (var appDir in appDirs) {
            var app = Path.GetFileName(appDir);
            var appFile = Path.Combine(appDir, "app.yaml");
            var declaredName = ScalarValue(File.ReadAllText(appFile), "name");
            if (declaredName != app)
                result.Checks.Add(new ValidationCheck(app, "-", "name-matches-directory", "fail", $"name={declaredName} (esperado {app})"));

            var envDir = Path.Combine(appDir, "env");
            var envFiles = Directory.Exists(envDir) ? Directory.GetFiles(envDir, "*.yaml").Order(StringComparer.Ordinal).ToList() : [];
            if (envFiles.Count == 0) {
                result.Checks.Add(new ValidationCheck(app, "-", "has-environment", "fail", "nenhum env/*.yaml"));
                continue;
            }

            foreach (var envFile in envFiles) {
                var env = Path.GetFileNameWithoutExtension(envFile);
                var values = new List<string>();
                var platformValues = Path.Combine(root, "platform", "values", $"{env}.yaml");
                if (File.Exists(platformValues)) values.Add(platformValues);
                else result.Checks.Add(new ValidationCheck(app, env, "platform-values", "warn", $"platform/values/{env}.yaml ausente"));
                values.Add(appFile);
                values.Add(envFile);
                result.ValueFiles[$"{app}/{env}"] = values;

                var (code, stdout, stderr) = await helm.TemplateAsync(app, resolvedChart, resolvedVersion, values, ct);
                if (code != 0) {
                    result.Checks.Add(new ValidationCheck(app, env, "helm-template", "fail", stderr.Trim()));
                    continue;
                }

                var rendered = Path.Combine(outDir, $"{app}-{env}.yaml");
                await File.WriteAllTextAsync(rendered, stdout, new UTF8Encoding(false), ct);
                result.Rendered[$"{app}/{env}"] = rendered;
                result.Checks.Add(new ValidationCheck(app, env, "helm-template", "pass", ""));

                var declaredEnv = ScalarValue(File.ReadAllText(envFile), "environment");
                if (!string.IsNullOrEmpty(declaredEnv) && declaredEnv != env)
                    result.Checks.Add(new ValidationCheck(app, env, "environment-matches-file", "fail", $"environment={declaredEnv}"));

                if (kubeconform.IsAvailable()) {
                    var (kcCode, output) = await kubeconform.ValidateAsync(rendered, kubernetesVersion, ct);
                    result.Checks.Add(new ValidationCheck(app, env, "kubeconform", kcCode == 0 ? "pass" : "fail", kcCode == 0 ? "" : output.Trim()));
                }
                else {
                    result.Checks.Add(new ValidationCheck(app, env, "kubeconform", "warn", "kubeconform não instalado"));
                }
            }
        }

        await WriteSummaryAsync(outDir, result, ct);
        return result;
    }

    /// <summary>Chart explícito, ou o do README do tenant (<c>uses: ...validate.yml</c> não serve; lê <c>chart:</c>/<c>chart-version:</c> do workflow), ou o OCI padrão.</summary>
    public static (string Chart, string? Version) ResolveChart(string root, string? chart, string? version) {
        if (!string.IsNullOrEmpty(chart)) return (chart, version);
        var workflow = Path.Combine(root, ".github", "workflows", "validate.yml");
        if (File.Exists(workflow)) {
            var text = File.ReadAllText(workflow);
            var c = WorkflowValue().Match(text) is { Success: true } m ? m.Groups["chart"].Value : null;
            var v = WorkflowVersion().Match(text) is { Success: true } mv ? mv.Groups["version"].Value : null;
            if (!string.IsNullOrEmpty(c)) return (c, version ?? v);
        }

        return ("oci://ghcr.io/aedis-build/charts/aedis-app", version);
    }

    internal static string ScalarValue(string yaml, string key) {
        var match = Regex.Match(yaml, $@"^{Regex.Escape(key)}:\s*[""']?(?<v>[^""'\r\n#]*)", RegexOptions.Multiline);
        return match.Success ? match.Groups["v"].Value.Trim() : string.Empty;
    }

    private static async Task WriteSummaryAsync(string outDir, ValidationResult result, CancellationToken ct) =>
        await File.WriteAllTextAsync(Path.Combine(outDir, "validate-summary.json"), result.ToJson(), new UTF8Encoding(false), ct);

    [GeneratedRegex(@"^\s*chart:\s*(?<chart>\S+)", RegexOptions.Multiline)]
    private static partial Regex WorkflowValue();

    [GeneratedRegex(@"^\s*chart-version:\s*[""']?(?<version>[^""'\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex WorkflowVersion();
}

/// <summary>Roda processos externos capturando a saída.</summary>
public static class ProcessRunner
{
    /// <summary>Verdadeiro quando <paramref name="executable" /> resolve no PATH.</summary>
    public static bool Exists(string executable) {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir, executable + ext))));
    }

    /// <summary>Executa e espera; devolve (código, stdout, stderr).</summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken ct) {
        var psi = new ProcessStartInfo(executable) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"não foi possível iniciar {executable}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }
}
