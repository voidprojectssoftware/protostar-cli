using System.Security.Cryptography;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Memory;

/// <summary>Backs up the agent memory on this machine to the registry, and reports what is not backed up.</summary>
/// <remarks>Shared by <c>protostar memory push</c>, <c>memory status</c> and the capture hooks' background push.</remarks>
internal interface IMemorySyncService
{
    /// <summary>Uploads the memory files that changed, appeared or were deleted since the registry last confirmed them.</summary>
    Task<MemoryPushResult> PushAsync(MemoryPushRequest request, CancellationToken cancellationToken);

    /// <summary>Compares the memory on disk with the ledger, without contacting the registry.</summary>
    MemoryStatusResult Status(string? registry, string? harnessHome);
}

/// <summary>What to back up.</summary>
/// <param name="Registry">The <c>--registry</c> option, or <c>null</c> for the environment or default.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="SessionPath">
/// Back up only the memory of the project this session transcript ran in, as a hook names it; <c>null</c> for every
/// project folder. Found from the transcript alone, so a hook needs no <paramref name="HarnessHome"/>.
/// </param>
internal sealed record MemoryPushRequest(string? Registry, string? HarnessHome, string? SessionPath = null);

/// <summary>A memory folder that was not backed up, and why.</summary>
/// <param name="Folder">The harness's project folder name, for example <c>-home-me-src-repo</c>.</param>
/// <param name="Reason">Why.</param>
internal sealed record SkippedMemoryFolder(string Folder, string Reason);

/// <summary>A memory file over <see cref="MemorySyncService.MaxFileBytes"/>, never uploaded.</summary>
internal sealed record OversizedMemoryFile(string Path, long Size);

/// <summary>One memory folder's backup state on this machine.</summary>
/// <param name="Folder">The harness's project folder name.</param>
/// <param name="Directory">The checkout the memory belongs to: the main worktree for git, else the working directory.</param>
/// <param name="New">Files the registry has never confirmed, or that reappeared after a deletion.</param>
/// <param name="Changed">Files whose content differs from what the registry confirmed.</param>
/// <param name="Deleted">Files the registry confirmed that are gone from disk and not yet recorded as deleted.</param>
/// <param name="UpToDate">Files the registry holds as they are.</param>
/// <param name="Oversized">Files too large to back up.</param>
internal sealed record MemoryFolderStatus(
    string Folder, string Directory, IReadOnlyList<string> New, IReadOnlyList<string> Changed, IReadOnlyList<string> Deleted, int UpToDate,
    IReadOnlyList<OversizedMemoryFile> Oversized)
{
    public int Pending => New.Count + Changed.Count + Deleted.Count;
}

/// <summary>Every memory folder's backup state on this machine.</summary>
internal sealed record MemoryStatusResult(
    SessionSyncFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<MemoryFolderStatus> Folders, IReadOnlyList<SkippedMemoryFolder> Skipped)
{
    /// <summary>True when nothing is pending. Skipped folders and oversized files are reported, but backing up again would not change them.</summary>
    public bool FullySynced => Failure == SessionSyncFailure.None && Folders.All(f => f.Pending == 0);
}

/// <summary>What a push did for one memory folder.</summary>
/// <param name="Folder">The harness's project folder name.</param>
/// <param name="Directory">The checkout the memory belongs to.</param>
/// <param name="ProjectName">The registry's name for the project; <c>null</c> when nothing was sent.</param>
/// <param name="Uploaded">Files sent with new content (new to the registry, or a new version).</param>
/// <param name="Deleted">Files the registry now records as deleted on this machine.</param>
/// <param name="UpToDate">Files that needed nothing.</param>
/// <param name="Oversized">Files too large to back up.</param>
/// <param name="Error">Why the folder was not (fully) backed up; <c>null</c> on success.</param>
internal sealed record MemoryFolderOutcome(
    string Folder, string Directory, string? ProjectName, int Uploaded, int Deleted, int UpToDate, IReadOnlyList<OversizedMemoryFile> Oversized, string? Error);

