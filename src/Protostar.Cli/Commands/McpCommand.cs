using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Mcp;
using Protostar.Cli.Search;
using Protostar.Cli.Sessions;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary><c>protostar mcp</c>: a local MCP server on stdio that lets coding agents search and read the user's history and memory.</summary>
/// <remarks>
/// <para>
/// It signs in with the CLI's stored session and the registry the CLI resolves (<c>--registry</c>, then
/// <c>$PROTOSTAR_REGISTRY_URL</c>, then the saved registry), afresh on every call. It serves no write tools.
/// See <see cref="HistoryServer"/> for how standard output is kept for the protocol alone.
/// </para>
/// </remarks>
internal sealed class McpCommand : AsyncCommand<McpCommand.Settings>
{
    private readonly ISearchService _search;
    private readonly IAgentHistoryService _history;
    private readonly ISessionReader _reader;
    private readonly IHarnessCatalog _catalog;

    public McpCommand(ISearchService search, IAgentHistoryService history, ISessionReader reader, IHarnessCatalog catalog)
    {
        _search = search;
        _history = history;
        _reader = reader;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
        public string? Registry { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation) =>
        HistoryServer.RunAsync(new HistoryTools(_search, _history, _reader, _catalog, settings.Registry), cancellation);
}
