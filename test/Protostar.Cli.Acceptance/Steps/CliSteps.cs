using System.Text.Json.Nodes;
using CliWrap.Buffered;
using Protostar.Cli.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Cli.Acceptance.Steps;

/// <summary>
/// Step definitions that drive the real protostar binary as a user would and assert on its
/// stdout/stderr, exit code, and filesystem side effects. One <see cref="Sandbox"/> and one
/// <see cref="HarnessSandbox"/> per scenario.
/// </summary>
[Binding]
public sealed class CliSteps : IDisposable
{
    private readonly HarnessSandbox _harness;
    private Sandbox? _sandbox;
    private BufferedCommandResult? _result;

    // Reqnroll resolves one HarnessSandbox per scenario and injects it here.
    public CliSteps(HarnessSandbox harness) => _harness = harness;

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
    public async Task WhenIRunWith(string argLine) =>
        _result = await CliRunner.RunAsync(SubstituteArgs(argLine));

    [When("I run the installed protostar with {string}")]
    public async Task WhenIRunTheInstalledProtostarWith(string argLine) =>
        _result = await CliRunner.RunBinaryAsync(Sandbox.InstalledBinary, SubstituteArgs(argLine));

    // Split on spaces, then substitute placeholders as whole tokens so a sandbox path containing
    // spaces still arrives as a single argument.
    private List<string> SubstituteArgs(string argLine) =>
        argLine
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token switch
            {
                "{installDir}" => Sandbox.InstallDir,
                "{harnessHome}" => _harness.ConfigDir,
                _ => token,
            })
            .ToList();

    [Given(@"a fake (\S+) harness")]
    public void GivenAFakeHarness(string harness)
    {
        // The HarnessSandbox ctor already created an empty config dir; the id is captured for the
        // Scenario Outline (one row per harness) and reserved for future per-harness layouts.
        _ = harness;
    }

    [Given(@"a fake (\S+) harness with settings:")]
    public void GivenAFakeHarnessWithSettings(string harness, string settings) =>
        _harness.WriteSettings(settings);

    [Then("the harness settings contain {string}")]
    public void ThenTheHarnessSettingsContain(string text) =>
        Assert.Contains(text, _harness.ReadSettings());

    [Then("the harness has no settings file")]
    public void ThenTheHarnessHasNoSettingsFile() =>
        Assert.False(File.Exists(_harness.SettingsPath),
            $"Did not expect a settings file at '{_harness.SettingsPath}'.");

    [Then("the harness has {int} protostar PostToolUse hooks")]
    public void ThenTheHarnessHasNManagedHooks(int expected)
    {
        var groups = JsonNode.Parse(_harness.ReadSettings())?["hooks"]?["PostToolUse"]?.AsArray();
        var count = groups?.Count(g =>
            g?["hooks"]?.AsArray()?.Any(h =>
                (h?["command"]?.GetValue<string>() ?? string.Empty).Contains("capture --hook")) == true) ?? 0;
        Assert.Equal(expected, count);
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

    [Then("within {int} seconds no protostar binary exists in the install dir")]
    public async Task ThenWithinSecondsNoBinaryExists(int seconds)
    {
        // A self-uninstall on Windows finishes deleting its own locked binary from a detached helper
        // after the process exits, so removal is asynchronous; poll until it lands.
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && File.Exists(Sandbox.InstalledBinary))
            await Task.Delay(250);

        Assert.False(File.Exists(Sandbox.InstalledBinary),
            $"Expected the self-uninstall to remove '{Sandbox.InstalledBinary}' within {seconds}s.");
    }

    public void Dispose()
    {
        _sandbox?.Dispose();
        _harness.Dispose();
    }
}
