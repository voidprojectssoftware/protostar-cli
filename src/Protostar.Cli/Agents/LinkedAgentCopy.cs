using Protostar.Cli.Registry;

namespace Protostar.Cli.Agents;

/// <summary>Whose agent a project agent copy is when its ledger record names a project other than its folder's.</summary>
/// <remarks>
/// <para>
/// The agents' copy of <see cref="Skills.LinkedSkillCopy"/> (ADR 0003: copy the rule, do not share it; a fix to one copy must
/// be checked against the other). A copy pulled while its folder was linked to another project is recorded under that
/// project, and an unlink does not move it: only agents pushed from the folder go with it. Afterwards the record and the
/// folder disagree about the copy's project, and <c>agents remote</c>, <c>pull</c> and <c>push</c> must all settle it the
/// same way, so they all ask <see cref="StillOf"/>.
/// </para>
/// <para>
/// The copy is still the other project's agent while it is unedited since it synced, that agent is still in the registry
/// (the same agent, not a new one that reused the name), and the folder's own project has no agent of that name. Remote then
/// compares it with that agent, pull updates it from there, and push reports it unchanged. Otherwise it is the folder's
/// project's agent, never synced there: remote lists it as only here, and push backs it up as a new agent of the folder's
/// project. A copy of an agent removed from the registry is neither: both commands report the removal first.
/// </para>
/// </remarks>
internal static class LinkedAgentCopy
{
    /// <summary>Whether <paramref name="recorded"/> names a project, and not <paramref name="folderProject"/> (<c>null</c>: the folder has no project yet).</summary>
    public static bool NamesOtherProject(AgentLedgerEntry? recorded, Guid? folderProject) =>
        recorded is { ProjectId: { } synced, ContentHash.Length: > 0 } && synced != folderProject;

    /// <summary>
    /// The other project's registry agent the copy hashing to <paramref name="hash"/> still belongs to; <c>null</c> when it
    /// belongs to its folder's project, <paramref name="folderProject"/>, instead (or the record names no other project).
    /// </summary>
    /// <param name="agents">The registry's whole agent list.</param>
    public static RegistryAgent? StillOf(AgentLedgerEntry? recorded, string? hash, Guid? folderProject, IReadOnlyList<RegistryAgent> agents)
    {
        if (!NamesOtherProject(recorded, folderProject) || hash != recorded!.ContentHash)
            return null;
        if (FolderHasOwn(recorded.Name, folderProject, agents))
            return null;
        return agents.FirstOrDefault(a => a.ProjectId == recorded.ProjectId && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal) &&
                                          recorded.Version >= a.FirstVersion && recorded.Version <= a.Version);
    }

    /// <summary>What remote and push both say about a copy that <see cref="StillOf"/> found still belongs to <paramref name="owner"/>.</summary>
    public static string StillNote(RegistryAgent owner) =>
        $"Still project {owner.ProjectName}'s agent: pulled while this folder was linked to it, and unchanged here since. " +
        "If you change it here, `protostar agents push` backs it up as this folder's project's agent instead.";

    /// <summary>Why a copy whose record names another project now belongs to its folder's project, as remote says it.</summary>
    public static string WhyNotStill(AgentLedgerEntry recorded, string? hash, IReadOnlyList<RegistryAgent> agents)
    {
        var owner = agents.FirstOrDefault(a => a.ProjectId == recorded.ProjectId && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal));
        var from = owner?.ProjectName is { } project ? $"project {project}" : "another project";
        var reason = hash != recorded.ContentHash ? "it has changed here since"
            : owner is null ? "that project no longer has it"
            : $"this folder's project has its own '{recorded.Name}'";
        return $"Last synced as {from}'s agent, which this folder is no longer in (it was in that project when this copy last synced), but {reason}, so it now belongs to this folder's project";
    }

    /// <summary>What <see cref="StillOf"/> settles for a copy left behind by a link or unlink, as one value.</summary>
    /// <param name="list">The registry's agent list; a failed list gives <see cref="LeftAgentFate.Unknown"/>.</param>
    /// <remarks>
    /// A copy of an agent removed from the registry is <see cref="LeftAgentFate.Removed"/> first, as push and remote report it.
    /// A folder project's own agent of the name comes before an edit here: the next backup is then refused as changed in both
    /// places rather than filed as a new agent.
    /// </remarks>
    public static LeftAgentFate FateOf(AgentLedgerEntry recorded, string? hash, Guid? folderProject, RegistryAgentList list)
    {
        if (list.Agents is not { } agents)
            return LeftAgentFate.Unknown;
        if (list.RemovalOf(recorded.Name, recorded.ProjectId, recorded.Version) is not null)
            return LeftAgentFate.Removed;
        if (StillOf(recorded, hash, folderProject, agents) is not null)
            return LeftAgentFate.StillLinked;
        if (FolderHasOwn(recorded.Name, folderProject, agents))
            return LeftAgentFate.FolderHasOwn;
        return hash != recorded.ContentHash ? LeftAgentFate.ChangedHere : LeftAgentFate.GoneFromProject;
    }

    // Agents are named exactly (Claude Code's identity for one), so only an agent of exactly this name in the folder's project counts.
    private static bool FolderHasOwn(string name, Guid? folderProject, IReadOnlyList<RegistryAgent> agents) =>
        folderProject is not null && agents.Any(a => a.ProjectId == folderProject && string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>What becomes of a project agent copy a link or unlink leaves in its folder (<see cref="LinkedAgentCopy"/>).</summary>
internal enum LeftAgentFate
{
    /// <summary>Unedited, so still the other project's agent: pull keeps updating it from there; an edit would make it the folder's project's.</summary>
    StillLinked,

    /// <summary>Changed here since it synced, so the folder's project's: the next backup files it there as a new agent.</summary>
    ChangedHere,

    /// <summary>The folder's project has its own agent of that name, so the copy counts as that one's.</summary>
    FolderHasOwn,

    /// <summary>The other project no longer has it (and the registry has no memory of removing it), so the next backup files it in the folder's project.</summary>
    GoneFromProject,

    /// <summary>Deleted or renamed in the registry since it synced: push refuses it and remote says what to do.</summary>
    Removed,

    /// <summary>The registry could not list its agents, so it was not worked out.</summary>
    Unknown,
}

/// <summary>A project agent copy a link or unlink left in its folder, and what becomes of it.</summary>
/// <param name="Name">The agent's name.</param>
/// <param name="Fate">What becomes of it.</param>
internal sealed record AgentLeftBehind(string Name, LeftAgentFate Fate);
