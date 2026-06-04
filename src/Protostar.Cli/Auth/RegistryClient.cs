using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Protostar.Cli.Auth;

/// <summary>
/// Thin HTTP client for the registry's metadata endpoint. The OAuth/OIDC mechanics (authorize,
/// token, userinfo, refresh) are handled by OidcClient against the discovery document; this client
/// only covers the protostar-specific <c>/v1/meta</c> compatibility check. TLS is validated normally,
/// so the registry must present a trusted certificate (the ASP.NET Core dev cert locally).
/// </summary>
internal sealed class RegistryClient(Uri registry) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = registry, Timeout = TimeSpan.FromSeconds(30) };

    public Uri Registry { get; } = registry;

    public async Task<RegistryMeta?> GetMetaAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/v1/meta", cancellationToken);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<RegistryMeta>(response, cancellationToken)
            : null;
    }

    // Parses a JSON body, returning default when the response isn't JSON (e.g. an HTML error page
    // from pointing at the wrong URL) instead of throwing a cryptic parser exception.
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
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
}

internal static class AuthJson
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record RegistryMeta
{
    [JsonPropertyName("service")] public string? Service { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("apiMajors")] public int[]? ApiMajors { get; init; }
}
