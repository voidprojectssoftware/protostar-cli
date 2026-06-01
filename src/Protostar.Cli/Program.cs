using Protostar.Cli;
using Protostar.Cli.Commands;
using Spectre.Console.Cli;

// Spectre.Console.Cli command app. `protostar` runs DefaultCommand; `--version`/`-v` and `--help`
// are provided by the framework. Future tickets register commands (auth, sync, hooks) here.
var app = new CommandApp<DefaultCommand>();
app.Configure(config =>
{
    config.SetApplicationName("protostar");
    config.SetApplicationVersion(CliInfo.Version);

    config.AddCommand<InstallCommand>("install")
        .WithDescription("Install protostar to a per-user directory and add it to PATH.");
    config.AddCommand<UninstallCommand>("uninstall")
        .WithDescription("Remove an installed protostar binary.");
});

return app.Run(args);
