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

    /// <summary><c>GET /v1/memory</c>: the signed-in user's projects that have memory.</summary>
    Task<ApiResult<IReadOnlyList<RemoteMemoryProject>>> ListMemoryProjectsAsync(Uri registry, string accessToken, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/memory/{projectId}/content</c>: every memory file of the project with its current version's bytes.</summary>
    /// <remarks>A registry from before memory pull answers with an error that says to update it.</remarks>
    Task<ApiResult<RemoteMemoryContent>> GetMemoryContentAsync(Uri registry, string accessToken, Guid projectId, CancellationToken cancellationToken);
}

/// <summary>A project that has memory in the registry.</summary>
internal sealed record RemoteMemoryProject
{
    [JsonPropertyName("projectId")] public Guid ProjectId { get; init; }
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";

    /// <summary>How many memory files it has, deleted ones included.</summary>
    [JsonPropertyName("files")] public int Files { get; init; }
}

/// <summary>A project's memory files with their content, as the registry answers it.</summary>
internal sealed record RemoteMemoryContent
{
    [JsonPropertyName("projectId")] public Guid ProjectId { get; init; }
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("files")] public IReadOnlyList<RemoteMemoryFile> Files { get; init; } = [];
}

/// <summary>One memory file's current version, with its bytes.</summary>
internal sealed record RemoteMemoryFile
{
    /// <summary>Its path within the memory folder, with <c>/</c> separators. Checked before anything is written.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = "";

    [JsonPropertyName("version")] public int Version { get; init; }

    /// <summary>Uppercase hex SHA-256 of <see cref="Content"/>, checked after download.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";

    [JsonPropertyName("content")] public byte[] Content { get; init; } = [];

    /// <summary>The machine that first sent this version: whose change it is.</summary>
    /// <remarks><see cref="Guid.Empty"/> from a registry older than protostar-cli#47.</remarks>
    [JsonPropertyName("machineId")] public Guid MachineId { get; init; }

    /// <summary>That machine's name; <c>null</c> once it is no longer registered, or from an older registry.</summary>
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }

    /// <summary>Every version of the file with the version it was edited from, so a pull can tell which versions build on the one it has.</summary>
    /// <remarks>Empty from a registry older than protostar-cli#47.</remarks>
    [JsonPropertyName("history")] public IReadOnlyList<RemoteMemoryVersionBase> History { get; init; } = [];

    /// <summary>Whether no machine's copy still has the file.</summary>
    [JsonPropertyName("deletedEverywhere")] public bool DeletedEverywhere { get; init; }

    /// <summary>The copies that deleted the file.</summary>
    [JsonPropertyName("deletedOn")] public IReadOnlyList<RemoteMemoryCopy> DeletedOn { get; init; } = [];
}

/// <summary>One version of a memory file and the version its sender had last synced when it sent it.</summary>
internal sealed record RemoteMemoryVersionBase
{
    [JsonPropertyName("version")] public int Version { get; init; }

    /// <summary><c>null</c> when the sender had no version of the file, or the registry does not know it.</summary>
    [JsonPropertyName("base")] public int? Base { get; init; }
}

/// <summary>One machine's copy of a memory file.</summary>
internal sealed record RemoteMemoryCopy
{
    [JsonPropertyName("machineId")] public Guid MachineId { get; init; }

    /// <summary>The machine's name, or <c>null</c> once it is no longer registered.</summary>
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }

    [JsonPropertyName("folder")] public string Folder { get; init; } = "";
}

/// <summary>One memory file to back up.</summary>
/// <param name="Path">Its path within the memory folder, with <c>/</c> separators.</param>
/// <param name="Content">Its bytes.</param>
/// <param name="BaseVersion">The version this machine last synced for the file, which it was edited from; <c>null</c> for a file new here.</param>
internal sealed record MemoryUpload(string Path, byte[] Content, int? BaseVersion = null);

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
