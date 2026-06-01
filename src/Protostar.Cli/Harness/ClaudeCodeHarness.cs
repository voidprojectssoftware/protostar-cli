using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Protostar.Cli.Harness;

/// <summary>
/// Claude Code integration. Capture hooks live in <c>settings.json</c> under <c>hooks</c>: a
/// <c>PostToolUse</c> hook matching the <c>Skill</c> tool (fires after each skill use — the capture
/// trigger) and a <c>SessionStart</c> hook (the seam for the future suggestion/push-back loop).
/// Every edit is surgical (via <see cref="JsonNode"/>) so unrelated user settings and hooks are
/// preserved, and re-running is idempotent: protostar-managed entries are recognised by a marker in
/// their command and replaced rather than duplicated.
/// </summary>
internal sealed class ClaudeCodeHarness : IHarness
{
    public string Id => "claude-code";
    public string DisplayName => "Claude Code";

    /// <summary>Hook commands containing this token are protostar-managed and safe to replace/remove.</summary>
    private const string Marker = "capture --hook";

    // Relaxed encoder so a quoted Windows exe path serialises as readable \" rather than ",
    // matching how Claude Code writes its own settings.json.
    private static readonly JsonSerializerOptions WriteOptions =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public bool TryLocate(string? rootOverride, out HarnessLocation location)
    {
        var (configDir, explicitSource) = ResolveConfigDir(rootOverride);
        location = new HarnessLocation(configDir, Path.Combine(configDir, "settings.json"));
        // An explicitly chosen root (flag or env var) signals intent, so treat it as present even if
        // the directory does not exist yet. Only the default ~/.claude requires real detection.
        return explicitSource || Directory.Exists(configDir);
    }

    // Resolution order: --harness-home > PROTOSTAR_HARNESS_ROOT > CLAUDE_CONFIG_DIR > ~/.claude.
    // The redirectable roots are what make harness integration testable without touching the real
    // harness (see the acceptance suite). The bool reports whether the root came from an explicit
    // source rather than the ~/.claude default.
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

    public HookChangeSet InstallHooks(HarnessLocation location, string exePath, bool dryRun)
    {
        var root = LoadSettings(location.SettingsPath);
        var existed = File.Exists(location.SettingsPath);
        var before = root.ToJsonString();

        // Clear any prior protostar entries (without tidying empties) then re-add, so a second run
        // reproduces byte-identical output and an upgraded exe path is picked up.
        RemoveManaged(root, cleanupEmpties: false);
        AddManaged(root, exePath);

        if (string.Equals(before, root.ToJsonString(), StringComparison.Ordinal))
            return new HookChangeSet(HookChange.Unchanged, location.SettingsPath, "capture hooks already up to date");

        if (!dryRun)
            WriteSettings(location, root);

        return existed
            ? new HookChangeSet(HookChange.Updated, location.SettingsPath, "updated capture hooks")
            : new HookChangeSet(HookChange.Added, location.SettingsPath, "wrote capture hooks");
    }

    public HookChangeSet RemoveHooks(HarnessLocation location, bool dryRun)
    {
        if (!File.Exists(location.SettingsPath))
            return new HookChangeSet(HookChange.Unchanged, location.SettingsPath, "no settings file");

        var root = LoadSettings(location.SettingsPath);
        var before = root.ToJsonString();

        RemoveManaged(root, cleanupEmpties: true);

        if (string.Equals(before, root.ToJsonString(), StringComparison.Ordinal))
            return new HookChangeSet(HookChange.Unchanged, location.SettingsPath, "no protostar hooks present");

        if (!dryRun)
            WriteSettings(location, root);

        return new HookChangeSet(HookChange.Removed, location.SettingsPath, "removed capture hooks");
    }

    // ── hook construction ────────────────────────────────────────────────────────

    private static void AddManaged(JsonObject root, string exePath)
    {
        var hooks = GetOrAddObject(root, "hooks");
        AddCommandHook(hooks, "PostToolUse", matcher: "Skill", Command(exePath, "PostToolUse"));
        AddCommandHook(hooks, "SessionStart", matcher: null, Command(exePath, "SessionStart"));
    }

    // Quote the exe path so one containing spaces survives the harness's shell invocation.
    private static string Command(string exePath, string hook) => $"\"{exePath}\" capture --hook {hook}";

    private static void AddCommandHook(JsonObject hooks, string eventName, string? matcher, string command)
    {
        var group = new JsonObject();
        if (matcher is not null)
            group["matcher"] = matcher;
        group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command });
        GetOrAddArray(hooks, eventName).Add(group);
    }

    private static void RemoveManaged(JsonObject root, bool cleanupEmpties)
    {
        if (root["hooks"] is not JsonObject hooks)
            return;

        foreach (var entry in hooks.ToList())
        {
            if (entry.Value is not JsonArray groups)
                continue;
            for (var i = groups.Count - 1; i >= 0; i--)
                if (groups[i] is JsonObject g && g["hooks"] is JsonArray hs &&
                    hs.Any(h => h is JsonObject ho && IsManaged(ho)))
                    groups.RemoveAt(i);
        }

        if (!cleanupEmpties)
            return;

        foreach (var key in hooks.Select(kv => kv.Key).ToList())
            if (hooks[key] is JsonArray a && a.Count == 0)
                hooks.Remove(key);
        if (hooks.Count == 0)
            root.Remove("hooks");
    }

    private static bool IsManaged(JsonObject hookEntry)
    {
        var cmd = hookEntry["command"]?.GetValue<string>();
        return cmd is not null
            && cmd.Contains(Marker, StringComparison.Ordinal)
            && cmd.Contains("protostar", StringComparison.OrdinalIgnoreCase);
    }

    // ── settings.json IO ──────────────────────────────────────────────────────────

    private static JsonObject LoadSettings(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Never clobber a settings file we cannot parse — surface it to the caller instead.
            throw new InvalidOperationException($"Could not parse {path} as JSON; leaving it untouched.");
        }
    }

    private static void WriteSettings(HarnessLocation location, JsonObject root)
    {
        Directory.CreateDirectory(location.ConfigDir);
        File.WriteAllText(location.SettingsPath, root.ToJsonString(WriteOptions));
    }

    private static JsonObject GetOrAddObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrAddArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing)
            return existing;
        var created = new JsonArray();
        parent[key] = created;
        return created;
    }
}
