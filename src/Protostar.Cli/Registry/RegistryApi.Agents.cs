using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Registry;

/// <summary>The custom agent half of <see cref="RegistryApi"/>.</summary>
internal sealed partial class RegistryApi : IAgentApi
{
    private const string StaleAgentVersionType = "https://protostar.dev/problems/stale-agent-version";
    private const string AgentDeletedType = "https://protostar.dev/problems/agent-deleted";
    private const string AgentRenamedType = "https://protostar.dev/problems/agent-renamed";

    /// <inheritdoc />
    public async Task<AgentPushOutcome> PushAgentAsync(
        Uri registry, string accessToken, string relativePath, byte[] content, SkillProject? project, int baseVersion, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(baseVersion.ToString(CultureInfo.InvariantCulture)), "baseVersion");
        if (project is not null)
            form.Add(new StringContent(JsonSerializer.Serialize(new { machineId = project.MachineId, facts = project.Facts }, TranscriptJson.Options)), "project");
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(part, "file", relativePath);

        using var request = Request(HttpMethod.Post, registry, "/v1/agents", accessToken);
        request.Content = form;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            return await ToAgentOutcomeAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new AgentPushOutcome(AgentPushStatus.Failed, null, $"Could not reach the registry: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<RegistryAgentList> ListAgentsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, registry, "/v1/agents?includeProjects=true", accessToken);
        var result = MissingRoute(await SendAsync(
            request,
            async response => await response.Content.ReadFromJsonAsync<AgentListResponse>(TranscriptJson.Options) ?? throw new JsonException("Empty response."),
            cancellationToken), RegistryCapability.Agents);
        if (result.Value is not { Agents: { } agents } body)
            return new RegistryAgentList(null, [], result.Error ?? "The registry sent an unreadable agent list.");

        return new RegistryAgentList(
            agents.Where(a => a.Name is not null && a.ContentHash is not null)
                .Select(a => new RegistryAgent(a.Name!, a.Version, a.ContentHash!, a.ProjectId, a.ProjectName, a.FirstVersion ?? 1))
                .ToList(),
            (body.Removed ?? []).Where(r => r.Name is not null)
                .Select(r => new RegistryRemovedAgent(r.Name!, r.ProjectId, r.LastVersion, r.RenamedTo))
                .ToList(),
            null);
    }

    // As ToOutcomeAsync for skills: 201 new, 200 unchanged, 409 by problem type, other 4xx refused, the rest failed. A bare
    // 404 (no problem body) is a registry without agents.
    private static async Task<AgentPushOutcome> ToAgentOutcomeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var body = await ReadAsync<AgentPushResponse>(response, cancellationToken);
            return new AgentPushOutcome(status == HttpStatusCode.Created ? AgentPushStatus.Created : AgentPushStatus.Unchanged, body?.Version, null, body?.ProjectId, body?.ProjectName);
        }

        var problem = await ReadAsync<ProblemResponse>(response, cancellationToken);
        var reason = problem?.Title ?? problem?.Detail;
        if (status == HttpStatusCode.Unauthorized)
            return new AgentPushOutcome(AgentPushStatus.Failed, null, reason ?? "The registry rejected the session. Run `protostar auth login`.");
        if (status == HttpStatusCode.NotFound && problem is null)
            return new AgentPushOutcome(AgentPushStatus.Refused, null, ApiCompatibility.TooOld(RegistryCapability.Agents));
        if (status == HttpStatusCode.Conflict && problem?.Type == StaleAgentVersionType && problem.CurrentVersion is { } current)
            return new AgentPushOutcome(AgentPushStatus.Stale, current, reason, problem.ProjectId);
        if (status == HttpStatusCode.Conflict && problem?.Type is AgentDeletedType or AgentRenamedType && problem.LastVersion is { } last)
            return new AgentPushOutcome(AgentPushStatus.Removed, last, reason, problem.ProjectId, RenamedTo: problem.RenamedTo);
        if ((int)status is >= 400 and < 500)
            return new AgentPushOutcome(AgentPushStatus.Refused, null, reason ?? $"Refused by the registry ({(int)status}).");
        return new AgentPushOutcome(AgentPushStatus.Failed, null, reason ?? $"The registry returned {(int)status}.");
    }

    private sealed record AgentPushResponse
    {
        [JsonPropertyName("version")] public int? Version { get; init; }
        [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
        [JsonPropertyName("projectName")] public string? ProjectName { get; init; }
    }

    private sealed record AgentListResponse
    {
        [JsonPropertyName("agents")] public List<AgentListItem>? Agents { get; init; }
        [JsonPropertyName("removed")] public List<RemovedItem>? Removed { get; init; }
    }

    private sealed record AgentListItem
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("version")] public int Version { get; init; }
        [JsonPropertyName("contentHash")] public string? ContentHash { get; init; }
        [JsonPropertyName("projectId")] public Guid? ProjectId { get; init; }
        [JsonPropertyName("projectName")] public string? ProjectName { get; init; }
        [JsonPropertyName("firstVersion")] public int? FirstVersion { get; init; }
    }
}
