using System.Security.Cryptography;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Writes a skill version downloaded from the registry into a skill directory on disk.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written until the whole download checks out: every file's bytes must match the SHA-256 the
/// registry sent, the set must match the version's content hash, and every path must stay inside the skill
/// directory without passing through a symbolic link. A registry, or anything between it and this machine,
/// can therefore never place a file outside the skill.
/// </para>
/// <para>
/// An existing skill is updated in place rather than replaced: files the new version no longer has are
/// deleted, and every other file is written to a temporary file and moved over the old one. What
/// <see cref="SkillFileReader"/> ignores (<c>.git</c>, <c>node_modules</c>, symbolic links) is left alone,
/// so a skill kept under version control keeps its history. A download that would write into one of those
/// folders is refused, since the reader would never see the file again. An updated file keeps its Unix
/// permissions; a new file that starts with <c>#!</c> is made executable for its owner, because the registry
/// does not store file modes.
/// </para>
/// <para>
/// Stale files are found by exact (ordinal) path, because on a case-sensitive file system <c>a.md</c> and
/// <c>A.md</c> are two real files. On a case-insensitive one, a folder whose name differs from the new
/// version's only by case can survive pruning (it still holds an ignored folder, say) and would then
/// receive the new files under its old name, so the reader would report the old name forever. Before
/// writing, such an entry is renamed to the new version's spelling; see <see cref="FindCaseOnlyEntry"/>.
/// </para>
/// </remarks>
internal static class SkillInstaller
{
    /// <summary>
    /// Checks <paramref name="content"/> and writes it into <paramref name="skillDirectory"/>, creating it
    /// when missing.
    /// </summary>
    /// <returns>The files as written, in <see cref="SkillFileReader.Read"/>'s order.</returns>
    /// <exception cref="SkillInstallException">The download failed a check, or the skill directory is a symbolic link; nothing was written.</exception>
    /// <exception cref="IOException">Writing failed part way.</exception>
    /// <exception cref="UnauthorizedAccessException">The skill directory is not writable.</exception>
    public static IReadOnlyList<SkillUploadFile> Install(string skillDirectory, RegistrySkillContent content)
    {
        var root = Path.GetFullPath(skillDirectory);
        if (new DirectoryInfo(root) is { LinkTarget: { } linkTarget })
        {
            throw new SkillInstallException(
                $"'{root}' is a symbolic link (to '{linkTarget}'), and protostar never writes through links. " +
                "Pull into the folder it points to, or replace the link with a real folder.");
        }

        var files = Verify(root, content);

        var wanted = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.Ordinal);
        if (Directory.Exists(root))
        {
            foreach (var stale in SkillFileReader.Read(root).Where(f => !wanted.Contains(f.RelativePath)))
                DeleteAndPrune(root, stale.RelativePath);
        }

        Directory.CreateDirectory(root);
        foreach (var file in files)
            MatchCase(root, file.RelativePath);
        foreach (var file in files)
            Write(root, file);

