using System.Diagnostics;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>Backs up session transcripts to the registry, and reports what is backed up.</summary>
/// <remarks>Shared by <c>protostar sessions push</c>, <c>sessions status</c> and the capture hooks.</remarks>
internal interface ISessionSyncService
{
    /// <summary>Uploads whatever the registry does not have yet of the requested transcripts.</summary>
    Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Compares the transcripts on disk with the ledger, without contacting the registry.</summary>
    SessionStatusResult Status(string? registry, string? harnessId, string? harnessHome);

    /// <summary>Lists the sessions the registry holds, from every machine.</summary>
    Task<SessionListResult> ListAsync(string? registry, TranscriptListQuery query, CancellationToken cancellationToken);
}

/// <summary>Sessions the registry holds, or why they could not be listed.</summary>
internal sealed record SessionListResult(SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<RemoteTranscript> Sessions);

/// <summary>What to sync.</summary>
/// <param name="Registry">The <c>--registry</c> option, or <c>null</c> for the environment or default.</param>
/// <param name="HarnessId">Only this harness; <c>null</c> for all.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="SessionPath">Sync only this session (and its subagents), as a hook names it; <c>null</c> for every transcript.</param>
/// <param name="Budget">Stop starting new chunks after this long; <c>null</c> for no limit. Unfinished transcripts carry on next time.</param>
/// <param name="Throttle">Skip a transcript whose last attempt was within this long; <c>null</c> to never skip.</param>
internal sealed record SessionSyncRequest(
    string? Registry, string? HarnessId, string? HarnessHome, string? SessionPath, TimeSpan? Budget, TimeSpan? Throttle);

/// <summary>Why a sync stopped before looking at any transcript.</summary>
internal enum SessionSyncFailure
{
    None,
    InvalidRegistry,
    UnknownHarness,
    NotLoggedIn,
    SessionExpired,
    RegistryUnreachable,
    Incompatible,
}

/// <summary>What happened to one transcript.</summary>
internal enum TranscriptSyncStatus
{
    /// <summary>The registry already had all of it.</summary>
    UpToDate,

    /// <summary>New content was uploaded and the registry now has all of it.</summary>
    Uploaded,

    /// <summary>Some content was uploaded, but the time budget ran out; the rest goes next time.</summary>
    Partial,

    /// <summary>Not attempted: another process is uploading it, or it was attempted too recently.</summary>
    Skipped,

    /// <summary>The upload failed; see the message.</summary>
    Failed,
}

/// <summary>One transcript's outcome.</summary>
/// <param name="Transcript">The transcript.</param>
/// <param name="Status">What happened.</param>
/// <param name="BytesUploaded">Uncompressed bytes sent this run.</param>
/// <param name="BytesPending">Bytes on disk the registry still does not have.</param>
/// <param name="Rewritten">Whether the file had been rewritten, so a new revision was started.</param>
/// <param name="Message">Why it was skipped or failed.</param>
internal sealed record TranscriptSyncOutcome(
    LocalTranscript Transcript, TranscriptSyncStatus Status, long BytesUploaded, long BytesPending, bool Rewritten, string? Message);

/// <summary>The result of a sync run.</summary>
internal sealed record SessionSyncResult(SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<TranscriptSyncOutcome> Transcripts)
{
    /// <summary>True when the run went ahead and nothing failed or was left over.</summary>
    public bool Complete => Failure == SessionSyncFailure.None &&
        Transcripts.All(t => t.Status is TranscriptSyncStatus.UpToDate or TranscriptSyncStatus.Uploaded);

    public static SessionSyncResult Fail(SessionSyncFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, []);
}

/// <summary>Progress during a sync, for a progress bar.</summary>
internal sealed record SessionSyncProgress(int TranscriptsDone, int TranscriptsTotal, long BytesUploaded, string? Current);

