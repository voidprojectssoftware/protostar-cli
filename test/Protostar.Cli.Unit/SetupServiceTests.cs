using Protostar.Cli.Agents;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Memory;
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
    private readonly FakePush _push = new();
    private readonly FakeSkills _skills = new();
    private readonly FakeSessions _sessions = new();
    private readonly FakeMemory _memory = new();
    private readonly FakeDefaultRegistry _defaultRegistry = new();
    private readonly FakeAgentPush _agentPush = new();
    private readonly FakeAgentPull _agentPull = new();
    private readonly List<string> _calls = [];
    private readonly List<SetupStepResult> _reported = [];
    private bool _answer;
    private int _asked;

    public SetupServiceTests()
    {
        _signIn.Calls = _hooks.Calls = _push.Calls = _skills.Calls = _memory.Calls = _sessions.Calls = _agentPush.Calls = _agentPull.Calls = _calls;
    }

    [Fact]
    public async Task A_first_run_signs_in_installs_hooks_pulls_skills_and_backs_up_with_yes()
    {
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));

        var result = await Run(yes: true);

        Assert.Equal(
            [SetupStep.SignIn, SetupStep.Hooks, SetupStep.Skills, SetupStep.Agents, SetupStep.Memory, SetupStep.Configuration, SetupStep.Sessions],
            result.Steps.Select(s => s.Step));
        Assert.Equal(
            [SetupOutcome.Done, SetupOutcome.Done, SetupOutcome.Done, SetupOutcome.Done, SetupOutcome.Done, SetupOutcome.Skipped, SetupOutcome.Done],
            result.Steps.Select(s => s.Outcome));
        Assert.Equal(SetupSkip.NotAvailableYet, result.Steps[5].Skip);
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
    public async Task This_machines_skills_are_pushed_before_the_registrys_are_pulled()
    {
        await Run(yes: true);

        Assert.True(_calls.IndexOf("push") < _calls.IndexOf("pull"), string.Join(", ", _calls));
        var request = Assert.Single(_push.Requests);
        Assert.Empty(request.Names);
        Assert.False(request.Force);
        Assert.False(request.FromHook);
        Assert.Equal("/harness", request.HarnessHome);
    }

    [Fact]
    public async Task A_pushed_skill_alone_makes_the_skills_step_done()
    {
        _push.Status = SkillPushStatus.Created;
        _skills.Status = SkillPullStatus.UpToDate;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Done, result.Steps[2].Outcome);
        Assert.Equal(SkillPushStatus.Created, Assert.Single(result.Steps[2].SkillPush!.Entries).Outcome.Status);
    }

    [Fact]
    public async Task A_push_refused_as_stale_needs_a_decision_and_does_not_fail_setup()
    {
        _signIn.Verification = SessionVerification.Verified;
        _hooks.Change = HookChange.Unchanged;
        _push.Status = SkillPushStatus.Stale;
        _skills.Status = SkillPullStatus.ChangedBoth;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.NeedsDecision, result.Steps[2].Outcome);
        Assert.Equal(SkillPushStatus.Stale, Assert.Single(result.Steps[2].SkillPush!.Entries).Outcome.Status);
        Assert.Equal("greet", Assert.Single(result.Steps[2].SkillDecisions).Name);
        Assert.True(result.Succeeded);
        Assert.False(result.NothingChanged);
        Assert.Equal(1, result.SkillDecisions);
    }

    [Theory]
    [InlineData(nameof(SkillPullStatus.ChangedBoth))]
    [InlineData(nameof(SkillPullStatus.RemovedInRegistry))]
    [InlineData(nameof(SkillPullStatus.LocalChanges))]
    [InlineData(nameof(SkillPullStatus.Conflict))]
    public async Task A_skill_only_the_user_can_settle_makes_the_step_need_a_decision_without_failing(string status)
    {
        _skills.Status = Enum.Parse<SkillPullStatus>(status);

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.NeedsDecision, result.Steps[2].Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.SkillDecisions);
    }

    [Fact]
    public async Task A_dismissed_copy_of_a_removed_skill_is_not_a_decision()
    {
        _skills.Status = SkillPullStatus.RemovedInRegistry;
        _skills.Dismissed = true;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.AlreadyDone, result.Steps[2].Outcome);
        Assert.Empty(result.Steps[2].SkillDecisions);
        Assert.Equal(0, result.SkillDecisions);
    }

    [Fact]
    public async Task A_decision_outranks_skills_installed_in_the_same_run_and_every_one_is_counted()
    {
        _skills.Status = SkillPullStatus.Installed;
        _skills.Others.AddRange([SkillPullStatus.ChangedBoth, SkillPullStatus.UpToDate, SkillPullStatus.RemovedInRegistry]);

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.NeedsDecision, result.Steps[2].Outcome);
        Assert.Equal(["other0", "other2"], result.Steps[2].SkillDecisions.Select(e => e.Name));
        Assert.Equal(2, result.SkillDecisions);
    }

    [Fact]
    public async Task A_skill_that_failed_to_pull_fails_the_step_even_beside_a_decision()
    {
        _skills.Status = SkillPullStatus.Failed;
        _skills.Others.Add(SkillPullStatus.ChangedBoth);

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[2].Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.SkillDecisions);
    }

    [Fact]
    public async Task A_push_that_could_not_run_fails_the_skills_step_but_the_pull_still_runs()
    {
        _push.Failure = SkillPushRunFailure.RegistryUnreachable;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[2].Outcome);
        Assert.Contains("pull", _calls);
        Assert.NotNull(result.Steps[2].Skills);
    }

    [Fact]
    public async Task A_second_run_reports_every_step_already_done_and_changes_nothing()
    {
        _signIn.Verification = SessionVerification.Verified;
        _hooks.Change = HookChange.Unchanged;
        _push.Status = SkillPushStatus.Unchanged;
        _skills.Status = SkillPullStatus.UpToDate;
        _agentPull.Status = AgentPullStatus.UpToDate;
        _memory.Written = 0;
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
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.RegistryUnreachable), (result.Steps[2].Outcome, result.Steps[2].Skip));
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.RegistryUnreachable), (result.Steps[4].Outcome, result.Steps[4].Skip));
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.RegistryUnreachable), (result.Steps[6].Outcome, result.Steps[6].Skip));
        Assert.DoesNotContain("memory pull", _calls);
        Assert.DoesNotContain("pull", _calls);
        Assert.DoesNotContain("push", _calls);
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
    public async Task A_sign_in_that_could_not_reach_the_registry_skips_what_needs_it_for_that_reason()
    {
        _signIn.Failure = SignInFailure.RegistryUnreachable;
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[0].Outcome);
        Assert.Equal(SetupSkip.RegistryUnreachable, result.Steps[2].Skip);
        Assert.Equal(SetupSkip.RegistryUnreachable, result.Steps[6].Skip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Without_yes_or_a_terminal_the_backup_is_skipped_and_nobody_is_asked()
    {
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));

        var result = await Run(yes: false, canAsk: false);

        var sessions = result.Steps[6];
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
        Assert.Equal((outcome, skip), (result.Steps[6].Outcome, result.Steps[6].Skip));
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

        Assert.Equal(new SessionBacklog(2, 3, 125), result.Steps[6].Backlog);
    }

    [Fact]
    public async Task An_unfinished_backup_fails_the_step()
    {
        _sessions.Pending(("s1", TranscriptKind.Session, null, 100));
        _sessions.SyncStatus = TranscriptSyncStatus.Failed;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[6].Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task No_harness_skips_hooks_skills_and_memory()
    {
        _hooks.NoHarness = true;
        _skills.Failure = SkillPullRunFailure.NoHarness;
        _memory.NoHarness = true;

        var result = await Run(yes: true);

        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NoHarness), (result.Steps[1].Outcome, result.Steps[1].Skip));
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NoHarness), (result.Steps[2].Outcome, result.Steps[2].Skip));
        Assert.Equal((SetupOutcome.Skipped, SetupSkip.NoHarness), (result.Steps[4].Outcome, result.Steps[4].Skip));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Memory_is_pulled_after_hooks_and_skills_with_the_harness_home()
    {
        await Run(yes: true);

        Assert.True(_calls.IndexOf("hooks") < _calls.IndexOf("memory pull"), string.Join(", ", _calls));
        Assert.True(_calls.IndexOf("pull") < _calls.IndexOf("memory pull"), string.Join(", ", _calls));
        Assert.Equal("/harness", Assert.Single(_memory.Requests).HarnessHome);
    }

    [Fact]
    public async Task A_memory_pull_that_wrote_nothing_and_left_a_conflict_is_already_done_not_a_decision()
    {
        _memory.Written = 0;
        _memory.Kept = true;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.AlreadyDone, result.Steps[4].Outcome);
        Assert.Equal(MemoryPullNoteKind.Conflicted, Assert.Single(Assert.Single(result.Steps[4].MemoryPull!.Folders).Notes).Kind);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_memory_pull_that_failed_for_a_folder_fails_the_step()
    {
        _memory.Error = "Could not write MEMORY.md";

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[4].Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Personal_agents_are_pushed_then_pulled_after_skills_and_never_forced()
    {
        await Run(yes: true);

        Assert.True(_calls.IndexOf("pull") < _calls.IndexOf("agents push"), string.Join(", ", _calls));
        Assert.True(_calls.IndexOf("agents push") < _calls.IndexOf("agents pull"), string.Join(", ", _calls));
        var push = Assert.Single(_agentPush.Requests);
        Assert.Null(push.ProjectStart);
        Assert.False(push.Force);
        Assert.False(push.FromHook);
        var pull = Assert.Single(_agentPull.Requests);
        Assert.Null(pull.ProjectStart);
        Assert.Empty(pull.Names);
        Assert.False(pull.Force);
        Assert.Equal("/harness", pull.HarnessHome);
    }

    [Theory]
    [InlineData(nameof(AgentPullStatus.ChangedBoth))]
    [InlineData(nameof(AgentPullStatus.RemovedInRegistry))]
    [InlineData(nameof(AgentPullStatus.LocalChanges))]
    [InlineData(nameof(AgentPullStatus.Conflict))]
    public async Task An_agent_only_the_user_can_settle_makes_the_agents_step_need_a_decision_without_failing(string status)
    {
        _agentPull.Status = Enum.Parse<AgentPullStatus>(status);

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.NeedsDecision, result.Steps[3].Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AgentDecisions);
        Assert.Equal(0, result.SkillDecisions);
    }

    [Fact]
    public async Task A_registry_without_agent_downloads_skips_the_agents_step_and_does_not_fail_setup()
    {
        _agentPull.Failure = AgentPullRunFailure.RegistryTooOld;

        var result = await Run(yes: true);

        Assert.Equal((SetupOutcome.Skipped, SetupSkip.RegistryTooOld), (result.Steps[3].Outcome, result.Steps[3].Skip));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_agent_that_failed_to_pull_fails_the_agents_step()
    {
        _agentPull.Status = AgentPullStatus.Failed;

        var result = await Run(yes: true);

        Assert.Equal(SetupOutcome.Failed, result.Steps[3].Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_agents_step_is_skipped_without_sign_in()
    {
        _signIn.Verification = SessionVerification.Unreachable;

        var result = await Run(yes: true);

        Assert.Equal((SetupOutcome.Skipped, SetupSkip.RegistryUnreachable), (result.Steps[3].Outcome, result.Steps[3].Skip));
        Assert.DoesNotContain("agents push", _calls);
    }

    [Fact]
    public async Task An_invalid_registry_stops_before_any_step()
    {
        var service = new SetupService(_signIn, _hooks, _push, _skills, _memory, _sessions, _defaultRegistry, _agentPush, _agentPull);
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
        var service = new SetupService(_signIn, _hooks, _push, _skills, _memory, _sessions, _defaultRegistry, _agentPush, _agentPull);
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

    private sealed class FakePush : ISkillPushService
    {
        public List<string> Calls { get; set; } = [];
        public List<SkillPushRequest> Requests { get; } = [];
        public SkillPushStatus Status { get; set; } = SkillPushStatus.Unchanged;
        public SkillPushRunFailure Failure { get; set; } = SkillPushRunFailure.None;

        public Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("push");
            Requests.Add(request);
            var registry = new Uri(Registry);
            var skill = new DiscoveredSkill { Name = "greet", Scope = SkillScope.Global, Path = "/harness/skills/greet" };
            return Task.FromResult(Failure != SkillPushRunFailure.None
                ? SkillPushRunResult.Fail(Failure, null, registry)
                : new SkillPushRunResult(SkillPushRunFailure.None, null, registry, [new SkillPushEntry(skill, new SkillPushOutcome(Status, 1, null))]));
        }
    }

    private sealed class FakeAgentPush : IAgentPushService
    {
        public List<string> Calls { get; set; } = [];
        public List<AgentPushRequest> Requests { get; } = [];

        public Task<AgentPushRunResult> PushAsync(AgentPushRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("agents push");
            Requests.Add(request);
            var agent = new DiscoveredAgent { Name = "reviewer", Scope = AgentScope.Personal, Path = "/harness/agents/reviewer.md", RelativePath = "reviewer.md" };
            return Task.FromResult(new AgentPushRunResult(AgentPushRunFailure.None, null, new Uri(Registry), [new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Unchanged, 1, null))]));
        }

        public IReadOnlyList<DiscoveredAgent> Changed(string? harnessHome, string? projectStart, string? registry) => throw new NotSupportedException();
    }

    private sealed class FakeAgentPull : IAgentPullService
    {
        public List<string> Calls { get; set; } = [];
        public List<AgentPullRequest> Requests { get; } = [];
        public AgentPullStatus Status { get; set; } = AgentPullStatus.Installed;
        public AgentPullRunFailure Failure { get; set; } = AgentPullRunFailure.None;

        public Task<AgentRemoteResult> CompareAsync(AgentRemoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentPullRunResult> PullAsync(AgentPullRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("agents pull");
            Requests.Add(request);
            var registry = new Uri(Registry);
            return Task.FromResult(Failure != AgentPullRunFailure.None
                ? AgentPullRunResult.Fail(Failure, "This registry is too old for reading agents. Update the registry.", registry)
                : new AgentPullRunResult(AgentPullRunFailure.None, null, registry, [new AgentPullEntry(Harness, "reviewer", Status, 1, "/harness/agents/reviewer.md", null)]));
        }
    }

    private sealed class FakeSkills : ISkillPullService
    {
        public List<string> Calls { get; set; } = [];
        public SkillPullStatus Status { get; set; } = SkillPullStatus.Installed;

        /// <summary>The statuses of more skills in the same pull, after "greet".</summary>
        public List<SkillPullStatus> Others { get; } = [];
        public SkillPullRunFailure Failure { get; set; } = SkillPullRunFailure.None;

        public Task<SkillRemoteResult> CompareAsync(SkillRemoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SkillPullRunResult> DismissAsync(SkillPullRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        /// <summary>Marks the "greet" entry as dismissed (`skills dismiss`).</summary>
        public bool Dismissed { get; set; }

        public Task<SkillPullRunResult> PullAsync(SkillPullRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("pull");
            Assert.Empty(request.Names);
            Assert.False(request.Force);
            var registry = new Uri(Registry);
            return Task.FromResult(Failure != SkillPullRunFailure.None
                ? SkillPullRunResult.Fail(Failure, null, registry)
                : new SkillPullRunResult(SkillPullRunFailure.None, null, registry,
                [
                    new SkillPullEntry(Harness, "greet", Status, 1, "/harness/skills/greet", null, Dismissed: Dismissed),
                    .. Others.Select((status, i) => new SkillPullEntry(Harness, $"other{i}", status, 1, $"/harness/skills/other{i}", null)),
                ]));
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

    private sealed class FakeMemory : IMemorySyncService
    {
        public List<string> Calls { get; set; } = [];
        public List<MemoryPullRequest> Requests { get; } = [];
        public int Written { get; set; } = 1;
        public bool Kept { get; set; }
        public bool NoHarness { get; set; }
        public string? Error { get; set; }

        public Task<MemoryPushResult> PushAsync(MemoryPushRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public MemoryStatusResult Status(string? registry, string? harnessHome) => throw new NotSupportedException();

        public Task<MemoryPullResult> PullAsync(MemoryPullRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("memory pull");
            Requests.Add(request);
            var notes = Kept ? new[] { new MemoryPullNote("notes.md", MemoryPullNoteKind.Conflicted, ["laptop"], "notes.from-laptop.md") } : [];
            var folder = new MemoryPullFolder("repo", "/src/repo", "/harness/projects/-src-repo/memory", Written, 0, 1, notes, Error);
            return Task.FromResult(new MemoryPullResult(SessionSyncFailure.None, null, new Uri(Registry), 1, NoHarness ? [] : [folder], []) { NoHarness = NoHarness });
        }
    }
}
