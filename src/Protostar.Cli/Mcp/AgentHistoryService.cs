using Protostar.Cli.Auth;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Mcp;

/// <summary>Reads the user's backed-up sessions and memory from the registry for <c>protostar mcp</c>, never changing anything.</summary>
internal interface IAgentHistoryService
{
    /// <summary>The registry's sessions matching <paramref name="query"/>, newest activity first.</summary>
    /// <remarks>Only what the registry holds: unlike <c>sessions list</c>, nothing on this machine is read.</remarks>
    Task<SessionListResult> ListSessionsAsync(string? registry, TranscriptListQuery query, CancellationToken cancellationToken);

    /// <summary>Finds a session by id or id prefix, as <c>sessions show</c> does, once the registry is known to redact what it serves.</summary>
    /// <remarks>
    /// A registry that does not list the <c>redacted-read</c> capability is refused before anything is read: it would
    /// ignore <c>redacted=true</c> and serve the stored lines, secrets included.
    /// </remarks>
    Task<SessionOpenResult> OpenSessionAsync(string? registry, string id, CancellationToken cancellationToken);

    /// <summary>Finds a session by id or id prefix and lists the skills it and its subagents used, with the versions the registry infers.</summary>
    /// <remarks>A registry that does not list the <c>session-skills</c> capability is refused, saying it needs updating.</remarks>
    Task<SessionSkillsResult> GetSessionSkillsAsync(string? registry, string id, CancellationToken cancellationToken);

    /// <summary>A project's memory files, or the one at <paramref name="path"/>, with secrets redacted by the registry.</summary>
    /// <param name="project">The project's id, or text in its name, matched against the projects that have memory.</param>
    /// <remarks>Refused on a registry that does not list <c>memory-redacted-read</c>, for the same reason as <see cref="OpenSessionAsync"/>.</remarks>
    Task<MemoryReadResult> GetMemoryAsync(string? registry, string project, string? path, CancellationToken cancellationToken);
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
/// the next call, and reads <c>/v1/meta</c> to check the registry speaks this CLI's API and serves the feature.
/// Nothing here registers the machine or writes to the registry.
/// </remarks>
internal sealed class AgentHistoryService : IAgentHistoryService
{
    /// <summary>The capability that makes a transcript download honour <c>redacted=true</c> (registry#72).</summary>
    internal const string RedactedRead = "redacted-read";

    /// <summary>The capability that makes a memory download honour <c>redacted=true</c>.</summary>
    internal const string MemoryRedactedRead = "memory-redacted-read";

    /// <summary>The capability that serves the skills a session used (registry#14).</summary>
    internal const string SessionSkills = "session-skills";

    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _meta;
    private readonly ITranscriptApi _transcripts;
    private readonly IMemoryApi _memory;
    private readonly ISessionReader _reader;

    public AgentHistoryService(IAccessTokenProvider tokens, IRegistryApi meta, ITranscriptApi transcripts, IMemoryApi memory, ISessionReader reader)
    {
        _tokens = tokens;
        _meta = meta;
        _transcripts = transcripts;
        _memory = memory;
        _reader = reader;
    }

    /// <inheritdoc />
    public async Task<SessionListResult> ListSessionsAsync(string? registry, TranscriptListQuery query, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(registry, null, cancellationToken);
        if (connection.Failure != SessionSyncFailure.None)
            return new SessionListResult(connection.Failure, connection.Detail, connection.Registry, []);

        var listed = await _transcripts.ListAsync(connection.Registry!, connection.Token!, query, cancellationToken);
        return listed.Ok
            ? new SessionListResult(SessionSyncFailure.None, null, connection.Registry, [.. listed.Value!.Select(t => new ListedSession(t, null))])
            : new SessionListResult(SessionSyncFailure.RegistryUnreachable, listed.Error, connection.Registry, []);
    }

    /// <inheritdoc />
    public async Task<SessionOpenResult> OpenSessionAsync(string? registry, string id, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(registry, RedactedRead, cancellationToken);
        return connection.Failure != SessionSyncFailure.None
            ? SessionOpenResult.Fail(connection.Failure, connection.Detail, connection.Registry)
            : await _reader.OpenAsync(registry, null, id, cancellationToken);
    }

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

    private sealed record Connection(SessionSyncFailure Failure, string? Detail, Uri? Registry, string? Token);

    // The registry, a current access token, and a registry that speaks this CLI's API and lists `capability` when one is named.
    private async Task<Connection> ConnectAsync(string? registryOption, string? capability, CancellationToken cancellationToken)
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

        var meta = await _meta.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return new Connection(SessionSyncFailure.RegistryUnreachable, null, registry, null);
        if (ApiCompatibility.Check(meta) is { } incompatible)
            return new Connection(SessionSyncFailure.Incompatible, incompatible, registry, null);
        // A registry too old to list capabilities is refused too: it cannot be shown to redact, and predates session skills.
        if (capability is not null && meta.Capabilities?.Contains(capability, StringComparer.Ordinal) != true)
            return new Connection(SessionSyncFailure.Incompatible, Refusal(capability), registry, null);

        return new Connection(SessionSyncFailure.None, null, registry, stored.AccessToken);
    }

    private static string Refusal(string capability) => capability switch
    {
        SessionSkills => "This registry cannot list the skills a session used. Update the registry.",
        MemoryRedactedRead => "This registry cannot serve memory with secrets redacted. Update the registry.",
        _ => "This registry cannot serve sessions with secrets redacted. Update the registry.",
    };
}
