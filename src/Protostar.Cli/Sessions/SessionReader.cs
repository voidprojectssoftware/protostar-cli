using System.Runtime.CompilerServices;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>Finds a backed-up session by id or id prefix and reads it back as a conversation, for <c>protostar sessions show</c>.</summary>
internal interface ISessionReader
{
    /// <summary>Finds the transcript <paramref name="id"/> names: a whole key, or the start of one session's key.</summary>
    /// <param name="harnessId">Only this harness; <c>null</c> for every harness that keeps sessions.</param>
    /// <remarks>
    /// A whole key is found directly, subagent keys included. Anything else is matched, ignoring case, against the
    /// start of the session keys in the registry; one match is opened, several are refused with the candidates.
    /// A registry that does not list <c>redacted-read</c> is refused before anything is asked, a registry too old to
    /// list capabilities included, since every read of the session is requested redacted and must come back so.
    /// </remarks>
    Task<SessionOpenResult> OpenAsync(string? registry, string? harnessId, string id, CancellationToken cancellationToken);

    /// <summary>The session's conversation, in order, read from the registry as it downloads.</summary>
    /// <param name="full">
    /// Also tool results, the lines of subagents written into the session's own file, and each subagent's transcript,
    /// nested right after the result of the call that started it. Without it, tool calls are listed (one entry each,
    /// a subagent's included) but nothing they returned.
    /// </param>
    /// <exception cref="SessionReadException">The download failed part way; the entries before it were already yielded.</exception>
    IAsyncEnumerable<ShownEntry> ReadAsync(OpenedSession session, bool full, CancellationToken cancellationToken);
}

/// <summary>Whether <see cref="ISessionReader.OpenAsync"/> found the transcript.</summary>
internal enum SessionOpenOutcome
{
    Found,

    /// <summary>No transcript has that key, and no session key starts with it.</summary>
    NotFound,

    /// <summary>More than one session key starts with it; <see cref="SessionOpenResult.Candidates"/> lists them.</summary>
    Ambiguous,

    /// <summary>The registry could not be asked; <see cref="SessionOpenResult.Failure"/> says why.</summary>
    Failed,
}

/// <summary>The result of looking a session up.</summary>
/// <param name="Candidates">For <see cref="SessionOpenOutcome.Ambiguous"/>, the matching sessions, newest first, at most <see cref="SessionReader.MaxCandidates"/>.</param>
internal sealed record SessionOpenResult(
    SessionOpenOutcome Outcome, SessionSyncFailure Failure, string? Detail, Uri? Registry, OpenedSession? Session,
    IReadOnlyList<RemoteTranscript> Candidates)
{
    public static SessionOpenResult Fail(SessionSyncFailure failure, string? detail, Uri? registry = null) =>
        new(SessionOpenOutcome.Failed, failure, detail, registry, null, []);
}

/// <summary>A transcript found in the registry, ready to read.</summary>
/// <param name="Format">The harness that reads its lines.</param>
internal sealed record OpenedSession(Uri Registry, string AccessToken, TranscriptDetail Detail, ISessionCapability Format);

/// <summary>One entry of a shown session, and where it sits.</summary>
/// <param name="TranscriptId">The id of the transcript the entry came from: the session's, or a subagent's.</param>
/// <param name="Depth">0 for the session itself, 1 inside a subagent.</param>
internal sealed record ShownEntry(string TranscriptId, int Depth, ConversationEntry Entry);

/// <summary>Where a subagent's transcript starts, nested in the session it ran in.</summary>
/// <param name="SubagentId">The subagent transcript's id.</param>
/// <param name="Linked">Whether it was matched to the call that started it; an unmatched one is shown after the session.</param>
internal sealed record SubagentStartEntry(string SubagentId, string? AgentType, string? Description, bool Linked)
    : ConversationEntry(null, false);

/// <summary>Where a subagent's transcript ends.</summary>
internal sealed record SubagentEndEntry(string SubagentId) : ConversationEntry(null, false);

