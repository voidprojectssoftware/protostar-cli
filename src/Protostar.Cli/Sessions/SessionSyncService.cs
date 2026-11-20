using System.Diagnostics;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>Backs up session transcripts to the registry, and reports what is backed up.</summary>
/// <remarks>Shared by <c>protostar sessions push</c>, <c>sessions status</c>, <c>sessions list</c> and the capture hooks.</remarks>
internal interface ISessionSyncService
{
    /// <summary>Uploads whatever the registry does not have yet of the requested transcripts.</summary>
    Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Compares the transcripts on disk with the ledger, without contacting the registry.</summary>
    SessionStatusResult Status(string? registry, string? harnessId, string? harnessHome);

    /// <summary>Compares the transcripts on disk with the ledger and with what the registry actually holds. Changes nothing.</summary>
    /// <remarks>
    /// The registry is asked about every transcript the ledger records, in batches; each one's status then carries
    /// <see cref="TranscriptStatus.Verified"/>, and its pending bytes count from what the registry holds.
    /// </remarks>
    Task<SessionStatusResult> VerifyAsync(string? registry, string? harnessId, string? harnessHome, CancellationToken cancellationToken);

    /// <summary>Lists the sessions the registry holds, from every machine, plus this machine's sessions not fully backed up yet.</summary>
    /// <remarks>
    /// <paramref name="query"/> filters both kinds of row. When the registry cannot be asked (not logged in, unreachable),
    /// the result carries the failure and still lists this machine's pending sessions.
    /// </remarks>
    Task<SessionListResult> ListAsync(string? registry, string? harnessHome, TranscriptListQuery query, CancellationToken cancellationToken);
}

/// <summary>The sessions to list, newest first, and why the registry's part is missing when it is.</summary>
/// <param name="Sessions">On a registry failure, only this machine's pending sessions; empty when the failure came before reading them.</param>
internal sealed record SessionListResult(SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<ListedSession> Sessions);

/// <summary>One row of <c>sessions list</c>.</summary>
/// <param name="Session">The registry's record, or for a session it does not have yet one made from the file: no title or counts, the file's size as its length.</param>
/// <param name="Pending">What this machine has not backed up yet; <c>null</c> when the registry has all of it, or the session is from another machine.</param>
internal sealed record ListedSession(RemoteTranscript Session, PendingBackup? Pending)
{
    /// <summary>The later of the registry's last activity and when the file last changed.</summary>
    public DateTimeOffset? When => Pending is { } pending && !(Session.LastActivityAt > pending.ChangedAt) ? pending.ChangedAt : Session.LastActivityAt;
}

/// <summary>A session on this machine with content the registry does not have yet.</summary>
/// <param name="Bytes">Bytes still to upload, across the session and its subagents.</param>
/// <param name="InRegistry">Whether the registry already holds part of it.</param>
/// <param name="FromFile">Whether the row was made from the file because the registry's list did not include it, so it has no title or counts.</param>
/// <param name="Path">The session's transcript file.</param>
/// <param name="ChangedAt">When the session or one of its subagents last changed.</param>
internal sealed record PendingBackup(long Bytes, bool InRegistry, bool FromFile, string Path, DateTimeOffset ChangedAt);

