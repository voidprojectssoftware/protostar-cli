using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Setup;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Contract tests for <see cref="ISetupService"/>: every service it composes is faked, so these check only how
/// setup orders the steps, classifies each one, and decides about the session backup.
/// </summary>
public sealed class SetupServiceTests
{
    private const string Registry = "https://registry.test";

    private readonly FakeSignIn _signIn = new();
    private readonly FakeHooks _hooks = new();
    private readonly FakeSkills _skills = new();
    private readonly FakeSessions _sessions = new();
    private readonly FakeDefaultRegistry _defaultRegistry = new();
    private readonly List<string> _calls = [];
    private readonly List<SetupStepResult> _reported = [];
    private bool _answer;
    private int _asked;

    public SetupServiceTests()
    {
        _signIn.Calls = _hooks.Calls = _skills.Calls = _sessions.Calls = _calls;
    }

    [Fact]
    public async Task A_first_run_signs_in_installs_hooks_pulls_skills_and_backs_up_with_yes()
    {
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));

        var result = await Run(yes: true);

        Assert.Equal(
            [SetupStep.SignIn, SetupStep.Hooks, SetupStep.Skills, SetupStep.Configuration, SetupStep.Sessions],
            result.Steps.Select(s => s.Step));
        Assert.Equal(
            [SetupOutcome.Done, SetupOutcome.Done, SetupOutcome.Done, SetupOutcome.Skipped, SetupOutcome.Done],
            result.Steps.Select(s => s.Outcome));
        Assert.Equal(SetupSkip.NotAvailableYet, result.Steps[3].Skip);
        Assert.Equal(result.Steps, _reported);
        Assert.True(result.Succeeded);
        Assert.False(result.NothingChanged);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task Hooks_are_installed_before_skills_are_pulled()
    {
        await Run(yes: true);

        Assert.True(_calls.IndexOf("hooks") < _calls.IndexOf("pull"), string.Join(", ", _calls));
    }

    [Fact]
    public async Task A_second_run_reports_every_step_already_done_and_changes_nothing()
    {
        _signIn.Verification = SessionVerification.Verified;
        _hooks.Change = HookChange.Unchanged;
        _skills.Status = SkillPullStatus.UpToDate;
        _sessions.Pending(("s1", TranscriptKind.Session, null, 0));

        var result = await Run(yes: true);

        Assert.All(result.Steps.Where(s => s.Step != SetupStep.Configuration), s => Assert.Equal(SetupOutcome.AlreadyDone, s.Outcome));
        Assert.True(result.NothingChanged);
        Assert.DoesNotContain("sign-in", _calls);
        Assert.DoesNotContain("sync", _calls);
    }

    [Fact]
    public async Task An_existing_session_still_makes_the_registry_the_default()
    {
        _signIn.Verification = SessionVerification.Verified;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.AlreadyDone, result.Steps[0].Outcome);
        Assert.Equal([new Uri(Registry)], _defaultRegistry.Saved);
    }

    [Fact]
    public async Task An_existing_session_fails_sign_in_when_the_default_cannot_be_saved()
    {
        _signIn.Verification = SessionVerification.Verified;
        _defaultRegistry.Fails = true;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[0].Outcome);
        Assert.Equal(SignInFailure.SaveFailed, result.Steps[0].SignIn!.Failure);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task An_unreachable_registry_fails_sign_in_without_a_browser_and_skips_what_needs_it()
    {
        _signIn.Verification = SessionVerification.Unreachable;
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[0].Outcome);
        Assert.DoesNotContain("sign-in", _calls);
        Assert.Equal(SetupOutcome.Done, result.Steps[1].Outcome);
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NotSignedIn), (result.Steps[2].Outcome, result.Steps[2].Skip));
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NotSignedIn), (result.Steps[4].Outcome, result.Steps[4].Skip));
        Assert.DoesNotContain("pull", _calls);
        Assert.DoesNotContain("sync", _calls);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_rejected_session_signs_in_again_with_the_requested_provider()
    {
        _signIn.Verification = SessionVerification.Rejected;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Done, result.Steps[0].Outcome);
        Assert.Equal("dev", _signIn.Options!.Provider);
        Assert.True(_signIn.Options.NoBrowser);
    }

    [Fact]
    public async Task A_failed_sign_in_fails_the_run()
    {
        _signIn.Failure = SignInFailure.TimedOut;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[0].Outcome);
        Assert.Equal(SetupSkip.NotSignedIn, result.Steps[2].Skip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Without_yes_or_a_terminal_the_backup_is_skipped_and_nobody_is_asked()
    {
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));

        var result = await Run(yes: false, canAsk: false);

        var sessions = result.Steps[4];
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NoTerminal), (sessions.Outcome, sessions.Skip));
        Assert.Equal(0, _asked);
        Assert.DoesNotContain("sync", _calls);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_a_terminal_the_backup_waits_for_an_answer(bool answer)
    {
        var (outcome, skip) = answer ? (SetupOutcome.Done, SetupSkip.None) : (SetupOutcome.Skipped, SetupSkip.Declined);
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));
        _answer = answer;

        var result = await Run(yes: false, canAsk: true);

        Assert.Equal(1, _asked);
        Assert.Equal((outcome, skip), (result.Steps[4].Outcome, result.Steps[4].Skip));
        Assert.Equal(answer, _calls.Contains("sync"));
    }

    [Fact]
    public async Task The_backlog_counts_a_subagent_under_its_session()
    {
        _sessions.Pending(
            ("s1", TranscriptKind.Session, null, 100),
            ("s1.agent-a", TranscriptKind.Subagent, "s1", 20),
            ("s2.agent-b", TranscriptKind.Subagent, "s2", 5),
            ("s3", TranscriptKind.Session, null, 0));

        var result = await Run(yes: false, canAsk: false);

        Assert.Equal(new SessionBacklog(2, 3, 125), result.Steps[4].Backlog);
    }

    [Fact]
    public async Task An_unfinished_backup_fails_the_step()
    {
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));
        _sessions.SyncStatus = TranscriptSyncStatus.Failed;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[4].Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_skill_conflict_fails_the_skills_step_and_local_changes_do_not()
    {
        _skills.Status = SkillPullStatus.Conflict;
        Assert.Equal(SetupOutcome.Failed, (await Run(yes: true)).Steps[2].Outcome);

        _skills.Status = SkillPullStatus.LocalChanges;
        Assert.Equal(SetupOutcome.AlreadyDone, (await Run(yes: true)).Steps[2].Outcome);
    }

    [Fact]
    public async Task No_harness_skips_hooks_and_skills()
    {
        _hooks.NoHarness = true;
        _skills.Failure = SkillPullRunFailure.NoHarness;

        var result = await Run(yes: true);

        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NoHarness), (result.Steps[1].Outcome, result.Steps[1].Skip));
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NoHarness), (result.Steps[2].Outcome, result.Steps[2].Skip));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_invalid_registry_stops_before_any_step()
    {
        var service = new SetupService(_signIn, _hooks, _skills, _sessions, _defaultRegistry);
        var request = new SetupRequest("not a url", null, new SignInOptions(null, false, TimeSpan.FromMinutes(5)), true, false, null);

        var result = await service.RunAsync(request, Interaction(), CancellationToken.None);

        Assert.NotNull(result.InvalidRegistry);
        Assert.Empty(result.Steps);
        Assert.Empty(_calls);
        Assert.False(result.Succeeded);
    }

    private async Task<SetupResult> Run(bool yes, bool canAsk = false)
    {
        _calls.Clear();
        _reported.Clear();
        var service = new SetupService(_signIn, _hooks, _skills, _sessions, _defaultRegistry);
        var request = new SetupRequest(Registry, "/harness", new SignInOptions("dev", true, TimeSpan.FromMinutes(5)), yes, canAsk, null);
        return await service.RunAsync(request, Interaction(), CancellationToken.None);
    }

    private SetupInteraction Interaction() => new(
        _reported.Add,
        _ =>
        {
            _asked++;
            return _answer;
        },
        (_, sync) => sync(null));

    // ── fakes ─────────────────────────────────────────────────────────────────────

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

    private static readonly IHarness Harness = new FakeHarness();

    private sealed class FakeSignIn : ISignInService
    {
        public List<string> Calls { get; set; } = [];
        public SessionVerification Verification { get; set; } = SessionVerification.NoSession;
        public SignInFailure Failure { get; set; } = SignInFailure.None;
        public SignInOptions? Options { get; private set; }

        public Task<SessionCheck> CheckAsync(Uri registry, CancellationToken cancellationToken)
        {
            Calls.Add("check");
            return Task.FromResult(new SessionCheck(registry, null, Verification, "dev"));
        }

        public Task<SignInResult> SignInAsync(Uri registry, SignInOptions options, CancellationToken cancellationToken)
        {
            Calls.Add("sign-in");
            Options = options;
            return Task.FromResult(new SignInResult(Failure, null, registry, "dev"));
        }
    }

    private sealed class FakeDefaultRegistry : IDefaultRegistryStore
    {
        public List<Uri> Saved { get; } = [];
        public bool Fails { get; set; }

        public bool Save(Uri registry)
        {
            Saved.Add(registry);
            return !Fails;
        }
    }

    private sealed class FakeHooks : IHookInstallService
    {
        public List<string> Calls { get; set; } = [];
        public HookChange Change { get; set; } = HookChange.Added;
        public bool NoHarness { get; set; }

        public HookRunResult Install(HookInstallOptions opts, HarnessSelector? select = null)
        {
            Calls.Add("hooks");
            return NoHarness
                ? HookRunResult.Nothing
                : new HookRunResult(HookRunFailure.None, null, [new HookApplyResult(Harness, new HookChangeSet(Change, "/harness/settings.json", "hooks"), null)]);
        }

        public HookRunResult Uninstall(HookInstallOptions opts, HarnessSelector? select = null) => throw new NotSupportedException();
    }

    private sealed class FakeSkills : ISkillPullService
    {
        public List<string> Calls { get; set; } = [];
        public SkillPullStatus Status { get; set; } = SkillPullStatus.Installed;
        public SkillPullRunFailure Failure { get; set; } = SkillPullRunFailure.None;

        public Task<SkillRemoteResult> CompareAsync(SkillRemoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SkillPullRunResult> PullAsync(SkillPullRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("pull");
            Assert.Empty(request.Names);
            Assert.False(request.Force);
            var registry = new Uri(Registry);
            return Task.FromResult(Failure != SkillPullRunFailure.None
                ? SkillPullRunResult.Fail(Failure, null, registry)
                : new SkillPullRunResult(SkillPullRunFailure.None, null, registry, [new SkillPullEntry(Harness, "greet", Status, 1, "/harness/skills/greet", null)]));
        }
    }

    private sealed class FakeSessions : ISessionSyncService
    {
        private List<TranscriptStatus> _transcripts = [];

        public List<string> Calls { get; set; } = [];
        public TranscriptSyncStatus SyncStatus { get; set; } = TranscriptSyncStatus.Uploaded;

        public void Pending(params (string Key, TranscriptKind Kind, string? Parent, long Bytes)[] transcripts) =>
            _transcripts = [.. transcripts.Select(t => new TranscriptStatus(
                new LocalTranscript("fake", t.Key, t.Kind, t.Parent, $"/harness/{t.Key}.jsonl", null),
                t.Bytes, 0, t.Bytes, false, null, DateTimeOffset.UnixEpoch))];

        public SessionStatusResult Status(string? registry, string? harnessId, string? harnessHome)
        {
            Calls.Add("status");
            return new SessionStatusResult(SessionSyncFailure.None, null, new Uri(Registry), _transcripts);
        }

        public Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("sync");
            var outcomes = _transcripts.Select(t => new TranscriptSyncOutcome(t.Transcript, SyncStatus, t.BytesPending, 0, false, null)).ToList();
            return Task.FromResult(new SessionSyncResult(SessionSyncFailure.None, null, new Uri(Registry), outcomes));
        }

        public Task<SessionStatusResult> VerifyAsync(string? registry, string? harnessId, string? harnessHome, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SessionListResult> ListAsync(string? registry, string? harnessHome, TranscriptListQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
