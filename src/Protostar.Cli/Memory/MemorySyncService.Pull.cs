using System.Security.Cryptography;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Memory;

/// <summary>What to bring down.</summary>
/// <param name="Registry">The <c>--registry</c> option, or <c>null</c> for the environment or default.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="Scan">Folders named with <c>--scan</c>: each, and its direct subfolders, is a candidate checkout besides those transcripts record.</param>
internal sealed record MemoryPullRequest(string? Registry, string? HarnessHome, IReadOnlyList<string> Scan);

/// <summary>Why a registry memory file was not written into a local memory folder, or what to know about one that is there.</summary>
internal enum MemoryPullNoteKind
{
    /// <summary>A local file with other content is there; it was left as it is.</summary>
    DiffersFromRegistry,

    /// <summary>The file is gone here but this machine's ledger knows it, so it was deleted here; not brought back.</summary>
    DeletedHere,

    /// <summary>Every machine's copy has deleted the file; not written.</summary>
    DeletedEverywhere,

    /// <summary>Another machine deleted the file; the copy here is kept.</summary>
    DeletedElsewhere,

    /// <summary>The download did not match its hash, or its path would leave the memory folder or pass a symbolic link; not written.</summary>
    Refused,
}

/// <summary>One file worth reporting from a pull.</summary>
/// <param name="Path">Its path within the memory folder.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Machines">For <see cref="MemoryPullNoteKind.DeletedElsewhere"/>, the machines that deleted it; else empty.</param>
internal sealed record MemoryPullNote(string Path, MemoryPullNoteKind Kind, IReadOnlyList<string> Machines);

/// <summary>What a pull did for one local checkout.</summary>
/// <param name="ProjectName">The registry's name for the project.</param>
/// <param name="Directory">The checkout: its main worktree for git.</param>
/// <param name="MemoryFolder">The harness memory folder written to.</param>
/// <param name="Written">Files written because they were not here.</param>
/// <param name="UpToDate">Files already here with the registry's content.</param>
/// <param name="Notes">Files kept, refused, or deleted elsewhere, in path order.</param>
/// <param name="Error">Why the folder was not (fully) written; <c>null</c> on success.</param>
internal sealed record MemoryPullFolder(
    string ProjectName, string Directory, string MemoryFolder, int Written, int UpToDate, IReadOnlyList<MemoryPullNote> Notes, string? Error);

/// <summary>A project with memory in the registry and no checkout on this machine.</summary>
internal sealed record MemoryPullAbsent(Guid ProjectId, string ProjectName, int Files);

/// <summary>The result of a memory pull.</summary>
/// <param name="Projects">How many projects the registry has memory for.</param>
/// <param name="Folders">One entry per local checkout written to.</param>
/// <param name="NotHere">Projects skipped because no checkout of them was found on this machine.</param>
internal sealed record MemoryPullResult(
    SessionSyncFailure Failure, string? Detail, Uri? Registry, int Projects, IReadOnlyList<MemoryPullFolder> Folders, IReadOnlyList<MemoryPullAbsent> NotHere)
{
    /// <summary>True when the pull went ahead and no folder failed. Kept files and absent projects are reported, not failures.</summary>
    public bool Complete => Failure == SessionSyncFailure.None && Folders.All(f => f.Error is null);

    /// <summary>No harness that keeps memory was found on this machine, so nothing was asked of the registry.</summary>
    public bool NoHarness { get; init; }

    /// <summary>Files written across every folder.</summary>
    public int Written => Folders.Sum(f => f.Written);

    public static MemoryPullResult Fail(SessionSyncFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, 0, [], []);
}

