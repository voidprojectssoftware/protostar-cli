using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Skills;

/// <summary>
/// Production <see cref="ISkillPushService"/>: discovery through <see cref="ISkillService"/>, the session
/// through <see cref="IAccessTokenProvider"/>, and uploads through <see cref="IRegistryApi"/>.
/// </summary>
/// <remarks>
/// Everything that can stop the whole run (bad URL, unknown skill, no session, unreachable or
/// incompatible registry) is checked before the first upload, so a run either uploads every skill it
/// can or uploads nothing. A project skill is sent with its project folder's facts, from
/// <see cref="IProjectFactsResolver"/>, so the registry files it under that project; a global skill is sent
/// without. When two skills of one scope share a name (two harnesses' global skills, say), only the first is
/// pushed; the other is skipped if its content is identical and reported as a conflict if not. A global and a
/// project skill of the same name are separate registry skills and are both pushed. A skill whose content
/// matches the <see cref="ISkillLedger"/> record for the same scope is reported unchanged without any network
/// call, and when every skill is, the registry is never contacted.
/// </remarks>
internal sealed class SkillPushService : ISkillPushService
{
    private readonly ISkillService _skills;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _registry;
    private readonly ISkillLedger _ledger;
    private readonly IProjectFactsResolver _facts;
    private readonly IMachineIdentity _machine;

    public SkillPushService(
        ISkillService skills, IAccessTokenProvider tokens, IRegistryApi registry, ISkillLedger ledger, IProjectFactsResolver facts, IMachineIdentity machine)
    {
        _skills = skills;
        _tokens = tokens;
        _registry = registry;
        _ledger = ledger;
        _facts = facts;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(request.Registry);
        }
        catch (FormatException ex)
        {
            return SkillPushRunResult.Fail(SkillPushRunFailure.InvalidRegistry, ex.Message);
        }

        var discovery = _skills.Discover(request.HarnessId, request.HarnessHome, request.ProjectStart);
        switch (discovery.Failure)
        {
            case SkillQueryFailure.UnknownHarness:
                return SkillPushRunResult.Fail(SkillPushRunFailure.UnknownHarness, discovery.OffendingHarnessId, registry);
            case SkillQueryFailure.Unsupported:
                return SkillPushRunResult.Fail(SkillPushRunFailure.Unsupported, discovery.OffendingHarnessId, registry);
        }

        var selected = Select(discovery.Skills, request.Names, out var missing);
        if (missing is not null)
            return SkillPushRunResult.Fail(SkillPushRunFailure.SkillNotFound, missing, registry);

        // Decide each skill locally first: unreadable skills fail, a second skill with an already-seen name in
        // the same scope is compared by content (identical is skipped, different is a conflict), and skills the
        // ledger says the registry already has are unchanged. Only what is left needs the network.
        var entries = new SkillPushEntry?[selected.Count];
        var pending = new List<(int Index, IReadOnlyList<SkillUploadFile> Files, string Hash)>();
        var seen = new Dictionary<(SkillScope, string), (DiscoveredSkill Skill, string? Hash)>(ScopedNameComparer.Instance);
        for (var i = 0; i < selected.Count; i++)
        {
            var skill = selected[i];

            IReadOnlyList<SkillUploadFile>? files = null;
            string? readError = null;
            try
            {
                files = SkillFileReader.Read(skill.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                readError = ex.Message;
            }

            var hash = files is null ? null : SkillFileReader.Hash(files);

            if (seen.TryGetValue((skill.Scope, skill.Name), out var first))
            {
                entries[i] = new SkillPushEntry(skill, CompareWithFirst(first.Skill, first.Hash, hash));
                continue;
            }
            seen[(skill.Scope, skill.Name)] = (skill, hash);

            if (files is null)
            {
                entries[i] = new SkillPushEntry(skill, new SkillPushOutcome(
                    SkillPushStatus.Failed, null, $"Could not read the skill's files: {readError}"));
                continue;
            }

            // The registry refuses a path segment with leading or trailing whitespace rather than trimming
            // it (a trimmed path would not match the file hashed here), so say so before uploading.
            if (files.FirstOrDefault(f => SkillFileReader.HasPaddedSegment(f.RelativePath)) is { } padded)
            {
                entries[i] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Failed, null,
                    $"'{padded.RelativePath}' has a file or folder name that starts or ends with whitespace, which the registry does not accept. Rename it and push again."));
                continue;
            }

