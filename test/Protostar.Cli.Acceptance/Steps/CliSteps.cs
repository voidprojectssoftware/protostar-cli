using CliWrap.Buffered;
using Protostar.Cli.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Cli.Acceptance.Steps;

/// <summary>
/// Step definitions that drive the real protostar binary as a user would and assert on its
/// stdout/stderr, exit code, and filesystem side effects. One <see cref="Sandbox"/> per scenario.
/// </summary>
[Binding]
public sealed class CliSteps : IDisposable
{
    private Sandbox? _sandbox;
    private BufferedCommandResult? _result;

    private Sandbox Sandbox => _sandbox ??= new Sandbox();

    private BufferedCommandResult Result =>
        _result ?? throw new InvalidOperationException("No protostar invocation has run in this scenario yet.");

    // Spectre disables colour when stdout is redirected, so captured output is plain text. We still
    // merge stderr in case a future command writes there.
    private string Output => Result.StandardOutput + Result.StandardError;

    [Given("a clean install sandbox")]
    public void GivenACleanInstallSandbox() => _ = Sandbox;

    [When("I run protostar with no arguments")]
    public async Task WhenIRunWithNoArguments() =>
        _result = await CliRunner.RunAsync([]);

    [When("I run protostar with {string}")]
    public async Task WhenIRunWith(string argLine)
    {
        // Split on spaces, then substitute the {installDir} placeholder as a whole token so a
        // sandbox path containing spaces still arrives as a single argument.
        var args = argLine
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token == "{installDir}" ? Sandbox.InstallDir : token)
            .ToList();
        _result = await CliRunner.RunAsync(args);
    }

    [Then("the exit code is {int}")]
    public void ThenTheExitCodeIs(int expected) =>
        Assert.Equal(expected, Result.ExitCode);

    [Then("the output contains {string}")]
    public void ThenTheOutputContains(string text) =>
        Assert.Contains(text, Output);

    [Then("the output matches {string}")]
    public void ThenTheOutputMatches(string pattern) =>
        Assert.Matches(pattern, Output);

    [Then("a protostar binary exists in the install dir")]
    public void ThenABinaryExists() =>
        Assert.True(File.Exists(Sandbox.InstalledBinary),
            $"Expected an installed binary at '{Sandbox.InstalledBinary}'.");

    [Then("no protostar binary exists in the install dir")]
    public void ThenNoBinaryExists() =>
        Assert.False(File.Exists(Sandbox.InstalledBinary),
            $"Did not expect a binary at '{Sandbox.InstalledBinary}'.");

    public void Dispose() => _sandbox?.Dispose();
}
