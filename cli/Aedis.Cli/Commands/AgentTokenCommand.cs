using System.CommandLine;
using Aedis.Cli.Org;

namespace Aedis.Cli.Commands;

/// <summary>
///     <c>aedis agent token</c>: emite o token de instalação (válido por uma hora) da GitHub App de um agente, para o
///     agente abrir branches e pull requests com identidade própria em vez da credencial de uma pessoa. A App não é
///     code owner e não tem permissão de administração: o que ela abre ainda passa pela revisão humana e pelo portão.
/// </summary>
public static class AgentTokenCommand
{
    /// <summary>Monta o comando; <paramref name="handlerFactory" /> troca o transporte HTTP nos testes.</summary>
    public static Command Create(Func<HttpMessageHandler>? handlerFactory = null) {
        var org = new Option<string>("--org") { Description = "Organization where the agent app is installed", Required = true };
        var appId = new Option<string?>("--app-id") { Description = "Agent GitHub App ID (default: AEDIS_AGENT_APP_ID)" };
        var key = new Option<string?>("--key") { Description = "Path to the app private key or the PEM itself (default: AEDIS_AGENT_PRIVATE_KEY)" };

        var command = new Command("token", "Prints a one-hour installation token of the agent's GitHub App, for git and gh.") { org, appId, key };
        command.SetAction(async (parse, cancellationToken) => {
            var id = parse.GetValue(appId) ?? Environment.GetEnvironmentVariable("AEDIS_AGENT_APP_ID");
            var pem = parse.GetValue(key) ?? Environment.GetEnvironmentVariable("AEDIS_AGENT_PRIVATE_KEY");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pem)) {
                await Console.Error.WriteLineAsync("pass --app-id and --key (or AEDIS_AGENT_APP_ID and AEDIS_AGENT_PRIVATE_KEY)");
                return 2;
            }

            if (File.Exists(pem)) pem = await File.ReadAllTextAsync(pem, cancellationToken);
            var api = new GitHubApi(new HttpClient(handlerFactory?.Invoke() ?? new HttpClientHandler()), "");
            Console.Out.Write(await api.CreateInstallationTokenAsync(parse.GetValue(org)!, GitHubAppAuth.CreateJwt(id, pem), cancellationToken));
            return 0;
        });
        return command;
    }
}
