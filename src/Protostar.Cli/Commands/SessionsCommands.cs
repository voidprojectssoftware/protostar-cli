using System.ComponentModel;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Options shared by the <c>protostar sessions</c> commands.</summary>
internal class SessionsSettings : CommandSettings
{
    [CommandOption("--registry <URL>")]
    [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the built-in dev default.")]
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
/// With the hidden <c>--session</c> option it is the background push a capture hook starts (see
/// <see cref="BackgroundPush"/>): one session, no progress display, and problems go to <c>capture.log</c>.
/// </remarks>
internal sealed class SessionsPushCommand : AsyncCommand<SessionsPushCommand.Settings>
{
    private readonly ISessionSyncService _sessions;
    private readonly IHarnessCatalog _catalog;

    public SessionsPushCommand(ISessionSyncService sessions, IHarnessCatalog catalog)
    {
        _sessions = sessions;
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
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.Session is { } session)
        {
            BackgroundPush.DetachFromTerminal();
            var throttle = settings.Throttle is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            var one = new SessionSyncRequest(settings.Registry, settings.Harness, settings.HarnessHome, session, null, throttle);
            return CapturePresenter.RenderBackgroundPush(await _sessions.SyncAsync(one, null, cancellation), Console.Out, CaptureLog.Append);
        }

        var request = new SessionSyncRequest(settings.Registry, settings.Harness, settings.HarnessHome, null, null, null);
        var result = await SessionsPresenter.WithProgressAsync(progress => _sessions.SyncAsync(request, progress, cancellation));
        return SessionsPresenter.RenderSync(result, _catalog);
    }
}

/// <summary><c>protostar sessions status</c>: what is and is not backed up on this machine. Works offline.</summary>
internal sealed class SessionsStatusCommand : Command<SessionsStatusCommand.Settings>
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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation) =>
        SessionsPresenter.RenderStatus(_sessions.Status(settings.Registry, settings.Harness, settings.HarnessHome), _catalog, settings.All);
}

/// <summary><c>protostar sessions list</c>: the sessions backed up from every machine.</summary>
internal sealed class SessionsListCommand : AsyncCommand<SessionsListCommand.Settings>
{
    private readonly ISessionSyncService _sessions;
    private readonly IHarnessCatalog _catalog;

    public SessionsListCommand(ISessionSyncService sessions, IHarnessCatalog catalog)
    {
        _sessions = sessions;
        _catalog = catalog;
    }

    public sealed class Settings : SessionsSettings
    {
        [CommandOption("-p|--project <TEXT>")]
        [Description("Only sessions whose working directory contains this text.")]
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
        [Description("Print JSON instead of a table, for agents and scripts.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var query = new TranscriptListQuery(settings.Project, settings.Machine, null, settings.Since, settings.Limit);
        var result = await _sessions.ListAsync(settings.Registry, query, cancellation);
        return SessionsPresenter.RenderList(result, _catalog, settings.Json);
    }
}
