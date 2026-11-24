using Protostar.Cli.Agents;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
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
    /// project (<see cref="MovedSkillLedger.Follow"/>), and so do its project agents' records (<see cref="MovedAgentLedger.Follow"/>).
    /// The same holds for <see cref="UnlinkAsync"/>. Project skills that synced as the earlier project's and stay behind are
    /// listed in the result with what becomes of each (<see cref="ProjectLinkResult.LeftSkills"/>), and so are project agents
    /// (<see cref="ProjectLinkResult.LeftAgents"/>).
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

    /// <summary>The registry speaks another API major or has no projects; <c>Detail</c> says which.</summary>
    Incompatible,
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
/// <param name="LeftSkills">
/// The project skills in the folder that synced as the folder's earlier project's and did not move with it, so stay
/// behind (<see cref="LinkedSkillCopy"/>); <c>null</c> when the project did not change or none were left.
/// </param>
/// <param name="LeftAgents">
/// The project agents in the folder that synced as the folder's earlier project's and did not move with it, so stay
/// behind (<see cref="LinkedAgentCopy"/>); <c>null</c> when the project did not change or none were left.
/// </param>
/// <param name="LeftFrom">The earlier project's name, when <paramref name="LeftSkills"/> or <paramref name="LeftAgents"/> is set.</param>
internal sealed record ProjectLinkResult(
    ProjectFacts Facts, Uri? Registry, ProjectLinkChange? Change, ProjectFailure Failure, string? Detail,
    IReadOnlyList<SkillLeftBehind>? LeftSkills = null, string? LeftFrom = null, IReadOnlyList<AgentLeftBehind>? LeftAgents = null);

/// <summary>Production <see cref="IProjectService"/>.</summary>
internal sealed class ProjectService : IProjectService
{
    private readonly IProjectFactsResolver _facts;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryCapabilities _capabilities;
    private readonly IProjectApi _api;
    private readonly IMachineIdentity _machine;
    private readonly ISkillLedger _ledger;
    private readonly IAgentLedger _agentLedger;
    private readonly ISkillService _skills;
    private readonly IRegistryApi _registry;
    private readonly IHarnessCatalog _catalog;
    private readonly IAgentApi _agents;

    public ProjectService(
        IProjectFactsResolver facts, IAccessTokenProvider tokens, IRegistryCapabilities capabilities, IProjectApi api, IMachineIdentity machine, ISkillLedger ledger,
        IAgentLedger agentLedger, ISkillService skills, IRegistryApi registry,
        IHarnessCatalog catalog, IAgentApi agents)
    {
        _catalog = catalog;
        _agents = agents;
        _agentLedger = agentLedger;
        _skills = skills;
        _registry = registry;
        _facts = facts;
        _tokens = tokens;
        _capabilities = capabilities;
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
        if (changed.Value.MovedAgents.Count > 0 && before.Value?.ProjectId is { } agentsFrom && changed.Value.Project.ProjectId is { } agentsTo)
            MovedAgentLedger.Follow(_agentLedger, uri!, changed.Value.MovedAgents, agentsFrom, agentsTo);

        var left = before.Value is { ProjectId: { } earlier } && earlier != changed.Value.Project.ProjectId
            ? await LeftBehindAsync(uri!, token!, directory, earlier, changed.Value, cancellationToken)
            : [];
        var leftAgents = before.Value is { ProjectId: { } agentsEarlier } && agentsEarlier != changed.Value.Project.ProjectId
            ? await AgentsLeftBehindAsync(uri!, token!, directory, agentsEarlier, changed.Value, cancellationToken)
            : [];
        return new ProjectLinkResult(facts, uri, changed.Value, ProjectFailure.None, null,
            left.Count > 0 ? left : null, left.Count + leftAgents.Count > 0 ? before.Value!.Name : null, leftAgents.Count > 0 ? leftAgents : null);
    }