        return files;
    }

    // Every check that can be made before touching the disk. Returns the files ordered as the reader orders them.
    private static List<SkillUploadFile> Verify(string root, RegistrySkillContent content)
    {
        var files = new List<SkillUploadFile>(content.Files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in content.Files)
        {
            var path = file.RelativePath;
            if (!IsSafeRelativePath(path))
                throw new SkillInstallException($"The registry sent an unsafe file path: '{path}'.");
            if (SkillFileReader.HasIgnoredSegment(path))
                throw new SkillInstallException($"The registry sent '{path}', inside a folder protostar never reads (such as .git or node_modules).");
            if (SkillFileReader.HasPaddedSegment(path))
                throw new SkillInstallException($"The registry sent '{path}', which has a name starting or ending with whitespace.");
            if (!seen.Add(path))
                throw new SkillInstallException($"The registry sent '{path}' twice.");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(file.Content)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SkillInstallException($"'{path}' does not match its checksum; the download may be damaged.");

            EnsureNoLinkOnTheWay(root, path);
            files.Add(new SkillUploadFile(path, file.Content));
        }

        if (!files.Any(f => f.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            throw new SkillInstallException("The download has no SKILL.md at its root.");
        if (!string.Equals(SkillFileReader.RegistryHash(files), content.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SkillInstallException("The downloaded files do not match the version's content hash.");

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    /// <summary>True when <paramref name="path"/> is relative, uses <c>/</c>, and has no empty, <c>.</c> or <c>..</c> segment.</summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.Contains('\0') || path.StartsWith('/'))
            return false;

        return path.Split('/').All(segment => segment is not ("" or "." or ".."));
    }

    /// <summary>True when <paramref name="name"/> can be used as a skill directory name on any OS.</summary>
    internal static bool IsSafeDirectoryName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") &&
        name.IndexOfAny(['/', '\\', ':', '\0', '*', '?', '"', '<', '>', '|']) < 0 &&
        name.Trim() == name;

    /// <summary>
    /// The entry in a directory that names <paramref name="wanted"/> only by case, which must be renamed to
    /// <paramref name="wanted"/>; <c>null</c> when nothing needs renaming.
    /// </summary>
    /// <param name="entries">The names actually in the directory.</param>
    /// <param name="wanted">The name the new version uses.</param>
    /// <param name="wantedResolves">
    /// Whether the file system finds <paramref name="wanted"/> in that directory. When no entry is spelled
    /// exactly <paramref name="wanted"/> yet the name resolves, the file system is case-insensitive and is
    /// handing back the differently-cased entry. When it does not resolve (a case-sensitive file system),
    /// a differently-cased entry is a separate file and is left alone.
    /// </param>
    internal static string? FindCaseOnlyEntry(IEnumerable<string> entries, string wanted, bool wantedResolves)
    {
        var list = entries as IReadOnlyCollection<string> ?? entries.ToList();
        if (!wantedResolves || list.Contains(wanted, StringComparer.Ordinal))
            return null;
        return list.FirstOrDefault(e => string.Equals(e, wanted, StringComparison.OrdinalIgnoreCase));
    }

    // Walks relativePath's segments under root and renames any existing entry whose name differs only by
    // case, so the file lands under the new version's spelling on a case-insensitive file system.
    private static void MatchCase(string root, string relativePath)
    {
        var parent = root;
        foreach (var segment in relativePath.Split('/'))
        {
            if (!Directory.Exists(parent))
                return;

            var candidate = Path.Combine(parent, segment);
            var resolves = File.Exists(candidate) || Directory.Exists(candidate);
            var names = new DirectoryInfo(parent).EnumerateFileSystemInfos().Select(e => e.Name).ToList();
            if (FindCaseOnlyEntry(names, segment, resolves) is { } existing)
            {
                // Two moves, because a case-only rename is a no-op or an error on some case-insensitive file systems.
                var from = Path.Combine(parent, existing);
                var temp = Path.Combine(parent, $".{segment}.protostar-{Guid.NewGuid():N}");
                if (Directory.Exists(from))
                {
                    Directory.Move(from, temp);
                    Directory.Move(temp, candidate);
                }
                else
                {
                    File.Move(from, temp);
                    File.Move(temp, candidate);
                }
            }

            parent = candidate;
        }
    }

    // Refuses a path whose parent directories, or the file itself, already exist as a symbolic link: writing
    // through one could land outside the skill.
    private static void EnsureNoLinkOnTheWay(string root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new SkillInstallException($"'{relativePath}' would be written through a symbolic link at '{current}'.");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new SkillInstallException($"'{relativePath}' resolves outside the skill directory.");
    }

    private static void Write(string root, SkillUploadFile file)
    {
        var target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.protostar-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, file.Content);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                if (File.Exists(target))
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
                else if (file.Content is [(byte)'#', (byte)'!', ..])
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(temp) | UnixFileMode.UserExecute);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    // Deletes a file the new version dropped, then any directories it leaves empty, up to (not including) the root.
    private static void DeleteAndPrune(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(path);

        var directory = Path.GetDirectoryName(path);
        while (directory is not null &&
               !string.Equals(directory, root, StringComparison.Ordinal) &&
               Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
}

/// <summary>A downloaded skill failed a safety or integrity check, so nothing was written.</summary>
internal sealed class SkillInstallException(string message) : Exception(message);
