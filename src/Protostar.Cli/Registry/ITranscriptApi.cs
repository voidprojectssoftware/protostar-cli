using System.Text.Json;
using System.Text.Json.Serialization;
using Protostar.Cli.Harness;

namespace Protostar.Cli.Registry;

/// <summary>The registry's machine and transcript endpoints, as the CLI uses them.</summary>
/// <remarks>Never throws for an unreachable registry or a refused request; those come back as typed results.</remarks>
internal interface ITranscriptApi
{
    /// <summary><c>PUT /v1/machines/{id}</c>.</summary>
    Task<ApiResult<bool>> RegisterMachineAsync(Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken);

    /// <summary><c>PUT /v1/transcripts/{harness}/{key}</c>: registers the transcript and returns where its stored content ends.</summary>
    Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/transcripts/{harness}/{key}/content</c>: appends one chunk of whole lines, gzip-compressed on the wire.</summary>
    Task<AppendResult> AppendAsync(
        Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId,
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/transcripts</c>.</summary>
    Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken);
}

/// <summary>A value, or why the registry did not return one.</summary>
internal sealed record ApiResult<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;

    public static ApiResult<T> Success(T value) => new(value, null);

    public static ApiResult<T> Fail(string error) => new(default, error);
}

/// <summary>Where a transcript's stored content ends in the registry.</summary>
internal sealed record TranscriptPosition(int Revision, long Length);

/// <summary>What happened to an appended chunk.</summary>
internal enum AppendStatus
{
    /// <summary>Stored; <see cref="AppendResult.Position"/> is the new end.</summary>
    Appended,

    /// <summary>Not at the registry's position, which <see cref="AppendResult.Position"/> carries.</summary>
    Mismatch,

    /// <summary>The registry refused the chunk itself (malformed, too large); retrying the same bytes will not help.</summary>
    Rejected,

    /// <summary>The request did not complete (network, auth, server error); retrying later may.</summary>
    Failed,
}

/// <summary>The result of appending a chunk.</summary>
internal sealed record AppendResult(AppendStatus Status, TranscriptPosition? Position, string? Message);

/// <summary>Filters for listing transcripts.</summary>
internal sealed record TranscriptListQuery(string? Project, Guid? Machine, string? Harness, DateTimeOffset? Since, int Limit);

/// <summary>A transcript as the registry lists it.</summary>
internal sealed record RemoteTranscript
{
    [JsonPropertyName("harness")] public string Harness { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("parentKey")] public string? ParentKey { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("firstPrompt")] public string? FirstPrompt { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("gitBranch")] public string? GitBranch { get; init; }
    [JsonPropertyName("machineId")] public Guid MachineId { get; init; }
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }
    [JsonPropertyName("sourcePath")] public string? SourcePath { get; init; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }
    [JsonPropertyName("lastActivityAt")] public DateTimeOffset? LastActivityAt { get; init; }
    [JsonPropertyName("userMessages")] public int UserMessages { get; init; }
    [JsonPropertyName("assistantMessages")] public int AssistantMessages { get; init; }
    [JsonPropertyName("length")] public long Length { get; init; }
    [JsonPropertyName("subagentCount")] public int SubagentCount { get; init; }
}

internal static class TranscriptJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
