using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aedis.Cli.Commands;

/// <summary><c>aedis platform new-app</c>: declara um app (<c>apps/&lt;nome&gt;/app.yaml</c> + <c>env/&lt;env&gt;.yaml</c>).</summary>
public static partial class NewAppCommand
{
    /// <summary>Monta o comando.</summary>
    public static Command Create() {
        var name = new Argument<string>("name") { Description = "Nome do app (DNS label; vira o diretório em apps/)" };
        var type = new Option<string>("--type") { Description = "api | worker | cronjob | site", DefaultValueFactory = _ => "api" };
        var ns = new Option<string>("--namespace") { Description = "Namespace de destino", Required = true };
        var repository = new Option<string?>("--image") { Description = "Repositório da imagem (sem tag). Padrão: ghcr.io/<org>/<nome> lido do README" };
        var schedule = new Option<string?>("--schedule") { Description = "Cron do CronJob (type cronjob)" };
        var fromProject = new Option<string?>("--from-project") { Description = "Diretório do projeto .NET: lê appsettings.json para sugerir secrets.keys" };
        var repo = new Option<string>("--repo") { Description = "Raiz do repo GitOps", DefaultValueFactory = _ => "." };

        var command = new Command("new-app", "Cria apps/<nome>/app.yaml e env/<env>.yaml para cada ambiente de platform/values.") {
            name, type, ns, repository, schedule, fromProject, repo
        };
        command.SetAction(parse => {
            var appName = parse.GetValue(name)!;
            if (!DnsLabel().IsMatch(appName)) {
                Console.Error.WriteLine($"nome inválido: {appName} (minúsculas, dígitos e hífen, até 52 caracteres)");
                return 2;
            }

            var appType = parse.GetValue(type)!;
            if (appType is not ("api" or "worker" or "cronjob" or "site")) {
                Console.Error.WriteLine("--type deve ser api, worker, cronjob ou site");
                return 2;
            }

            var root = Path.GetFullPath(parse.GetValue(repo)!);
            var appDir = Path.Combine(root, "apps", appName);
            if (Directory.Exists(appDir)) {
                Console.Error.WriteLine($"apps/{appName} já existe");
                return 1;
            }

            var environments = DiscoverEnvironments(root);
            if (environments.Count == 0) {
                Console.Error.WriteLine("nenhum platform/values/<env>.yaml encontrado: rode `aedis platform init` primeiro ou use --repo");
                return 1;
            }

            var image = parse.GetValue(repository) ?? $"ghcr.io/{DiscoverOrg(root) ?? "minha-org"}/{appName}";
            var secretKeys = parse.GetValue(fromProject) is { } project ? SuggestSecretKeys(project, appName) : [];

            Directory.CreateDirectory(Path.Combine(appDir, "env"));
            File.WriteAllText(Path.Combine(appDir, "app.yaml"), RenderAppYaml(appName, appType, parse.GetValue(ns)!, image, parse.GetValue(schedule), secretKeys), new UTF8Encoding(false));
            foreach (var env in environments)
                File.WriteAllText(Path.Combine(appDir, "env", $"{env}.yaml"), RenderEnvYaml(env), new UTF8Encoding(false));

            Console.WriteLine($"Criado apps/{appName} ({appType}, imagem {image}) para {string.Join(", ", environments)}.");
            Console.WriteLine("Próximos passos: preencher secrets.keys, ajustar resources/ingress e rodar `aedis platform validate`.");
            return 0;
        });
        return command;
    }

    /// <summary>Ambientes = nomes dos arquivos em <c>platform/values/*.yaml</c>.</summary>
    public static IReadOnlyList<string> DiscoverEnvironments(string root) {
        var dir = Path.Combine(root, "platform", "values");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.yaml").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null && !n.StartsWith('_')).Select(n => n!).Order(StringComparer.Ordinal).ToList();
    }

    private static string? DiscoverOrg(string root) {
        var codeowners = Path.Combine(root, "CODEOWNERS");
        if (!File.Exists(codeowners)) return null;
        var match = OrgInCodeowners().Match(File.ReadAllText(codeowners));
        return match.Success ? match.Groups["org"].Value : null;
    }

    /// <summary>
    ///     Sugere <c>secrets.keys</c> a partir do <c>appsettings.json</c>: toda folha cujo caminho contenha
    ///     ConnectionString, Secret, Password, Token, ApiKey ou Key vira uma chave <c>Secao__Chave</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, string Property)> SuggestSecretKeys(string projectDir, string appName) {
        var file = Directory.Exists(projectDir)
            ? Directory.GetFiles(projectDir, "appsettings.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (file is null) return [];

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var keys = new List<(string, string)>();
        Walk(doc.RootElement, [], keys);
        return keys;

        static void Walk(JsonElement element, List<string> path, List<(string, string)> keys) {
            if (element.ValueKind == JsonValueKind.Object) {
                foreach (var property in element.EnumerateObject()) {
                    path.Add(property.Name);
                    Walk(property.Value, path, keys);
                    path.RemoveAt(path.Count - 1);
                }
            }
            else if (path.Count > 0 && SensitiveName().IsMatch(path[^1])) {
                keys.Add((string.Join("__", path), string.Join(string.Empty, path.Select(p => p))));
            }
        }
    }

