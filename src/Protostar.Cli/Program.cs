using Microsoft.Extensions.DependencyInjection;
using Protostar.Cli;
using Protostar.Cli.Auth;
using Protostar.Cli.Commands;
using Protostar.Cli.Commands.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Hooks;
using Protostar.Cli.Infrastructure;
using Protostar.Cli.Skills;
using Spectre.Console.Cli;

// Business-logic services are registered in the DI container and constructor-injected into commands.
// This keeps commands thin and lets unit tests substitute fakes for the file- and network-touching
// services instead of reaching for real disk or registries.
var services = new ServiceCollection();
services.AddSingleton<ISkillService, SkillService>();
services.AddSingleton<IHookInstallService, HookInstallService>();
services.AddSingleton<ITokenStore, TokenStore>();

// Harnesses and their collaborators live in the container too: register one IHarness per harness
// (the extension point) plus the catalog that exposes them and the ClaudeCode skill parser/mapper.
services.AddSingleton<IClaudeCodeSkillParser, ClaudeCodeSkillParser>();
services.AddSingleton<IClaudeCodeSkillMapper, ClaudeCodeSkillMapper>();
services.AddSingleton<IHarness, ClaudeCodeHarness>();
services.AddSingleton<IHarnessCatalog, HarnessCatalog>();

// Spectre.Console.Cli command app. `protostar` runs DefaultCommand; `--version`/`-v` and `--help`
// are provided by the framework. The registrar resolves commands (and their dependencies) from the
// container above.
var app = new CommandApp<DefaultCommand>(new TypeRegistrar(services));
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
