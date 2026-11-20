namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>Claude Code's memory capability: auto memory lives in <c>projects/&lt;encoded path&gt;/memory/</c>.</summary>
/// <remarks>
/// <para>
/// Claude Code derives the folder from the git repository, so all worktrees and subdirectories of a repo share the
/// main worktree's memory folder, while their transcripts sit in folders of their own (ADR 0002).
/// </para>
/// <para>
/// Project folders are searched in the same roots as transcripts (<see cref="ProjectRoots"/>): a pinned root
/// (<c>--harness-home</c>) alone, so a fixture can never pick up the real <c>~/.claude</c>.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeHarness : IMemoryCapability
{
    // Transcripts tried per folder for a working directory: nearly every transcript records one in its first lines,
    // so a few cover a folder whose newest session is still empty, without reading a long history.
    private const int WorkingDirectoryTries = 5;

    /// <inheritdoc />
    public IReadOnlyList<HarnessProjectFolder> LocateProjectFolders(HarnessLocation location)
    {
        var found = new Dictionary<string, HarnessProjectFolder>(StringComparer.Ordinal);
        foreach (var projects in ProjectRoots(location))
        {
            foreach (var dir in SafeDirectories(projects))
                found.TryAdd(Path.GetFullPath(dir), Folder(dir));
        }

        return [.. found.Values.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public HarnessProjectFolder? ProjectFolderOf(string transcriptPath) =>
        LocateSession(transcriptPath) is [{ } session, ..] ? Folder(Path.GetDirectoryName(session.Path)!) : null;

    /// <inheritdoc />
    public string? ReadWorkingDirectory(HarnessProjectFolder folder)
    {
        var newest = SafeFiles(folder.Path, "*.jsonl")
            .Where(f => SessionIdPattern().IsMatch(Path.GetFileNameWithoutExtension(f)))
            .Select(f => (Path: f, Modified: SafeModified(f)))
            .OrderByDescending(f => f.Modified)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(WorkingDirectoryTries);
        foreach (var (path, _) in newest)
        {
            var transcript = new LocalTranscript(Id, Path.GetFileNameWithoutExtension(path), TranscriptKind.Session, null, path, null);
            if (ReadWorkingDirectory(transcript) is { } directory)
                return directory;
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>Claude Code replaces every character that is not an ASCII letter or digit with '-' (ADR 0002, evidence E6).</remarks>
    public string ProjectFolderName(string directory) =>
        string.Create(directory.Length, directory, (span, path) =>
        {
            for (var i = 0; i < path.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(path[i]) ? path[i] : '-';
        });

    /// <inheritdoc />
    /// <remarks>Always under the location's own <c>projects/</c>, the root Claude Code writes to, never a fallback root.</remarks>
    public string MemoryFolderFor(HarnessLocation location, string directory) =>
        Path.GetFullPath(Path.Combine(location.ConfigDir, "projects", ProjectFolderName(directory), "memory"));

    private HarnessProjectFolder Folder(string dir)
    {
        var memory = Path.Combine(dir, "memory");
        return new HarnessProjectFolder(Id, Path.GetFullPath(dir), Directory.Exists(memory) ? Path.GetFullPath(memory) : null);
    }

    private static DateTime SafeModified(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