/// <summary>The result of a memory push.</summary>
/// <param name="Machine">This machine's name, which the registry labels the backups with.</param>
internal sealed record MemoryPushResult(
    SessionSyncFailure Failure, string? Detail, Uri? Registry, string? Machine, IReadOnlyList<MemoryFolderOutcome> Folders, IReadOnlyList<SkippedMemoryFolder> Skipped)
{
    /// <summary>True when the push went ahead and no folder failed.</summary>
    public bool Complete => Failure == SessionSyncFailure.None && Folders.All(f => f.Error is null);

    public static MemoryPushResult Fail(SessionSyncFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, null, [], []);
}

/// <summary>Production <see cref="IMemorySyncService"/>.</summary>
/// <remarks>
/// <para>
/// <b>Which project.</b> A memory folder's project is found from the working directory a transcript in the same
/// project folder records (<see cref="IMemoryCapability.ReadWorkingDirectory"/>), resolved to facts by
/// <see cref="IProjectFactsResolver"/> and sent with the files; the registry matches them as it does a session's. The
/// folder name is never decoded (ADR 0002). Claude Code keeps a repo's memory in its main worktree's folder, while a
/// session in a linked worktree or a subfolder writes its transcript to a folder of its own, so a memory folder
/// whose own transcripts record nothing borrows a transcript from a folder whose name extends its own, but only when
/// that transcript's checkout encodes forward to exactly this folder's name. A memory folder with neither is skipped
/// and reported, never guessed.
/// </para>
/// <para>
/// <b>Worktrees.</b> Every memory folder is filed under its checkout's main worktree. When two memory folders
/// resolve to the same checkout, only the one named after it is backed up, so nothing is uploaded twice.
/// </para>
/// <para>
/// <b>Cost.</b> Planning reads only the ledger and a stat of each file; a file whose size and write time match the
/// ledger is never read. Git runs once per memory folder, for its facts. The registry is contacted only when a
/// folder has something to send. A hook's push (<see cref="MemoryPushRequest.SessionPath"/>) looks at most at two
/// folders: the session's own and its main worktree's.
/// </para>
/// <para><b>Privacy.</b> Contents go only to the signed-in user's registry, and nothing here logs them: outcomes carry paths and counts.</para>
/// </remarks>
internal sealed class MemorySyncService : IMemorySyncService
{
    /// <summary>The largest memory file backed up, the registry's own limit.</summary>
    /// <remarks>Memory is short markdown notes; see the registry's <c>MemoryFileSizePolicy</c> for why 256 KiB.</remarks>
    internal const int MaxFileBytes = 256 * 1024;

    /// <summary>The most files, sent and deleted together, in one request (the registry's limit).</summary>
    internal const int MaxFilesPerRequest = 100;

    /// <summary>The most content bytes in one request, before base64; keeps a request well under the registry's size limit.</summary>
    internal const int MaxBytesPerRequest = 8 * 1024 * 1024;

    // How close a file's write time may be to when its ledger entry was recorded before a matching stat stops
    // counting as unchanged. Covers coarse file system timestamps (FAT keeps two seconds).
    private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    // Folders whose names extend a memory folder's are tried for a transcript only up to this many, so one memory
    // folder with hundreds of agent-worktree siblings costs a bounded number of git runs.
    private const int BorrowLimit = 20;

    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _meta;
    private readonly ITranscriptApi _machines;
    private readonly IMemoryApi _api;
    private readonly IMemoryLedger _ledger;
    private readonly IMachineIdentity _machine;
    private readonly IProjectFactsResolver _projects;

