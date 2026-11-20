using Protostar.Cli.Auth;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Search;

/// <summary>Searches the user's backed-up sessions and memory in the registry: <c>protostar search</c>.</summary>
internal interface ISearchService
{
    /// <summary>The hits for <paramref name="request"/>, best match first, or why there are none to give.</summary>
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

/// <summary>A <c>protostar search</c>.</summary>
/// <param name="Registry">The <c>--registry</c> option, or <c>null</c> for the default.</param>
/// <param name="Text">The query.</param>
/// <param name="Kind"><c>session</c> or <c>memory</c>, or <c>null</c> for both.</param>
/// <param name="Project">A project id or name text, <c>.</c> for the current folder's project, or <c>null</c> for every project.</param>
/// <param name="Limit">The most hits, 1 to 100.</param>
/// <param name="Directory">The folder <c>.</c> stands for.</param>
internal sealed record SearchRequest(string? Registry, string Text, string? Kind, string? Project, int Limit, string Directory);

/// <summary>A search's hits, or why it could not run.</summary>
/// <param name="Failure">Why the search did not run, or <see cref="SessionSyncFailure.None"/>.</param>
/// <param name="Detail">The failure's message, when it has one of its own.</param>
/// <param name="Registry">The registry asked, when it could be resolved.</param>
/// <param name="Hits">The hits, best first; empty on failure or when nothing matched.</param>
/// <param name="FolderProject">For <c>--project .</c>, the name of the project the folder resolved to.</param>
internal sealed record SearchResult(
    SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<SearchHit> Hits, string? FolderProject = null);

/// <summary>Production <see cref="ISearchService"/>.</summary>
/// <remarks>
/// <para>
/// Read-only: it never registers the machine or changes the registry. The registry does the searching, the
/// ranking and the redaction; nothing is read from disk but the git facts of the folder <c>--project .</c> names.
/// </para>
/// <para>
/// The capability check (<see cref="IRegistryCapabilities.RequireAsync"/>): a registry that lists capabilities without
/// <c>search</c> is refused without asking it to search. One that lists none predates the list, so it is asked anyway,
/// and a bare 404 from it means the same thing (<see cref="ISearchApi.RegistryTooOldForSearch"/>).
/// </para>
/// <para>
/// <c>--project .</c> is resolved with the resolver session backups use, so it names the project this folder's
/// sessions are filed under, and is sent as that project's id. Any other value is passed through as given, because
/// the registry matches a name by substring and may match several projects: deciding which here would disagree with
/// <c>sessions list</c>. A folder whose project the registry has not made yet has nothing backed up, so it gives no hits.
/// </para>
/// </remarks>
internal sealed class SearchService : ISearchService
{
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryCapabilities _capabilities;
    private readonly ISearchApi _api;
    private readonly IProjectFactsResolver _facts;
    private readonly IProjectApi _projects;
    private readonly IMachineIdentity _machine;

    public SearchService(
        IAccessTokenProvider tokens, IRegistryCapabilities capabilities, ISearchApi api, IProjectFactsResolver facts, IProjectApi projects, IMachineIdentity machine)
    {
        _tokens = tokens;
        _capabilities = capabilities;
        _api = api;
        _facts = facts;
        _projects = projects;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(request.Registry);
        }
        catch (FormatException ex)
        {
            return new SearchResult(SessionSyncFailure.InvalidRegistry, ex.Message, null, []);
        }

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } stored })
        {
            var failure = lookup.State switch
            {
                SessionState.None => SessionSyncFailure.NotLoggedIn,
                SessionState.RegistryUnreachable => SessionSyncFailure.RegistryUnreachable,
                _ => SessionSyncFailure.SessionExpired,
            };
            return new SearchResult(failure, null, registry, []);
        }

        var check = await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Search);
        if (!check.Ok)
            return new SearchResult(SessionSyncService.CheckFailure(check), check.Detail, registry, []);

        var project = request.Project;
        string? folderProject = null;
        if (project == ".")
        {
            var facts = _facts.Resolve(Path.GetFullPath(request.Directory));
            var resolved = await _projects.ResolveProjectAsync(registry, stored.AccessToken, _machine.Get().Id, facts, cancellationToken);
            if (!resolved.Ok)
                return new SearchResult(SessionSyncFailure.RegistryUnreachable, resolved.Error, registry, []);
            folderProject = resolved.Value!.Name;
            if (resolved.Value.ProjectId is not { } id)
                return new SearchResult(SessionSyncFailure.None, null, registry, [], folderProject);
            project = id.ToString();
        }

        var hits = await _api.SearchAsync(registry, stored.AccessToken, new SearchQuery(request.Text, request.Kind, project, request.Limit), cancellationToken);
        if (hits.Ok)
            return new SearchResult(SessionSyncFailure.None, null, registry, hits.Value!, folderProject);
        return hits.Error == ISearchApi.RegistryTooOldForSearch
            ? new SearchResult(SessionSyncFailure.Incompatible, hits.Error, registry, [])
            : new SearchResult(SessionSyncFailure.RegistryUnreachable, hits.Error, registry, []);
    }
}
