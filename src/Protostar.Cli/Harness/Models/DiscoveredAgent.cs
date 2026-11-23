namespace Protostar.Cli.Harness;

/// <summary>Where a custom agent was found.</summary>
internal enum AgentScope
{
    /// <summary>In the harness's own config folder (<c>~/.claude/agents</c>): the user's, on every project.</summary>
    Personal,

    /// <summary>In a project's config folder (<c>&lt;repo&gt;/.claude/agents</c>).</summary>
    Project,
}

/// <summary>One custom agent file found on disk.</summary>
/// <remarks>
/// An agent is one Markdown file, identified by the <c>name</c> in its front matter rather than its file name, and it may
/// sit in a subfolder of the agents folder (Claude Code scans it recursively).
/// </remarks>
internal sealed record DiscoveredAgent
{
    /// <summary>The front-matter <c>name</c>; <c>null</c> when the file has none, which Claude Code would not load.</summary>
    public required string? Name { get; init; }

    /// <summary>The front-matter <c>description</c>; <c>null</c> when absent.</summary>
    public string? Description { get; init; }

    /// <summary>Where it was found.</summary>
    public required AgentScope Scope { get; init; }

    /// <summary>The file's full path: the unit a push reads, and the key of its ledger record.</summary>
    public required string Path { get; init; }

    /// <summary>The path relative to the agents folder, with <c>/</c> separators, as the registry stores it.</summary>
    public required string RelativePath { get; init; }

    /// <summary>For a project agent, the project folder it was found in; <c>null</c> for a personal agent.</summary>
    public string? ProjectRoot { get; init; }
}