/// <summary>What to sync.</summary>
/// <param name="Registry">The <c>--registry</c> option, or <c>null</c> for the environment or default.</param>
/// <param name="HarnessId">Only this harness; <c>null</c> for all.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="SessionPath">Sync only this session (and its subagents), as a hook names it; <c>null</c> for every transcript.</param>
/// <param name="Budget">Stop starting new chunks after this long; <c>null</c> for no limit. Unfinished transcripts carry on next time.</param>
/// <param name="Throttle">Skip a transcript whose last attempt was within this long; <c>null</c> to never skip.</param>
/// <param name="Verify">
/// Ask the registry what it holds of every transcript the ledger records, and back up again what it lost. Costs a few
/// requests even when nothing changed, so only <c>sessions push --verify</c> sets it, never a hook.
/// </param>
internal sealed record SessionSyncRequest(
    string? Registry, string? HarnessId, string? HarnessHome, string? SessionPath, TimeSpan? Budget, TimeSpan? Throttle, bool Verify = false);

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
/// <param name="Verified">What a verify found in the registry before uploading; <c>null</c> without <c>--verify</c>, or when the ledger recorded nothing to check.</param>
internal sealed record TranscriptSyncOutcome(
    LocalTranscript Transcript, TranscriptSyncStatus Status, long BytesUploaded, long BytesPending, bool Rewritten, string? Message,
    VerifiedBackup? Verified = null);

/// <summary>The result of a sync run.</summary>
/// <param name="Stopped">Whether the run was cancelled (Ctrl+C) before it finished; what it had not sent goes next time.</param>
/// <param name="Retried">
/// What a hook's push did for other sessions whose background backup had failed (see <see cref="SessionSyncService.RetryLimit"/>);
/// <c>null</c> when it retried none. Not part of <see cref="Transcripts"/> or <see cref="Complete"/>, which describe the session asked for.
/// </param>
internal sealed record SessionSyncResult(
    SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<TranscriptSyncOutcome> Transcripts, bool Stopped = false,
    IReadOnlyList<TranscriptSyncOutcome>? Retried = null)
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
/// <param name="Verified">What the registry holds compared with the ledger, from <c>sessions status --verify</c>; <c>null</c> otherwise.</param>
/// <param name="BackupFailedAt">When a hook's background backup of its session last failed, so a later hook retries it; <c>null</c> when not marked.</param>
internal sealed record TranscriptStatus(
    LocalTranscript Transcript, long SizeOnDisk, long BytesBackedUp, long BytesPending, bool Rewritten,
    DateTimeOffset? LastSyncedAt, DateTimeOffset LastModifiedAt, VerifiedBackup? Verified = null, DateTimeOffset? BackupFailedAt = null);

/// <summary>Every transcript's backup state on this machine.</summary>
internal sealed record SessionStatusResult(SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<TranscriptStatus> Transcripts)
{
    /// <summary>True when nothing is pending and no verified transcript differs from what the registry holds.</summary>
    public bool FullySynced => Failure == SessionSyncFailure.None &&
        Transcripts.All(t => t.BytesPending == 0 && t.Verified?.Check != BackupCheck.Diverged);
}

/// <summary>How a transcript the ledger records as backed up compares with what the registry holds of it.</summary>
internal enum BackupCheck
{
    /// <summary>The registry holds the revision and length the ledger records.</summary>
    Confirmed,

    /// <summary>The registry holds none of it, typically after its rows were lost or its database reset.</summary>
    Missing,

    /// <summary>The registry holds the same revision but fewer bytes than the ledger records, as after restoring an older backup.</summary>
    Short,

    /// <summary>The registry holds more than the ledger records, or a different revision. Left alone; see <see cref="SessionSyncService"/>.</summary>
    Diverged,
}

