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
/// A local skill matches a registry skill by name, ignoring case. It is up to date when its files hash to
/// the registry's content hash. Otherwise the ledger decides: if the local files still match what was last
/// pushed or pulled, nobody edited them here and the registry simply moved on (an update is available);
/// if they do not, or there is no record, the difference may be local work and is protected.
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
            var local = LocalByName(target);
            foreach (var remote in context.Skills)
            {
                local.Remove(remote.Name, out var installed);
                comparisons.Add(Compare(target.Harness, remote, installed, context.Registry!));
            }

            foreach (var leftover in local.Values)
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
        var byName = context.Skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        if (request.Names.FirstOrDefault(n => !byName.ContainsKey(n)) is { } missing)
            return SkillPullRunResult.Fail(SkillPullRunFailure.SkillNotFound, missing, registry);

        var selected = request.Names.Count == 0
            ? context.Skills
            : request.Names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => byName[n]).ToList();

        var entries = new List<SkillPullEntry>();
        foreach (var target in context.Targets)
        {
            var local = LocalByName(target);
            foreach (var remote in selected)
            {
                local.TryGetValue(remote.Name, out var installed);
                var comparison = Compare(target.Harness, remote, installed, registry);
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

        if (comparison.State == SkillSyncState.UpToDate)
        {
            // Make sure the ledger knows, so the capture hook treats this copy as already backed up.
            RecordInstalled(registry, comparison.LocalPath!, remote.Version);
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

        _ledger.Record(registry, path, new SkillLedgerEntry(SkillFileReader.Hash(written), content.Version));

        var status = comparison.LocalPath is null ? SkillPullStatus.Installed : SkillPullStatus.Updated;
        var note = comparison.State == SkillSyncState.LocalChanges ? "Local changes were overwritten (--force)." : null;
        return new SkillPullEntry(harness, remote.Name, status, content.Version, path, note);
    }

    private SkillComparison Compare(IHarness harness, RegistrySkill remote, DiscoveredSkill? installed, Uri registry)
    {
        if (installed is null)
            return new SkillComparison(harness, remote.Name, SkillSyncState.NotInstalled, remote, null, null);

        var recorded = _ledger.Get(registry, installed.Path);
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

    private void RecordInstalled(Uri registry, string path, int version)
    {
        try
        {
            var hash = SkillFileReader.Hash(SkillFileReader.Read(path));
            if (_ledger.Get(registry, path) is not { } recorded || recorded.ContentHash != hash || recorded.Version != version)
                _ledger.Record(registry, path, new SkillLedgerEntry(hash, version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs a redundant upload later.
        }
    }

    // The global skills of one harness by name. When two share a name the first (directory order) wins,
    // matching how the push side treats duplicates.
    private static Dictionary<string, DiscoveredSkill> LocalByName(Target target)
    {
        var local = new Dictionary<string, DiscoveredSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in target.Capability.DiscoverSkills(target.Location, projectStart: null))
            local.TryAdd(skill.Name, skill);
        return local;
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
