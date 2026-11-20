using Protostar.Cli.Infrastructure;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Tests for <see cref="OptionPlacementGuard.Check"/>, which refuses an option typed between a branch and its
/// subcommand. The command name is what Spectre resolved, so these feed it the way the parser would.
/// </summary>
public sealed class OptionPlacementGuardTests
{
    [Fact]
    public void Refuses_a_flag_before_the_subcommand_and_gives_the_line_that_works()
    {
        var error = OptionPlacementGuard.Check(["skills", "--global-only", "push", "notes"], "push");

        Assert.NotNull(error);
        Assert.StartsWith("--global-only goes after the subcommand", error);
        Assert.EndsWith("protostar skills push --global-only notes", error);
    }

    [Fact]
    public void Moves_an_option_value_along_with_its_option()
    {
        var error = OptionPlacementGuard.Check(["project", "--dir", "/tmp/x", "link", "demo"], "link");

        Assert.EndsWith("protostar project link --dir /tmp/x demo", error);
    }

    [Fact]
    public void Names_every_misplaced_option()
    {
        var error = OptionPlacementGuard.Check(["skills", "--project", "x", "-H", "claude-code", "pull"], "pull");

        Assert.StartsWith("--project, -H go after the subcommand", error);
    }

    [Fact]
    public void Quotes_a_value_with_spaces_in_the_suggested_line()
    {
        var error = OptionPlacementGuard.Check(["sessions", "--harness-home", "/my home", "status"], "status");

        Assert.EndsWith("protostar sessions status --harness-home \"/my home\"", error);
    }

    [Fact]
    public void Refuses_when_an_option_value_equals_the_subcommand_name()
    {
        // The first "push" is --project's value; the option still precedes the subcommand.
        var error = OptionPlacementGuard.Check(["skills", "--project", "push", "push"], "push");

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(new[] { "skills", "push", "--global-only" }, "push")]
    [InlineData(new[] { "capture", "--hook", "Stop" }, "capture")]
    [InlineData(new[] { "install-hooks", "--harness", "claude-code", "--yes" }, "install-hooks")]
    [InlineData(new[] { "auth", "status", "--registry", "https://example.test" }, "status")]
    public void Allows_options_after_the_command(string[] args, string commandName)
    {
        Assert.Null(OptionPlacementGuard.Check(args, commandName));
    }

    [Fact]
    public void Allows_options_on_a_branch_default_command()
    {
        Assert.Null(OptionPlacementGuard.Check(["skills", "--global-only"], "__default_command"));
        Assert.Null(OptionPlacementGuard.Check(["project", "--dir", "push"], "__default_command"));
    }
}
