using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Skills;

/// <summary>
/// Production <see cref="ISkillPullService"/>: harnesses from <see cref="IHarnessCatalog"/>, the session
/// from <see cref="IAccessTokenProvider"/>, the registry through <see cref="IRegistryApi"/>, and sync
/// history from <see cref="ISkillLedger"/>.
/// </summary>
/// <remarks>
/// <para>
/// A local skill matches a registry skill by exact name first, then ignoring case. It is up to date when
/// its files hash to the registry's content hash. Otherwise the ledger decides: if the local files still
/// match what was last pushed or pulled as this same registry skill, nobody edited them here and the
/// registry simply moved on (an update is available); if they do not, or there is no such record, the
/// difference may be local work and is protected. When the copy was edited here since the version it last
/// synced and the registry has moved past that version too, or the registry refused this exact content as stale,
/// it is changed on both sides (<see cref="SkillSyncState.ChangedBoth"/>): protected the same way, and shown so
/// the user can choose which to keep.
/// </para>
/// <para>
/// <c>--version N</c> compares with, and pulls, version N of one named skill instead of its current version, so
/// work lost to an overwrite can be recovered. Every rule above still holds: only a copy that is unedited since
/// it last synced (or already equals version N) is replaced without <c>--force</c>.
/// </para>
/// <para>
/// Two cases are conflicts that are never written, even with <c>--force</c>. A registry skill with no local
/// match whose folder name is already taken (by a skill with another name, a folder with no SKILL.md, or
/// the same name in another case) would overwrite something that is not this skill. Two registry skills
/// whose names differ only by case would share one folder, so neither is pulled rather than guessing.
/// </para>
/// <para>
/// Each registry skill is matched only within its own scope. Global skills pair with each harness's global
/// skills. Project skills pair with the project skills of the folder the command runs in, and only when the
/// registry resolves that folder (through <see cref="IProjectFactsResolver"/>, the resolver session backups
/// use, and <c>POST /v1/projects/resolve</c>, which changes nothing) to the skill's project. The folder's
/// project skills live under the repo top level of this checkout, not its main worktree, so a worktree pulls
/// into itself. Every other project's skill is reported as <see cref="SkillSyncState.OtherProject"/> and
/// never written, so a pull only ever writes into the repo the current folder is in.
/// </para>
/// <para>
/// A local copy no registry skill claims, whose ledger record says it synced as a skill the registry has since deleted
/// or renamed (the list's removed names, registry#58), is <see cref="SkillSyncState.RemovedInRegistry"/>. A full pull
/// renames an unedited copy of a renamed skill to follow it (<see cref="SkillPullStatus.FollowedRename"/>) and keeps
/// every other such copy; it never deletes local files.
/// </para>
/// <para>
/// Everything that can stop the run (bad URL, unknown harness, no session, unreachable or incompatible
/// registry, unknown skill name) is checked before anything is written.
/// </para>
/// </remarks>
internal sealed class SkillPullService : ISkillPullService
{
    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _registry;
    private readonly ISkillLedger _ledger;
    private readonly IProjectFactsResolver _facts;
    private readonly IProjectApi _projects;
    private readonly IMachineIdentity _machine;

