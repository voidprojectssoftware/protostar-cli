using System.Security.Cryptography;
using System.Text;
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

/// <summary>What to know about one registry memory file after a pull.</summary>
internal enum MemoryPullNoteKind
{
    /// <summary>Changed here and on another machine since this machine last synced it; the local file is kept and the other version written beside it.</summary>
    Conflicted,

    /// <summary><c>MEMORY.md</c> changed here and on another machine; the two were merged line by line.</summary>
    Merged,

    /// <summary>Deleted here, then changed on another machine; the change wins and the file is back.</summary>
    Restored,

    /// <summary>The file is gone here but this machine's ledger knows it, so it was deleted here; not brought back.</summary>
    DeletedHere,

    /// <summary>Every machine's copy has deleted the file; not written.</summary>
    DeletedEverywhere,

    /// <summary>Another machine deleted the file; the copy here is kept.</summary>
    DeletedElsewhere,

    /// <summary><c>MEMORY.md</c> is longer than Claude Code loads into a session, so its last lines are not seen.</summary>
    IndexTooLong,

    /// <summary>The download did not match its hash, or its path would leave the memory folder or pass a symbolic link; not written.</summary>
    Refused,
}

/// <summary>One file worth reporting from a pull.</summary>
/// <param name="Path">Its path within the memory folder.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Machines">
/// The other machines involved: for <see cref="MemoryPullNoteKind.DeletedElsewhere"/> those that deleted it; for
/// <see cref="MemoryPullNoteKind.Conflicted"/>, <see cref="MemoryPullNoteKind.Merged"/> and
/// <see cref="MemoryPullNoteKind.Restored"/> the one whose version arrived. Else empty.
/// </param>
/// <param name="Detail">
/// For <see cref="MemoryPullNoteKind.Conflicted"/>, the conflict copy's path; for <see cref="MemoryPullNoteKind.Merged"/>,
/// the lines added; for <see cref="MemoryPullNoteKind.IndexTooLong"/>, its length in lines. Else <c>null</c>.
/// </param>
internal sealed record MemoryPullNote(string Path, MemoryPullNoteKind Kind, IReadOnlyList<string> Machines, string? Detail = null);

