using System.Net.Http.Json;
using Protostar.Cli.Projects;

namespace Protostar.Cli.Registry;

/// <summary>The project half of <see cref="RegistryApi"/>.</summary>
internal sealed partial class RegistryApi : IProjectApi
{
    /// <inheritdoc />
    public Task<ApiResult<RemoteProject>> ResolveProjectAsync(
        Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
        PostAsync<RemoteProject>(registry, accessToken, "/v1/projects/resolve", new { machineId, facts }, cancellationToken);

    /// <inheritdoc />
    public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(
        Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken) =>
        PostAsync<ProjectLinkChange>(registry, accessToken, "/v1/projects/link", new { machineId, facts, project }, cancellationToken);

    /// <inheritdoc />
    public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(
        Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
        PostAsync<ProjectLinkChange>(registry, accessToken, "/v1/projects/unlink", new { machineId, facts }, cancellationToken);

    private async Task<ApiResult<T>> PostAsync<T>(Uri registry, string accessToken, string path, object body, CancellationToken cancellationToken)
        where T : class
    {
        using var request = Request(HttpMethod.Post, registry, path, accessToken);
        request.Content = JsonContent.Create(body, options: TranscriptJson.Options);
        return await SendAsync(
            request,
            async response => await response.Content.ReadFromJsonAsync<T>(TranscriptJson.Options) ?? throw new System.Text.Json.JsonException("Empty response."),
            cancellationToken);
    }
}
