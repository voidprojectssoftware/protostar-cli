using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Spectre.Console;

namespace Protostar.Cli.Search;

/// <summary>The console face of <see cref="ISearchService"/>: a compact list of hits, or JSON for agents and scripts.</summary>
internal static partial class SearchPresenter
{
    private const int KindWidth = 8;
    private const string Ellipsis = "...";

    /// <summary>Marks the start of a match in a registry snippet.</summary>
    internal const char MatchStart = '«';

    /// <summary>Marks the end of a match in a registry snippet.</summary>
    internal const char MatchEnd = '»';

    /// <summary>Renders the hits and returns the exit code: 0 for hits or none, 1 when the search could not run.</summary>
    /// <remarks>
    /// Each hit is its kind beside three lines: its title (or memory path); its project, machine, date and, for a
    /// session, the short id <c>sessions show</c> takes; then the snippet with the matches highlighted, cut to about two
    /// lines of the terminal. With <paramref name="json"/>, the hits are one array
    /// of <see cref="Record"/>s, and a failure is the error object on standard error.
    /// </remarks>
    public static int Render(SearchResult result, SearchRequest request, IHarnessCatalog catalog, bool json)
    {
        if (SessionsPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog) is { } message)
        {
            if (json)
                return SessionsJson.Error(SessionsJson.Code(result.Failure)!, message);
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            return 1;
        }

        if (json)
        {
            SessionsJson.WriteArray(result.Hits.Select(Record));
            return 0;
        }

        if (result.FolderProject is { } folder)
            AnsiConsole.MarkupLine($"[grey]In {Markup.Escape(folder)}, this folder's project.[/]");

        if (result.Hits.Count == 0)
        {
            AnsiConsole.WriteLine($"No matches for '{request.Text}'.");
            return 0;
        }

        // A grid keeps wrapped lines in the second column, under the title rather than under the kind.
        var room = Math.Max(30, AnsiConsole.Profile.Width - KindWidth - 1);
        var grid = new Grid().AddColumn(new GridColumn().Width(KindWidth).NoWrap().PadRight(1)).AddColumn(new GridColumn().PadRight(0));
        foreach (var hit in result.Hits)
        {
            if (grid.Rows.Count > 0)
                grid.AddEmptyRow();
            grid.AddRow(
                new Markup($"[bold]{Kind(hit)}[/]"),
                new Rows(
                    new Markup(Markup.Escape(SessionsPresenter.Truncate(Title(hit), room))),
                    new Markup($"[grey]{Markup.Escape(SessionsPresenter.Truncate(Meta(hit), room))}[/]"),
                    new Markup(Highlight(Segments(hit.Snippet, 2 * room)))));
        }
        AnsiConsole.Write(grid);
        return 0;
    }

    /// <summary>A hit as <c>search --json</c> prints it.</summary>
    /// <remarks>
    /// <c>id</c> is a session's harness key, the id <c>sessions show</c> takes; memory has none, and is named by
    /// <c>projectId</c> and <c>path</c>; an agent is named by <c>name</c> and <c>projectId</c>, with its <c>version</c> and
    /// <c>description</c>, and has no machine. Fields a hit does not have are left out. The snippet keeps the registry's
    /// « and » around each match.
    /// </remarks>
    public static JsonNode Record(SearchHit hit) => JsonSerializer.SerializeToNode(
        new HitRow(
            hit.Kind, hit.Key, hit.Harness, hit.ParentKey, hit.Title, hit.Path, hit.Name, hit.Version, hit.Description, hit.ProjectId,
            hit.ProjectName, hit.MachineId, hit.MachineName, hit.Date, hit.Snippet),
        TranscriptJson.Options)!;

    private sealed record HitRow(
        string Kind, string? Id, string? Harness, string? ParentId, string? Title, string? Path, string? Name, int? Version,
        string? Description, Guid? ProjectId, string? Project, Guid? MachineId, string? MachineName, DateTimeOffset? Date, string Snippet);

    /// <summary>The snippet as runs of plain and matched text, its whitespace collapsed and cut to <paramref name="max"/> characters.</summary>
    /// <remarks>
    /// The markers are removed, so they never count against the room or reach the screen. A cut ends with "...",
    /// inside a match when it falls there. An unclosed « marks the rest as matched, and a stray » is dropped.
    /// </remarks>
    internal static IReadOnlyList<(string Text, bool Match)> Segments(string snippet, int max)
    {
        var runs = new List<(string Text, bool Match)>();
        var current = new StringBuilder();
        var match = false;
        var used = 0;
        var room = Math.Max(Ellipsis.Length + 1, max);
        var text = Whitespace().Replace(snippet, " ").Trim();

        void Flush()
        {
            if (current.Length > 0)
                runs.Add((current.ToString(), match));
            current.Clear();
        }

        var visible = text.Count(c => c is not (MatchStart or MatchEnd));
        var limit = visible <= room ? visible : room - Ellipsis.Length;
        foreach (var c in text)
        {
            if (c is MatchStart or MatchEnd)
            {
                Flush();
                match = c == MatchStart;
                continue;
            }
            if (used == limit)
                break;
            current.Append(c);
            used++;
        }
        if (visible > limit)
            current.Append(Ellipsis);
        Flush();
        return runs;
    }

    // The runs as markup, matches highlighted.
    private static string Highlight(IEnumerable<(string Text, bool Match)> runs) =>
        string.Concat(runs.Select(r => r.Match ? $"[bold yellow]{Markup.Escape(r.Text)}[/]" : Markup.Escape(r.Text)));

    private static string Kind(SearchHit hit) => hit.Kind switch
    {
        "memory" or SearchService.AgentKind => hit.Kind,
        _ => hit.ParentKey is null ? "session" : "subagent",
    };

    private static string Title(SearchHit hit) => hit.Kind switch
    {
        "memory" => hit.Path ?? "(unnamed)",
        SearchService.AgentKind => $"{hit.Name} v{hit.Version}" + (hit.Description is { Length: > 0 } d ? $": {d}" : ""),
        _ => hit.Title ?? "(untitled)",
    };

    // "project · machine · 2026-11-20 10:00 · 7d3c9a52": the session's short id last, as sessions show takes it.
    private static string Meta(SearchHit hit)
    {
        var parts = hit.Kind == SearchService.AgentKind
            ? new List<string> { hit.ProjectName ?? "personal" }
            : new List<string> { hit.ProjectName ?? "(no project)", hit.MachineName ?? "(unknown machine)" };
        if (hit.Date is { } date)
            parts.Add(date.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (hit.Key is { } key)
            parts.Add(SessionsPresenter.Short(key));
        return string.Join(" · ", parts);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
