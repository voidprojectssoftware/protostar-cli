using System.Text;
using System.Text.RegularExpressions;

namespace Protostar.Cli.Memory;

/// <summary>The rules for a memory folder's <c>MEMORY.md</c> index and the conflict copies a pull keeps beside a memory file.</summary>
/// <remarks>
/// <para>
/// Claude Code's <c>MEMORY.md</c> is a list of one-line pointers (<c>- [Title](file.md): hook</c>), one per memory
/// file, and Claude Code loads its start into every session. Because each line stands alone, two machines' edits to
/// it can be combined as a set union of lines (<see cref="Union"/>) without a model. That is only safe for this file,
/// which is why the rule applies to <see cref="FileName"/> by exact name and every other memory file conflicts
/// instead.
/// </para>
/// <para>
/// A conflict copy (<c>notes.from-laptop.md</c>) is another machine's version of a file this machine also changed.
/// It stays on this machine: push never sends it (another machine sending it back would name it after itself, and
/// it is not this machine's learning), and its pointer line in <c>MEMORY.md</c> is left out of what is synced
/// (<see cref="Synced"/>), so no other machine is handed a pointer to a file it does not have.
/// </para>
/// </remarks>
internal static partial class MemoryIndex
{
    /// <summary>The index file's name, at the root of a memory folder.</summary>
    public const string FileName = "MEMORY.md";

    /// <summary>The most lines of <c>MEMORY.md</c> Claude Code loads into a session.</summary>
    public const int LoadedLines = 200;

    /// <summary>The most bytes of <c>MEMORY.md</c> Claude Code loads into a session.</summary>
    public const int LoadedBytes = 25 * 1024;

    // A conflict copy's file name: the original's stem, ".from-", a sanitised machine name (no dots, so a note named
    // "notes.from-the.web.md" is not mistaken for one), an optional "-2" against collisions, then ".md".
    [GeneratedRegex(@"^(?<stem>.+)\.from-(?<machine>[A-Za-z0-9_-]+)\.md$")]
    private static partial Regex ConflictCopyPattern();

    // A pointer line: a list item that is a markdown link, capturing the link target. Matches to the end of the line
    // and its line break, so removing a match leaves the rest of the file byte for byte.
    [GeneratedRegex(@"^[ \t]*[-*][ \t]+\[[^\]\n]*\]\((?<target>[^)\s]+)\)[^\n]*(\n|$)", RegexOptions.Multiline)]
    private static partial Regex PointerPattern();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex UnsafeMachineChars();

    /// <summary>Whether <paramref name="path"/> (relative, <c>/</c> separators) is a conflict copy.</summary>
    public static bool IsConflictCopy(string path) => ConflictCopyPattern().IsMatch(path[(path.LastIndexOf('/') + 1)..]);

    /// <summary>The path of the <paramref name="attempt"/>th candidate name for <paramref name="machine"/>'s version of <paramref name="path"/>.</summary>
    /// <remarks><c>notes/port.md</c> from <c>Blake's Laptop</c> is <c>notes/port.from-Blake-s-Laptop.md</c>, then <c>...-2.md</c> and so on.</remarks>
    public static string ConflictCopyPath(string path, string? machine, int attempt)
    {
        var stem = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
        var name = UnsafeMachineChars().Replace(machine ?? "", "-").Trim('-');
        if (name.Length == 0)
            name = "another-machine";
        else if (name.Length > 40)
            name = name[..40].TrimEnd('-');
        return $"{stem}.from-{name}{(attempt > 1 ? $"-{attempt}" : "")}.md";
    }

    /// <summary>The bytes of <c>MEMORY.md</c> that are synced: the file without the pointer lines to conflict copies.</summary>
    /// <remarks>Returns <paramref name="content"/> itself when there are none, so its hash is the file's.</remarks>
    public static byte[] Synced(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        var stripped = Strip(text);
        return ReferenceEquals(stripped, text) ? content : Encoding.UTF8.GetBytes(stripped);
    }

    /// <summary><paramref name="text"/> without its pointer lines to conflict copies; the same instance when it has none.</summary>
    public static string Strip(string text)
    {
        var changed = false;
        var result = PointerPattern().Replace(text, m =>
        {
            if (!IsConflictCopy(m.Groups["target"].Value))
                return m.Value;
            changed = true;
            return "";
        });
        return changed ? result : text;
    }

    /// <summary>The pointer lines to conflict copies in <paramref name="text"/>, by the copy they point at, without line breaks.</summary>
    public static Dictionary<string, string> Pointers(string text)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in PointerPattern().Matches(text))
        {
            var target = m.Groups["target"].Value;
            if (IsConflictCopy(target))
                found.TryAdd(target, m.Value.TrimEnd('\n', '\r'));
        }

        return found;
    }

    /// <summary>The pointer line for the conflict copy at <paramref name="copy"/>.</summary>
    public static string PointerLine(string copy)
    {
        var slash = copy.LastIndexOf('/');
        var match = ConflictCopyPattern().Match(copy[(slash + 1)..]);
        var original = copy[..(slash + 1)] + match.Groups["stem"].Value + ".md";
        var machine = match.Groups["machine"].Value;
        return $"- [Conflict: {original} as {machine} changed it]({copy}): this machine changed {original} too. " +
               $"Merge what is worth keeping into {original}, then delete this copy.";
    }

    /// <summary>
    /// <paramref name="local"/>'s lines in their order, then each line of <paramref name="remote"/> that
    /// <paramref name="local"/> does not have, in <paramref name="remote"/>'s order. Never drops a line.
    /// </summary>
    /// <returns>The union, in <paramref name="local"/>'s line break style, and how many lines were added.</returns>
    public static (string Text, int Added) Union(string local, string remote)
    {
        var lines = Lines(local);
        var have = new HashSet<string>(lines, StringComparer.Ordinal);
        var added = Lines(remote).Where(have.Add).ToList();
        if (added.Count == 0)
            return (local, 0);
        return (Join([.. lines, .. added], NewLine(local)), added.Count);
    }

    /// <summary><paramref name="body"/> with <paramref name="pointers"/> above it, so an agent reads them first and they stay within what Claude Code loads.</summary>
    public static string WithPointers(string body, IReadOnlyList<string> pointers) =>
        pointers.Count == 0 ? body : Join(pointers, NewLine(body)) + body;

    /// <summary>The number of lines in <paramref name="text"/>.</summary>
    public static int LineCount(string text) => Lines(text).Count;

    private static List<string> Lines(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string NewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string Join(IEnumerable<string> lines, string newLine) => string.Concat(lines.Select(l => l + newLine));
}
