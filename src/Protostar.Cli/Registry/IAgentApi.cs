using System.Text.Json.Serialization;

namespace Protostar.Cli.Registry;

/// <summary>The registry's custom agent endpoints (<c>/v1/agents</c>), as the CLI uses them.</summary>
/// <remarks>Never throws for an unreachable registry or a refused request; those come back as typed results.</remarks>
internal interface IAgentApi
{
    /// <summary>Uploads one agent file to <c>POST /v1/agents</c>.</summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="relativePath">The file's path relative to the agents folder, with <c>/</c> separators.</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="project">The project folder a project agent lives in; <c>null</c> for a personal agent.</param>
    /// <param name="baseVersion">The registry version this copy was last synced from, 0 when it never synced one.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <remarks>
    /// The registry names the agent from the file's front matter. A stale push comes back as <see cref="AgentPushStatus.Stale"/>
    /// with the current version in <see cref="AgentPushOutcome.Version"/>; a push from a copy of a removed agent as
    /// <see cref="AgentPushStatus.Removed"/>. A registry without agents answers 404, which comes back as a refusal saying so.
    /// </remarks>
    Task<AgentPushOutcome> PushAgentAsync(
        Uri registry, string accessToken, string relativePath, byte[] content, SkillProject? project, int baseVersion, CancellationToken cancellationToken);

    /// <summary>Lists every one of the caller's agents, personal and project, with the names removed (<c>GET /v1/agents?includeProjects=true</c>).</summary>
    Task<RegistryAgentList> ListAgentsAsync(Uri registry, string accessToken, CancellationToken cancellationToken);

    /// <summary>Downloads one version of the caller's agent with known secrets redacted (<c>GET /v1/agents/{name}/content?redacted=true</c>).</summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="name">The agent's exact name.</param>
    /// <param name="projectId">The agent's project; <c>null</c> for the personal agent of that name.</param>
    /// <param name="version">The version to read; <c>null</c> for the current one.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <remarks>
    /// Callers check the <c>agent-content</c> capability first, strictly: the redacted read is part of it. An agent or
    /// version the caller does not have comes back with <see cref="ApiResult{T}.AgentUnknown"/> set.
    /// </remarks>
    Task<ApiResult<RemoteAgentContent>> GetRedactedAgentContentAsync(
        Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken);
}

/// <summary>One version of an agent as the registry serves it, with its file.</summary>
/// <remarks>Read with <c>redacted=true</c>, so <see cref="Content"/> may not hash to <see cref="ContentHash"/>, which is always the stored file's.</remarks>
internal sealed record RemoteAgentContent
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }

    /// <summary>The file's path relative to the agents folder, as it was pushed.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = "";

    [JsonPropertyName("contentHash")] public string ContentHash { get; init; } = "";
    [JsonPropertyName("pushedAt")] public DateTimeOffset PushedAt { get; init; }

    /// <summary>True when the registry replaced known secrets in <see cref="Content"/> with markers.</summary>
    [JsonPropertyName("redacted")] public bool Redacted { get; init; }

    /// <summary>The file's bytes (base64 in JSON).</summary>
    [JsonPropertyName("content")] public byte[] Content { get; init; } = [];
}

/// <summary>One agent in the caller's registry, described by its current version.</summary>
/// <param name="Name">The agent's name, unique per user within its project (or among personal agents).</param>
/// <param name="Version">The current version number.</param>
/// <param name="ContentHash">The uppercase hex SHA-256 of the current version's file; compare ignoring case.</param>
/// <param name="ProjectId">The agent's project; <c>null</c> for a personal agent.</param>
/// <param name="ProjectName">That project's display name.</param>
/// <param name="FirstVersion">1, or above it for an agent that reused a removed agent's name.</param>
internal sealed record RegistryAgent(string Name, int Version, string ContentHash, Guid? ProjectId, string? ProjectName, int FirstVersion);

/// <summary>An agent name the caller deleted or renamed away.</summary>
internal sealed record RegistryRemovedAgent(string Name, Guid? ProjectId, int LastVersion, string? RenamedTo, bool Replaced = false);

/// <summary>The result of listing agents: the agents and removed names, or why they could not be listed.</summary>
/// <param name="Agents">The caller's agents; <c>null</c> when the request failed.</param>
/// <param name="Removed">The names the caller deleted or renamed away.</param>
/// <param name="Error">Why the request failed; <c>null</c> on success.</param>
internal sealed record RegistryAgentList(IReadOnlyList<RegistryAgent>? Agents, IReadOnlyList<RegistryRemovedAgent> Removed, string? Error)
{
    /// <summary>
    /// The removal of the agent <paramref name="name"/> in <paramref name="projectId"/> that a copy last synced at
    /// <paramref name="syncedVersion"/> came from; <c>null</c> when that copy is not of a removed agent.
    /// </summary>
    /// <remarks>The registry's own rule, as <see cref="RegistrySkillList.RemovalOf"/>: a copy synced at a version from 1 up to the last one.</remarks>
    public RegistryRemovedAgent? RemovalOf(string name, Guid? projectId, int? syncedVersion)
    {
        if (syncedVersion is not { } synced || synced < 1)
            return null;

        if (Agents?.FirstOrDefault(a => a.ProjectId == projectId && string.Equals(a.Name, name, StringComparison.Ordinal)) is { } live)
            return synced < live.FirstVersion ? new RegistryRemovedAgent(live.Name, projectId, live.FirstVersion - 1, null, Replaced: true) : null;

        return Removed.FirstOrDefault(r => r.ProjectId == projectId && string.Equals(r.Name, name, StringComparison.Ordinal) && synced <= r.LastVersion);
    }
}

/// <summary>What happened to one agent file.</summary>
internal enum AgentPushStatus
{
    /// <summary>The registry stored a new version.</summary>
    Created,

    /// <summary>The registry already had these bytes as the current version.</summary>
    Unchanged,

    /// <summary>Not uploaded, or refused by the registry (no name, too large, not the creator); see the message.</summary>
    Refused,

    /// <summary>Not uploaded because an identical file with the same name and scope was.</summary>
    Skipped,

    /// <summary>Not uploaded because a different file with the same name and scope was: the registry holds one per name.</summary>
    Conflict,

    /// <summary>Refused as stale: changed here and in the registry since this copy synced. Version is the registry's current one.</summary>
    Stale,

    /// <summary>Not backed up because the agent this copy synced as was deleted or renamed in the registry since. Version is its last one.</summary>
    Removed,

    /// <summary>The upload could not complete (unreachable registry, unreadable file, server error).</summary>
    Failed,
}

/// <summary>The outcome of sending one agent to the registry.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Version">The current version after the push, the registry's current one for a stale refusal, or the removed agent's last one.</param>
/// <param name="Message">Why it was refused, skipped, stale or failed, or a note on a success; <c>null</c> otherwise.</param>
/// <param name="ProjectId">The project the registry filed a project agent under.</param>
/// <param name="ProjectName">That project's display name.</param>
/// <param name="Remembered">True when a refusal was answered from the ledger without asking the registry again.</param>
/// <param name="RenamedTo">For a removal by rename, the agent's name now.</param>
internal sealed record AgentPushOutcome(
    AgentPushStatus Status, int? Version, string? Message, Guid? ProjectId = null, string? ProjectName = null, bool Remembered = false,
    string? RenamedTo = null)
{
    /// <summary>True when the agent is now safely in the registry.</summary>
    public bool Succeeded => Status is AgentPushStatus.Created or AgentPushStatus.Unchanged;
}
