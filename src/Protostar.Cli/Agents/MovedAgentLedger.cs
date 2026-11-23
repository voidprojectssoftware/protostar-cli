using Protostar.Cli.Registry;

namespace Protostar.Cli.Agents;

/// <summary>Brings agent ledger records up to date when a link or unlink has moved their agents to another project.</summary>
/// <remarks>
/// The agents' copy of <see cref="Skills.MovedSkillLedger.Follow"/>: the machine that ran the link knows which agents moved
/// (the registry names them in <c>movedAgents</c>), so every record of them on this machine follows to the new project. Only
/// the project changes; the hash and version stay those of the last sync. Other machines' records follow on their next
/// push, remote or pull (<see cref="FollowAsync"/>).
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

    /// <summary>
    /// <paramref name="recorded"/>, the record of the copy at <paramref name="path"/>, pointed at <paramref name="remote"/>'s
    /// project and saved, when it is a record of the same agent from before a move; otherwise <paramref name="recorded"/> unchanged.
    /// </summary>
    /// <remarks>
    /// The agents' copy of <see cref="Skills.MovedSkillLedger.FollowAsync"/>, for machines that did not run the link. The
    /// record follows only when it names the remote agent's exact name under another project P, <paramref name="agents"/>
    /// (the registry's whole list) has no agent of that name under P any more, and the remote agent's version that the
    /// record names has exactly the content the record hashed. When that version is the current one the list's hash
    /// answers; otherwise it costs one download of that version, and a failed download leaves the record as it was.
    /// </remarks>
    public static async Task<AgentLedgerEntry?> FollowAsync(
        IAgentApi api, IAgentLedger ledger, Uri registry, string accessToken, string path, AgentLedgerEntry? recorded,
        RegistryAgent remote, IReadOnlyList<RegistryAgent> agents, CancellationToken cancellationToken)
    {
        if (recorded is not { ProjectId: { } from, Version: >= 1 and var version } ||
            remote.ProjectId is not { } to || from == to ||
            !string.Equals(recorded.Name, remote.Name, StringComparison.Ordinal) ||
            version > remote.Version ||
            agents.Any(a => a.ProjectId == from && string.Equals(a.Name, remote.Name, StringComparison.Ordinal)))
        {
            return recorded;
        }

        var hash = remote.ContentHash;
        if (version != remote.Version)
        {
            var download = await api.GetAgentContentAsync(registry, accessToken, remote.Name, to, version, cancellationToken);
            if (download.Value is not { } content || content.Version != version)
                return recorded;
            hash = content.ContentHash;
        }

        if (!string.Equals(hash, recorded.ContentHash, StringComparison.OrdinalIgnoreCase))
            return recorded;

        var followed = recorded with { ProjectId = to };
        ledger.Record(registry, path, followed);
        return followed;
    }
}
