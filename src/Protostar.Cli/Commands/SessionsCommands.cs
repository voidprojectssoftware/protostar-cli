using System.ComponentModel;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Infrastructure;
using Protostar.Cli.Memory;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Options shared by the <c>protostar sessions</c> commands.</summary>
internal class SessionsSettings : CommandSettings
{
    [CommandOption("--registry <URL>")]
    [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
    public string? Registry { get; init; }
}

/// <summary>Options for the commands that read transcripts from disk.</summary>
internal class LocalSessionsSettings : SessionsSettings
{
    [CommandOption("-H|--harness <ID>")]
    [Description("Only this harness id.")]
    public string? Harness { get; init; }

    [CommandOption("--harness-home <DIR>")]
    [Description("Override the harness config root (testing or a non-default location).")]
    public string? HarnessHome { get; init; }
}

/// <summary><c>protostar sessions push</c>: backs up every session transcript on this machine.</summary>
/// <remarks>
/// <para>Newest transcripts first, with progress, and resumable: Ctrl+C stops it cleanly and the next push carries on.</para>
/// <para>
/// With the hidden <c>--session</c> option it is the background push a capture hook starts (see
/// <see cref="BackgroundPush"/>): one session, no progress display, and problems go to <c>capture.log</c>. It then
/// backs up the memory of that session's project (<see cref="MemoryPushRequest.SessionPath"/>), which costs a stat per
/// memory file when nothing changed, so agent memory goes up as it is written without a full scan.
/// </para>
/// </remarks>
internal sealed class SessionsPushCommand : AsyncCommand<SessionsPushCommand.Settings>
{
    private readonly ISessionSyncService _sessions;
    private readonly IMemorySyncService _memory;
    private readonly IHarnessCatalog _catalog;

    public SessionsPushCommand(ISessionSyncService sessions, IMemorySyncService memory, IHarnessCatalog catalog)
    {
        _sessions = sessions;
        _memory = memory;
        _catalog = catalog;
    }

    public sealed class Settings : LocalSessionsSettings
    {
        [CommandOption("--session <PATH>", IsHidden = true)]
        [Description("Push only this session transcript (and its subagents), detached from the terminal. Started by the capture hooks.")]
        public string? Session { get; init; }

        [CommandOption("--throttle <SECONDS>", IsHidden = true)]
        [Description("With --session: skip a transcript whose last push started less than this many seconds ago.")]
        public int? Throttle { get; init; }

        [CommandOption("--verify")]
        [Description("Ask the registry what it holds of every backed-up transcript, in batches, and back up again what it lost.")]
        public bool Verify { get; init; }

        [CommandOption("--json")]
        [Description("Print one JSON record per transcript instead of progress and a table, for agents and scripts.")]
        public bool Json { get; init; }

        // Hooks run on every turn; a verify costs requests even when nothing changed, so it is never part of one. A
        // hook's push writes to capture.log, not standard output, so it has nothing to print as JSON.
        public override ValidationResult Validate() =>
            Verify && Session is not null
                ? ValidationResult.Error("--verify cannot be combined with --session; capture hooks never verify.")
                : Json && Session is not null
                    ? ValidationResult.Error("--json cannot be combined with --session; a hook's push logs to capture.log.")
                    : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.Session is { } session)
        {
            BackgroundPush.DetachFromTerminal();
            var throttle = settings.Throttle is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            var one = new SessionSyncRequest(settings.Registry, settings.Harness, settings.HarnessHome, session, null, throttle);
            var exit = CapturePresenter.RenderBackgroundPush(await _sessions.SyncAsync(one, null, cancellation), Console.Out, CaptureLog.Append, CaptureRecords.Append);
            var memory = await _memory.PushAsync(new MemoryPushRequest(settings.Registry, settings.HarnessHome, session), cancellation);
            CapturePresenter.RenderBackgroundMemoryPush(memory, Console.Out, CaptureLog.Append, CaptureRecords.Append);
            return exit;
        }

        // Ctrl+C stops between or during chunks, with the ledger and locks left clean, rather than killing the process.
        using var ctrlC = new CtrlCCancellation(cancellation);
        var request = new SessionSyncRequest(settings.Registry, settings.Harness, settings.HarnessHome, null, null, null, settings.Verify);
        // Progress lines would put text that is not JSON on standard output.
        var result = settings.Json
            ? await _sessions.SyncAsync(request, null, ctrlC.Token)
            : await SessionsPresenter.WithProgressAsync(progress => _sessions.SyncAsync(request, progress, ctrlC.Token));
        return SessionsPresenter.RenderSync(result, _catalog, settings.Json);
    }
}

/// <summary><c>protostar sessions status</c>: what is and is not backed up on this machine. Works offline, except with <c>--verify</c>.</summary>
internal sealed class SessionsStatusCommand : AsyncCommand<SessionsStatusCommand.Settings>
{
    private readonly ISessionSyncService _sessions;
    private readonly IHarnessCatalog _catalog;

    public SessionsStatusCommand(ISessionSyncService sessions, IHarnessCatalog catalog)
    {
        _sessions = sessions;
        _catalog = catalog;
    }

    public sealed class Settings : LocalSessionsSettings
    {
        [CommandOption("-a|--all")]
        [Description("List every transcript, backed up ones too, not only the pending ones.")]
        public bool All { get; init; }

        [CommandOption("--verify")]
        [Description("Also ask the registry what it holds, to confirm the backups are really there (before wiping a machine). Changes nothing.")]
        public bool Verify { get; init; }

        [CommandOption("--json")]
        [Description("Print one JSON record per transcript instead of a table, for agents and scripts.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var result = settings.Verify
            ? await _sessions.VerifyAsync(settings.Registry, settings.Harness, settings.HarnessHome, cancellation)
            : _sessions.Status(settings.Registry, settings.Harness, settings.HarnessHome);
        return SessionsPresenter.RenderStatus(result, _catalog, settings.All, settings.Json);
    }
}

/// <summary><c>protostar sessions list</c>: the sessions backed up from every machine, plus this machine's not backed up yet.</summary>
internal sealed class SessionsListCommand : AsyncCommand<SessionsListCommand.Settings>
{
    private readonly ISessionSyncService _sessions;
    private readonly IHarnessCatalog _catalog;

    public SessionsListCommand(ISessionSyncService sessions, IHarnessCatalog catalog)
    {
        _sessions = sessions;
        _catalog = catalog;
    }

    public sealed class Settings : LocalSessionsSettings
    {
        [CommandOption("-p|--project <ID|NAME>")]
        [Description("Only sessions in this project: its id, or text in its name (for example the repo name).")]
        public string? Project { get; init; }

        [CommandOption("--machine <ID>")]
        [Description("Only sessions from this machine id.")]
        public Guid? Machine { get; init; }

        [CommandOption("--since <DATE>")]
        [Description("Only sessions active on or after this date or time.")]
        public DateTimeOffset? Since { get; init; }

        [CommandOption("-n|--limit <N>")]
        [Description("How many to show (1 to 200). Defaults to 25.")]
        public int Limit { get; init; } = 25;

        [CommandOption("--json")]
        [Description("Print one JSON record per session instead of a table, for agents and scripts.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var query = new TranscriptListQuery(settings.Project, settings.Machine, settings.Harness, settings.Since, settings.Limit);
        var result = await _sessions.ListAsync(settings.Registry, settings.HarnessHome, query, cancellation);
        return SessionsPresenter.RenderList(result, _catalog, query, settings.Json);
    }
}
