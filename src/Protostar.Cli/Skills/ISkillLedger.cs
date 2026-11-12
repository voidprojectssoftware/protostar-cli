namespace Protostar.Cli.Skills;

/// <summary>
/// Remembers, per registry, the content of each local skill that last reached that registry.
/// </summary>
/// <remarks>
/// Lets a push decide that a skill is unchanged without contacting the registry, which is what keeps the
/// capture hook free on every ordinary skill use. Entries are keyed by the skill's directory, not its
/// name, so a global and a project skill sharing a name never overwrite each other's record.
/// </remarks>
internal interface ISkillLedger
{
    /// <summary>The last recorded push of the skill at <paramref name="skillPath"/>, or <c>null</c> if none.</summary>
    SkillLedgerEntry? Get(Uri registry, string skillPath);

    /// <summary>Records that the skill at <paramref name="skillPath"/> is in the registry with this content.</summary>
    /// <remarks>Never throws: a ledger that cannot be written only costs a redundant upload next time.</remarks>
    void Record(Uri registry, string skillPath, SkillLedgerEntry entry);
}

/// <summary>What was last pushed for one skill.</summary>
/// <param name="ContentHash">The lowercase hex SHA-256 from <see cref="SkillFileReader.Hash"/>.</param>
/// <param name="Version">The registry's version number for that content.</param>
internal sealed record SkillLedgerEntry(string ContentHash, int? Version);
