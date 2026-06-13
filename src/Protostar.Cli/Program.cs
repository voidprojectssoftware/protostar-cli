using Protostar.Cli;
using Protostar.Cli.Commands;
using Protostar.Cli.Commands.Auth;
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
    config.AddCommand<InstallHooksCommand>("install-hooks")
        .WithDescription("Detect supported harnesses and install protostar capture hooks idempotently.");
    config.AddCommand<CaptureCommand>("capture")
        .WithDescription("Capture a harness hook event (invoked by installed hooks).")
        .IsHidden();
    config.AddCommand<SkillsCommand>("skills")
        .WithDescription("List skills discovered on disk (global and project) across supported harnesses.");

    config.AddBranch("auth", auth =>
    {
        auth.SetDescription("Authenticate to the protostar registry.");
        auth.AddCommand<LoginCommand>("login")
            .WithDescription("Sign in to the registry via your browser and store the session.");
        auth.AddCommand<LogoutCommand>("logout")
            .WithDescription("Remove the stored session for the registry.");
        auth.AddCommand<StatusCommand>("status")
            .WithDescription("Show whether you are signed in to the registry.");
    });
});

return app.Run(args);
