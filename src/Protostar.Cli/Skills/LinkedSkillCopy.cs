using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>Whose skill a project skill copy is when its ledger record names a project other than its folder's.</summary>
/// <remarks>
/// <para>
/// A copy pulled while its folder was linked to another project is recorded under that project, and an unlink does not
/// move it: only skills pushed from the folder go with it (registry#43). Afterwards the record and the folder disagree
/// about the copy's project, and <c>skills remote</c>, <c>pull</c> and <c>push</c> must all settle it the same way
/// (cli#95), so they all ask <see cref="StillOf"/>.
/// </para>
/// <para>
/// The copy is still the other project's skill while it is unedited since it synced, that skill is still in the registry
/// (the same skill, not a new one that reused the name), and the folder's own project has no skill of that name: an unlink
/// should not fork a skill nobody touched. Remote then compares it with that skill, pull updates it from there, and push
/// reports it unchanged. Otherwise it is the folder's project's skill, never synced there: remote lists it as only here,
/// and push backs it up as a new skill of the folder's project, where the registry's stale and tombstone rules apply as to
/// any new skill. A copy of a skill removed from the registry is neither: both commands report the removal first.
/// </para>
/// </remarks>
internal static class LinkedSkillCopy
{
    /// <summary>Whether <paramref name="recorded"/> names a project, and not <paramref name="folderProject"/> (<c>null</c>: the folder has no project yet).</summary>
    public static bool NamesOtherProject(SkillLedgerEntry? recorded, Guid? folderProject) =>
        recorded?.ProjectId is { } synced && synced != folderProject;

    /// <summary>
    /// The other project's registry skill the copy hashing to <paramref name="hash"/> still belongs to; <c>null</c> when it
    /// belongs to its folder's project, <paramref name="folderProject"/>, instead (or the record names no other project).
    /// </summary>
    /// <param name="skills">The registry's whole skill list.</param>
    public static RegistrySkill? StillOf(SkillLedgerEntry? recorded, string? hash, Guid? folderProject, IReadOnlyList<RegistrySkill> skills)
    {
        if (!NamesOtherProject(recorded, folderProject) || recorded is not { Name: { } name, Version: { } version } || hash != recorded.ContentHash)
            return null;
        if (folderProject is not null && skills.Any(s => s.ProjectId == folderProject && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return null;
        return skills.FirstOrDefault(s => s.ProjectId == recorded.ProjectId && string.Equals(s.Name, name, StringComparison.Ordinal) &&
                                          version >= s.FirstVersion && version <= s.Version);
    }

    /// <summary>What remote and push both say about a copy that <see cref="StillOf"/> found still belongs to <paramref name="owner"/>.</summary>
    public static string StillNote(RegistrySkill owner) =>
        $"Still project {owner.ProjectName}'s skill: pulled while this folder was linked to it, and unchanged here since. " +
        "If you change it here, `protostar skills push` backs it up as this folder's project's skill instead.";

    /// <summary>Why a copy whose record names another project now belongs to its folder's project, as remote and push both say it.</summary>
    public static string WhyNotStill(SkillLedgerEntry recorded, string? hash, IReadOnlyList<RegistrySkill> skills)
    {
        var owner = skills.FirstOrDefault(s => s.ProjectId == recorded.ProjectId && string.Equals(s.Name, recorded.Name, StringComparison.Ordinal));
        var from = owner?.ProjectName is { } project ? $"project {project}" : "another project";
        var reason = hash != recorded.ContentHash ? "it has changed here since"
            : owner is null ? "that project no longer has it"
            : $"this folder's project has its own '{recorded.Name}'";
        return $"Last synced as {from}'s skill, which this folder is no longer in (it was in that project when this copy last synced), but {reason}, so it now belongs to this folder's project";
    }

    /// <summary>What <see cref="StillOf"/> and <see cref="WhyNotStill"/> settle for a copy left behind by a link or unlink, as one value.</summary>
    /// <param name="list">The registry's skill list; a failed list gives <see cref="LeftSkillFate.Unknown"/>.</param>
    /// <remarks>
    /// A copy of a skill removed from the registry is <see cref="LeftSkillFate.Removed"/> first, as push and remote report it.
    /// A folder project's own skill of the name comes before an edit here: the next backup is then refused as changed in both
    /// places rather than filed as a new skill.
    /// </remarks>
    public static LeftSkillFate FateOf(SkillLedgerEntry recorded, string? hash, Guid? folderProject, RegistrySkillList list)
    {
        if (list.Skills is not { } skills || recorded.Name is not { } name)
            return LeftSkillFate.Unknown;
        if (list.RemovalOf(name, recorded.ProjectId, recorded.Version) is not null)
            return LeftSkillFate.Removed;
        if (StillOf(recorded, hash, folderProject, skills) is not null)
            return LeftSkillFate.StillLinked;
        if (folderProject is not null && skills.Any(s => s.ProjectId == folderProject && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return LeftSkillFate.FolderHasOwn;
        return hash != recorded.ContentHash ? LeftSkillFate.ChangedHere : LeftSkillFate.GoneFromProject;
    }
}

/// <summary>What becomes of a project skill copy a link or unlink leaves in its folder (<see cref="LinkedSkillCopy"/>).</summary>
internal enum LeftSkillFate
{
    /// <summary>Unedited, so still the other project's skill: pull keeps updating it from there; an edit would make it the folder's project's.</summary>
    StillLinked,

    /// <summary>Changed here since it synced, so the folder's project's: the next backup files it there as a new skill.</summary>
    ChangedHere,

    /// <summary>The folder's project has its own skill of that name, so the copy counts as that one's.</summary>
    FolderHasOwn,

    /// <summary>The other project no longer has it (and the registry has no memory of removing it), so the next backup files it in the folder's project.</summary>
    GoneFromProject,

    /// <summary>Deleted or renamed in the registry since it synced: push refuses it and remote says what to do.</summary>
    Removed,

    /// <summary>The registry could not list its skills, so it was not worked out.</summary>
    Unknown,
}

/// <summary>A project skill copy a link or unlink left in its folder, and what becomes of it.</summary>
/// <param name="Name">The skill's name.</param>
/// <param name="Fate">What becomes of it.</param>
internal sealed record SkillLeftBehind(string Name, LeftSkillFate Fate);
