using System.ComponentModel;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Infrastructure;
using Protostar.Cli.Setup;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary><c>protostar setup</c>: signs in, installs hooks, pushes and pulls skills and backs up sessions, idempotently.</summary>
/// <remarks>The steps are in <see cref="SetupService"/> and the output in <see cref="SetupPresenter"/>.</remarks>
internal sealed class SetupCommand : AsyncCommand<SetupCommand.Settings>
{
    private readonly ISetupService _setup;
    private readonly IHarnessCatalog _catalog;

    public SetupCommand(ISetupService setup, IHarnessCatalog catalog)
    {
        _setup = setup;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
        public string? Registry { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Back up this machine's sessions without asking. Without it, setup asks on a terminal and skips the backup otherwise.")]
        public bool Yes { get; init; }

        [CommandOption("--no-browser")]
        [Description("Print the sign-in URL instead of opening a browser automatically.")]
        public bool NoBrowser { get; init; }

        [CommandOption("--provider <NAME>")]
        [Description("Skip the registry's sign-in chooser and go straight to this provider (e.g. github).")]
        public string? Provider { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Ctrl+C during the session backup stops it cleanly, with the ledger intact, rather than killing the process.
        using var ctrlC = new CtrlCCancellation(cancellation);
        var request = new SetupRequest(
            settings.Registry,
            settings.HarnessHome,
            new SignInOptions(settings.Provider, settings.NoBrowser, TimeSpan.FromMinutes(5)),
            settings.Yes,
            CanAsk: !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive,
            ProjectStart: Directory.GetCurrentDirectory());
        var interaction = SetupPresenter.Interaction(_catalog, SetupPresenter.PushCommand(settings.Registry, settings.HarnessHome));
        return SetupPresenter.Render(await _setup.RunAsync(request, interaction, ctrlC.Token));
    }
}
