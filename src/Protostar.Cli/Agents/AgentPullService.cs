using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Agents;

/// <summary>Production <see cref="IAgentPullService"/>.</summary>
/// <remarks>
/// <para>
/// Copied from <see cref="Skills.SkillPullService"/> and cut to what a one-file agent needs (ADR 0003: copy the rules, do
/// not share them; a fix to one copy must be checked against the other). A local agent matches a registry agent of its
/// scope by exact <c>name</c>, Claude Code's identity for an agent, so there is no folder-name or letter-case collision to
/// guard against: two agents whose names differ only by case are two agents, in two files.
/// </para>
/// <para>
/// A copy is up to date when its bytes hash to the registry's content hash, or when the ledger says this machine synced
/// exactly these bytes at the registry's current version (a pull of a renamed agent rewrites its <c>name</c> line, see
/// <see cref="AgentInstaller"/>). Otherwise the ledger decides as it does for skills (<see cref="Classify"/>): unedited and
/// older is updated, anything that may be local work is kept.
/// </para>
/// <para>
/// A registry agent not on this machine is installed at the path it was pushed from, relative to the agents folder; when
/// a file is already there it is not this agent (no local agent has its name), so that is a conflict, never written.
/// </para>
/// <para>
/// A local copy whose ledger record names an agent the registry has deleted or renamed since is
/// <see cref="AgentSyncState.RemovedInRegistry"/>. A pull renames an unedited copy of a renamed agent to follow it (its
/// <c>name</c> line only; the file stays where it is, which is where a fresh pull of the agent puts it too) and keeps every
/// other such copy. A full pull settles every removed copy; a pull of named agents follows only renames into those names.
/// </para>
/// <para>Everything that can stop the run is checked before anything is written.</para>
/// </remarks>
internal sealed class AgentPullService : IAgentPullService
{
    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IAgentApi _api;
    private readonly IRegistryCapabilities _capabilities;
    private readonly IAgentLedger _ledger;
    private readonly IProjectFactsResolver _facts;
    private readonly IProjectApi _projects;
    private readonly IMachineIdentity _machine;

