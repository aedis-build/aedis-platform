using System.CommandLine;
using Aedis.Cli.Validation;

namespace Aedis.Cli.Commands;

/// <summary><c>aedis platform validate</c>: renderiza e valida todos os apps; opcionalmente checa um nível de conformidade.</summary>
public static class ValidateCommand
{
    /// <summary>Monta o comando.</summary>
    public static Command Create() {
        var repo = new Option<string>("--repo") { Description = "Raiz do repo GitOps", DefaultValueFactory = _ => "." };
        var chart = new Option<string?>("--chart") { Description = "Chart: diretório local ou oci://... (padrão: o chart/versão do README do tenant ou oci://ghcr.io/aedis-build/charts/aedis-app)" };
        var chartVersion = new Option<string?>("--chart-version") { Description = "Versão do chart quando OCI" };
        var output = new Option<string?>("--out") { Description = "Diretório dos manifestos renderizados e do validate-summary.json (padrão: temporário)" };
        var k8s = new Option<string>("--kubernetes-version") { Description = "Versão do Kubernetes para o kubeconform", DefaultValueFactory = _ => "1.31.0" };
        var level = new Option<int?>("--level") { Description = "Checa os controles do nível de conformidade (1, 2 ou 3)" };

        var command = new Command("validate", "helm template (3 camadas) + schema + kubeconform + sumário JSON; com --level, checa os controles verificáveis localmente.") {
            repo, chart, chartVersion, output, k8s, level
        };
        command.SetAction(async (parse, cancellationToken) => {
            var root = Path.GetFullPath(parse.GetValue(repo)!);
            var outDir = parse.GetValue(output) is { } o ? Path.GetFullPath(o) : Path.Combine(Path.GetTempPath(), "aedis-validate-" + Guid.NewGuid().ToString("N")[..8]);
            var validator = new RepoValidator(new HelmRunner(), new KubeconformRunner());
            var result = await validator.ValidateAsync(root, parse.GetValue(chart), parse.GetValue(chartVersion), outDir, parse.GetValue(k8s)!, cancellationToken);

            foreach (var check in result.Checks)
                Console.WriteLine($"{Status(check.Status)} {check.App} ({check.Environment}) {check.Check}{(string.IsNullOrEmpty(check.Detail) ? "" : ": " + check.Detail.Trim())}");
            Console.WriteLine($"manifestos e sumário em {outDir}");

            var exit = result.Passed ? 0 : 1;
            if (parse.GetValue(level) is { } lvl) {
                var conformance = ConformanceChecker.Check(lvl, result, root);
                Console.WriteLine();
                Console.WriteLine($"Nível L{lvl} ({conformance.LevelName}):");
                foreach (var c in conformance.Controls)
                    Console.WriteLine($"  {Status(c.Status)} {c.Id} {c.Title}{(string.IsNullOrEmpty(c.Detail) ? "" : " — " + c.Detail)}");
                Console.WriteLine($"  {conformance.Passed} ok, {conformance.Failed} falhas, {conformance.Unknown} não verificáveis localmente (precisam dos sumários do CI)");
                if (conformance.Failed > 0) exit = 1;
            }

            Console.WriteLine(exit == 0 ? "OK" : "FALHOU");
            return exit;
        });
        return command;
    }

    private static string Status(string status) => status switch {
        "pass" => "[ok]  ",
        "fail" => "[FAIL]",
        "warn" => "[warn]",
        _ => "[ ?  ]"
    };
}