/// <summary>A transcript's backup state on this machine.</summary>
/// <param name="Transcript">The transcript.</param>
/// <param name="SizeOnDisk">Its size now.</param>
/// <param name="BytesBackedUp">What the ledger says the registry has.</param>
/// <param name="BytesPending">What is still to upload.</param>
/// <param name="Rewritten">Whether the file no longer matches what was uploaded, so it would be uploaded again in full.</param>
/// <param name="LastSyncedAt">When the registry last confirmed it.</param>
/// <param name="LastModifiedAt">When the file last changed.</param>
internal sealed record TranscriptStatus(
    LocalTranscript Transcript, long SizeOnDisk, long BytesBackedUp, long BytesPending, bool Rewritten,
    DateTimeOffset? LastSyncedAt, DateTimeOffset LastModifiedAt);

/// <summary>Every transcript's backup state on this machine.</summary>
internal sealed record SessionStatusResult(SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<TranscriptStatus> Transcripts)
{
    public bool FullySynced => Failure == SessionSyncFailure.None && Transcripts.All(t => t.BytesPending == 0);
}

/// <summary>Production <see cref="ISessionSyncService"/>.</summary>
/// <remarks>
/// <para>
/// Per transcript: compare the file with the ledger first, so an unchanged transcript costs no network call.
/// When the bytes the registry already has no longer match the file (it shrank, or its tail changed), start
/// a new revision from offset 0. Otherwise read from the stored length in chunks of whole lines, never
/// including a final line still being written, and append each one. A <c>409</c> carries the registry's real
/// position, which is adopted and the upload resumed from there.
/// </para>
/// <para>Uploads within a run are sequential; the per-transcript lock stops two processes uploading the same file.</para>
/// </remarks>
internal sealed class SessionSyncService : ISessionSyncService
{
    /// <summary>Upload size per request, before compression.</summary>
    internal const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>The longest line accepted; a longer line could never fit in a chunk.</summary>
    internal const int MaxLineBytes = 16 * 1024 * 1024;

    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _meta;
    private readonly ITranscriptApi _api;
    private readonly ISessionLedger _ledger;
    private readonly IMachineIdentity _machine;
    private readonly TimeProvider _time;

