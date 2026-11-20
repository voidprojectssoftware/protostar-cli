using Protostar.Cli.Projects;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>Renames or deletes a skill in the registry: <c>protostar skills rename</c> and <c>skills delete</c>.</summary>
/// <remarks>
/// <para>
/// Finding the skill is a separate step from changing it, so the command can show what it found (and ask before a
/// delete) before anything happens. A skill is found the way a pull scopes it: the project skill of the folder's
/// project when there is one with that exact name, otherwise the global skill of that name; with no folder
/// (<c>--global-only</c>) only the global skill.
/// </para>
/// <para>
/// This machine's copies of the skill are the folders the ledger records as synced with it, in any harness or
/// checkout. A delete never touches them. A rename brings them along so they keep matching the registry skill:
/// see <see cref="RenameAsync"/>. Returns data only; rendering belongs to <see cref="SkillChangePresenter"/>.
/// </para>
/// </remarks>
internal interface ISkillChangeService
{
    /// <summary>Finds the registry skill <see cref="SkillTargetRequest.Name"/> names, and this machine's copies of it.</summary>
    Task<SkillTargetResult> FindAsync(SkillTargetRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes <paramref name="target"/>'s registry skill, with every version. Local files are never touched.</summary>
    /// <remarks>
    /// The ledger records of the local copies are kept, so the capture hook still sees an unchanged copy as already
    /// backed up and does not push it straight back. An edited copy is pushed again, as a new skill.
    /// </remarks>
    Task<SkillDeleteResult> DeleteAsync(SkillTarget target, CancellationToken cancellationToken);

    /// <summary>Renames <paramref name="target"/>'s registry skill to <paramref name="newName"/>, then brings this machine's copies along.</summary>
    /// <remarks>
    /// <para>
    /// A local skill is named by its SKILL.md <c>name</c>, falling back to its folder name, and is matched with the
    /// registry by that name, so a copy left alone would show as "only here" beside a "not installed" registry skill.
    /// Each copy therefore has its SKILL.md <c>name</c> line rewritten, its folder renamed when the folder carried the
    /// old name and the new one is free, and its ledger record moved to the new name and path.
    /// </para>
    /// <para>
    /// The registry rename changes only the name, so the current version's SKILL.md still says the old one, and every
    /// later pull would install a folder whose skill calls itself by the old name. So when that SKILL.md declares a
    /// name, the current version is downloaded, its <c>name</c> line set to the new name, and pushed as the next
    /// version. History is untouched: the earlier versions keep the name they were pushed with.
    /// </para>
    /// <para>
    /// An unedited copy that matched the registry then matches the new version (the same one-line edit) and is
    /// recorded as up to date; one that was behind is recorded with its rewritten content, so an update is still
    /// available. A copy with unpushed changes keeps its old record, so its changes stay protected and are pushed under
    /// the new name by the next push.
    /// </para>
    /// </remarks>
    Task<SkillRenameResult> RenameAsync(SkillTarget target, string newName, CancellationToken cancellationToken);
}

/// <summary>Which skill to find.</summary>
/// <param name="Name">The registry skill's exact name.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="ProjectStart">The folder whose project's skill is meant; <c>null</c> means the global skill only.</param>
internal sealed record SkillTargetRequest(string Name, string? Registry, string? ProjectStart);

/// <summary>A registry skill to rename or delete, with what is needed to change it.</summary>
/// <param name="Registry">The registry it is in.</param>
/// <param name="AccessToken">The session's bearer token.</param>
/// <param name="Skill">The registry skill.</param>
/// <param name="Copies">This machine's copies of it, from the ledger; empty when none.</param>
/// <param name="ProjectFacts">The folder's facts when <paramref name="Skill"/> is a project skill, for the push a rename makes; <c>null</c> for a global skill.</param>
internal sealed record SkillTarget(Uri Registry, string AccessToken, RegistrySkill Skill, IReadOnlyList<LocalSkillCopy> Copies, ProjectFacts? ProjectFacts);

/// <summary>One of this machine's copies of a registry skill.</summary>
/// <param name="Path">The skill folder.</param>
/// <param name="Recorded">What the ledger says was last synced there.</param>
/// <param name="HasLocalChanges">True when the files no longer match that record (edited here and not pushed), or cannot be read.</param>
internal sealed record LocalSkillCopy(string Path, SkillLedgerEntry Recorded, bool HasLocalChanges);

/// <summary>The skill found, or why not.</summary>
/// <param name="Failure">Why the lookup stopped; <see cref="SkillPullRunFailure.SkillNotFound"/> when there is no such skill.</param>
/// <param name="Detail">The failure's detail; for a missing skill, the full explanation.</param>
/// <param name="Registry">The registry asked, when it could be resolved.</param>
/// <param name="Target">The skill; <c>null</c> on failure.</param>
internal sealed record SkillTargetResult(SkillPullRunFailure Failure, string? Detail, Uri? Registry, SkillTarget? Target)
{
    public static SkillTargetResult Fail(SkillPullRunFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, null);
}

/// <summary>A delete's result.</summary>
/// <param name="Target">What was deleted (or not).</param>
/// <param name="Error">Why the registry refused; <c>null</c> when it is gone.</param>
internal sealed record SkillDeleteResult(SkillTarget Target, string? Error);

/// <summary>A rename's result.</summary>
/// <param name="Target">What was renamed, as it was before.</param>
/// <param name="NewName">The name it has now; <c>null</c> when the registry refused.</param>
/// <param name="Error">Why the registry refused (for example, the name is taken); <c>null</c> on success.</param>
/// <param name="Copies">What happened to each local copy; empty when the registry refused.</param>
/// <param name="Push">The push of the current version with the renamed SKILL.md; <c>null</c> when its SKILL.md declares no name, so none was needed.</param>
internal sealed record SkillRenameResult(
    SkillTarget Target, string? NewName, string? Error, IReadOnlyList<LocalCopyRename> Copies, SkillPushOutcome? Push = null);

/// <summary>What a rename did to one local copy.</summary>
/// <param name="OldPath">Where it was.</param>
/// <param name="Path">Where it is now (the same when the folder was not renamed).</param>
/// <param name="ManifestRenamed">True when its SKILL.md <c>name</c> line was rewritten.</param>
/// <param name="Note">Why something was left as it was (folder not renamed, unpushed changes); <c>null</c> when all went to plan.</param>
internal sealed record LocalCopyRename(string OldPath, string Path, bool ManifestRenamed, string? Note);
