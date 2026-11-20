using Protostar.Cli.Auth;
using Protostar.Cli.Hooks;
using Protostar.Cli.Memory;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Setup;

/// <summary>Gets a machine ready in one run: sign in, install hooks, push and pull skills, pull memory, and back up sessions.</summary>
/// <remarks>
/// Backs <c>protostar setup</c>. Each step calls the same service its own command does (<see cref="ISignInService"/>,
/// <see cref="IHookInstallService"/>, <see cref="ISkillPushService"/> and <see cref="ISkillPullService"/>,
/// <see cref="IMemorySyncService"/>, <see cref="ISessionSyncService"/>), so setup has
/// no second implementation of any of them. Every step is idempotent, so running setup again changes nothing and says so.
/// </remarks>
internal interface ISetupService
{
    /// <summary>Runs every step in order, reporting each through <paramref name="interaction"/> as it finishes.</summary>
    /// <remarks>
    /// The order is fixed: sign-in, hooks, skills, memory, configuration, sessions. Hooks go in before skills are pulled so a
    /// skill used the moment it lands is already captured; the pull records each skill in the ledger, so that capture
    /// recognises it as unchanged and does not push it straight back. Within the skills step this machine's skills are
    /// pushed before the registry's are pulled, so a skill changed here is backed up rather than kept back by the pull
    /// as a local change. Memory is pulled after hooks for the same reason as skills: the pull records each file it
    /// writes in the memory ledger, so the hooks' background push does not send it back. A step that needs the registry is skipped when
    /// sign-in did not succeed. Every step runs even when an earlier one failed, so one run reports everything.
    /// </remarks>
    Task<SetupResult> RunAsync(SetupRequest request, SetupInteraction interaction, CancellationToken cancellationToken);
}

/// <summary>What <c>protostar setup</c> was asked to do.</summary>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="SignIn">How to run the browser sign-in when one is needed.</param>
/// <param name="Yes">Back up pending sessions without asking.</param>
/// <param name="CanAsk">Whether there is a terminal to ask on; without one and without <paramref name="Yes"/>, the backup is skipped.</param>
/// <param name="ProjectStart">The folder whose project skills are pushed and pulled, as <c>skills push</c> and <c>skills pull</c> do; <c>null</c> syncs global skills only.</param>
internal sealed record SetupRequest(
    string? Registry, string? HarnessHome, SignInOptions SignIn, bool Yes, bool CanAsk, string? ProjectStart);

/// <summary>The presentation side of a setup run, injected so the service never touches the console.</summary>
/// <param name="StepFinished">Called once per step, in order, as soon as it finishes.</param>
/// <param name="ConfirmBackup">Asks whether to back up the pending sessions now; only called when <see cref="SetupRequest.CanAsk"/> and not <see cref="SetupRequest.Yes"/>.</param>
/// <param name="RunBackup">Runs the backup it is handed in the foreground, with progress, and returns its result.</param>
internal sealed record SetupInteraction(
    Action<SetupStepResult> StepFinished,
    Func<SessionBacklog, bool> ConfirmBackup,
    Func<SessionBacklog, Func<IProgress<SessionSyncProgress>?, Task<SessionSyncResult>>, Task<SessionSyncResult>> RunBackup);

/// <summary>The steps of a setup run, in the order they run.</summary>
internal enum SetupStep
{
    SignIn,
    Hooks,
    Skills,

    /// <summary>Bringing the registry's memory to this machine's checkouts.</summary>
    Memory,

    /// <summary>Harness configuration sync (epic E4); not available yet, so always skipped.</summary>
    Configuration,
    Sessions,
}

/// <summary>How a step ended.</summary>
internal enum SetupOutcome
{
    /// <summary>The step changed something on this machine or in the registry.</summary>
    Done,

    /// <summary>Nothing needed changing.</summary>
    AlreadyDone,

    /// <summary>The step ran, but something it found is left for the user to choose; see <see cref="SetupStepResult.SkillDecisions"/>.</summary>
    /// <remarks>
    /// Not a failure: running setup again cannot settle it, only the user's choice can, so it does not change the exit code.
    /// It takes precedence over <see cref="Done"/> and <see cref="AlreadyDone"/>, so the step never reads as finished while
    /// a choice is open.
    /// </remarks>
    NeedsDecision,

    /// <summary>Not run; see <see cref="SetupStepResult.Skip"/>.</summary>
    Skipped,

    /// <summary>The step failed; the payload says why.</summary>
    Failed,
}

/// <summary>Why a step was skipped.</summary>
internal enum SetupSkip
{
    None,

