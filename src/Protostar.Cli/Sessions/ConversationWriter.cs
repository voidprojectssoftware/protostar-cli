using System.Text;
using Spectre.Console;

namespace Protostar.Cli.Sessions;

/// <summary>A run of text in one style, a piece of a line <see cref="ConversationWriter"/> writes.</summary>
/// <param name="Style">A Spectre style such as <c>grey</c> or <c>bold blue</c>; <c>null</c> for the default.</param>
internal readonly record struct Span(string Text, string? Style = null);

/// <summary>Writes the lines of <c>sessions show</c>, styled and wrapped for a terminal or plain for a file or pipe.</summary>
/// <remarks>
/// <para>
/// A transcript is millions of lines, and sending each through Spectre's markup parser and renderer cost about 20
/// microseconds a line, most of a 100 MB session's time. This writes the same characters Spectre would: each style's
/// escape codes are asked of Spectre once, and a line that does not fit the terminal is word wrapped the way Spectre's
/// paragraph wraps it. A line with any character outside <see cref="IsSimple"/> (wide or combining characters, tabs,
/// control characters), whose width this does not measure, is still handed to Spectre. Output is gathered and written
/// to the console in large pieces rather than one write per line.
/// </para>
/// <para>
/// When the output is not a terminal, lines are written plain: no escape codes and no wrapping. Spectre would have
/// wrapped them at an assumed 80 columns, which splits a line that grep or a script would want whole.
/// </para>
/// </remarks>
internal sealed class ConversationWriter
{
    // Gathered output is written once it passes this, and at every Flush.
    private const int FlushAt = 64 * 1024;

    private readonly IAnsiConsole _console;
    private readonly TextWriter _output;
    private readonly bool _plain;
    private readonly int _width;
    private readonly StringBuilder _buffer = new();
    private readonly Dictionary<string, (string Open, string Close)> _styles = new(StringComparer.Ordinal);
    private readonly List<Span> _line = [];

    /// <param name="console">The console whose capabilities and width styled lines follow.</param>
    /// <param name="output">Where the lines go: the writer behind <paramref name="console"/>.</param>
    /// <param name="plain">Write without escape codes or wrapping, for output that is not a terminal.</param>
    public ConversationWriter(IAnsiConsole console, TextWriter output, bool plain)
    {
        _console = console;
        _output = output;
        _plain = plain;
        _width = console.Profile.Width;
    }

    /// <summary>The writer for standard output, plain when it is redirected.</summary>
    public static ConversationWriter ForConsole() => new(AnsiConsole.Console, Console.Out, plain: Console.IsOutputRedirected);

    /// <summary>Writes one line made of <paramref name="spans"/>.</summary>
    public void Line(params ReadOnlySpan<Span> spans)
    {
        if (_plain)
        {
            foreach (var span in spans)
                _buffer.Append(span.Text);
            _buffer.Append('\n');
        }
        else if (AllSimple(spans))
        {
            Wrap(spans);
        }
        else
        {
            var markup = new StringBuilder();
            foreach (var span in spans)
                markup.Append(span.Style is null ? Markup.Escape(span.Text) : $"[{span.Style}]{Markup.Escape(span.Text)}[/]");
            _buffer.Append(_console.ToAnsi(new Markup(markup.ToString()))).Append('\n');
        }

        if (_buffer.Length >= FlushAt)
            Flush();
    }

    /// <summary>Writes out what has been gathered.</summary>
    public void Flush()
    {
        if (_buffer.Length == 0)
            return;
        _output.Write(_buffer);
        _output.Flush();
        _buffer.Clear();
    }

    // Printable ASCII, and the box and arrow characters the conversation draws: each is one terminal cell.
    private static bool IsSimple(char c) => c is >= ' ' and <= '~' or '│' or '┌' or '└' or '▸' or '◂' or '·';

