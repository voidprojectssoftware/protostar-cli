using System.Text.Json;
using System.Text.Json.Serialization;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;

namespace Protostar.Cli.Registry;

/// <summary>The registry's machine and transcript endpoints, as the CLI uses them.</summary>
/// <remarks>Never throws for an unreachable registry or a refused request; those come back as typed results.</remarks>
internal interface ITranscriptApi
{
    /// <summary><c>PUT /v1/machines/{id}</c>.</summary>
    Task<ApiResult<bool>> RegisterMachineAsync(Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken);

    /// <summary><c>PUT /v1/transcripts/{harness}/{key}</c>: registers the transcript and returns where its stored content ends.</summary>
    /// <remarks>
    /// <paramref name="project"/> files it under the project of its working directory; <c>null</c> when that is not known yet.
    /// A registry that does not know <paramref name="machineId"/> answers with <see cref="ApiResult{T}.MachineUnknown"/> set.
    /// </remarks>
    Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(
        Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, ProjectFacts? project, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/transcripts/{harness}/{key}/content</c>: appends one chunk of whole lines, gzip-compressed on the wire.</summary>
    /// <remarks>
    /// A registry that does not know <paramref name="machineId"/> answers <see cref="AppendStatus.UnknownMachine"/>, and one
    /// that does not know the transcript answers <see cref="AppendStatus.UnknownTranscript"/>.
    /// </remarks>
    Task<AppendResult> AppendAsync(
        Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId,
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/transcripts/lookup</c>: where each transcript ends in the registry, or that it holds none of it.</summary>
    /// <remarks>
    /// One request per call; the caller batches, at most <see cref="MaxLookup"/> per call. The answers come back in
    /// request order. A registry from before the endpoint existed fails with a message saying to update it.
    /// </remarks>
    Task<ApiResult<IReadOnlyList<TranscriptLookup>>> LookupAsync(
        Uri registry, string accessToken, IReadOnlyList<LocalTranscript> transcripts, CancellationToken cancellationToken);

    /// <summary>The most transcripts the registry answers in one lookup (its <c>TranscriptService.MaxLookup</c>).</summary>
    const int MaxLookup = 500;

    /// <summary><c>GET /v1/transcripts</c>.</summary>
    Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/transcripts/{harness}/{key}</c>: one transcript's record and, for a session, its subagents'.</summary>
    /// <remarks>A transcript the registry does not hold comes back with <see cref="ApiResult{T}.TranscriptUnknown"/> set.</remarks>
    Task<ApiResult<TranscriptDetail>> GetAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/transcripts/{harness}/{key}/content?redacted=true</c>: the transcript's lines with secrets redacted, as a stream.</summary>
    /// <remarks>
    /// <para>
    /// The stream reads straight from the response as it arrives, so a transcript of any size is never held in memory;
    /// disposing it ends the response. Reading it can still fail part way with an <see cref="IOException"/> or
    /// <see cref="HttpRequestException"/> if the connection drops.
    /// </para>
    /// <para>
    /// Always redacted (registry#72): this is for reading a transcript as a person or an agent should. Backups and
    /// verification compare the stored bytes and never use it. A registry from before redaction ignores the parameter
    /// and sends the stored lines.
    /// </para>
    /// </remarks>
    Task<ApiResult<Stream>> OpenRedactedContentAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/transcripts/{harness}/{key}/skills</c>: the skills the transcript (and, for a session, its subagents) used.</summary>
    /// <remarks>
    /// Callers check the <c>session-skills</c> capability first (registry#14). A transcript the registry does not hold
    /// comes back with <see cref="ApiResult{T}.TranscriptUnknown"/> set.
    /// </remarks>
    Task<ApiResult<IReadOnlyList<SessionSkill>>> GetSkillsAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken);
}

/// <summary>One skill a session used, as the registry reports it.</summary>
/// <remarks>A transcript records only the name, so the registry infers the version from push times; only names, counts and times are kept.</remarks>
internal sealed record SessionSkill
{
    /// <summary>The name as invoked; a plugin skill keeps its <c>plugin:skill</c> form.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>How many times it was invoked.</summary>
    [JsonPropertyName("uses")] public int Uses { get; init; }

