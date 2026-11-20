using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Backs up local skills to the registry.
/// </summary>
/// <remarks>
/// Shared by <c>protostar skills push</c> and the capture hook. Returns data only; rendering belongs to
/// <see cref="SkillPushPresenter"/>.
/// </remarks>
internal interface ISkillPushService
{
    /// <summary>
    /// Discovers the requested skills and uploads each one to the registry as a new version.
    /// </summary>
    /// <remarks>
    /// Skills are sent one request each, so one refused skill never fails the others. A project skill is
    /// sent with its project folder's facts so the registry keeps it apart from a global skill or another
    /// project's skill of the same name. When two discovered skills of one scope share a name, the first in
    /// discovery order is pushed; a later one is skipped when its content is identical, and reported as
    /// <see cref="SkillPushStatus.Conflict"/> when it differs, since pushing both would make them overwrite
    /// each other.
    /// <para>
    /// Each push names the version its copy was last synced from (from the ledger), so the registry refuses an edit
    /// made from an older version than its current one instead of stacking it on top. Such a skill comes back as
    /// <see cref="SkillPushStatus.Stale"/>, is never retried, and the refusal is recorded in the ledger so
    /// <c>skills remote</c> shows the copy as changed here and in the registry, <c>skills pull</c> does not overwrite
    /// it, and the capture hook does not send the same content again.
    /// </para>
    /// <para>
    /// Except from the hook, a run that has skills the ledger says are synced asks the registry for its skill list once.
    /// A skill whose registry skill (the name and project it last synced as) is no longer listed because it was deleted or
    /// renamed since this copy synced, which the list's removed names say, is not uploaded: it comes back as
    /// <see cref="SkillPushStatus.Removed"/>, and an edited copy's refusal is recorded like a stale one. With
    /// <see cref="SkillPushRequest.Force"/> it is restored on purpose, as a new skill. A registry skill that is gone without
    /// the registry remembering why (an older registry) is pushed again as a new skill with a message saying so.
    /// </para>
    /// </remarks>
    Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken);
}

/// <summary>What to push and where.</summary>
/// <param name="Names">Skill names to push (case-insensitive); empty pushes every discovered skill.</param>
/// <param name="HarnessId">Limit discovery to this harness; <c>null</c> for every harness.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="ProjectStart">Where the project-scope walk-up starts; <c>null</c> skips project skills.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="Force">
/// Upload even when the local ledger says the registry already has this content, and replace the registry's current
/// version when it is newer than the one this copy was based on (keep mine). The replaced version stays in history.
/// </param>
/// <param name="As">
/// Push the one selected skill as a new registry skill of this name, and rename the local copy to match (keep both).
/// </param>
/// <param name="FromHook">
/// The capture hook is pushing: the ledger is trusted without checking the registry's list, so an unchanged skill
/// costs no network call, and a copy whose exact content the registry already refused as stale is reported from
/// the ledger without asking again, so a refused skill costs nothing on every later use.
/// </param>
internal sealed record SkillPushRequest(
    IReadOnlyList<string> Names,
    string? HarnessId,
    string? HarnessHome,
    string? ProjectStart,
    string? Registry,
    bool Force = false,
    string? As = null,
    bool FromHook = false);

/// <summary>Why a push run stopped before uploading anything.</summary>
internal enum SkillPushRunFailure
{
    /// <summary>The run went ahead; see the per-skill entries.</summary>
    None,

    /// <summary>The registry URL is not an absolute http(s) URL.</summary>
    InvalidRegistry,

    /// <summary>A <c>--harness</c> id matched no registered harness.</summary>
    UnknownHarness,

    /// <summary>The named harness does not support skill discovery.</summary>
    Unsupported,

    /// <summary>A requested skill name was not found on this machine.</summary>
    SkillNotFound,

    /// <summary>There is no stored session for the registry.</summary>
    NotLoggedIn,

    /// <summary>The stored session has expired and could not be refreshed.</summary>
    SessionExpired,

    /// <summary>The registry did not answer its metadata endpoint.</summary>
    RegistryUnreachable,

    /// <summary>The registry does not speak this CLI's API major, or lacks a feature the push needs; the detail says which.</summary>
    Incompatible,

    /// <summary>
    /// <c>--as</c> needs exactly one local skill to push under a new name that nothing in its scope, here or in the registry,
    /// already answers to; see the detail.
    /// </summary>
    InvalidAs,

    /// <summary>The registry answered, but would not list the skills needed to confirm what the ledger says; see the detail.</summary>
    ListFailed,
}

/// <summary>One skill and what happened to it.</summary>
internal sealed record SkillPushEntry(DiscoveredSkill Skill, SkillPushOutcome Outcome);

/// <summary>The result of a push run.</summary>
/// <param name="Failure">Why the run stopped early, or <see cref="SkillPushRunFailure.None"/>.</param>
/// <param name="Detail">The offending value or explanation for a failure; <c>null</c> otherwise.</param>
/// <param name="Registry">The registry the run targeted; <c>null</c> when its URL was invalid.</param>
/// <param name="Entries">One entry per discovered skill, in discovery order; empty on an early failure.</param>
/// <param name="Warning">Something the run went ahead despite, such as a <c>--force</c> push to a registry without stale protection.</param>
internal sealed record SkillPushRunResult(
    SkillPushRunFailure Failure,
    string? Detail,
    Uri? Registry,
    IReadOnlyList<SkillPushEntry> Entries,
    string? Warning = null)
{
    /// <summary>True when the run went ahead and every skill reached the registry.</summary>
    public bool Succeeded => Failure == SkillPushRunFailure.None && Entries.All(e => e.Outcome.Succeeded || e.Outcome.Status == SkillPushStatus.Skipped);

    public static SkillPushRunResult Fail(SkillPushRunFailure failure, string? detail, Uri? registry = null) =>
        new(failure, detail, registry, []);
}
