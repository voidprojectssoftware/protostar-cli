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
}
