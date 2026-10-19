using System.CommandLine;
using System.Text.RegularExpressions;
using Aedis.Cli.Templates;

namespace Aedis.Cli.Commands;

/// <summary><c>aedis platform new-site</c>: cria o repositório de um site Next.js com Aedis UI a partir do template embarcado.</summary>
public static partial class NewSiteCommand
{
    /// <summary>Monta o comando.</summary>
    public static Command Create() {
        var name = new Argument<string>("name") { Description = "Nome do site (DNS label; nome do pacote, da imagem e do app no chart)" };
        var org = new Option<string>("--org") { Description = "Organização do GitHub (dona da imagem ghcr.io/<org>/<nome>)", Required = true };
        var platformRef = new Option<string>("--platform-ref") { Description = "Ref do aedis-platform usada pelo workflow", DefaultValueFactory = _ => "v1" };
        var output = new Option<string?>("--out") { Description = "Diretório de saída (padrão: ./<nome>)" };
        var force = new Option<bool>("--force") { Description = "Sobrescreve arquivos existentes" };

        var command = new Command("new-site", "Cria um site Next.js (App Router, standalone) com Aedis UI, Dockerfile distroless, proxy.ts com os cabeçalhos de segurança e CI do app-ci com language node.") {
            name, org, platformRef, output, force
        };
        command.SetAction(parse => {
            var siteName = parse.GetValue(name)!;
            if (!DnsLabel().IsMatch(siteName)) {
                Console.Error.WriteLine($"nome inválido: {siteName} (minúsculas, dígitos e hífen, até 52 caracteres)");
                return 2;
            }

            var files = SiteTemplate.Render(TenantTemplate.ReadEmbedded(SiteTemplate.Prefix), siteName, parse.GetValue(org)!, parse.GetValue(platformRef)!);
            var root = Path.GetFullPath(parse.GetValue(output) ?? siteName);
            var written = TenantTemplate.WriteTo(root, files, parse.GetValue(force));

            Console.WriteLine($"Site {siteName} gerado em {root} ({written} arquivos).");
            Console.WriteLine("Próximos passos: `pnpm install`, `pnpm dev`, adicionar componentes com `pnpm dlx shadcn@latest add @aedis/<item>` e copiar deploy/app.yaml para o repo GitOps.");
            return 0;
        });
        return command;
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,50}[a-z0-9])?$")]
    private static partial Regex DnsLabel();
}
