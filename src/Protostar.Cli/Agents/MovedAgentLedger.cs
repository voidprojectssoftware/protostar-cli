namespace Protostar.Cli.Agents;

/// <summary>Brings agent ledger records up to date when a link or unlink has moved their agents to another project.</summary>
/// <remarks>
/// The agents' copy of <see cref="Skills.MovedSkillLedger.Follow"/>: the machine that ran the link knows which agents moved
/// (the registry names them in <c>movedAgents</c>), so every record of them on this machine follows to the new project. Only
/// the project changes; the hash and version stay those of the last sync. Other machines' records follow on their next
/// push, when the registry's list shows the agent under its new project with the content they synced
/// (<see cref="AgentPushService"/>).
/// </remarks>
internal static class MovedAgentLedger
{
    /// <summary>Points this machine's records of the agents <paramref name="moved"/> out of <paramref name="from"/> at <paramref name="to"/>.</summary>
    public static void Follow(IAgentLedger ledger, Uri registry, IReadOnlyList<string> moved, Guid from, Guid to)
    {
        if (from == to)
            return;

        foreach (var (path, recorded) in ledger.Entries(registry).ToList())
        {
            if (recorded.ProjectId == from && moved.Contains(recorded.Name, StringComparer.Ordinal))
                ledger.Record(registry, path, recorded with { ProjectId = to });
        }
    }
}
