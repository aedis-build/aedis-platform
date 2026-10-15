using System.CommandLine;
using System.Text;
using Aedis.Cli.Commands;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

var root =new RootCommand("Aedis Platform — GitOps seguro e agnóstico de nuvem para aplicações Aedis.");
var platform = new Command("platform", "Repositório GitOps: init, new-app, validate.");
platform.Subcommands.Add(InitCommand.Create());
platform.Subcommands.Add(NewAppCommand.Create());
platform.Subcommands.Add(ValidateCommand.Create());
root.Subcommands.Add(platform);

return await root.Parse(args).InvokeAsync();