/// <summary>The pull half of <see cref="MemorySyncService"/>.</summary>
/// <remarks>
/// <para>
/// <b>Finding a checkout.</b> The registry knows a project's facts, not where it is on this machine. Candidates are the
/// working directories recorded by transcripts under the harness root (the folders this machine has opened in the
/// harness), plus <c>--scan</c> folders and their direct subfolders. Nothing else is searched: no disk crawl. Each
/// candidate that exists is resolved to facts, grouped by main worktree, and asked of the registry with
/// <c>POST /v1/projects/resolve</c>, the same matching that files sessions; a checkout whose answer is a project with
/// memory receives that memory.
/// </para>
/// <para>
/// <b>Where it goes.</b> The memory folder is the main worktree's path encoded forward (<see cref="IMemoryCapability.MemoryFolderFor"/>),
/// created when missing. Two checkouts of one project at different paths each get the memory, as each has a memory
/// folder of its own and a session in either should start with it.
/// </para>
/// <para>
/// <b>Never over a local file.</b> A file already here with other content is left and reported. A file gone here that
/// the ledger knows was deleted here and is not brought back; a file every machine deleted is not written; a file
/// another machine deleted is kept here and reported. Files are written via a temp file and a move, never through a
/// symbolic link, and only inside the memory folder. Every file written or found equal is recorded in the ledger with
/// the registry's version, so the next push does not send it back.
/// </para>
/// </remarks>
internal sealed partial class MemorySyncService
{
    /// <inheritdoc />
    public async Task<MemoryPullResult> PullAsync(MemoryPullRequest request, CancellationToken cancellationToken)
    {
        if (!TryResolveRegistry(request.Registry, out var registry, out var detail))
            return MemoryPullResult.Fail(SessionSyncFailure.InvalidRegistry, detail);

        var harnesses = new List<(IMemoryCapability Memory, HarnessLocation Location)>();
        foreach (var harness in _catalog.All)
        {
            if (harness is IMemoryCapability memory && harness.TryLocate(request.HarnessHome, out var location))
                harnesses.Add((memory, location));
        }

        if (harnesses.Count == 0)
            return new MemoryPullResult(SessionSyncFailure.None, null, registry, 0, [], []) { NoHarness = true };

        var connected = await ConnectAsync(registry, cancellationToken);
        if (connected.Failure != SessionSyncFailure.None)
            return MemoryPullResult.Fail(connected.Failure, connected.Detail, registry);
        var session = connected.Session!;

        var listed = await _api.ListMemoryProjectsAsync(registry, session.AccessToken, cancellationToken);
        if (!listed.Ok)
            return MemoryPullResult.Fail(SessionSyncFailure.RegistryUnreachable, listed.Error, registry);
        var remote = listed.Value!;
        if (remote.Count == 0)
            return new MemoryPullResult(SessionSyncFailure.None, null, registry, 0, [], []);

        // Which local checkout belongs to which project, asked once per checkout.
        var checkouts = Checkouts(harnesses, request.Scan);
        var byProject = new Dictionary<Guid, List<(IMemoryCapability Memory, HarnessLocation Location, string Directory)>>();
        foreach (var checkout in checkouts)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            var resolved = await _projectApi.ResolveProjectAsync(registry, session.AccessToken, session.MachineId, checkout.Facts, cancellationToken);
            if (!resolved.Ok)
                return MemoryPullResult.Fail(SessionSyncFailure.RegistryUnreachable, resolved.Error, registry);
            if (resolved.Value!.ProjectId is not { } projectId)
                continue;
            if (!byProject.TryGetValue(projectId, out var list))
                byProject[projectId] = list = [];
            list.Add((checkout.Memory, checkout.Location, checkout.Directory));
        }

        var folders = new List<MemoryPullFolder>();
        var notHere = new List<MemoryPullAbsent>();
        foreach (var project in remote.OrderBy(p => p.ProjectName, StringComparer.Ordinal))
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            if (!byProject.TryGetValue(project.ProjectId, out var targets))
            {
                notHere.Add(new MemoryPullAbsent(project.ProjectId, project.ProjectName, project.Files));
                continue;
            }

