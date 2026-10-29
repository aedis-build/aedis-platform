using System.CommandLine;
using System.Text.RegularExpressions;
using Aedis.Cli.Templates;

namespace Aedis.Cli.Commands;

/// <summary><c>aedis platform new-site</c>: cria o repositório de um site Next.js com Aedis UI a partir do template embarcado.</summary>
public static partial class NewSiteCommand
{
    /// <summary>Monta o comando.</summary>
    public static Command Create() {
        var name = new Argument<string>("name") { Description = "Nome do site (DNS label; nome do pacote, da imagem e do app no chart, ou do Worker)" };
        var org = new Option<string>("--org") { Description = "Organização do GitHub (dona da imagem ghcr.io/<org>/<nome>)", Required = true };
        var target = new Option<string>("--target") { Description = "kubernetes (Dockerfile + chart type: site) ou cloudflare (Workers, deploy pelo deploy-site.yml)", DefaultValueFactory = _ => "kubernetes" };
        target.AcceptOnlyFromAmong("kubernetes", "cloudflare");
        var type = new Option<string>("--type") { Description = "Com --target cloudflare: static (export estático, CSP com hashes) ou ssr (OpenNext, CSP com nonce)", DefaultValueFactory = _ => "ssr" };
        type.AcceptOnlyFromAmong("static", "ssr");
        var domain = new Option<string?>("--domain") { Description = "Com --target cloudflare: domínio próprio do Worker (rota custom_domain), ex. www.minha-org.com" };
        var platformRef = new Option<string>("--platform-ref") { Description = "Ref do aedis-platform para os workflows (vX.Y.Z, v1 = última release do major, branch ou SHA); gravada como <sha> # <tag>", DefaultValueFactory = _ => "v1" };
        var output = new Option<string?>("--out") { Description = "Diretório de saída (padrão: ./<nome>)" };
        var force = new Option<bool>("--force") { Description = "Sobrescreve arquivos existentes" };

        var command = new Command("new-site", "Cria um site Next.js (App Router) com Aedis UI, cabeçalhos de segurança e CI do aedis-ci com language node. --target kubernetes: Dockerfile distroless e release da imagem em tag; --target cloudflare: wrangler.jsonc e deploy pelo deploy-site.yml.") {
            name, org, target, type, domain, platformRef, output, force
        };
        command.SetAction(parse => {
            var siteName = parse.GetValue(name)!;
            if (!DnsLabel().IsMatch(siteName)) {
                Console.Error.WriteLine($"nome inválido: {siteName} (minúsculas, dígitos e hífen, até 52 caracteres)");
                return 2;
            }

            var cloudflare = parse.GetValue(target) == "cloudflare";
            var host = parse.GetValue(domain) ?? "";
            if (cloudflare && !Hostname().IsMatch(host)) {
                Console.Error.WriteLine($"--target cloudflare pede --domain com o domínio próprio do site (recebido: '{host}')");
                return 2;
            }

            var files = SiteTemplate.Render(SiteTemplate.Files(parse.GetValue(target)!, parse.GetValue(type)!), siteName, parse.GetValue(org)!, PlatformPin.Resolve(parse.GetValue(platformRef)!), host);
            var root = Path.GetFullPath(parse.GetValue(output) ?? siteName);
            var written = TenantTemplate.WriteTo(root, files, parse.GetValue(force));

            Console.WriteLine($"Site {siteName} gerado em {root} ({written} arquivos).");
            Console.WriteLine(cloudflare
                ? "Próximos passos: `pnpm install`, `pnpm dev`, criar o environment prd com CLOUDFLARE_API_TOKEN e CLOUDFLARE_ACCOUNT_ID e declarar a zona no módulo infra/cloudflare do Aedis Platform."
                : "Próximos passos: `pnpm install`, `pnpm dev`, adicionar componentes com `pnpm dlx shadcn@latest add @aedis/<item>` e copiar deploy/app.yaml para o repo GitOps.");
            return 0;
        });
        return command;
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,50}[a-z0-9])?$")]
    private static partial Regex DnsLabel();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex Hostname();
}
