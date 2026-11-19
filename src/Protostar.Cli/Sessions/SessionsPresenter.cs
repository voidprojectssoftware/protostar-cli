using System.Text.Json;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Spectre.Console;

namespace Protostar.Cli.Sessions;

/// <summary>The console face of <see cref="ISessionSyncService"/>: progress, sync results, status and listings.</summary>
internal static class SessionsPresenter
{
    private const int MaxRows = 40;

    /// <summary>Runs <paramref name="sync"/> under a progress bar when attached to a terminal, plainly otherwise.</summary>
    public static async Task<SessionSyncResult> WithProgressAsync(Func<IProgress<SessionSyncProgress>?, Task<SessionSyncResult>> sync)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsOutputRedirected)
            return await sync(null);

        SessionSyncResult? result = null;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask("Backing up sessions");
                var progress = new Progress<SessionSyncProgress>(p =>
                {
                    task.MaxValue = Math.Max(1, p.TranscriptsTotal);
                    task.Value = p.TranscriptsDone;
                    task.Description = $"Backing up sessions ({Bytes(p.BytesUploaded)} sent)";
                });
                result = await sync(progress);
            });
        return result!;
    }

    /// <summary>Renders a sync run and returns the exit code: 0 when everything is backed up, otherwise 1.</summary>
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
            foreach (var t in notable.OrderBy(t => t.Status).Take(MaxRows))
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

        return result.Complete ? 0 : 1;
    }

    /// <summary>Renders backup status and returns the exit code: 0 when fully backed up, 1 when anything is pending.</summary>
    public static int RenderStatus(SessionStatusResult result, IHarnessCatalog catalog)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } exit)
            return exit;

        var pending = result.Transcripts.Where(t => t.BytesPending > 0).OrderByDescending(t => t.LastModifiedAt).ToList();
        if (pending.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Session");
            table.AddColumn("Kind");
            table.AddColumn(new TableColumn("Pending").RightAligned());
            table.AddColumn("Changed");
            table.AddColumn("State");
            foreach (var t in pending.Take(MaxRows))
            {
                var state = t.Rewritten ? "[yellow]rewritten, re-upload[/]" : t.BytesBackedUp == 0 ? "never backed up" : "new content";
                table.AddRow(
                    Markup.Escape(Short(t.Transcript.Key)),
                    t.Transcript.Kind == TranscriptKind.Session ? "session" : "[grey]subagent[/]",
                    Bytes(t.BytesPending),
                    Markup.Escape(Ago(t.LastModifiedAt)),
                    state);
            }
            AnsiConsole.Write(table);
            if (pending.Count > MaxRows)
                AnsiConsole.MarkupLine($"[grey]... and {pending.Count - MaxRows} more.[/]");
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
                Markup.Escape(s.Project ?? "-"),
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