            // A record only vouches for the scope it was pushed in. A project skill backed up as global by a CLI
            // from before project skills has a record without a project, and must still go up as the project's.
            if (!request.Force && _ledger.Get(registry, skill.Path) is { } recorded && recorded.ContentHash == hash &&
                recorded.ProjectId is not null == (skill.Scope == SkillScope.Project))
            {
                entries[i] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Unchanged, recorded.Version, null, recorded.ProjectId));
                continue;
            }

            pending.Add((i, files, hash!));
        }

        if (pending.Count > 0)
        {
            var session = await _tokens.GetSessionAsync(registry, cancellationToken);
            if (session is null)
                return SkillPushRunResult.Fail(SkillPushRunFailure.NotLoggedIn, null, registry);
            if (session.IsExpired)
                return SkillPushRunResult.Fail(SkillPushRunFailure.SessionExpired, null, registry);

            var meta = await _registry.GetMetaAsync(registry, cancellationToken);
            if (meta is null)
                return SkillPushRunResult.Fail(SkillPushRunFailure.RegistryUnreachable, null, registry);
            if (ApiCompatibility.Check(meta) is { } incompatibility)
                return SkillPushRunResult.Fail(SkillPushRunFailure.Incompatible, incompatibility, registry);

            foreach (var (index, files, hash) in pending)
            {
                var skill = selected[index];
                var outcome = await _registry.PushSkillAsync(registry, session.AccessToken, skill.Name, files, ProjectOf(skill), cancellationToken);
                if (outcome.Succeeded)
                    _ledger.Record(registry, skill.Path, new SkillLedgerEntry(hash, outcome.Version, skill.Name, outcome.ProjectId));
                entries[index] = new SkillPushEntry(skill, outcome);
            }
        }

        return new SkillPushRunResult(SkillPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
    }

    // The project folder a project skill is pushed as part of: the folder its project config was found in,
    // resolved the same way session backups resolve a working directory. Null for a global skill.
    private SkillProject? ProjectOf(DiscoveredSkill skill) =>
        skill.Scope == SkillScope.Project
            ? new SkillProject(_machine.Get().Id, _facts.Resolve(skill.ProjectRoot ?? skill.Path))
            : null;

    // A later skill whose name was already taken in this run, in the same scope. The registry holds one skill per
    // name in each scope, so the later copy can only be left out safely when its content is identical to the one
    // being backed up.
    private static SkillPushOutcome CompareWithFirst(DiscoveredSkill first, string? firstHash, string? hash)
    {
        var where = $"the {first.Scope.ToString().ToLowerInvariant()} skill at {first.Path}";
        if (firstHash is not null && firstHash == hash)
            return new SkillPushOutcome(SkillPushStatus.Skipped, null, $"Identical to {where}.");

        var why = firstHash is null || hash is null
            ? $"Could not compare it with {where}, which has the same name."
            : $"Differs from {where}, which has the same name.";
        return new SkillPushOutcome(SkillPushStatus.Conflict, null,
            $"{why} The registry holds one {first.Scope.ToString().ToLowerInvariant()} skill per name, so this copy was not backed up.");
    }

    // Skill names compare ignoring case, as the harness's folders do on some file systems.
    private sealed class ScopedNameComparer : IEqualityComparer<(SkillScope Scope, string Name)>
    {
        public static readonly ScopedNameComparer Instance = new();

        public bool Equals((SkillScope Scope, string Name) x, (SkillScope Scope, string Name) y) =>
            x.Scope == y.Scope && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((SkillScope Scope, string Name) obj) =>
            HashCode.Combine(obj.Scope, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    // Every discovered skill when no names were given; otherwise the skills matching each name, with
    // the first name that matched nothing reported back so the caller can say which one is missing.
    private static List<DiscoveredSkill> Select(IReadOnlyList<DiscoveredSkill> skills, IReadOnlyList<string> names, out string? missing)
    {
        missing = null;
        if (names.Count == 0)
            return skills.ToList();

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        missing = names.FirstOrDefault(n => !skills.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)));
        return skills.Where(s => wanted.Contains(s.Name)).ToList();
    }
}
