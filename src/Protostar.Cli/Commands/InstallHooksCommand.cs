using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Detects supported harnesses and installs protostar's capture hooks into the selected ones,
/// idempotently. With no selection flags and a TTY it prompts (space to toggle); otherwise it acts
/// non-interactively. <c>--remove</c> tears the hooks back out.
/// </summary>
internal sealed class InstallHooksCommand : Command<InstallHooksCommand.Settings>
{
    private readonly IHookInstallService _hooks;
    private readonly IHarnessCatalog _catalog;

    public InstallHooksCommand(IHookInstallService hooks, IHarnessCatalog catalog)
    {
        _hooks = hooks;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-H|--harness <ID>")]
        [Description("Target a specific harness by id (repeatable). Implies non-interactive.")]
        public string[]? Harness { get; init; }

        [CommandOption("--all")]
        [Description("Select all detected harnesses without prompting.")]
        public bool All { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Non-interactive: skip the prompt and use all detected harnesses.")]
        public bool Yes { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--exe-path <PATH>")]
        [Description("Path to the protostar binary the hooks should invoke. Defaults to this binary.")]
        public string? ExePath { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would change without writing.")]
        public bool DryRun { get; init; }

        [CommandOption("--remove")]
        [Description("Remove protostar's capture hooks instead of installing them.")]
        public bool Remove { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var options = new HookInstallOptions
        {
            RootOverride = settings.HarnessHome,
            HarnessIds = settings.Harness,
            DryRun = settings.DryRun,
            ExePathOverride = settings.ExePath,
        };

        // Prompt only when nothing forced a selection: no --yes, no --all, no explicit --harness ids,
        // and an actual TTY to prompt into. Otherwise act on every detected harness.
        var interactive = !settings.Yes
            && !settings.All
            && settings.Harness is not { Length: > 0 }
            && AnsiConsole.Profile.Capabilities.Interactive;
        HarnessSelector? selector = interactive ? HookInstallPresenter.Prompt : null;

        var result = settings.Remove ? _hooks.Uninstall(options, selector) : _hooks.Install(options, selector);
        return HookInstallPresenter.Render(result, settings.DryRun, _catalog);
    }
}
