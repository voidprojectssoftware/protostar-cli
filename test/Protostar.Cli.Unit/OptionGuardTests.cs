using Protostar.Cli.Infrastructure;
using Spectre.Console.Cli;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Tests for <see cref="OptionGuard"/>, which refuses an option typed between a branch and its subcommand, before the
/// first command, or not declared by the command at all. The command name and the unknown options are what Spectre
/// resolved, so these feed them the way the parser would.
/// </summary>
public sealed class OptionGuardTests
{
    [Fact]
    public void Refuses_a_flag_before_the_subcommand_and_gives_the_line_that_works()
    {
        var error = OptionGuard.Check(["skills", "--global-only", "push", "notes"], "push");

        Assert.NotNull(error);
        Assert.StartsWith("--global-only goes after the subcommand", error);
        Assert.EndsWith("protostar skills push --global-only notes", error);
    }

    [Fact]
    public void Moves_an_option_value_along_with_its_option()
    {
        var error = OptionGuard.Check(["project", "--dir", "/tmp/x", "link", "demo"], "link");

        Assert.EndsWith("protostar project link --dir /tmp/x demo", error);
    }

    [Fact]
    public void Names_every_misplaced_option()
    {
        var error = OptionGuard.Check(["skills", "--project", "x", "-H", "claude-code", "pull"], "pull");

        Assert.StartsWith("--project, -H go after the subcommand", error);
    }

    [Fact]
    public void Quotes_a_value_with_spaces_in_the_suggested_line()
    {
        var error = OptionGuard.Check(["sessions", "--harness-home", "/my home", "status"], "status");

        Assert.EndsWith("protostar sessions status --harness-home \"/my home\"", error);
    }

    [Fact]
    public void Refuses_when_an_option_value_equals_the_subcommand_name()
    {
        // The first "push" is --project's value; the option still precedes the subcommand.
        var error = OptionGuard.Check(["skills", "--project", "push", "push"], "push");

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(new[] { "skills", "push", "--global-only" }, "push")]
    [InlineData(new[] { "capture", "--hook", "Stop" }, "capture")]
    [InlineData(new[] { "install-hooks", "--harness", "claude-code", "--yes" }, "install-hooks")]
    [InlineData(new[] { "auth", "status", "--registry", "https://example.test" }, "status")]
    public void Allows_options_after_the_command(string[] args, string commandName)
    {
        Assert.Null(OptionGuard.Check(args, commandName));
    }

    [Fact]
    public void Allows_options_on_a_branch_default_command()
    {
        Assert.Null(OptionGuard.Check(["skills", "--global-only"], "__default_command"));
        Assert.Null(OptionGuard.Check(["project", "--dir", "push"], "__default_command"));
    }

    private static readonly string[] Commands = ["setup", "install-hooks", "capture", "skills", "sessions", "project", "auth"];

    [Fact]
    public void Refuses_an_option_before_a_branch_and_moves_it_after_the_subcommand()
    {
        var error = OptionGuard.CheckRoot(["--registry", "X", "skills", "remote"], Commands);

        Assert.NotNull(error);
        Assert.StartsWith("--registry goes after the subcommand", error);
        Assert.EndsWith("protostar skills remote --registry X", error);
    }

    [Fact]
    public void Refuses_a_flag_whose_next_token_is_the_command()
    {
        // Spectre would take "setup" as the flag's value and quietly run the default command.
        Assert.EndsWith("protostar setup --yes", OptionGuard.CheckRoot(["--yes", "setup"], Commands));
    }

    [Fact]
    public void Names_every_option_before_the_command_and_keeps_the_rest_in_order()
    {
        var error = OptionGuard.CheckRoot(["--registry=X", "--harness-home", "/my home", "project", "link", "demo"], Commands);

        Assert.StartsWith("--registry=X, --harness-home go after the subcommand", error);
        Assert.EndsWith("protostar project link demo --registry=X --harness-home \"/my home\"", error);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v skills")]
    [InlineData("--help")]
    [InlineData("--unknown")]
    [InlineData("skills --registry X remote")]
    [InlineData("capture --hook Stop")]
    public void Leaves_lines_without_an_option_before_the_first_command_to_spectre(string line)
    {
        Assert.Null(OptionGuard.CheckRoot(line.Split(' '), Commands));
    }

    private sealed class PullSettings : CommandSettings
    {
        [CommandOption("-H|--harness <ID>")]
        public string? Harness { get; init; }

        [CommandOption("--force")]
        public bool Force { get; init; }

        [CommandOption("--forge", IsHidden = true)]
        public bool Forge { get; init; }

        [CommandOption("--project <DIR>")]
        public string? Project { get; init; }
    }

    private static string? Unknown(params string[] options) =>
        OptionGuard.CheckUnknown(options, ["skills", "pull", .. options], typeof(PullSettings), "skills pull");

    [Fact]
    public void Allows_a_command_line_with_no_unknown_option()
    {
        Assert.Null(Unknown());
    }

    [Fact]
    public void Refuses_an_unknown_option_and_suggests_the_closest_declared_one()
    {
        var error = Unknown("--forse");

        Assert.StartsWith("Unknown option --forse for protostar skills pull. Did you mean --force?", error);
        Assert.EndsWith("protostar skills pull --help", error);
    }

    [Theory]
    [InlineData("--projcet", "--project")]
    [InlineData("--FORCE2", "--force")]
    [InlineData("--harnes", "--harness")]
    public void Suggests_across_swaps_case_and_missing_letters(string typed, string suggested)
    {
        Assert.Contains($"Did you mean {suggested}?", Unknown(typed));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-x")]
    public void Suggests_nothing_when_no_declared_option_is_close(string typed)
    {
        var error = Unknown(typed);

        Assert.StartsWith($"Unknown option {typed} for protostar skills pull. See:", error);
        Assert.DoesNotContain("Did you mean", error);
    }

    [Fact]
    public void Never_suggests_a_hidden_option()
    {
        // --forg is one edit from the hidden --forge and two from --force, too far to offer.
        Assert.DoesNotContain("Did you mean", Unknown("--forg"));
    }

    [Fact]
    public void Names_every_unknown_option_with_its_suggestion()
    {
        var error = Unknown("--forse", "--bogus");

        Assert.StartsWith("Unknown options --forse (did you mean --force?), --bogus for protostar skills pull.", error);
    }

    [Fact]
    public void Names_a_long_option_typed_with_one_dash_as_typed()
    {
        // Spectre splits -force into -f -o -r -c -e.
        var error = OptionGuard.CheckUnknown(
            ["-f", "-o", "-r", "-c", "-e"], ["skills", "pull", "-force"], typeof(PullSettings), "skills pull");

        Assert.StartsWith("Unknown option -force for protostar skills pull. Did you mean --force?", error);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Points_a_version_flag_at_the_root_command(string flag)
    {
        var error = OptionGuard.CheckUnknown([flag], ["skills", flag], typeof(PullSettings), "skills");

        Assert.StartsWith($"{flag} is not an option of protostar skills. For the version, run:", error);
        Assert.EndsWith("protostar --version", error);
    }

    [Theory]
    [InlineData(new[] { "skills", "pull", "--forse" }, "pull", "skills pull")]
    [InlineData(new[] { "skills", "--version" }, "__default_command", "skills")]
    [InlineData(new[] { "setup", "--yse" }, "setup", "setup")]
    [InlineData(new[] { "--yse" }, "__default_command", "")]
    public void Names_the_command_as_typed(string[] args, string commandName, string path)
    {
        Assert.Equal(path, OptionGuard.CommandPath(args, commandName));
    }
}
