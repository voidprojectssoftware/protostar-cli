using Protostar.Cli.Harness;

namespace Protostar.Cli.Skills;

/// <summary>Why a skill query produced no results, so a caller can render the right message.</summary>
internal enum SkillQueryFailure
{
    /// <summary>The query ran; <see cref="SkillDiscoveryResult.Skills"/> is authoritative (possibly empty).</summary>
    None,

    /// <summary>A <c>--harness</c> id was given that no registered harness matches.</summary>
    UnknownHarness,

    /// <summary>The named harness exists but does not implement skill discovery.</summary>
    Unsupported,
}

/// <summary>
/// Outcome of a skill query. Either a success carrying the discovered skills (already ordered for
/// display) or a typed <see cref="Failure"/> with the offending harness id. Deliberately free of any
/// presentation concern: the caller turns this into a table, an error line, or whatever it needs.
/// </summary>
internal sealed record SkillDiscoveryResult(
    IReadOnlyList<DiscoveredSkill> Skills,
    SkillQueryFailure Failure,
    string? OffendingHarnessId);

/// <summary>
/// The read side of "push a local skill": resolve which harnesses to ask, fan out their skill
/// discovery, and return the merged, ordered set. This is the reusable logic seam shared by the
/// <c>skills</c> listing command and (later) skill sync, so neither re-implements harness resolution
/// or ordering. The harness capability (<see cref="ISkillCapability"/>) is the data source; this is
/// the orchestration over it. Returns data only, never console markup.
/// </summary>
internal sealed class SkillService : ISkillService
{
    private readonly IHarnessCatalog _catalog;

    public SkillService(IHarnessCatalog catalog) => _catalog = catalog;

    /// <inheritdoc />
    public SkillDiscoveryResult Discover(string? harnessId, string? harnessHome, string? projectStart)
    {
        var harnesses = ResolveHarnesses(harnessId, out var failure);
        if (failure != SkillQueryFailure.None)
            return new SkillDiscoveryResult([], failure, harnessId);

        var skills = harnesses
            .SelectMany(harness => DiscoverSkills(harness, harnessHome, projectStart))
            .OrderBy(s => s.Scope)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SkillDiscoveryResult(skills, SkillQueryFailure.None, null);
    }

    // Skills from one harness, or none if it isn't installed. The out-parameter TryLocate doesn't fold
    // into a query, so it lives here and lets the caller stay a single SelectMany.
    private static IEnumerable<DiscoveredSkill> DiscoverSkills(IHarness harness, string? harnessHome, string? projectStart)
    {
        if (harness.TryLocate(harnessHome, out var location) && harness is ISkillCapability skillHarness)
            return skillHarness.DiscoverSkills(location, projectStart);

        return [];
    }

    // Either the one harness the operator named, or every registered harness that supports skills.
    private IReadOnlyList<IHarness> ResolveHarnesses(string? id, out SkillQueryFailure failure)
    {
        failure = SkillQueryFailure.None;
        if (id is null)
            return _catalog.All.Where(h => h is ISkillCapability).ToList();

        var harness = _catalog.ById(id);
        if (harness is null)
        {
            failure = SkillQueryFailure.UnknownHarness;
            return [];
        }
        if (harness is not ISkillCapability)
        {
            failure = SkillQueryFailure.Unsupported;
            return [];
        }
        return [harness];
    }
}
