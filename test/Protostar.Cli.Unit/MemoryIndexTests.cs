using System.Text;
using Protostar.Cli.Memory;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="MemoryIndex"/>: the line union of <c>MEMORY.md</c> and conflict copy naming.</summary>
public sealed class MemoryIndexTests
{
    [Fact]
    public void Union_keeps_local_lines_in_order_then_appends_the_remote_lines_it_lacks_in_remote_order()
    {
        var (text, added) = MemoryIndex.Union("- c\n- a\n", "- a\n- b\n- d\n");

        Assert.Equal("- c\n- a\n- b\n- d\n", text);
        Assert.Equal(2, added);
    }

    [Fact]
    public void Union_never_drops_a_line_either_side_has()
    {
        var local = "- [Mine](mine.md): mine\n- [Shared](shared.md): shared\n";
        var remote = "- [Shared](shared.md): shared\n- [Theirs](theirs.md): theirs\n";

        var (text, _) = MemoryIndex.Union(local, remote);

        foreach (var line in (local + remote).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line, text.Split('\n'));
    }

    [Fact]
    public void Union_with_nothing_new_returns_the_local_text_unchanged()
    {
        const string local = "- a\n- b";

        Assert.Equal((local, 0), MemoryIndex.Union(local, "- b\n"));
    }

    [Fact]
    public void Union_keeps_local_crlf_line_breaks_and_matches_lines_whatever_the_remote_uses()
    {
        var (text, added) = MemoryIndex.Union("- a\r\n", "- a\n- b\n");

        Assert.Equal("- a\r\n- b\r\n", text);
        Assert.Equal(1, added);
    }

    [Theory]
    [InlineData("notes/port.md", "laptop", 1, "notes/port.from-laptop.md")]
    [InlineData("port.md", "Blake's Laptop (2)", 1, "port.from-Blake-s-Laptop-2.md")]
    [InlineData("port.md", "laptop", 3, "port.from-laptop-3.md")]
    [InlineData("port.md", null, 1, "port.from-another-machine.md")]
    [InlineData("port.md", "...", 1, "port.from-another-machine.md")]
    [InlineData("data.txt", "laptop", 1, "data.txt.from-laptop.md")]
    public void A_conflict_copy_is_named_after_the_file_and_a_sanitised_machine_name(string path, string? machine, int attempt, string expected)
    {
        Assert.Equal(expected, MemoryIndex.ConflictCopyPath(path, machine, attempt));
        Assert.True(MemoryIndex.IsConflictCopy(expected));
    }

    [Theory]
    [InlineData("port.md")]
    [InlineData("notes.from-the.web.md")]
    [InlineData("from-laptop.md")]
    [InlineData("MEMORY.md")]
    public void An_ordinary_note_is_not_a_conflict_copy(string path) => Assert.False(MemoryIndex.IsConflictCopy(path));

    [Fact]
    public void Synced_leaves_out_only_the_pointer_lines_to_conflict_copies()
    {
        var body = "- [Port](port.md): the port\n- [Web](notes.from-the.web.md): a note\n";
        var index = MemoryIndex.WithPointers(body, [MemoryIndex.PointerLine("port.from-laptop.md")]);

        Assert.StartsWith("- [Conflict: port.md as laptop changed it](port.from-laptop.md)", index);
        Assert.Equal(body, Encoding.UTF8.GetString(MemoryIndex.Synced(Encoding.UTF8.GetBytes(index))));
    }

    [Fact]
    public void Synced_returns_the_same_bytes_when_there_is_nothing_to_leave_out()
    {
        var content = Encoding.UTF8.GetBytes("- [Port](port.md): the port\n");

        Assert.Same(content, MemoryIndex.Synced(content));
    }

    [Fact]
    public void Pointers_finds_each_conflict_copys_line_by_its_target()
    {
        var pointers = MemoryIndex.Pointers("- [x](a.from-laptop.md): custom words\r\n- [Port](port.md): the port\n");

        Assert.Equal("- [x](a.from-laptop.md): custom words", Assert.Single(pointers).Value);
    }
}
