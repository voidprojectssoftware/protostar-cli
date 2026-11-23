using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Search;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary><c>protostar search &lt;query&gt;</c>: finds backed-up sessions and memory, from every machine, by what they say.</summary>
internal sealed class SearchCommand : AsyncCommand<SearchCommand.Settings>
{
    // The registry's limits (SearchService.MaxQueryLength and MaxLimit), checked here so a bad value is a usage
    // error rather than a refused request.
    private const int MaxQueryLength = 500;
    private const int MaxLimit = 100;

    private readonly ISearchService _search;
    private readonly IHarnessCatalog _catalog;

    public SearchCommand(ISearchService search, IHarnessCatalog catalog)
    {
        _search = search;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [Description("The words to find. All are required; \"quoted words\" are a phrase, `or` gives an alternative, -word excludes. Quote the whole query for the shell.")]
        public string Query { get; init; } = "";

        [CommandOption("-k|--kind <KIND>")]
        [Description("Only `session` (sessions and their subagents) or `memory`, or `agent` to search your custom agents instead. Sessions and memory by default.")]
        public string? Kind { get; init; }

        [CommandOption("-p|--project <ID|NAME|.>")]
        [Description("Only this project: its id, text in its name (which may match several), or `.` for this folder's project.")]
        public string? Project { get; init; }

        [CommandOption("-n|--limit <N>")]
        [Description("How many to show (1 to 100). Defaults to 20.")]
        public int Limit { get; init; } = 20;

        [CommandOption("--json")]
        [Description("Print one JSON array of hits instead of a list, for agents and scripts.")]
        public bool Json { get; init; }

        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
        public string? Registry { get; init; }

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Query) ? ValidationResult.Error("Give the words to search for.")
            : Query.Length > MaxQueryLength ? ValidationResult.Error($"The query may be at most {MaxQueryLength} characters.")
            : Kind is not (null or "session" or "memory" or SearchService.AgentKind) ? ValidationResult.Error("--kind must be session, memory or agent.")
            : Limit is < 1 or > MaxLimit ? ValidationResult.Error($"--limit must be 1 to {MaxLimit}.")
            : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var request = new SearchRequest(settings.Registry, settings.Query, settings.Kind, settings.Project, settings.Limit, Environment.CurrentDirectory);
        return SearchPresenter.Render(await _search.SearchAsync(request, cancellation), request, _catalog, settings.Json);
    }
}
