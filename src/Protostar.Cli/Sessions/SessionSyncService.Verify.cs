using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>The <c>--verify</c> half of <see cref="SessionSyncService"/>: checking the ledger against the registry.</summary>
/// <remarks>
/// <para>
/// The ledger is what lets an unchanged transcript cost no network call, so on its own it cannot notice a registry
/// that lost what it once confirmed. A verify asks the registry, for every transcript the ledger records as
/// registered, which revision and length it holds, up to <see cref="ITranscriptApi.MaxLookup"/> at a time, and
/// compares each answer with the ledger (<see cref="Classify"/>).
/// </para>
/// <para>
/// <see cref="BackupCheck.Missing"/> and <see cref="BackupCheck.Short"/> are backed up again through the same path a
/// first backup or a lost transcript takes: register, which is idempotent and answers the registry's real position,
/// then upload from that position. A transcript the registry lost starts again from 0; one it holds less of resumes
/// from the registry's length, not from 0 and not from the ledger's. Because the position always comes from the
/// registration rather than from the lookup, a stale answer cannot make an upload start in the wrong place.
/// </para>
/// <para>
/// <see cref="BackupCheck.Diverged"/> is reported and left alone. The registry holding more than this machine
/// recorded, or another revision, means something other than this machine's last push wrote it: another process
/// whose ledger write was lost, another machine, or a ledger restored from an older copy. The registry is append-only
/// per revision, so the only way to "fix" it from here is a new revision from this file, which would replace the
/// registry's current content with a copy this machine cannot show is newer. That is a decision for the user.
/// </para>
/// <para>
/// A file rewritten since its last backup is already re-uploaded in full as a new revision, so a check changes
/// nothing for it: a missing transcript is then caught by the append's <c>unknown-transcript</c> answer as usual.
/// </para>
/// </remarks>
internal sealed partial class SessionSyncService
{
    /// <inheritdoc />
    public async Task<SessionStatusResult> VerifyAsync(string? registryOption, string? harnessId, string? harnessHome, CancellationToken cancellationToken)
    {
        if (!TryResolve(registryOption, harnessId, out var registry, out var harnesses, out var failure, out var detail))
            return new SessionStatusResult(failure, detail, registry, []);

        var located = Locate(harnesses, harnessHome, null)
            .Select(transcript => (Transcript: transcript, Plan: Plan(registry, transcript)))
            .ToList();
        var verifying = await ConnectAndCheckAsync(registry, located, registerMachine: false, cancellationToken);
        if (verifying.Failure != SessionSyncFailure.None)
            return new SessionStatusResult(verifying.Failure, verifying.Detail, registry, []);

        var failed = FailedSince(registry);
        var statuses = located.Select(p =>
        {
            var verified = verifying.Checks!.GetValueOrDefault((p.Transcript.Harness, p.Transcript.Key));
            var plan = AfterCheck(p.Plan, verified);
            var backedUp = plan.Rewritten ? 0 : plan.Entry?.Length ?? 0;
            return new TranscriptStatus(
                p.Transcript, plan.Size, backedUp, plan.Pending, plan.Rewritten, plan.Entry?.LastSyncedAt, plan.Modified, verified,
                failed(p.Transcript));
        }).ToList();

        return new SessionStatusResult(SessionSyncFailure.None, null, registry, statuses);
    }

    /// <summary>How the registry's answer for a transcript compares with what the ledger records of it.</summary>
    internal static VerifiedBackup Classify(SessionLedgerEntry entry, TranscriptLookup? held) => held switch
    {
        { Known: true, Revision: { } revision, Length: { } length } when revision == entry.Revision && length == entry.Length =>
            new VerifiedBackup(BackupCheck.Confirmed, revision, length),
        { Known: true, Revision: { } revision, Length: { } length } when revision == entry.Revision && length < entry.Length =>
            new VerifiedBackup(BackupCheck.Short, revision, length),
        { Known: true } => new VerifiedBackup(BackupCheck.Diverged, held.Revision, held.Length),
        _ => new VerifiedBackup(BackupCheck.Missing, null, null),
    };

    private sealed record Verification(
        SessionSyncFailure Failure, string? Detail, Session? Session, Dictionary<(string Harness, string Key), VerifiedBackup>? Checks);

    // Connects, then asks the registry about every transcript the ledger records as registered, in batches.
    // A transcript the ledger records nothing for is pending anyway, so it is not asked about.
    private async Task<Verification> ConnectAndCheckAsync(
        Uri registry, IReadOnlyList<(LocalTranscript Transcript, UploadPlan Plan)> located, bool registerMachine,
        CancellationToken cancellationToken)
    {
        var connected = await ConnectAsync(registry, cancellationToken, registerMachine, RegistryCapability.TranscriptLookup);
        if (connected.Failure != SessionSyncFailure.None)
            return new Verification(connected.Failure, connected.Detail, null, null);

        var recorded = located.Where(p => p.Plan.Entry is { Registered: true }).ToList();
        var checks = new Dictionary<(string Harness, string Key), VerifiedBackup>();
        foreach (var batch in recorded.Chunk(ITranscriptApi.MaxLookup))
        {
            var answer = await _api.LookupAsync(registry, connected.Session!.AccessToken, [.. batch.Select(p => p.Transcript)], cancellationToken);
            if (!answer.Ok)
                return new Verification(SessionSyncFailure.RegistryUnreachable, answer.Error, null, null);

            var held = answer.Value!.GroupBy(a => (a.Harness, a.Key)).ToDictionary(g => g.Key, g => g.First());
            foreach (var (transcript, plan) in batch)
                checks[(transcript.Harness, transcript.Key)] = Classify(plan.Entry!, held.GetValueOrDefault((transcript.Harness, transcript.Key)));
        }

        return new Verification(SessionSyncFailure.None, null, connected.Session, checks);
    }

    // The plan once a check is taken into account. Missing and short transcripts are registered again, which answers
    // the registry's position to upload from; the ledger length becomes what the registry holds, so the pending bytes
    // and the progress bar count from there. A rewritten file is re-uploaded in full regardless, so it is unchanged.
    private static UploadPlan AfterCheck(UploadPlan plan, VerifiedBackup? verified) => verified switch
    {
        { Check: BackupCheck.Missing or BackupCheck.Short } when !plan.Rewritten && plan.Entry is { } entry =>
            plan with { UpToDate = false, Entry = entry with { Registered = false, Length = verified.Length ?? 0 } },
        _ => plan,
    };

    private static TranscriptSyncOutcome LeftAlone(LocalTranscript transcript, UploadPlan plan, VerifiedBackup verified) =>
        new(transcript, TranscriptSyncStatus.Skipped, 0, plan.Pending, plan.Rewritten,
            $"The registry holds revision {verified.Revision} at {verified.Length} bytes, but this machine recorded revision " +
            $"{plan.Entry!.Revision} at {plan.Entry.Length}. Left alone so nothing newer is overwritten.",
            verified);
}
