using System.Text.Json.Serialization;
using Protostar.Cli.Projects;

namespace Protostar.Cli.Registry;

/// <summary>The registry's project endpoints, as the CLI uses them.</summary>
/// <remarks>Never throws for an unreachable registry or a refused request; those come back as <see cref="ApiResult{T}.Error"/>.</remarks>
internal interface IProjectApi
{
    /// <summary><c>POST /v1/projects/resolve</c>: which project the folder described by <paramref name="facts"/> is in. Changes nothing.</summary>
    Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/projects/link</c>: links the folder to <paramref name="project"/>, an id or exact name.</summary>
    Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/projects/unlink</c>: unlinks the folder.</summary>
    Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken);
}

/// <summary>Which project the registry says a folder is in.</summary>
internal sealed record RemoteProject
{
    /// <summary>The project, or <c>null</c> when none exists yet: the first backup from the folder would create it.</summary>
    [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary><c>link</c>, <c>rootCommit</c>, <c>previousRootCommit</c>, <c>remote</c>, <c>folder</c> or <c>new</c>.</summary>
    [JsonPropertyName("recognisedBy")] public string RecognisedBy { get; init; } = "";

    /// <summary>The folder it is filed by: the main worktree for git.</summary>
    [JsonPropertyName("folder")] public string Folder { get; init; } = "";

    [JsonPropertyName("subpath")] public string? Subpath { get; init; }

    /// <summary>Projects a folder outside git looks like. Never applied by the registry.</summary>
    [JsonPropertyName("suggestions")] public IReadOnlyList<RemoteProjectSuggestion> Suggestions { get; init; } = [];
}

/// <summary>A project the registry suggests linking a folder to.</summary>
internal sealed record RemoteProjectSuggestion
{
    [JsonPropertyName("projectId")] public Guid ProjectId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>What a link or unlink did, and where the folder belongs now.</summary>
internal sealed record ProjectLinkChange
{
    /// <summary><c>linked</c>, <c>unlinked</c>, <c>separated</c> or <c>nothingToUnlink</c>.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";

    [JsonPropertyName("project")] public RemoteProject Project { get; init; } = new();

    /// <summary>The names of the project skills that moved with the folder, sorted; empty when none did, or from a registry too old to say.</summary>
    [JsonPropertyName("movedSkills")] public IReadOnlyList<string> MovedSkills { get; init; } = [];

    /// <summary>The names of the project agents that moved with the folder, sorted; empty when none did, or from a registry without agents.</summary>
    [JsonPropertyName("movedAgents")] public IReadOnlyList<string> MovedAgents { get; init; } = [];
}
