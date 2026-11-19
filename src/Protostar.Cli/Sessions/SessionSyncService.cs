using System.Diagnostics;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
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
/// <param name="Stopped">Whether the run was cancelled (Ctrl+C) before it finished; what it had not sent goes next time.</param>
internal sealed record SessionSyncResult(
    SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<TranscriptSyncOutcome> Transcripts, bool Stopped = false)
{
    /// <summary>True when the run went ahead and nothing failed or was left over.</summary>
    public bool Complete => Failure == SessionSyncFailure.None &&
        Transcripts.All(t => t.Status is TranscriptSyncStatus.UpToDate or TranscriptSyncStatus.Uploaded);

    public static SessionSyncResult Fail(SessionSyncFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, []);
}

/// <summary>Progress during a sync, for a progress bar.</summary>
/// <remarks>
/// Counts only the transcripts that had something to upload when the run was planned, so a second push after an
/// interrupted one shows just what remains. A transcript counts as done once it is finished with, whether it was
/// uploaded, skipped or failed, and its planned bytes then count as done too, so the totals always add up.
/// Within a transcript the bytes move chunk by chunk.
/// </remarks>
/// <param name="TranscriptsDone">Transcripts finished with so far.</param>
/// <param name="TranscriptsTotal">Transcripts that had something to upload.</param>
/// <param name="BytesDone">Bytes accounted for so far, of <paramref name="BytesTotal"/>.</param>
/// <param name="BytesTotal">Bytes the registry did not have when the run was planned.</param>
/// <param name="Current">The transcript being uploaded, or <c>null</c> before the first and after the last.</param>
internal sealed record SessionSyncProgress(int TranscriptsDone, int TranscriptsTotal, long BytesDone, long BytesTotal, string? Current);

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
/// <para>
/// Registering a transcript sends the project facts of the working directory its lines record, from
/// <see cref="IProjectFactsResolver"/>, so the registry can file it under its project (ADR 0002). A transcript
/// registered before this existed is registered again once, with facts, even when nothing else changed.
/// </para>
/// <para>Uploads within a run are sequential; the per-transcript lock stops two processes uploading the same file.</para>
/// <para>
/// A run first plans every transcript, then uploads the ones with something new, most recently written first, so a
/// first backup of a large history secures the sessions that matter most before the old ones. Planning reads only
/// file sizes, write times and the ledger, plus at most a 4 KiB tail fingerprint and a scan to the next newline for
/// a transcript already partly backed up, so it costs the same for 17 GB as for 17 MB. The ledger advances only
/// after the registry accepts a chunk, so a run stopped at any point (Ctrl+C, a crash, the machine sleeping) loses
/// nothing: the next run resumes each transcript from its last accepted chunk, and a chunk the registry took but
/// the ledger missed comes back as a <c>409</c> with the registry's position.
/// </para>
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
    private readonly IProjectFactsResolver _projects;
    private readonly TimeProvider _time;

    public SessionSyncService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IRegistryApi meta, ITranscriptApi api,
        ISessionLedger ledger, IMachineIdentity machine, IProjectFactsResolver projects, TimeProvider time)
    {
        _catalog = catalog;
        _tokens = tokens;
        _meta = meta;
        _api = api;
        _ledger = ledger;
        _machine = machine;
        _projects = projects;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken)
    {
        if (!TryResolve(request.Registry, request.HarnessId, out var registry, out var harnesses, out var failure, out var detail))
            return SessionSyncResult.Fail(failure, detail, registry);

        var planned = Locate(harnesses, request.HarnessHome, request.SessionPath)
            .Select(transcript => (Transcript: transcript, Plan: Plan(registry, transcript)))
            .ToList();
        var outcomes = new List<TranscriptSyncOutcome>(planned.Count);
        outcomes.AddRange(planned.Where(p => p.Plan.UpToDate).Select(p => UpToDate(p.Transcript)));

        var work = planned
            .Where(p => !p.Plan.UpToDate)
            .OrderByDescending(p => p.Plan.Modified)
            .ThenBy(p => p.Transcript.Path, StringComparer.Ordinal)
            .ToList();
        var bytesTotal = work.Sum(p => p.Plan.Pending);
        var done = 0;
        long bytesDone = 0;
        progress?.Report(new SessionSyncProgress(0, work.Count, 0, bytesTotal, null));

        var clock = Stopwatch.StartNew();
        Session? session = null;
        var stopped = false;

        foreach (var (transcript, plan) in work)
        {
            if (stopped || cancellationToken.IsCancellationRequested)
            {
                // Never started, so its ledger entry is untouched and the next run plans it afresh.
                stopped = true;
                outcomes.Add(Skipped(transcript, plan, StoppedMessage));
                continue;
            }

            progress?.Report(new SessionSyncProgress(done, work.Count, bytesDone, bytesTotal, transcript.Key));
            TranscriptSyncOutcome outcome;

            if (Throttled(request, plan))
            {
                outcome = Skipped(transcript, plan, ThrottledMessage);
            }
            else if (request.Budget is { } budget && clock.Elapsed >= budget)
            {
                outcome = Skipped(transcript, plan, "Out of time this run; it goes next time.");
            }
            else
            {
                // The network is needed from here on: connect once, lazily, so a run with nothing new stays offline.
                if (session is null)
                {
                    (SessionSyncFailure Failure, string? Detail, Session? Session) connected;
                    try
                    {
                        connected = await ConnectAsync(registry, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        stopped = true;
                        outcomes.Add(Skipped(transcript, plan, StoppedMessage));
                        continue;
                    }

                    if (connected.Failure != SessionSyncFailure.None)
                        return SessionSyncResult.Fail(connected.Failure, connected.Detail, registry);
                    session = connected.Session!;
                }

                var pending = plan.Pending;
                outcome = await UploadUnderLockAsync(
                    registry, session, transcript, request, clock,
                    sent => progress?.Report(new SessionSyncProgress(done, work.Count, bytesDone + Math.Min(sent, pending), bytesTotal, transcript.Key)),
                    cancellationToken);
            }

            outcomes.Add(outcome);
            stopped = cancellationToken.IsCancellationRequested;
            if (stopped)
                continue;
            done++;
            bytesDone += plan.Pending;
            progress?.Report(new SessionSyncProgress(done, work.Count, bytesDone, bytesTotal, null));
        }

        return new SessionSyncResult(SessionSyncFailure.None, null, registry, outcomes, stopped);
    }

    // Takes the transcript's lock and uploads from a fresh plan, or says why it did not.
    private async Task<TranscriptSyncOutcome> UploadUnderLockAsync(
        Uri registry, Session session, LocalTranscript transcript, SessionSyncRequest request, Stopwatch clock, Action<long> sent,
        CancellationToken cancellationToken)
    {
        using var held = _ledger.TryLock(registry, transcript.Harness, transcript.Key);
        if (held is null)
            return Skipped(transcript, Plan(registry, transcript), "Another protostar process is uploading it.");

        // Plan again under the lock: another process (a push from an earlier hook) may have uploaded since,
        // and uploading from the stale plan would write its old position back over the ledger.
        var plan = Plan(registry, transcript);
        if (plan.UpToDate)
            return UpToDate(transcript);

        if (Throttled(request, plan))
            return Skipped(transcript, plan, ThrottledMessage);

        return await UploadAsync(registry, session, transcript, plan, request.Budget, clock, sent, cancellationToken);
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
            var backedUp = plan.Rewritten ? 0 : plan.Entry?.Length ?? 0;
            statuses.Add(new TranscriptStatus(
                transcript, plan.Size, backedUp, plan.Pending, plan.Rewritten, plan.Entry?.LastSyncedAt, plan.Modified));
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

    private sealed record UploadPlan(SessionLedgerEntry? Entry, long Size, DateTimeOffset Modified, bool UpToDate, bool Rewritten)
    {
        /// <summary>Bytes on disk the registry does not have yet.</summary>
        public long Pending => UpToDate ? 0 : Math.Max(0, Size - (Rewritten ? 0 : Entry?.Length ?? 0));
    }

    // Decides from the file and the ledger alone whether anything needs uploading. It never reads the bytes still to
    // upload, only a stat, the ledger entry, a 4 KiB fingerprint and the scan to the next newline after it.
    private UploadPlan Plan(Uri registry, LocalTranscript transcript)
    {
        var (size, modified) = Stat(transcript.Path);
        var entry = _ledger.Get(registry, transcript.Harness, transcript.Key);
        if (entry is null || !entry.Registered)
            return new UploadPlan(entry, size, modified, false, false);

        var rewritten = size < entry.Length || SessionLedger.TailHash(transcript.Path, entry.Length) != entry.TailSha256;
        if (rewritten)
            return new UploadPlan(entry, size, modified, false, true);

        // Bytes past the stored length that do not yet form a whole line are not uploadable yet. A transcript
        // registered before project facts existed is not up to date until it has been registered again with them.
        var upToDate = (size == entry.Length || !HasCompleteLineAfter(transcript.Path, entry.Length)) && entry.Project is not null;
        return new UploadPlan(entry, size, modified, upToDate, false);
    }

    private const string StoppedMessage = "Stopped part way; the rest goes next time.";

    private static TranscriptSyncOutcome UpToDate(LocalTranscript transcript) =>
        new(transcript, TranscriptSyncStatus.UpToDate, 0, 0, false, null);

    private const string ThrottledMessage = "Synced moments ago; waiting for the throttle window.";

    private bool Throttled(SessionSyncRequest request, UploadPlan plan) =>
        request.Throttle is { } throttle && plan.Entry is { Registered: true, LastAttemptAt: { } last } &&
        _time.GetUtcNow() - last < throttle;

    private static TranscriptSyncOutcome Skipped(LocalTranscript transcript, UploadPlan plan, string reason) =>
        new(transcript, TranscriptSyncStatus.Skipped, 0, plan.Pending, plan.Rewritten, reason);

    // ── uploading ─────────────────────────────────────────────────────────────────

    private sealed record Session(string AccessToken, Guid MachineId);

    // Reports the bytes sent so far through onSent after each accepted chunk. Cancellation stops it between or during
    // chunks and keeps what was sent: the ledger only ever holds a position the registry confirmed.
    private async Task<TranscriptSyncOutcome> UploadAsync(
        Uri registry, Session session, LocalTranscript transcript, UploadPlan plan, TimeSpan? budget, Stopwatch clock,
        Action<long> onSent, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var entry = plan.Entry;
        int revision;
        long offset = 0;
        long sent = 0;

        var filing = entry?.Project;
        if (entry is null || !entry.Registered || filing != ProjectFiling.Sent)
        {
            var project = ProjectFactsFor(transcript);
            ApiResult<TranscriptPosition> registered;
            try
            {
                registered = await _api.RegisterTranscriptAsync(registry, session.AccessToken, transcript, session.MachineId, project, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Stopped();
            }

            if (!registered.Ok)
                return Failed(transcript, plan, 0, registered.Error!);
            filing = project is null ? ProjectFiling.Pending : ProjectFiling.Sent;

            // Record the registry's real position. It may already hold part of this file (for example from before
            // the ledger was lost); if it holds more than the file now has, the file was rewritten: start over.
            var position = registered.Value!;
            _ledger.Save(registry, transcript.Harness, transcript.Key, new SessionLedgerEntry(
                position.Revision, position.Length, SessionLedger.TailHash(transcript.Path, position.Length), true, null, now, filing));
            (revision, offset) = position.Length <= plan.Size ? (position.Revision, position.Length) : (position.Revision + 1, 0);
        }
        else
        {
            // Only the attempt time changes until a chunk lands, so a failure here never moves the position.
            _ledger.Save(registry, transcript.Harness, transcript.Key, entry with { LastAttemptAt = now });
            (revision, offset) = plan.Rewritten ? (entry.Revision + 1, 0) : (entry.Revision, entry.Length);
        }

        var mismatches = 0;
        while (true)
        {
            if (budget is { } limit && clock.Elapsed >= limit)
                return Finish(TranscriptSyncStatus.Partial, "Out of time this run; the rest goes next time.");
            if (cancellationToken.IsCancellationRequested)
                return Stopped();

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

            AppendResult result;
            try
            {
                result = await _api.AppendAsync(registry, session.AccessToken, transcript.Harness, transcript.Key, revision, offset, session.MachineId, chunk, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The registry may have taken this chunk; if so, the next run's append gets a 409 with its position.
                return Stopped();
            }

            switch (result.Status)
            {
                case AppendStatus.Appended:
                    offset += chunk.Length;
                    sent += chunk.Length;
                    _ledger.Save(registry, transcript.Harness, transcript.Key, new SessionLedgerEntry(
                        revision, offset, SessionLedger.TailHash(transcript.Path, offset), true, _time.GetUtcNow(), now, filing));
                    onSent(sent);
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

        TranscriptSyncOutcome Stopped() => sent > 0 ? Finish(TranscriptSyncStatus.Partial, StoppedMessage) : Skipped(transcript, plan, StoppedMessage);
    }

    // The facts of the directory the transcript's session ran in, or null while its file records none.
    private ProjectFacts? ProjectFactsFor(LocalTranscript transcript) =>
        _catalog.ById(transcript.Harness) is ISessionCapability sessions && sessions.ReadWorkingDirectory(transcript) is { } directory
            ? _projects.Resolve(directory)
            : null;

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

    private static (long Size, DateTimeOffset Modified) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0, DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, DateTimeOffset.MinValue);
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
