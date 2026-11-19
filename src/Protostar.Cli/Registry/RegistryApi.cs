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
internal sealed partial class RegistryApi : IRegistryApi, IDisposable
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
        SkillProject? project,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(name), "name");
        if (project is not null)
            content.Add(new StringContent(JsonSerializer.Serialize(new { machineId = project.MachineId, facts = project.Facts }, TranscriptJson.Options)), "project");
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

    /// <inheritdoc />
    public async Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
    {
        var (body, error) = await GetJsonAsync<ListResponse>(new Uri(registry, "/v1/skills?includeProjects=true"), accessToken, cancellationToken);
        if (body?.Skills is not { } skills)
            return new RegistrySkillList(null, error ?? "The registry sent an unreadable skill list.");

        return new RegistrySkillList(
            skills.Where(s => s.Name is not null && s.ContentHash is not null)
                .Select(s => new RegistrySkill(s.Name!, s.Description, s.Version, s.ContentHash!, s.UpdatedAt, s.ProjectId, s.ProjectName))
                .ToList(),
            null);
    }

    /// <inheritdoc />
    public async Task<RegistrySkillDownload> GetSkillFilesAsync(
        Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (projectId is { } project)
            query.Add($"projectId={project}");
        if (version is { } v)
            query.Add($"version={v}");
        var path = $"/v1/skills/{Uri.EscapeDataString(name)}/files" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        var (body, error) = await GetJsonAsync<FilesResponse>(new Uri(registry, path), accessToken, cancellationToken);
        if (body is not { Name: not null, ContentHash: not null, Files: not null })
            return new RegistrySkillDownload(null, error ?? "The registry sent an unreadable skill.");

        var files = body.Files
            .Select(f => new RegistrySkillFile(f.Path ?? string.Empty, f.Sha256 ?? string.Empty, f.Content ?? []))
            .ToList();
        return new RegistrySkillDownload(new RegistrySkillContent(body.Name, body.Version, body.ContentHash, files), null);
    }

    // An authenticated GET that reads a JSON body, turning every failure into a message instead of throwing.
    private async Task<(T? Body, string? Error)> GetJsonAsync<T>(Uri url, string accessToken, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return (await ReadAsync<T>(response, cancellationToken), null);

            var problem = await ReadAsync<ProblemResponse>(response, cancellationToken);
            var reason = problem?.Title ?? problem?.Detail;
            return response.StatusCode == HttpStatusCode.Unauthorized
                ? (null, reason ?? "The registry rejected the session. Run `protostar auth login`.")
                : (null, reason ?? $"The registry returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, $"Could not reach the registry: {ex.Message}");
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
            return new SkillPushOutcome(pushStatus, body?.Version, null, body?.ProjectId, body?.ProjectName);
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
        [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
        [JsonPropertyName("projectName")] public string? ProjectName { get; init; }
    }

    private sealed record ListResponse
    {
        [JsonPropertyName("skills")] public List<ListItem>? Skills { get; init; }
    }

    private sealed record ListItem
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("version")] public int Version { get; init; }
        [JsonPropertyName("contentHash")] public string? ContentHash { get; init; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
        [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
        [JsonPropertyName("projectName")] public string? ProjectName { get; init; }
    }

    private sealed record FilesResponse
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("version")] public int Version { get; init; }
        [JsonPropertyName("contentHash")] public string? ContentHash { get; init; }
        [JsonPropertyName("files")] public List<FileItem>? Files { get; init; }
    }

    private sealed record FileItem
    {
        [JsonPropertyName("path")] public string? Path { get; init; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
        [JsonPropertyName("content")] public byte[]? Content { get; init; }
    }

    private sealed record ProblemResponse
    {
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("detail")] public string? Detail { get; init; }
    }
}