/// <summary>What a verify found in the registry for one transcript.</summary>
/// <param name="Check">How it compares with the ledger.</param>
/// <param name="Revision">The registry's revision; <c>null</c> when <see cref="BackupCheck.Missing"/>.</param>
/// <param name="Length">The bytes the registry holds of that revision; <c>null</c> when <see cref="BackupCheck.Missing"/>.</param>
internal sealed record VerifiedBackup(BackupCheck Check, int? Revision, long? Length);

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
/// <para>
/// A registry whose database was rebuilt or restored no longer knows this machine, though <c>machine.json</c> says
/// it registered. When registering a transcript or appending a chunk is refused for that reason, the machine is
/// registered again under the same id and the request retried once. The registration is an idempotent
/// <c>PUT</c>, and the retry happens even when it fails, because a concurrent push (a burst of Stop hooks) may have
/// registered the machine first; if the retry is refused too, the transcript fails as any other refusal does.
/// </para>
/// <para>
/// A registry that lost the transcript itself answers an append with the <c>unknown-transcript</c> problem type. Only
/// that answer makes the transcript register again and upload from the start, once per run; a server error or an
/// unreachable registry leaves the ledger's position alone, so a transient failure never re-sends a large file. The
/// registration recovers a forgotten machine first, so a registry reset that lost both comes back in one push. A
/// subagent's registration carries its parent key as always, so its link to the session is restored with it.
/// </para>
/// <para>
/// A hook's push (<see cref="SessionSyncRequest.SessionPath"/>) that fails marks its session in the ledger
/// (<see cref="ISessionLedger.MarkFailed"/>), because a session that has ended fires no more hooks to send it. A later
/// hook's push that reached the registry then retries up to <see cref="RetryLimit"/> marked sessions, oldest failure
/// first. Only marked sessions are retried, never the transcripts that were simply never backed up, so a hook still
/// never takes on the backlog (#62). Any run that finds every transcript of a marked session backed up clears its
/// mark, a manual <c>sessions push</c> included.
/// </para>
/// </remarks>
internal sealed partial class SessionSyncService : ISessionSyncService
{
    /// <summary>Upload size per request, before compression.</summary>
    internal const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>The longest line accepted; a longer line could never fit in a chunk.</summary>
    internal const int MaxLineBytes = 16 * 1024 * 1024;

    /// <summary>The most failed sessions one hook's push retries after its own.</summary>
    /// <remarks>
    /// Failures come in bursts: an outage or a flight ends a few sessions, rarely dozens. Stop fires after every reply
    /// and pushes at most twice a minute per session, so three a push clears a burst within minutes of ordinary use,
    /// while one push never does more than four sessions' work, its own included. A session that keeps failing is
    /// marked again with the new time, which moves it behind the others, so it cannot hold a slot for ever.
    /// </remarks>
    internal const int RetryLimit = 3;

    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _meta;
    private readonly ITranscriptApi _api;
    private readonly ISessionLedger _ledger;
    private readonly IMachineIdentity _machine;
    private readonly IProjectFactsResolver _projects;
    private readonly IProjectApi _projectApi;
    private readonly TimeProvider _time;

