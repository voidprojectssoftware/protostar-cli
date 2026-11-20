using System.Text.Json.Serialization;

namespace Protostar.Cli.Registry;

/// <summary>The registry's search endpoint, as the CLI uses it.</summary>
/// <remarks>Never throws for an unreachable registry or a refused request; those come back as <see cref="ApiResult{T}.Error"/>.</remarks>
internal interface ISearchApi
{
    /// <summary><c>GET /v1/search</c>: the caller's sessions and memory files matching the query, best match first.</summary>
    /// <remarks>
    /// A bare 404 means a registry from before search, and comes back as <see cref="ApiResult{T}.Error"/> set to
    /// <see cref="RegistryTooOldForSearch"/> so the caller can say so instead of showing a status code.
    /// </remarks>
    Task<ApiResult<IReadOnlyList<SearchHit>>> SearchAsync(Uri registry, string accessToken, SearchQuery query, CancellationToken cancellationToken);

    /// <summary>The error for a registry that has no search: one that does not list the capability, or answers 404.</summary>
    const string RegistryTooOldForSearch = "This registry is too old for search. Update the registry.";
}

/// <summary>What to search for.</summary>
/// <param name="Text">The words or phrase, in web search syntax (quotes for a phrase, <c>or</c>, <c>-word</c>).</param>
/// <param name="Kind"><c>session</c> or <c>memory</c>; both when <c>null</c>.</param>
/// <param name="Project">A project id, or text its name contains, as the registry matches it; <c>null</c> for every project.</param>
/// <param name="Limit">The most hits to return, 1 to 100.</param>
internal sealed record SearchQuery(string Text, string? Kind, string? Project, int Limit);

/// <summary>One matching session or memory file, as the registry returns it.</summary>
/// <remarks>The snippet and title are redacted by the registry; nothing here comes from anywhere else.</remarks>
internal sealed record SearchHit
{
    /// <summary><c>session</c> (a session or subagent transcript) or <c>memory</c>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    [JsonPropertyName("harness")] public string? Harness { get; init; }

    /// <summary>For a session, the harness's transcript key, the same on every machine.</summary>
    [JsonPropertyName("key")] public string? Key { get; init; }

    /// <summary>For a subagent transcript, the key of the session it ran in.</summary>
    [JsonPropertyName("parentKey")] public string? ParentKey { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }

    /// <summary>For memory, the file's path within the project's memory folder.</summary>
    [JsonPropertyName("path")] public string? Path { get; init; }

    [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
    [JsonPropertyName("projectName")] public string? ProjectName { get; init; }
    [JsonPropertyName("machineId")] public Guid MachineId { get; init; }
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }

    /// <summary>For a session, its latest activity; for memory, when the file last changed.</summary>
    [JsonPropertyName("date")] public DateTimeOffset? Date { get; init; }

    /// <summary>Redacted text around the match, each match between <c>«</c> and <c>»</c>.</summary>
    [JsonPropertyName("snippet")] public string Snippet { get; init; } = "";
}
