using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Backs up local skills to the registry.
/// </summary>
/// <remarks>
/// Shared by <c>protostar skills push</c> and the capture hook. Returns data only; rendering belongs to
/// <see cref="SkillPushPresenter"/>.
/// </remarks>
internal interface ISkillPushService
{
    /// <summary>
    /// Discovers the requested skills and uploads each one to the registry as a new version.
    /// </summary>
    /// <remarks>
    /// Skills are sent one request each, so one refused skill never fails the others. When two
    /// discovered skills share a name, the first in discovery order (global before project) is pushed
    /// and the rest are skipped, matching which one the harness itself loads.
    /// </remarks>
    Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken);
}

/// <summary>What to push and where.</summary>
/// <param name="Names">Skill names to push (case-insensitive); empty pushes every discovered skill.</param>
/// <param name="HarnessId">Limit discovery to this harness; <c>null</c> for every harness.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="ProjectStart">Where the project-scope walk-up starts; <c>null</c> skips project skills.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment and default.</param>
internal sealed record SkillPushRequest(
    IReadOnlyList<string> Names,
    string? HarnessId,
    string? HarnessHome,
    string? ProjectStart,
    string? Registry);

/// <summary>Why a push run stopped before uploading anything.</summary>
internal enum SkillPushRunFailure
{
    /// <summary>The run went ahead; see the per-skill entries.</summary>
    None,

    /// <summary>The registry URL is not an absolute http(s) URL.</summary>
    InvalidRegistry,

    /// <summary>A <c>--harness</c> id matched no registered harness.</summary>
    UnknownHarness,

    /// <summary>The named harness does not support skill discovery.</summary>
    Unsupported,

    /// <summary>A requested skill name was not found on this machine.</summary>
    SkillNotFound,

    /// <summary>There is no stored session for the registry.</summary>
    NotLoggedIn,

    /// <summary>The stored session has expired and could not be refreshed.</summary>
    SessionExpired,

    /// <summary>The registry did not answer its metadata endpoint.</summary>
    RegistryUnreachable,

    /// <summary>The registry does not speak this CLI's API major.</summary>
    Incompatible,
}

/// <summary>One skill and what happened to it.</summary>
internal sealed record SkillPushEntry(DiscoveredSkill Skill, SkillPushOutcome Outcome);

/// <summary>The result of a push run.</summary>
/// <param name="Failure">Why the run stopped early, or <see cref="SkillPushRunFailure.None"/>.</param>
/// <param name="Detail">The offending value or explanation for a failure; <c>null</c> otherwise.</param>
/// <param name="Registry">The registry the run targeted; <c>null</c> when its URL was invalid.</param>
/// <param name="Entries">One entry per discovered skill, in discovery order; empty on an early failure.</param>
internal sealed record SkillPushRunResult(
    SkillPushRunFailure Failure,
    string? Detail,
    Uri? Registry,
    IReadOnlyList<SkillPushEntry> Entries)
{
    /// <summary>True when the run went ahead and every skill reached the registry.</summary>
    public bool Succeeded => Failure == SkillPushRunFailure.None && Entries.All(e => e.Outcome.Succeeded || e.Outcome.Status == SkillPushStatus.Skipped);

    public static SkillPushRunResult Fail(SkillPushRunFailure failure, string? detail, Uri? registry = null) =>
        new(failure, detail, registry, []);
}
