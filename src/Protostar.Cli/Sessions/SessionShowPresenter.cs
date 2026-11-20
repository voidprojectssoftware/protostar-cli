using System.Text.Json;
using System.Text.Json.Nodes;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Spectre.Console;

namespace Protostar.Cli.Sessions;

/// <summary>The console face of <see cref="ISessionReader"/>: a session as a readable conversation, or as JSON Lines.</summary>
/// <remarks>
/// Each entry is written as it is read, so output starts before the download ends and memory stays flat. Prompts and
/// replies are shown in full; a tool call is one line (its tool and what it acted on), and so is a subagent, through
/// the call that started it. With <c>--full</c>, each call's input and result follow it, and each subagent's own
/// conversation is nested, marked with a bar, right after the call that ran it.
/// </remarks>
internal static class SessionShowPresenter
{
    // A collapsed tool call fits on one line of an 80-column terminal, with its indent.
    private const int SummaryWidth = 72;

    /// <summary>Renders a lookup that found nothing to show and returns its exit code; <c>null</c> when a session was found.</summary>
    public static int? RenderOpen(SessionOpenResult result, string id, IHarnessCatalog catalog, bool json)
    {
        switch (result.Outcome)
        {
            case SessionOpenOutcome.Found:
                return null;
            case SessionOpenOutcome.NotFound:
                var notFound = $"No session matches '{id}'. Run `protostar sessions list` to see your sessions and their ids.";
                if (json)
                    return SessionsJson.Error(SessionsJson.NotFound, notFound);
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(notFound)}[/]");
                return 1;
            case SessionOpenOutcome.Ambiguous:
                var ambiguous = $"'{id}' matches {SessionsPresenter.Count(result.Candidates.Count, "session")}" +
                    (result.Candidates.Count >= SessionReader.MaxCandidates ? " or more" : "") + ". Give more of the id.";
                if (json)
                    return SessionsJson.Error(SessionsJson.AmbiguousId, ambiguous, extra: new JsonObject { ["candidates"] = new JsonArray([.. result.Candidates.Select(SessionsJson.Candidate)]) });
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ambiguous)}[/]");
                foreach (var c in result.Candidates)
                    AnsiConsole.MarkupLine($"  {Markup.Escape(c.Key)}  [grey]{Markup.Escape(When(c.LastActivityAt))}[/]  {Markup.Escape(Truncate(c.Title ?? "(untitled)", 48))}");
                return 1;
            default:
                var message = SessionsPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog)!;
                if (json)
                    return SessionsJson.Error(SessionsJson.Code(result.Failure)!, message);
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
                return 1;
        }
    }

    /// <summary>Writes the session's entries as they are read and returns the exit code: 0, or 1 when the download failed part way.</summary>
    public static async Task<int> RenderAsync(OpenedSession session, IAsyncEnumerable<ShownEntry> entries, bool full, bool json)
    {
        var harness = session.Detail.Transcript.Harness;
        var view = new ConversationView();
        if (!json)
            Header(session.Detail.Transcript);
        try
        {
            await foreach (var shown in entries)
            {
                if (json)
                    SessionsJson.WriteObject(SessionsJson.ShowRecord(shown, harness, full));
                else
                    view.Write(shown, full);
            }
        }
        catch (SessionReadException ex)
        {
            if (json)
                return SessionsJson.Error(SessionsJson.RegistryUnreachable, ex.Message);
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/] [grey]What came before it is shown above.[/]");
            return 1;
        }

        return 0;
    }

    private static void Header(RemoteTranscript t)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(t.Title ?? "(untitled)")}[/]");
        var facts = new[]
        {
            t.Project, t.MachineName, t.Harness,
            t.StartedAt is { } started ? $"{When(started)} to {When(t.LastActivityAt)}" : null,
            t.Kind == "subagent" ? $"subagent of {t.ParentKey}" : null,
        };
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(string.Join(" · ", facts.OfType<string>()))}[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(t.Key)}[/]");
    }

    // Writes entries as a conversation; remembers who spoke last so a run of replies and tool calls gets one heading.
    private sealed class ConversationView
    {
        private string? _speaker;

        public void Write(ShownEntry shown, bool full)
        {
            var bar = shown.Depth > 0 ? "[grey]│[/] " : "";
            switch (shown.Entry)
            {
                case PromptEntry prompt:
                    // Inside a subagent the prompt is the task its caller gave it, not something the user typed.
                    Speak(bar, shown.Depth > 0 ? "[bold blue]Task[/]" : "[bold blue]You[/]", "you", prompt.At);
                    Lines(bar, "  ", prompt.Text, null);
                    break;
                case ReplyEntry reply:
                    Speak(bar, "[bold green]Assistant[/]", "assistant", reply.At);
                    Lines(bar, "  ", reply.Text, null);
                    break;
                case ToolCallEntry call:
                    Speak(bar, "[bold green]Assistant[/]", "assistant", call.At);
                    var what = call.Subagent is { } subagent
                        ? $"subagent{(subagent.AgentType is { } type ? " " + type : "")}: {subagent.Description ?? call.Summary}"
                        : call.Summary;
                    AnsiConsole.MarkupLine($"{bar}  [grey]▸[/] [bold]{Markup.Escape(call.Name)}[/] {Markup.Escape(Truncate(what, SummaryWidth - call.Name.Length))}");
                    if (full && call.Input.ValueKind != JsonValueKind.Undefined)
                        Lines(bar, "      ", JsonSerializer.Serialize(call.Input, Indented), "grey");
                    break;
                case ToolResultEntry result:
                    AnsiConsole.MarkupLine($"{bar}    [grey]◂ {(result.IsError ? "[red]error[/]" : "result")}[/]");
                    Lines(bar, "      ", result.Text, result.IsError ? "red" : "grey");
                    break;
                case SubagentStartEntry start:
                    var kind = start.AgentType is { } t ? $" {t}" : "";
                    var about = start.Description is { } d ? $": {d}" : "";
                    var where = start.Linked ? "" : " [grey](not linked to a call)[/]";
                    AnsiConsole.MarkupLine($"[grey]┌[/] [bold]Subagent{Markup.Escape(kind)}[/]{Markup.Escape(about)}{where} [grey]{Markup.Escape(start.SubagentId)}[/]");
                    _speaker = null;
                    break;
                case SubagentEndEntry:
                    AnsiConsole.MarkupLine("[grey]└[/]");
                    _speaker = null;
                    break;
            }
        }

        private void Speak(string bar, string heading, string speaker, DateTimeOffset? at)
        {
            if (_speaker == speaker)
                return;
            _speaker = speaker;
            AnsiConsole.MarkupLine(bar.Length > 0 ? bar.TrimEnd() : "");
            AnsiConsole.MarkupLine($"{bar}{heading}" + (at is { } when ? $" [grey]{Markup.Escape(when.ToLocalTime().ToString("HH:mm"))}[/]" : ""));
        }

        private static void Lines(string bar, string indent, string text, string? style)
        {
            foreach (var line in text.TrimEnd().Split('\n'))
            {
                var escaped = Markup.Escape(line.TrimEnd('\r'));
                AnsiConsole.MarkupLine(style is null ? $"{bar}{indent}{escaped}" : $"{bar}{indent}[{style}]{escaped}[/]");
            }
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string When(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(Math.Max(1, max - 3))] + "...";
}
