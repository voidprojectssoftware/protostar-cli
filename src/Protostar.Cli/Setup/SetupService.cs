using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Setup;

/// <summary>Production <see cref="ISetupService"/>: composes the sign-in, hook, skill push and pull, and session services.</summary>
/// <remarks>
/// Each step only classifies the result of the services it calls: <see cref="SetupOutcome.Done"/> when that result
/// shows a change, <see cref="SetupOutcome.AlreadyDone"/> when it shows none. Hooks are installed into every detected
/// harness without prompting, as <c>install-hooks --yes</c> does. Skills are pushed and then pulled with the same rules
/// as <c>skills push</c> and <c>skills pull</c> (never <c>--force</c>), so a refused push or a conflict is reported and
/// left for the user to resolve, never overwritten.
/// </remarks>
internal sealed class SetupService : ISetupService
{
    private readonly ISignInService _signIn;
    private readonly IHookInstallService _hooks;
    private readonly ISkillPushService _push;
    private readonly ISkillPullService _skills;
    private readonly ISessionSyncService _sessions;
    private readonly IDefaultRegistryStore _defaultRegistry;

    public SetupService(ISignInService signIn, IHookInstallService hooks, ISkillPushService push, ISkillPullService skills,
        ISessionSyncService sessions, IDefaultRegistryStore defaultRegistry)
    {
        _push = push;
        _signIn = signIn;
        _defaultRegistry = defaultRegistry;
        _hooks = hooks;
        _skills = skills;
        _sessions = sessions;
    }

    /// <inheritdoc />
    public async Task<SetupResult> RunAsync(SetupRequest request, SetupInteraction interaction, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(request.Registry);
        }
        catch (FormatException ex)
        {
            return new SetupResult(null, ex.Message, []);
        }

        var steps = new List<SetupStepResult>();
        void Finish(SetupStepResult step)
        {
            steps.Add(step);
            interaction.StepFinished(step);
        }

        var signIn = await SignInAsync(registry, request.SignIn, cancellationToken);
        Finish(signIn);
        // None when signed in; otherwise why the steps that need the registry are skipped.
        var whyNotSignedIn = signIn.Outcome is SetupOutcome.Done or SetupOutcome.AlreadyDone ? SetupSkip.None
            : signIn.SessionCheck?.Verification == SessionVerification.Unreachable || signIn.SignIn?.Failure == SignInFailure.RegistryUnreachable
                ? SetupSkip.RegistryUnreachable
                : SetupSkip.NotSignedIn;

        Finish(InstallHooks(request.HarnessHome));

        Finish(whyNotSignedIn == SetupSkip.None
            ? await SyncSkillsAsync(request, cancellationToken)
            : new SetupStepResult(SetupStep.Skills, SetupOutcome.Skipped, whyNotSignedIn));

        Finish(new SetupStepResult(SetupStep.Configuration, SetupOutcome.Skipped, SetupSkip.NotAvailableYet));

        Finish(await BackUpSessionsAsync(request, whyNotSignedIn, interaction, cancellationToken));

