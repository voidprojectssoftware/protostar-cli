using Protostar.Cli.Commands;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Tests for <see cref="SkillsCommand.Truncate"/>, the table-cell shortener. Beyond the happy path,
/// these pin down the awkward widths (zero, negative, and a budget too small to hold the ellipsis)
/// that an earlier version got wrong by computing a negative slice index and throwing.
/// </summary>
public sealed class TruncateTests
{
    [Fact]
    public void Returns_short_text_unchanged()
    {
        Assert.Equal("hello", SkillsCommand.Truncate("hello", 80));
    }

    [Fact]
    public void Returns_text_unchanged_when_exactly_at_the_limit()
    {
        var text = new string('x', 80);
        // The boundary is inclusive (<=), so an exactly-max string must not be truncated.
        Assert.Equal(text, SkillsCommand.Truncate(text, 80));
    }

    [Fact]
    public void Returns_empty_input_unchanged()
    {
        Assert.Equal("", SkillsCommand.Truncate("", 80));
    }

    [Fact]
    public void Truncates_long_text_and_appends_an_ellipsis()
    {
        var result = SkillsCommand.Truncate("abcdefghij", 7);

        Assert.Equal("abcd...", result);
    }

    [Fact]
    public void Truncated_result_is_exactly_the_max_width_including_the_ellipsis()
    {
        var result = SkillsCommand.Truncate(new string('x', 200), 80);

        Assert.Equal(80, result.Length);
        Assert.EndsWith("...", result);
        // 77 chars of content + 3 dots; nothing beyond the budget.
        Assert.Equal(new string('x', 77) + "...", result);
    }

    [Theory]
    [InlineData(4, "a...")]   // smallest width that fits one content char plus the ellipsis
    [InlineData(3, "...")]    // exactly the ellipsis: no room for content
    [InlineData(2, "..")]     // too narrow for a full ellipsis: as many dots as fit
    [InlineData(1, ".")]
    public void Narrow_widths_degrade_to_dots_without_throwing(int max, string expected)
    {
        // "abcdefghij" is longer than every max here, so the truncation branch always runs.
        Assert.Equal(expected, SkillsCommand.Truncate("abcdefghij", max));
    }

    [Fact]
    public void Zero_width_returns_empty_even_for_nonempty_text()
    {
        Assert.Equal("", SkillsCommand.Truncate("hello", 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    [InlineData(int.MinValue)]
    public void Negative_width_returns_empty_instead_of_throwing(int max)
    {
        // Regression guard: max - 3 used to produce a negative slice index here and throw
        // ArgumentOutOfRangeException. It must now return empty.
        Assert.Equal("", SkillsCommand.Truncate("hello", max));
    }

    [Fact]
    public void Short_text_is_returned_even_when_max_is_below_the_ellipsis_length()
    {
        // text.Length (2) <= max (2): the fits-already check wins before any ellipsis logic.
        Assert.Equal("hi", SkillsCommand.Truncate("hi", 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(79)]
    public void Result_never_exceeds_the_requested_width(int max)
    {
        var result = SkillsCommand.Truncate(new string('y', 500), max);

        // The whole point of the helper: the output fits the budget for any positive width.
        Assert.True(result.Length <= max, $"width {max} produced {result.Length} chars");
        Assert.Equal(max, result.Length);
    }
}
