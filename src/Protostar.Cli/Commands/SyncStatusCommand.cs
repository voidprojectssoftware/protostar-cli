using System.ComponentModel;
using Protostar.Cli.Status;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary><c>protostar status</c>: whether this machine is signed in, hooked up, and backing up in the background.</summary>
/// <remarks>Offline first; the only network call is a short check of the stored sign-in, skipped with <c>--offline</c>.</remarks>
internal sealed class SyncStatusCommand : AsyncCommand<SyncStatusCommand.Settings>
{
    private readonly ISyncStatusService _status;

    public SyncStatusCommand(ISyncStatusService status) => _status = status;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
        public string? Registry { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--offline")]
        [Description("Do not ask the registry whether the sign-in is still good; answer from this machine alone.")]
        public bool Offline { get; init; }

        [CommandOption("--json")]
        [Description("Print one JSON object instead of the report, for agents and scripts.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        SyncStatus status;
        try
        {
            status = await _status.CheckAsync(new SyncStatusRequest(settings.Registry, settings.HarnessHome, settings.Offline), cancellation);
        }
        catch (FormatException ex)
        {
            return SyncStatusPresenter.RenderInvalidRegistry(ex.Message, settings.Json);
        }

        return SyncStatusPresenter.Render(status, settings.Json);
    }
}
