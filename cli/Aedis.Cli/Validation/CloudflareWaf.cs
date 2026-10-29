using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aedis.Cli.Validation;

/// <summary>Um workload declarado no módulo <c>infra/cloudflare</c>: o nome (a chave em <c>workloads</c>), os hostnames e as rotas de formulário.</summary>
/// <param name="Name">Chave do workload.</param>
/// <param name="Hostnames">Hostnames do workload.</param>
/// <param name="Forms">Rotas que recebem POST, cobertas pelo rate limit.</param>
public sealed record CloudflareWorkload(string Name, IReadOnlyList<string> Hostnames, IReadOnlyList<string> Forms);

/// <summary>
///     O lado Cloudflare do controle <c>waf-declared-prod</c> (PLT-20): um site com <c>wrangler.jsonc</c> de produção (uma
///     rota <c>custom_domain</c>) na raiz ou em <c>apps/*/</c> precisa de <c>workers_dev: false</c> e
///     <c>preview_urls: false</c>, e todo domínio próprio dele precisa pertencer a um workload do módulo
///     <c>infra/cloudflare</c> declarado em <c>infra/cloudflare/*.tf</c>, cujo <c>forms</c> cubra as rotas de formulário
///     do site (route handler do Next que exporta <c>POST</c>).
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
        var workloads = DeclaredWorkloads(root);
        if (workloads is null)
            problems.Add("sem módulo infra/cloudflare com workloads declarado em infra/cloudflare/*.tf");

        foreach (var file in wranglers) {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            var config = Parse(file);
            var owners = new List<CloudflareWorkload>();
            foreach (var (scope, label) in Scopes(config).Where(s => HasCustomDomain(s.Scope) || s.Label == "")) {
                foreach (var flag in new[] { "workers_dev", "preview_urls" })
                    if ((Flag(scope, flag) ?? Flag(config, flag)) is not false)
                        problems.Add($"{name}{label}: {flag} precisa ser false");
                if (workloads is null) continue;
                foreach (var domain in CustomDomains(scope)) {
                    var owner = workloads.FirstOrDefault(w => w.Hostnames.Contains(domain, StringComparer.OrdinalIgnoreCase));
                    if (owner is null) problems.Add($"{name}{label}: domínio {domain} não pertence a nenhum workload de infra/cloudflare (workloads[*].hostnames)");
                    else if (!owners.Contains(owner)) owners.Add(owner);
                }
            }

            var routes = FormRoutes(Path.GetDirectoryName(file)!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            foreach (var owner in owners)
                foreach (var route in routes.Where(r => !owner.Forms.Contains(r, StringComparer.Ordinal)))
                    problems.Add($"{name}: rota de formulário {route} fora de forms do workload {owner.Name} ({string.Join(", ", owner.Hostnames)}), sem rate limit");
        }

        return problems;
    }

    /// <summary>
    ///     Os workloads dos módulos <c>infra/cloudflare</c> declarados em <c>infra/cloudflare/*.tf</c> (o <c>source</c>
    ///     contém <c>infra/cloudflare</c>); <c>null</c> quando nenhum módulo declara <c>workloads</c>.
    /// </summary>
    public static IReadOnlyList<CloudflareWorkload>? DeclaredWorkloads(string root) {
        var dir = Path.Combine(root, "infra", "cloudflare");
        if (!Directory.Exists(dir)) return null;
        List<CloudflareWorkload>? workloads = null;
        foreach (var file in Directory.GetFiles(dir, "*.tf").Order(StringComparer.Ordinal)) {
            // Arquivos enumerados sob o repositório informado pelo operador, não entrada externa.
            // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
            var text = HclComment().Replace(File.ReadAllText(file), "");
            foreach (Match module in ModuleStart().Matches(text)) {
                var body = Block(text, module.Index + module.Length - 1);
                if (!body.Contains("infra/cloudflare", StringComparison.Ordinal) || WorkloadsStart().Match(body) is not { Success: true } start) continue;
                workloads ??= [];
                var entries = Block(body, start.Index + start.Length - 1);
                for (var entry = Entry().Match(entries); entry.Success; ) {
                    var open = entry.Index + entry.Length - 1;
                    var fields = Block(entries, open);
                    workloads.Add(new CloudflareWorkload(entry.Groups["key"].Value, List(fields, "hostnames"), List(fields, "forms")));
                    entry = Entry().Match(entries, Math.Min(open + fields.Length + 2, entries.Length));
                }
            }
        }

        return workloads;
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
                // shortcut: segmentos dinâmicos ([id]) entram literais e só casam com um forms igual; trocar por prefixo quando um site tiver formulário em rota dinâmica.
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

    private static IEnumerable<string> CustomDomains(JsonElement scope) =>
        scope.TryGetProperty("routes", out var routes) && routes.ValueKind == JsonValueKind.Array
            ? routes.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("custom_domain", out var c) && c.ValueKind == JsonValueKind.True
                    && r.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String)
                .Select(r => r.GetProperty("pattern").GetString()!)
            : [];

    /// <summary>
    ///     O conteúdo entre a chave aberta em <paramref name="open" /> e a que a fecha, pulando chaves dentro de strings
    ///     (as expressões de regra têm <c>{"GET" "HEAD"}</c>).
    /// </summary>
    // shortcut: interpolação com aspas dentro de "${...}" e heredocs não são entendidas; ler HCL de verdade se um módulo precisar.
    private static string Block(string text, int open) {
        var depth = 0;
        for (var i = open; i < text.Length; i++) {
            switch (text[i]) {
                case '"':
                    for (i++; i < text.Length && text[i] != '"'; i++)
                        if (text[i] == '\\') i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return text[(open + 1)..i];
            }
        }

        return text[(open + 1)..];
    }

    private static IReadOnlyList<string> List(string fields, string name) =>
        Regex.Match(fields, $@"(?:^|[\s{{,]){name}\s*=\s*\[(?<list>[^\]]*)\]") is { Success: true } m
            ? Quoted().Matches(m.Groups["list"].Value).Select(q => q.Groups[1].Value).ToList()
            : [];

    private static bool? Flag(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    [GeneratedRegex(@"(?m)^\s*(#|//).*$")]
    private static partial Regex HclComment();

    [GeneratedRegex(@"module\s+""[^""]*""\s*\{")]
    private static partial Regex ModuleStart();

    [GeneratedRegex(@"(?:^|\s)workloads\s*=\s*\{")]
    private static partial Regex WorkloadsStart();

    [GeneratedRegex(@"\G[\s,]*(?:""(?<key>[^""]+)""|(?<key>[A-Za-z_][\w-]*))\s*=\s*\{")]
    private static partial Regex Entry();

    [GeneratedRegex(@"""([^""]*)""")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^route\.(ts|tsx|js|jsx|mjs)$")]
    private static partial Regex RouteFile();

    [GeneratedRegex(@"export\s+(?:async\s+)?function\s+POST\b|export\s+const\s+POST\b|export\s*\{[^}]*\bPOST\b")]
    private static partial Regex PostExport();
}
