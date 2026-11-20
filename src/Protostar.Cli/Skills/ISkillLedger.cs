namespace Protostar.Cli.Skills;

/// <summary>
/// Remembers, per registry, the content of each local skill that last reached that registry.
/// </summary>
/// <remarks>
/// Lets a push decide that a skill is unchanged without contacting the registry, which is what keeps the
/// capture hook free on every ordinary skill use. Entries are keyed by the skill's directory, not its
/// name, so a global and a project skill sharing a name never overwrite each other's record, and two clones
/// of one project keep a record each. The entry names the registry skill (name and project) it was synced as.
/// </remarks>
internal interface ISkillLedger
{
    /// <summary>The last recorded push of the skill at <paramref name="skillPath"/>, or <c>null</c> if none.</summary>
    SkillLedgerEntry? Get(Uri registry, string skillPath);

    /// <summary>Records that the skill at <paramref name="skillPath"/> is in the registry with this content.</summary>
    /// <remarks>Never throws: a ledger that cannot be written only costs a redundant upload next time.</remarks>
    void Record(Uri registry, string skillPath, SkillLedgerEntry entry);

    /// <summary>Every record for <paramref name="registry"/>, keyed by skill directory (full path).</summary>
    /// <remarks>How a rename or delete finds this machine's copies of one registry skill, in any harness or checkout.</remarks>
    IReadOnlyDictionary<string, SkillLedgerEntry> Entries(Uri registry);

    /// <summary>Drops the record for the skill at <paramref name="skillPath"/>, when its folder has moved.</summary>
    /// <remarks>Never throws, like <see cref="Record"/>.</remarks>
    void Forget(Uri registry, string skillPath);
}

/// <summary>What was last pushed or pulled for one skill, and any push the registry refused since.</summary>
/// <param name="ContentHash">
/// The lowercase hex SHA-256 from <see cref="SkillFileReader.Hash"/>; empty in a record made only to remember a
/// refusal for a copy that never synced.
/// </param>
/// <param name="Version">
/// The registry's version number for that content, which the next push names as its base; 0 for a copy that never
/// synced a version; <c>null</c> in records written before versions were kept, which push with no base.
/// </param>
/// <param name="Name">
/// The registry skill name that content was synced as, exactly; <c>null</c> in records written before
/// names were kept. A pull trusts a record only for the registry skill it names, so a folder synced as
/// <c>foo</c> is never treated as an unedited copy of <c>Foo</c>.
/// </param>
/// <param name="ProjectId">
/// The registry project that content was synced under; <c>null</c> for a global skill, and in every record
/// written before project skills, when every skill was pushed as global. A record speaks only for the registry
/// skill with this name and this project.
/// </param>
/// <param name="RefusedHash">
/// The content of this copy the registry last refused as stale (<see cref="Registry.SkillPushStatus.Stale"/>): it was
/// changed here and in the registry since <paramref name="Version"/>. <c>null</c> when no push was refused since the
/// last sync. The capture hook does not send that same content again.
/// </param>
/// <param name="RefusedVersion">The registry's current version when it refused <paramref name="RefusedHash"/>.</param>
internal sealed record SkillLedgerEntry(
    string ContentHash, int? Version, string? Name = null, Guid? ProjectId = null, string? RefusedHash = null, int? RefusedVersion = null);
