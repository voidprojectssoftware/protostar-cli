using Protostar.Cli.Auth;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Mcp;

/// <summary>Reads the user's backed-up sessions, memory and custom agents from the registry for <c>protostar mcp</c>, never changing anything.</summary>
internal interface IAgentHistoryService
{
    /// <summary>The registry's sessions matching <paramref name="query"/>, newest activity first.</summary>
    /// <remarks>Only what the registry holds: unlike <c>sessions list</c>, nothing on this machine is read.</remarks>
    Task<SessionListResult> ListSessionsAsync(string? registry, TranscriptListQuery query, CancellationToken cancellationToken);

    /// <summary>Finds a session by id or id prefix, as <c>sessions show</c> does, once the registry is known to redact what it serves.</summary>
    /// <remarks>
    /// A registry that does not list the <c>redacted-read</c> capability is refused before anything is read: it would
    /// ignore <c>redacted=true</c> and serve the stored lines, secrets included. The refusal is
    /// <see cref="ISessionReader.OpenAsync"/>'s, so <c>sessions show</c> makes the same one.
    /// </remarks>
    Task<SessionOpenResult> OpenSessionAsync(string? registry, string id, CancellationToken cancellationToken);

    /// <summary>Finds a session by id or id prefix and lists the skills it and its subagents used, with the versions the registry infers.</summary>
    /// <remarks>
    /// A registry that does not list the <c>session-skills</c> capability is refused, saying it needs updating; finding
    /// the session needs <c>redacted-read</c> too, as <see cref="OpenSessionAsync"/> does.
    /// </remarks>
    Task<SessionSkillsResult> GetSessionSkillsAsync(string? registry, string id, CancellationToken cancellationToken);

    /// <summary>A project's memory files, or the one at <paramref name="path"/>, with secrets redacted by the registry.</summary>
    /// <param name="project">The project's id, or text in its name, matched against the projects that have memory.</param>
    /// <remarks>Refused on a registry that does not list <c>memory-redacted-read</c>, for the same reason as <see cref="OpenSessionAsync"/>.</remarks>
    Task<MemoryReadResult> GetMemoryAsync(string? registry, string project, string? path, CancellationToken cancellationToken);

    /// <summary>One of the user's custom agents, its file with secrets redacted by the registry.</summary>
    /// <param name="name">The agent's exact name, as search shows it.</param>
    /// <param name="project">
    /// The agent's project: its id, or text in its name. <c>null</c> picks the one agent of that name, which must be
    /// unambiguous; when the name is both a personal and a project agent, or in several projects, the candidates come back.
    /// </param>
    /// <param name="version">The version to read; <c>null</c> for the current one.</param>
    /// <remarks>
    /// Refused on a registry that does not list <c>agent-content</c>, strictly, for the same reason as
    /// <see cref="OpenSessionAsync"/>: the redacted read is part of that capability.
    /// </remarks>
    Task<AgentReadResult> GetAgentAsync(string? registry, string name, string? project, int? version, CancellationToken cancellationToken);
}

/// <summary>Whether <see cref="IAgentHistoryService.GetAgentAsync"/> found the agent it was asked for.</summary>
internal enum AgentReadOutcome
{
    Found,

    /// <summary>The user has no agent of that name (in that project), or no such version.</summary>
    NotFound,

    /// <summary>The name matches agents in more than one scope; <see cref="AgentReadResult.Candidates"/> lists them.</summary>
    Ambiguous,

    /// <summary>The registry could not be asked; <see cref="AgentReadResult.Failure"/> says why.</summary>
    Failed,
}

/// <summary>The result of reading one custom agent.</summary>
/// <param name="Detail">For <see cref="AgentReadOutcome.Failed"/> or <see cref="AgentReadOutcome.NotFound"/>, the reason in words.</param>
/// <param name="ProjectName">The project of the agent found, for a project agent.</param>
/// <param name="Candidates">For <see cref="AgentReadOutcome.Ambiguous"/>, the agents of that name to choose from.</param>
internal sealed record AgentReadResult(
    AgentReadOutcome Outcome, SessionSyncFailure Failure, string? Detail, Uri? Registry, RemoteAgentContent? Content, string? ProjectName,
    IReadOnlyList<RegistryAgent> Candidates)
{
    public static AgentReadResult Fail(SessionSyncFailure failure, string? detail, Uri? registry) =>
        new(AgentReadOutcome.Failed, failure, detail, registry, null, null, []);

    public static AgentReadResult NotFound(string detail, Uri registry) =>
        new(AgentReadOutcome.NotFound, SessionSyncFailure.None, detail, registry, null, null, []);
}

/// <summary>The skills a session used, or why they could not be listed.</summary>
/// <param name="Session">The lookup of the session; its outcome says whether it was found, and why not.</param>
/// <param name="Skills">The skills, earliest first used first; empty unless the session was found and listed.</param>
internal sealed record SessionSkillsResult(SessionOpenResult Session, IReadOnlyList<SessionSkill> Skills);