    private static bool AllSimple(ReadOnlySpan<Span> spans)
    {
        foreach (var span in spans)
        {
            foreach (var c in span.Text)
            {
                if (!IsSimple(c))
                    return false;
            }
        }

        return true;
    }

    // A line that fits is written as it is. One that does not is wrapped the way Spectre's Paragraph.SplitLines does it
    // (Spectre.Console 0.55): the text is cut into words and runs of white space, one style run at a time; a piece that
    // does not fit ends the line, white space that would start a wrapped line is dropped, and a piece wider than the
    // whole line is cut into widths that start on a line of their own. Ends with the line's newline.
    private void Wrap(ReadOnlySpan<Span> spans)
    {
        var length = 0;
        foreach (var span in spans)
            length += span.Text.Length;
        if (length <= _width)
        {
            foreach (var span in spans)
                _line.Add(span);
            Write(_line);
            _line.Clear();
            _buffer.Append('\n');
            return;
        }

        var lines = new List<List<Span>> { new() };
        foreach (var run in Runs(spans))
        {
            var start = 0;
            while (start < run.Text.Length)
            {
                var end = start;
                var space = run.Text[start] == ' ';
                while (end < run.Text.Length && (run.Text[end] == ' ') == space)
                    end++;
                var piece = run.Text[start..end];
                start = end;

                if (piece.Length > _width)
                {
                    if (Width(lines[^1]) > 0)
                        lines.Add([]);
                    for (var at = 0; at < piece.Length; at += _width)
                        Place(lines, new Span(piece.Substring(at, Math.Min(_width, piece.Length - at)), run.Style), space);
                }
                else
                {
                    Place(lines, new Span(piece, run.Style), space);
                }
            }
        }

        // Spectre also loses the newline, or adds a blank line, after a line that ends in white space it dropped;
        // a wrapped line here ends in exactly one newline either way.
        foreach (var line in lines)
        {
            if (line.Count == 0)
                continue;
            Write(line);
            _buffer.Append('\n');
        }
    }

    // Spectre's markup joins neighbouring text of one style before it is cut into words, so a word can span two spans.
    private static List<Span> Runs(ReadOnlySpan<Span> spans)
    {
        var runs = new List<Span>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
                continue;
            if (runs.Count > 0 && runs[^1].Style == span.Style)
                runs[^1] = new Span(runs[^1].Text + span.Text, span.Style);
            else
                runs.Add(span);
        }

        return runs;
    }

    private void Place(List<List<Span>> lines, Span piece, bool space)
    {
        if (Width(lines[^1]) + piece.Text.Length > _width)
        {
            lines.Add([]);
            if (space)
                return;
        }

        lines[^1].Add(piece);
    }

    private static int Width(List<Span> line)
    {
        var width = 0;
        foreach (var piece in line)
            width += piece.Text.Length;
        return width;
    }

    // Writes one line's pieces a style run at a time, as Spectre merges neighbouring segments of one style.
    private void Write(List<Span> line)
    {
        var i = 0;
        while (i < line.Count)
        {
            var style = line[i].Style;
            var j = i;
            var empty = true;
            while (j < line.Count && (line[j].Style == style || line[j].Text.Length == 0))
                empty &= line[j++].Text.Length == 0;
            if (!empty)
            {
                var (open, close) = Codes(style);
                _buffer.Append(open);
                for (var k = i; k < j; k++)
                    _buffer.Append(line[k].Text);
                _buffer.Append(close);
            }

            i = j;
        }
    }

    // The escape codes Spectre writes around text in this style on this console, asked of it once per style.
    private (string Open, string Close) Codes(string? style)
    {
        if (style is null)
            return ("", "");
        if (!_styles.TryGetValue(style, out var codes))
        {
            var sample = _console.ToAnsi(new Markup($"[{style}]x[/]"));
            var at = sample.IndexOf('x');
            codes = (sample[..at], sample[(at + 1)..]);
            _styles[style] = codes;
        }

        return codes;
    }
}
