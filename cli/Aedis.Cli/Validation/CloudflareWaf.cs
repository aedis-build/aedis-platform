using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aedis.Cli.Validation;

/// <summary>
///     O lado Cloudflare do controle <c>waf-declared-prod</c> (PLT-20): um site com <c>wrangler.jsonc</c> de produção (uma
///     rota <c>custom_domain</c>) na raiz ou em <c>apps/*/</c> precisa de <c>workers_dev: false</c> e
///     <c>preview_urls: false</c> e de um módulo <c>infra/cloudflare</c> declarado em <c>infra/cloudflare/*.tf</c> cujo
///     <c>form_paths</c> cubra toda rota de formulário do site (route handler do Next que exporta <c>POST</c>), ou
///     <c>form_paths = []</c> explícito quando não há nenhuma.
/// </summary>
public static partial class CloudflareWaf
{
    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Os <c>wrangler.jsonc</c> de produção do repositório (caminhos completos), na raiz ou em <c>apps/*/</c>.</summary>
    public static IReadOnlyList<string> ProductionWranglers(string root) {
        var apps = Path.Combine(root, "apps");
        var candidates = new[] { Path.Combine(root, "wrangler.jsonc") }
            .Concat(Directory.Exists(apps) ? Directory.GetDirectories(apps).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "wrangler.jsonc")) : []);
        return candidates.Where(f => File.Exists(f) && Scopes(Parse(f)).Any(s => HasCustomDomain(s.Scope))).ToList();
    }

    /// <summary>O que falta para o controle passar; vazio quando cumpre ou quando não há <c>wrangler.jsonc</c> de produção.</summary>
    public static IReadOnlyList<string> Problems(string root) {
        var wranglers = ProductionWranglers(root);
        if (wranglers.Count == 0) return [];

        var problems = new List<string>();
        var formRoutes = new List<string>();
        foreach (var file in wranglers) {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            var config = Parse(file);
            foreach (var (scope, label) in Scopes(config).Where(s => HasCustomDomain(s.Scope) || s.Label == ""))
                foreach (var flag in new[] { "workers_dev", "preview_urls" })
                    if ((Flag(scope, flag) ?? Flag(config, flag)) is not false)
                        problems.Add($"{name}{label}: {flag} precisa ser false");
            formRoutes.AddRange(FormRoutes(Path.GetDirectoryName(file)!));
        }

        var declared = DeclaredFormPaths(root);
        if (declared is null) {
            problems.Add("sem módulo infra/cloudflare declarado em infra/cloudflare/*.tf (com form_paths, [] quando não há formulário)");
            return problems;
        }

        foreach (var route in formRoutes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Where(r => !declared.Contains(r)))
            problems.Add($"rota de formulário {route} fora do form_paths do módulo infra/cloudflare (sem rate limit)");
        return problems;
    }

    /// <summary>
    ///     A união dos <c>form_paths</c> dos módulos <c>infra/cloudflare</c> declarados em <c>infra/cloudflare/*.tf</c>;
    ///     <c>null</c> quando nenhum módulo declara <c>form_paths</c>.
    /// </summary>
    public static IReadOnlySet<string>? DeclaredFormPaths(string root) {
        var dir = Path.Combine(root, "infra", "cloudflare");
        if (!Directory.Exists(dir)) return null;
        HashSet<string>? paths = null;
        foreach (var file in Directory.GetFiles(dir, "*.tf")) {
            // Arquivos enumerados sob o repositório informado pelo operador, não entrada externa.
            // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
            var text = HclComment().Replace(File.ReadAllText(file), "");
            foreach (Match module in ModuleBlock().Matches(text)) {
                var body = module.Groups["body"].Value;
                if (!body.Contains("infra/cloudflare", StringComparison.Ordinal) || FormPaths().Match(body) is not { Success: true } list) continue;
                paths ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (Match path in Quoted().Matches(list.Groups["list"].Value)) paths.Add(path.Groups[1].Value);
            }
        }

        return paths;
    }

    /// <summary>
    ///     Rotas que recebem formulário no site em <paramref name="siteDir" />: route handlers do Next (<c>app/</c> ou
    ///     <c>src/app/</c>) que exportam <c>POST</c>, com grupos <c>(nome)</c> e slots <c>@nome</c> fora do caminho.
    /// </summary>
    public static IEnumerable<string> FormRoutes(string siteDir) {
        foreach (var app in new[] { Path.Combine(siteDir, "app"), Path.Combine(siteDir, "src", "app") }.Where(Directory.Exists)) {
            var handlers = Directory.EnumerateFiles(app, "route.*", SearchOption.AllDirectories)
                .Where(f => RouteFile().IsMatch(Path.GetFileName(f)) && PostExport().IsMatch(File.ReadAllText(f)));
            foreach (var handler in handlers) {
                var segments = Path.GetRelativePath(app, Path.GetDirectoryName(handler)!).Replace('\\', '/').Split('/')
                    .Where(s => s != "." && !(s.StartsWith('(') && s.EndsWith(')')) && !s.StartsWith('@'));
                // shortcut: segmentos dinâmicos ([id]) entram literais e só casam com um form_paths igual; trocar por prefixo quando um site tiver formulário em rota dinâmica.
                yield return "/" + string.Join('/', segments);
            }
        }
    }

    private static JsonElement Parse(string file) {
        using var document = JsonDocument.Parse(File.ReadAllText(file), Jsonc);
        return document.RootElement.Clone();
    }

    /// <summary>O nível de cima do wrangler.jsonc (rótulo vazio) e cada <c>env.&lt;nome&gt;</c>.</summary>
    private static IEnumerable<(JsonElement Scope, string Label)> Scopes(JsonElement config) {
        yield return (config, "");
        if (config.TryGetProperty("env", out var envs) && envs.ValueKind == JsonValueKind.Object)
            foreach (var env in envs.EnumerateObject())
                yield return (env.Value, $" (env.{env.Name})");
    }

    private static bool HasCustomDomain(JsonElement scope) =>
        scope.TryGetProperty("routes", out var routes) && routes.ValueKind == JsonValueKind.Array
        && routes.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("custom_domain", out var c) && c.ValueKind == JsonValueKind.True);

    private static bool? Flag(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    [GeneratedRegex(@"(?m)^\s*(#|//).*$")]
    private static partial Regex HclComment();

    [GeneratedRegex(@"module\s+""[^""]*""\s*\{(?<body>(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}")]
    private static partial Regex ModuleBlock();

    [GeneratedRegex(@"(?m)^\s*form_paths\s*=\s*\[(?<list>[^\]]*)\]")]
    private static partial Regex FormPaths();

    [GeneratedRegex(@"""([^""]*)""")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^route\.(ts|tsx|js|jsx|mjs)$")]
    private static partial Regex RouteFile();

    [GeneratedRegex(@"export\s+(?:async\s+)?function\s+POST\b|export\s+const\s+POST\b|export\s*\{[^}]*\bPOST\b")]
    private static partial Regex PostExport();
}
