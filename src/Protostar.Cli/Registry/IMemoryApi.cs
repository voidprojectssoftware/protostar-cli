using System.Text.Json.Serialization;
using Protostar.Cli.Projects;

namespace Protostar.Cli.Registry;

/// <summary>The registry's memory endpoints, as the CLI uses them.</summary>
/// <remarks>Never throws for an unreachable registry or a refused request; those come back as <see cref="ApiResult{T}.Error"/>.</remarks>
internal interface IMemoryApi
{
    /// <summary><c>POST /v1/memory</c>: backs up the changed and deleted files of one project folder.</summary>
    /// <remarks>A registry that does not know <paramref name="machineId"/> answers with <see cref="ApiResult{T}.MachineUnknown"/> set.</remarks>
    Task<ApiResult<MemoryPushResponse>> PushMemoryAsync(
        Uri registry, string accessToken, Guid machineId, ProjectFacts facts, IReadOnlyList<MemoryUpload> files, IReadOnlyList<string> deleted,
        CancellationToken cancellationToken);
}

/// <summary>One memory file to back up.</summary>
/// <param name="Path">Its path within the memory folder, with <c>/</c> separators.</param>
/// <param name="Content">Its bytes.</param>
internal sealed record MemoryUpload(string Path, byte[] Content);

/// <summary>What a memory push did, as the registry answers it.</summary>
internal sealed record MemoryPushResponse
{
    [JsonPropertyName("projectId")] public Guid ProjectId { get; init; }
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";

    /// <summary>The folder the registry filed this machine's copy by: the main worktree for git.</summary>
    [JsonPropertyName("folder")] public string Folder { get; init; } = "";

    [JsonPropertyName("files")] public IReadOnlyList<MemoryFileOutcome> Files { get; init; } = [];
}

/// <summary>One file's outcome.</summary>
internal sealed record MemoryFileOutcome
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";

    /// <summary><c>created</c>, <c>versioned</c>, <c>unchanged</c>, <c>markedDeleted</c> or <c>unknown</c>.</summary>
    [JsonPropertyName("change")] public string Change { get; init; } = "";

    /// <summary>The version this machine's copy points at; <c>null</c> for <c>unknown</c>.</summary>
    [JsonPropertyName("version")] public int? Version { get; init; }
}
