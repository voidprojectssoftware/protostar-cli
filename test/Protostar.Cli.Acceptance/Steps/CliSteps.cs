using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
public sealed partial class CliSteps : IDisposable
{
    private readonly HarnessSandbox _harness;
    private Sandbox? _sandbox;
    private BufferedCommandResult? _result;

    // Reqnroll resolves one HarnessSandbox per scenario and injects it here.
    public CliSteps(HarnessSandbox harness) => _harness = harness;

    private Sandbox Sandbox => _sandbox ??= new Sandbox();

    private BufferedCommandResult Result =>
        _result ?? throw new InvalidOperationException("No protostar invocation has run in this scenario yet.");

    // Assertions read plain text, with stderr merged in case a command writes there. Spectre usually
    // drops colour when stdout is redirected, but it detects CI (GITHUB_ACTIONS and others) and emits
    // ANSI anyway, so a phrase that spans two styles ("[bold]Sign-in[/]: [red]failed[/]") only matches
    // once the escape sequences are stripped. Stripping here keeps every scenario independent of the
    // machine's environment.
    private string Output => AnsiEscape().Replace(Result.StandardOutput + Result.StandardError, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

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
                "{protostar}" => CliRunner.BinaryPath,
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

    [Given("a global skill named {string} in the harness")]
    public void GivenAGlobalSkillInTheHarness(string name)
    {
        var dir = Path.Combine(_harness.ConfigDir, "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: test skill\n---\nBody.\n");
    }

    // Shared by every scenario through the one config dir, so each scenario that stores a session uses its own registry.
    [Given(@"a stored session for ""(\S+)"" whose access token is (current|expired)")]
    public void GivenAStoredSession(string registry, string state)
    {
        var path = Path.Combine(CliRunner.ConfigDir, "credentials.json");
        var file = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        var registries = file["Registries"] as JsonObject ?? (JsonObject)(file["Registries"] = new JsonObject());
        registries[registry] = new JsonObject
        {
            ["Registry"] = registry,
            ["AccessToken"] = "access",
            ["RefreshToken"] = "refresh",
            ["ExpiresAtUtc"] = DateTimeOffset.UtcNow.AddHours(state == "expired" ? -1 : 1).ToString("O"),
            ["Login"] = "octocat",
        };
        File.WriteAllText(path, file.ToJsonString());
    }

    [Given("a session transcript {string} in the harness")]
    public void GivenASessionTranscriptInTheHarness(string key)
    {
        var project = Path.Combine(_harness.ConfigDir, "projects", "-fixture-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, key + ".jsonl"), "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n");
    }

    // The --json contract (docs/reference/json-output.md): data alone on stdout, at most one error object on stderr.
    [Then("standard output is a JSON array whose records have ids {string}")]
    public void ThenStandardOutputIsAJsonArrayWithIds(string ids)
    {
        var records = JsonNode.Parse(Result.StandardOutput)!.AsArray();
        Assert.Equal(ids.Split(',', StringSplitOptions.RemoveEmptyEntries), records.Select(r => r!["id"]!.GetValue<string>()));
        Assert.False(Result.StandardOutput.Contains('\u001b'), "Standard output holds terminal escape codes.");
    }

    [Then("standard output is empty")]
    public void ThenStandardOutputIsEmpty() =>
        Assert.Equal(string.Empty, Result.StandardOutput.Trim());

    [Then("standard error is a JSON error with code {string}")]
    public void ThenStandardErrorIsAJsonError(string code)
    {
        var error = JsonNode.Parse(Result.StandardError)!["error"]!;
        Assert.Equal(code, error["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(error["message"]!.GetValue<string>()));
    }

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

    // Every protostar-managed hook command in the harness settings, as event name and the exact command line.
    private List<(string Event, string Command)> InstalledHookCommands() =>
        (JsonNode.Parse(_harness.ReadSettings())?["hooks"]?.AsObject() ?? [])
            .SelectMany(entry => (entry.Value?.AsArray() ?? [])
                .SelectMany(group => group?["hooks"]?.AsArray() ?? [])
                .Select(hook => hook?["command"]?.GetValue<string>() ?? string.Empty)
                .Where(command => command.Contains("capture --hook"))
                .Select(command => (entry.Key, command)))
            .ToList();

    [Then("every installed hook command runs through the shell and names its own event")]
    public async Task ThenEveryInstalledHookCommandNamesItsEvent()
    {
        var hooks = InstalledHookCommands();
        Assert.Equal(["PostToolUse", "SessionEnd", "SessionStart", "Stop"], hooks.Select(h => h.Event).Order());

        foreach (var (hookEvent, command) in hooks)
        {
            var result = await CliRunner.RunShellAsync(command, "{}", _harness.ConfigDir);
            var output = AnsiEscape().Replace(result.StandardOutput + result.StandardError, string.Empty);
            Assert.True(result.ExitCode == 0, $"`{command}` exited {result.ExitCode}: {output}");
            // "unknown" would mean --hook was dropped; the event name proves it reached the capture command.
            Assert.Contains($"protostar capture ({hookEvent})", output);
        }
    }

    [Then("the exit code is {int}")]
    public void ThenTheExitCodeIs(int expected) =>
        Assert.Equal(expected, Result.ExitCode);

    [Then("the exit code is not {int}")]
    public void ThenTheExitCodeIsNot(int unexpected) =>
        Assert.NotEqual(unexpected, Result.ExitCode);

    [Then("the output contains {string}")]
    public void ThenTheOutputContains(string text) =>
        Assert.Contains(text, Output);

    [Then("the output does not contain {string}")]
    public void ThenTheOutputDoesNotContain(string text) =>
        Assert.DoesNotContain(text, Output);

    // Spectre wraps an error at the console width (80 columns when redirected), so a long suggested command line
    // arrives across several lines; joining them lets a scenario assert on the line as typed.
    [Then("the output, with line breaks joined, contains {string}")]
    public void ThenTheJoinedOutputContains(string text) =>
        Assert.Contains(text, WhitespaceRun().Replace(Output, " "));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

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
