using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Reads every file of a skill directory into upload form.
/// </summary>
/// <remarks>
/// Paths are relative to the skill's root with <c>/</c> separators on every OS, which is what the
/// registry stores, so the same skill pushed from Windows and Linux hashes the same. Version-control
/// and dependency folders (<see cref="IsIgnoredName"/>) are skipped because they are not part of the
/// skill, and so is a file with one of those names (a submodule's <c>.git</c> file, for one). Symbolic
/// links below the root are not followed, so a link loop or a link out of the skill can never be uploaded.
/// </remarks>
internal static class SkillFileReader
{
    // The one definition of what is never part of a skill. The installer refuses these paths through
    // HasIgnoredSegment, and the registry's RelativePath refuses the same names at push.
    private static readonly HashSet<string> IgnoredNames =
        new(StringComparer.OrdinalIgnoreCase) { ".git", ".hg", ".svn", "node_modules", "__pycache__", ".venv" };

    /// <summary>True when a file or folder called <paramref name="name"/> is never part of a skill (compared ignoring case).</summary>
    public static bool IsIgnoredName(string name) => IgnoredNames.Contains(name);

    /// <summary>True when any <c>/</c>-separated segment of <paramref name="relativePath"/> is <see cref="IsIgnoredName">ignored</see>.</summary>
    /// <remarks>Such a path is never read back from disk, so a skill holding one could never hash the same on both sides.</remarks>
    public static bool HasIgnoredSegment(string relativePath) => relativePath.Split('/').Any(IsIgnoredName);

    /// <summary>True when any <c>/</c>-separated segment of <paramref name="relativePath"/> starts or ends with whitespace.</summary>
    /// <remarks>The registry refuses such paths rather than trimming them, so the CLI refuses them too.</remarks>
    public static bool HasPaddedSegment(string relativePath) => relativePath.Split('/').Any(s => s.Trim() != s);

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

    /// <summary>The registry's content hash over <paramref name="files"/>, as uppercase hex.</summary>
    /// <remarks>
    /// Mirrors the registry's own digest (SHA-256 over each file's <c>path\n</c> plus its uppercase hex
    /// SHA-256 and <c>\n</c>, files in ordinal path order), so a local copy can be compared with a registry
    /// version without downloading it. Distinct from <see cref="Hash"/>, which the ledger keys on.
    /// </remarks>
    public static string RegistryHash(IEnumerable<SkillUploadFile> files)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('\n')
                .Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Content))).Append('\n');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Collect(DirectoryInfo directory, string rootPath, List<SkillUploadFile> files)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || IsIgnoredName(entry.Name))
                continue;

            if (entry is DirectoryInfo child)
            {
                Collect(child, rootPath, files);
                continue;
            }

            var relative = Path.GetRelativePath(rootPath, entry.FullName).Replace('\\', '/');
            files.Add(new SkillUploadFile(relative, File.ReadAllBytes(entry.FullName)));
        }
    }
}
