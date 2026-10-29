using System.Text.RegularExpressions;

namespace Aedis.Cli.Templates;

/// <summary>
///     Site Next.js com Aedis UI embarcado na CLI (cópia de <c>template/site</c> do aedis-platform). Instanciar =
///     copiar os arquivos substituindo os tokens <c>__AEDIS_NAME__</c>, <c>__AEDIS_ORG__</c>,
///     <c>__AEDIS_PLATFORM_PIN__</c> (de <see cref="PlatformPin.Resolve" />), <c>__AEDIS_DOMAIN__</c> e
///     <c>__AEDIS_COMPAT_DATE__</c> (de <see cref="CompatibilityDate" />). O alvo <c>cloudflare</c> parte do mesmo
///     site, sem o que é do cluster (Dockerfile, values do chart, release da imagem), com as camadas de
///     <c>template/site-cloudflare</c> por cima.
/// </summary>
public static partial class SiteTemplate
{
    /// <summary>Prefixo dos recursos embarcados do template.</summary>
    public const string Prefix = "site/";

    /// <summary>Prefixo das camadas do alvo cloudflare (<c>common/</c>, <c>static/</c>, <c>ssr/</c>).</summary>
    public const string CloudflarePrefix = "site-cloudflare/";

    private static readonly string[] ClusterOnly = ["Dockerfile", ".dockerignore", "deploy/app.yaml", ".github/workflows/release.yml", "scripts/standalone.mjs"];

    private static readonly string[] ServerOnly = ["proxy.ts", "test/proxy.test.ts"];

    /// <summary>
    ///     O template do alvo: <c>kubernetes</c> é o site com Dockerfile e chart; <c>cloudflare</c> tira o que é do
    ///     cluster (e, no <paramref name="type" /> <c>static</c>, o proxy, que um export estático não executa) e aplica
    ///     as camadas <c>common</c> e <paramref name="type" />.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Files(string target, string type) {
        var site = TenantTemplate.ReadEmbedded(Prefix);
        if (target != "cloudflare") return site;

        var files = site.Where(kv => !ClusterOnly.Contains(kv.Key) && !(type == "static" && ServerOnly.Contains(kv.Key)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var layers = TenantTemplate.ReadEmbedded(CloudflarePrefix);
        foreach (var layer in new[] { "common/", type + "/" })
            foreach (var (path, content) in layers.Where(kv => kv.Key.StartsWith(layer, StringComparison.Ordinal)))
                files[path[layer.Length..]] = content;
        return files;
    }

    /// <summary>Produz os arquivos do site (caminho relativo → conteúdo) a partir do template embarcado.</summary>
    public static IReadOnlyDictionary<string, string> Render(IReadOnlyDictionary<string, string> template, string name, string org, string platformRef, string domain = "") =>
        template.ToDictionary(kv => kv.Key, kv => kv.Value
            .Replace("__AEDIS_NAME__", name)
            .Replace("__AEDIS_ORG__", org)
            .Replace("__AEDIS_PLATFORM_PIN__", platformRef)
            .Replace("__AEDIS_DOMAIN__", domain)
            .Replace("__AEDIS_COMPAT_DATE__", CompatibilityDate(template, DateTime.UtcNow)), StringComparer.Ordinal);

    /// <summary>
    ///     O <c>compatibility_date</c> do Worker: o dia, limitado à data do <c>workerd</c> do lockfile do template, porque o
    ///     <c>wrangler dev</c> (o DAST do CI) recusa uma data mais nova que a do runtime que ele traz.
    /// </summary>
    public static string CompatibilityDate(IReadOnlyDictionary<string, string> template, DateTime today) {
        var day = today.ToString("yyyy-MM-dd");
        if (!template.TryGetValue("pnpm-lock.yaml", out var lockfile) || Workerd().Match(lockfile) is not { Success: true } m) return day;
        var runtime = $"{m.Groups["y"].Value}-{m.Groups["m"].Value}-{m.Groups["d"].Value}";
        return string.CompareOrdinal(runtime, day) < 0 ? runtime : day;
    }

    [GeneratedRegex(@"workerd@1\.(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})\.")]
    private static partial Regex Workerd();
}
