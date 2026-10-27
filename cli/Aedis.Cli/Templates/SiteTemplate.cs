namespace Aedis.Cli.Templates;

/// <summary>
///     Site Next.js com Aedis UI embarcado na CLI (cópia de <c>template/site</c> do aedis-platform). Instanciar =
///     copiar os arquivos substituindo os tokens <c>__AEDIS_NAME__</c>, <c>__AEDIS_ORG__</c> e
///     <c>__AEDIS_PLATFORM_PIN__</c> (de <see cref="PlatformPin.Resolve" />).
/// </summary>
public static class SiteTemplate
{
    /// <summary>Prefixo dos recursos embarcados do template.</summary>
    public const string Prefix = "site/";

    /// <summary>Produz os arquivos do site (caminho relativo → conteúdo) a partir do template embarcado.</summary>
    public static IReadOnlyDictionary<string, string> Render(IReadOnlyDictionary<string, string> template, string name, string org, string platformRef) =>
        template.ToDictionary(kv => kv.Key, kv => kv.Value
            .Replace("__AEDIS_NAME__", name)
            .Replace("__AEDIS_ORG__", org)
            .Replace("__AEDIS_PLATFORM_PIN__", platformRef), StringComparer.Ordinal);
}
