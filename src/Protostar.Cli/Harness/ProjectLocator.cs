namespace Protostar.Cli.Harness;

/// <summary>
/// Generic "walk up to a project root" helper. It knows nothing about any harness: the calling
/// provider supplies the marker name(s) that mark a project root (e.g. Claude Code passes its own
/// <c>.claude</c>), so harness-specific identifiers stay with the harness and this code just walks
/// directories.
/// </summary>
internal static class ProjectLocator
{
    /// <summary>
    /// Walk up from <paramref name="start"/> to the nearest ancestor directory that contains one of
    /// <paramref name="markers"/> (matched as either a subdirectory or a file). Returns that
    /// directory, or <c>null</c> when no ancestor matches.
    /// </summary>
    public static string? FindAncestorContaining(string start, params string[] markers)
    {
        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            if (markers.Any(m => Exists(current.FullName, m)))
                return current.FullName;
        return null;
    }

    private static bool Exists(string dir, string marker) =>
        Directory.Exists(Path.Combine(dir, marker)) || File.Exists(Path.Combine(dir, marker));
}
