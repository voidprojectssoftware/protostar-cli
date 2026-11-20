using System.Text.Json;
using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Memory;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Protostar.Cli.Status;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Tests for the capture records the hooks write and for <c>protostar status</c>, which reads them.</summary>
public sealed class SyncStatusTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "protostar-status-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string RecordsPath => Path.Combine(_dir, "capture-records.jsonl");

    private static CaptureRecord Ok(CaptureKind kind, DateTimeOffset at) => new(at, kind, true, Registry, 1);

    private static CaptureRecord Failed(CaptureKind kind, DateTimeOffset at, string error = "the registry could not be reached") =>
        new(at, kind, false, Registry, 0, Reason: CaptureFailure.RegistryUnreachable, Error: error);

    // ── records file ─────────────────────────────────────────────────────────────

    [Fact]
    public void Records_round_trip_as_one_json_line_each_and_a_torn_line_is_skipped()
    {
        var at = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        CaptureRecords.Append(RecordsPath, Ok(CaptureKind.Skills, at));
        File.AppendAllText(RecordsPath, "{\"at\":\"2026-11-20T10:0");
        File.AppendAllText(RecordsPath, "\n");
        CaptureRecords.Append(RecordsPath, Failed(CaptureKind.Sessions, at.AddMinutes(1)));

        var lines = File.ReadAllLines(RecordsPath);
        var read = CaptureRecords.Read(RecordsPath);

        Assert.Equal(3, lines.Length);
        Assert.Contains("\"kind\":\"skills\"", lines[0]);
        Assert.DoesNotContain("\"error\"", lines[0]);
        Assert.Equal([CaptureKind.Skills, CaptureKind.Sessions], read.Select(r => r.Kind));
        Assert.Equal(CaptureFailure.RegistryUnreachable, read[1].Reason);
    }

    [Fact]
    public void A_full_file_is_rotated_once_and_both_generations_are_read()
    {
        Directory.CreateDirectory(_dir);
        var first = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        CaptureRecords.Append(RecordsPath, Ok(CaptureKind.Memory, first));
        File.AppendAllText(RecordsPath, new string(' ', (int)CaptureRecords.MaxBytes) + "\n");

        CaptureRecords.Append(RecordsPath, Ok(CaptureKind.Skills, first.AddDays(1)));
        CaptureRecords.Append(RecordsPath, Ok(CaptureKind.Skills, first.AddDays(2)));

        Assert.True(File.Exists(Path.Combine(_dir, "capture-records.1.jsonl")));
        Assert.True(new FileInfo(RecordsPath).Length < 1024);
        Assert.Equal([CaptureKind.Memory, CaptureKind.Skills, CaptureKind.Skills], CaptureRecords.Read(RecordsPath).Select(r => r.Kind));
    }

    [Fact]
    public void Reading_no_file_gives_no_records() => Assert.Empty(CaptureRecords.Read(RecordsPath));

    // ── what the hooks record ───────────────────────────────────────────────────

    [Fact]
    public void A_background_push_that_could_not_reach_the_registry_records_why()
    {
        var kept = new List<CaptureRecord>();

        CapturePresenter.RenderBackgroundPush(
            SessionSyncResult.Fail(SessionSyncFailure.RegistryUnreachable, null, new Uri(Registry + "/v1")), TextWriter.Null, _ => { }, kept.Add);

        var record = Assert.Single(kept);
        Assert.Equal((CaptureKind.Sessions, false, CaptureFailure.RegistryUnreachable, Registry), (record.Kind, record.Ok, record.Reason, record.Registry));
        Assert.StartsWith("session not backed up: the registry could not be reached", record.Error);
    }

    [Fact]
    public void A_push_that_only_skipped_a_throttled_session_records_nothing_and_an_upload_records_success()
    {
        var transcript = new LocalTranscript("claude-code", "s", TranscriptKind.Session, null, "/p/s.jsonl", null);
        SessionSyncResult Of(TranscriptSyncStatus status, long sent) =>
            new(SessionSyncFailure.None, null, new Uri(Registry), [new TranscriptSyncOutcome(transcript, status, sent, 0, false, null)]);

        Assert.Null(CapturePresenter.Record(Of(TranscriptSyncStatus.Skipped, 0), "x: y"));
        var uploaded = CapturePresenter.Record(Of(TranscriptSyncStatus.Uploaded, 120), "x: y");
        Assert.Equal((true, 1, 120L), (uploaded!.Ok, uploaded.Items, uploaded.Bytes));
    }

    [Fact]
    public void Skill_outcomes_are_recorded_and_a_stale_refusal_counts_as_a_failure()
    {
        CaptureResult Of(SkillPushStatus status) => new(CaptureAction.SkillBackedUp, "alpha", new SkillPushRunResult(
            SkillPushRunFailure.None, null, new Uri(Registry),
            [new SkillPushEntry(new DiscoveredSkill { Name = "alpha", Scope = SkillScope.Global, Path = "/s/alpha" },
                new SkillPushOutcome(status, 2, "Changed here and in the registry.", Remembered: true))]), null);
        var kept = new List<CaptureRecord>();

        CapturePresenter.Render("PostToolUse", Of(SkillPushStatus.Unchanged), TextWriter.Null, _ => { }, kept.Add);
        CapturePresenter.Render("PostToolUse", Of(SkillPushStatus.Stale), TextWriter.Null, _ => { }, kept.Add);
        CapturePresenter.Render("PostToolUse", new CaptureResult(CaptureAction.Ignored, null, null, "x"), TextWriter.Null, _ => { }, kept.Add);

        Assert.Equal([true, false], kept.Select(r => r.Ok));
        Assert.Equal(CaptureFailure.Refused, kept[1].Reason);
        Assert.Contains("Changed here and in the registry.", kept[1].Error);
    }

    [Fact]
    public void A_memory_push_with_no_memory_records_nothing()
    {
        var none = new MemoryPushResult(SessionSyncFailure.None, null, new Uri(Registry), "m", [], []);
        var signedOut = MemoryPushResult.Fail(SessionSyncFailure.NotLoggedIn, null, new Uri(Registry));

        Assert.Null(CapturePresenter.Record(none, "x: y"));
        Assert.Equal(CaptureFailure.NotSignedIn, CapturePresenter.Record(signedOut, "x: y")!.Reason);
    }

    [Fact]
    public void A_background_backup_refused_by_a_registry_too_old_for_it_records_why()
    {
        var tooOld = "This registry is too old for memory backups. Update the registry.";
        var memory = MemoryPushResult.Fail(SessionSyncFailure.Incompatible, tooOld, new Uri(Registry));
        var sessions = SessionSyncResult.Fail(SessionSyncFailure.Incompatible, "This registry is too old for session backups. Update the registry.", new Uri(Registry));
        var skills = new CaptureResult(CaptureAction.SkillBackedUp, "alpha",
            SkillPushRunResult.Fail(SkillPushRunFailure.Incompatible, SkillPushService.NoStaleProtection, new Uri(Registry)), null);
        var kept = new List<CaptureRecord>();

        CapturePresenter.RenderBackgroundMemoryPush(memory, TextWriter.Null, _ => { }, kept.Add);
        CapturePresenter.RenderBackgroundPush(sessions, TextWriter.Null, _ => { }, kept.Add);
        CapturePresenter.Render("PostToolUse", skills, TextWriter.Null, _ => { }, kept.Add);

        Assert.All(kept, r => Assert.Equal(CaptureFailure.Incompatible, r.Reason));
        Assert.Contains(tooOld, kept[0].Error);
        Assert.Contains("too old for session backups", kept[1].Error);
        Assert.Contains("too old for detecting overwritten skills", kept[2].Error);
    }

    // ── summarising ─────────────────────────────────────────────────────────────

    [Fact]
    public void Failures_are_counted_since_the_last_success_and_a_success_clears_them()
    {
        var t = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        var failing = SyncStatusService.Summarize(CaptureKind.Sessions,
            [Failed(CaptureKind.Sessions, t), Ok(CaptureKind.Sessions, t.AddMinutes(1)), Failed(CaptureKind.Sessions, t.AddMinutes(2)), Failed(CaptureKind.Sessions, t.AddMinutes(3), "last")]);
        var recovered = SyncStatusService.Summarize(CaptureKind.Sessions,
            [Failed(CaptureKind.Sessions, t), Ok(CaptureKind.Sessions, t.AddMinutes(1))]);

        Assert.Equal((true, 2, "last", t.AddMinutes(1)), (failing.Failing, failing.FailuresSinceSuccess, failing.LastError, failing.LastSuccessAt));
        Assert.False(recovered.Failing);
        Assert.Equal(t, recovered.LastFailureAt);
    }

    // ── the service ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_healthy_machine_has_no_problems()
    {
        var at = DateTimeOffset.UtcNow;
        var sut = Service(records: [Ok(CaptureKind.Skills, at), Ok(CaptureKind.Sessions, at)]);

        var status = await sut.CheckAsync(new SyncStatusRequest(Registry, null, false), CancellationToken.None);

        Assert.Equal(SignInState.SignedIn, status.SignIn);
        Assert.Empty(status.Problems);
        Assert.Equal(at, status.Backups.Single(b => b.Kind == CaptureKind.Sessions).LastSuccessAt);
    }

    [Fact]
    public async Task Signed_out_is_answered_from_the_credential_store_without_asking_the_registry()
    {
        var signIn = new FakeSignIn();
        var sut = Service(signedIn: false, signIn: signIn);

        var status = await sut.CheckAsync(new SyncStatusRequest(Registry, null, false), CancellationToken.None);

        Assert.Equal(SignInState.SignedOut, status.SignIn);
        Assert.Equal(0, signIn.Calls);
        Assert.Contains(SyncProblem.NotSignedIn, status.Problems);
    }

    [Fact]
    public async Task Offline_never_asks_the_registry()
    {
        var signIn = new FakeSignIn();

        var status = await Service(signIn: signIn).CheckAsync(new SyncStatusRequest(Registry, null, true), CancellationToken.None);

        Assert.Equal(SignInState.NotChecked, status.SignIn);
        Assert.Equal(0, signIn.Calls);
        Assert.Empty(status.Problems);
    }

    [Fact]
    public async Task A_registry_that_does_not_answer_is_given_up_on_after_the_probe_timeout()
    {
        var sut = Service(signIn: new FakeSignIn { Hangs = true });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var status = await sut.CheckAsync(new SyncStatusRequest(Registry, null, false), CancellationToken.None);

        Assert.Equal(SignInState.Unreachable, status.SignIn);
        Assert.True(clock.Elapsed < SyncStatusService.ProbeTimeout + TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        Assert.Contains(SyncProblem.RegistryUnreachable, status.Problems);
    }

    [Fact]
    public async Task Missing_hooks_failing_backups_and_marked_sessions_are_problems()
    {
        var ledger = new FakeLedger([new FailedBackup("claude-code", "s1", "/p/s1.jsonl", DateTimeOffset.UtcNow)]);
        var sut = Service(hooks: HookChange.Unchanged, ledger: ledger, records: [Failed(CaptureKind.Memory, DateTimeOffset.UtcNow)]);

        var status = await sut.CheckAsync(new SyncStatusRequest(Registry, null, false), CancellationToken.None);

        Assert.Equal([SyncProblem.HooksMissing, SyncProblem.MemoryFailing, SyncProblem.SessionsAwaitingRetry], status.Problems);
    }

    [Fact]
    public async Task Records_for_another_registry_are_ignored()
    {
        var other = Failed(CaptureKind.Skills, DateTimeOffset.UtcNow) with { Registry = "https://old.test" };

        var status = await Service(records: [other]).CheckAsync(new SyncStatusRequest(Registry, null, false), CancellationToken.None);

        Assert.Empty(status.Problems);
    }

    [Fact]
    public async Task The_json_has_the_contract_shape()
    {
        var sut = Service(records: [Failed(CaptureKind.Sessions, DateTimeOffset.UtcNow)], pendingBytes: 42);
        var status = await sut.CheckAsync(new SyncStatusRequest(Registry, null, false), CancellationToken.None);

        using var json = JsonDocument.Parse(SyncStatusPresenter.ToJson(status).ToJsonString());
        var root = json.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("signedIn", root.GetProperty("signIn").GetProperty("state").GetString());
        Assert.Equal("sessionsFailing", root.GetProperty("problems")[0].GetString());
        Assert.Equal(42, root.GetProperty("pending").GetProperty("bytes").GetInt64());
        var sessions = root.GetProperty("backups").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "sessions");
        Assert.Equal("registryUnreachable", sessions.GetProperty("lastReason").GetString());
        Assert.False(sessions.TryGetProperty("lastSuccessAt", out _));
    }

    private static SyncStatusService Service(
        bool signedIn = true, FakeSignIn? signIn = null, HookChange hooks = HookChange.Removed, FakeLedger? ledger = null,
        IReadOnlyList<CaptureRecord>? records = null, long pendingBytes = 0) =>
        new(new FakeTokens(signedIn), signIn ?? new FakeSignIn(), new FakeHooks(hooks), new FakeSessions(pendingBytes), ledger ?? new FakeLedger([]),
            () => records ?? []);

    private sealed class FakeTokens(bool signedIn) : ITokenStore
    {
        public bool Save(StoredToken token) => true;

        public StoredToken? Load(Uri registry) =>
            signedIn ? new StoredToken { Registry = registry.ToString(), AccessToken = "t", Login = "dev" } : null;

        public void Delete(Uri registry)
        {
        }
    }

    private sealed class FakeSignIn : ISignInService
    {
        public int Calls { get; private set; }

        public bool Hangs { get; init; }

        public async Task<SessionCheck> CheckAsync(Uri registry, CancellationToken cancellationToken)
        {
            Calls++;
            if (Hangs)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return new SessionCheck(registry, null, SessionVerification.Verified, "dev");
        }

        public Task<SignInResult> SignInAsync(Uri registry, SignInOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // A dry-run uninstall that would remove hooks means they are installed; Unchanged means they are not.
    private sealed class FakeHooks(HookChange change) : IHookInstallService
    {
        public HookRunResult Install(HookInstallOptions opts, HarnessSelector? select = null) => throw new NotSupportedException();

        public HookRunResult Uninstall(HookInstallOptions opts, HarnessSelector? select = null)
        {
            Assert.True(opts.DryRun);
            return new HookRunResult(HookRunFailure.None, null,
                [new HookApplyResult(new FakeHarness(), new HookChangeSet(change, "/harness/settings.json", "x"), null)]);
        }
    }

    private sealed class FakeHarness : IHarness
    {
        public string Id => "fake";
        public string DisplayName => "Fake";

        public bool TryLocate(string? rootOverride, out HarnessLocation location)
        {
            location = new HarnessLocation("/harness", "/harness/settings.json");
            return true;
        }
    }

    private sealed class FakeSessions(long pendingBytes) : ISessionSyncService
    {
        public Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public SessionStatusResult Status(string? registry, string? harnessId, string? harnessHome) =>
            new(SessionSyncFailure.None, null, new Uri(Registry),
            [
                new TranscriptStatus(
                    new LocalTranscript("claude-code", "s", TranscriptKind.Session, null, "/p/s.jsonl", null),
                    100, 100 - pendingBytes, pendingBytes, false, null, DateTimeOffset.UtcNow),
            ]);

        public Task<SessionStatusResult> VerifyAsync(string? registry, string? harnessId, string? harnessHome, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SessionListResult> ListAsync(string? registry, string? harnessHome, TranscriptListQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLedger(IReadOnlyList<FailedBackup> failed) : ISessionLedger
    {
        public SessionLedgerEntry? Get(Uri registry, string harness, string key) => null;

        public void Save(Uri registry, string harness, string key, SessionLedgerEntry entry)
        {
        }

        public IDisposable? TryLock(Uri registry, string harness, string key) => null;

        public void MarkFailed(Uri registry, FailedBackup backup)
        {
        }

        public IReadOnlyList<FailedBackup> FailedBackups(Uri registry) => failed;

        public void ClearFailed(Uri registry, string harness, string key, DateTimeOffset before)
        {
        }
    }
}
