using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Protostar.Cli.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Cli.Acceptance.Steps;

/// <summary>
/// Drives the real Stop hook and proves its push is detached: the hook process exits and its output closes while
/// the push it started is still connected to a registry that never answers.
/// </summary>
/// <remarks>
/// Each scenario gets its own config dir (a fake, unexpired session for a loopback registry) and a fixture
/// transcript, so nothing reads the real <c>~/.protostar</c> or <c>~/.claude</c>. The "registry" is a TCP listener
/// that accepts and never replies; dropping its connections is what lets the background push finish, so the
/// scenario never leaves a process behind, and <see cref="Dispose"/> drops them whatever happened.
/// </remarks>
[Binding]
public sealed class BackgroundBackupSteps : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "protostar-background", Guid.NewGuid().ToString("n"));
    private readonly TcpListener _registry = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _connections = [];
    private string _transcript = string.Empty;
    private BufferedCommandResult? _hook;
    private TimeSpan _hookTime;
    private bool _registryInEnvironment = true;

    private string ConfigDir => Path.Combine(_root, "protostar");

    private string RegistryUrl => $"http://127.0.0.1:{((IPEndPoint)_registry.LocalEndpoint).Port}";

    private string CaptureLog => Path.Combine(ConfigDir, "capture.log");

    [Given("I am signed in to a registry that accepts connections but never answers")]
    public void GivenASilentRegistry()
    {
        _registry.Start();
        Directory.CreateDirectory(ConfigDir);
        var token = new
        {
            Registries = new Dictionary<string, object>
            {
                [RegistryUrl] = new { Registry = RegistryUrl, AccessToken = "test-token", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) },
            },
        };
        File.WriteAllText(Path.Combine(ConfigDir, "credentials.json"), JsonSerializer.Serialize(token));
    }

    // What setup and auth login leave behind; the hook then runs with no PROTOSTAR_REGISTRY_URL, as Claude Code
    // started from a shell that never exported it would.
    [Given("that registry is my saved default and not in the environment")]
    public void GivenTheRegistryIsTheSavedDefault()
    {
        File.WriteAllText(Path.Combine(ConfigDir, "config.json"), JsonSerializer.Serialize(new { registry = RegistryUrl }));
        _registryInEnvironment = false;
    }

    [Given("a session transcript on disk")]
    public void GivenASessionTranscript()
    {
        var project = Path.Combine(_root, ".claude", "projects", "-fixture-project");
        Directory.CreateDirectory(project);
        _transcript = Path.Combine(project, "6f83525b-790f-4654-944d-ba3b0c96453e.jsonl");
        File.WriteAllText(_transcript, "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n");
    }

    [When("the Stop hook fires for the transcript")]
    public async Task WhenTheStopHookFires()
    {
        var payload = JsonSerializer.Serialize(new { session_id = "s", hook_event_name = "Stop", transcript_path = _transcript });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();

        // ExecuteBufferedAsync returns only once the hook's stdout and stderr close, so a child that inherited
        // them would hold this call until the push ended, which cannot happen before the registry drops it.
        _hook = await CliWrap.Cli.Wrap(CliRunner.BinaryPath)
            .WithArguments(["capture", "--hook", "Stop"])
            .WithStandardInputPipe(PipeSource.FromString(payload, Encoding.UTF8))
            .WithEnvironmentVariables(env => env
                .Set("PROTOSTAR_CONFIG_DIR", ConfigDir)
                // null removes a value the test runner itself may have inherited.
                .Set("PROTOSTAR_REGISTRY_URL", _registryInEnvironment ? RegistryUrl : null)
                .Build())
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(limit.Token);
        _hookTime = clock.Elapsed;
    }

    [Then("the hook exits {int} within {int} seconds")]
    public void ThenTheHookExitsWithin(int exitCode, int seconds)
    {
        Assert.NotNull(_hook);
        Assert.Equal(exitCode, _hook.ExitCode);
        Assert.Contains("backing up the session in the background", _hook.StandardOutput);
        Assert.True(_hookTime < TimeSpan.FromSeconds(seconds), $"The hook took {_hookTime.TotalSeconds:0.0} s.");
    }

    [Then("a background push is connected to the registry after the hook has exited")]
    public async Task ThenABackgroundPushIsConnected()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = await _registry.AcceptTcpClientAsync(limit.Token);
        _connections.Add(client);

        // Read the start of the request so we know it is the push asking for /v1/meta, not a stray connection.
        var buffer = new byte[256];
        var read = await client.GetStream().ReadAsync(buffer, limit.Token);
        Assert.Contains("/v1/meta", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [When("the registry drops the connection")]
    public void WhenTheRegistryDropsTheConnection() => DropConnections();

    [Then("the background push logs {string}")]
    public async Task ThenTheBackgroundPushLogs(string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            // HttpClient may retry on a fresh connection once; drop that too so the push gives up.
            if (_registry.Pending())
            {
                _connections.Add(await _registry.AcceptTcpClientAsync());
                DropConnections();
            }

            if (File.Exists(CaptureLog) && ReadShared(CaptureLog).Contains(text, StringComparison.Ordinal))
                return;
            await Task.Delay(100);
        }

        Assert.Fail($"capture.log never said \"{text}\". It holds: {(File.Exists(CaptureLog) ? ReadShared(CaptureLog) : "(no log)")}");
    }

    // --offline so the step does not wait on the silent registry; the records alone carry the failure.
    [Then("protostar status --offline exits {int} and says {string}")]
    public async Task ThenStatusSays(int exitCode, string text)
    {
        var status = await RunStatusAsync();
        Assert.True(exitCode == status.ExitCode, $"status exited {status.ExitCode}:\n{status.StandardOutput}{status.StandardError}");
        Assert.Contains(text, status.StandardOutput.ReplaceLineEndings(" "), StringComparison.Ordinal);
    }

    [Then("protostar status --offline --json reports the problem {string}")]
    public async Task ThenStatusJsonReports(string problem)
    {
        var status = await RunStatusAsync("--json");
        using var json = JsonDocument.Parse(status.StandardOutput);
        Assert.Contains(problem, json.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
    }

    private Task<BufferedCommandResult> RunStatusAsync(params string[] extra) =>
        CliWrap.Cli.Wrap(CliRunner.BinaryPath)
            .WithArguments(["status", "--offline", "--registry", RegistryUrl, "--harness-home", Path.Combine(_root, ".claude"), .. extra])
            .WithEnvironmentVariables(env => env.Set("PROTOSTAR_CONFIG_DIR", ConfigDir).Build())
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private void DropConnections()
    {
        foreach (var connection in _connections)
        {
            connection.Client.LingerState = new LingerOption(true, 0);
            connection.Dispose();
        }

        _connections.Clear();
    }

    public void Dispose()
    {
        DropConnections();
        _registry.Stop();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a push still finishing may hold the log open for a moment on Windows.
        }
    }
}
