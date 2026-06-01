namespace Protostar.Cli.Install;

/// <summary>Where protostar installs to, per OS.</summary>
internal static class InstallLocations
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "protostar.exe" : "protostar";

    /// <summary>Default per-user install directory (no admin rights needed).</summary>
    public static string DefaultDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", "protostar");
        }

        // XDG-ish: ~/.local/bin is conventional and often already on PATH.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "bin");
    }
}

/// <summary>Reads/updates PATH so the install directory is resolvable as <c>protostar</c>.</summary>
internal static class PathManager
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True if <paramref name="dir"/> is on the current process PATH.</summary>
    public static bool IsOnPath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        return path.Split(sep, StringSplitOptions.RemoveEmptyEntries).Any(p => Same(p, dir));
    }

    /// <summary>
    /// Ensures <paramref name="dir"/> is on PATH. On Windows this persists to the user PATH
    /// environment variable. On Unix we do not edit shell rc files automatically. Returns a
    /// human-readable next-step hint, or null if nothing more is needed.
    /// </summary>
    public static string? EnsureOnPath(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
            var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (!parts.Any(p => Same(p, dir)))
            {
                var updated = string.IsNullOrEmpty(userPath) ? dir : $"{userPath};{dir}";
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
            }
            return IsOnPath(dir) ? null : "Restart your shell to pick up the updated PATH.";
        }

        if (IsOnPath(dir))
            return null;

        return $"Add it to your PATH:  export PATH=\"{dir}:$PATH\"  (e.g. append to ~/.profile)";
    }

    /// <summary>Removes <paramref name="dir"/> from the persisted user PATH (Windows only).</summary>
    public static void RemoveFromPath(string dir)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var kept = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !Same(p, dir));
        Environment.SetEnvironmentVariable("Path", string.Join(';', kept), EnvironmentVariableTarget.User);
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), Comparison);
}