        return new SetupResult(registry, null, steps);
    }

    private async Task<SetupStepResult> SignInAsync(Uri registry, SignInOptions options, CancellationToken cancellationToken)
    {
        var check = await _signIn.CheckAsync(registry, cancellationToken);
        switch (check.Verification)
        {
            case SessionVerification.Verified:
                // A fresh sign-in saves the default itself; an existing session still makes this registry the one hooks use.
                return _defaultRegistry.Save(registry)
                    ? new SetupStepResult(SetupStep.SignIn, SetupOutcome.AlreadyDone) { SessionCheck = check }
                    : new SetupStepResult(SetupStep.SignIn, SetupOutcome.Failed)
                    {
                        SessionCheck = check,
                        SignIn = new SignInResult(SignInFailure.SaveFailed, null, registry, check.Login),
                    };
            case SessionVerification.Unreachable:
                // Signing in again would fail the same way; say the registry is down rather than open a browser.
                return new SetupStepResult(SetupStep.SignIn, SetupOutcome.Failed) { SessionCheck = check };
        }

        var result = await _signIn.SignInAsync(registry, options, cancellationToken);
        return new SetupStepResult(SetupStep.SignIn, result.Succeeded ? SetupOutcome.Done : SetupOutcome.Failed)
        {
            SessionCheck = check,
            SignIn = result,
        };
    }

    private SetupStepResult InstallHooks(string? harnessHome)
    {
        var result = _hooks.Install(new HookInstallOptions { RootOverride = harnessHome });
        var outcome = result switch
        {
            { Failure: not HookRunFailure.None } => SetupOutcome.Failed,
            { Results.Count: 0 } => SetupOutcome.Skipped,
            _ when result.Results.Any(r => r.Failed) => SetupOutcome.Failed,
            _ when result.Results.All(r => r.Change!.Change == HookChange.Unchanged) => SetupOutcome.AlreadyDone,
            _ => SetupOutcome.Done,
        };
        return new SetupStepResult(SetupStep.Hooks, outcome, outcome == SetupOutcome.Skipped ? SetupSkip.NoHarness : SetupSkip.None)
        {
            Hooks = result,
        };
    }

    // Push before pull, so a skill changed here is backed up before the pull compares it: otherwise the pull would
    // keep it as "local changes" and leave it un-backed-up. A push the registry refused as stale is recorded by the push
    // service, so the pull that follows shows the copy as changed here and in the registry and keeps it. That, a
    // conflict, and a copy of a skill deleted or renamed in the registry that the pull keeps (it renames an unedited one
    // to follow a rename) need the user's decision: the step says so, but they are not failures, since running setup
    // again would not change them.
    private async Task<SetupStepResult> SyncSkillsAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        var push = await _push.PushAsync(
            new SkillPushRequest([], null, request.HarnessHome, request.ProjectStart, request.Registry), cancellationToken);
        var pull = await _skills.PullAsync(
            new SkillPullRequest([], null, request.HarnessHome, request.Registry, Force: false, request.ProjectStart), cancellationToken);

        var outcome = pull switch
        {
            { Failure: SkillPullRunFailure.NoHarness } => SetupOutcome.Skipped,
            _ when pull.Failure != SkillPullRunFailure.None || pull.Entries.Any(e => e.Status == SkillPullStatus.Failed) => SetupOutcome.Failed,
            _ when push.Failure != SkillPushRunFailure.None || push.Entries.Any(e => e.Outcome.Status == SkillPushStatus.Failed) => SetupOutcome.Failed,
            _ when push.Entries.Any(e => e.Outcome.Status == SkillPushStatus.Created) ||
                pull.Entries.Any(e => e.Status is SkillPullStatus.Installed or SkillPullStatus.Updated or SkillPullStatus.FollowedRename) => SetupOutcome.Done,
            _ => SetupOutcome.AlreadyDone,
        };
        var step = new SetupStepResult(SetupStep.Skills, outcome, outcome == SetupOutcome.Skipped ? SetupSkip.NoHarness : SetupSkip.None)
        {
            SkillPush = push,
            Skills = pull,
        };
        return outcome is (SetupOutcome.Done or SetupOutcome.AlreadyDone) && step.SkillDecisions.Count > 0
            ? step with { Outcome = SetupOutcome.NeedsDecision }
            : step;
    }

    private async Task<SetupStepResult> BackUpSessionsAsync(
        SetupRequest request, SetupSkip whyNotSignedIn, SetupInteraction interaction, CancellationToken cancellationToken)
    {
        var status = _sessions.Status(request.Registry, null, request.HarnessHome);
        if (status.Failure != SessionSyncFailure.None)
            return new SetupStepResult(SetupStep.Sessions, SetupOutcome.Failed) { SessionStatus = status };

        var backlog = Backlog(status.Transcripts);
        SetupStepResult Step(SetupOutcome outcome, SetupSkip skip = SetupSkip.None) =>
            new(SetupStep.Sessions, outcome, skip) { SessionStatus = status, Backlog = backlog };

        if (backlog.Transcripts == 0)
            return Step(SetupOutcome.AlreadyDone);
        if (whyNotSignedIn != SetupSkip.None)
            return Step(SetupOutcome.Skipped, whyNotSignedIn);

        // A first backup can be many gigabytes, so it never starts without a yes: the flag, or an answer on a terminal.
        if (!request.Yes)
        {
            if (!request.CanAsk)
                return Step(SetupOutcome.Skipped, SetupSkip.NoTerminal);
            if (!interaction.ConfirmBackup(backlog))
                return Step(SetupOutcome.Skipped, SetupSkip.Declined);
        }

        var sync = new SessionSyncRequest(request.Registry, null, request.HarnessHome, null, null, null);
        var result = await interaction.RunBackup(backlog, progress => _sessions.SyncAsync(sync, progress, cancellationToken));
        return Step(result.Complete ? SetupOutcome.Done : SetupOutcome.Failed) with { Backup = result };
    }

    // A subagent's transcript belongs to its parent's session, so a session with pending subagents counts once.
    private static SessionBacklog Backlog(IReadOnlyList<TranscriptStatus> transcripts)
    {
        var pending = transcripts.Where(t => t.BytesPending > 0).ToList();
        var sessions = pending
            .Select(t => t.Transcript.Kind == TranscriptKind.Subagent ? t.Transcript.ParentKey ?? t.Transcript.Key : t.Transcript.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return new SessionBacklog(sessions, pending.Count, pending.Sum(t => t.BytesPending));
    }
}
