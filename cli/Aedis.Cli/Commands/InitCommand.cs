using System.CommandLine;
using Aedis.Cli.Templates;

namespace Aedis.Cli.Commands;

/// <summary><c>aedis platform init</c>: cria o repositório GitOps do tenant a partir do template embarcado.</summary>
public static class InitCommand
{
    /// <summary>Monta o comando.</summary>
    public static Command Create() {
        var cloud = new Option<CloudProvider>("--cloud") { Description = "Perfil de nuvem: aws | azure | digitalocean | generic", DefaultValueFactory = _ => CloudProvider.Generic };
        var org = new Option<string>("--org") { Description = "Organização do GitHub (dona do repo e dos times de CODEOWNERS)", Required = true };
        var envs = new Option<string>("--envs") { Description = "Ambientes separados por vírgula", DefaultValueFactory = _ => "dev,prod" };
        var platformRef = new Option<string>("--platform-ref") { Description = "Ref do aedis-platform para os workflows (vX.Y.Z, v1 = última release do major, branch ou SHA); gravada como <sha> # <tag>", DefaultValueFactory = _ => "v1" };
        var chart = new Option<string>("--chart") { Description = "Referência OCI do chart", DefaultValueFactory = _ => "oci://ghcr.io/aedis-build/charts/aedis-app" };
        var chartVersion = new Option<string>("--chart-version") { Description = "Versão do chart", DefaultValueFactory = _ => "0.1.0" };
        var output = new Option<string>("--out") { Description = "Diretório de saída (padrão: diretório atual)", DefaultValueFactory = _ => "." };
        var force = new Option<bool>("--force") { Description = "Sobrescreve arquivos existentes" };

        var command = new Command("init", "Cria o repositório GitOps (platform/values, apps/, ApplicationSet, workflows, CODEOWNERS).") {
            cloud, org, envs, platformRef, chart, chartVersion, output, force
        };
        command.SetAction(parse => {
            var environments = parse.GetValue(envs)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (environments.Length == 0) {
                Console.Error.WriteLine("--envs precisa de ao menos um ambiente.");
                return 2;
            }

            var options = new TenantOptions(parse.GetValue(cloud), parse.GetValue(org)!, environments,
                PlatformPin.Resolve(parse.GetValue(platformRef)!), parse.GetValue(chart)!, parse.GetValue(chartVersion)!);
            var files = TenantTemplate.Render(TenantTemplate.ReadEmbedded(), options);
            var root = Path.GetFullPath(parse.GetValue(output)!);
            var written = TenantTemplate.WriteTo(root, files, parse.GetValue(force));

            Console.WriteLine($"Repositório GitOps gerado em {root} ({written} arquivos; nuvem {options.CloudName}; ambientes {string.Join(", ", environments)}).");
            Console.WriteLine("Próximos passos: preencher platform/values/<env>.yaml (região, prefixo do cofre, CIDR do proxy, OTLP), instalar argocd/applicationset.yaml e criar apps com `aedis platform new-app`.");
            return 0;
        });
        return command;
    }
}
