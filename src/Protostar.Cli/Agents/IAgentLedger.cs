namespace Protostar.Cli.Agents;

/// <summary>Remembers, per registry, the content of each local agent file that last reached that registry.</summary>
/// <remarks>
/// The agents' copy of <see cref="Skills.ISkillLedger"/> (ADR 0003): it lets a push call an agent unchanged without asking the
/// registry, which keeps the capture hook free when nothing changed. Records are keyed by the file's full path, so a
/// personal and a project agent of one name, or two clones of a project, keep a record each.
/// </remarks>
internal interface IAgentLedger
{
    /// <summary>The record of the agent file at <paramref name="filePath"/>, or <c>null</c>.</summary>
    AgentLedgerEntry? Get(Uri registry, string filePath);

    /// <summary>Records that the agent file at <paramref name="filePath"/> is in the registry with this content. Never throws.</summary>
    void Record(Uri registry, string filePath, AgentLedgerEntry entry);

    /// <summary>Every record for <paramref name="registry"/>, keyed by full file path.</summary>
    IReadOnlyDictionary<string, AgentLedgerEntry> Entries(Uri registry);
}

/// <summary>What was last pushed for one agent file, and any push the registry refused since.</summary>
/// <param name="ContentHash">The lowercase hex SHA-256 of the file's bytes; empty in a record kept only for a refusal.</param>
/// <param name="Version">The registry version for that content, which the next push names as its base; 0 when it never synced one.</param>
/// <param name="Name">The registry agent name the content was synced as, exactly.</param>
/// <param name="ProjectId">The registry project it was synced under; <c>null</c> for a personal agent.</param>
/// <param name="RefusedHash">Content the registry last refused (stale, or a copy of a removed agent); the hook does not send it again.</param>
/// <param name="RefusedVersion">The registry's current version at a stale refusal, or the removed agent's last version.</param>
/// <param name="RefusedAsRemoved">True when the refusal was because the agent was deleted or renamed, not stale.</param>
/// <param name="RefusedRenamedTo">For a refusal because of a rename, the agent's name in the registry then.</param>
internal sealed record AgentLedgerEntry(
    string ContentHash, int Version, string Name, Guid? ProjectId, string? RefusedHash = null, int? RefusedVersion = null,
    bool RefusedAsRemoved = false, string? RefusedRenamedTo = null);