/// <summary>Whether <see cref="IAgentHistoryService.GetMemoryAsync"/> found what it was asked for.</summary>
internal enum MemoryReadOutcome
{
    Found,

    /// <summary>No project with memory matches, or it has no file at that path.</summary>
    NotFound,

    /// <summary>The name matches more than one project; <see cref="MemoryReadResult.Candidates"/> lists them.</summary>
    Ambiguous,

    /// <summary>The registry could not be asked; <see cref="MemoryReadResult.Failure"/> says why.</summary>
    Failed,
}

/// <summary>The result of reading a project's memory.</summary>
/// <param name="Detail">For <see cref="MemoryReadOutcome.Failed"/> or <see cref="MemoryReadOutcome.NotFound"/>, the reason in words.</param>
/// <param name="Candidates">For <see cref="MemoryReadOutcome.Ambiguous"/> and a project not found, the projects that have memory to choose from.</param>
internal sealed record MemoryReadResult(
    MemoryReadOutcome Outcome, SessionSyncFailure Failure, string? Detail, Uri? Registry, RemoteMemoryContent? Content,
    IReadOnlyList<RemoteMemoryProject> Candidates)
{
    public static MemoryReadResult Fail(SessionSyncFailure failure, string? detail, Uri? registry) =>
        new(MemoryReadOutcome.Failed, failure, detail, registry, null, []);
}

/// <summary>Production <see cref="IAgentHistoryService"/>.</summary>
/// <remarks>
/// Every call resolves the registry and the stored sign-in afresh, so signing in while the server runs takes effect on
/// the next call, and checks through <see cref="IRegistryCapabilities"/> that the registry speaks this CLI's API and
/// serves the feature (<c>/v1/meta</c> is read once per server process).
/// Nothing here registers the machine or writes to the registry.
/// </remarks>
internal sealed class AgentHistoryService : IAgentHistoryService
{
    /// <summary>The capability that makes a memory download honour <c>redacted=true</c>.</summary>
    internal const string MemoryRedactedRead = RegistryCapability.MemoryRedactedRead;

    /// <summary>The capability that serves the skills a session used (registry#14).</summary>
    internal const string SessionSkills = RegistryCapability.SessionSkills;

    /// <summary>The capability that serves an agent's file, with <c>redacted=true</c> honoured.</summary>
    internal const string AgentContent = RegistryCapability.AgentContent;

    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryCapabilities _capabilities;
    private readonly ITranscriptApi _transcripts;
    private readonly IMemoryApi _memory;
    private readonly IAgentApi _agents;
    private readonly ISessionReader _reader;

    public AgentHistoryService(
        IAccessTokenProvider tokens, IRegistryCapabilities capabilities, ITranscriptApi transcripts, IMemoryApi memory, IAgentApi agents, ISessionReader reader)
    {
        _tokens = tokens;
        _capabilities = capabilities;
        _transcripts = transcripts;
        _memory = memory;
        _agents = agents;
        _reader = reader;
    }

    /// <inheritdoc />
    public async Task<SessionListResult> ListSessionsAsync(string? registry, TranscriptListQuery query, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(registry, RegistryCapability.Sessions, cancellationToken);
        if (connection.Failure != SessionSyncFailure.None)
            return new SessionListResult(connection.Failure, connection.Detail, connection.Registry, []);

        var listed = await _transcripts.ListAsync(connection.Registry!, connection.Token!, query, cancellationToken);
        return listed.Ok
            ? new SessionListResult(SessionSyncFailure.None, null, connection.Registry, [.. listed.Value!.Select(t => new ListedSession(t, null))])
            : new SessionListResult(SessionSyncFailure.RegistryUnreachable, listed.Error, connection.Registry, []);
    }

    /// <inheritdoc />
    public Task<SessionOpenResult> OpenSessionAsync(string? registry, string id, CancellationToken cancellationToken) =>
        _reader.OpenAsync(registry, null, id, cancellationToken);

    /// <inheritdoc />
    public async Task<SessionSkillsResult> GetSessionSkillsAsync(string? registry, string id, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(registry, SessionSkills, cancellationToken);
        if (connection.Failure != SessionSyncFailure.None)
            return new SessionSkillsResult(SessionOpenResult.Fail(connection.Failure, connection.Detail, connection.Registry), []);

        var opened = await _reader.OpenAsync(registry, null, id, cancellationToken);
        if (opened.Outcome != SessionOpenOutcome.Found)
            return new SessionSkillsResult(opened, []);

        var transcript = opened.Session!.Detail.Transcript;
        var skills = await _transcripts.GetSkillsAsync(connection.Registry!, connection.Token!, transcript.Harness, transcript.Key, cancellationToken);
        return skills.Ok
            ? new SessionSkillsResult(opened, skills.Value!)
            : new SessionSkillsResult(SessionOpenResult.Fail(SessionSyncFailure.RegistryUnreachable, skills.Error, connection.Registry), []);
    }