    // The folder's project skills whose records still name the project it was in: pulled while linked there, they stay
    // behind (only skills pushed from the folder move, registry#43), and the rule push, pull and remote share settles what
    // becomes of each. Worked out here so the user hears it when the move happens, not first from a hook nobody sees
    // (cli#129). Costs one list request, and only when there is such a copy.
    private async Task<IReadOnlyList<SkillLeftBehind>> LeftBehindAsync(
        Uri registry, string token, string directory, Guid earlier, ProjectLinkChange change, CancellationToken cancellationToken)
    {
        var copies = _skills.Discover(null, null, Path.GetFullPath(directory)).Skills
            .Where(s => s.Scope == SkillScope.Project && !change.MovedSkills.Contains(s.Name, StringComparer.Ordinal))
            .Select(s => (Skill: s, Recorded: _ledger.Get(registry, s.Path)))
            .Where(c => c.Recorded?.ProjectId == earlier)
            .ToList();
        if (copies.Count == 0)
            return [];

        var list = await _registry.ListSkillsAsync(registry, token, cancellationToken);
        return copies
            .Select(c => new SkillLeftBehind(c.Skill.Name, LinkedSkillCopy.FateOf(c.Recorded!, HashOrNull(c.Skill.Path), change.Project.ProjectId, list)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The folder's project agents whose records still name the project it was in, settled by the agents' copy of the rule
    // (LinkedAgentCopy), as LeftBehindAsync does for skills. Costs one list request, and only when there is such a copy.
    private async Task<IReadOnlyList<AgentLeftBehind>> AgentsLeftBehindAsync(
        Uri registry, string token, string directory, Guid earlier, ProjectLinkChange change, CancellationToken cancellationToken)
    {
        var start = Path.GetFullPath(directory);
        var copies = _catalog.All
            .Where(h => h is IAgentCapability)
            .SelectMany(h => h.TryLocate(null, out var location) ? ((IAgentCapability)h).DiscoverAgents(location, start) : [])
            .Where(a => a.Scope == AgentScope.Project && a.Name is { } name && !change.MovedAgents.Contains(name, StringComparer.Ordinal))
            .Select(a => (Agent: a, Recorded: _agentLedger.Get(registry, a.Path)))
            .Where(c => c.Recorded is { ContentHash.Length: > 0 } recorded && recorded.ProjectId == earlier)
            .ToList();
        if (copies.Count == 0)
            return [];

        var list = await _agents.ListAgentsAsync(registry, token, cancellationToken);
        return copies
            .Select(c => new AgentLeftBehind(c.Agent.Name!, LinkedAgentCopy.FateOf(c.Recorded!, AgentHashOrNull(c.Agent.Path), change.Project.ProjectId, list)))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? AgentHashOrNull(string path)
    {
        try
        {
            return AgentPushService.Hash(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // An unreadable copy hashes to nothing, which no record matches: it counts as changed, as push would find it.
    private static string? HashOrNull(string path)
    {
        try
        {
            return SkillFileReader.Hash(SkillFileReader.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        if (lookup is not { State: SessionState.Current, Session: { } stored })
        {
            return lookup.State switch
            {
                SessionState.None => (uri, null, ProjectFailure.NotLoggedIn, null),
                // Failed with no detail reads "could not reach" the registry.
                SessionState.RegistryUnreachable => (uri, null, ProjectFailure.Failed, null),
                _ => (uri, null, ProjectFailure.SessionExpired, null),
            };
        }

        var check = await _capabilities.RequireAsync(uri, cancellationToken, RegistryCapability.Projects);
        return check.Failure switch
        {
            RegistryCheckFailure.None => (uri, stored.AccessToken, ProjectFailure.None, null),
            RegistryCheckFailure.Unreachable => (uri, null, ProjectFailure.Failed, null),
            _ => (uri, null, ProjectFailure.Incompatible, check.Detail),
        };
    }
}
