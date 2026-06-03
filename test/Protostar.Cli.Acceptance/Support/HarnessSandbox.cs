namespace Protostar.Cli.Acceptance.Support;

/// <summary>
/// A throwaway fake harness layout in a temp directory. Scenarios point the CLI at
/// <see cref="ConfigDir"/> via <c>--harness-home</c> so hook-install scenarios assert on the
/// produced <c>settings.json</c> without ever touching the developer's real harness. One instance
/// per scenario (Reqnroll resolves and disposes it); removed on dispose.
/// </summary>
public sealed class HarnessSandbox : IDisposable
{
    private bool _disposed;

    public HarnessSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "protostar-harness", Guid.NewGuid().ToString("n"));
        ConfigDir = Path.Combine(Root, ".claude");
        Directory.CreateDirectory(ConfigDir);
    }

    /// <summary>Root of the fixture; removed on dispose.</summary>
    public string Root { get; }

    /// <summary>The harness config dir, passed to the CLI as <c>--harness-home</c>.</summary>
    public string ConfigDir { get; }

    /// <summary>Where the harness keeps its settings (and where hooks are written).</summary>
    public string SettingsPath => Path.Combine(ConfigDir, "settings.json");

    /// <summary>Current settings.json text, or empty string if none exists yet.</summary>
    public string ReadSettings() => File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : string.Empty;

    /// <summary>Seed an initial settings.json (for "existing settings are preserved" scenarios).</summary>
    public void WriteSettings(string json) => File.WriteAllText(SettingsPath, json);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a leaked temp dir must never fail a test.
        }
    }
}
