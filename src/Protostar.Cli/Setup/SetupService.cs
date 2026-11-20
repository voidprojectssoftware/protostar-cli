using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Setup;

/// <summary>Production <see cref="ISetupService"/>: composes the sign-in, hook, skill-pull and session services.</summary>
/// <remarks>
/// Each step only classifies the result of the service it calls: <see cref="SetupOutcome.Done"/> when that result
/// shows a change, <see cref="SetupOutcome.AlreadyDone"/> when it shows none. Hooks are installed into every detected
/// harness without prompting, as <c>install-hooks --yes</c> does, and skills are pulled with the same conflict and
/// local-changes rules as <c>skills pull</c> (never <c>--force</c>).
/// </remarks>
internal sealed class SetupService : ISetupService
{
    private readonly ISignInService _signIn;
    private readonly IHookInstallService _hooks;
    private readonly ISkillPullService _skills;
    private readonly ISessionSyncService _sessions;

    public SetupService(ISignInService signIn, IHookInstallService hooks, ISkillPullService skills, ISessionSyncService sessions)
    {
        _signIn = signIn;
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
        var signedIn = signIn.Outcome is SetupOutcome.Done or SetupOutcome.AlreadyDone;

        Finish(InstallHooks(request.HarnessHome));

        Finish(signedIn
            ? await PullSkillsAsync(request, cancellationToken)
            : new SetupStepResult(SetupStep.Skills, SetupOutcome.Skipped, SetupSkip.NotSignedIn));

        Finish(new SetupStepResult(SetupStep.Configuration, SetupOutcome.Skipped, SetupSkip.NotAvailableYet));

        Finish(await BackUpSessionsAsync(request, signedIn, interaction, cancellationToken));

        return new SetupResult(registry, null, steps);
    }

    private async Task<SetupStepResult> SignInAsync(Uri registry, SignInOptions options, CancellationToken cancellationToken)
    {
        var check = await _signIn.CheckAsync(registry, cancellationToken);
        switch (check.Verification)
        {
            case SessionVerification.Verified:
                return new SetupStepResult(SetupStep.SignIn, SetupOutcome.AlreadyDone) { SessionCheck = check };
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

    private async Task<SetupStepResult> PullSkillsAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        var pull = new SkillPullRequest([], null, request.HarnessHome, request.Registry, Force: false, request.ProjectStart);
        var result = await _skills.PullAsync(pull, cancellationToken);
        var outcome = result switch
        {
            { Failure: SkillPullRunFailure.NoHarness } => SetupOutcome.Skipped,
            { Succeeded: false } => SetupOutcome.Failed,
            _ when result.Entries.Any(e => e.Status is SkillPullStatus.Installed or SkillPullStatus.Updated) => SetupOutcome.Done,
            _ => SetupOutcome.AlreadyDone,
        };
        return new SetupStepResult(SetupStep.Skills, outcome, outcome == SetupOutcome.Skipped ? SetupSkip.NoHarness : SetupSkip.None)
        {
            Skills = result,
        };
    }

    private async Task<SetupStepResult> BackUpSessionsAsync(
        SetupRequest request, bool signedIn, SetupInteraction interaction, CancellationToken cancellationToken)
    {
        var status = _sessions.Status(request.Registry, null, request.HarnessHome);
        if (status.Failure != SessionSyncFailure.None)
            return new SetupStepResult(SetupStep.Sessions, SetupOutcome.Failed) { SessionStatus = status };

        var backlog = Backlog(status.Transcripts);
        SetupStepResult Step(SetupOutcome outcome, SetupSkip skip = SetupSkip.None) =>
            new(SetupStep.Sessions, outcome, skip) { SessionStatus = status, Backlog = backlog };

        if (backlog.Transcripts == 0)
            return Step(SetupOutcome.AlreadyDone);
        if (!signedIn)
            return Step(SetupOutcome.Skipped, SetupSkip.NotSignedIn);

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
