using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Spectre.Console;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>How <c>sessions show</c> draws a conversation: what <see cref="ConversationWriter"/> writes, and how fast.</summary>
public sealed class SessionShowRenderTests
{
    private static readonly ClaudeCodeHarness Harness = new(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

    private static IAnsiConsole Terminal(TextWriter output, int width)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.EightBit, Out = new AnsiConsoleOutput(output),
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = width;
        return console;
    }

    [Fact]
    public void A_styled_line_is_written_as_Spectre_renders_its_markup_wrapping_included()
    {
        // Words, runs of spaces long and short, words wider than the line, markup characters, the conversation's own
        // box characters, and (handed to Spectre) characters this does not measure.
        string[] pieces = ["a", "bc", "def", " ", " ", "  ", "[", "]", "/", "│", "▸", "◂", "┌", "└", "·", "é", "\t", "界"];
        string?[] styles = [null, null, "grey", "red", "bold", "bold blue"];
        var random = new Random(112);
        var differ = new List<string>();
        for (var i = 0; i < 3000; i++)
        {
            var width = random.Next(5, 60);
            var spans = new Span[random.Next(1, 5)];
            for (var s = 0; s < spans.Length; s++)
            {
                var text = new StringBuilder();
                for (var n = random.Next(0, 40); n > 0; n--)
                    text.Append(random.Next(12) == 0 ? new string(random.Next(2) == 0 ? ' ' : 'w', random.Next(1, 90)) : pieces[random.Next(pieces.Length - (i % 7 == 0 ? 0 : 3))]);
                spans[s] = new Span(text.ToString(), styles[random.Next(styles.Length)]);
            }

            var spectre = new StringWriter();
            Terminal(spectre, width).MarkupLine(string.Concat(spans.Select(s => s.Style is null ? Markup.Escape(s.Text) : $"[{s.Style}]{Markup.Escape(s.Text)}[/]")));
            var ours = new StringWriter();
            var writer = new ConversationWriter(Terminal(ours, width), ours, plain: false);
            writer.Line(spans);
            writer.Flush();

            // Spectre drops the newline, or adds a blank line, after a line ending in spaces it wrapped away; ours ends in one.
            var expected = spectre.ToString().TrimEnd('\n', '\r') + Environment.NewLine;
            if (ours.ToString() != expected)
                differ.Add($"width {width}: {string.Join(" | ", spans.Select(s => $"{s.Style}:'{s.Text}'"))}");
        }

        Assert.Empty(differ);
    }