    /// <inheritdoc />
    public async Task<MemoryReadResult> GetMemoryAsync(string? registry, string project, string? path, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(registry, MemoryRedactedRead, cancellationToken);
        if (connection.Failure != SessionSyncFailure.None)
            return MemoryReadResult.Fail(connection.Failure, connection.Detail, connection.Registry);
        var (url, token) = (connection.Registry!, connection.Token!);

        var projects = await _memory.ListMemoryProjectsAsync(url, token, cancellationToken);
        if (!projects.Ok)
            return MemoryReadResult.Fail(SessionSyncFailure.RegistryUnreachable, projects.Error, url);

        // The registry's rule for a project filter: an id matches exactly, anything else is text in the name, ignoring case.
        var matches = Guid.TryParse(project, out var id)
            ? projects.Value!.Where(p => p.ProjectId == id).ToList()
            : projects.Value!.Where(p => p.ProjectName.Contains(project, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return new MemoryReadResult(MemoryReadOutcome.NotFound, SessionSyncFailure.None, $"No project with memory matches '{project}'.", url, null, projects.Value!);
        if (matches.Count > 1)
        {
            // A name that is exactly one project's whole name picks it, so a project whose name another contains stays reachable.
            var exact = matches.Where(p => string.Equals(p.ProjectName, project, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count != 1)
                return new MemoryReadResult(MemoryReadOutcome.Ambiguous, SessionSyncFailure.None, null, url, null, matches);
            matches = exact;
        }

        var content = await _memory.GetRedactedMemoryContentAsync(url, token, matches[0].ProjectId, path, cancellationToken);
        if (content.Ok)
            return new MemoryReadResult(MemoryReadOutcome.Found, SessionSyncFailure.None, null, url, content.Value, []);
        return content.MemoryUnknown
            ? new MemoryReadResult(MemoryReadOutcome.NotFound, SessionSyncFailure.None, content.Error, url, null, [])
            : MemoryReadResult.Fail(SessionSyncFailure.RegistryUnreachable, content.Error, url);
    }

    /// <inheritdoc />
    public async Task<AgentReadResult> GetAgentAsync(string? registry, string name, string? project, int? version, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(registry, AgentContent, cancellationToken);
        if (connection.Failure != SessionSyncFailure.None)
            return AgentReadResult.Fail(connection.Failure, connection.Detail, connection.Registry);
        var (url, token) = (connection.Registry!, connection.Token!);

        // The list says which scopes have an agent of that name, so a project can be given by name and left out when only one has it.
        var listed = await _agents.ListAgentsAsync(url, token, cancellationToken);
        if (listed.Agents is not { } agents)
            return AgentReadResult.Fail(SessionSyncFailure.RegistryUnreachable, listed.Error, url);

        var named = agents.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
        var matches = project is null ? named
            : Guid.TryParse(project, out var id) ? named.Where(a => a.ProjectId == id).ToList()
            : named.Where(a => a.ProjectName?.Contains(project, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (matches.Count == 0)
        {
            var where = project is null ? "" : $" in a project matching '{project}'";
            return AgentReadResult.NotFound($"You have no agent named '{name}'{where}. Use search with kind agent to find one.", url);
        }
        if (matches.Count > 1)
            return new AgentReadResult(AgentReadOutcome.Ambiguous, SessionSyncFailure.None, null, url, null, null, matches);

        var agent = matches[0];
        var content = await _agents.GetRedactedAgentContentAsync(url, token, agent.Name, agent.ProjectId, version, cancellationToken);
        if (content.Ok)
            return new AgentReadResult(AgentReadOutcome.Found, SessionSyncFailure.None, null, url, content.Value, agent.ProjectName, []);
        return content.AgentUnknown
            ? AgentReadResult.NotFound(content.Error!, url)
            : AgentReadResult.Fail(SessionSyncFailure.RegistryUnreachable, content.Error, url);
    }

    private sealed record Connection(SessionSyncFailure Failure, string? Detail, Uri? Registry, string? Token);

    // The registry, a current access token, and a registry that speaks this CLI's API and has `capability`.
    private async Task<Connection> ConnectAsync(string? registryOption, string capability, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(registryOption);
        }
        catch (FormatException ex)
        {
            return new Connection(SessionSyncFailure.InvalidRegistry, ex.Message, null, null);
        }

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } stored })
            return new Connection(SessionSyncService.SessionFailure(lookup.State), null, registry, null);

        var check = await (Strict(capability)
            ? _capabilities.RequireListedAsync(registry, cancellationToken, capability)
            : _capabilities.RequireAsync(registry, cancellationToken, capability));
        if (!check.Ok)
            return new Connection(SessionSyncService.CheckFailure(check), check.Detail, registry, null);

        return new Connection(SessionSyncFailure.None, null, registry, stored.AccessToken);
    }

    // Strict: a registry too old to list capabilities is refused, not asked. The redacted reads must never be assumed,
    // because a registry that ignores `redacted=true` serves the stored lines, secrets included, and refusing is the
    // lesser harm. Session skills and agent content came after the list itself, so a registry without the list cannot
    // have them. (redacted-read is checked by ISessionReader.OpenAsync, for sessions show too.)
    private static bool Strict(string capability) => capability is MemoryRedactedRead or SessionSkills or AgentContent;
}
