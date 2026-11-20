using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Brings skills from the registry to this machine: shows how each registry skill compares with what is
/// installed here, and installs or updates them.
/// </summary>
/// <remarks>
/// Backs <c>protostar skills remote</c> and <c>protostar skills pull</c>. Global skills are compared with,
/// and installed into, each harness's global skills directory. A project skill is compared and installed
/// only when the folder the command runs in resolves to that skill's project, and then into that checkout's
/// own project skills directory (<c>&lt;repo top level&gt;/.claude/skills</c> for Claude Code), so a worktree
/// installs into itself. Other projects' skills are listed as theirs and never written. Returns data only;
/// rendering belongs to <see cref="SkillPullPresenter"/>.
/// </remarks>
internal interface ISkillPullService
{
    /// <summary>Lists the registry's skills and the local global skills, each with its sync state.</summary>
    Task<SkillRemoteResult> CompareAsync(SkillRemoteRequest request, CancellationToken cancellationToken);

    /// <summary>Installs or updates the requested registry skills on this machine.</summary>
    /// <remarks>
    /// A skill with local changes is never overwritten unless <see cref="SkillPullRequest.Force"/> is set;
    /// it is reported as <see cref="SkillPullStatus.LocalChanges"/> (or <see cref="SkillPullStatus.ChangedBoth"/>
    /// when the registry moved on too) instead. A
    /// <see cref="SkillPullStatus.Conflict"/> is never written, even with <c>Force</c>: that flag means
    /// "overwrite my edits to this skill", not "overwrite an unrelated folder". After a pull the ledger
    /// records the installed content, so the capture hook does not push it straight back.
    /// </remarks>
    Task<SkillPullRunResult> PullAsync(SkillPullRequest request, CancellationToken cancellationToken);
}

/// <summary>Which registry and harnesses to compare.</summary>
/// <param name="HarnessId">Limit to this harness; <c>null</c> for every installed harness that has skills.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="ProjectStart">The folder whose project's skills are compared; <c>null</c> compares global skills only.</param>
/// <param name="Names">Registry skill names to compare, matched as a pull matches them; <c>null</c> or empty compares every skill.</param>
/// <param name="Version">
/// Compare with this version of the one named skill instead of its current version: what <c>skills pull NAME --version N</c>
/// would do. Needs exactly one name.
/// </param>
internal sealed record SkillRemoteRequest(
    string? HarnessId, string? HarnessHome, string? Registry, string? ProjectStart = null, IReadOnlyList<string>? Names = null, int? Version = null);

/// <summary>What to pull and where.</summary>
/// <param name="Names">
/// Registry skill names to pull, matched exactly first and then ignoring case; empty pulls every registry skill.
/// </param>
/// <param name="HarnessId">Limit to this harness; <c>null</c> for every installed harness that has skills.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="Force">Overwrite skills that have local changes.</param>
/// <param name="ProjectStart">The folder whose project's skills are installed; <c>null</c> pulls global skills only.</param>
/// <param name="Version">
/// Install this earlier (or any) version of the one named skill instead of its current version, to recover work. Needs
/// exactly one name, and keeps every rule of an ordinary pull: local changes are kept unless forced, and conflicts and
/// other projects' skills are never written.
/// </param>
internal sealed record SkillPullRequest(
    IReadOnlyList<string> Names,
    string? HarnessId,
    string? HarnessHome,
    string? Registry,
    bool Force = false,
    string? ProjectStart = null,
    int? Version = null);

/// <summary>Why a remote listing or pull stopped before doing anything.</summary>
internal enum SkillPullRunFailure
{
    /// <summary>The run went ahead.</summary>
    None,

    /// <summary>The registry URL is not an absolute http(s) URL.</summary>
    InvalidRegistry,

    /// <summary>A <c>--harness</c> id matched no registered harness.</summary>
    UnknownHarness,

    /// <summary>The named harness does not support skills.</summary>
    Unsupported,

    /// <summary>No harness that supports skills is installed on this machine.</summary>
    NoHarness,

    /// <summary>There is no stored session for the registry.</summary>
    NotLoggedIn,

    /// <summary>The stored session has expired and could not be refreshed.</summary>
    SessionExpired,

    /// <summary>The registry did not answer its metadata endpoint.</summary>
    RegistryUnreachable,

    /// <summary>The registry does not speak this CLI's API major.</summary>
    Incompatible,

    /// <summary>The registry answered but would not list the skills; see the detail.</summary>
    ListFailed,

    /// <summary>A requested skill name is not in the registry.</summary>
    SkillNotFound,

    /// <summary><c>--version</c> was given without exactly one skill name, or is not a version number; see the detail.</summary>
    InvalidVersion,

    /// <summary>The requested version of the named skill could not be fetched (it does not exist, say); see the detail.</summary>
    VersionNotFound,
}

/// <summary>How a skill on this machine compares with the registry.</summary>
internal enum SkillSyncState
{
    /// <summary>In the registry, not installed here.</summary>
    NotInstalled,

    /// <summary>Installed here with exactly the registry's current content.</summary>
    UpToDate,

    /// <summary>Installed here unchanged since it last synced, and the registry has a newer version.</summary>
    UpdateAvailable,

