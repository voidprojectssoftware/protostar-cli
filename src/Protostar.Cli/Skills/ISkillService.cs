namespace Protostar.Cli.Skills;

/// <summary>
/// The read side of "push a local skill": resolves which harnesses to ask, fans out their skill
/// discovery, and returns the merged, ordered set. Injected into commands so the discovery logic can
/// be faked in tests. See <see cref="SkillService"/> for the production implementation.
/// </summary>
internal interface ISkillService
{
    /// <summary>
    /// Discover skills across the selected harness(es). <paramref name="harnessId"/> limits the query
    /// to one harness (null = every harness that supports discovery); <paramref name="harnessHome"/>
    /// overrides the config root; <paramref name="projectStart"/> is where to begin the project-scope
    /// walk-up (null skips project scope). Results are ordered by scope then name for stable display.
    /// </summary>
    SkillDiscoveryResult Discover(string? harnessId, string? harnessHome, string? projectStart);
}
