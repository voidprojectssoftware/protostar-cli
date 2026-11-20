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
        return $"Last synced as {from}'s skill, which this folder is no longer in (it was linked there when this copy was pulled), but {reason}, so it now belongs to this folder's project";
    }
}