    public MemorySyncService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IRegistryApi meta, ITranscriptApi machines, IMemoryApi api,
        IMemoryLedger ledger, IMachineIdentity machine, IProjectFactsResolver projects)
    {
        _catalog = catalog;
        _tokens = tokens;
        _meta = meta;
        _machines = machines;
        _api = api;
        _ledger = ledger;
        _machine = machine;
        _projects = projects;
    }

    /// <inheritdoc />
    public async Task<MemoryPushResult> PushAsync(MemoryPushRequest request, CancellationToken cancellationToken)
    {
        if (!TryResolveRegistry(request.Registry, out var registry, out var detail))
            return MemoryPushResult.Fail(SessionSyncFailure.InvalidRegistry, detail);

        var (folders, skipped) = Find(request.HarnessHome, request.SessionPath);
        Session? session = null;
        var outcomes = new List<MemoryFolderOutcome>();
        foreach (var folder in folders)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            using var held = _ledger.TryLock(registry, folder.Memory.MemoryPath!);
            if (held is null)
            {
                outcomes.Add(Outcome(folder, null, 0, 0, 0, [], "Another protostar process is backing it up."));
                continue;
            }

            var plan = Plan(registry, folder);
            if (plan.Pending == 0)
            {
                // Refresh write times the ledger had wrong, so the next run need not read these files again.
                if (plan.Refreshed)
                    _ledger.Save(registry, folder.Memory.MemoryPath!, plan.Entries);
                outcomes.Add(Outcome(folder, null, 0, 0, plan.UpToDate.Count, plan.Oversized, null));
                continue;
            }

            // The network is needed from here on: connect once, lazily, so a push with nothing new stays offline.
            if (session is null)
            {
                var connected = await ConnectAsync(registry, cancellationToken);
                if (connected.Failure != SessionSyncFailure.None)
                    return MemoryPushResult.Fail(connected.Failure, connected.Detail, registry);
                session = connected.Session!;
            }

            outcomes.Add(await UploadAsync(registry, session, folder, plan, cancellationToken));
        }

        return new MemoryPushResult(SessionSyncFailure.None, null, registry, _machine.Get().Name, outcomes, skipped);
    }

    /// <inheritdoc />
    public MemoryStatusResult Status(string? registryOption, string? harnessHome)
    {
        if (!TryResolveRegistry(registryOption, out var registry, out var detail))
            return new MemoryStatusResult(SessionSyncFailure.InvalidRegistry, detail, null, [], []);

        var (folders, skipped) = Find(harnessHome, null);
        var statuses = folders.Select(folder =>
        {
            var plan = Plan(registry, folder);
            return new MemoryFolderStatus(
                folder.Memory.Name, folder.Directory,
                [.. plan.Send.Where(s => s.IsNew).Select(s => s.Path)],
                [.. plan.Send.Where(s => !s.IsNew).Select(s => s.Path)],
                plan.Delete, plan.UpToDate.Count, plan.Oversized);
        }).ToList();
        return new MemoryStatusResult(SessionSyncFailure.None, null, registry, statuses, skipped);
    }

    // ── finding memory folders and their projects ─────────────────────────────────

    /// <summary>A memory folder and the checkout it belongs to.</summary>
    private sealed record MemoryFolder(HarnessProjectFolder Memory, ProjectFacts Facts)
    {
        /// <summary>The main worktree for git, else the folder git was run in: what memory is filed under.</summary>
        public string Directory => Facts.MainWorktree ?? Facts.Directory;
    }

    private (List<MemoryFolder> Folders, List<SkippedMemoryFolder> Skipped) Find(string? harnessHome, string? sessionPath)
    {
        var found = new List<MemoryFolder>();
        var skipped = new List<SkippedMemoryFolder>();
        foreach (var harness in _catalog.All)
        {
            if (harness is not IMemoryCapability memory)
                continue;
            if (sessionPath is not null)
                found.AddRange(FindForSession(memory, sessionPath));
            else if (harness.TryLocate(harnessHome, out var location))
                FindAll(memory, location, found, skipped);
        }

        // Worktrees share their main worktree's memory folder; a second memory folder of the same checkout is not sent.
        var chosen = new List<MemoryFolder>();
        foreach (var group in found.GroupBy(f => (f.Memory.Harness, f.Directory)))
        {
            var memory = (IMemoryCapability)_catalog.ById(group.Key.Harness)!;
            var keep = group.FirstOrDefault(f => f.Memory.Name == memory.ProjectFolderName(f.Directory))
                ?? group.OrderBy(f => f.Memory.Name, StringComparer.Ordinal).First();
            chosen.Add(keep);
            skipped.AddRange(group.Where(f => f != keep).Select(f =>
                new SkippedMemoryFolder(f.Memory.Name, $"{f.Directory} shares one memory folder; it is backed up from {keep.Memory.Name}.")));
        }

        return ([.. chosen.OrderBy(f => f.Memory.Name, StringComparer.Ordinal)], skipped);
    }

    private void FindAll(IMemoryCapability memory, HarnessLocation location, List<MemoryFolder> found, List<SkippedMemoryFolder> skipped)
    {
        var all = memory.LocateProjectFolders(location);
        foreach (var folder in all.Where(f => f.MemoryPath is not null))
        {
            if (memory.ReadWorkingDirectory(folder) is { } directory)
            {
                found.Add(new MemoryFolder(folder, _projects.Resolve(directory)));
                continue;
            }

            var borrowed = all
                .Where(f => f != folder && f.Name.StartsWith(folder.Name + "-", StringComparison.Ordinal))
                .Take(BorrowLimit)
                .Select(f => memory.ReadWorkingDirectory(f) is { } d ? _projects.Resolve(d) : null)
                .FirstOrDefault(facts => facts is not null && memory.ProjectFolderName(facts.MainWorktree ?? facts.Directory) == folder.Name);
            if (borrowed is not null)
                found.Add(new MemoryFolder(folder, borrowed));
            else
                skipped.Add(new SkippedMemoryFolder(folder.Name, "No transcript records the folder it belongs to, so its project is unknown."));
        }
    }

    // The session's own project folder, and its checkout's main worktree folder beside it, whichever have memory.
    private IEnumerable<MemoryFolder> FindForSession(IMemoryCapability memory, string sessionPath)
    {
        if (memory.ProjectFolderOf(sessionPath) is not { } own || memory.ReadWorkingDirectory(own) is not { } directory)
            yield break;

        var facts = _projects.Resolve(directory);
        if (own.MemoryPath is not null)
            yield return new MemoryFolder(own, facts);

        var mainName = memory.ProjectFolderName(facts.MainWorktree ?? facts.Directory);
        if (mainName == own.Name)
            yield break;
        var main = Path.Combine(Path.GetDirectoryName(own.Path)!, mainName);
        var memoryPath = Path.Combine(main, "memory");
        if (Directory.Exists(memoryPath))
            yield return new MemoryFolder(new HarnessProjectFolder(own.Harness, main, memoryPath), facts);
    }

    // ── planning ──────────────────────────────────────────────────────────────────

    private sealed record Pending(string Path, string FullPath, bool IsNew);

    /// <summary>What a folder needs, from the ledger and a stat of each file; content is read only when size or write time moved.</summary>
    /// <param name="Entries">The ledger's entries, with write times refreshed for files found unchanged by hash.</param>
    /// <param name="Refreshed">Whether <paramref name="Entries"/> differs from what the ledger holds.</param>
    private sealed record UploadPlan(
        Dictionary<string, MemoryLedgerEntry> Entries, bool Refreshed, IReadOnlyList<Pending> Send, IReadOnlyList<string> Delete,
        IReadOnlyList<string> UpToDate, IReadOnlyList<OversizedMemoryFile> Oversized)
    {
        public int Pending => Send.Count + Delete.Count;
    }

    private UploadPlan Plan(Uri registry, MemoryFolder folder)
    {
        var root = folder.Memory.MemoryPath!;
        var entries = new Dictionary<string, MemoryLedgerEntry>(_ledger.Get(registry, root), StringComparer.Ordinal);
        var refreshed = false;
        var send = new List<Pending>();
        var upToDate = new List<string>();
        var oversized = new List<OversizedMemoryFile>();
        var onDisk = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Files(root))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            onDisk.Add(path);
            var (size, modified) = Stat(file);
            if (size > MaxFileBytes)
            {
                oversized.Add(new OversizedMemoryFile(path, size));
                continue;
            }

            if (!entries.TryGetValue(path, out var entry) || entry.Deleted)
            {
                send.Add(new Pending(path, file, true));
                continue;
            }

            // A write in the same clock tick as the one recorded leaves size and write time alone, so a write time
            // within RacyWindow of when the entry was recorded proves nothing and the file is hashed (git's "racy clean").
            if (entry.Size == size && entry.ModifiedAt == modified && modified < entry.RecordedAt - RacyWindow)
            {
                upToDate.Add(path);
                continue;
            }

            if (entry.Size == size && HashOf(file) == entry.Sha256)
            {
                entries[path] = entry with { ModifiedAt = modified, RecordedAt = DateTimeOffset.UtcNow };
                refreshed = true;
                upToDate.Add(path);
                continue;
            }

            send.Add(new Pending(path, file, false));
        }

        var delete = entries.Where(e => !e.Value.Deleted && !onDisk.Contains(e.Key)).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
        return new UploadPlan(entries, refreshed, send, delete, upToDate, oversized);
    }

    // ── uploading ─────────────────────────────────────────────────────────────────

    private sealed record Session(string AccessToken, Guid MachineId);

    // Sends the folder's pending files in batches; the ledger takes each batch's answer before the next is sent, so a
    // run stopped part way loses nothing and the next run sends only what is left.
    private async Task<MemoryFolderOutcome> UploadAsync(Uri registry, Session session, MemoryFolder folder, UploadPlan plan, CancellationToken cancellationToken)
    {
        var root = folder.Memory.MemoryPath!;
        var entries = plan.Entries;
        var uploaded = 0;
        var deleted = 0;
        var upToDate = plan.UpToDate.Count;
        string? projectName = null;

        foreach (var batch in Batches(plan))
        {
            if (cancellationToken.IsCancellationRequested)
                return Outcome(folder, projectName, uploaded, deleted, upToDate, plan.Oversized, "Stopped part way; the rest goes next time.");

            var stats = new Dictionary<string, (long Size, DateTimeOffset Modified, string Sha256)>(StringComparer.Ordinal);
            var files = new List<MemoryUpload>();
            foreach (var pending in batch.Send)
            {
                // Stat before reading: a write after the stat leaves the ledger behind the file, so the next run looks again.
                var (_, modified) = Stat(pending.FullPath);
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(pending.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable since planning; the next run plans it again.
                    continue;
                }

                if (content.Length > MaxFileBytes)
                    continue;
                stats[pending.Path] = (content.Length, modified, Convert.ToHexStringLower(SHA256.HashData(content)));
                files.Add(new MemoryUpload(pending.Path, content));
            }

            ApiResult<MemoryPushResponse> result;
            try
            {
                result = await PushAsync(registry, session, folder.Facts, files, batch.Delete, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Outcome(folder, projectName, uploaded, deleted, upToDate, plan.Oversized, "Stopped part way; the rest goes next time.");
            }

            if (!result.Ok)
                return Outcome(folder, projectName, uploaded, deleted, upToDate, plan.Oversized, result.Error);

            var response = result.Value!;
            projectName = response.ProjectName;
            foreach (var file in response.Files)
            {
                if (stats.TryGetValue(file.Path, out var stat) && file.Version is { } version)
                {
                    entries[file.Path] = new MemoryLedgerEntry(stat.Sha256, stat.Size, stat.Modified, version, DateTimeOffset.UtcNow);
                    if (file.Change == "unchanged")
                        upToDate++;
                    else
                        uploaded++;
                }
                else if (file.Change == "unknown")
                {
                    // The registry never had this copy of it, so there is nothing to mark; forget it.
                    entries.Remove(file.Path);
                }
                else if (entries.TryGetValue(file.Path, out var entry))
                {
                    entries[file.Path] = entry with { Deleted = true };
                    deleted++;
                }
            }

            _ledger.Save(registry, root, entries);
        }

        return Outcome(folder, projectName, uploaded, deleted, upToDate, plan.Oversized, null);
    }

    private static IEnumerable<(List<Pending> Send, List<string> Delete)> Batches(UploadPlan plan)
    {
        var send = new List<Pending>();
        var delete = new List<string>();
        long bytes = 0;
        foreach (var pending in plan.Send)
        {
            var size = Stat(pending.FullPath).Size;
            if (send.Count > 0 && (send.Count >= MaxFilesPerRequest || bytes + size > MaxBytesPerRequest))
            {
                yield return (send, delete);
                (send, delete, bytes) = ([], [], 0);
            }

            send.Add(pending);
            bytes += size;
        }

        foreach (var path in plan.Delete)
        {
            if (send.Count + delete.Count >= MaxFilesPerRequest)
            {
                yield return (send, delete);
                (send, delete) = ([], []);
            }

            delete.Add(path);
        }

        if (send.Count + delete.Count > 0)
            yield return (send, delete);
    }

    // Pushes, registering the machine again first when the registry has forgotten it (its database was rebuilt).
    private async Task<ApiResult<MemoryPushResponse>> PushAsync(
        Uri registry, Session session, ProjectFacts facts, List<MemoryUpload> files, List<string> deleted, CancellationToken cancellationToken)
    {
        var result = await _api.PushMemoryAsync(registry, session.AccessToken, session.MachineId, facts, files, deleted, cancellationToken);
        if (!result.MachineUnknown)
            return result;

        var machine = _machine.Get();
        if ((await _machines.RegisterMachineAsync(registry, session.AccessToken, machine.Id, machine.Name, machine.OperatingSystem, cancellationToken)).Ok)
            _machine.MarkRegistered(registry);
        return await _api.PushMemoryAsync(registry, session.AccessToken, session.MachineId, facts, files, deleted, cancellationToken);
    }

    private async Task<(SessionSyncFailure Failure, string? Detail, Session? Session)> ConnectAsync(Uri registry, CancellationToken cancellationToken)
    {
        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } stored })
        {
            return (lookup.State switch
            {
                SessionState.None => SessionSyncFailure.NotLoggedIn,
                SessionState.RegistryUnreachable => SessionSyncFailure.RegistryUnreachable,
                _ => SessionSyncFailure.SessionExpired,
            }, null, null);
        }

        var meta = await _meta.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return (SessionSyncFailure.RegistryUnreachable, null, null);
        if (ApiCompatibility.Check(meta) is { } incompatible)
            return (SessionSyncFailure.Incompatible, incompatible, null);

        var machine = _machine.Get();
        if (!_machine.IsRegisteredWith(registry))
        {
            var registered = await _machines.RegisterMachineAsync(registry, stored.AccessToken, machine.Id, machine.Name, machine.OperatingSystem, cancellationToken);
            if (!registered.Ok)
                return (SessionSyncFailure.RegistryUnreachable, registered.Error, null);
            _machine.MarkRegistered(registry);
        }

        return (SessionSyncFailure.None, null, new Session(stored.AccessToken, machine.Id));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static MemoryFolderOutcome Outcome(
        MemoryFolder folder, string? projectName, int uploaded, int deleted, int upToDate, IReadOnlyList<OversizedMemoryFile> oversized, string? error) =>
        new(folder.Memory.Name, folder.Directory, projectName, uploaded, deleted, upToDate, oversized, error);

    private static bool TryResolveRegistry(string? option, out Uri registry, out string? detail)
    {
        registry = null!;
        detail = null;
        try
        {
            registry = RegistryEndpoint.Resolve(option);
            return true;
        }
        catch (FormatException ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> Files(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static (long Size, DateTimeOffset Modified) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0, DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, DateTimeOffset.MinValue);
        }
    }

    private static string? HashOf(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