    /// <summary>The step needs the registry and sign-in did not succeed.</summary>
    NotSignedIn,

    /// <summary>The step needs the registry, and sign-in failed because the registry could not be reached.</summary>
    RegistryUnreachable,

    /// <summary>No supported harness was found on this machine.</summary>
    NoHarness,

    /// <summary>The step's feature does not exist yet.</summary>
    NotAvailableYet,

    /// <summary>The user answered no to backing up sessions now.</summary>
    Declined,

    /// <summary>There was no terminal to ask on and <c>--yes</c> was not given.</summary>
    NoTerminal,
}

/// <summary>Sessions on this machine the registry does not have yet.</summary>
/// <param name="Sessions">Sessions with something pending, counting a subagent under its parent session.</param>
/// <param name="Transcripts">Transcript files with something pending, subagents included.</param>
/// <param name="Bytes">Bytes still to upload across them.</param>
internal sealed record SessionBacklog(int Sessions, int Transcripts, long Bytes);

/// <summary>One step's outcome, with the result of the service it called.</summary>
/// <remarks>Only the payloads that belong to <see cref="Step"/> are set; the presenter reads them to describe the step.</remarks>
internal sealed record SetupStepResult(SetupStep Step, SetupOutcome Outcome, SetupSkip Skip = SetupSkip.None)
{
    /// <summary>The stored session's check, for <see cref="SetupStep.SignIn"/>.</summary>
    public SessionCheck? SessionCheck { get; init; }

    /// <summary>The browser sign-in, for <see cref="SetupStep.SignIn"/> when one ran.</summary>
    public SignInResult? SignIn { get; init; }

    /// <summary>The hook install, for <see cref="SetupStep.Hooks"/>.</summary>
    public HookRunResult? Hooks { get; init; }

    /// <summary>The push of this machine's skills, for <see cref="SetupStep.Skills"/> when signed in; it runs before <see cref="Skills"/>.</summary>
    public SkillPushRunResult? SkillPush { get; init; }

    /// <summary>The skill pull, for <see cref="SetupStep.Skills"/>.</summary>
    public SkillPullRunResult? Skills { get; init; }

    /// <summary>The memory pull, for <see cref="SetupStep.Memory"/> when signed in.</summary>
    public MemoryPullResult? MemoryPull { get; init; }

    /// <summary>The offline backup status, for <see cref="SetupStep.Sessions"/>.</summary>
    public SessionStatusResult? SessionStatus { get; init; }

    /// <summary>What was pending before any backup, for <see cref="SetupStep.Sessions"/>.</summary>
    public SessionBacklog? Backlog { get; init; }

    /// <summary>The backup, for <see cref="SetupStep.Sessions"/> when one ran.</summary>
    public SessionSyncResult? Backup { get; init; }

    /// <summary>The skills the pull kept or left alone that only the user can settle, in pull order; empty for other steps.</summary>
    /// <remarks>
    /// Changed here and in the registry, deleted or renamed in the registry but kept here, kept for local changes the push
    /// did not back up, and a conflict with a folder or a same-name twin. None of them changes on a second run.
    /// </remarks>
    public IReadOnlyList<SkillPullEntry> SkillDecisions =>
        Skills?.Entries.Where(e => e.Status is SkillPullStatus.ChangedBoth or SkillPullStatus.RemovedInRegistry
            or SkillPullStatus.LocalChanges or SkillPullStatus.Conflict).ToList() ?? [];
}

/// <summary>Every step's outcome, in order.</summary>
/// <param name="Registry">The registry setup ran against; <c>null</c> when <see cref="InvalidRegistry"/> stopped it first.</param>
/// <param name="InvalidRegistry">Why the registry URL was rejected before any step ran; <c>null</c> otherwise.</param>
internal sealed record SetupResult(Uri? Registry, string? InvalidRegistry, IReadOnlyList<SetupStepResult> Steps)
{
    /// <summary>True when the run went ahead and no step failed. A skipped step is not a failure.</summary>
    public bool Succeeded => InvalidRegistry is null && Steps.All(s => s.Outcome != SetupOutcome.Failed);

    /// <summary>True when the run changed nothing and left nothing open: every step was already done or skipped.</summary>
    public bool NothingChanged => Succeeded && Steps.All(s => s.Outcome is SetupOutcome.AlreadyDone or SetupOutcome.Skipped);

    /// <summary>How many skills need the user's decision, across every step.</summary>
    public int SkillDecisions => Steps.Sum(s => s.SkillDecisions.Count);
}
