using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>Brings ledger records up to date when a link or unlink has moved their skill to another project.</summary>
/// <remarks>
/// <para>
/// A record names the registry skill (name and project) its copy was synced as, and a moved skill keeps its name,
/// versions and content but changes project. Until the record follows, rename, delete and remote do not see the copy
/// as the moved skill's. The machine that ran the link knows what moved (<see cref="Follow"/>). Every other machine
/// works it out on its next <c>skills remote</c> or <c>pull</c> (<see cref="FollowAsync"/>).
/// </para>
/// <para>
/// Following a move changes only the record's project. Its content hash and version stay those of the last sync, so
/// <see cref="SkillPullService.Classify"/> still compares the copy with what was actually synced, and a copy edited
/// since then still shows local changes.
/// </para>
/// </remarks>
internal static class MovedSkillLedger
{
    /// <summary>Points this machine's records of the skills <paramref name="moved"/> out of <paramref name="from"/> at <paramref name="to"/>.</summary>
    /// <remarks>
    /// For the machine that ran the link: the registry has just said which skills moved, by exact name, and the skill
    /// <c>(name, from)</c> no longer exists, so every record of it on this machine, in any checkout, follows.
    /// </remarks>
    public static void Follow(ISkillLedger ledger, Uri registry, IReadOnlyList<string> moved, Guid from, Guid to)
    {
        if (from == to)
            return;

        foreach (var (path, recorded) in ledger.Entries(registry).ToList())
        {
            if (recorded.ProjectId == from && recorded.Name is { } name && moved.Contains(name, StringComparer.Ordinal))
                ledger.Record(registry, path, recorded with { ProjectId = to });
        }
    }

    /// <summary>
    /// <paramref name="recorded"/>, the record of the copy at <paramref name="path"/>, pointed at <paramref name="remote"/>'s
    /// project and saved, when it is a record of the same skill from before a move; otherwise <paramref name="recorded"/> unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For machines that did not run the link. The record follows only when all of these hold: it names the remote
    /// skill's exact name under another project P; <paramref name="skills"/> (the registry's whole list) has no skill
    /// of that name under P any more; and the remote skill's version that the record names has exactly the content the
    /// record hashed. A name match alone is never enough: the content at that version is what shows the remote skill
    /// is the same version history the copy was synced from, carried to another project.
    /// </para>
    /// <para>
    /// Costs one download, and only for a record that names a project the skill is no longer in. A record from before
    /// projects or versions were kept (no project, or no version) is never followed: it cannot be shown to be the
    /// same skill. A failed download leaves the record as it was.
    /// </para>
    /// </remarks>
    public static async Task<SkillLedgerEntry?> FollowAsync(
        IRegistryApi api, ISkillLedger ledger, Uri registry, string accessToken, string path, SkillLedgerEntry? recorded,
        RegistrySkill remote, IReadOnlyList<RegistrySkill> skills, CancellationToken cancellationToken)
    {
        if (recorded is not { ProjectId: { } from, Version: { } version } ||
            remote.ProjectId is not { } to || from == to ||
            !string.Equals(recorded.Name, remote.Name, StringComparison.Ordinal) ||
            version > remote.Version ||
            skills.Any(s => s.ProjectId == from && string.Equals(s.Name, remote.Name, StringComparison.Ordinal)))
        {
            return recorded;
        }

        var download = await api.GetSkillFilesAsync(registry, accessToken, remote.Name, remote.ProjectId, version, cancellationToken);
        if (download.Content is not { } content || content.Version != version)
            return recorded;

        var files = content.Files
            .Select(f => new SkillUploadFile(f.RelativePath, f.Content))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
        if (SkillFileReader.Hash(files) != recorded.ContentHash)
            return recorded;

        var followed = recorded with { ProjectId = remote.ProjectId };
        ledger.Record(registry, path, followed);
        return followed;
    }
}
