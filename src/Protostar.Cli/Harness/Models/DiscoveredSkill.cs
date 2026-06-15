namespace Protostar.Cli.Harness;

/// <summary>Where a discovered skill was found, relative to the operator.</summary>
internal enum SkillScope
{
    /// <summary>Authored once per user, in the harness's config dir (e.g. <c>~/.claude/skills</c>).</summary>
    Global,

    /// <summary>Authored inside a project working tree (e.g. <c>&lt;repo&gt;/.claude/skills</c>).</summary>
    Project,
}

/// <summary>
/// One skill found on disk: its discovery location plus the open-standard fields a caller can show or
/// publish without re-reading the manifest.
/// </summary>
/// <remarks>
/// The harness-agnostic projection of a skill. <see cref="Scope"/> and <see cref="Path"/> are
/// protostar's discovery metadata (where the skill was found); the rest are the cross-tool Agent Skills
/// schema (<see href="https://agentskills.io/specification"/>), populated by each harness's mapper from
/// its native model. A harness that does not surface a field leaves it at its default (<c>null</c>, or
/// empty). Claude Code's superset fields stay on <c>ClaudeCodeSkill</c>; only the open standard crosses
/// into discovery so every harness maps onto the same shape.
/// </remarks>
internal sealed record DiscoveredSkill
{
    /// <summary>The skill's identifier (front-matter <c>name</c>, or the directory name as a fallback).</summary>
    public required string Name { get; init; }

    /// <summary>Where the skill was found, relative to the operator.</summary>
    public required SkillScope Scope { get; init; }

    /// <summary>The skill's directory (the unit a push reads).</summary>
    public required string Path { get; init; }

    /// <summary>What the skill does and when to use it; <c>null</c> when the manifest omits it.</summary>
    public string? Description { get; init; }

    /// <summary>License name or bundled-file reference; <c>null</c> when absent.</summary>
    public string? License { get; init; }

    /// <summary>Environment requirements (intended product, packages, network access); <c>null</c> when absent.</summary>
    public string? Compatibility { get; init; }

    /// <summary>Arbitrary string metadata; empty when absent.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Tools the skill pre-approves; empty when absent.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
}
