namespace Protostar.Cli.Harness;

/// <summary>
/// Capability: a harness protostar can scan for authored skills, in two scopes —
/// <see cref="SkillScope.Global"/> (per-user config dir) and <see cref="SkillScope.Project"/> (a
/// working tree). This is the read side of "push a local skill" (PROT-11). A harness without a skill
/// convention simply does not implement it.
/// </summary>
internal interface ISkillCapability
{
    /// <summary>
    /// Enumerate global skills under <paramref name="location"/> plus, when
    /// <paramref name="projectStart"/> is given, the local skills of the project containing it (the
    /// provider locates its own project root from that path). Pass <c>null</c> to skip the project
    /// scope. A project skill sharing a global skill's name is reported separately, tagged
    /// <see cref="SkillScope.Project"/>; precedence is the caller's call, not discovery's.
    /// </summary>
    IReadOnlyList<DiscoveredSkill> DiscoverSkills(HarnessLocation location, string? projectStart);
}