    [JsonPropertyName("firstUsedAt")] public DateTimeOffset? FirstUsedAt { get; init; }
    [JsonPropertyName("lastUsedAt")] public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>The registry skill it matched; <c>null</c> when it matched none or was deleted.</summary>
    [JsonPropertyName("skillId")] public Guid? SkillId { get; init; }

    /// <summary>The version inferred to have been used; <c>null</c> when none had been pushed by then.</summary>
    [JsonPropertyName("version")] public int? Version { get; init; }

    /// <summary>True when <see cref="Version"/> is set: inferred from push times, never read from the transcript.</summary>
    [JsonPropertyName("versionInferred")] public bool VersionInferred { get; init; }

    /// <summary>True when the skill has since been deleted from the registry.</summary>
    [JsonPropertyName("deleted")] public bool Deleted { get; init; }
}

/// <summary>A value, or why the registry did not return one.</summary>
/// <param name="MachineUnknown">The registry refused because it has no record of the machine the request named.</param>
/// <param name="TranscriptUnknown">The registry answered that it holds no transcript with that harness and key.</param>
/// <param name="MemoryUnknown">The registry answered that the caller has no such project, or no memory file at that path.</param>
internal sealed record ApiResult<T>(T? Value, string? Error, bool MachineUnknown = false, bool TranscriptUnknown = false, bool MemoryUnknown = false)
{
    public bool Ok => Error is null;

    public static ApiResult<T> Success(T value) => new(value, null);

    public static ApiResult<T> Fail(string error) => new(default, error);

    public static ApiResult<T> UnknownMachine(string error) => new(default, error, MachineUnknown: true);

    public static ApiResult<T> UnknownTranscript(string error) => new(default, error, TranscriptUnknown: true);

    public static ApiResult<T> UnknownMemory(string error) => new(default, error, MemoryUnknown: true);
}

/// <summary>Where a transcript's stored content ends in the registry.</summary>
internal sealed record TranscriptPosition(int Revision, long Length);

/// <summary>What the registry holds of one transcript, as a lookup answers it.</summary>
/// <param name="Known">Whether the registry holds it at all; when false, <paramref name="Revision"/> and <paramref name="Length"/> are <c>null</c>.</param>
internal sealed record TranscriptLookup(string Harness, string Key, bool Known, int? Revision, long? Length);

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

    /// <summary>The registry has no record of the machine that sent the chunk, typically after its database was rebuilt.</summary>
    UnknownMachine,

    /// <summary>The registry has no record of the transcript itself, typically after its rows were lost or the database reset.</summary>
    /// <remarks>Only the registry's <c>unknown-transcript</c> problem type means this; any other 404 is <see cref="Rejected"/>.</remarks>
    UnknownTranscript,
}

/// <summary>The result of appending a chunk.</summary>
internal sealed record AppendResult(AppendStatus Status, TranscriptPosition? Position, string? Message);

/// <summary>Filters for listing transcripts.</summary>
/// <param name="Before">Only transcripts last active before this time, to page: the previous page's last <c>lastActivityAt</c>.</param>
/// <param name="KeyPrefix">
/// Only sessions whose key starts with this, ignoring case; needs the <c>transcript-prefix-lookup</c> capability, and at
/// least 4 characters a key may hold, or the registry answers 400.
/// </param>
internal sealed record TranscriptListQuery(
    string? Project, Guid? Machine, string? Harness, DateTimeOffset? Since, int Limit, DateTimeOffset? Before = null, string? KeyPrefix = null);

/// <summary>A transcript as the registry describes it on its own: its record and, for a session, its subagents'.</summary>
/// <param name="Subagents">The session's subagents, earliest first; empty for a subagent.</param>
internal sealed record TranscriptDetail(
    [property: JsonPropertyName("transcript")] RemoteTranscript Transcript,
    [property: JsonPropertyName("subagents")] IReadOnlyList<RemoteTranscript> Subagents);

/// <summary>A transcript as the registry lists it.</summary>
internal sealed record RemoteTranscript
{
    [JsonPropertyName("harness")] public string Harness { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("parentKey")] public string? ParentKey { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("firstPrompt")] public string? FirstPrompt { get; init; }
    [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("projectSubpath")] public string? ProjectSubpath { get; init; }
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