/// <summary>A transcript download that failed after reading began.</summary>
internal sealed class SessionReadException : Exception
{
    public SessionReadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Production <see cref="ISessionReader"/>.</summary>
/// <remarks>
/// <para>
/// The content is read line by line from the response as it arrives and each line is turned into entries on its own,
/// so a transcript of hundreds of megabytes is shown in constant memory. It is always requested redacted
/// (<see cref="ITranscriptApi.OpenRedactedContentAsync"/>), and nothing read is ever logged.
/// </para>
/// <para>
/// With <c>full</c>, subagent transcripts are downloaded to a private temporary folder before the session is read, and
/// read back from there where each ran. Downloading one in the middle of the session would leave the session's own
/// response unread meanwhile, and the server drops a response that is not read for long enough. The folder is
/// deleted when reading ends, however it ends.
/// </para>
/// <para>
/// A prefix is looked up in one request (<c>GET /v1/transcripts?keyPrefix=…&amp;limit=11</c>) when the registry lists
/// <c>transcript-prefix-lookup</c> and the prefix is one it accepts (at least <see cref="MinLookupPrefix"/> characters a
/// key may hold). Otherwise, on an older registry or for a shorter prefix, which the registry would refuse, it is matched
/// by paging through the registry's session list, 200 at a time, until a page comes back short or enough candidates
/// are found to refuse: one request per 200 sessions, and sessions with the same last-activity time on a page boundary
/// can be missed. A short prefix is still served that way rather than refused, since it works and older registries
/// need the paging anyway.
/// </para>
/// </remarks>
internal sealed class SessionReader : ISessionReader
{
    /// <summary>The most candidates an ambiguous prefix lists; past it, more of the id is needed anyway.</summary>
    internal const int MaxCandidates = 10;

    /// <summary>The shortest prefix the registry looks up itself (<c>transcript-prefix-lookup</c>); a shorter one is matched by paging.</summary>
    internal const int MinLookupPrefix = 4;

    private const int PageSize = 200;

    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryCapabilities _capabilities;
    private readonly ITranscriptApi _api;

    public SessionReader(IHarnessCatalog catalog, IAccessTokenProvider tokens, IRegistryCapabilities capabilities, ITranscriptApi api)
    {
        _catalog = catalog;
        _tokens = tokens;
        _capabilities = capabilities;
        _api = api;
    }

    /// <inheritdoc />
    public async Task<SessionOpenResult> OpenAsync(string? registryOption, string? harnessId, string id, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(registryOption);
        }
        catch (FormatException ex)
        {
            return SessionOpenResult.Fail(SessionSyncFailure.InvalidRegistry, ex.Message);
        }

