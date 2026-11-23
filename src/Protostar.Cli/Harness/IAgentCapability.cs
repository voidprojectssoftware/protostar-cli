namespace Protostar.Cli.Harness;

/// <summary>Capability: a harness whose custom agents protostar can find on disk, personal and per project (ADR 0003 in the registry).</summary>
/// <remarks>A harness without custom agents does not implement it. Claude Code is the only one today.</remarks>
internal interface IAgentCapability
{
    /// <summary>
    /// The personal agents under <paramref name="location"/> plus, when <paramref name="projectStart"/> is given, the agents
    /// of the project containing it. Never throws: an absent folder, or a start in no project, gives none.
    /// </summary>
    /// <remarks>A file whose front matter lacks <c>name</c> is still reported (with no name), so a push can say why it was refused.</remarks>
    IReadOnlyList<DiscoveredAgent> DiscoverAgents(HarnessLocation location, string? projectStart);

    /// <summary>The folder that holds the personal agents at <paramref name="location"/>; it may not exist.</summary>
    string GlobalAgentsDirectory(HarnessLocation location);

    /// <summary>The folder that holds the agents of the project folder <paramref name="projectRoot"/>; it may not exist.</summary>
    string ProjectAgentsDirectory(string projectRoot);
}
