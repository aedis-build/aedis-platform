using System.Reflection;
using System.Text;

namespace Aedis.Cli.Templates;

/// <summary>Perfil de nuvem suportado pelo template do tenant.</summary>
public enum CloudProvider
{
    /// <summary>AWS (EKS, IRSA, Secrets Manager, ALB).</summary>
    Aws,
    /// <summary>Azure (AKS, Workload Identity, Key Vault, AGIC).</summary>
    Azure,
    /// <summary>DigitalOcean (DOKS, Vault, nginx + DO LB).</summary>
    DigitalOcean,
    /// <summary>Qualquer Kubernetes (Vault, nginx).</summary>
    Generic
}

/// <summary>Parâmetros de instanciação do repositório GitOps do tenant.</summary>
/// <param name="Cloud">Perfil de nuvem.</param>
/// <param name="Org">Organização do GitHub (dona do repo GitOps e dos times de CODEOWNERS).</param>
/// <param name="Environments">Ambientes (um <c>platform/values/&lt;env&gt;.yaml</c> por item).</param>
/// <param name="PlatformRef">Pin do aedis-platform nos workflows do tenant (<c>&lt;sha&gt; # vX.Y.Z</c>, de <see cref="PlatformPin.Resolve" />).</param>
/// <param name="Chart">Referência OCI do chart.</param>
/// <param name="ChartVersion">Versão do chart.</param>
public sealed record TenantOptions(
    CloudProvider Cloud,
    string Org,
    IReadOnlyList<string> Environments,
    string PlatformRef = "v1",
    string Chart = "oci://ghcr.io/aedis-build/charts/aedis-app",
    string ChartVersion = "0.1.0")
{
    /// <summary>Provider de segredos padrão do perfil.</summary>
    public string SecretsProvider => Cloud switch {
        CloudProvider.Aws => "aws",
        CloudProvider.Azure => "azure",
        _ => "vault"
    };

    /// <summary>Preset de ingress padrão do perfil.</summary>
    public string IngressPreset => Cloud switch {
        CloudProvider.Aws => "alb",
        CloudProvider.Azure => "agic",
        CloudProvider.DigitalOcean => "do-nginx",
        _ => "nginx"
    };

    /// <summary>Nome do perfil como o chart espera em <c>cloud.provider</c>.</summary>
    public string CloudName => Cloud switch {
        CloudProvider.Aws => "aws",
        CloudProvider.Azure => "azure",
        CloudProvider.DigitalOcean => "digitalocean",
        _ => "generic"
    };
}

/// <summary>
///     Esqueleto do repositório GitOps embarcado na CLI (cópia de <c>template/tenant</c> do aedis-platform).
///     Instanciar = copiar os arquivos, gerar um <c>platform/values/&lt;env&gt;.yaml</c> por ambiente e substituir
///     os tokens <c>__AEDIS_*__</c>.
/// </summary>
public static class TenantTemplate
{
    private const string Prefix = "tenant/";
    private const string EnvTemplate = "platform/values/_env.yaml.tmpl";

    /// <summary>Classe do ambiente pelo nome: prod/production/prd são <c>prod</c>, o resto <c>nonprod</c>.</summary>
    public static string EnvironmentClass(string environment) =>
        environment is "prod" or "production" or "prd" ? "prod" : "nonprod";

    /// <summary>Lê todos os arquivos embarcados de um template (caminho relativo → conteúdo); por padrão o do tenant.</summary>
    public static IReadOnlyDictionary<string, string> ReadEmbedded(string prefix = Prefix) {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal))) {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[name[prefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }

        return files;
    }

    /// <summary>Produz os arquivos finais do tenant (caminho relativo → conteúdo) a partir do template e das opções.</summary>
    public static IReadOnlyDictionary<string, string> Render(IReadOnlyDictionary<string, string> template, TenantOptions options) {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in template) {
            if (path == EnvTemplate) continue;
            output[path] = ReplaceTokens(content, options);
        }

        if (template.TryGetValue(EnvTemplate, out var envTemplate))
            foreach (var env in options.Environments) {
                var rendered = envTemplate.Replace("__AEDIS_ENV__", env).Replace("__AEDIS_ENV_CLASS__", EnvironmentClass(env));
                output[$"platform/values/{env}.yaml"] = ReplaceTokens(rendered, options);
            }

        return output;
    }

    /// <summary>Substitui os tokens globais do template.</summary>
    public static string ReplaceTokens(string content, TenantOptions o) => content
        .Replace("__AEDIS_CLOUD__", o.CloudName)
        .Replace("__AEDIS_ORG__", o.Org)
        .Replace("__AEDIS_PLATFORM_PIN__", o.PlatformRef)
        .Replace("__AEDIS_CHART_VERSION__", o.ChartVersion)
        .Replace("__AEDIS_CHART__", o.Chart)
        .Replace("__AEDIS_SECRETS_PROVIDER__", o.SecretsProvider)
        .Replace("__AEDIS_INGRESS_PRESET__", o.IngressPreset);

    /// <summary>Grava os arquivos em disco sob <paramref name="root" />, criando diretórios; não sobrescreve sem <paramref name="overwrite" />.</summary>
    public static int WriteTo(string root, IReadOnlyDictionary<string, string> files, bool overwrite) {
        var written = 0;
        foreach (var (path, content) in files) {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full) && !overwrite) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            written++;
        }

        return written;
    }
}
