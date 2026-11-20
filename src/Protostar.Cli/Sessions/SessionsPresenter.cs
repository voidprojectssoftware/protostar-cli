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
                    task.Description = $"Backing up sessions: {Finished(p)}";
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

            var backedUp = p.TranscriptsDone - p.TranscriptsUnfinished;
            var tenths = p.BytesTotal > 0 ? (int)(10 * p.BytesDone / p.BytesTotal) : 10 * backedUp / p.TranscriptsTotal;
            // The last bytes can land before the last transcript is finished with, and a skipped or failed transcript
            // is finished with but not backed up: 100% waits until every transcript is both.
            if (backedUp < p.TranscriptsTotal)
                tenths = Math.Min(tenths, 9);
            if (tenths <= _tenths)
                return;
            _tenths = tenths;
            AnsiConsole.WriteLine($"{tenths * 10}%: {Bytes(p.BytesDone)} of {Bytes(p.BytesTotal)}, {Finished(p)}.");
        }
    }

    // "3 of 5 transcripts done", plus how many of those were not backed up, so a failure never reads as done.
    private static string Finished(SessionSyncProgress p) =>
        $"{p.TranscriptsDone - p.TranscriptsUnfinished} of {p.TranscriptsTotal} transcripts done" +
        (p.TranscriptsUnfinished > 0 ? $", {p.TranscriptsUnfinished} not finished" : "");

    /// <summary>Renders a sync run and returns the exit code: 0 when everything is backed up, 130 when stopped by Ctrl+C, otherwise 1.</summary>
    /// <remarks>
    /// With <paramref name="json"/>, every transcript's <see cref="SessionsJson.PushRecord"/> is printed instead of the
    /// table and summary, and a run that was stopped or left anything unfinished also writes an error object
    /// (<see cref="SessionsJson.Interrupted"/>, <see cref="SessionsJson.Incomplete"/>). The exit codes are the same.
    /// </remarks>
    public static int RenderSync(SessionSyncResult result, IHarnessCatalog catalog, bool json = false)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog, json) is { } exit)
            return exit;

        if (json)
        {
            SessionsJson.WriteArray(result.Transcripts.Select(SessionsJson.PushRecord));
            return result.Complete ? 0
                : result.Stopped ? SessionsJson.Error(SessionsJson.Interrupted, "Stopped part way. Nothing is lost: run `protostar sessions push` again to carry on.", 130)
                : SessionsJson.Error(SessionsJson.Incomplete, "Some transcripts were not backed up; see each record's result and message.");
        }

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

        if (VerifySummary(result.Transcripts.Select(t => t.Verified)) is { } verifiedLine)
            AnsiConsole.MarkupLine(verifiedLine);

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
    /// <remarks>
    /// The table lists pending transcripts only, or every transcript when <paramref name="all"/> is set. With
    /// <paramref name="json"/>, the same rows are printed as <see cref="SessionsJson.StatusRecord"/>s instead of the
    /// table and summary, and the exit code is the same.
    /// </remarks>
    public static int RenderStatus(SessionStatusResult result, IHarnessCatalog catalog, bool all = false, bool json = false)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog, json) is { } exit)
            return exit;

        var pending = result.Transcripts.Where(t => t.BytesPending > 0).OrderByDescending(t => t.LastModifiedAt).ToList();
        // A transcript the registry holds differently needs the user's attention though nothing is pending to upload.
        var rows = all
            ? [.. result.Transcripts.OrderByDescending(t => t.LastModifiedAt)]
            : result.Transcripts
                .Where(t => t.BytesPending > 0 || t.Verified?.Check == BackupCheck.Diverged)
                .OrderByDescending(t => t.LastModifiedAt)
                .ToList();
        if (json)
        {
            SessionsJson.WriteArray(rows.Select(SessionsJson.StatusRecord));
            return result.FullySynced ? 0 : 1;
        }

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
                var state = State(t) switch
                {
                    TranscriptState.Diverged => "[yellow]registry differs, push leaves it alone[/]",
                    TranscriptState.BackedUp => t.Verified?.Check == BackupCheck.Confirmed ? "[green]backed up, verified[/]" : "[green]backed up[/]",
                    TranscriptState.BackupFailed => "[yellow]failed to back up, will retry[/]",
                    TranscriptState.Rewritten => "[yellow]rewritten, re-upload[/]",
                    TranscriptState.Missing => "[red]missing from the registry[/]",
                    TranscriptState.Short => $"[yellow]registry holds only {Bytes(t.BytesBackedUp)}[/]",
                    TranscriptState.NeverBackedUp => "never backed up",
                    _ => "new content",
                };
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
        if (VerifySummary(result.Transcripts.Select(t => t.Verified)) is { } verifiedLine)
            AnsiConsole.MarkupLine(verifiedLine);

        return result.FullySynced ? 0 : 1;
    }

    /// <summary>Renders the sessions as a table, or as JSON for agents and scripts, and returns the exit code.</summary>
    /// <remarks>
    /// When the registry could not be asked, this machine's pending sessions are still listed, followed by why the
    /// rest are missing, and the exit code is 1. In JSON each row is a <see cref="SessionsJson.ListRecord"/> (a row
    /// made from the file has no title and zero counts, and its <c>pending</c> says <c>fromFile</c>), and the failure
    /// goes to standard error as the error object.
    /// </remarks>
    public static int RenderList(SessionListResult result, IHarnessCatalog catalog, TranscriptListQuery query, bool json)
    {
        var partial = result.Failure is not (SessionSyncFailure.None or SessionSyncFailure.InvalidRegistry or SessionSyncFailure.UnknownHarness);
        if (!partial && Failure(result.Failure, result.Detail, result.Registry, catalog, json) is { } exit)
            return exit;

        if (json)
        {
            SessionsJson.WriteArray(result.Sessions.Select(SessionsJson.ListRecord));
            return partial ? JsonFailure(result.Failure, result.Detail, result.Registry, catalog) : 0;
        }

        // With a filter, an empty list says nothing about whether anything is backed up, only that nothing matched.
        var filters = Filters(query);
        if (result.Sessions.Count == 0 && !partial)
        {
            AnsiConsole.MarkupLine(filters is null
                ? "[grey]No sessions backed up yet. Run `protostar sessions push`.[/]"
                : $"[grey]No sessions match {Markup.Escape(filters)}. Leave the filter out to see every session.[/]");
            return 0;
        }

        if (result.Sessions.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Last active");
            table.AddColumn("Title");
            table.AddColumn("Project");
            table.AddColumn("Machine");
            table.AddColumn("Harness");
            table.AddColumn(new TableColumn("Prompts").RightAligned());
            table.AddColumn(new TableColumn("Size").RightAligned());
            table.AddColumn("Backup");
            table.AddColumn("Id");
            foreach (var row in result.Sessions)
            {
                var s = row.Session;
                var fromFile = row.Pending is { FromFile: true };
                table.AddRow(
                    Markup.Escape(row.When?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"),
                    // Only the registry knows a title; for a session it has none of, the file says where it is.
                    fromFile ? $"[grey]{Markup.Escape(TruncateStart(row.Pending!.Path, 48))}[/]"
                        : Markup.Escape(Truncate(s.Title ?? "(untitled)", 48)),
                    Markup.Escape(s.Project ?? "-") + (s.ProjectSubpath is { } sub ? $" [grey]{Markup.Escape(sub)}[/]" : ""),
                    Markup.Escape(s.MachineName ?? "-"),
                    Markup.Escape(s.Harness),
                    fromFile ? "-" : s.UserMessages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Bytes(s.Length),
                    row.Pending switch
                    {
                        null => "[green]backed up[/]",
                        { InRegistry: true } p => $"[yellow]{Bytes(p.Bytes)} pending[/]",
                        _ => "[yellow]not backed up[/]",
                    },
                    Markup.Escape(Short(s.Key)) + (s.SubagentCount > 0 ? $" [grey]+{s.SubagentCount}[/]" : ""));
            }
            AnsiConsole.Write(table);
        }

        var pending = result.Sessions.Count(r => r.Pending is not null);
        if (pending > 0)
            AnsiConsole.MarkupLine($"[yellow]{Count(pending, "session")} on this machine not fully backed up.[/] Run `protostar sessions push`.");

        if (!partial)
            return 0;
        AnsiConsole.MarkupLine(
            $"[red]{Markup.Escape(FailureMessage(result.Failure, result.Detail, result.Registry, catalog)!)}[/] " +
            (result.Sessions.Count == 0
                ? filters is null
                    ? "[grey]No pending sessions on this machine to show.[/]"
                    : $"[grey]No pending sessions on this machine match {Markup.Escape(filters)}.[/]"
                : "[grey]Showing only sessions on this machine that are not backed up yet.[/]"));
        return 1;
    }

    // The filters given, as the options that set them ("--project foo, --since 2026-11-01"), or null for none.
    private static string? Filters(TranscriptListQuery query)
    {
        var given = new List<string>();
        if (query.Project is { } project)
            given.Add($"--project {project}");
        if (query.Machine is { } machine)
            given.Add($"--machine {machine}");
        if (query.Harness is { } harness)
            given.Add($"--harness {harness}");
        if (query.Since is { } since)
            given.Add($"--since {since:yyyy-MM-dd HH:mm}");
        return given.Count == 0 ? null : string.Join(", ", given);
    }

    // Prints why the command stopped early, as markup or as the JSON error object, and returns 1; null when it went ahead.
    private static int? Failure(SessionSyncFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog, bool json = false)
    {
        if (FailureMessage(failure, detail, registry, catalog) is not { } message)
            return null;
        if (json)
            return SessionsJson.Error(SessionsJson.Code(failure)!, message);
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    private static int JsonFailure(SessionSyncFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog) =>
        SessionsJson.Error(SessionsJson.Code(failure)!, FailureMessage(failure, detail, registry, catalog)!);

    /// <summary>What a transcript's backup state is, as the status table words it and its JSON names it.</summary>
    /// <remarks>Checked in this order, so a verify's finding outranks what the ledger alone says.</remarks>
    internal static TranscriptState State(TranscriptStatus t) =>
        t.Verified?.Check == BackupCheck.Diverged ? TranscriptState.Diverged
        : t.BytesPending == 0 ? TranscriptState.BackedUp
        : t.BackupFailedAt is not null ? TranscriptState.BackupFailed
        : t.Rewritten ? TranscriptState.Rewritten
        : t.Verified?.Check == BackupCheck.Missing ? TranscriptState.Missing
        : t.Verified?.Check == BackupCheck.Short ? TranscriptState.Short
        : t.BytesBackedUp == 0 ? TranscriptState.NeverBackedUp
        : TranscriptState.NewContent;

    /// <summary>Why a sync or status stopped early, as plain text; <c>null</c> when it went ahead. Also used by <c>setup</c>.</summary>
    internal static string? FailureMessage(SessionSyncFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        var authority = registry?.GetLeftPart(UriPartial.Authority) ?? "";
        return failure switch
        {
            SessionSyncFailure.None => null,
            SessionSyncFailure.InvalidRegistry => detail,
            SessionSyncFailure.UnknownHarness => $"Harness '{detail}' does not exist or does not keep sessions. Known: {catalog.KnownIds}",
            SessionSyncFailure.NotLoggedIn => $"Not logged in to {authority}. Run `protostar auth login` first.",
            SessionSyncFailure.SessionExpired => $"Your session for {authority} has expired. Run `protostar auth login` again.",
            SessionSyncFailure.Incompatible => detail,
            _ => $"Could not reach the registry at {authority}." + (detail is null ? "" : $" {detail}"),
        };
    }

    // One line on what a verify found, or null when nothing was verified.
    private static string? VerifySummary(IEnumerable<VerifiedBackup?> checks)
    {
        var found = checks.OfType<VerifiedBackup>().ToList();
        if (found.Count == 0)
            return null;

        int Of(BackupCheck check) => found.Count(c => c.Check == check);
        var (missing, shorter, diverged) = (Of(BackupCheck.Missing), Of(BackupCheck.Short), Of(BackupCheck.Diverged));
        return $"Verified {Count(found.Count, "transcript")} against the registry: {Of(BackupCheck.Confirmed)} confirmed" +
            (missing > 0 ? $", [red]{missing} missing[/]" : ", 0 missing") +
            (shorter > 0 ? $", [yellow]{shorter} short[/]" : ", 0 short") +
            (diverged > 0 ? $", [yellow]{diverged} differ (left alone)[/]" : "") + ".";
    }

    internal static string Describe(TranscriptSyncOutcome t) => t.Status switch
    {
        TranscriptSyncStatus.Uploaded when t.Verified?.Check == BackupCheck.Missing => "[green]restored[/] [grey](the registry had lost it)[/]",
        TranscriptSyncStatus.Uploaded when t.Verified?.Check == BackupCheck.Short =>
            $"[green]restored[/] [grey](resumed from the registry's {Bytes(t.Verified.Length ?? 0)})[/]",
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
    internal static string Short(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 ? key[..Math.Min(8, key.Length)] : $"{key[..Math.Min(8, dot)]}.{key[(dot + 1)..]}";
    }

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    // Keeps the end, where a path's file name is.
    private static string TruncateStart(string text, int max) => text.Length <= max ? text : "..." + text[^(max - 3)..];

    internal static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : $"{(int)span.TotalDays} d ago";
    }
}

/// <summary>A transcript's backup state on this machine, as <c>sessions status</c> reports it.</summary>
internal enum TranscriptState
{
    /// <summary>The registry has all of it.</summary>
    BackedUp,

    /// <summary>Never sent.</summary>
    NeverBackedUp,

    /// <summary>Partly sent; the file has grown since.</summary>
    NewContent,

    /// <summary>The file was rewritten rather than appended, so it goes again in full as a new revision.</summary>
    Rewritten,

    /// <summary>A capture hook's background backup of its session failed; a later hook retries it.</summary>
    BackupFailed,

    /// <summary>A verify found the registry holds none of it.</summary>
    Missing,

    /// <summary>A verify found the registry holds fewer bytes than this machine sent.</summary>
    Short,

    /// <summary>A verify found the registry holds more, or another revision; push leaves it alone.</summary>
    Diverged,
}
