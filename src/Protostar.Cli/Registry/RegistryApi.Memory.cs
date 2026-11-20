using System.Net.Http.Json;
using Protostar.Cli.Projects;

namespace Protostar.Cli.Registry;

/// <summary>The memory half of <see cref="RegistryApi"/>.</summary>
internal sealed partial class RegistryApi : IMemoryApi
{
    /// <inheritdoc />
    public async Task<ApiResult<MemoryPushResponse>> PushMemoryAsync(
        Uri registry, string accessToken, Guid machineId, ProjectFacts facts, IReadOnlyList<MemoryUpload> files, IReadOnlyList<string> deleted,
        CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, registry, "/v1/memory", accessToken);
        request.Content = JsonContent.Create(
            new { machineId, project = facts, files = files.Select(f => new { path = f.Path, content = f.Content }), deleted },
            options: TranscriptJson.Options);
        var result = await SendAsync(
            request,
            async response => await response.Content.ReadFromJsonAsync<MemoryPushResponse>(TranscriptJson.Options) ?? throw new System.Text.Json.JsonException("Empty response."),
            cancellationToken,
            machineId);
        // A registry from before memory backup has no such route. Saying so beats a bare status code.
        return result.Error == Describe(System.Net.HttpStatusCode.NotFound)
            ? ApiResult<MemoryPushResponse>.Fail("This registry cannot back up memory yet. Update the registry.")
            : result;
    }

    /// <inheritdoc />
    public async Task<ApiResult<IReadOnlyList<RemoteMemoryProject>>> ListMemoryProjectsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, registry, "/v1/memory", accessToken);
        var result = await SendAsync<IReadOnlyList<RemoteMemoryProject>>(
            request,
            async response => await response.Content.ReadFromJsonAsync<RemoteMemoryProject[]>(TranscriptJson.Options) ?? [],
            cancellationToken);
        return result.Error == Describe(System.Net.HttpStatusCode.NotFound)
            ? ApiResult<IReadOnlyList<RemoteMemoryProject>>.Fail(TooOldToPull)
            : result;
    }

    /// <inheritdoc />
    public async Task<ApiResult<RemoteMemoryContent>> GetMemoryContentAsync(Uri registry, string accessToken, Guid projectId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, registry, $"/v1/memory/{projectId}/content", accessToken);
        var result = await SendAsync(
            request,
            async response => await response.Content.ReadFromJsonAsync<RemoteMemoryContent>(TranscriptJson.Options) ?? throw new System.Text.Json.JsonException("Empty response."),
            cancellationToken);
        // An unknown project carries a problem title; only a registry without the route answers a bare 404.
        return result.Error == Describe(System.Net.HttpStatusCode.NotFound)
            ? ApiResult<RemoteMemoryContent>.Fail(TooOldToPull)
            : result;
    }

    private const string TooOldToPull = "This registry cannot bring memory to this machine yet. Update the registry.";
}