    public SkillPullService(
        IHarnessCatalog catalog,
        IAccessTokenProvider tokens,
        IRegistryApi registry,
        ISkillLedger ledger,
        IProjectFactsResolver facts,
        IProjectApi projects,
        IMachineIdentity machine)
    {
        _catalog = catalog;
        _tokens = tokens;
        _registry = registry;
        _ledger = ledger;
        _facts = facts;
        _projects = projects;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<SkillRemoteResult> CompareAsync(SkillRemoteRequest request, CancellationToken cancellationToken)
    {
        var names = request.Names ?? [];
        if (VersionProblem(names, request.Version) is { } versionProblem)
            return SkillRemoteResult.Fail(SkillPullRunFailure.InvalidVersion, versionProblem);

        var context = await OpenAsync(request.HarnessId, request.HarnessHome, request.Registry, request.ProjectStart, cancellationToken);
        if (context.Failure != SkillPullRunFailure.None)
            return SkillRemoteResult.Fail(context.Failure, context.Detail, context.Registry);

        var selected = Select(context.Skills, names, out var missing);
        if (missing is not null)
            return SkillRemoteResult.Fail(SkillPullRunFailure.SkillNotFound, missing, context.Registry);

        var pinned = await PinAsync(context, selected, request.Version, cancellationToken);
        if (pinned.Error is { } pinError)
            return SkillRemoteResult.Fail(SkillPullRunFailure.VersionNotFound, pinError, context.Registry);

        var comparisons = new List<SkillComparison>();
        foreach (var target in context.Targets)
        {
            var inScope = InScope(target, context.Skills);
            var matches = MatchAll(target, InScope(target, selected), inScope, out var leftovers);
            foreach (var match in matches)
                comparisons.Add(await CompareAsync(target, match, context, pinned.Of(match.Remote), cancellationToken));

            // Local skills no registry skill claims are only listed when every skill is. A copy of a skill the registry
            // deleted or renamed since it synced says so, rather than looking like a skill that was never backed up.
            if (names.Count == 0)
            {
                foreach (var leftover in leftovers)
                {
                    comparisons.Add(RemovedCopyOf(context, target, leftover) is { } removed
                        ? new SkillComparison(target.Harness, leftover.Name, SkillSyncState.RemovedInRegistry, null, leftover.Path, removed.Recorded.Version,
                            RemovalNote(removed, pulling: false), target.ProjectName)
                        : new SkillComparison(target.Harness, leftover.Name, SkillSyncState.LocalOnly, null, leftover.Path, null, Project: target.ProjectName));
                }
            }

            if (target.Scope == SkillScope.Global)
            {
                foreach (var remote in Elsewhere(context, selected))
                    comparisons.Add(new SkillComparison(target.Harness, remote.Name, SkillSyncState.OtherProject, remote, null, null, ElsewhereNote(target, context.Here, remote), remote.ProjectName));
            }
        }

        var ordered = comparisons
            .OrderBy(c => c.Harness.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Project is not null)
            .ThenBy(c => c.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SkillRemoteResult(SkillPullRunFailure.None, null, context.Registry, ordered, request.Version);
    }

    /// <inheritdoc />
    public async Task<SkillPullRunResult> PullAsync(SkillPullRequest request, CancellationToken cancellationToken)
    {
        if (VersionProblem(request.Names, request.Version) is { } versionProblem)
            return SkillPullRunResult.Fail(SkillPullRunFailure.InvalidVersion, versionProblem);

        var context = await OpenAsync(request.HarnessId, request.HarnessHome, request.Registry, request.ProjectStart, cancellationToken);
        if (context.Failure != SkillPullRunFailure.None)
            return SkillPullRunResult.Fail(context.Failure, context.Detail, context.Registry);

        var registry = context.Registry!;
        var selected = Select(context.Skills, request.Names, out var missing);
        if (missing is not null)
            return SkillPullRunResult.Fail(SkillPullRunFailure.SkillNotFound, missing, registry);

        var pinned = await PinAsync(context, selected, request.Version, cancellationToken);
        if (pinned.Error is { } pinError)
            return SkillPullRunResult.Fail(SkillPullRunFailure.VersionNotFound, pinError, registry);

        var entries = new List<SkillPullEntry>();
        foreach (var each in context.Targets)
        {
            var target = each;
            var matches = MatchAll(target, InScope(target, selected), InScope(target, context.Skills), out var leftovers);

            // A full pull also settles copies of skills removed from the registry: an unedited copy of a renamed skill
            // follows the rename, so the registry's skill then matches it under its new name; anything else is kept and
            // reported. Local files are never deleted.
            if (request.Names.Count == 0)
            {
                var local = target.Local.ToList();
                foreach (var leftover in leftovers)
                {
                    if (RemovedCopyOf(context, target, leftover) is not { } removed)
                        continue;
                    var (entry, renamed) = SettleRemoved(target, removed, registry);
                    entries.Add(entry);
                    if (renamed is not null)
                        local[local.IndexOf(leftover)] = renamed;
                }

                if (!local.SequenceEqual(target.Local))
                {
                    target = target with { Local = local };
                    matches = MatchAll(target, InScope(target, selected), InScope(target, context.Skills), out _);
                }
            }

            foreach (var match in matches)
            {
                var version = pinned.Of(match.Remote);
                var comparison = await CompareAsync(target, match, context, version, cancellationToken);
                entries.Add(await PullOneAsync(target, comparison, registry, context.AccessToken!, request.Force, version?.Content, match.Remote.Version, cancellationToken));
            }

            if (target.Scope == SkillScope.Global)
            {
                foreach (var remote in Elsewhere(context, selected))
                {
                    entries.Add(new SkillPullEntry(target.Harness, remote.Name, SkillPullStatus.OtherProject, remote.Version,
                        WouldGo(target, remote), ElsewhereNote(target, context.Here, remote), remote.ProjectName));
                }
            }
        }

        entries = entries
            .OrderBy(e => e.Harness.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Project is not null)
            .ThenBy(e => e.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SkillPullRunResult(SkillPullRunFailure.None, null, registry, entries, request.Version);
    }

    // Why --version cannot be used as asked: it picks one version of one skill.
    private static string? VersionProblem(IReadOnlyList<string> names, int? version) => version switch
    {
        null => null,
        < 1 => $"--version takes a version number (1 or more), not {version}.",
        _ when names.Count != 1 => "--version picks a version of one skill: name exactly one skill.",
        _ => null,
    };

    // With --version, fetches that version of each selected skill this folder can pull (global skills, and this
    // folder's project's), so it can be compared and installed in place of the current version. Other projects'
    // skills are only listed, so they are not fetched.
    private async Task<Pinned> PinAsync(Context context, IReadOnlyList<RegistrySkill> selected, int? version, CancellationToken cancellationToken)
    {
        var pinned = new Dictionary<RegistrySkill, PinnedVersion>();
        if (version is not { } wanted)
            return new Pinned(pinned, null);

        foreach (var remote in selected.Where(s => s.ProjectId is null || context.Targets.Any(t => t.ProjectId == s.ProjectId)))
        {
            var download = await _registry.GetSkillFilesAsync(context.Registry!, context.AccessToken!, remote.Name, remote.ProjectId, wanted, cancellationToken);
            if (download.Content is not { } content || content.Version != wanted)
                return new Pinned(pinned, download.Error ?? $"The registry did not send version {wanted} of '{remote.Name}'.");
            pinned[remote] = new PinnedVersion(remote with { Version = wanted, ContentHash = content.ContentHash }, content);
        }

        return new Pinned(pinned, null);
    }

    private async Task<SkillPullEntry> PullOneAsync(
        Target target,
        SkillComparison comparison,
        Uri registry,
        string accessToken,
        bool force,
        RegistrySkillContent? pinned,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        var remote = comparison.Remote!;
        var harness = target.Harness;
        var project = target.ProjectName;

        if (comparison.State == SkillSyncState.Conflict)
        {
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Conflict, remote.Version,
                comparison.LocalPath ?? target.SkillsDirectory, comparison.Note, project);
        }

        if (comparison.State == SkillSyncState.UpToDate)
        {
            // Make sure the ledger knows, so the capture hook treats this copy as already backed up.
            RecordInstalled(registry, comparison.LocalPath!, remote);
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.UpToDate, remote.Version, comparison.LocalPath!, null, project);
        }

        if (comparison.State == SkillSyncState.LocalChanges && !force)
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.LocalChanges, remote.Version, comparison.LocalPath!, null, project);

        if (comparison.State == SkillSyncState.ChangedBoth && !force)
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.ChangedBoth, remote.Version, comparison.LocalPath!, null, project);