/// <summary>What a pull did for one local checkout.</summary>
/// <param name="ProjectName">The registry's name for the project.</param>
/// <param name="Directory">The checkout: its main worktree for git.</param>
/// <param name="MemoryFolder">The harness memory folder written to.</param>
/// <param name="Written">Files written because they were not here (new, or restored).</param>
/// <param name="Updated">Files unchanged here since this machine last synced them, updated to another machine's newer version.</param>
/// <param name="UpToDate">Files already here with the registry's content.</param>
/// <param name="Notes">Files conflicted, merged, restored, kept, refused, or deleted elsewhere, in path order.</param>
/// <param name="Error">Why the folder was not (fully) written; <c>null</c> on success.</param>
internal sealed record MemoryPullFolder(
    string ProjectName, string Directory, string MemoryFolder, int Written, int Updated, int UpToDate, IReadOnlyList<MemoryPullNote> Notes, string? Error);

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

    /// <summary>Files fast-forwarded across every folder.</summary>
    public int Updated => Folders.Sum(f => f.Updated);

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
/// <b>Merging two machines.</b> The ledger entry is the base: the version this machine last synced and its hash.
/// Push sends it with each file, and the registry keeps it on the version it creates, so the registry's version
/// either builds on the base (the base is among its ancestors), is the base, is older than it, or was made in
/// parallel without it. Then:
/// <list type="bullet">
/// <item>a local file still equal to the base is fast-forwarded to a version that builds on it (the old one stays in the registry);</item>
/// <item>a local file changed as well, or any local file against a parallel version, is kept, and the other version
/// is written beside it as a conflict copy (<see cref="MemoryIndex.ConflictCopyPath"/>) with a pointer line in
/// <c>MEMORY.md</c>; no model merges anything;</item>
/// <item><c>MEMORY.md</c> changed on both sides is merged as a set union of its lines (<see cref="MemoryIndex.Union"/>);</item>
/// <item>a file deleted here and changed elsewhere since comes back; one deleted here and not changed elsewhere stays deleted.</item>
/// </list>
/// A local file edited here while the registry has nothing new for it is left for the next push. A file every machine
/// deleted is not written; one another machine deleted is kept here and reported. Files are written via a temp file
/// and a move, never through a symbolic link, and only inside the memory folder. Every file written or found equal is
/// recorded in the ledger with the registry's version, so the next push does not send it back.
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
                    ? WriteFolder(registry, session.MachineId, project.ProjectName, directory, folder, content.Value!)
                    : new MemoryPullFolder(project.ProjectName, directory, folder, 0, 0, 0, [], content.Error));
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

    private MemoryPullFolder WriteFolder(Uri registry, Guid machineId, string projectName, string directory, string root, RemoteMemoryContent content)
    {
        // The memory folder and the project folder above it must be real folders: writing through a link could land anywhere.
        if ((LinkAt(root) ?? LinkAt(Path.GetDirectoryName(root)!)) is { } link)
            return new FolderWrite(root, machineId, directory, []).Result(projectName, $"'{link}' is a symbolic link, and protostar never writes through links.");

        using var held = _ledger.TryLock(registry, root);
        if (held is null)
            return new FolderWrite(root, machineId, directory, []).Result(projectName, "Another protostar process is using this memory folder.");

        var write = new FolderWrite(root, machineId, directory, new Dictionary<string, MemoryLedgerEntry>(_ledger.Get(registry, root), StringComparer.Ordinal));
        string? error = null;
        RemoteMemoryFile? index = null;
        foreach (var file in content.Files)
        {
            var hash = SHA256.HashData(file.Content);
            if (!IsSafe(root, file.Path, out var target) || !Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                write.Note(file.Path, MemoryPullNoteKind.Refused);
                continue;
            }

            // The index goes last: it lists the conflict copies the other files may leave.
            if (file.Path == MemoryIndex.FileName)
            {
                index = file;
                continue;
            }

            try
            {
                write.Take(file, target, Convert.ToHexStringLower(hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Could not write {file.Path}: {ex.Message}";
                break;
            }
        }

        if (error is null)
        {
            try
            {
                write.TakeIndex(index);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Could not write {MemoryIndex.FileName}: {ex.Message}";
            }
        }

        _ledger.Save(registry, root, write.Entries);
        return write.Result(projectName, error);
    }

    /// <summary>Brings one project's registry memory into one local memory folder, keeping count of what it did.</summary>
    private sealed class FolderWrite
    {
        private readonly string _root;
        private readonly Guid _machineId;
        private readonly string _directory;
        private readonly List<MemoryPullNote> _notes = [];
        private int _written;
        private int _updated;
        private int _upToDate;

        public FolderWrite(string root, Guid machineId, string directory, Dictionary<string, MemoryLedgerEntry> entries)
        {
            _root = root;
            _machineId = machineId;
            _directory = directory;
            Entries = entries;
        }

        /// <summary>The folder's ledger entries, updated as files are taken.</summary>
        public Dictionary<string, MemoryLedgerEntry> Entries { get; }

        public MemoryPullFolder Result(string projectName, string? error) =>
            new(projectName, _directory, _root, _written, _updated, _upToDate, [.. _notes.OrderBy(n => n.Path, StringComparer.Ordinal)], error);

        public void Note(string path, MemoryPullNoteKind kind, IReadOnlyList<string>? machines = null, string? detail = null) =>
            _notes.Add(new MemoryPullNote(path, kind, machines ?? [], detail));

        /// <summary>Takes one memory file other than the index, by the rules in <see cref="MemorySyncService"/>'s remarks.</summary>
        public void Take(RemoteMemoryFile file, string target, string sha)
        {
            Entries.TryGetValue(file.Path, out var entry);
            if (File.Exists(target))
            {
                NoteDeletedElsewhere(file);
                var local = HashOf(target);
                if (local == sha)
                {
                    _upToDate++;
                    Confirm(file, target, sha, entry);
                }
                else if (Since(file, entry) is Remote.Unchanged or Remote.Older)
                {
                    // Changed here only: the next push sends it.
                }
                else if (Since(file, entry) == Remote.BuildsOnIt && local == entry!.Sha256)
                {
                    Write(target, file.Content, overwrite: true);
                    _updated++;
                    Confirm(file, target, sha, entry);
                }
                else if (KeepBeside(file, sha) is { } copy)
                {
                    Note(file.Path, MemoryPullNoteKind.Conflicted, [MachineOf(file)], copy);
                    Seen(file, sha, entry);
                }
                else
                {
                    Note(file.Path, MemoryPullNoteKind.Refused);
                }

                return;
            }

            if (entry is not null)
            {
                if (Since(file, entry) is (Remote.BuildsOnIt or Remote.Parallel) && !file.DeletedEverywhere)
                {
                    Write(target, file.Content, overwrite: false);
                    _written++;
                    Confirm(file, target, sha, entry);
                    Note(file.Path, MemoryPullNoteKind.Restored, [MachineOf(file)]);
                }
                else
                {
                    Note(file.Path, MemoryPullNoteKind.DeletedHere);
                }

                return;
            }

            if (file.DeletedEverywhere)
            {
                Note(file.Path, MemoryPullNoteKind.DeletedEverywhere);
                return;
            }

            Write(target, file.Content, overwrite: false);
            _written++;
            Confirm(file, target, sha, entry);
        }

        /// <summary>Takes <c>MEMORY.md</c> (<c>null</c> when the registry has none), merging line by line, and lists the conflict copies at its top.</summary>
        /// <remarks>
        /// Its hash is always taken of the synced part (<see cref="MemoryIndex.Strip"/>), so the pointer lines this
        /// machine adds never count as a change. Run after every other file, so the copies they left are listed.
        /// </remarks>
        public void TakeIndex(RemoteMemoryFile? file)
        {
            if (!IsSafe(_root, MemoryIndex.FileName, out var target))
            {
                Note(MemoryIndex.FileName, MemoryPullNoteKind.Refused);
                return;
            }

            var local = File.Exists(target) ? Encoding.UTF8.GetString(File.ReadAllBytes(target)) : null;
            var body = local is null ? null : MemoryIndex.Strip(local);
            Entries.TryGetValue(MemoryIndex.FileName, out var entry);
            string? sha = null;
            var confirm = false;
            var seen = false;
            if (file is not null)
            {
                sha = Convert.ToHexStringLower(SHA256.HashData(file.Content));
                var remote = Encoding.UTF8.GetString(file.Content);
                if (body is not null)
                {
                    NoteDeletedElsewhere(file);
                    var bodySha = HashOfText(body);
                    if (bodySha == sha)
                    {
                        _upToDate++;
                        confirm = true;
                    }
                    else if (Since(file, entry) is Remote.Unchanged or Remote.Older)
                    {
                        // Changed here only: the next push sends it.
                    }
                    else if (Since(file, entry) == Remote.BuildsOnIt && bodySha == entry!.Sha256)
                    {
                        body = remote;
                        _updated++;
                        confirm = true;
                    }
                    else
                    {
                        (body, var added) = MemoryIndex.Union(body, remote);
                        if (added > 0)
                            Note(MemoryIndex.FileName, MemoryPullNoteKind.Merged, [MachineOf(file)], added.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        seen = true;
                    }
                }
                else if (entry is not null)
                {
                    if (Since(file, entry) is (Remote.BuildsOnIt or Remote.Parallel) && !file.DeletedEverywhere)
                    {
                        body = remote;
                        _written++;
                        confirm = true;
                        Note(MemoryIndex.FileName, MemoryPullNoteKind.Restored, [MachineOf(file)]);
                    }
                    else
                    {
                        Note(MemoryIndex.FileName, MemoryPullNoteKind.DeletedHere);
                    }
                }
                else if (file.DeletedEverywhere)
                {
                    Note(MemoryIndex.FileName, MemoryPullNoteKind.DeletedEverywhere);
                }
                else
                {
                    body = remote;
                    _written++;
                    confirm = true;
                }
            }

            // One pointer per conflict copy on disk: a new copy gains one, a copy the user deleted loses its own.
            var copies = Files(_root).Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).Where(MemoryIndex.IsConflictCopy).ToList();
            if (body is null && copies.Count == 0)
                return;
            var pointers = local is null ? [] : MemoryIndex.Pointers(local);
            var desired = MemoryIndex.WithPointers(body ?? "", [.. copies.Select(c => pointers.GetValueOrDefault(c) ?? MemoryIndex.PointerLine(c))]);
            var changed = desired != local;
            if (changed)
                Write(target, Encoding.UTF8.GetBytes(desired), overwrite: local is not null);

            if (confirm)
                Confirm(file!, target, sha!, entry);
            else if (seen)
                Seen(file!, sha!, entry);
            else if (changed && entry is { Deleted: false } && entry.ModifiedAt != DateTimeOffset.MinValue && HashOfText(MemoryIndex.Strip(desired)) == entry.Sha256)
                Confirm(entry, target); // only pointer lines changed: the synced part is still what the registry has

            var lines = MemoryIndex.LineCount(desired);
            if (changed && (lines > MemoryIndex.LoadedLines || Encoding.UTF8.GetByteCount(desired) > MemoryIndex.LoadedBytes))
                Note(MemoryIndex.FileName, MemoryPullNoteKind.IndexTooLong, null, lines.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>How the registry's version relates to the base, the version this machine last synced.</summary>
        private enum Remote
        {
            /// <summary>It is the base.</summary>
            Unchanged,

            /// <summary>It was edited from the base, directly or through later versions: taking it loses nothing here.</summary>
            BuildsOnIt,

            /// <summary>The base was edited from it: an older version another copy still holds. Never taken.</summary>
            Older,

            /// <summary>Made without the base, on another machine in parallel, or there is no base.</summary>
            Parallel,
        }

        // Follows each version's base back from the registry's version, and from the base. The number alone would take
        // a parallel edit for a newer one: a machine that never saw version 1 still sends version 2. A registry from
        // before protostar-cli#47 sends no history, so then a higher number is all there is to go on.
        private static Remote Since(RemoteMemoryFile file, MemoryLedgerEntry? entry)
        {
            if (entry is null)
                return Remote.Parallel;
            if (file.Version == entry.Version)
                return Remote.Unchanged;
            if (file.History.Count == 0)
                return file.Version > entry.Version ? Remote.BuildsOnIt : Remote.Older;

            var bases = file.History.ToDictionary(h => h.Version, h => h.Base);
            if (Ancestors(file.Version).Contains(entry.Version))
                return Remote.BuildsOnIt;
            return Ancestors(entry.Version).Contains(file.Version) ? Remote.Older : Remote.Parallel;

            HashSet<int> Ancestors(int version)
            {
                var found = new HashSet<int>();
                while (bases.GetValueOrDefault(version) is { } parent && found.Add(parent))
                    version = parent;
                return found;
            }
        }

        private static string MachineOf(RemoteMemoryFile file) => file.MachineName ?? "another machine";

        // Copies deleted on another machine, or in another checkout here; this checkout's own deletion is not news.
        private void NoteDeletedElsewhere(RemoteMemoryFile file)
        {
            var others = file.DeletedOn.Where(c => c.MachineId != _machineId || c.Folder != _directory).Select(c => c.MachineName ?? c.MachineId.ToString()).ToList();
            if (others.Count > 0)
                Note(file.Path, MemoryPullNoteKind.DeletedElsewhere, others);
        }

        // Writes the other machine's version beside the local file, under the first free name; a copy already there
        // with the same content is reused, so pulling twice never makes a second one.
        private string? KeepBeside(RemoteMemoryFile file, string sha)
        {
            for (var attempt = 1; ; attempt++)
            {
                var copy = MemoryIndex.ConflictCopyPath(file.Path, file.MachineName, attempt);
                if (!IsSafe(_root, copy, out var target))
                    return null;
                if (!File.Exists(target))
                {
                    Write(target, file.Content, overwrite: false);
                    return copy;
                }

                if (HashOf(target) == sha)
                    return copy;
            }
        }

        // The file now holds the registry's version. A deletion the registry recorded for this machine stays recorded,
        // so the next push sends the file and the registry knows this machine has it again.
        private void Confirm(RemoteMemoryFile file, string target, string sha, MemoryLedgerEntry? entry)
        {
            var (size, modified) = Stat(target);
            Entries[file.Path] = new MemoryLedgerEntry(sha, size, modified, file.Version, DateTimeOffset.UtcNow, entry?.Deleted ?? false);
        }

        private void Confirm(MemoryLedgerEntry entry, string target)
        {
            var (size, modified) = Stat(target);
            Entries[MemoryIndex.FileName] = entry with { Size = size, ModifiedAt = modified, RecordedAt = DateTimeOffset.UtcNow };
        }

        // This machine has seen the version (it is beside the file, or merged in), so it becomes the base, and a pull
        // with nothing newer reports nothing again. No file has the minimum write time, so the next push hashes the
        // local file, finds it differs from that version, and sends it.
        private void Seen(RemoteMemoryFile file, string sha, MemoryLedgerEntry? entry) =>
            Entries[file.Path] = new MemoryLedgerEntry(sha, file.Content.Length, DateTimeOffset.MinValue, file.Version, DateTimeOffset.UtcNow, entry?.Deleted ?? false);

        private static string HashOfText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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

    // Written beside the target and moved into place, so a reader never sees half a file.
    private static void Write(string target, byte[] content, bool overwrite)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.protostar-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, content);
        try
        {
            File.Move(temp, target, overwrite);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
