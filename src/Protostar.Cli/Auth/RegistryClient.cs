using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Protostar.Cli.Auth;

/// <summary>
/// Thin HTTP client for the registry's OAuth + metadata endpoints. TLS is validated normally, so
/// the registry must present a trusted certificate (the ASP.NET Core dev cert locally).
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

    public Task<TokenResponse> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = AuthConstants.ClientId,
            ["code_verifier"] = codeVerifier,
        }, cancellationToken);

    public Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthConstants.ClientId,
        }, cancellationToken);

    public async Task<UserInfo?> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<UserInfo>(response, cancellationToken)
            : null;
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync("/connect/token", content, cancellationToken);

        return await ReadJsonAsync<TokenResponse>(response, cancellationToken)
            ?? new TokenResponse
            {
                Error = "invalid_response",
                ErrorDescription = "The registry returned an unexpected (non-JSON) response.",
            };
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

internal sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }

    [JsonIgnore]
    public bool IsSuccess => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(AccessToken);
}

internal sealed record UserInfo
{
    [JsonPropertyName("sub")] public string? Sub { get; init; }
    [JsonPropertyName("preferred_username")] public string? PreferredUsername { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("github_login")] public string? GitHubLogin { get; init; }
}