        string path;
        if (comparison.LocalPath is { } existing)
        {
            path = existing;
        }
        else if (SkillInstaller.IsSafeDirectoryName(remote.Name))
        {
            path = Path.Combine(target.SkillsDirectory, remote.Name);
        }
        else
        {
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Failed, remote.Version, target.SkillsDirectory,
                $"'{remote.Name}' cannot be used as a folder name on this machine.", project);
        }

        var download = pinned is null
            ? await _registry.GetSkillFilesAsync(registry, accessToken, remote.Name, remote.ProjectId, null, cancellationToken)
            : new RegistrySkillDownload(pinned, null);
        if (download.Content is not { } content)
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Failed, remote.Version, path, download.Error, project);

        IReadOnlyList<SkillUploadFile> written;
        try
        {
            written = SkillInstaller.Install(path, content);
        }
        catch (Exception ex) when (ex is SkillInstallException or IOException or UnauthorizedAccessException)
        {
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Failed, content.Version, path, ex.Message, project);
        }

        _ledger.Record(registry, path, new SkillLedgerEntry(SkillFileReader.Hash(written), content.Version, remote.Name, remote.ProjectId));

        var status = comparison.LocalPath is null ? SkillPullStatus.Installed : SkillPullStatus.Updated;
        var note = comparison.State is SkillSyncState.LocalChanges or SkillSyncState.ChangedBoth ? "Local changes were overwritten (--force)." : null;
        if (content.Version < currentVersion)
        {
            // Recorded as synced at the pinned version: honest, and it makes a later `push --force` a deliberate replace.
            note = (note is null ? "" : note + " ") +
                $"A plain `protostar skills pull` would replace it with the current version; `protostar skills push {remote.Name} --force` makes it current.";
        }
        return new SkillPullEntry(harness, remote.Name, status, content.Version, path, note, project);
    }

    // The removal behind a local copy no registry skill claims: the ledger says it synced as a registry skill of this
    // target's scope, and the registry says that skill was deleted or renamed after the version the copy synced.
    private RemovedCopy? RemovedCopyOf(Context context, Target target, DiscoveredSkill local)
    {
        if (context.Listing is not { } listing || _ledger.Get(context.Registry!, local.Path) is not { } recorded)
            return null;
        if (recorded.ProjectId is null != (target.Scope == SkillScope.Global))
            return null;
        if (listing.RemovalOf(recorded.Name ?? local.Name, recorded.ProjectId, recorded.Version) is not { } removal)
            return null;

        bool edited;
        try
        {
            edited = SkillFileReader.Hash(SkillFileReader.Read(local.Path)) != recorded.ContentHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            edited = true;
        }

        return new RemovedCopy(local, recorded, removal, edited);
    }

    // Follows a rename for an unedited copy (its SKILL.md name and its folder, as `skills rename` does on the machine that
    // ran it) and returns the copy under its new name; reports anything else as kept. Never deletes a local file.
    private (SkillPullEntry Entry, DiscoveredSkill? Renamed) SettleRemoved(Target target, RemovedCopy removed, Uri registry)
    {
        var (local, recorded, removal, edited) = removed;
        var version = recorded.Version ?? 0;
        SkillPullEntry Kept(string note) =>
            new(target.Harness, local.Name, SkillPullStatus.RemovedInRegistry, version, local.Path, note, target.ProjectName);

        if (removal.RenamedTo is not { } newName || edited)
            return (Kept(RemovalNote(removed, pulling: true)), null);

        if (target.Local.Any(l => string.Equals(l.Name, newName, StringComparison.OrdinalIgnoreCase)) || OccupiedFolder(target.SkillsDirectory, newName) is not null)
        {
            return (Kept($"Renamed to '{newName}' in the registry (at v{removal.LastVersion}), but '{newName}' is already here, so this copy was left as it is. " +
                "It has no changes of its own since it last synced: remove it if you no longer need it."), null);
        }

        var (path, _, note) = SkillChangeService.RenameCopy(local.Path, removal.Name, newName);
        if (path != local.Path)
            _ledger.Forget(registry, local.Path);
        string hash;
        try
        {
            hash = SkillFileReader.Hash(SkillFileReader.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hash = recorded.ContentHash;
        }

        // Still the version it last synced, now under the new name, so the registry's skill of that name matches it as
        // an unedited copy: up to date, or an update available.
        _ledger.Record(registry, path, recorded with { Name = newName, ContentHash = hash });
        var message = $"Renamed here to '{newName}' at {path}, following the rename in the registry.";
        return (new SkillPullEntry(target.Harness, local.Name, SkillPullStatus.FollowedRename, version, path,
                note is null ? message : $"{message} Note: {note}.", target.ProjectName),
            local with { Name = newName, Path = path });
    }

    /// <summary>What happened to a removed skill this copy came from, and the ways out, as remote and pull say it.</summary>
    private static string RemovalNote(RemovedCopy removed, bool pulling)
    {
        var (local, _, removal, edited) = removed;
        var quoted = local.Name.Any(char.IsWhiteSpace) ? $"\"{local.Name}\"" : local.Name;
        if (removal.RenamedTo is not { } newName)
        {
            var why = edited ? "This copy has changes made here and is kept" : "This copy is kept";
            return $"Deleted from the registry (at v{removal.LastVersion}) after this copy last synced. {why}: remove it if you no longer want it, " +
                $"or run `protostar skills push {quoted} --force` to restore it.";
        }

        if (!edited)
            return $"Renamed to '{newName}' in the registry (at v{removal.LastVersion}). `protostar skills pull` renames this copy to match.";
        return $"Renamed to '{newName}' in the registry (at v{removal.LastVersion}), but this copy has changes made here, so it " +
            (pulling ? "was not renamed" : "will not be renamed") +
            $". Move them into '{newName}', or run `protostar skills push {quoted} --force` to keep this as a separate skill named '{local.Name}'.";
    }

    // The registry skills a target compares with: global skills for a global target, this folder's project's
    // skills for a project target.
    private static List<RegistrySkill> InScope(Target target, IReadOnlyList<RegistrySkill> skills) =>
        skills.Where(s => target.Scope == SkillScope.Global ? s.ProjectId is null : target.ProjectId is { } id && s.ProjectId == id).ToList();

    // Project skills no project target here covers (another project's, as a rule): listed, never written.
    private static IEnumerable<RegistrySkill> Elsewhere(Context context, IReadOnlyList<RegistrySkill> skills) =>
        skills.Where(s => s.ProjectId is { } id && !context.Targets.Any(t => t.Scope == SkillScope.Project && t.ProjectId == id));

    // Where a project skill of another project would be installed: its own checkout's project skills folder.
    private static string WouldGo(Target target, RegistrySkill remote) =>
        Path.Combine(target.Capability.ProjectSkillsDirectory($"<checkout of {remote.ProjectName}>"), remote.Name);

    private static string ElsewhereNote(Target target, Here? here, RegistrySkill remote)
    {
        var whose = $"Belongs to project {remote.ProjectName}";
        var go = $"pull it from a checkout of that project, which installs it into {WouldGo(target, remote)}";
        if (here?.Problem is { } problem)
            return $"{whose}; could not tell which project this folder is in ({problem}), so {go}.";
        if (here?.ProjectId is { } id && id == remote.ProjectId)
            return $"{whose}, this folder's, but this folder's project skills folder is the global skills folder, so it is not pulled here; {go}.";
        return $"{whose}, not this folder's; {go}.";
    }

    private async Task<SkillComparison> CompareAsync(Target target, Match match, Context context, PinnedVersion? pinned, CancellationToken cancellationToken)
    {
        var registry = context.Registry!;
        var (current, installed, conflict, conflictPath) = match;
        var remote = pinned?.Skill ?? current;
        var harness = target.Harness;
        if (conflict is not null)
            return new SkillComparison(harness, remote.Name, SkillSyncState.Conflict, remote, conflictPath, null, conflict, target.ProjectName);

        if (installed is null)
            return new SkillComparison(harness, remote.Name, SkillSyncState.NotInstalled, remote, null, null, Project: target.ProjectName);

        // A record only speaks for the registry skill it was synced as: the same name and the same project. A
        // record from before names were kept is trusted only when the local skill's name is exactly the
        // registry's; one from before projects were kept was synced as a global skill. A record of the same skill
        // under the project it was in before a link moved it follows it first.
        var recorded = await MovedSkillLedger.FollowAsync(
            _registry, _ledger, registry, context.AccessToken!, installed.Path, _ledger.Get(registry, installed.Path), current, context.Skills, cancellationToken);
        if (recorded is not null &&
            (!string.Equals(recorded.Name ?? installed.Name, remote.Name, StringComparison.Ordinal) || recorded.ProjectId != remote.ProjectId))
        {
            recorded = null;
        }

        IReadOnlyList<SkillUploadFile> files;
        try
        {
            files = SkillFileReader.Read(installed.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable means it cannot be shown to be safe to overwrite.
            return new SkillComparison(harness, installed.Name, SkillSyncState.LocalChanges, remote, installed.Path, recorded?.Version, Project: target.ProjectName);
        }

        var state = Classify(files, current, recorded);
        if (pinned is not null)
        {
            // Against an earlier version: already there, or safe to replace only when nothing here is unsaved work.
            state = string.Equals(SkillFileReader.RegistryHash(files), pinned.Skill.ContentHash, StringComparison.OrdinalIgnoreCase) ? SkillSyncState.UpToDate
                : state is SkillSyncState.UpToDate or SkillSyncState.UpdateAvailable ? SkillSyncState.UpdateAvailable
                : state;
        }

        return new SkillComparison(harness, installed.Name, state, remote, installed.Path, recorded?.Version, Project: target.ProjectName);
    }

    /// <summary>Decides how local <paramref name="files"/> compare with <paramref name="remote"/>, given the last recorded sync.</summary>
    /// <remarks>
    /// Changed on both sides when the registry refused this exact content as stale, or when the copy differs from what
    /// it last synced and the registry has a newer version than that. A record from before versions were kept cannot
    /// show the registry moved, so such a copy stays <see cref="SkillSyncState.LocalChanges"/>, which is protected alike.
    /// </remarks>
    internal static SkillSyncState Classify(IReadOnlyList<SkillUploadFile> files, RegistrySkill remote, SkillLedgerEntry? recorded)
    {
        if (string.Equals(SkillFileReader.RegistryHash(files), remote.ContentHash, StringComparison.OrdinalIgnoreCase))
            return SkillSyncState.UpToDate;
        if (recorded is null)
            return SkillSyncState.LocalChanges;

        var hash = SkillFileReader.Hash(files);
        if (recorded.RefusedHash == hash)
            return SkillSyncState.ChangedBoth;
        if (recorded.ContentHash == hash)
            return (recorded.Version ?? 0) < remote.Version ? SkillSyncState.UpdateAvailable : SkillSyncState.LocalChanges;
        return recorded.Version is { } synced && synced < remote.Version ? SkillSyncState.ChangedBoth : SkillSyncState.LocalChanges;
    }

    private void RecordInstalled(Uri registry, string path, RegistrySkill remote)
    {
        try
        {
            var hash = SkillFileReader.Hash(SkillFileReader.Read(path));
            var entry = new SkillLedgerEntry(hash, remote.Version, remote.Name, remote.ProjectId);
            if (_ledger.Get(registry, path) != entry)
                _ledger.Record(registry, path, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs a redundant upload later.
        }
    }

    // The registry skills the request names: each name matches exactly when it can, otherwise ignoring case
    // (which can select two skills that differ only by case; they then come back as conflicts).
    private static IReadOnlyList<RegistrySkill> Select(IReadOnlyList<RegistrySkill> skills, IReadOnlyList<string> names, out string? missing)
    {
        missing = null;
        if (names.Count == 0)
            return skills;

        var selected = new List<RegistrySkill>();
        foreach (var name in names)
        {
            var found = skills.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList();
            if (found.Count == 0)
                found = skills.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
            {
                missing = name;
                return [];
            }

            selected.AddRange(found.Where(f => !selected.Contains(f)));
        }

        return selected;
    }

    // Pairs each of `remotes` with the local skill of the target's scope it updates, or with the reason it cannot
    // be pulled. `all` is the registry's full list in that scope, so a name collision is seen even when only one
    // side was asked for. `leftovers` are the local skills no registry skill claimed.
    private static List<Match> MatchAll(Target target, IReadOnlyList<RegistrySkill> remotes, IReadOnlyList<RegistrySkill> all, out List<DiscoveredSkill> leftovers)
    {
        // When two local skills share an exact name the first (directory order) wins, matching the push side.
        var local = new List<DiscoveredSkill>();
        foreach (var skill in target.Local)
        {
            if (!local.Any(l => string.Equals(l.Name, skill.Name, StringComparison.Ordinal)))
                local.Add(skill);
        }

        var claimed = new HashSet<DiscoveredSkill>();
        var matches = new List<Match>();
        foreach (var remote in remotes)
        {
            var exact = local.FirstOrDefault(l => string.Equals(l.Name, remote.Name, StringComparison.Ordinal));
            var twins = all.Where(s => string.Equals(s.Name, remote.Name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).ToList();
            if (twins.Count > 1)
            {
                foreach (var l in local.Where(l => string.Equals(l.Name, remote.Name, StringComparison.OrdinalIgnoreCase)))
                    claimed.Add(l);
                var list = string.Join(", ", twins.Order(StringComparer.Ordinal).Select(n => $"'{n}'"));
                matches.Add(new Match(remote, null,
                    $"The registry has skills named {list}, which differ only by case and would share one folder here. Rename one of them, then pull again.",
                    exact?.Path));
                continue;
            }

            var installed = exact ?? local.FirstOrDefault(l => !claimed.Contains(l) && string.Equals(l.Name, remote.Name, StringComparison.OrdinalIgnoreCase));
            if (installed is not null)
            {
                claimed.Add(installed);
                matches.Add(new Match(remote, installed, null, null));
                continue;
            }

            if (OccupiedFolder(target.SkillsDirectory, remote.Name) is { } occupied)
            {
                matches.Add(new Match(remote, null,
                    $"A folder named '{Path.GetFileName(occupied)}' already exists here and is not this skill, so it was left alone. Move or rename it, then pull again.",
                    occupied));
                continue;
            }

            matches.Add(new Match(remote, null, null, null));
        }

        leftovers = local.Where(l => !claimed.Contains(l)).ToList();
        return matches;
    }

    // An existing entry in the skills folder whose name equals `name` ignoring case, so `FOO` is caught on a
    // case-sensitive file system as well; null when the name is free.
    private static string? OccupiedFolder(string skillsDirectory, string name)
    {
        try
        {
            if (!Directory.Exists(skillsDirectory))
                return null;
            return new DirectoryInfo(skillsDirectory).EnumerateFileSystemInfos()
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot prove the name is free, so treat the folder as taken.
            return Path.Combine(skillsDirectory, name);
        }
    }

    // Resolves the registry, the harnesses, the session, the registry's skill list, and which project the folder
    // is in: every check that can stop a run before it touches anything.
    private async Task<Context> OpenAsync(
        string? harnessId, string? harnessHome, string? registryOption, string? projectStart, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(registryOption);
        }
        catch (FormatException ex)
        {
            return Context.Fail(SkillPullRunFailure.InvalidRegistry, ex.Message, null);
        }

        var targets = new List<Target>();
        if (harnessId is not null)
        {
            var harness = _catalog.ById(harnessId);
            if (harness is null)
                return Context.Fail(SkillPullRunFailure.UnknownHarness, harnessId, registry);
            if (harness is not ISkillCapability)
                return Context.Fail(SkillPullRunFailure.Unsupported, harnessId, registry);
            AddGlobalTarget(harness, harnessHome, targets);
        }
        else
        {
            foreach (var harness in _catalog.All.Where(h => h is ISkillCapability))
                AddGlobalTarget(harness, harnessHome, targets);
        }

        if (targets.Count == 0)
            return Context.Fail(SkillPullRunFailure.NoHarness, harnessId, registry);

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } session })
            return Context.Fail(SkillPullService.Failure(lookup.State), null, registry);

        var meta = await _registry.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return Context.Fail(SkillPullRunFailure.RegistryUnreachable, null, registry);
        if (ApiCompatibility.Check(meta) is { } incompatibility)
            return Context.Fail(SkillPullRunFailure.Incompatible, incompatibility, registry);

        var list = await _registry.ListSkillsAsync(registry, session.AccessToken, cancellationToken);
        if (list.Skills is not { } skills)
            return Context.Fail(SkillPullRunFailure.ListFailed, list.Error, registry);

        Here? here = null;
        if (projectStart is not null)
        {
            here = await ResolveHereAsync(registry, session.AccessToken, projectStart, cancellationToken);
            if (here.Root is { } root)
            {
                foreach (var global in targets.ToList())
                    AddProjectTarget(global, here, root, targets);
            }
        }

        return new Context(SkillPullRunFailure.None, null, registry, targets, skills, session.AccessToken, here, list);
    }

    // Which project the folder is in, and the checkout root its project skills live under: the repo top level
    // of this checkout (so a worktree is its own root), or the folder itself outside git.
    private async Task<Here> ResolveHereAsync(Uri registry, string accessToken, string projectStart, CancellationToken cancellationToken)
    {
        var facts = _facts.Resolve(Path.GetFullPath(projectStart));
        var root = facts.IsGit ? facts.TopLevel ?? facts.Directory : facts.Directory;
        var resolved = await _projects.ResolveProjectAsync(registry, accessToken, _machine.Get().Id, facts, cancellationToken);
        return resolved.Ok
            ? new Here(resolved.Value!.ProjectId, resolved.Value.Name, root, null)
            : new Here(null, null, null, resolved.Error);
    }

    // Why a command that lists the registry's skills stops when there is no current session; shared with SkillChangeService.
    internal static SkillPullRunFailure Failure(SessionState session) => session switch
    {
        SessionState.None => SkillPullRunFailure.NotLoggedIn,
        SessionState.RegistryUnreachable => SkillPullRunFailure.RegistryUnreachable,
        _ => SkillPullRunFailure.SessionExpired,
    };

    private static void AddGlobalTarget(IHarness harness, string? harnessHome, List<Target> targets)
    {
        if (harness is ISkillCapability capability && harness.TryLocate(harnessHome, out var location))
        {
            var local = capability.DiscoverSkills(location, projectStart: null);
            targets.Add(new Target(harness, capability, location, capability.GlobalSkillsDirectory(location), SkillScope.Global, null, null, local));
        }
    }

    // The folder's project skills in one harness. Discovery walks up to the nearest project config, which may be
    // above the checkout (a fresh clone has none of its own yet), so only skills found under this root count.
    // A root whose project skills folder is the harness's global one (the home folder) is not a project.
    private static void AddProjectTarget(Target global, Here here, string root, List<Target> targets)
    {
        var directory = global.Capability.ProjectSkillsDirectory(root);
        if (SamePath(directory, global.SkillsDirectory))
            return;

        var local = global.Capability.DiscoverSkills(global.Location, root)
            .Where(s => s.Scope == SkillScope.Project && s.ProjectRoot is { } r && SamePath(r, root))
            .ToList();
        targets.Add(global with { SkillsDirectory = directory, Scope = SkillScope.Project, ProjectId = here.ProjectId, ProjectName = here.Name, Local = local });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The versions fetched for --version, by the registry skill (current version) they stand in for, or why one could not be.
    private sealed record Pinned(IReadOnlyDictionary<RegistrySkill, PinnedVersion> Versions, string? Error)
    {
        public PinnedVersion? Of(RegistrySkill remote) => Versions.GetValueOrDefault(remote);
    }

    // One fetched version: the registry skill as it was at that version, and its files.
    private sealed record PinnedVersion(RegistrySkill Skill, RegistrySkillContent Content);

    // A registry skill and what it lines up with here: the installed copy, or why it is a conflict and where.
    private sealed record Match(RegistrySkill Remote, DiscoveredSkill? Installed, string? Conflict, string? ConflictPath);

    // Where skills of one scope are compared and installed in one harness. A project target's ProjectId is null
    // when the folder has no project in the registry yet: its local skills are then only here.
    private sealed record Target(
        IHarness Harness,
        ISkillCapability Capability,
        HarnessLocation Location,
        string SkillsDirectory,
        SkillScope Scope,
        Guid? ProjectId,
        string? ProjectName,
        IReadOnlyList<DiscoveredSkill> Local);

    // The project the command's folder is in. Root is null when the registry could not say; Problem says why.
    private sealed record Here(Guid? ProjectId, string? Name, string? Root, string? Problem);

    private sealed record Context(
        SkillPullRunFailure Failure,
        string? Detail,
        Uri? Registry,
        IReadOnlyList<Target> Targets,
        IReadOnlyList<RegistrySkill> Skills,
        string? AccessToken,
        Here? Here = null,
        RegistrySkillList? Listing = null)
    {
        public static Context Fail(SkillPullRunFailure failure, string? detail, Uri? registry) =>
            new(failure, detail, registry, [], [], null);
    }

    // A local copy of a skill the registry deleted or renamed since the copy synced, and whether it was edited here since.
    private sealed record RemovedCopy(DiscoveredSkill Local, SkillLedgerEntry Recorded, RegistryRemovedSkill Removal, bool Edited);
}
