namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>
/// The Claude Code provider: the <see cref="IHarness"/> core (identity and detection). Each capability
/// lives in a sibling partial file: hooks in <c>ClaudeCodeHarness.Hooks.cs</c>
/// (<see cref="IHookCapability"/>), skill discovery in <c>ClaudeCodeHarness.Skills.cs</c>
/// (<see cref="ISkillCapability"/>).
/// </summary>
internal sealed partial class ClaudeCodeHarness : IHarness
{
    public string Id => "claude-code";
    public string DisplayName => "Claude Code";

    public bool TryLocate(string? rootOverride, out HarnessLocation location)
    {
        var (configDir, explicitSource) = ResolveConfigDir(rootOverride);
        location = new HarnessLocation(configDir, Path.Combine(configDir, "settings.json"));

        return explicitSource || Directory.Exists(configDir);
    }

    // Resolution order: --harness-home > PROTOSTAR_HARNESS_ROOT > CLAUDE_CONFIG_DIR > ~/.claude.
    private static (string dir, bool explicitSource) ResolveConfigDir(string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
            return (rootOverride, true);

        var generic = Environment.GetEnvironmentVariable("PROTOSTAR_HARNESS_ROOT");
        if (!string.IsNullOrWhiteSpace(generic))
            return (generic, true);

        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(claude))
            return (claude, true);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return (Path.Combine(home, ".claude"), false);
    }
}
