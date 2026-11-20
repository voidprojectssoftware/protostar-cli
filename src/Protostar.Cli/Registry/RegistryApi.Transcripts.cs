using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;

namespace Protostar.Cli.Registry;

/// <summary>The transcript half of <see cref="RegistryApi"/>.</summary>
internal sealed partial class RegistryApi : ITranscriptApi
{
    /// <inheritdoc />
    public async Task<ApiResult<bool>> RegisterMachineAsync(
        Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Put, registry, $"/v1/machines/{id}", accessToken);
        request.Content = JsonContent.Create(new { name, operatingSystem }, options: TranscriptJson.Options);
        return await SendAsync(request, async _ => true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(
        Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, ProjectFacts? project, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Put, registry, TranscriptPath(transcript.Harness, transcript.Key), accessToken);
        JsonElement? metadata = transcript.MetadataJson is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null;
        request.Content = JsonContent.Create(new
        {
            kind = transcript.Kind,
            parentKey = transcript.ParentKey,
            machineId,
            sourcePath = transcript.Path,
            metadata,
            project,
        }, options: TranscriptJson.Options);

        return await SendAsync(request, ReadPositionAsync, cancellationToken, machineId);
    }

    /// <inheritdoc />
    public async Task<AppendResult> AppendAsync(
        Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId,
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        var path = $"{TranscriptPath(harness, key)}/content?revision={revision}&offset={offset}&machineId={machineId}";
        using var request = Request(HttpMethod.Post, registry, path, accessToken);
        var body = new ByteArrayContent(Gzip(chunk.Span));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentEncoding.Add("gzip");
        request.Content = body;
        request.Headers.Add("X-Content-SHA256", Convert.ToHexStringLower(SHA256.HashData(chunk.Span)));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new AppendResult(AppendStatus.Appended, await ReadPositionAsync(response), null);

            var problem = await ReadProblemAsync(response, cancellationToken);
            if (IsUnknownMachine(response, problem, machineId))
                return new AppendResult(AppendStatus.UnknownMachine, null, problem!.Title);
            if (IsUnknownTranscript(response, problem))
                return new AppendResult(AppendStatus.UnknownTranscript, null, problem!.Title);

            return response.StatusCode switch
            {
                HttpStatusCode.Conflict when problem?.Revision is { } r && problem.Length is { } l =>
                    new AppendResult(AppendStatus.Mismatch, new TranscriptPosition(r, l), problem.Title),
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.NotFound =>
                    new AppendResult(AppendStatus.Rejected, null, problem?.Title ?? $"Refused by the registry ({(int)response.StatusCode})."),
                _ => new AppendResult(AppendStatus.Failed, null, problem?.Title ?? Describe(response.StatusCode)),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new AppendResult(AppendStatus.Failed, null, $"Could not reach the registry: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<ApiResult<IReadOnlyList<TranscriptLookup>>> LookupAsync(
        Uri registry, string accessToken, IReadOnlyList<LocalTranscript> transcripts, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, registry, "/v1/transcripts/lookup", accessToken);
        request.Content = JsonContent.Create(
            new { transcripts = transcripts.Select(t => new { harness = t.Harness, key = t.Key }) }, options: TranscriptJson.Options);

        var result = await SendAsync<IReadOnlyList<TranscriptLookup>>(
            request,
            async response => await response.Content.ReadFromJsonAsync<TranscriptLookup[]>(TranscriptJson.Options) ?? [],
            cancellationToken);
        // A registry from before the lookup existed has no such route. Saying so beats a bare status code.
        return result.Error == Describe(HttpStatusCode.NotFound)
            ? ApiResult<IReadOnlyList<TranscriptLookup>>.Fail(ApiCompatibility.TooOld(RegistryCapability.TranscriptLookup))
            : result;
    }

    /// <inheritdoc />
    public async Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(
        Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken)
    {
        var parts = new List<string> { $"limit={Math.Clamp(query.Limit, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(query.Project)) parts.Add("project=" + Uri.EscapeDataString(query.Project));
        if (!string.IsNullOrWhiteSpace(query.Harness)) parts.Add("harness=" + Uri.EscapeDataString(query.Harness));
        if (query.Machine is { } machine) parts.Add("machine=" + machine);
        // In UTC: `--since 2026-11-01` parses with the local offset, and the registry answered 500 to any offset but zero.
        if (query.Since is { } since) parts.Add("since=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("O")));
        if (query.Before is { } before) parts.Add("before=" + Uri.EscapeDataString(before.ToUniversalTime().ToString("O")));
        if (!string.IsNullOrEmpty(query.KeyPrefix)) parts.Add("keyPrefix=" + Uri.EscapeDataString(query.KeyPrefix));

        using var request = Request(HttpMethod.Get, registry, "/v1/transcripts?" + string.Join('&', parts), accessToken);
        return await SendAsync<IReadOnlyList<RemoteTranscript>>(
            request,
            async response => await response.Content.ReadFromJsonAsync<RemoteTranscript[]>(TranscriptJson.Options) ?? [],
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<TranscriptDetail>> GetAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, registry, TranscriptPath(harness, key), accessToken);
        return await SendAsync(
            request,
            async response => await response.Content.ReadFromJsonAsync<TranscriptDetail>(TranscriptJson.Options, cancellationToken)
                ?? throw new JsonException("The registry sent an empty transcript."),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiResult<Stream>> OpenRedactedContentAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, registry, $"{TranscriptPath(harness, key)}/content?redacted=true", accessToken);
        HttpResponseMessage? response = null;
        try
        {
            // Headers only: the body is read as the caller renders it, never buffered whole.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                return ApiResult<Stream>.Success(new ResponseStream(body, response));
            }

            using (response)
            {
                var problem = await ReadProblemAsync(response, cancellationToken);
                return IsUnknownTranscript(response, problem)
                    ? ApiResult<Stream>.UnknownTranscript(problem!.Title ?? "No such transcript.")
                    : ApiResult<Stream>.Fail(problem?.Title ?? Describe(response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            return ApiResult<Stream>.Fail($"Could not reach the registry: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<ApiResult<IReadOnlyList<SessionSkill>>> GetSkillsAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, registry, $"{TranscriptPath(harness, key)}/skills", accessToken);
        return await SendAsync<IReadOnlyList<SessionSkill>>(
            request,
            async response => (await response.Content.ReadFromJsonAsync<SkillsBody>(TranscriptJson.Options, cancellationToken))?.Skills ?? [],
            cancellationToken);
    }

    private sealed record SkillsBody(IReadOnlyList<SessionSkill>? Skills);

    // The response body, which ends the response when disposed.
    private sealed class ResponseStream(Stream body, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => body.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri registry, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(registry, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static string TranscriptPath(string harness, string key) =>
        $"/v1/transcripts/{Uri.EscapeDataString(harness)}/{Uri.EscapeDataString(key)}";

    // machineId: the machine the request names, so a refusal for not knowing it comes back as ApiResult.MachineUnknown.
    private async Task<ApiResult<T>> SendAsync<T>(
        HttpRequestMessage request, Func<HttpResponseMessage, Task<T>> read, CancellationToken cancellationToken, Guid? machineId = null)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return ApiResult<T>.Success(await read(response));

            var problem = await ReadProblemAsync(response, cancellationToken);
            if (machineId is { } id && IsUnknownMachine(response, problem, id))
                return ApiResult<T>.UnknownMachine(problem!.Title!);
            if (IsUnknownTranscript(response, problem))
                return ApiResult<T>.UnknownTranscript(problem!.Title ?? "No such transcript.");
            if (response.StatusCode == HttpStatusCode.NotFound && problem?.Type is UnknownProjectType or UnknownMemoryFileType)
                return ApiResult<T>.UnknownMemory(problem.Title ?? "No such project or memory file.");
            return ApiResult<T>.Fail(problem?.Title ?? Describe(response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Fail($"Could not reach the registry: {ex.Message}");
        }
    }

    private static async Task<TranscriptPosition> ReadPositionAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<PositionBody>(TranscriptJson.Options);
        return new TranscriptPosition(body?.Revision ?? 1, body?.Length ?? 0);
    }

    private static async Task<ProblemBody?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json"))
            return null;
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemBody>(TranscriptJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The problem type identifies the refusal (registry#41). Registries from before it send only a 400 whose title names
    // the machine ("Machine <id> is not registered."), so that exact title still counts; other 400s (a bad key, a
    // corrupt chunk) must never look like it.
    private static bool IsUnknownMachine(HttpResponseMessage response, ProblemBody? problem, Guid machineId) =>
        response.StatusCode == HttpStatusCode.BadRequest &&
        (problem?.Type == UnknownMachineType ||
         problem?.Title?.StartsWith($"Machine {machineId} is not registered.", StringComparison.OrdinalIgnoreCase) == true);

    // Only the problem type, never a bare 404: a 404 from a proxy or a wrong base URL must not make the caller upload
    // a whole transcript again from offset 0.
    private static bool IsUnknownTranscript(HttpResponseMessage response, ProblemBody? problem) =>
        response.StatusCode == HttpStatusCode.NotFound && problem?.Type == UnknownTranscriptType;

    private const string UnknownMachineType = "https://protostar.dev/problems/unknown-machine";
    private const string UnknownTranscriptType = "https://protostar.dev/problems/unknown-transcript";
    private const string UnknownProjectType = "https://protostar.dev/problems/unknown-project";
    private const string UnknownMemoryFileType = "https://protostar.dev/problems/unknown-memory-file";

    private static string Describe(HttpStatusCode status) => status == HttpStatusCode.Unauthorized
        ? "The registry rejected the session. Run `protostar auth login`."
        : $"The registry returned {(int)status}.";

    private static byte[] Gzip(ReadOnlySpan<byte> raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(raw);
        return output.ToArray();
    }

    private sealed record PositionBody(int Revision, long Length);

    private sealed record ProblemBody(string? Type, string? Title, int? Revision, long? Length);
}
