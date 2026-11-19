using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

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
/// difference may be local work and is protected.
/// </para>
/// <para>
/// Two cases are conflicts that are never written, even with <c>--force</c>. A registry skill with no local
/// match whose folder name is already taken (by a skill with another name, a folder with no SKILL.md, or
/// the same name in another case) would overwrite something that is not this skill. Two registry skills
/// whose names differ only by case would share one folder, so neither is pulled rather than guessing.
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

    public SkillPullService(IHarnessCatalog catalog, IAccessTokenProvider tokens, IRegistryApi registry, ISkillLedger ledger)
    {
        _catalog = catalog;
        _tokens = tokens;
        _registry = registry;
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async Task<SkillRemoteResult> CompareAsync(SkillRemoteRequest request, CancellationToken cancellationToken)
    {
        var context = await OpenAsync(request.HarnessId, request.HarnessHome, request.Registry, cancellationToken);
        if (context.Failure != SkillPullRunFailure.None)
            return SkillRemoteResult.Fail(context.Failure, context.Detail, context.Registry);

        var comparisons = new List<SkillComparison>();
        foreach (var target in context.Targets)
        {
            var matches = MatchAll(target, context.Skills, context.Skills, out var leftovers);
            foreach (var match in matches)
                comparisons.Add(Compare(target, match, context.Registry!));

            foreach (var leftover in leftovers)
                comparisons.Add(new SkillComparison(target.Harness, leftover.Name, SkillSyncState.LocalOnly, null, leftover.Path, null));
        }

        var ordered = comparisons
            .OrderBy(c => c.Harness.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SkillRemoteResult(SkillPullRunFailure.None, null, context.Registry, ordered);
    }

    /// <inheritdoc />
    public async Task<SkillPullRunResult> PullAsync(SkillPullRequest request, CancellationToken cancellationToken)
    {
        var context = await OpenAsync(request.HarnessId, request.HarnessHome, request.Registry, cancellationToken);
        if (context.Failure != SkillPullRunFailure.None)
            return SkillPullRunResult.Fail(context.Failure, context.Detail, context.Registry);

        var registry = context.Registry!;
        var selected = Select(context.Skills, request.Names, out var missing);
        if (missing is not null)
            return SkillPullRunResult.Fail(SkillPullRunFailure.SkillNotFound, missing, registry);

        var entries = new List<SkillPullEntry>();
        foreach (var target in context.Targets)
        {
            foreach (var match in MatchAll(target, selected, context.Skills, out _))
            {
                var comparison = Compare(target, match, registry);
                entries.Add(await PullOneAsync(target, comparison, registry, context.AccessToken!, request.Force, cancellationToken));
            }
        }

        return new SkillPullRunResult(SkillPullRunFailure.None, null, registry, entries);
    }

    private async Task<SkillPullEntry> PullOneAsync(
        Target target, SkillComparison comparison, Uri registry, string accessToken, bool force, CancellationToken cancellationToken)
    {
        var remote = comparison.Remote!;
        var harness = target.Harness;

        if (comparison.State == SkillSyncState.Conflict)
        {
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Conflict, remote.Version,
                comparison.LocalPath ?? target.SkillsDirectory, comparison.Note);
        }

        if (comparison.State == SkillSyncState.UpToDate)
        {
            // Make sure the ledger knows, so the capture hook treats this copy as already backed up.
            RecordInstalled(registry, comparison.LocalPath!, remote);
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.UpToDate, remote.Version, comparison.LocalPath!, null);
        }

        if (comparison.State == SkillSyncState.LocalChanges && !force)
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.LocalChanges, remote.Version, comparison.LocalPath!, null);

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
                $"'{remote.Name}' cannot be used as a folder name on this machine.");
        }

        var download = await _registry.GetSkillFilesAsync(registry, accessToken, remote.Name, null, cancellationToken);
        if (download.Content is not { } content)
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Failed, remote.Version, path, download.Error);

        IReadOnlyList<SkillUploadFile> written;
        try
        {
            written = SkillInstaller.Install(path, content);
        }
        catch (Exception ex) when (ex is SkillInstallException or IOException or UnauthorizedAccessException)
        {
            return new SkillPullEntry(harness, remote.Name, SkillPullStatus.Failed, content.Version, path, ex.Message);
        }

        _ledger.Record(registry, path, new SkillLedgerEntry(SkillFileReader.Hash(written), content.Version, remote.Name));

        var status = comparison.LocalPath is null ? SkillPullStatus.Installed : SkillPullStatus.Updated;
        var note = comparison.State == SkillSyncState.LocalChanges ? "Local changes were overwritten (--force)." : null;
        return new SkillPullEntry(harness, remote.Name, status, content.Version, path, note);
    }

    private SkillComparison Compare(Target target, Match match, Uri registry)
    {
        var (remote, installed, conflict, conflictPath) = match;
        var harness = target.Harness;
        if (conflict is not null)
            return new SkillComparison(harness, remote.Name, SkillSyncState.Conflict, remote, conflictPath, null, conflict);

        if (installed is null)
            return new SkillComparison(harness, remote.Name, SkillSyncState.NotInstalled, remote, null, null);

        // A record only speaks for the registry skill it was synced as. A record from before names were kept
        // is trusted only when the local skill's name is exactly the registry's.
        var recorded = _ledger.Get(registry, installed.Path);
        if (recorded is not null && !string.Equals(recorded.Name ?? installed.Name, remote.Name, StringComparison.Ordinal))
            recorded = null;

        IReadOnlyList<SkillUploadFile> files;
        try
        {
            files = SkillFileReader.Read(installed.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable means it cannot be shown to be safe to overwrite.
            return new SkillComparison(harness, installed.Name, SkillSyncState.LocalChanges, remote, installed.Path, recorded?.Version);
        }

        var state = Classify(files, remote, recorded);
        return new SkillComparison(harness, installed.Name, state, remote, installed.Path, recorded?.Version);
    }

    /// <summary>Decides how local <paramref name="files"/> compare with <paramref name="remote"/>, given the last recorded sync.</summary>
    internal static SkillSyncState Classify(IReadOnlyList<SkillUploadFile> files, RegistrySkill remote, SkillLedgerEntry? recorded)
    {
        if (string.Equals(SkillFileReader.RegistryHash(files), remote.ContentHash, StringComparison.OrdinalIgnoreCase))
            return SkillSyncState.UpToDate;

        return recorded is not null && recorded.ContentHash == SkillFileReader.Hash(files) && (recorded.Version ?? 0) < remote.Version
            ? SkillSyncState.UpdateAvailable
            : SkillSyncState.LocalChanges;
    }

    private void RecordInstalled(Uri registry, string path, RegistrySkill remote)
    {
        try
        {
            var hash = SkillFileReader.Hash(SkillFileReader.Read(path));
            var entry = new SkillLedgerEntry(hash, remote.Version, remote.Name);
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

    // Pairs each of `remotes` with the local global skill it updates, or with the reason it cannot be pulled.
    // `all` is the registry's full list, so a name collision is seen even when only one side was asked for.
    // `leftovers` are the local skills no registry skill claimed.
    private static List<Match> MatchAll(Target target, IReadOnlyList<RegistrySkill> remotes, IReadOnlyList<RegistrySkill> all, out List<DiscoveredSkill> leftovers)
    {
        // When two local skills share an exact name the first (directory order) wins, matching the push side.
        var local = new List<DiscoveredSkill>();
        foreach (var skill in target.Capability.DiscoverSkills(target.Location, projectStart: null))
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

    // Resolves the registry, the harnesses, the session, and the registry's skill list: every check that can
    // stop a run before it touches anything.
    private async Task<Context> OpenAsync(string? harnessId, string? harnessHome, string? registryOption, CancellationToken cancellationToken)
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
            AddTarget(harness, harnessHome, targets);
        }
        else
        {
            foreach (var harness in _catalog.All.Where(h => h is ISkillCapability))
                AddTarget(harness, harnessHome, targets);
        }

        if (targets.Count == 0)
            return Context.Fail(SkillPullRunFailure.NoHarness, harnessId, registry);

        var session = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (session is null)
            return Context.Fail(SkillPullRunFailure.NotLoggedIn, null, registry);
        if (session.IsExpired)
            return Context.Fail(SkillPullRunFailure.SessionExpired, null, registry);

        var meta = await _registry.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return Context.Fail(SkillPullRunFailure.RegistryUnreachable, null, registry);
        if (ApiCompatibility.Check(meta) is { } incompatibility)
            return Context.Fail(SkillPullRunFailure.Incompatible, incompatibility, registry);

        var list = await _registry.ListSkillsAsync(registry, session.AccessToken, cancellationToken);
        if (list.Skills is not { } skills)
            return Context.Fail(SkillPullRunFailure.ListFailed, list.Error, registry);

        return new Context(SkillPullRunFailure.None, null, registry, targets, skills, session.AccessToken);
    }

    private static void AddTarget(IHarness harness, string? harnessHome, List<Target> targets)
    {
        if (harness is ISkillCapability capability && harness.TryLocate(harnessHome, out var location))
            targets.Add(new Target(harness, capability, location, capability.GlobalSkillsDirectory(location)));
    }

    // A registry skill and what it lines up with here: the installed copy, or why it is a conflict and where.
    private sealed record Match(RegistrySkill Remote, DiscoveredSkill? Installed, string? Conflict, string? ConflictPath);

    private sealed record Target(IHarness Harness, ISkillCapability Capability, HarnessLocation Location, string SkillsDirectory);

    private sealed record Context(
        SkillPullRunFailure Failure,
        string? Detail,
        Uri? Registry,
        IReadOnlyList<Target> Targets,
        IReadOnlyList<RegistrySkill> Skills,
        string? AccessToken)
    {
        public static Context Fail(SkillPullRunFailure failure, string? detail, Uri? registry) =>
            new(failure, detail, registry, [], [], null);
    }
}