    internal static string RenderAppYaml(string name, string type, string ns, string image, string? schedule, IReadOnlyList<(string Name, string Property)> secretKeys) {
        var sb = new StringBuilder();
        sb.AppendLine("# Declaração do app (chart aedis-app). O que muda por ambiente fica em env/<env>.yaml.");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"namespace: {ns}");
        sb.AppendLine($"type: {type}");
        sb.AppendLine("owner: \"\"   # slug do time no GitHub");
        sb.AppendLine("catalog:");
        sb.AppendLine("  description: \"\"");
        sb.AppendLine("image:");
        sb.AppendLine($"  repository: {image}");
        sb.AppendLine("resources:");
        sb.AppendLine("  requests: { cpu: 100m, memory: 256Mi }");
        sb.AppendLine("  limits: { cpu: 500m, memory: 512Mi }");
        if (type == "site") {
            sb.AppendLine("# uid/gid nonroot das imagens distroless; o cache do Next é o único caminho gravável.");
            sb.AppendLine("security:");
            sb.AppendLine("  uid: 65532");
            sb.AppendLine("  gid: 65532");
            sb.AppendLine("  writablePaths:");
            sb.AppendLine("    - path: /app/.next/cache");
            sb.AppendLine("      sizeLimit: 256Mi");
            sb.AppendLine("observability:");
            sb.AppendLine("  metrics:");
            sb.AppendLine("    enabled: false");
        }

        if (type is "api" or "site") {
            sb.AppendLine("ingress:");
            sb.AppendLine("  enabled: false");
            sb.AppendLine($"  hosts: [{name}.example.com]");
            sb.AppendLine("  edge:");
            sb.AppendLine("    waf: false");
        }

        if (type == "worker") {
            sb.AppendLine("# Escala por fila (KEDA). Preencha queues conforme o broker ou remova o bloco.");
            sb.AppendLine("autoscaling:");
            sb.AppendLine("  keda:");
            sb.AppendLine("    enabled: false");
            sb.AppendLine("    queues: []");
        }

        if (type == "cronjob") {
            sb.AppendLine("cronjob:");
            sb.AppendLine($"  schedule: \"{schedule ?? "0 * * * *"}\"");
            sb.AppendLine("  timeZone: UTC");
        }

        // Um site não tem segredo de banco nem de fila: a sessão fica em cookie do backend.
        if (type == "site") return sb.ToString();

        sb.AppendLine("# Chaves do Secret <app>-secret: key é o segredo no cofre SEM o prefixo do ambiente.");
        sb.AppendLine("secrets:");
        if (secretKeys.Count == 0) {
            sb.AppendLine("  keys: []");
            sb.AppendLine("  # - name: Database__ConnectionString");
            sb.AppendLine($"  #   key: {name}");
            sb.AppendLine("  #   property: ConnectionString");
        }
        else {
            sb.AppendLine("  keys:");
            foreach (var (keyName, property) in secretKeys) {
                sb.AppendLine($"    - name: {keyName}");
                sb.AppendLine($"      key: {name}");
                sb.AppendLine($"      property: {property}");
            }
        }

        return sb.ToString();
    }

    internal static string RenderEnvYaml(string env) {
        var prod = Templates.TenantTemplate.EnvironmentClass(env) == "prod";
        var sb = new StringBuilder();
        sb.AppendLine(prod
            ? "# Produção: image.tag e image.digest mudam só por PR de promoção aprovado; sync manual."
            : "# Não-prod: image.tag é escrita pelo argocd-image-updater.");
        sb.AppendLine($"environment: {env}");
        sb.AppendLine("image:");
        sb.AppendLine(prod ? "  tag: 0.0.0\n  digest: \"\"" : "  tag: 0.0.0-rc.0");
        if (!prod) {
            sb.AppendLine("rollout:");
            sb.AppendLine("  steps:");
            sb.AppendLine("    - setWeight: 50");
            sb.AppendLine("    - pause: { duration: 2m }");
        }

        sb.AppendLine("sync:");
        sb.AppendLine("  automated: false");
        return sb.ToString();
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,50}[a-z0-9])?$")]
    private static partial Regex DnsLabel();

    [GeneratedRegex("@(?<org>[A-Za-z0-9-]+)/")]
    private static partial Regex OrgInCodeowners();

    [GeneratedRegex("ConnectionString|Secret|Password|Token|ApiKey|Key$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();
}
