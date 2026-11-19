using System.Text.Json;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Spectre.Console;

namespace Protostar.Cli.Sessions;

/// <summary>The console face of <see cref="ISessionSyncService"/>: progress, sync results, status and listings.</summary>
internal static class SessionsPresenter
{
    private const int MaxRows = 40;

    /// <summary>Runs <paramref name="sync"/> under a live progress bar on a terminal, or with a few plain lines otherwise.</summary>
    /// <remarks>
    /// The bar measures bytes, so it moves within a large transcript, not only between transcripts. Redirected output
    /// (a log, a pipe, an agent's shell) gets one line once the run is planned and one per tenth of the bytes, with no
    /// redraws, so it stays short and readable however large the history is.
    /// </remarks>
    public static async Task<SessionSyncResult> WithProgressAsync(Func<IProgress<SessionSyncProgress>?, Task<SessionSyncResult>> sync)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsOutputRedirected)
            return await sync(new PlainProgress());

        SessionSyncResult? result = null;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(
                new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(),
                new RemainingTimeColumn(), new SpinnerColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask("Looking for sessions to back up");
                result = await sync(new Reporter(p =>
                {
                    task.MaxValue = Math.Max(1, p.BytesTotal);
                    task.Value = p.BytesDone;
                    task.Description = $"Backing up sessions: {p.TranscriptsDone} of {p.TranscriptsTotal} transcripts";
                }));
            });
        return result!;
    }

    // Applies each report as it is made. Progress<T> would post them to the thread pool, where they can arrive out of
    // order, or after the run has ended and its result has been printed.
    private sealed class Reporter(Action<SessionSyncProgress> report) : IProgress<SessionSyncProgress>
    {
        public void Report(SessionSyncProgress value) => report(value);
    }

    // For redirected output: what the run will do, then a line each time another tenth of it is done.
    private sealed class PlainProgress : IProgress<SessionSyncProgress>
    {
        private int _tenths = -1;

        public void Report(SessionSyncProgress p)
        {
            if (p.TranscriptsTotal == 0)
                return;

            if (_tenths < 0)
            {
                _tenths = 0;
                AnsiConsole.WriteLine($"Backing up {Count(p.TranscriptsTotal, "transcript")} ({Bytes(p.BytesTotal)}), newest first.");
                return;
            }

            var tenths = p.BytesTotal > 0 ? (int)(10 * p.BytesDone / p.BytesTotal) : 10 * p.TranscriptsDone / p.TranscriptsTotal;
            // The last bytes can land before the last transcript is finished with; 100% waits for that.
            if (p.TranscriptsDone < p.TranscriptsTotal)
                tenths = Math.Min(tenths, 9);
            if (tenths <= _tenths)
                return;
            _tenths = tenths;
            AnsiConsole.WriteLine(
                $"{tenths * 10}%: {Bytes(p.BytesDone)} of {Bytes(p.BytesTotal)}, {p.TranscriptsDone} of {p.TranscriptsTotal} transcripts done.");
        }
    }

    /// <summary>Renders a sync run and returns the exit code: 0 when everything is backed up, 130 when stopped by Ctrl+C, otherwise 1.</summary>
    public static int RenderSync(SessionSyncResult result, IHarnessCatalog catalog)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } exit)
            return exit;

        var notable = result.Transcripts.Where(t => t.Status != TranscriptSyncStatus.UpToDate).ToList();
        if (notable.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Session");
            table.AddColumn("Kind");
            table.AddColumn(new TableColumn("Sent").RightAligned());
            table.AddColumn("Result");
            // Failed, then partly sent, then skipped: after a large or stopped run they are the rows worth reading.
            foreach (var t in notable.OrderBy(t => t.Status switch
            {
                TranscriptSyncStatus.Failed => 0,
                TranscriptSyncStatus.Partial => 1,
                TranscriptSyncStatus.Skipped => 2,
                _ => 3,
            }).Take(MaxRows))
            {
                table.AddRow(
                    Markup.Escape(Short(t.Transcript.Key)),
                    t.Transcript.Kind == TranscriptKind.Session ? "session" : "[grey]subagent[/]",
                    Bytes(t.BytesUploaded),
                    Describe(t));
            }
            AnsiConsole.Write(table);
            if (notable.Count > MaxRows)
                AnsiConsole.MarkupLine($"[grey]... and {notable.Count - MaxRows} more.[/]");
        }

        var count = result.Transcripts.Count;
        var uploaded = result.Transcripts.Count(t => t.Status == TranscriptSyncStatus.Uploaded);
        var upToDate = result.Transcripts.Count(t => t.Status == TranscriptSyncStatus.UpToDate);
        var unfinished = result.Transcripts.Count(t => t.Status is TranscriptSyncStatus.Partial or TranscriptSyncStatus.Skipped);
        var failed = result.Transcripts.Count(t => t.Status == TranscriptSyncStatus.Failed);
        var sent = result.Transcripts.Sum(t => t.BytesUploaded);
        var authority = Markup.Escape(result.Registry!.GetLeftPart(UriPartial.Authority));

        if (count == 0)
            AnsiConsole.MarkupLine("[grey]No session transcripts found on this machine.[/]");
        else if (uploaded == 0 && unfinished == 0 && failed == 0)
            AnsiConsole.MarkupLine($"All {count} transcripts were already backed up to [grey]{authority}[/].");
        else
            AnsiConsole.MarkupLine(
                $"{count} transcripts: {uploaded} backed up ({Bytes(sent)} sent), {upToDate} already up to date" +
                (unfinished > 0 ? $", [yellow]{unfinished} not finished[/]" : "") +
                (failed > 0 ? $", [red]{failed} failed[/]" : "") + $". Registry: [grey]{authority}[/].");

        if (result.Complete)
        {
            if (uploaded > 0)
                AnsiConsole.MarkupLine("[green]Everything backed up.[/]");
            return 0;
        }

        if (!result.Stopped)
            return 1;
        AnsiConsole.MarkupLine("[yellow]Stopped part way.[/] Nothing is lost: run `protostar sessions push` again to carry on.");
        return 130;
    }

    /// <summary>Renders backup status and returns the exit code: 0 when fully backed up, 1 when anything is pending.</summary>
    /// <remarks>The table lists pending transcripts only, or every transcript when <paramref name="all"/> is set.</remarks>
    public static int RenderStatus(SessionStatusResult result, IHarnessCatalog catalog, bool all = false)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } exit)
            return exit;

        var pending = result.Transcripts.Where(t => t.BytesPending > 0).OrderByDescending(t => t.LastModifiedAt).ToList();
        var rows = all ? [.. result.Transcripts.OrderByDescending(t => t.LastModifiedAt)] : pending;
        if (rows.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Session");
            table.AddColumn("Kind");
            table.AddColumn(new TableColumn("Pending").RightAligned());
            table.AddColumn("Changed");
            table.AddColumn("State");
            foreach (var t in rows.Take(MaxRows))
            {
                var state = t.BytesPending == 0 ? "[green]backed up[/]"
                    : t.Rewritten ? "[yellow]rewritten, re-upload[/]"
                    : t.BytesBackedUp == 0 ? "never backed up"
                    : "new content";
                table.AddRow(
                    Markup.Escape(Short(t.Transcript.Key)),
                    t.Transcript.Kind == TranscriptKind.Session ? "session" : "[grey]subagent[/]",
                    Bytes(t.BytesPending),
                    Markup.Escape(Ago(t.LastModifiedAt)),
                    state);
            }
            AnsiConsole.Write(table);
            if (rows.Count > MaxRows)
                AnsiConsole.MarkupLine($"[grey]... and {rows.Count - MaxRows} more.[/]");
        }

        var total = result.Transcripts.Count;
        var lastSync = result.Transcripts.Max(t => t.LastSyncedAt);
        var sessions = result.Transcripts.Count(t => t.Transcript.Kind == TranscriptKind.Session);
        var summary = $"{Count(total, "transcript")} on this machine ({Count(sessions, "session")}, {Count(total - sessions, "subagent")}). " +
            (pending.Count == 0
                ? "[green]All backed up.[/]"
                : $"[yellow]{pending.Count} pending, {Bytes(pending.Sum(t => t.BytesPending))} not uploaded yet.[/]") +
            (lastSync is { } last ? $" Last backup: {Markup.Escape(Ago(last))}." : " Never backed up.");
        AnsiConsole.MarkupLine(summary);

        return result.FullySynced ? 0 : 1;
    }

    /// <summary>Renders the registry's sessions as a table, or as JSON for agents and scripts.</summary>
    public static int RenderList(SessionListResult result, IHarnessCatalog catalog, bool json)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } exit)
            return exit;

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result.Sessions, TranscriptJson.Options));
            return 0;
        }

        if (result.Sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No sessions backed up yet. Run `protostar sessions push`.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Last active");
        table.AddColumn("Title");
        table.AddColumn("Project");
        table.AddColumn("Machine");
        table.AddColumn("Harness");
        table.AddColumn(new TableColumn("Prompts").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Id");
        foreach (var s in result.Sessions)
        {
            table.AddRow(
                Markup.Escape(s.LastActivityAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"),
                Markup.Escape(Truncate(s.Title ?? "(untitled)", 48)),
                Markup.Escape(s.Project ?? "-") + (s.ProjectSubpath is { } sub ? $" [grey]{Markup.Escape(sub)}[/]" : ""),
                Markup.Escape(s.MachineName ?? "-"),
                Markup.Escape(s.Harness),
                s.UserMessages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Bytes(s.Length),
                Markup.Escape(Short(s.Key)) + (s.SubagentCount > 0 ? $" [grey]+{s.SubagentCount}[/]" : ""));
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private static int? Failure(SessionSyncFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        var authority = registry?.GetLeftPart(UriPartial.Authority) ?? "";
        string? message = failure switch
        {
            SessionSyncFailure.None => null,
            SessionSyncFailure.InvalidRegistry => detail,
            SessionSyncFailure.UnknownHarness => $"Harness '{detail}' does not exist or does not keep sessions. Known: {catalog.KnownIds}",
            SessionSyncFailure.NotLoggedIn => $"Not logged in to {authority}. Run `protostar auth login` first.",
            SessionSyncFailure.SessionExpired => $"Your session for {authority} has expired. Run `protostar auth login` again.",
            SessionSyncFailure.Incompatible => detail,
            _ => $"Could not reach the registry at {authority}." + (detail is null ? "" : $" {detail}"),
        };
        if (message is null)
            return null;
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    internal static string Describe(TranscriptSyncOutcome t) => t.Status switch
    {
        TranscriptSyncStatus.Uploaded => t.Rewritten ? "[green]backed up[/] [grey](file was rewritten; new revision)[/]" : "[green]backed up[/]",
        TranscriptSyncStatus.Partial => $"[yellow]partly backed up[/], {Bytes(t.BytesPending)} to go",
        TranscriptSyncStatus.Skipped => $"[grey]skipped:[/] {Markup.Escape(t.Message ?? "")}",
        TranscriptSyncStatus.Failed => $"[red]failed:[/] {Markup.Escape(t.Message ?? "")}",
        _ => "[grey]up to date[/]",
    };

    internal static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // Session ids are long UUIDs; the first 8 characters identify one in practice, plus the agent for subagents.
    private static string Short(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 ? key[..Math.Min(8, key.Length)] : $"{key[..Math.Min(8, dot)]}.{key[(dot + 1)..]}";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : $"{(int)span.TotalDays} d ago";
    }
}
