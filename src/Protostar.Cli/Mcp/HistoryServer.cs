using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Spectre.Console;

namespace Protostar.Cli.Mcp;

/// <summary>Runs the MCP server for <see cref="HistoryTools"/> on standard input and output until the client disconnects.</summary>
/// <remarks>
/// Standard output is the MCP channel, so before the server starts, <see cref="Console.Out"/> and Spectre's console
/// are pointed at standard error: anything else that prints by accident lands there, never in the protocol stream.
/// The server writes to the standard output stream it opened first. It logs nothing.
/// </remarks>
internal static class HistoryServer
{
    private const string Instructions =
        "Read-only access to the developer's backed-up coding sessions and agent memory, from every project and machine. " +
        "Use search to find a past session or memory by what it says, get_session to read how the work was done, list_sessions to browse, " +
        "and get_memory to read a project's memory. Secrets are redacted.";

    /// <summary>Serves until standard input closes or <paramref name="cancellationToken"/> fires, and returns the exit code.</summary>
    public static async Task<int> RunAsync(HistoryTools tools, CancellationToken cancellationToken)
    {
        var protocol = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "protostar", Version = CliInfo.Version },
            ServerInstructions = Instructions,
            ToolCollection = [.. Tools(tools)],
        };

        await using var transport = new StreamServerTransport(Console.OpenStandardInput(), protocol, "protostar");
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken);
        return 0;
    }

    /// <summary>One tool per <see cref="McpServerToolAttribute"/> method of <paramref name="tools"/>, bound to it.</summary>
    internal static IEnumerable<McpServerTool> Tools(HistoryTools tools) =>
        typeof(HistoryTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, tools));
}
