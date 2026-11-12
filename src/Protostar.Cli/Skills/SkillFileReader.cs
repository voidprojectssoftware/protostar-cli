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

    /// <summary>A stable SHA-256 over every file's path and bytes, as lowercase hex.</summary>
    /// <remarks>
    /// Each path and content is length-prefixed so moving bytes between a file's name and its content, or
    /// between two files, always changes the hash. <paramref name="files"/> must be in
    /// <see cref="Read"/>'s order.
    /// </remarks>
    public static string Hash(IReadOnlyList<SkillUploadFile> files)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var file in files)
        {
            var path = System.Text.Encoding.UTF8.GetBytes(file.RelativePath);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, path.Length);
            sha.AppendData(length);
            sha.AppendData(path);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, file.Content.Length);
            sha.AppendData(length);
            sha.AppendData(file.Content);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
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
