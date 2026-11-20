using System.Diagnostics;
using System.Text.Json.Nodes;
using Protostar.Cli.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Cli.Acceptance.Steps;

/// <summary>Steps for Mcp.feature: a scripted MCP client that drives <c>protostar mcp</c> over stdio, one request at a time.</summary>
[Binding]
public sealed class McpSteps : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private readonly List<string> _stdout = [];
    private Process? _server;
    private int _nextId;
    private JsonNode? _tools;
    private JsonNode? _reply;

    private Process Server => _server ?? throw new InvalidOperationException("No MCP server was started in this scenario.");

    [When("an MCP client starts protostar with {string}")]
    public async Task WhenAnMcpClientStarts(string argLine)
    {
        var start = new ProcessStartInfo(CliRunner.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in argLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            start.ArgumentList.Add(arg);
        start.Environment["PROTOSTAR_CONFIG_DIR"] = CliRunner.ConfigDir;
        _server = Process.Start(start)!;
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginErrorReadLine();

        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "acceptance", ["version"] = "0" },
        });
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        _tools = (await RequestAsync("tools/list", new JsonObject()))["result"]!["tools"];
    }

    [Then("the server offers exactly the tools {string}, each read-only")]
    public void ThenTheServerOffersExactlyTheTools(string names)
    {
        var tools = _tools!.AsArray();
        Assert.Equal(names.Split(','), tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(tools, t =>
        {
            Assert.True(t!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            Assert.False(t["annotations"]!["destructiveHint"]!.GetValue<bool>());
        });
    }

    [When("the client calls {string} for {string}")]
    public async Task WhenTheClientCalls(string tool, string value)
    {
        var argument = tool == "search" ? "query" : "id";
        _reply = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject { [argument] = value } });
    }

    [Then("the call is a tool error with code {string}")]
    public void ThenTheCallIsAToolError(string code)
    {
        var result = _reply!["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var error = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!["error"]!;
        Assert.Equal(code, error["code"]!.GetValue<string>());
    }

    [Then("the call is refused as an unknown tool")]
    public void ThenTheCallIsRefusedAsAnUnknownTool()
    {
        var refusal = _reply!["error"]?["message"]?.GetValue<string>() ?? _reply!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("delete_session", refusal);
    }

    [Then("the server is still running, and wrote nothing but JSON-RPC to standard output")]
    public void ThenTheServerIsStillRunning()
    {
        Assert.False(Server.HasExited);
        Assert.All(_stdout, line => Assert.Equal("2.0", JsonNode.Parse(line)!["jsonrpc"]!.GetValue<string>()));
    }

    private async Task<JsonNode> RequestAsync(string method, JsonObject parameters)
    {
        var id = ++_nextId;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        while (true)
        {
            var line = await Server.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("The MCP server closed standard output.");
            _stdout.Add(line);
            var message = JsonNode.Parse(line)!;
            if (message["id"]?.GetValue<int>() == id)
                return message;
        }
    }

    private async Task SendAsync(JsonObject message)
    {
        await Server.StandardInput.WriteLineAsync(message.ToJsonString());
        await Server.StandardInput.FlushAsync();
    }

    public void Dispose()
    {
        if (_server is null)
            return;
        if (!_server.HasExited)
        {
            _server.StandardInput.Close();
            if (!_server.WaitForExit(TimeSpan.FromSeconds(10)))
                _server.Kill(entireProcessTree: true);
        }
        _server.Dispose();
    }
}