    /// <summary>Installed here and differs from the registry in a way this machine made (or cannot rule out).</summary>
    LocalChanges,

    /// <summary>
    /// Changed here and in the registry: edited here since the version it last synced, while the registry moved past
    /// that version (another machine pushed), or the registry refused this exact content as stale. Never overwritten
    /// without <c>--force</c>; the user picks which to keep.
    /// </summary>
    ChangedBoth,

    /// <summary>Installed here but not in the registry.</summary>
    LocalOnly,

    /// <summary>
    /// Cannot be pulled safely: a folder with the skill's name exists here but is not this skill, or the
    /// registry holds two skills whose names differ only by case. See <see cref="SkillComparison.Note"/>.
    /// </summary>
    Conflict,

    /// <summary>
    /// A project skill of a project other than the one this folder is in. Never compared or written here; see
    /// <see cref="SkillComparison.Note"/> for which project and where it would go.
    /// </summary>
    OtherProject,
}

/// <summary>One skill name in one harness, compared with the registry.</summary>
/// <param name="Harness">The harness the local side belongs to.</param>
/// <param name="Name">The skill name.</param>
/// <param name="State">How the two sides compare.</param>
/// <param name="Remote">The registry's skill; <c>null</c> for <see cref="SkillSyncState.LocalOnly"/>.</param>
/// <param name="LocalPath">The installed skill directory; <c>null</c> when not installed.</param>
/// <param name="LocalVersion">The registry version this machine last synced, from the ledger; <c>null</c> when unknown.</param>
/// <param name="Note">Why it is a <see cref="SkillSyncState.Conflict"/> or whose it is for <see cref="SkillSyncState.OtherProject"/>; <c>null</c> otherwise.</param>
/// <param name="Project">The project's display name for a project skill; <c>null</c> for a global skill.</param>
internal sealed record SkillComparison(
    IHarness Harness,
    string Name,
    SkillSyncState State,
    RegistrySkill? Remote,
    string? LocalPath,
    int? LocalVersion,
    string? Note = null,
    string? Project = null);

/// <summary>The result of comparing the registry with this machine.</summary>
/// <param name="Version">The version asked for with <c>--version</c>; <c>null</c> when comparing with current versions.</param>
internal sealed record SkillRemoteResult(
    SkillPullRunFailure Failure,
    string? Detail,
    Uri? Registry,
    IReadOnlyList<SkillComparison> Skills,
    int? Version = null)
{
    public static SkillRemoteResult Fail(SkillPullRunFailure failure, string? detail, Uri? registry = null) =>
        new(failure, detail, registry, []);
}

/// <summary>What a pull did with one skill.</summary>
internal enum SkillPullStatus
{
    /// <summary>The skill was not here and is now installed.</summary>
    Installed,

    /// <summary>An older copy was replaced with the registry's current version.</summary>
    Updated,

    /// <summary>Already identical to the registry; nothing was written.</summary>
    UpToDate,

    /// <summary>Left alone because it has local changes; rerun with <c>--force</c> to overwrite them.</summary>
    LocalChanges,

    /// <summary>Left alone because it changed here and in the registry (<see cref="SkillSyncState.ChangedBoth"/>); <c>--force</c> keeps the registry's.</summary>
    ChangedBoth,

    /// <summary>The download or the write failed; see the message.</summary>
    Failed,

    /// <summary>Not written, even with <c>--force</c>, because it would land on something that is not this skill; see the message.</summary>
    Conflict,

    /// <summary>Not written: it belongs to a project other than this folder's. The message says which, and where it would go.</summary>
    OtherProject,
}

/// <summary>One skill a pull handled.</summary>
/// <param name="Harness">The harness it was installed into.</param>
/// <param name="Name">The skill name.</param>
/// <param name="Status">What happened.</param>
/// <param name="Version">The registry version now installed (or that would have been).</param>
/// <param name="Path">The skill directory.</param>
/// <param name="Message">Why it failed, or a note (such as local changes overwritten); <c>null</c> otherwise.</param>
/// <param name="Project">The project's display name for a project skill; <c>null</c> for a global skill.</param>
internal sealed record SkillPullEntry(IHarness Harness, string Name, SkillPullStatus Status, int Version, string Path, string? Message, string? Project = null);

/// <summary>The result of a pull run.</summary>
/// <param name="Version">The version asked for with <c>--version</c>; <c>null</c> for current versions.</param>
internal sealed record SkillPullRunResult(
    SkillPullRunFailure Failure,
    string? Detail,
    Uri? Registry,
    IReadOnlyList<SkillPullEntry> Entries,
    int? Version = null)
{
    /// <summary>
    /// True when the run went ahead and no skill failed or conflicted. Skills left alone for local changes
    /// do not count as failures, since <c>--force</c> is the documented way past them; a conflict has no
    /// flag that resolves it, so it needs the user's attention.
    /// </summary>
    public bool Succeeded => Failure == SkillPullRunFailure.None && Entries.All(e => e.Status is not (SkillPullStatus.Failed or SkillPullStatus.Conflict));

    public static SkillPullRunResult Fail(SkillPullRunFailure failure, string? detail, Uri? registry = null) =>
        new(failure, detail, registry, []);
}
