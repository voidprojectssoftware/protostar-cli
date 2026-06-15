namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>How a skill executes (Claude Code's <c>context</c> field).</summary>
internal enum SkillContext
{
    /// <summary>Runs inline in the current conversation.</summary>
    Default,

    /// <summary>Runs in a forked subagent with no conversation history.</summary>
    Fork,
}