    [Fact]
    public void A_plain_line_has_no_escape_codes_and_is_not_wrapped()
    {
        var output = new StringWriter();
        var writer = new ConversationWriter(Terminal(new StringWriter(), 10), output, plain: true);

        writer.Line(new Span("│", "grey"), new Span(" "), new Span("a line much longer than ten columns", "bold"));
        writer.Flush();

        Assert.Equal("│ a line much longer than ten columns" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task A_session_written_plain_reads_as_the_conversation()
    {
        var output = new StringWriter();
        var writer = new ConversationWriter(Terminal(new StringWriter(), 80), output, plain: true);
        ShownEntry[] entries =
        [
            new("s", 0, new PromptEntry(null, false, "Summarise the [README]")),
            new("s", 0, new ReplyEntry(null, false, "Asking a helper.\r\nIt reads it.")),
            new("s", 0, new ToolCallEntry(null, false, "t1", "Task", "Summarise", Json("""{"description":"Summarise"}"""), new SubagentCall("general-purpose", "Summarise"))),
            new("s", 0, new ToolResultEntry(null, false, "t1", "A demo.", false, "a1")),
            new("s.agent-a1", 1, new SubagentStartEntry("s.agent-a1", "general-purpose", "Summarise", Linked: true)),
            new("s.agent-a1", 1, new PromptEntry(null, true, "Summarise")),
            new("s.agent-a1", 1, new ToolCallEntry(null, true, "t2", "Read", "README.md", Json("""{"file_path":"README.md"}"""), null)),
            new("s.agent-a1", 1, new ToolResultEntry(null, true, "t2", "No such file", true, null)),
            new("s.agent-a1", 1, new SubagentEndEntry("s.agent-a1")),
            new("s", 0, new ReplyEntry(null, false, "It is a demo.")),
        ];

        var exit = await SessionShowPresenter.RenderAsync(Session(), entries.ToAsyncEnumerable(), full: true, json: false, writer);

        Assert.Equal(0, exit);
        Assert.Equal(
            """
            Demo
            protostar · vm · claude-code
            s

            You
              Summarise the [README]

            Assistant
              Asking a helper.
              It reads it.
              ▸ Task subagent general-purpose: Summarise
                  {
                    "description": "Summarise"
                  }
                ◂ result
                  A demo.
            ┌ Subagent general-purpose: Summarise s.agent-a1
            │
            │ Task
            │   Summarise
            │
            │ Assistant
            │   ▸ Read README.md
            │       {
            │         "file_path": "README.md"
            │       }
            │     ◂ error
            │       No such file
            └

            Assistant
              It is a demo.

            """.ReplaceLineEndings(),
            output.ToString());
    }

    /// <summary>A regression guard for #112: drawing a long session as text costs a small multiple of parsing it.</summary>
    /// <remarks>
    /// The bound is a ratio to the time the harness takes to parse the same lines, measured in the same process, so it
    /// holds on a slow CI runner. In a debug build, drawing through Spectre markup line by line took about 35 times the
    /// parse, and this takes about 3; a bound of 10 leaves room for noise either way.
    /// </remarks>
    [Fact]
    public async Task Drawing_a_long_session_costs_a_small_multiple_of_parsing_it()
    {
        var lines = Transcript(5 * 1024 * 1024);
        Parse(lines.Take(200));
        await Draw(Parse(lines.Take(200)));

        var parse = Stopwatch.StartNew();
        var entries = Parse(lines);
        parse.Stop();
        var draw = Stopwatch.StartNew();
        await Draw(entries);
        draw.Stop();

        Assert.True(draw.Elapsed < parse.Elapsed * 10 + TimeSpan.FromMilliseconds(200),
            $"drawing took {draw.ElapsedMilliseconds} ms against {parse.ElapsedMilliseconds} ms to parse");
    }

    private static List<ShownEntry> Parse(IEnumerable<string> lines) =>
        [.. lines.SelectMany(Harness.ReadConversationLine).Select(e => new ShownEntry("s", 0, e))];

    private static Task<int> Draw(List<ShownEntry> entries) =>
        SessionShowPresenter.RenderAsync(Session(), entries.ToAsyncEnumerable(), full: true, json: false,
            new ConversationWriter(Terminal(TextWriter.Null, 120), TextWriter.Null, plain: false));

    // A synthetic Claude Code transcript of about the given size: prompts, replies, calls and results of many lines.
    private static List<string> Transcript(int bytes)
    {
        string[] words = ["the", "build", "test", "[red]", "[/]", "render", "markup", "console", "wrap", "line", "value", "path", "json"];
        var random = new Random(112);
        string Text(int lines, int width) =>
            string.Join("\n", Enumerable.Range(0, lines).Select(_ => string.Join(" ", Enumerable.Range(0, width).Select(_ => words[random.Next(words.Length)]))));
        var result = new List<string>();
        var size = 0;
        for (var i = 0; size < bytes; i++)
        {
            string[] records =
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = Text(1, 30) } }),
                JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = Text(6, 14) }, new { type = "tool_use", id = $"t{i}", name = "Bash", input = new { command = "ls" } } } } }),
                JsonSerializer.Serialize(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = $"t{i}", content = Text(80, 12) } } } }),
            ];
            result.AddRange(records);
            size += records.Sum(r => r.Length);
        }

        return result;
    }

    private static OpenedSession Session() => new(
        new Uri("https://registry.test"), "tok",
        new TranscriptDetail(new RemoteTranscript { Harness = "claude-code", Key = "s", Kind = "session", Title = "Demo", Project = "protostar", MachineName = "vm" }, []),
        Harness);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
