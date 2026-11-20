using System.Net;
using System.Net.Http.Json;

namespace Protostar.Cli.Registry;

/// <summary>The search half of <see cref="RegistryApi"/>.</summary>
internal sealed partial class RegistryApi : ISearchApi
{
    /// <inheritdoc />
    public async Task<ApiResult<IReadOnlyList<SearchHit>>> SearchAsync(Uri registry, string accessToken, SearchQuery query, CancellationToken cancellationToken)
    {
        var parts = new List<string> { "q=" + Uri.EscapeDataString(query.Text), $"limit={query.Limit}" };
        if (query.Kind is { } kind) parts.Add("kind=" + Uri.EscapeDataString(kind));
        if (!string.IsNullOrWhiteSpace(query.Project)) parts.Add("project=" + Uri.EscapeDataString(query.Project));

        using var request = Request(HttpMethod.Get, registry, "/v1/search?" + string.Join('&', parts), accessToken);
        var result = await SendAsync<IReadOnlyList<SearchHit>>(
            request,
            async response => await response.Content.ReadFromJsonAsync<SearchHit[]>(TranscriptJson.Options) ?? [],
            cancellationToken);
        // Only a registry without the route answers a bare 404 here; the endpoint itself never does.
        return result.Error == Describe(HttpStatusCode.NotFound)
            ? ApiResult<IReadOnlyList<SearchHit>>.Fail(ISearchApi.RegistryTooOldForSearch)
            : result;
    }
}