    public SessionSyncService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IRegistryApi meta, ITranscriptApi api,
        ISessionLedger ledger, IMachineIdentity machine, TimeProvider time)
    {
        _catalog = catalog;
        _tokens = tokens;
        _meta = meta;
        _api = api;
        _ledger = ledger;
        _machine = machine;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken)
    {
        if (!TryResolve(request.Registry, request.HarnessId, out var registry, out var harnesses, out var failure, out var detail))
            return SessionSyncResult.Fail(failure, detail, registry);

        var transcripts = Locate(harnesses, request.HarnessHome, request.SessionPath);
        var clock = Stopwatch.StartNew();
        var outcomes = new List<TranscriptSyncOutcome>(transcripts.Count);
        Session? session = null;
        long uploaded = 0;

        for (var i = 0; i < transcripts.Count; i++)
        {
            var transcript = transcripts[i];
            progress?.Report(new SessionSyncProgress(i, transcripts.Count, uploaded, transcript.Key));

            var plan = Plan(registry, transcript);
            if (plan.UpToDate)
            {
                outcomes.Add(new TranscriptSyncOutcome(transcript, TranscriptSyncStatus.UpToDate, 0, 0, false, null));
                continue;
            }

            if (request.Throttle is { } throttle && plan.Entry is { Registered: true, LastAttemptAt: { } last } &&
                _time.GetUtcNow() - last < throttle)
            {
                outcomes.Add(Skipped(transcript, plan, "Synced moments ago; waiting for the throttle window."));
                continue;
            }

            if (request.Budget is { } budget && clock.Elapsed >= budget)
            {
                outcomes.Add(Skipped(transcript, plan, "Out of time this run; it goes next time."));
                continue;
            }

            // The network is needed from here on: connect once, lazily, so a run with nothing new stays offline.
            if (session is null)
            {
                var connected = await ConnectAsync(registry, cancellationToken);
                if (connected.Failure != SessionSyncFailure.None)
                    return SessionSyncResult.Fail(connected.Failure, connected.Detail, registry);
                session = connected.Session!;
            }

            using var held = _ledger.TryLock(registry, transcript.Harness, transcript.Key);
            if (held is null)
            {
                outcomes.Add(Skipped(transcript, plan, "Another protostar process is uploading it."));
                continue;
            }

            var outcome = await UploadAsync(registry, session, transcript, plan, request.Budget, clock, cancellationToken);
            uploaded += outcome.BytesUploaded;
            outcomes.Add(outcome);
        }

        progress?.Report(new SessionSyncProgress(transcripts.Count, transcripts.Count, uploaded, null));
        return new SessionSyncResult(SessionSyncFailure.None, null, registry, outcomes);
    }

    /// <inheritdoc />
    public SessionStatusResult Status(string? registryOption, string? harnessId, string? harnessHome)
    {
        if (!TryResolve(registryOption, harnessId, out var registry, out var harnesses, out var failure, out var detail))
            return new SessionStatusResult(failure, detail, registry, []);

        var statuses = new List<TranscriptStatus>();
        foreach (var transcript in Locate(harnesses, harnessHome, null))
        {
            var plan = Plan(registry, transcript);
            var info = new FileInfo(transcript.Path);
            var backedUp = plan.Rewritten ? 0 : plan.Entry?.Length ?? 0;
            statuses.Add(new TranscriptStatus(
                transcript, plan.Size, backedUp, plan.UpToDate ? 0 : Math.Max(0, plan.Size - backedUp), plan.Rewritten,
                plan.Entry?.LastSyncedAt, info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.MinValue));
        }

        return new SessionStatusResult(SessionSyncFailure.None, null, registry, statuses);
    }

    /// <inheritdoc />
    public async Task<SessionListResult> ListAsync(string? registryOption, TranscriptListQuery query, CancellationToken cancellationToken)
    {
        if (!TryResolve(registryOption, null, out var registry, out _, out var failure, out var detail))
            return new SessionListResult(failure, detail, registry, []);

        var stored = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (stored is null)
            return new SessionListResult(SessionSyncFailure.NotLoggedIn, null, registry, []);
        if (stored.IsExpired)
            return new SessionListResult(SessionSyncFailure.SessionExpired, null, registry, []);

        var listed = await _api.ListAsync(registry, stored.AccessToken, query, cancellationToken);
        return listed.Ok
            ? new SessionListResult(SessionSyncFailure.None, null, registry, listed.Value!)
            : new SessionListResult(SessionSyncFailure.RegistryUnreachable, listed.Error, registry, []);
    }

    // ── planning ──────────────────────────────────────────────────────────────────

    private sealed record UploadPlan(SessionLedgerEntry? Entry, long Size, bool UpToDate, bool Rewritten);

    // Decides from the file and the ledger alone whether anything needs uploading.
    private UploadPlan Plan(Uri registry, LocalTranscript transcript)
    {
        var size = SafeLength(transcript.Path);
        var entry = _ledger.Get(registry, transcript.Harness, transcript.Key);
        if (entry is null || !entry.Registered)
            return new UploadPlan(entry, size, false, false);

        var rewritten = size < entry.Length || SessionLedger.TailHash(transcript.Path, entry.Length) != entry.TailSha256;
        if (rewritten)
            return new UploadPlan(entry, size, false, true);

        // Bytes past the stored length that do not yet form a whole line are not uploadable yet.
        var upToDate = size == entry.Length || !HasCompleteLineAfter(transcript.Path, entry.Length);
        return new UploadPlan(entry, size, upToDate, false);
    }

    private static TranscriptSyncOutcome Skipped(LocalTranscript transcript, UploadPlan plan, string reason) =>
        new(transcript, TranscriptSyncStatus.Skipped, 0, Math.Max(0, plan.Size - (plan.Rewritten ? 0 : plan.Entry?.Length ?? 0)), plan.Rewritten, reason);

    // ── uploading ─────────────────────────────────────────────────────────────────

    private sealed record Session(string AccessToken, Guid MachineId);

    private async Task<TranscriptSyncOutcome> UploadAsync(
        Uri registry, Session session, LocalTranscript transcript, UploadPlan plan, TimeSpan? budget, Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var entry = plan.Entry;
        int revision;
        long offset;

        if (entry is null || !entry.Registered)
        {
            var registered = await _api.RegisterTranscriptAsync(registry, session.AccessToken, transcript, session.MachineId, cancellationToken);
            if (!registered.Ok)
                return Failed(transcript, plan, 0, registered.Error!);

            // Record the registry's real position. It may already hold part of this file (for example from before
            // the ledger was lost); if it holds more than the file now has, the file was rewritten: start over.
            var position = registered.Value!;
            _ledger.Save(registry, transcript.Harness, transcript.Key, new SessionLedgerEntry(
                position.Revision, position.Length, SessionLedger.TailHash(transcript.Path, position.Length), true, null, now));
            (revision, offset) = position.Length <= plan.Size ? (position.Revision, position.Length) : (position.Revision + 1, 0);
        }
        else
        {
            // Only the attempt time changes until a chunk lands, so a failure here never moves the position.
            _ledger.Save(registry, transcript.Harness, transcript.Key, entry with { LastAttemptAt = now });
            (revision, offset) = plan.Rewritten ? (entry.Revision + 1, 0) : (entry.Revision, entry.Length);
        }

        long sent = 0;
        var mismatches = 0;
        while (true)
        {
            if (budget is { } limit && clock.Elapsed >= limit)
                return Finish(TranscriptSyncStatus.Partial, "Out of time this run; the rest goes next time.");

            byte[] chunk;
            try
            {
                chunk = ReadWholeLines(transcript.Path, offset);
            }
            catch (LineTooLongException)
            {
                return Failed(transcript, plan, sent, $"A line at byte {offset} is longer than {MaxLineBytes / (1024 * 1024)} MiB.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(transcript, plan, sent, $"Could not read the file: {ex.Message}");
            }

            if (chunk.Length == 0)
                return Finish(sent > 0 || plan.Rewritten ? TranscriptSyncStatus.Uploaded : TranscriptSyncStatus.UpToDate, null);

            var result = await _api.AppendAsync(registry, session.AccessToken, transcript.Harness, transcript.Key, revision, offset, session.MachineId, chunk, cancellationToken);
            switch (result.Status)
            {
                case AppendStatus.Appended:
                    offset += chunk.Length;
                    sent += chunk.Length;
                    _ledger.Save(registry, transcript.Harness, transcript.Key, new SessionLedgerEntry(
                        revision, offset, SessionLedger.TailHash(transcript.Path, offset), true, _time.GetUtcNow(), now));
                    break;

                case AppendStatus.Mismatch when result.Position is { } position && ++mismatches <= 3:
                    // Adopt the registry's position: resume from it when the file reaches that far, else start over.
                    (revision, offset) = position.Length <= SafeLength(transcript.Path)
                        ? (position.Revision, position.Length)
                        : (position.Revision + 1, 0);
                    break;

                case AppendStatus.Mismatch:
                    return Failed(transcript, plan, sent, "The registry's position kept moving; another machine may be uploading the same transcript.");

                default:
                    return Failed(transcript, plan, sent, result.Message ?? "The upload failed.");
            }
        }

        TranscriptSyncOutcome Finish(TranscriptSyncStatus status, string? message) =>
            new(transcript, status, sent, Math.Max(0, SafeLength(transcript.Path) - offset), plan.Rewritten, message);
    }

    private static TranscriptSyncOutcome Failed(LocalTranscript transcript, UploadPlan plan, long sent, string message) =>
        new(transcript, TranscriptSyncStatus.Failed, sent, Math.Max(0, plan.Size - (plan.Rewritten ? 0 : plan.Entry?.Length ?? 0) - sent), plan.Rewritten, message);

    private async Task<(SessionSyncFailure Failure, string? Detail, Session? Session)> ConnectAsync(Uri registry, CancellationToken cancellationToken)
    {
        var stored = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (stored is null)
            return (SessionSyncFailure.NotLoggedIn, null, null);
        if (stored.IsExpired)
            return (SessionSyncFailure.SessionExpired, null, null);

        var meta = await _meta.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return (SessionSyncFailure.RegistryUnreachable, null, null);
        if (ApiCompatibility.Check(meta) is { } incompatible)
            return (SessionSyncFailure.Incompatible, incompatible, null);

        var machine = _machine.Get();
        if (!_machine.IsRegisteredWith(registry))
        {
            var registered = await _api.RegisterMachineAsync(registry, stored.AccessToken, machine.Id, machine.Name, machine.OperatingSystem, cancellationToken);
            if (!registered.Ok)
                return (SessionSyncFailure.RegistryUnreachable, registered.Error, null);
            _machine.MarkRegistered(registry);
        }

        return (SessionSyncFailure.None, null, new Session(stored.AccessToken, machine.Id));
    }

    // ── file reading ──────────────────────────────────────────────────────────────

    // Reads from offset up to ChunkBytes, cut after the last newline. Returns empty when no whole line follows.
    // A line longer than ChunkBytes is read whole, up to MaxLineBytes.
    internal static byte[] ReadWholeLines(string path, long offset)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset >= file.Length)
            return [];

        file.Position = offset;
        var window = (int)Math.Min(ChunkBytes, file.Length - offset);
        var buffer = new byte[window];
        file.ReadExactly(buffer);

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline >= 0)
            return lastNewline == buffer.Length - 1 ? buffer : buffer[..(lastNewline + 1)];

        // No newline in a full window: one very long line. Read on until it ends, within the hard limit.
        if (window < ChunkBytes)
            return [];

        var extended = new byte[(int)Math.Min(MaxLineBytes, file.Length - offset)];
        file.Position = offset;
        file.ReadExactly(extended);
        var end = Array.IndexOf(extended, (byte)'\n', ChunkBytes);
        if (end >= 0)
            return extended[..(end + 1)];
        if (extended.Length >= MaxLineBytes)
            throw new LineTooLongException();
        return [];
    }

    private static bool HasCompleteLineAfter(string path, long offset)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset >= file.Length)
                return false;
            file.Position = offset;
            var buffer = new byte[81920];
            int read;
            while ((read = file.Read(buffer)) > 0)
            {
                if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ── resolution ────────────────────────────────────────────────────────────────

    private bool TryResolve(string? registryOption, string? harnessId, out Uri registry, out IReadOnlyList<IHarness> harnesses,
        out SessionSyncFailure failure, out string? detail)
    {
        registry = null!;
        harnesses = [];
        detail = null;
        try
        {
            registry = RegistryEndpoint.Resolve(registryOption);
        }
        catch (FormatException ex)
        {
            failure = SessionSyncFailure.InvalidRegistry;
            detail = ex.Message;
            return false;
        }

        if (harnessId is null)
        {
            harnesses = _catalog.All.Where(h => h is ISessionCapability).ToList();
        }
        else if (_catalog.ById(harnessId) is ISessionCapability and IHarness one)
        {
            harnesses = [one];
        }
        else
        {
            failure = SessionSyncFailure.UnknownHarness;
            detail = harnessId;
            return false;
        }

        failure = SessionSyncFailure.None;
        return true;
    }

    private static List<LocalTranscript> Locate(IReadOnlyList<IHarness> harnesses, string? harnessHome, string? sessionPath)
    {
        var transcripts = new List<LocalTranscript>();
        foreach (var harness in harnesses)
        {
            if (harness is not ISessionCapability sessions)
                continue;
            if (sessionPath is not null)
                transcripts.AddRange(sessions.LocateSession(sessionPath));
            else if (harness.TryLocate(harnessHome, out var location))
                transcripts.AddRange(sessions.LocateTranscripts(location));
        }

        return transcripts;
    }

    private sealed class LineTooLongException : Exception;
}