            var content = await _api.GetMemoryContentAsync(registry, session.AccessToken, project.ProjectId, cancellationToken);
            foreach (var (memory, location, directory) in targets.OrderBy(t => t.Directory, StringComparer.Ordinal))
            {
                var folder = memory.MemoryFolderFor(location, directory);
                folders.Add(content.Ok
                    ? WriteFolder(registry, project.ProjectName, directory, folder, content.Value!)
                    : new MemoryPullFolder(project.ProjectName, directory, folder, 0, 0, [], content.Error));
            }
        }

        return new MemoryPullResult(SessionSyncFailure.None, null, registry, remote.Count, folders, notHere);
    }

    // ── finding local checkouts ───────────────────────────────────────────────────

    private sealed record Checkout(IMemoryCapability Memory, HarnessLocation Location, string Directory, ProjectFacts Facts);

    // One per harness and main worktree. A folder that no longer exists is not a checkout, so it is never resolved
    // (git would answer for the nearest folder above it instead).
    private List<Checkout> Checkouts(List<(IMemoryCapability Memory, HarnessLocation Location)> harnesses, IReadOnlyList<string> scan)
    {
        var scanned = scan.SelectMany(Scanned).ToList();
        var found = new List<Checkout>();
        foreach (var (memory, location) in harnesses)
        {
            var candidates = memory.LocateProjectFolders(location)
                .Select(memory.ReadWorkingDirectory)
                .OfType<string>()
                .Concat(scanned)
                .Distinct(StringComparer.Ordinal)
                .Where(Directory.Exists);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var facts = _projects.Resolve(candidate);
                var directory = facts.MainWorktree ?? facts.Directory;
                if (seen.Add(directory))
                    found.Add(new Checkout(memory, location, directory, facts));
            }
        }

        return found;
    }

    // A --scan folder and its direct subfolders that are git checkouts (a .git folder, or a .git file for a worktree). No deeper.
    private static IEnumerable<string> Scanned(string folder)
    {
        var root = Path.GetFullPath(folder);
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            children = [];
        }

        return new[] { root }.Concat(children).Where(d => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git")));
    }

    // ── writing ───────────────────────────────────────────────────────────────────

    private MemoryPullFolder WriteFolder(Uri registry, string projectName, string directory, string root, RemoteMemoryContent content)
    {
        MemoryPullFolder Result(int written, int upToDate, List<MemoryPullNote> notes, string? error) =>
            new(projectName, directory, root, written, upToDate, [.. notes.OrderBy(n => n.Path, StringComparer.Ordinal)], error);

        // The memory folder and the project folder above it must be real folders: writing through a link could land anywhere.
        if ((LinkAt(root) ?? LinkAt(Path.GetDirectoryName(root)!)) is { } link)
            return Result(0, 0, [], $"'{link}' is a symbolic link, and protostar never writes through links.");

        using var held = _ledger.TryLock(registry, root);
        if (held is null)
            return Result(0, 0, [], "Another protostar process is using this memory folder.");

        var entries = new Dictionary<string, MemoryLedgerEntry>(_ledger.Get(registry, root), StringComparer.Ordinal);
        var notes = new List<MemoryPullNote>();
        var written = 0;
        var upToDate = 0;
        string? error = null;
        foreach (var file in content.Files)
        {
            var hash = SHA256.HashData(file.Content);
            if (!IsSafe(root, file.Path, out var target) || !Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(new MemoryPullNote(file.Path, MemoryPullNoteKind.Refused, []));
                continue;
            }

            var sha = Convert.ToHexStringLower(hash);
            if (File.Exists(target))
            {
                if (file.DeletedOn.Count > 0)
                    notes.Add(new MemoryPullNote(file.Path, MemoryPullNoteKind.DeletedElsewhere, [.. file.DeletedOn.Select(c => c.MachineName ?? c.MachineId.ToString())]));

                if (HashOf(target) != sha)
                {
                    notes.Add(new MemoryPullNote(file.Path, MemoryPullNoteKind.DiffersFromRegistry, []));
                    continue;
                }

                upToDate++;
                Record(entries, file.Path, target, sha, file.Version);
                continue;
            }

            if (entries.ContainsKey(file.Path))
            {
                notes.Add(new MemoryPullNote(file.Path, MemoryPullNoteKind.DeletedHere, []));
                continue;
            }

            if (file.DeletedEverywhere)
            {
                notes.Add(new MemoryPullNote(file.Path, MemoryPullNoteKind.DeletedEverywhere, []));
                continue;
            }

            try
            {
                Write(target, file.Content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Could not write {file.Path}: {ex.Message}";
                break;
            }

            written++;
            Record(entries, file.Path, target, sha, file.Version);
        }

        _ledger.Save(registry, root, entries);
        return Result(written, upToDate, notes, error);
    }

    private static void Record(Dictionary<string, MemoryLedgerEntry> entries, string path, string target, string sha, int version)
    {
        var (size, modified) = Stat(target);
        entries[path] = new MemoryLedgerEntry(sha, size, modified, version, DateTimeOffset.UtcNow);
    }

    // A registry path is relative with '/' separators. Refuses a rooted path, empty, '.' or '..' segments, a path that
    // resolves outside the memory folder, and one whose folders or file already exist as a symbolic link.
    private static bool IsSafe(string root, string path, out string target)
    {
        target = "";
        var segments = path.Split('/');
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains('\\') || segments.Any(s => s is "" or "." or ".."))
            return false;

        target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (LinkAt(current) is not null)
                return false;
        }

        return true;
    }

    private static string? LinkAt(string path) =>
        new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null ? path : null;

    // Written beside the target and moved over it, so a reader never sees half a file.
    private static void Write(string target, byte[] content)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.protostar-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, content);
        try
        {
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