        var harnesses = _catalog.All.Where(h => h is ISessionCapability && (harnessId is null || h.Id == harnessId)).ToList();
        if (harnesses.Count == 0)
            return SessionOpenResult.Fail(SessionSyncFailure.UnknownHarness, harnessId, registry);

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } stored })
            return SessionOpenResult.Fail(SessionSyncService.SessionFailure(lookup.State), null, registry);
        var token = stored.AccessToken;

        // Sessions as leniently as every command; redaction strictly, as the MCP tools do: a registry that cannot list
        // redacted-read may ignore redacted=true and serve the stored lines, secrets included, and agents run
        // `sessions show --json` through a shell (#122). /v1/meta is read once, so the second check asks nothing.
        var check = await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Sessions);
        if (check.Ok)
            check = await _capabilities.RequireListedAsync(registry, cancellationToken, RegistryCapability.RedactedRead);
        if (!check.Ok)
            return SessionOpenResult.Fail(SessionSyncService.CheckFailure(check), check.Detail, registry);

        // One request when the registry can look a prefix up itself; a whole subagent key is no session's prefix, so
        // nothing found that way still tries the id as a whole key. So does a prefix the registry refuses (400): no
        // session key starts with something the registry will not even look up.
        if (check.Supports(RegistryCapability.TranscriptPrefixLookup) == CapabilitySupport.Listed && IsLookupPrefix(id))
        {
            var found = await _api.ListAsync(
                registry, token, new TranscriptListQuery(null, null, harnessId, null, MaxCandidates + 1, KeyPrefix: id), cancellationToken);
            if (!found.Ok && !found.Refused)
                return SessionOpenResult.Fail(SessionSyncFailure.RegistryUnreachable, found.Error, registry);
            var matches = found.Ok ? found.Value!.Where(t => harnesses.Any(h => h.Id == t.Harness)).ToList() : [];
            if (matches.Count > 0)
                return await OpenMatchAsync(registry, token, harnesses, id, matches, cancellationToken);
            return await OpenExactAsync(registry, token, harnesses, id, cancellationToken)
                ?? new SessionOpenResult(SessionOpenOutcome.NotFound, SessionSyncFailure.None, null, registry, null, []);
        }

        if (await OpenExactAsync(registry, token, harnesses, id, cancellationToken) is { } exact)
            return exact;

        var candidates = new List<RemoteTranscript>();
        var harnessIds = harnesses.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        DateTimeOffset? before = null;
        while (true)
        {
            var page = await _api.ListAsync(registry, token, new TranscriptListQuery(null, null, harnessId, null, PageSize, before), cancellationToken);
            if (!page.Ok)
                return SessionOpenResult.Fail(SessionSyncFailure.RegistryUnreachable, page.Error, registry);
            candidates.AddRange(page.Value!.Where(t => harnessIds.Contains(t.Harness) && t.Key.StartsWith(id, StringComparison.OrdinalIgnoreCase)));
            if (page.Value!.Count < PageSize || candidates.Count > MaxCandidates || page.Value![^1].LastActivityAt is not { } last)
                break;
            before = last;
        }

        if (candidates.Count == 0)
            return new SessionOpenResult(SessionOpenOutcome.NotFound, SessionSyncFailure.None, null, registry, null, []);
        return await OpenMatchAsync(registry, token, harnesses, id, candidates, cancellationToken);
    }

    // Opens the one session the prefix matched (or the one whose whole key it is), or lists the candidates.
    private async Task<SessionOpenResult> OpenMatchAsync(
        Uri registry, string token, List<IHarness> harnesses, string id, List<RemoteTranscript> matches, CancellationToken cancellationToken)
    {
        var whole = matches.Where(t => string.Equals(t.Key, id, StringComparison.Ordinal)).ToList();
        if (matches.Count > 1 && whole.Count != 1)
            return new SessionOpenResult(SessionOpenOutcome.Ambiguous, SessionSyncFailure.None, null, registry, null, [.. matches.Take(MaxCandidates)]);

        var match = whole.Count == 1 ? whole[0] : matches[0];
        var detail = await _api.GetAsync(registry, token, match.Harness, match.Key, cancellationToken);
        if (!detail.Ok)
        {
            return detail.TranscriptUnknown
                ? new SessionOpenResult(SessionOpenOutcome.NotFound, SessionSyncFailure.None, null, registry, null, [])
                : SessionOpenResult.Fail(SessionSyncFailure.RegistryUnreachable, detail.Error, registry);
        }

        return Found(registry, token, harnesses.First(h => h.Id == match.Harness), detail.Value!);
    }

    // The transcript whose whole key is `id`, subagents included: found, ambiguous across harnesses, a failure, or null when none has it.
    private async Task<SessionOpenResult?> OpenExactAsync(
        Uri registry, string token, List<IHarness> harnesses, string id, CancellationToken cancellationToken)
    {
        var exact = new List<(IHarness Harness, TranscriptDetail Detail)>();
        foreach (var harness in harnesses)
        {
            var got = await _api.GetAsync(registry, token, harness.Id, id, cancellationToken);
            if (got.Ok)
                exact.Add((harness, got.Value!));
            else if (!got.TranscriptUnknown)
                return SessionOpenResult.Fail(SessionSyncFailure.RegistryUnreachable, got.Error, registry);
        }

        if (exact.Count == 1)
            return Found(registry, token, exact[0].Harness, exact[0].Detail);
        if (exact.Count > 1)
            return new SessionOpenResult(SessionOpenOutcome.Ambiguous, SessionSyncFailure.None, null, registry, null, [.. exact.Select(e => e.Detail.Transcript)]);
        return null;
    }

    // What the registry's keyPrefix accepts: at least 4 of the characters a key may hold. Anything else is matched by paging.
    internal static bool IsLookupPrefix(string id) =>
        id.Length >= MinLookupPrefix && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static SessionOpenResult Found(Uri registry, string token, IHarness harness, TranscriptDetail detail) =>
        new(SessionOpenOutcome.Found, SessionSyncFailure.None, null, registry, new OpenedSession(registry, token, detail, (ISessionCapability)harness), []);

    /// <inheritdoc />
    public async IAsyncEnumerable<ShownEntry> ReadAsync(OpenedSession session, bool full, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var transcript = session.Detail.Transcript;
        var spool = full && session.Detail.Subagents.Count > 0 ? Directory.CreateTempSubdirectory("protostar-show-") : null;
        try
        {
            var subagents = new Dictionary<string, (RemoteTranscript Transcript, string File)>(StringComparer.Ordinal);
            foreach (var subagent in spool is null ? [] : session.Detail.Subagents)
            {
                var file = Path.Combine(spool!.FullName, subagents.Count + ".jsonl");
                await DownloadAsync(session, subagent, file, cancellationToken);
                subagents[subagent.Key] = (subagent, file);
            }

            var calls = new Dictionary<string, ToolCallEntry>(StringComparer.Ordinal);
            await using var content = await OpenAsync(session, transcript, cancellationToken);
            using var reader = new StreamReader(content);
            while (await NextLineAsync(reader, transcript, cancellationToken) is { } line)
            {
                foreach (var read in session.Format.ReadConversationLine(line))
                {
                    if (!full && read is ToolResultEntry || !full && read.Sidechain)
                        continue;

                    // Only the result of a call that ran a subagent names one (#121): an agent id on any other result is
                    // text that happens to mention it, and must neither take the subagent nor reach the JSON.
                    var entry = read;
                    SubagentCall? subagent = null;
                    if (read is ToolResultEntry { AgentId: not null } named)
                    {
                        subagent = calls.GetValueOrDefault(named.ToolUseId)?.Subagent;
                        if (subagent is null)
                            entry = named with { AgentId = null };
                    }

                    yield return new ShownEntry(transcript.Key, entry.Sidechain ? 1 : 0, entry);

                    if (entry is ToolCallEntry call)
                        calls[call.Id] = call;
                    if (entry is ToolResultEntry { AgentId: { } agentId } &&
                        subagents.Remove($"{transcript.Key}.agent-{agentId}", out var ran))
                    {
                        await foreach (var nested in SubagentAsync(session, ran.Transcript, ran.File, subagent, linked: true, cancellationToken))
                            yield return nested;
                    }
                }
            }

            // Subagents no call result named (a version that recorded no agent id, or a session cut short) still show, after it.
            foreach (var (subagent, file) in subagents.Values.OrderBy(s => s.Transcript.StartedAt))
            {
                await foreach (var nested in SubagentAsync(session, subagent, file, null, linked: false, cancellationToken))
                    yield return nested;
            }
        }
        finally
        {
            spool?.Delete(recursive: true);
        }
    }

    private static async IAsyncEnumerable<ShownEntry> SubagentAsync(
        OpenedSession session, RemoteTranscript subagent, string file, SubagentCall? call, bool linked,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ShownEntry(subagent.Key, 1, new SubagentStartEntry(subagent.Key, call?.AgentType, call?.Description ?? subagent.Title, linked));
        using (var reader = new StreamReader(file))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                foreach (var entry in session.Format.ReadConversationLine(line))
                    yield return new ShownEntry(subagent.Key, 1, entry);
            }
        }

        yield return new ShownEntry(subagent.Key, 1, new SubagentEndEntry(subagent.Key));
    }

    private async Task<Stream> OpenAsync(OpenedSession session, RemoteTranscript transcript, CancellationToken cancellationToken)
    {
        var opened = await _api.OpenRedactedContentAsync(session.Registry, session.AccessToken, transcript.Harness, transcript.Key, cancellationToken);
        return opened.Ok
            ? opened.Value!
            : throw new SessionReadException($"Could not download {transcript.Key}: {opened.Error}", new IOException(opened.Error));
    }

    private async Task DownloadAsync(OpenedSession session, RemoteTranscript subagent, string file, CancellationToken cancellationToken)
    {
        await using var content = await OpenAsync(session, subagent, cancellationToken);
        await using var target = File.Create(file);
        try
        {
            await content.CopyToAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new SessionReadException($"The download of {subagent.Key} stopped part way: {ex.Message}", ex);
        }
    }

    // A line of the download, or null at its end; a dropped connection becomes a SessionReadException.
    private static async Task<string?> NextLineAsync(StreamReader reader, RemoteTranscript transcript, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new SessionReadException($"The download of {transcript.Key} stopped part way: {ex.Message}", ex);
        }
    }
}
