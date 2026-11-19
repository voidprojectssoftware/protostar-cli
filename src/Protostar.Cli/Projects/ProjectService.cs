using Protostar.Cli.Auth;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Projects;

/// <summary>Shows which project a folder is in, and links or unlinks it: <c>protostar project</c>.</summary>
/// <remarks>
/// Facts come from <see cref="IProjectFactsResolver"/>, the resolver session backups use, and the registry answers
/// with the matching that files sessions. So what this shows is where the folder's sessions go.
/// </remarks>
internal interface IProjectService
{
    /// <summary>What can be worked out about <paramref name="directory"/> locally, and the registry's project for it when reachable.</summary>
    Task<ProjectShowResult> ShowAsync(string? registry, string directory, CancellationToken cancellationToken);

    /// <summary>Links the folder of <paramref name="directory"/> on this machine to <paramref name="project"/>, an id or exact name.</summary>
    Task<ProjectLinkResult> LinkAsync(string? registry, string directory, string project, CancellationToken cancellationToken);

    /// <summary>Unlinks the folder of <paramref name="directory"/> on this machine.</summary>
    Task<ProjectLinkResult> UnlinkAsync(string? registry, string directory, CancellationToken cancellationToken);
}

/// <summary>Why the registry's side of a project command is missing.</summary>
internal enum ProjectFailure
{
    None,
    InvalidRegistry,
    NotLoggedIn,
    SessionExpired,

    /// <summary>The registry could not be reached or refused the request; <c>Detail</c> says which.</summary>
    Failed,
}

/// <summary>A folder's local facts and, when the registry answered, its project.</summary>
/// <param name="Facts">What git says about the folder, worked out locally.</param>
/// <param name="Registry">The registry asked, when it could be resolved.</param>
/// <param name="Project">The registry's answer, or <c>null</c> on failure.</param>
/// <param name="Failure">Why there is no answer.</param>
/// <param name="Detail">The failure's message.</param>
internal sealed record ProjectShowResult(ProjectFacts Facts, Uri? Registry, RemoteProject? Project, ProjectFailure Failure, string? Detail);

/// <summary>A link or unlink's result.</summary>
/// <param name="Facts">What git says about the folder.</param>
/// <param name="Registry">The registry asked.</param>
/// <param name="Change">What changed, or <c>null</c> on failure.</param>
/// <param name="Failure">Why it did not happen.</param>
/// <param name="Detail">The failure's message.</param>
internal sealed record ProjectLinkResult(ProjectFacts Facts, Uri? Registry, ProjectLinkChange? Change, ProjectFailure Failure, string? Detail);

/// <summary>Production <see cref="IProjectService"/>.</summary>
internal sealed class ProjectService : IProjectService
{
    private readonly IProjectFactsResolver _facts;
    private readonly IAccessTokenProvider _tokens;
    private readonly IProjectApi _api;
    private readonly IMachineIdentity _machine;

    public ProjectService(IProjectFactsResolver facts, IAccessTokenProvider tokens, IProjectApi api, IMachineIdentity machine)
    {
        _facts = facts;
        _tokens = tokens;
        _api = api;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<ProjectShowResult> ShowAsync(string? registry, string directory, CancellationToken cancellationToken)
    {
        var facts = _facts.Resolve(Path.GetFullPath(directory));
        var (uri, token, failure, detail) = await ConnectAsync(registry, cancellationToken);
        if (failure != ProjectFailure.None)
            return new ProjectShowResult(facts, uri, null, failure, detail);

        var resolved = await _api.ResolveProjectAsync(uri!, token!, _machine.Get().Id, facts, cancellationToken);
        return resolved.Ok
            ? new ProjectShowResult(facts, uri, resolved.Value, ProjectFailure.None, null)
            : new ProjectShowResult(facts, uri, null, ProjectFailure.Failed, resolved.Error);
    }

    /// <inheritdoc />
    public Task<ProjectLinkResult> LinkAsync(string? registry, string directory, string project, CancellationToken cancellationToken) =>
        ChangeAsync(registry, directory, (uri, token, machine, facts) => _api.LinkFolderAsync(uri, token, machine, facts, project, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<ProjectLinkResult> UnlinkAsync(string? registry, string directory, CancellationToken cancellationToken) =>
        ChangeAsync(registry, directory, (uri, token, machine, facts) => _api.UnlinkFolderAsync(uri, token, machine, facts, cancellationToken), cancellationToken);

    private async Task<ProjectLinkResult> ChangeAsync(
        string? registry, string directory, Func<Uri, string, Guid, ProjectFacts, Task<ApiResult<ProjectLinkChange>>> change,
        CancellationToken cancellationToken)
    {
        var facts = _facts.Resolve(Path.GetFullPath(directory));
        var (uri, token, failure, detail) = await ConnectAsync(registry, cancellationToken);
        if (failure != ProjectFailure.None)
            return new ProjectLinkResult(facts, uri, null, failure, detail);

        var changed = await change(uri!, token!, _machine.Get().Id, facts);
        return changed.Ok
            ? new ProjectLinkResult(facts, uri, changed.Value, ProjectFailure.None, null)
            : new ProjectLinkResult(facts, uri, null, ProjectFailure.Failed, changed.Error);
    }

    private async Task<(Uri? Registry, string? Token, ProjectFailure Failure, string? Detail)> ConnectAsync(string? registry, CancellationToken cancellationToken)
    {
        Uri uri;
        try
        {
            uri = RegistryEndpoint.Resolve(registry);
        }
        catch (FormatException ex)
        {
            return (null, null, ProjectFailure.InvalidRegistry, ex.Message);
        }

        var stored = await _tokens.GetSessionAsync(uri, cancellationToken);
        if (stored is null)
            return (uri, null, ProjectFailure.NotLoggedIn, null);
        if (stored.IsExpired)
            return (uri, null, ProjectFailure.SessionExpired, null);
        return (uri, stored.AccessToken, ProjectFailure.None, null);
    }
}
