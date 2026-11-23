using System.ComponentModel;
using Protostar.Cli.Agents;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary><c>protostar agents push [NAME...]</c>: backs up local custom agents to the registry.</summary>
/// <remarks>
/// With no names it pushes every agent found: personal ones, and the project's when run inside one. With the hidden
/// <c>--from-hook</c> it is the background push the capture hook starts at <c>SessionEnd</c> (see <see cref="BackgroundPush"/>):
/// detached, trusting the ledger, and writing to <c>capture.log</c> instead of the console.
/// </remarks>
internal sealed class AgentsPushCommand : AsyncCommand<AgentsPushCommand.Settings>
{
    private readonly IAgentPushService _push;
    private readonly IHarnessCatalog _catalog;

    public AgentsPushCommand(IAgentPushService push, IHarnessCatalog catalog)
    {
        _push = push;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Agent name(s) to back up, as the 'name' in their front matter. Omit to back up every agent found.")]
        public string[] Names { get; init; } = [];

        [CommandOption("-H|--harness <ID>")]
        [Description("Only back up agents from this harness id.")]
        public string? Harness { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--project <DIR>")]
        [Description("Where to start looking for project agents. Defaults to the current directory.")]
        public string? Project { get; init; }

        [CommandOption("--personal-only")]
        [Description("Back up only personal agents; skip the project's.")]
        public bool PersonalOnly { get; init; }

        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
        public string? Registry { get; init; }

        [CommandOption("--force")]
        [Description("Upload even if unchanged since the last backup, replace the registry's version when it changed elsewhere since this copy was synced (keep mine), and restore an agent deleted or renamed in the registry.")]
        public bool Force { get; init; }

        [CommandOption("--from-hook", IsHidden = true)]
        [Description("Run as the capture hook's background push: detached, ledger trusted, output to capture.log.")]
        public bool FromHook { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var request = new AgentPushRequest(
            settings.Names,
            settings.Harness,
            settings.HarnessHome,
            settings.PersonalOnly ? null : settings.Project ?? Directory.GetCurrentDirectory(),
            settings.Registry,
            settings.Force && !settings.FromHook,
            settings.FromHook);

        if (settings.FromHook)
        {
            BackgroundPush.DetachFromTerminal();
            return CapturePresenter.RenderBackgroundAgentPush(await _push.PushAsync(request, cancellation), Console.Out, CaptureLog.Append, CaptureRecords.Append);
        }

        return AgentPushPresenter.Render(await _push.PushAsync(request, cancellation), _catalog);
    }
}