    public AgentPullService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IAgentApi api, IRegistryCapabilities capabilities, IAgentLedger ledger,
        IProjectFactsResolver facts, IProjectApi projects, IMachineIdentity machine)
    {
        _catalog = catalog;
        _tokens = tokens;
        _api = api;
        _capabilities = capabilities;
        _ledger = ledger;
        _facts = facts;
        _projects = projects;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<AgentRemoteResult> CompareAsync(AgentRemoteRequest request, CancellationToken cancellationToken)
    {
        var names = request.Names ?? [];
        if (VersionProblem(names, request.Version) is { } versionProblem)
            return AgentRemoteResult.Fail(AgentPullRunFailure.InvalidVersion, versionProblem);

        var context = await OpenAsync(request.HarnessId, request.HarnessHome, request.Registry, request.ProjectStart, cancellationToken);
        if (context.Failure != AgentPullRunFailure.None)
            return AgentRemoteResult.Fail(context.Failure, context.Detail, context.Registry);

        var selected = Select(context.Agents, names, out var missing);
        if (missing is not null)
            return AgentRemoteResult.Fail(AgentPullRunFailure.AgentNotFound, missing, context.Registry);

        var pinned = await PinAsync(context, selected, request.Version, cancellationToken);
        if (pinned.Error is { } pinError)
            return AgentRemoteResult.Fail(AgentPullRunFailure.VersionNotFound, pinError, context.Registry);

        var comparisons = new List<AgentComparison>();
        foreach (var target in context.Targets)
        {
            var matches = MatchAll(target, InScope(target, selected), out var leftovers);
            foreach (var match in matches)
                comparisons.Add(await CompareAsync(target, match, context, pinned.Of(match.Remote), cancellationToken));

            // Local agents no registry agent claims are listed only when every agent is. A copy of an agent deleted or renamed
            // since it synced says so, rather than looking like an agent that was never backed up.
            if (names.Count == 0)
            {
                foreach (var leftover in leftovers)
                {
                    comparisons.Add(RemovedCopyOf(context, target, leftover) is { } removed
                        ? new AgentComparison(target.Harness, leftover.Name!, AgentSyncState.RemovedInRegistry, null, leftover.Path, removed.Recorded.Version,
                            RemovalNote(removed, pulling: false), target.ProjectName)
                        : new AgentComparison(target.Harness, leftover.Name!, AgentSyncState.LocalOnly, null, leftover.Path, null, Project: target.ProjectName));
                }
            }

            if (target.Scope == AgentScope.Personal)
            {
                foreach (var remote in Elsewhere(context, selected))
                    comparisons.Add(new AgentComparison(target.Harness, remote.Name, AgentSyncState.OtherProject, remote, null, null, ElsewhereNote(target, context.Here, remote), remote.ProjectName));
            }
        }

        var ordered = comparisons
            .OrderBy(c => c.Harness.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Project is not null)
            .ThenBy(c => c.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AgentRemoteResult(AgentPullRunFailure.None, null, context.Registry, ordered, request.Version);
    }

    /// <inheritdoc />
    public async Task<AgentPullRunResult> PullAsync(AgentPullRequest request, CancellationToken cancellationToken)
    {
        if (VersionProblem(request.Names, request.Version) is { } versionProblem)
            return AgentPullRunResult.Fail(AgentPullRunFailure.InvalidVersion, versionProblem);

        var context = await OpenAsync(request.HarnessId, request.HarnessHome, request.Registry, request.ProjectStart, cancellationToken);
        if (context.Failure != AgentPullRunFailure.None)
            return AgentPullRunResult.Fail(context.Failure, context.Detail, context.Registry);

        var registry = context.Registry!;
        var selected = Select(context.Agents, request.Names, out var missing);
        if (missing is not null)
            return AgentPullRunResult.Fail(AgentPullRunFailure.AgentNotFound, missing, registry);

        var pinned = await PinAsync(context, selected, request.Version, cancellationToken);
        if (pinned.Error is { } pinError)
            return AgentPullRunResult.Fail(AgentPullRunFailure.VersionNotFound, pinError, registry);

        var entries = new List<AgentPullEntry>();
        foreach (var each in context.Targets)
        {
            var target = each;
            var inScope = InScope(target, selected);
            var matches = MatchAll(target, inScope, out var leftovers);

            // Settle copies of agents removed from the registry: an unedited copy of a renamed agent follows the rename, so
            // the registry's agent then matches it under its new name; on a full pull anything else is kept and reported.
            var local = target.Local.ToList();
            foreach (var leftover in leftovers)
            {
                if (RemovedCopyOf(context, target, leftover) is not { } removed)
                    continue;
                var follows = removed.Removal.RenamedTo is { } newName && inScope.Any(r => string.Equals(r.Name, newName, StringComparison.Ordinal));
                if (request.Names.Count > 0 && !follows)
                    continue;
                var (entry, renamed) = SettleRemoved(target, removed, registry);
                entries.Add(entry);
                if (renamed is not null)
                    local[local.IndexOf(leftover)] = renamed;
            }

            if (!local.SequenceEqual(target.Local))
            {
                target = target with { Local = local };
                matches = MatchAll(target, inScope, out _);
            }

            foreach (var match in matches)
            {
                var version = pinned.Of(match.Remote);
                var comparison = await CompareAsync(target, match, context, version, cancellationToken);
                entries.Add(await PullOneAsync(target, comparison, registry, context.AccessToken!, request.Force, version?.Content, match.Remote.Version, cancellationToken));
            }

            if (target.Scope == AgentScope.Personal)
            {
                foreach (var remote in Elsewhere(context, selected))
                {
                    entries.Add(new AgentPullEntry(target.Harness, remote.Name, AgentPullStatus.OtherProject, remote.Version,
                        WouldGo(target, remote), ElsewhereNote(target, context.Here, remote), remote.ProjectName));
                }
            }
        }

        entries = entries
            .OrderBy(e => e.Harness.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Project is not null)
            .ThenBy(e => e.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AgentPullRunResult(AgentPullRunFailure.None, null, registry, entries, request.Version);
    }

    /// <summary>Decides how a local copy whose bytes hash to <paramref name="hash"/> compares with <paramref name="remote"/>, given the last recorded sync.</summary>
    /// <remarks>
    /// <paramref name="recorded"/> is only passed when it was synced as this agent (same name and project). The rules are
    /// <see cref="Skills.SkillPullService.Classify"/>'s, plus one: a copy unedited since it synced at the registry's current
    /// version is up to date even when its bytes differ from the registry's, because a pull wrote it with the agent's
    /// current name in its <c>name</c> line (a renamed agent's stored file keeps its old name).
    /// </remarks>
    internal static AgentSyncState Classify(string hash, RegistryAgent remote, AgentLedgerEntry? recorded)
    {
        if (string.Equals(hash, remote.ContentHash, StringComparison.OrdinalIgnoreCase))
            return AgentSyncState.UpToDate;
        if (recorded is null)
            return AgentSyncState.LocalChanges;
        if (recorded.Version >= 1 && recorded.Version < remote.FirstVersion)
            return AgentSyncState.RemovedInRegistry;
        if (recorded.RefusedHash == hash)
            return AgentSyncState.ChangedBoth;
        if (recorded.ContentHash == hash)
        {
            return recorded.Version < remote.Version ? AgentSyncState.UpdateAvailable
                : recorded.Version == remote.Version ? AgentSyncState.UpToDate
                : AgentSyncState.LocalChanges;
        }

        return recorded.Version >= 1 && recorded.Version < remote.Version ? AgentSyncState.ChangedBoth : AgentSyncState.LocalChanges;
    }

    private static string? VersionProblem(IReadOnlyList<string> names, int? version) => version switch
    {
        null => null,
        < 1 => $"--version takes a version number (1 or more), not {version}.",
        _ when names.Count != 1 => "--version picks a version of one agent: name exactly one agent.",
        _ => null,
    };

    // With --version, fetches that version of each selected agent this folder can pull, to compare and install in place of
    // the current one. Other projects' agents are only listed, so they are not fetched.
    private async Task<Pinned> PinAsync(Context context, IReadOnlyList<RegistryAgent> selected, int? version, CancellationToken cancellationToken)
    {
        var pinned = new Dictionary<RegistryAgent, PinnedVersion>();
        if (version is not { } wanted)
            return new Pinned(pinned, null);

        foreach (var remote in selected.Where(a => context.Targets.Any(t => Covers(t, a))))
        {
            var download = await _api.GetAgentContentAsync(context.Registry!, context.AccessToken!, remote.Name, remote.ProjectId, wanted, cancellationToken);
            if (download.Value is not { } content || content.Version != wanted)
                return new Pinned(pinned, download.Error ?? $"The registry did not send version {wanted} of '{remote.Name}'.");
            pinned[remote] = new PinnedVersion(remote with { Version = wanted, ContentHash = content.ContentHash, Path = content.Path }, content);
        }

        return new Pinned(pinned, null);
    }

    private async Task<AgentPullEntry> PullOneAsync(
        Target target, AgentComparison comparison, Uri registry, string accessToken, bool force, RemoteAgentContent? pinned, int currentVersion,
        CancellationToken cancellationToken)
    {
        var remote = comparison.Remote!;
        var harness = target.Harness;
        var project = comparison.Project;
        AgentPullEntry Entry(AgentPullStatus status, string path, string? message = null, int? version = null) =>
            new(harness, remote.Name, status, version ?? remote.Version, path, message, project);

        switch (comparison.State)
        {
            case AgentSyncState.Conflict:
                return Entry(AgentPullStatus.Conflict, comparison.LocalPath ?? target.AgentsDirectory, comparison.Note);
            case AgentSyncState.UpToDate:
                // Make sure the ledger knows, so the capture hook treats this copy as already backed up.
                RecordInstalled(registry, comparison.LocalPath!, remote);
                return Entry(AgentPullStatus.UpToDate, comparison.LocalPath!);
            case AgentSyncState.LocalChanges when !force:
                return Entry(AgentPullStatus.LocalChanges, comparison.LocalPath!);
            case AgentSyncState.ChangedBoth when !force:
                return Entry(AgentPullStatus.ChangedBoth, comparison.LocalPath!);
            case AgentSyncState.RemovedInRegistry when !force:
                return Entry(AgentPullStatus.RemovedInRegistry, comparison.LocalPath!, comparison.Note);
        }

        var content = pinned;
        if (content is null)
        {
            var download = await _api.GetAgentContentAsync(registry, accessToken, remote.Name, remote.ProjectId, null, cancellationToken);
            if (download.Value is not { } fetched)
                return Entry(AgentPullStatus.Failed, comparison.LocalPath ?? target.AgentsDirectory, download.Error);
            content = fetched;
        }

        var path = comparison.LocalPath ?? AgentInstaller.PathFor(target.AgentsDirectory, content.Path);
        if (path is null)
            return Entry(AgentPullStatus.Failed, target.AgentsDirectory, $"The registry's path for '{remote.Name}', '{content.Path}', cannot be written here.", content.Version);
        if (comparison.LocalPath is null && Occupied(path))
            return Entry(AgentPullStatus.Conflict, path, OccupiedNote(path), content.Version);

        byte[] written;
        try
        {
            written = AgentInstaller.Install(target.AgentsDirectory, path, content, remote.Name);
        }
        catch (Exception ex) when (ex is AgentInstallException or IOException or UnauthorizedAccessException)
        {
            return Entry(AgentPullStatus.Failed, path, ex.Message, content.Version);
        }

        _ledger.Record(registry, path, new AgentLedgerEntry(AgentPushService.Hash(written), content.Version, remote.Name, remote.ProjectId));

        var note = comparison.State switch
        {
            AgentSyncState.LocalChanges or AgentSyncState.ChangedBoth => "Local changes were overwritten (--force).",
            AgentSyncState.RemovedInRegistry => "The copy of the earlier, deleted agent of this name was overwritten (--force).",
            _ => null,
        };
        if (content.Version < currentVersion)
        {
            note = (note is null ? "" : note + " ") +
                $"A plain `protostar agents pull` would replace it with the current version; `protostar agents push {Quote(remote.Name)} --force` makes it current.";
        }

        return Entry(comparison.LocalPath is null ? AgentPullStatus.Installed : AgentPullStatus.Updated, path, note, content.Version);
    }

    private async Task<AgentComparison> CompareAsync(Target target, Match match, Context context, PinnedVersion? pinned, CancellationToken cancellationToken)
    {
        var registry = context.Registry!;
        var (current, installed) = match;
        var remote = pinned?.Agent ?? current;
        var harness = target.Harness;
        var project = target.ProjectName;

        if (installed is null)
        {
            if (remote.Path is not { } relative)
                return new AgentComparison(harness, remote.Name, AgentSyncState.NotInstalled, remote, null, null, Project: project);
            if (AgentInstaller.PathFor(target.AgentsDirectory, relative) is not { } path)
                return new AgentComparison(harness, remote.Name, AgentSyncState.Conflict, remote, null, null, $"The registry's path '{relative}' cannot be written here.", project);
            if (!Occupied(path))
                return new AgentComparison(harness, remote.Name, AgentSyncState.NotInstalled, remote, null, null, Project: project);

            // The file there may be an unedited copy of this agent from before a rename, which a pull renames to follow
            // before it compares: then this agent is here already, only under its old name.
            var renamed = target.Local
                .Where(l => SamePath(l.Path, path))
                .Select(l => RemovedCopyOf(context, target, l))
                .FirstOrDefault(r => r is { Edited: false } && r.Removal.RenamedTo == current.Name);
            return renamed is null
                ? new AgentComparison(harness, remote.Name, AgentSyncState.Conflict, remote, path, null, OccupiedNote(path), project)
                : new AgentComparison(harness, remote.Name, AgentSyncState.UpdateAvailable, remote, path, renamed.Recorded.Version,
                    $"The copy here is named '{renamed.Local.Name}'; `protostar agents pull` renames it to match.", project);
        }

        // A record only speaks for the registry agent it was synced as: the same name and the same project. A record of the
        // same agent under the project a link moved it out of follows it first.
        var recorded = await MovedAgentLedger.FollowAsync(
            _api, _ledger, registry, context.AccessToken!, installed.Path, _ledger.Get(registry, installed.Path), current, context.Agents, cancellationToken);
        if (recorded is not null && (!string.Equals(recorded.Name, current.Name, StringComparison.Ordinal) || recorded.ProjectId != current.ProjectId))
            recorded = null;

        // Unreadable means it cannot be shown to be safe to overwrite.
        if (HashOrNull(installed.Path) is not { } hash)
            return new AgentComparison(harness, installed.Name!, AgentSyncState.LocalChanges, remote, installed.Path, recorded?.Version, Project: project);

        var state = Classify(hash, current, recorded);
        if (pinned is not null)
        {
            // Against an earlier version: already there, or safe to replace only when nothing here is unsaved work.
            var matchesPinned = string.Equals(hash, pinned.Agent.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                recorded is not null && recorded.ContentHash == hash && recorded.Version == pinned.Agent.Version;
            state = matchesPinned ? AgentSyncState.UpToDate
                : state is AgentSyncState.UpToDate or AgentSyncState.UpdateAvailable ? AgentSyncState.UpdateAvailable
                : state;
        }

        var note = state == AgentSyncState.RemovedInRegistry
            ? $"This copy is of an earlier agent named '{current.Name}' (synced at v{recorded!.Version}), deleted from the registry since; the registry's " +
              $"'{current.Name}' is a new agent that took the name. Run `protostar agents pull {Quote(current.Name)} --force` to replace this copy with it."
            : null;
        return new AgentComparison(harness, installed.Name!, state, remote, installed.Path, recorded?.Version, note, project);
    }

    // The removal behind a local copy no registry agent claims: the ledger says it synced as a registry agent of this
    // target's scope, and the registry says that agent was deleted or renamed after the version the copy synced.
    private RemovedCopy? RemovedCopyOf(Context context, Target target, DiscoveredAgent local)
    {
        if (context.Listing is not { } listing || _ledger.Get(context.Registry!, local.Path) is not { } recorded)
            return null;
        if (recorded.ProjectId is null != (target.Scope == AgentScope.Personal))
            return null;
        if (listing.RemovalOf(recorded.Name, recorded.ProjectId, recorded.Version) is not { } removal)
            return null;

        return new RemovedCopy(local, recorded, removal, HashOrNull(local.Path) != recorded.ContentHash);
    }

    // Follows a rename for an unedited copy by setting its name line, and returns the copy under its new name; reports anything
    // else as kept. The file is not moved: the registry keeps the path it was pushed from, so this machine and a fresh pull
    // elsewhere agree on where the agent lives. Never deletes a local file.
    private (AgentPullEntry Entry, DiscoveredAgent? Renamed) SettleRemoved(Target target, RemovedCopy removed, Uri registry)
    {
        var (local, recorded, removal, edited) = removed;
        AgentPullEntry Kept(string note) =>
            new(target.Harness, local.Name!, AgentPullStatus.RemovedInRegistry, recorded.Version, local.Path, note, target.ProjectName);

        if (removal.RenamedTo is not { } newName || edited)
            return (Kept(RemovalNote(removed, pulling: true)), null);

        if (target.Local.Any(l => string.Equals(l.Name, newName, StringComparison.Ordinal)))
        {
            return (Kept($"Renamed to '{newName}' in the registry (at v{removal.LastVersion}), but an agent named '{newName}' is already here, so this copy was left as it is. " +
                "It has no changes of its own since it last synced: remove it if you no longer need it."), null);
        }

        byte[] renamed;
        try
        {
            renamed = AgentInstaller.Named(File.ReadAllBytes(local.Path), newName);
            AgentInstaller.Write(local.Path, renamed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Kept($"Renamed to '{newName}' in the registry (at v{removal.LastVersion}), but this copy could not be renamed to match: {ex.Message}"), null);
        }

        // Still the version it last synced, now under the new name, so the registry's agent of that name matches it as an
        // unedited copy: up to date, or an update available.
        _ledger.Record(registry, local.Path, recorded with { Name = newName, ContentHash = AgentPushService.Hash(renamed) });
        return (new AgentPullEntry(target.Harness, local.Name!, AgentPullStatus.FollowedRename, recorded.Version, local.Path,
                $"Renamed here to '{newName}' (its name line; the file stays where it is), following the rename in the registry.", target.ProjectName),
            local with { Name = newName });
    }

    /// <summary>What happened to a removed agent this copy came from, and the ways out, as remote and pull say it.</summary>
    private static string RemovalNote(RemovedCopy removed, bool pulling)
    {
        var (local, _, removal, edited) = removed;
        var quoted = Quote(local.Name!);
        if (removal.RenamedTo is not { } newName)
        {
            var why = edited ? "This copy has changes made here and is kept" : "This copy is kept";
            return $"Deleted from the registry (at v{removal.LastVersion}) after this copy last synced. {why}: remove it if you no longer want it, " +
                $"or run `protostar agents push {quoted} --force` to restore it.";
        }

        if (!edited)
            return $"Renamed to '{newName}' in the registry (at v{removal.LastVersion}). `protostar agents pull` renames this copy to match.";
        return $"Renamed to '{newName}' in the registry (at v{removal.LastVersion}), but this copy has changes made here, so it " +
            (pulling ? "was not renamed" : "will not be renamed") +
            $". Move them into '{newName}', or run `protostar agents push {quoted} --force` to keep this as a separate agent named '{local.Name}'.";
    }

    private static string? HashOrNull(string path)
    {
        try
        {
            return AgentPushService.Hash(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Occupied(string path) => File.Exists(path) || Directory.Exists(path);

    private static string OccupiedNote(string path) =>
        $"'{path}' already exists and is not this agent (no agent here has its name), so it was left alone. Move or rename it, then pull again.";

    private static string Quote(string name) => name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;

    private static List<RegistryAgent> InScope(Target target, IReadOnlyList<RegistryAgent> agents) => agents.Where(a => Covers(target, a)).ToList();

    private static bool Covers(Target target, RegistryAgent agent) =>
        target.Scope == AgentScope.Personal ? agent.ProjectId is null : target.ProjectId is { } id && agent.ProjectId == id;

    // Project agents no project target here covers (another project's, as a rule): listed, never written. A run for personal
    // agents only (no project folder, as setup and --personal-only) leaves project agents out altogether.
    private static IEnumerable<RegistryAgent> Elsewhere(Context context, IReadOnlyList<RegistryAgent> agents) =>
        context.Here is null ? [] : agents.Where(a => a.ProjectId is not null && !context.Targets.Any(t => t.Scope == AgentScope.Project && Covers(t, a)));

    private static string WouldGo(Target target, RegistryAgent remote) =>
        Path.Combine(target.Capability.ProjectAgentsDirectory($"<checkout of {remote.ProjectName}>"), remote.Path ?? $"{remote.Name}.md");

    private static string ElsewhereNote(Target target, Here? here, RegistryAgent remote)
    {
        var whose = $"Belongs to project {remote.ProjectName}";
        var go = $"pull it from a checkout of that project, which installs it into {WouldGo(target, remote)}";
        if (here?.Problem is { } problem)
            return $"{whose}; could not tell which project this folder is in ({problem}), so {go}.";
        if (here?.ProjectId is { } id && id == remote.ProjectId)
            return $"{whose}, this folder's, but this folder's project agents folder is the personal agents folder, so it is not pulled here; {go}.";
        return $"{whose}, not this folder's; {go}.";
    }

    private void RecordInstalled(Uri registry, string path, RegistryAgent remote)
    {
        if (HashOrNull(path) is not { } hash)
            return;
        var entry = new AgentLedgerEntry(hash, remote.Version, remote.Name, remote.ProjectId);
        if (_ledger.Get(registry, path) != entry)
            _ledger.Record(registry, path, entry);
    }

    // The registry agents the request names: each name matches exactly when it can, otherwise ignoring case.
    private static IReadOnlyList<RegistryAgent> Select(IReadOnlyList<RegistryAgent> agents, IReadOnlyList<string> names, out string? missing)
    {
        missing = null;
        if (names.Count == 0)
            return agents;

        var selected = new List<RegistryAgent>();
        foreach (var name in names)
        {
            var found = agents.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
            if (found.Count == 0)
                found = agents.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
            {
                missing = name;
                return [];
            }

            selected.AddRange(found.Where(f => !selected.Contains(f)));
        }

        return selected;
    }

    // Pairs each of `remotes` with the local agent of the target's scope that has its exact name, if any. When two local
    // files share a name the first (path order) is the agent, as for push. `leftovers` are local agents no remote claimed.
    private static List<Match> MatchAll(Target target, IReadOnlyList<RegistryAgent> remotes, out List<DiscoveredAgent> leftovers)
    {
        var local = new List<DiscoveredAgent>();
        foreach (var agent in target.Local)
        {
            if (!local.Any(l => string.Equals(l.Name, agent.Name, StringComparison.Ordinal)))
                local.Add(agent);
        }

        var matches = remotes.Select(r => new Match(r, local.FirstOrDefault(l => string.Equals(l.Name, r.Name, StringComparison.Ordinal)))).ToList();
        leftovers = local.Where(l => !matches.Any(m => m.Installed == l)).ToList();
        return matches;
    }

    // Resolves the registry, the harnesses, the session, the registry's agent list, and which project the folder is in:
    // every check that can stop a run before it touches anything.
    private async Task<Context> OpenAsync(string? harnessId, string? harnessHome, string? registryOption, string? projectStart, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(registryOption);
        }
        catch (FormatException ex)
        {
            return Context.Fail(AgentPullRunFailure.InvalidRegistry, ex.Message, null);
        }

        var targets = new List<Target>();
        if (harnessId is not null)
        {
            var harness = _catalog.ById(harnessId);
            if (harness is null)
                return Context.Fail(AgentPullRunFailure.UnknownHarness, harnessId, registry);
            if (harness is not IAgentCapability)
                return Context.Fail(AgentPullRunFailure.Unsupported, harnessId, registry);
            AddPersonalTarget(harness, harnessHome, targets);
        }
        else
        {
            foreach (var harness in _catalog.All.Where(h => h is IAgentCapability))
                AddPersonalTarget(harness, harnessHome, targets);
        }

        if (targets.Count == 0)
            return Context.Fail(AgentPullRunFailure.NoHarness, harnessId, registry);

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } session })
        {
            var failure = lookup.State switch
            {
                SessionState.None => AgentPullRunFailure.NotLoggedIn,
                SessionState.RegistryUnreachable => AgentPullRunFailure.RegistryUnreachable,
                _ => AgentPullRunFailure.SessionExpired,
            };
            return Context.Fail(failure, null, registry);
        }

        var check = projectStart is null
            ? await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Agents, RegistryCapability.AgentContent)
            : await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Agents, RegistryCapability.AgentContent, RegistryCapability.Projects);
        if (!check.Ok)
        {
            var failure = check.Failure == RegistryCheckFailure.Unreachable ? AgentPullRunFailure.RegistryUnreachable
                : check.Missing is not null ? AgentPullRunFailure.RegistryTooOld
                : AgentPullRunFailure.Incompatible;
            return Context.Fail(failure, check.Detail, registry);
        }

        var list = await _api.ListAgentsAsync(registry, session.AccessToken, cancellationToken);
        if (list.Agents is not { } agents)
            return Context.Fail(AgentPullRunFailure.ListFailed, list.Error, registry);

        Here? here = null;
        if (projectStart is not null)
        {
            here = await ResolveHereAsync(registry, session.AccessToken, projectStart, cancellationToken);
            if (here.Root is { } root)
            {
                foreach (var personal in targets.ToList())
                    AddProjectTarget(personal, here, root, targets);
            }
        }

        return new Context(AgentPullRunFailure.None, null, registry, targets, agents, session.AccessToken, here, list);
    }

    // Which project the folder is in, and the checkout root its project agents live under: the repo top level of this
    // checkout (so a worktree is its own root), or the folder itself outside git. The same resolution project skills use.
    private async Task<Here> ResolveHereAsync(Uri registry, string accessToken, string projectStart, CancellationToken cancellationToken)
    {
        var facts = _facts.Resolve(Path.GetFullPath(projectStart));
        var root = facts.IsGit ? facts.TopLevel ?? facts.Directory : facts.Directory;
        var resolved = await _projects.ResolveProjectAsync(registry, accessToken, _machine.Get().Id, facts, cancellationToken);
        return resolved.Ok
            ? new Here(resolved.Value!.ProjectId, resolved.Value.Name, root, null)
            : new Here(null, null, null, resolved.Error);
    }

    private static void AddPersonalTarget(IHarness harness, string? harnessHome, List<Target> targets)
    {
        if (harness is IAgentCapability capability && harness.TryLocate(harnessHome, out var location))
        {
            var local = capability.DiscoverAgents(location, projectStart: null).Where(a => a.Name is not null).ToList();
            targets.Add(new Target(harness, capability, location, capability.GlobalAgentsDirectory(location), AgentScope.Personal, null, null, local));
        }
    }

    // The folder's project agents in one harness. Discovery walks up to the nearest project config, which may be above the
    // checkout (a fresh clone has none of its own yet), so only agents found under this root count. A root whose project
    // agents folder is the personal one (the home folder) is not a project.
    private static void AddProjectTarget(Target personal, Here here, string root, List<Target> targets)
    {
        var directory = personal.Capability.ProjectAgentsDirectory(root);
        if (SamePath(directory, personal.AgentsDirectory))
            return;

        var local = personal.Capability.DiscoverAgents(personal.Location, root)
            .Where(a => a.Scope == AgentScope.Project && a.Name is not null && a.ProjectRoot is { } r && SamePath(r, root))
            .ToList();
        targets.Add(personal with { AgentsDirectory = directory, Scope = AgentScope.Project, ProjectId = here.ProjectId, ProjectName = here.Name, Local = local });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The versions fetched for --version, by the registry agent (current version) they stand in for, or why one could not be.
    private sealed record Pinned(IReadOnlyDictionary<RegistryAgent, PinnedVersion> Versions, string? Error)
    {
        public PinnedVersion? Of(RegistryAgent remote) => Versions.GetValueOrDefault(remote);
    }

    // One fetched version: the registry agent as it was at that version, and its file.
    private sealed record PinnedVersion(RegistryAgent Agent, RemoteAgentContent Content);

    // A registry agent and the local copy of its name in the target's scope, if any.
    private sealed record Match(RegistryAgent Remote, DiscoveredAgent? Installed);

    // Where agents of one scope are compared and installed in one harness. A project target's ProjectId is null when the
    // folder has no project in the registry yet.
    private sealed record Target(
        IHarness Harness, IAgentCapability Capability, HarnessLocation Location, string AgentsDirectory, AgentScope Scope, Guid? ProjectId, string? ProjectName,
        IReadOnlyList<DiscoveredAgent> Local);

    // The project the command's folder is in. Root is null when the registry could not say; Problem says why.
    private sealed record Here(Guid? ProjectId, string? Name, string? Root, string? Problem);

    private sealed record Context(
        AgentPullRunFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<Target> Targets, IReadOnlyList<RegistryAgent> Agents, string? AccessToken,
        Here? Here = null, RegistryAgentList? Listing = null)
    {
        public static Context Fail(AgentPullRunFailure failure, string? detail, Uri? registry) => new(failure, detail, registry, [], [], null);
    }

    // A local copy of an agent the registry deleted or renamed since the copy synced, and whether it was edited here since.
    private sealed record RemovedCopy(DiscoveredAgent Local, AgentLedgerEntry Recorded, RegistryRemovedAgent Removal, bool Edited);
}
