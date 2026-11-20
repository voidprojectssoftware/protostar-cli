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
    public static Task<int> RenderAsync(OpenedSession session, IAsyncEnumerable<ShownEntry> entries, bool full, bool json) =>
        RenderAsync(session, entries, full, json, json ? null : ConversationWriter.ForConsole());

    /// <summary>Writes the session's entries through <paramref name="writer"/>, or as JSON Lines when it is <c>null</c>.</summary>
    internal static async Task<int> RenderAsync(OpenedSession session, IAsyncEnumerable<ShownEntry> entries, bool full, bool json, ConversationWriter? writer)
    {
        var harness = session.Detail.Transcript.Harness;
        var view = writer is null ? null : new ConversationView(writer);
        view?.Header(session.Detail.Transcript);
        var reading = entries.GetAsyncEnumerator();
        try
        {
            while (true)
            {
                // What is written so far goes out whenever the next entry is not ready yet, so a slow download still
                // shows as it arrives, while a fast one is written in large pieces.
                var next = reading.MoveNextAsync();
                if (!next.IsCompleted)
                    writer?.Flush();
                if (!await next)
                    break;
                if (view is null)
                    SessionsJson.WriteObject(SessionsJson.ShowRecord(reading.Current, harness, full));
                else
                    view.Write(reading.Current, full);
            }
        }
        catch (SessionReadException ex)
        {
            if (json)
                return SessionsJson.Error(SessionsJson.RegistryUnreachable, ex.Message);
            writer!.Flush();
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/] [grey]What came before it is shown above.[/]");
            return 1;
        }
        finally
        {
            writer?.Flush();
            await reading.DisposeAsync();
        }

        return 0;
    }

    // Writes entries as a conversation; remembers who spoke last so a run of replies and tool calls gets one heading.
    // Each line is written as spans rather than markup, which keeps a transcript of millions of lines fast (see
    // ConversationWriter); the output is what the equivalent Spectre markup renders.
    private sealed class ConversationView
    {
        private static readonly Span Bar = new("│", "grey");
        private static readonly Span Space = new(" ");

        private readonly ConversationWriter _out;
        private string? _speaker;

        public ConversationView(ConversationWriter writer) => _out = writer;

        public void Header(RemoteTranscript t)
        {
            _out.Line(new Span(t.Title ?? "(untitled)", "bold"));
            var facts = new[]
            {
                t.Project, t.MachineName, t.Harness,
                t.StartedAt is { } started ? $"{When(started)} to {When(t.LastActivityAt)}" : null,
                t.Kind == "subagent" ? $"subagent of {t.ParentKey}" : null,
            };
            _out.Line(new Span(string.Join(" · ", facts.OfType<string>()), "grey"));
            _out.Line(new Span(t.Key, "grey"));
        }

        public void Write(ShownEntry shown, bool full)
        {
            var nested = shown.Depth > 0;
            switch (shown.Entry)
            {
                case PromptEntry prompt:
                    // Inside a subagent the prompt is the task its caller gave it, not something the user typed.
                    Speak(nested, new Span(nested ? "Task" : "You", "bold blue"), "you", prompt.At);
                    Lines(nested, "  ", prompt.Text, null);
                    break;
                case ReplyEntry reply:
                    Speak(nested, new Span("Assistant", "bold green"), "assistant", reply.At);
                    Lines(nested, "  ", reply.Text, null);
                    break;
                case ToolCallEntry call:
                    Speak(nested, new Span("Assistant", "bold green"), "assistant", call.At);
                    var what = call.Subagent is { } subagent
                        ? $"subagent{(subagent.AgentType is { } type ? " " + type : "")}: {subagent.Description ?? call.Summary}"
                        : call.Summary;
                    Line(nested, new Span("  "), new Span("▸", "grey"), Space, new Span(call.Name, "bold"), new Span(" " + Truncate(what, SummaryWidth - call.Name.Length)));
                    if (full && call.Input.ValueKind != JsonValueKind.Undefined)
                        Lines(nested, "      ", JsonSerializer.Serialize(call.Input, Indented), "grey");
                    break;
                case ToolResultEntry result:
                    if (result.IsError)
                        Line(nested, new Span("    "), new Span("◂ ", "grey"), new Span("error", "red"));
                    else
                        Line(nested, new Span("    "), new Span("◂ result", "grey"));
                    Lines(nested, "      ", result.Text, result.IsError ? "red" : "grey");
                    break;
                case SubagentStartEntry start:
                    var kind = start.AgentType is { } t ? $" {t}" : "";
                    var about = start.Description is { } d ? $": {d}" : "";
                    if (start.Linked)
                        _out.Line(new Span("┌", "grey"), Space, new Span($"Subagent{kind}", "bold"), new Span(about + " "), new Span(start.SubagentId, "grey"));
                    else
                        _out.Line(new Span("┌", "grey"), Space, new Span($"Subagent{kind}", "bold"), new Span(about + " "), new Span("(not linked to a call)", "grey"), Space, new Span(start.SubagentId, "grey"));
                    _speaker = null;
                    break;
                case SubagentEndEntry:
                    _out.Line(new Span("└", "grey"));
                    _speaker = null;
                    break;
            }
        }

        private void Speak(bool nested, Span heading, string speaker, DateTimeOffset? at)
        {
            if (_speaker == speaker)
                return;
            _speaker = speaker;
            if (nested)
                _out.Line(Bar);
            else
                _out.Line();
            if (at is { } when)
                Line(nested, heading, Space, new Span(when.ToLocalTime().ToString("HH:mm"), "grey"));
            else
                Line(nested, heading);
        }

        // A line, after the subagent bar when it is nested.
        private void Line(bool nested, params ReadOnlySpan<Span> spans)
        {
            if (!nested)
            {
                _out.Line(spans);
                return;
            }

            Span[] barred = [Bar, Space, .. spans];
            _out.Line(barred);
        }

        private void Lines(bool nested, string indent, string text, string? style)
        {
            foreach (var line in text.TrimEnd().Split('\n'))
            {
                var content = line.TrimEnd('\r');
                if (style is null)
                    Line(nested, new Span(indent + content));
                else
                    Line(nested, new Span(indent), new Span(content, style));
            }
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string When(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(Math.Max(1, max - 3))] + "...";
}
