using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Reads every file of a skill directory into upload form.
/// </summary>
/// <remarks>
/// Paths are relative to the skill's root with <c>/</c> separators on every OS, which is what the
/// registry stores, so the same skill pushed from Windows and Linux hashes the same. Version-control
/// and dependency folders are skipped because they are not part of the skill. Symbolic links below the
/// root are not followed, so a link loop or a link out of the skill can never be uploaded.
/// </remarks>
internal static class SkillFileReader
{
    private static readonly HashSet<string> IgnoredDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", ".hg", ".svn", "node_modules", "__pycache__", ".venv" };

    /// <summary>Reads the files under <paramref name="skillDirectory"/>, ordered by relative path.</summary>
    /// <exception cref="IOException">A file or directory could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A file or directory is not readable by this user.</exception>
    public static IReadOnlyList<SkillUploadFile> Read(string skillDirectory)
    {
        var root = new DirectoryInfo(skillDirectory);
        var files = new List<SkillUploadFile>();
        Collect(root, root.FullName, files);
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    private static void Collect(DirectoryInfo directory, string rootPath, List<SkillUploadFile> files)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
                continue;

            if (entry is DirectoryInfo child)
            {
                if (!IgnoredDirectories.Contains(child.Name))
                    Collect(child, rootPath, files);
                continue;
            }

            var relative = Path.GetRelativePath(rootPath, entry.FullName).Replace('\\', '/');
            files.Add(new SkillUploadFile(relative, File.ReadAllBytes(entry.FullName)));
        }
    }
}
