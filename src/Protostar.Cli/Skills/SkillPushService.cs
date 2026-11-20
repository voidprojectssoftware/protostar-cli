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
/// <para>
/// The base version each push names is the version the ledger says this copy last synced (pushed or pulled), not
/// the registry's current one: that is what makes a stale edit detectable. A copy the ledger has no record of, for
/// this skill and scope, names base 0, so it can create a skill but never overwrite one it has not seen. A record
/// from before versions were kept names no base and is accepted as before.
/// </para>
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
        if (request.As is not null && AsProblem(request.Names, selected, request.As) is { } asProblem)
            return SkillPushRunResult.Fail(SkillPushRunFailure.InvalidAs, asProblem, registry);

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
            var recorded = _ledger.Get(registry, skill.Path);
            if (!request.Force && request.As is null && recorded is not null && recorded.ContentHash == hash &&
                recorded.ProjectId is not null == (skill.Scope == SkillScope.Project))
            {
                entries[i] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Unchanged, recorded.Version, null, recorded.ProjectId));
                continue;
            }

            // The registry already refused exactly this content as stale. Asking again would only be refused again,
            // so the hook, which runs on every use of the skill, answers from the ledger until the copy changes.
            if (request.FromHook && recorded is not null && recorded.RefusedHash == hash)
            {
                entries[i] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Stale, recorded.RefusedVersion,
                    StaleMessage(recorded.Version ?? 0, recorded.RefusedVersion) + " Refused earlier; not sent again until it changes.",
                    recorded.ProjectId, Remembered: true));
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
                entries[index] = new SkillPushEntry(skill, request.As is { } newName
                    ? await PushAsAsync(registry, session.AccessToken, skill, files, newName, cancellationToken)
                    : await PushOneAsync(registry, session.AccessToken, skill, files, hash, request.Force, cancellationToken));
            }
        }

        return new SkillPushRunResult(SkillPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
    }

    // Pushes one skill based on the version its copy last synced, and records the result. A stale refusal is recorded,
    // never retried; with force it is answered by pushing again based on the registry's current version, which the
    // registry has just named, so the replace is deliberate and a third push in between is still refused.
    private async Task<SkillPushOutcome> PushOneAsync(
        Uri registry, string accessToken, DiscoveredSkill skill, IReadOnlyList<SkillUploadFile> files, string hash, bool force, CancellationToken cancellationToken)
    {
        var recorded = _ledger.Get(registry, skill.Path);
        var basis = recorded is not null && Vouches(recorded, skill) ? recorded : null;
        var based = basis is null ? 0 : basis.Version;
        var project = ProjectOf(skill);

        var outcome = await _registry.PushSkillAsync(registry, accessToken, skill.Name, files, project, based, cancellationToken);
        if (outcome is { Status: SkillPushStatus.Stale, Version: { } current } && force)
        {
            outcome = await _registry.PushSkillAsync(registry, accessToken, skill.Name, files, project, current, cancellationToken);
            if (outcome.Status == SkillPushStatus.Created)
            {
                outcome = outcome with
                {
                    Message = $"Replaced v{current}, which reached the registry after this copy's {Basis(based ?? 0)}. " +
                        $"v{current} stays in its history: `protostar skills pull {skill.Name} --version {current}` gets it back.",
                };
            }
        }

        if (outcome.Succeeded)
        {
            _ledger.Record(registry, skill.Path, new SkillLedgerEntry(hash, outcome.Version, skill.Name, outcome.ProjectId));
        }
        else if (outcome.Status == SkillPushStatus.Stale)
        {
            var entry = basis ?? new SkillLedgerEntry(string.Empty, 0, skill.Name, outcome.ProjectId);
            _ledger.Record(registry, skill.Path, entry with { RefusedHash = hash, RefusedVersion = outcome.Version });
            outcome = outcome with { Message = StaleMessage(based ?? 0, outcome.Version) };
        }

        return outcome;
    }

    // Keep both: pushes the copy as a new registry skill named newName (its SKILL.md name rewritten to match), then
    // renames the local copy the same way, so the original name is free here for the registry's version of it.
    // Base 0: newName must be a new skill, so an existing skill of that name is refused, never overwritten.
    private async Task<SkillPushOutcome> PushAsAsync(
        Uri registry, string accessToken, DiscoveredSkill skill, IReadOnlyList<SkillUploadFile> files, string newName, CancellationToken cancellationToken)
    {
        var (renamed, _) = SkillChangeService.RenameInManifest(files, newName);
        var outcome = await _registry.PushSkillAsync(registry, accessToken, newName, renamed, ProjectOf(skill), 0, cancellationToken);
        if (outcome.Status == SkillPushStatus.Stale)
            return new SkillPushOutcome(SkillPushStatus.Refused, null, $"The registry already has a skill named '{newName}' with other content, so nothing was pushed. Choose another name.");
        if (!outcome.Succeeded)
            return outcome;

        var (path, _, note) = SkillChangeService.RenameCopy(skill.Path, skill.Name, newName);
        if (path != skill.Path)
            _ledger.Forget(registry, skill.Path);

        string hash;
        try
        {
            hash = SkillFileReader.Hash(SkillFileReader.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hash = SkillFileReader.Hash(renamed);
        }

        _ledger.Record(registry, path, new SkillLedgerEntry(hash, outcome.Version, newName, outcome.ProjectId));
        var message = $"Pushed as '{newName}' and renamed here to {path}. `protostar skills pull {skill.Name}` installs the registry's '{skill.Name}' beside it.";
        return outcome with { Message = note is null ? message : $"{message} Note: {note}." };
    }

    // Why --as cannot go ahead: it pushes exactly one local skill under a name the registry can use.
    private static string? AsProblem(IReadOnlyList<string> names, IReadOnlyList<DiscoveredSkill> selected, string newName)
    {
        if (names.Count != 1)
            return "--as pushes one skill under a new name: name exactly one skill.";
        if (selected.Count != 1)
            return $"'{names[0]}' matches {selected.Count} local skills (a global and a project skill, or several harnesses). Narrow it with --global-only, --harness or --project.";
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(['/', '\\']) >= 0)
            return $"'{newName}' cannot name a skill: it must not be blank or contain '/' or '\\'.";
        if (string.Equals(newName, selected[0].Name, StringComparison.Ordinal))
            return $"'{newName}' is already this skill's name. --as takes a different one.";
        return null;
    }

    // A record vouches for a copy's base only when it was synced as this skill: the same scope, and the same name
    // (records from before names were kept say none). Otherwise the copy is treated as never synced (base 0).
    private static bool Vouches(SkillLedgerEntry recorded, DiscoveredSkill skill) =>
        recorded.ProjectId is not null == (skill.Scope == SkillScope.Project) &&
        (recorded.Name is null || string.Equals(recorded.Name, skill.Name, StringComparison.Ordinal));

    private static string Basis(int based) => based == 0 ? "last sync (it never synced a version)" : $"v{based}";

    private static string StaleMessage(int based, int? current) =>
        $"Changed here and in the registry: the registry has v{current}, but this copy is based on " +
        (based == 0 ? "no registry version (it was never pulled or pushed here)." : $"v{based}.") + " Nothing was overwritten.";

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
