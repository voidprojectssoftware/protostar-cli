namespace Protostar.Cli.Harness;

/// <summary>
/// A coding harness protostar can target (Claude Code today; others later). The core contract is thin:
/// identity and detection (who the harness is, where it lives). Everything protostar does to a harness
/// (wiring hooks, discovering skills) is layered on as optional capability interfaces
/// (<see cref="IHookCapability"/>, <see cref="ISkillCapability"/>, ...) a provider implements only when
/// it supports them. Callers test support by pattern-matching (<c>if (harness is IHookCapability h)</c>),
/// keeping the two axes independent: a new harness is one provider, a new capability is one interface.
/// </summary>
internal interface IHarness
{
    /// <summary>Stable slug used as the <c>--harness</c> selector value, e.g. <c>claude-code</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name, e.g. <c>Claude Code</c>.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Resolve where this harness keeps its config and decide whether it is present.
    /// <paramref name="rootOverride"/> (from <c>--harness-home</c>) takes precedence over the
    /// harness's own environment variables and defaults. Returns <c>false</c> when the harness is
    /// not detected (and was not explicitly pointed at via an override).
    /// </summary>
    bool TryLocate(string? rootOverride, out HarnessLocation location);
}
