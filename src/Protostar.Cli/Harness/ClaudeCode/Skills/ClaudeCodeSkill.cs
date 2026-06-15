namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>
/// A Claude Code skill exactly as Claude Code models it: the full SKILL.md front matter, the markdown
/// body, and where the skill was found.
/// </summary>
/// <remarks>
/// This is the definitive, harness-native expression of a skill. Its fields are the Agent Skills
/// open-standard schema plus Claude Code's superset (invocation control, subagent execution, tool
/// pre-approval, dynamic-context and interception hints). It is a pure domain model with no knowledge
/// of <see cref="DiscoveredSkill"/>; translation to the harness-agnostic model is the mapper's job.
/// <para>
/// Well-known fields are typed; <see cref="Hooks"/> and <see cref="Metadata"/> keep their parsed shape
/// rather than a bespoke hierarchy discovery does not need; and <see cref="UnknownFields"/> preserves
/// any key the model does not name, so nothing is lost and future Claude fields round-trip.
/// </para>
/// <para>
/// Field definitions track two specs: the cross-tool Agent Skills standard
/// (<see href="https://agentskills.io/specification"/>) for <c>name</c>, <c>description</c>,
/// <c>license</c>, <c>compatibility</c>, and <c>metadata</c>; and Claude Code's frontmatter reference
/// (<see href="https://code.claude.com/docs/en/skills"/>) for the rest. Where the two disagree, Claude
/// Code wins, since this models a Claude skill.
/// </para>
/// </remarks>
internal sealed record ClaudeCodeSkill
{
    /// <summary>The skill's directory (the unit a push reads); absolute.</summary>
    public required string Directory { get; init; }

    /// <summary>Where the skill was found, relative to the operator.</summary>
    public required SkillScope Scope { get; init; }

    /// <summary>The front-matter <c>name</c>, or the directory name when absent or blank; never blank.</summary>
    public required string Name { get; init; }

    /// <summary>The markdown content after the front matter, verbatim; empty when there is none.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>What the skill does and when to use it; <c>null</c> when absent or blank.</summary>
    public string? Description { get; init; }

    /// <summary>Extra invocation context (<c>when_to_use</c>): trigger phrases or example requests.</summary>
    public string? WhenToUse { get; init; }

    /// <summary>Autocomplete placeholder for arguments (<c>argument-hint</c>), e.g. <c>[issue-number]</c>.</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>Named positional arguments for <c>$name</c> substitution, in order.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Tools pre-approved while the skill is active (<c>allowed-tools</c>).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Tools removed from the pool while the skill is active (<c>disallowed-tools</c>).</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>When <c>true</c>, only the user can invoke the skill; the model cannot. Default <c>false</c>.</summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>When <c>false</c>, the skill is hidden from the slash menu. Default <c>true</c>.</summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Model override while the skill is active; <c>null</c> to inherit the session model.</summary>
    public string? Model { get; init; }

    /// <summary>Effort override while the skill is active; <c>null</c> to inherit the session effort.</summary>
    public SkillEffort? Effort { get; init; }

    /// <summary>Execution context; <see cref="SkillContext.Fork"/> runs the skill in a subagent.</summary>
    public SkillContext? Context { get; init; }

    /// <summary>Subagent type to use when <see cref="Context"/> is <see cref="SkillContext.Fork"/>.</summary>
    public string? Agent { get; init; }

    /// <summary>Glob patterns that gate automatic activation; empty means no path restriction.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>License name or bundled-file reference (Agent Skills field); <c>null</c> when absent.</summary>
    public string? License { get; init; }

    /// <summary>Environment requirements (Agent Skills field); <c>null</c> when absent.</summary>
    public string? Compatibility { get; init; }

    /// <summary>Arbitrary string metadata (Agent Skills field); empty when absent.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The parsed <c>hooks</c> subtree, structure preserved; empty when absent.</summary>
    public IReadOnlyDictionary<string, object?> Hooks { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Tool name this skill intercepts before execution (<c>intercept-tool</c>).</summary>
    public string? InterceptTool { get; init; }

    /// <summary>Tool name this skill intercepts after execution (<c>intercept-after-tool</c>).</summary>
    public string? InterceptAfterTool { get; init; }

    /// <summary>Front-matter keys the model does not name, preserved verbatim for round-tripping.</summary>
    public IReadOnlyDictionary<string, object?> UnknownFields { get; init; } =
        new Dictionary<string, object?>();
}