    public SessionSyncService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IRegistryApi meta, ITranscriptApi api,
        ISessionLedger ledger, IMachineIdentity machine, IProjectFactsResolver projects, IProjectApi projectApi, TimeProvider time)
    {
        _projectApi = projectApi;
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

        var startedAt = _time.GetUtcNow();
        var located = Locate(harnesses, request.HarnessHome, request.SessionPath)
            .Select(transcript => (Transcript: transcript, Plan: Plan(registry, transcript)))
            .ToList();
        Session? session = null;
        var outcomes = new List<TranscriptSyncOutcome>(located.Count);

        // Without --verify every transcript is unchecked and planned from the ledger alone, as before.
        var planned = located.Select(p => (p.Transcript, p.Plan, Verified: (VerifiedBackup?)null)).ToList();
        if (request.Verify)
        {
            Verification verifying;
            try
            {
                verifying = await ConnectAndCheckAsync(registry, located, registerMachine: true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopped before anything was uploaded; nothing changed, so there is nothing to report per transcript.
                return new SessionSyncResult(SessionSyncFailure.None, null, registry, [], Stopped: true);
            }

            if (verifying.Failure != SessionSyncFailure.None)
                return SessionSyncResult.Fail(verifying.Failure, verifying.Detail, registry);
            session = verifying.Session;
            planned = located.Select(p =>
            {
                var verified = verifying.Checks!.GetValueOrDefault((p.Transcript.Harness, p.Transcript.Key));
                return (p.Transcript, Plan: AfterCheck(p.Plan, verified), Verified: verified);
            }).ToList();
            outcomes.AddRange(planned.Where(p => p.Verified?.Check == BackupCheck.Diverged).Select(p => LeftAlone(p.Transcript, p.Plan, p.Verified!)));
            planned.RemoveAll(p => p.Verified?.Check == BackupCheck.Diverged);
        }

        outcomes.AddRange(planned.Where(p => p.Plan.UpToDate).Select(p => UpToDate(p.Transcript) with { Verified = p.Verified }));

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
        var stopped = false;

        foreach (var (transcript, plan, verified) in work)
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
                    {
                        MarkIfHook(registry, request, located.Select(p => p.Transcript));
                        return SessionSyncResult.Fail(connected.Failure, connected.Detail, registry);
                    }
                    session = connected.Session!;
                }

                var pending = plan.Pending;
                outcome = await UploadUnderLockAsync(
                    registry, session, transcript, request, verified, clock,
                    sent => progress?.Report(new SessionSyncProgress(done, work.Count, bytesDone + Math.Min(sent, pending), bytesTotal, transcript.Key)),
                    cancellationToken);
            }

            outcome = outcome with { Verified = verified };

            outcomes.Add(outcome);
            stopped = cancellationToken.IsCancellationRequested;
            if (stopped)
                continue;
            done++;
            bytesDone += plan.Pending;
            progress?.Report(new SessionSyncProgress(done, work.Count, bytesDone, bytesTotal, null));
        }

        if (outcomes.Any(o => o.Status == TranscriptSyncStatus.Failed))
            MarkIfHook(registry, request, located.Select(p => p.Transcript));
        ClearBackedUp(registry, outcomes, startedAt);

        // Only a hook's push retries, and only once it has reached the registry: an offline hook adds no network call.
        IReadOnlyList<TranscriptSyncOutcome>? retried = null;
        if (request.SessionPath is not null && session is not null && !stopped)
            retried = await RetryFailedAsync(registry, session, located.Select(p => p.Transcript), startedAt, cancellationToken);

        return new SessionSyncResult(SessionSyncFailure.None, null, registry, outcomes, stopped, retried);
    }

    // ── retrying failed background backups ────────────────────────────────────────

    // Marks the session among a hook push's transcripts as failed; a manual push marks nothing, since its user sees the failure.
    private void MarkIfHook(Uri registry, SessionSyncRequest request, IEnumerable<LocalTranscript> transcripts)
    {
        if (request.SessionPath is null || transcripts.FirstOrDefault(t => t.Kind == TranscriptKind.Session) is not { } session)
            return;
        _ledger.MarkFailed(registry, new FailedBackup(session.Harness, session.Key, session.Path, _time.GetUtcNow()));
    }

    // Clears the mark of every marked session whose transcripts in this run all ended backed up.
    private void ClearBackedUp(Uri registry, IReadOnlyList<TranscriptSyncOutcome> outcomes, DateTimeOffset startedAt)
    {
        foreach (var mark in _ledger.FailedBackups(registry))
        {
            var its = outcomes.Where(o => o.Transcript.Harness == mark.Harness && SessionKey(o.Transcript) == mark.Key).ToList();
            if (its.Count > 0 && its.All(o => o.Status is TranscriptSyncStatus.UpToDate or TranscriptSyncStatus.Uploaded))
                _ledger.ClearFailed(registry, mark.Harness, mark.Key, startedAt);
        }
    }

    private static string SessionKey(LocalTranscript transcript) => transcript.ParentKey ?? transcript.Key;

    // When each transcript's session was marked failed, for status; read once, since status walks every transcript.
    private Func<LocalTranscript, DateTimeOffset?> FailedSince(Uri registry)
    {
        var marks = _ledger.FailedBackups(registry).ToDictionary(m => (m.Harness, m.Key), m => m.FailedAt);
        return transcript => marks.TryGetValue((transcript.Harness, SessionKey(transcript)), out var at) ? at : null;
    }

    // Sends up to RetryLimit marked sessions other than the hook's own, oldest failure first, each under its
    // transcripts' locks, so a retry racing a fresh push of the same session never uploads it twice. A session sent in
    // full loses its mark; one that fails again is marked with the new time; one another process holds keeps its mark.
    private async Task<IReadOnlyList<TranscriptSyncOutcome>?> RetryFailedAsync(
        Uri registry, Session session, IEnumerable<LocalTranscript> own, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var ownSessions = own.Select(t => (t.Harness, SessionKey(t))).ToHashSet();
        var marks = _ledger.FailedBackups(registry)
            .Where(m => !ownSessions.Contains((m.Harness, m.Key)))
            .Take(RetryLimit)
            .ToList();
        if (marks.Count == 0)
            return null;

        // Neither budget nor throttle: the cap bounds the work, and a marked session is overdue whenever it last tried.
        var unbounded = new SessionSyncRequest(null, null, null, null, null, null);
        var clock = Stopwatch.StartNew();
        var outcomes = new List<TranscriptSyncOutcome>();
        foreach (var mark in marks)
        {
            var transcripts = _catalog.ById(mark.Harness) is ISessionCapability sessions ? sessions.LocateSession(mark.Path) : [];
            if (transcripts.Count == 0)
            {
                // The file is gone (deleted, or its harness uninstalled): there is nothing left to back up.
                _ledger.ClearFailed(registry, mark.Harness, mark.Key, startedAt);
                continue;
            }

            var its = new List<TranscriptSyncOutcome>(transcripts.Count);
            foreach (var transcript in transcripts)
            {
                if (cancellationToken.IsCancellationRequested)
                    return outcomes;
                its.Add(Plan(registry, transcript).UpToDate
                    ? UpToDate(transcript)
                    : await UploadUnderLockAsync(registry, session, transcript, unbounded, null, clock, _ => { }, cancellationToken));
            }

            if (its.Any(o => o.Status == TranscriptSyncStatus.Failed))
                _ledger.MarkFailed(registry, mark with { FailedAt = _time.GetUtcNow() });
            else if (its.All(o => o.Status is TranscriptSyncStatus.UpToDate or TranscriptSyncStatus.Uploaded))
                _ledger.ClearFailed(registry, mark.Harness, mark.Key, startedAt);
            outcomes.AddRange(its);
        }

        return outcomes;
    }

    // Takes the transcript's lock and uploads from a fresh plan, or says why it did not.
    // verified: what a verify found before the run started, applied again to the fresh plan.
    private async Task<TranscriptSyncOutcome> UploadUnderLockAsync(
        Uri registry, Session session, LocalTranscript transcript, SessionSyncRequest request, VerifiedBackup? verified, Stopwatch clock,
        Action<long> sent, CancellationToken cancellationToken)
    {
        using var held = _ledger.TryLock(registry, transcript.Harness, transcript.Key);
        if (held is null)
            return Skipped(transcript, Plan(registry, transcript), "Another protostar process is uploading it.");

        // Plan again under the lock: another process (a push from an earlier hook) may have uploaded since,
        // and uploading from the stale plan would write its old position back over the ledger.
        var plan = AfterCheck(Plan(registry, transcript), verified);
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

        var failed = FailedSince(registry);
        var statuses = new List<TranscriptStatus>();
        foreach (var transcript in Locate(harnesses, harnessHome, null))
        {
            var plan = Plan(registry, transcript);
            var backedUp = plan.Rewritten ? 0 : plan.Entry?.Length ?? 0;
            statuses.Add(new TranscriptStatus(
                transcript, plan.Size, backedUp, plan.Pending, plan.Rewritten, plan.Entry?.LastSyncedAt, plan.Modified,
                BackupFailedAt: failed(transcript)));
        }

        return new SessionStatusResult(SessionSyncFailure.None, null, registry, statuses);
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
                registered = await RegisterTranscriptAsync(registry, session, transcript, project, cancellationToken);
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
        var reRegistered = false;
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
                if (result.Status == AppendStatus.UnknownMachine)
                {
                    await RegisterMachineAgainAsync(registry, session, cancellationToken);
                    result = await _api.AppendAsync(registry, session.AccessToken, transcript.Harness, transcript.Key, revision, offset, session.MachineId, chunk, cancellationToken);
                }
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

                case AppendStatus.UnknownTranscript when !reRegistered:
                    // The registry lost the transcript (its rows were deleted, or the database was reset), so what the
                    // ledger says it holds is gone. Register it again and upload from the position that returns: a
                    // fresh registration answers revision 1 at offset 0. Once only; a second refusal fails below.
                    reRegistered = true;
                    var project = ProjectFactsFor(transcript);
                    ApiResult<TranscriptPosition> again;
                    try
                    {
                        again = await RegisterTranscriptAsync(registry, session, transcript, project, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return Stopped();
                    }

                    if (!again.Ok)
                        return Failed(transcript, plan, sent, again.Error!);
                    filing = project is null ? ProjectFiling.Pending : ProjectFiling.Sent;
                    // As after a first registration: the ledger takes the registry's confirmed position, and the
                    // upload resumes from it unless it is past the end of the file.
                    var restart = again.Value!;
                    _ledger.Save(registry, transcript.Harness, transcript.Key, new SessionLedgerEntry(
                        restart.Revision, restart.Length, SessionLedger.TailHash(transcript.Path, restart.Length), true, null, now, filing));
                    (revision, offset) = restart.Length <= SafeLength(transcript.Path)
                        ? (restart.Revision, restart.Length)
                        : (restart.Revision + 1, 0);
                    break;

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

    // registerMachine: false for a caller that only reads (status --verify), which then never changes the registry.
    private async Task<(SessionSyncFailure Failure, string? Detail, Session? Session)> ConnectAsync(
        Uri registry, CancellationToken cancellationToken, bool registerMachine = true)
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
        if (registerMachine && !_machine.IsRegisteredWith(registry))
        {
            var registered = await _api.RegisterMachineAsync(registry, stored.AccessToken, machine.Id, machine.Name, machine.OperatingSystem, cancellationToken);
            if (!registered.Ok)
                return (SessionSyncFailure.RegistryUnreachable, registered.Error, null);
            _machine.MarkRegistered(registry);
        }

        return (SessionSyncFailure.None, null, new Session(stored.AccessToken, machine.Id));
    }

    // Registers the transcript, registering the machine again first when the registry has forgotten it too, so the
    // two recoveries compose: after a full reset both are gone, and the machine must exist before the transcript can.
    private async Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(
        Uri registry, Session session, LocalTranscript transcript, ProjectFacts? project, CancellationToken cancellationToken)
    {
        var registered = await _api.RegisterTranscriptAsync(registry, session.AccessToken, transcript, session.MachineId, project, cancellationToken);
        if (!registered.MachineUnknown)
            return registered;

        await RegisterMachineAgainAsync(registry, session, cancellationToken);
        return await _api.RegisterTranscriptAsync(registry, session.AccessToken, transcript, session.MachineId, project, cancellationToken);
    }

    // The registry forgot this machine. Registers it again under the same id: a new id would split its history in
    // two. A failure is not reported here; the caller's retry either succeeds (another process registered it) or
    // fails with the registry's own answer.
    private async Task RegisterMachineAgainAsync(Uri registry, Session session, CancellationToken cancellationToken)
    {
        var machine = _machine.Get();
        var registered = await _api.RegisterMachineAsync(registry, session.AccessToken, session.MachineId, machine.Name, machine.OperatingSystem, cancellationToken);
        if (registered.Ok)
            _machine.MarkRegistered(registry);
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
