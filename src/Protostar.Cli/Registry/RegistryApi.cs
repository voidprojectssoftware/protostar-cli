using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Registry;

/// <summary>
/// Production <see cref="IRegistryApi"/> over a single shared <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// The registry URL is passed per call rather than fixed at construction because it is resolved from
/// command options and the environment at run time. TLS is validated normally.
/// </remarks>
internal sealed class RegistryApi : IRegistryApi, IDisposable
{
    private readonly HttpClient _http;

    public RegistryApi()
        : this(new HttpClientHandler())
    {
    }

    /// <summary>Creates a client over <paramref name="handler"/>; tests pass a stub handler.</summary>
    internal RegistryApi(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <inheritdoc />
    public async Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(registry, "/v1/meta"), cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
                return null;

            return await response.Content.ReadFromJsonAsync<RegistryMeta>(AuthJson.Default, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SkillPushOutcome> PushSkillAsync(
        Uri registry,
        string accessToken,
        string name,
        IReadOnlyList<SkillUploadFile> files,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(name), "name");
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "files", file.RelativePath);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(registry, "/v1/skills")) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            return await ToOutcomeAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new SkillPushOutcome(SkillPushStatus.Failed, null, $"Could not reach the registry: {ex.Message}");
        }
    }

    // 201 is a new version and 200 an unchanged push, both carrying the push body. Any 4xx is the
    // registry refusing this skill (its problem title says why); anything else is a failure to retry.
    private static async Task<SkillPushOutcome> ToOutcomeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var body = await ReadAsync<PushResponse>(response, cancellationToken);
            var pushStatus = status == HttpStatusCode.Created ? SkillPushStatus.Created : SkillPushStatus.Unchanged;
            return new SkillPushOutcome(pushStatus, body?.Version, null);
        }

        var problem = await ReadAsync<ProblemResponse>(response, cancellationToken);
        var reason = problem?.Title ?? problem?.Detail;

        if (status == HttpStatusCode.Unauthorized)
            return new SkillPushOutcome(SkillPushStatus.Failed, null, reason ?? "The registry rejected the session. Run `protostar auth login`.");

        if ((int)status is >= 400 and < 500)
            return new SkillPushOutcome(SkillPushStatus.Refused, null, reason ?? $"Refused by the registry ({(int)status}).");

        return new SkillPushOutcome(SkillPushStatus.Failed, null, reason ?? $"The registry returned {(int)status}.");
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json"))
            return default;

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(AuthJson.Default, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record PushResponse
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("version")] public int? Version { get; init; }
    }

    private sealed record ProblemResponse
    {
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("detail")] public string? Detail { get; init; }
    }
}
