using Protostar.Cli.Auth;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;

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
    /// <remarks>
    /// The folder's project skills may move with it; this machine's skill ledger records of them then name their new
    /// project (<see cref="MovedSkillLedger.Follow"/>). The same holds for <see cref="UnlinkAsync"/>.
    /// </remarks>
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
    private readonly ISkillLedger _ledger;

    public ProjectService(IProjectFactsResolver facts, IAccessTokenProvider tokens, IProjectApi api, IMachineIdentity machine, ISkillLedger ledger)
    {
        _facts = facts;
        _tokens = tokens;
        _api = api;
        _machine = machine;
        _ledger = ledger;
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

        // Where the folder belongs before the change, so this machine's records of the skills that move with it can
        // follow them. If the registry cannot say, the records are left for the next `skills remote` or `pull` to fix.
        var machine = _machine.Get().Id;
        var before = await _api.ResolveProjectAsync(uri!, token!, machine, facts, cancellationToken);
        var changed = await change(uri!, token!, machine, facts);
        if (!changed.Ok)
            return new ProjectLinkResult(facts, uri, null, ProjectFailure.Failed, changed.Error);

        if (changed.Value!.MovedSkills.Count > 0 && before.Value?.ProjectId is { } from && changed.Value.Project.ProjectId is { } to)
            MovedSkillLedger.Follow(_ledger, uri!, changed.Value.MovedSkills, from, to);
        return new ProjectLinkResult(facts, uri, changed.Value, ProjectFailure.None, null);
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

        var lookup = await _tokens.GetSessionAsync(uri, cancellationToken);
        if (lookup is { State: SessionState.Current, Session: { } stored })
            return (uri, stored.AccessToken, ProjectFailure.None, null);
        return lookup.State switch
        {
            SessionState.None => (uri, null, ProjectFailure.NotLoggedIn, null),
            // Failed with no detail reads "could not reach" the registry.
            SessionState.RegistryUnreachable => (uri, null, ProjectFailure.Failed, null),
            _ => (uri, null, ProjectFailure.SessionExpired, null),
        };
    }
}
