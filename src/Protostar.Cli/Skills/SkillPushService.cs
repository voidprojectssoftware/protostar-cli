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
/// matches the <see cref="ISkillLedger"/> record for the same scope is unchanged: the capture hook takes the
/// ledger's word for it and makes no network call, while the command confirms it against the registry's skill list,
/// one request per run however many skills there are, so a skill deleted or renamed from another machine is never
/// reported as safe. A copy of a skill the registry remembers removing (the list's removed names) is reported and not
/// uploaded, so a push or setup never undoes a delete or rename; one the registry has no memory of is backed up again.
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
    private readonly IProjectApi _projects;

    public SkillPushService(
        ISkillService skills, IAccessTokenProvider tokens, IRegistryApi registry, ISkillLedger ledger, IProjectFactsResolver facts, IMachineIdentity machine,
        IProjectApi projects)
    {
        _skills = skills;
        _tokens = tokens;
        _registry = registry;
        _ledger = ledger;
        _facts = facts;
        _machine = machine;
        _projects = projects;
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

        // --as renames the copy here once the registry has it, so a name another skill of the scope already uses, here or
        // in the registry, would leave two skills answering to it. Every harness's skills count, as they share the registry.
        var local = request.HarnessId is null ? discovery.Skills : _skills.Discover(null, request.HarnessHome, request.ProjectStart).Skills;
        if (request.As is { } asName && LocalHolder(selected[0], asName, local) is { } localHolder)
            return SkillPushRunResult.Fail(SkillPushRunFailure.InvalidAs, Taken(asName, localHolder, selected[0], local, null), registry);

        // Decide each skill locally first: unreadable skills fail, a second skill with an already-seen name in
        // the same scope is compared by content (identical is skipped, different is a conflict), and skills the
        // ledger says the registry already has are unchanged, once the list below confirms it. Only what is left is uploaded.
        var entries = new SkillPushEntry?[selected.Count];
        var pending = new List<(int Index, IReadOnlyList<SkillUploadFile> Files, string Hash)>();

        // The command checks the ledger against the registry's list (one request), because the ledger cannot know
        // that another machine deleted or renamed a skill since. The hook, which runs on every use of a skill, keeps
        // trusting the ledger, so an unchanged skill still costs it nothing. --as pushes under a new name, so it has
        // nothing to confirm.
        var verify = !request.FromHook && request.As is null;
        var unconfirmed = new List<(int Index, IReadOnlyList<SkillUploadFile> Files, string Hash)>();
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
                if (verify)
                    unconfirmed.Add((i, files, hash));
                else
                    entries[i] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Unchanged, recorded.Version, null, recorded.ProjectId));
                continue;
            }

            // The registry already refused exactly this content, as stale or as a copy of a removed skill. Asking again
            // would only be refused again, so the hook, which runs on every use of the skill, answers from the ledger until
            // the copy changes.
            if (request.FromHook && recorded is not null && recorded.RefusedHash == hash)
            {
                const string remembered = " Refused earlier; not sent again until it changes.";
                entries[i] = new SkillPushEntry(skill, recorded.RefusedAsRemoved
                    ? new SkillPushOutcome(SkillPushStatus.Removed, recorded.RefusedVersion,
                        RemovedMessage(skill.Name, new RegistryRemovedSkill(skill.Name, recorded.ProjectId, recorded.RefusedVersion ?? 0, recorded.RefusedRenamedTo), edited: true) + remembered,
                        recorded.ProjectId, Remembered: true, RenamedTo: recorded.RefusedRenamedTo)
                    : new SkillPushOutcome(SkillPushStatus.Stale, recorded.RefusedVersion,
                        StaleMessage(recorded.Version ?? 0, recorded.RefusedVersion) + remembered,
                        recorded.ProjectId, Remembered: true));
                continue;
            }

            pending.Add((i, files, hash!));
        }

        var mustList = verify && (unconfirmed.Count > 0 || pending.Any(p => Synced(registry, selected[p.Index]) is not null));
        if (pending.Count == 0 && !mustList)
            return new SkillPushRunResult(SkillPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } session })
        {
            var failure = lookup.State switch
            {
                SessionState.None => SkillPushRunFailure.NotLoggedIn,
                SessionState.RegistryUnreachable => SkillPushRunFailure.RegistryUnreachable,
                _ => SkillPushRunFailure.SessionExpired,
            };
            return SkillPushRunResult.Fail(failure, null, registry);
        }

        if (request.As is { } asNew && pending.Count > 0)
        {
            var taken = await RegistryNamesAsync(registry, session.AccessToken, selected[0], cancellationToken);
            if (taken.Failure is { } failure)
                return failure;
            if (RegistryHolder(asNew, taken) is { } registryHolder)
                return SkillPushRunResult.Fail(SkillPushRunFailure.InvalidAs, Taken(asNew, registryHolder, selected[0], local, taken), registry);
        }

        var gone = new HashSet<int>();
        if (mustList)
        {
            var list = await _registry.ListSkillsAsync(registry, session.AccessToken, cancellationToken);
            if (list.Skills is not { } skills)
            {
                // Say why when the registry is down or speaks another API; otherwise pass on what the list said.
                if (await CheckMetaAsync(registry, cancellationToken) is { } failure)
                    return failure;
                return SkillPushRunResult.Fail(SkillPushRunFailure.ListFailed, list.Error, registry);
            }

            // A copy whose registry skill is gone is either a copy of a skill deleted or renamed since it synced, which is
            // reported and never uploaded (that would undo the delete or rename), or of one the registry has no memory of,
            // which is backed up again. With --force a removed skill is restored on purpose, as a new skill.
            foreach (var (index, _, hash) in pending.ToList())
            {
                var skill = selected[index];
                if (await StillInRegistryAsync(registry, session.AccessToken, skill, skills, cancellationToken))
                    continue;
                gone.Add(index);
                if (Removal(registry, skill, list) is { } removal && !request.Force)
                {
                    pending.RemoveAll(p => p.Index == index);
                    entries[index] = new SkillPushEntry(skill, RefuseRemoved(registry, skill, hash, removal));
                }
            }

            foreach (var (index, files, hash) in unconfirmed)
            {
                var skill = selected[index];
                var recorded = _ledger.Get(registry, skill.Path)!;
                if (await StillInRegistryAsync(registry, session.AccessToken, skill, skills, cancellationToken))
                {
                    entries[index] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Unchanged, recorded.Version, null, recorded.ProjectId));
                }
                else if (Removal(registry, skill, list) is { } removal)
                {
                    // Unedited since it synced, so there is nothing of this machine's to protect: only report it.
                    entries[index] = new SkillPushEntry(skill, new SkillPushOutcome(SkillPushStatus.Removed, removal.LastVersion,
                        RemovedMessage(skill.Name, removal, edited: false), recorded.ProjectId, RenamedTo: removal.RenamedTo));
                }
                else
                {
                    gone.Add(index);
                    pending.Add((index, files, hash));
                }
            }
        }

        if (pending.Count > 0)
        {
            if (await CheckMetaAsync(registry, cancellationToken) is { } failure)
                return failure;

            foreach (var (index, files, hash) in pending.OrderBy(p => p.Index))
            {
                var skill = selected[index];
                entries[index] = new SkillPushEntry(skill, request.As is { } newName
                    ? await PushAsAsync(registry, session.AccessToken, skill, files, newName, cancellationToken)
                    : await PushOneAsync(registry, session.AccessToken, skill, files, hash, request.Force, gone.Contains(index), cancellationToken));
            }
        }

        return new SkillPushRunResult(SkillPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
    }

    // Null when the registry is reachable and speaks this CLI's API; otherwise the failure that stops the run.
    private async Task<SkillPushRunResult?> CheckMetaAsync(Uri registry, CancellationToken cancellationToken)
    {
        var meta = await _registry.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return SkillPushRunResult.Fail(SkillPushRunFailure.RegistryUnreachable, null, registry);
        if (ApiCompatibility.Check(meta) is { } incompatibility)
            return SkillPushRunResult.Fail(SkillPushRunFailure.Incompatible, incompatibility, registry);
        return null;
    }

    // The ledger's record of this copy when it vouches for the copy's registry skill; null when it never synced as it.
    private SkillLedgerEntry? Synced(Uri registry, DiscoveredSkill skill) =>
        _ledger.Get(registry, skill.Path) is { } recorded && Vouches(recorded, skill) ? recorded : null;

    // The registry's removal of the skill this copy last synced as, when the copy came from it (RegistrySkillList.RemovalOf).
    private RegistryRemovedSkill? Removal(Uri registry, DiscoveredSkill skill, RegistrySkillList list) =>
        Synced(registry, skill) is { } recorded ? list.RemovalOf(recorded.Name ?? skill.Name, recorded.ProjectId, recorded.Version) : null;

    // An edited copy of a removed skill: nothing is uploaded, and the refusal is recorded like a stale one, so the hook does
    // not send the same content again and a new skill that later takes the name shows as changed here and in the registry.
    private SkillPushOutcome RefuseRemoved(Uri registry, DiscoveredSkill skill, string hash, RegistryRemovedSkill removal)
    {
        var recorded = Synced(registry, skill)!;
        _ledger.Record(registry, skill.Path, recorded with
        {
            RefusedHash = hash, RefusedVersion = removal.LastVersion, RefusedAsRemoved = true, RefusedRenamedTo = removal.RenamedTo,
        });
        return new SkillPushOutcome(SkillPushStatus.Removed, removal.LastVersion,
            RemovedMessage(skill.Name, removal, edited: true), recorded.ProjectId, RenamedTo: removal.RenamedTo);
    }

    // Whether the registry skill this copy last synced as (its name and project, from the ledger) is in the registry's
    // list. A copy that never synced has nothing to lose, so it counts as present. The list holds every project's
    // skills, so a skill of another project is never mistaken for this one, and a skill that a link moved to another
    // project is followed there (MovedSkillLedger) rather than treated as deleted.
    private async Task<bool> StillInRegistryAsync(
        Uri registry, string accessToken, DiscoveredSkill skill, IReadOnlyList<RegistrySkill> skills, CancellationToken cancellationToken)
    {
        if (Synced(registry, skill) is not { } recorded)
            return true;

        // A skill of that name whose versions start after this copy's is a new skill that reused the name, not this one.
        var name = recorded.Name ?? skill.Name;
        if (skills.Any(s => s.ProjectId == recorded.ProjectId && string.Equals(s.Name, name, StringComparison.Ordinal) &&
                            !(recorded.Version is { } synced && synced >= 1 && synced < s.FirstVersion)))
            return true;

        foreach (var moved in skills.Where(s => s.ProjectId is not null && s.ProjectId != recorded.ProjectId && string.Equals(s.Name, name, StringComparison.Ordinal)))
        {
            var followed = await MovedSkillLedger.FollowAsync(_registry, _ledger, registry, accessToken, skill.Path, recorded with { Name = name }, moved, skills, cancellationToken);
            if (followed?.ProjectId == moved.ProjectId)
                return true;
        }

        return false;
    }

    // Pushes one skill based on the version its copy last synced, and records the result. A stale refusal is recorded,
    // never retried; with force it is answered by pushing again based on the registry's current version, which the
    // registry has just named, so the replace is deliberate and a third push in between is still refused. A copy whose
    // registry skill is gone is pushed as a new skill, base 0, and says why: one the registry has no memory of, or, with
    // force, a removed one being restored. A refusal as a copy of a removed skill (the hook, which does not list, or a
    // removal between the list and the push) is recorded like a stale one; with force it is answered with base 0.
    private async Task<SkillPushOutcome> PushOneAsync(
        Uri registry, string accessToken, DiscoveredSkill skill, IReadOnlyList<SkillUploadFile> files, string hash, bool force, bool gone,
        CancellationToken cancellationToken)
    {
        var basis = gone ? null : Synced(registry, skill);
        var based = basis is null ? 0 : basis.Version;
        var project = ProjectOf(skill);

        var outcome = await _registry.PushSkillAsync(registry, accessToken, skill.Name, files, project, based, cancellationToken);
        var replacedCurrent = false;
        if (outcome.Status == SkillPushStatus.Removed && force)
        {
            gone = true;
            outcome = await _registry.PushSkillAsync(registry, accessToken, skill.Name, files, project, 0, cancellationToken);
        }
        else if (outcome is { Status: SkillPushStatus.Stale, Version: { } current } && force)
        {
            outcome = await _registry.PushSkillAsync(registry, accessToken, skill.Name, files, project, current, cancellationToken);
            replacedCurrent = outcome.Status == SkillPushStatus.Created;
            if (replacedCurrent)
            {
                outcome = outcome with
                {
                    Message = $"Replaced v{current}, which reached the registry after this copy's {Basis(based ?? 0)}. " +
                        $"v{current} stays in its history: `protostar skills pull {skill.Name} --version {current}` gets it back.",
                };
            }
        }

        if (gone && outcome.Succeeded && !replacedCurrent)
        {
            outcome = outcome with
            {
                Message = force
                    ? $"Restored: '{skill.Name}' had been deleted or renamed in the registry; pushed this copy as a new skill named '{skill.Name}'."
                    : $"'{skill.Name}' was renamed or deleted in the registry; backed it up again as '{skill.Name}'.",
            };
        }

        if (outcome.Succeeded)
        {
            _ledger.Record(registry, skill.Path, new SkillLedgerEntry(hash, outcome.Version, skill.Name, outcome.ProjectId));
        }
        else if (outcome.Status == SkillPushStatus.Stale)
        {
            var entry = basis ?? new SkillLedgerEntry(string.Empty, 0, skill.Name, outcome.ProjectId);
            _ledger.Record(registry, skill.Path, entry with { RefusedHash = hash, RefusedVersion = outcome.Version, RefusedAsRemoved = false, RefusedRenamedTo = null });
            outcome = outcome with { Message = StaleMessage(based ?? 0, outcome.Version) };
        }
        else if (outcome.Status == SkillPushStatus.Removed && basis is not null)
        {
            // Only a push that named a base from 1 up is refused this way, so the copy has a record.
            _ledger.Record(registry, skill.Path, basis with
            {
                RefusedHash = hash, RefusedVersion = outcome.Version, RefusedAsRemoved = true, RefusedRenamedTo = outcome.RenamedTo,
            });
            outcome = outcome with { Message = RemovedMessage(skill.Name, new RegistryRemovedSkill(skill.Name, outcome.ProjectId, outcome.Version ?? 0, outcome.RenamedTo), edited: true) };
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

    // What already answers to newName in the scope of the skill being renamed, on this machine: another skill of that
    // name or in a folder of that name, or any folder of that name beside it (which the rename would move into). Null when free.
    private static string? LocalHolder(DiscoveredSkill skill, string newName, IReadOnlyList<DiscoveredSkill> local)
    {
        foreach (var other in local.Where(s => s.Path != skill.Path && SameScope(s, skill)))
        {
            if (string.Equals(other.Name, newName, StringComparison.OrdinalIgnoreCase))
                return $"the {Describe(other.Scope)} skill at {other.Path} is called '{other.Name}'";
            if (string.Equals(Path.GetFileName(other.Path), newName, StringComparison.OrdinalIgnoreCase))
                return $"the {Describe(other.Scope)} skill '{other.Name}' is in a folder named '{Path.GetFileName(other.Path)}' ({other.Path})";
        }

        try
        {
            var parent = Path.GetDirectoryName(skill.Path);
            if (parent is not null && Directory.Exists(parent) &&
                new DirectoryInfo(parent).EnumerateFileSystemInfos().FirstOrDefault(e => string.Equals(e.Name, newName, StringComparison.OrdinalIgnoreCase)) is { } entry)
            {
                return $"{entry.FullName} already exists beside it";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the folder beside it could not be checked for '{newName}' ({ex.Message})";
        }

        return null;
    }

    // The registry's skill names, live and removed, in the scope of the skill being renamed: its project's (or none when
    // the folder has no project yet), or the global ones.
    private async Task<RegistryNames> RegistryNamesAsync(Uri registry, string accessToken, DiscoveredSkill skill, CancellationToken cancellationToken)
    {
        Guid? projectId = null;
        if (ProjectOf(skill) is { } project)
        {
            var resolved = await _projects.ResolveProjectAsync(registry, accessToken, project.MachineId, project.Facts, cancellationToken);
            if (!resolved.Ok)
                return new RegistryNames(null, [], [], SkillPushRunResult.Fail(SkillPushRunFailure.ListFailed, resolved.Error, registry));
            if (resolved.Value!.ProjectId is not { } id)
                return new RegistryNames(null, [], [], null);
            projectId = id;
        }

        var list = await _registry.ListSkillsAsync(registry, accessToken, cancellationToken);
        if (list.Skills is not { } skills)
            return new RegistryNames(projectId, [], [], await CheckMetaAsync(registry, cancellationToken) ?? SkillPushRunResult.Fail(SkillPushRunFailure.ListFailed, list.Error, registry));

        return new RegistryNames(
            projectId,
            skills.Where(s => s.ProjectId == projectId).ToList(),
            (list.Removed ?? []).Where(r => r.ProjectId == projectId).ToList(),
            null);
    }

    // What answers to newName in the registry's scope: a skill, or a removed one whose copies may still be about. Null when free.
    private static string? RegistryHolder(string newName, RegistryNames names)
    {
        var where = names.ProjectId is null ? "global skill" : "skill in this project";
        if (names.Skills.FirstOrDefault(s => string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase)) is { } live)
            return $"the registry already has a {where} named '{live.Name}'";
        if (names.Removed.FirstOrDefault(r => string.Equals(r.Name, newName, StringComparison.OrdinalIgnoreCase)) is { } removed)
        {
            var how = removed.RenamedTo is { } renamedTo ? $"renamed to '{renamedTo}'" : "deleted";
            return $"the registry had a {where} named '{removed.Name}' ({how} at v{removed.LastVersion}), and copies of it may still be synced under that name";
        }

        return null;
    }

    // Why --as cannot use newName, with a free name to try instead: the first of newName-2, newName-3, ... that nothing
    // here, and nothing in the registry when it was asked, answers to.
    private static string Taken(string newName, string holder, DiscoveredSkill skill, IReadOnlyList<DiscoveredSkill> local, RegistryNames? names)
    {
        var suggestion = Enumerable.Range(2, 98).Select(n => $"{newName}-{n}")
            .FirstOrDefault(candidate => LocalHolder(skill, candidate, local) is null && (names is null || RegistryHolder(candidate, names) is null));
        var instead = suggestion is null ? "Choose another name." : $"Choose another name, such as '{suggestion}'.";
        return $"'{newName}' is already taken: {holder}. Nothing was pushed or changed. {instead}";
    }

    private static bool SameScope(DiscoveredSkill a, DiscoveredSkill b) =>
        a.Scope == b.Scope && (a.Scope == SkillScope.Global || string.Equals(
            a.ProjectRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(a.ProjectRoot)),
            b.ProjectRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.ProjectRoot)),
            StringComparison.Ordinal));

    private static string Describe(SkillScope scope) => scope.ToString().ToLowerInvariant();

    // The registry's names in one scope (a project, or global when ProjectId is null), or the failure that stops the run.
    private sealed record RegistryNames(Guid? ProjectId, IReadOnlyList<RegistrySkill> Skills, IReadOnlyList<RegistryRemovedSkill> Removed, SkillPushRunResult? Failure);

    // A record vouches for a copy's base only when it was synced as this skill: the same scope, and the same name
    // (records from before names were kept say none). Otherwise the copy is treated as never synced (base 0).
    private static bool Vouches(SkillLedgerEntry recorded, DiscoveredSkill skill) =>
        recorded.ProjectId is not null == (skill.Scope == SkillScope.Project) &&
        (recorded.Name is null || string.Equals(recorded.Name, skill.Name, StringComparison.Ordinal));

    private static string Basis(int based) => based == 0 ? "last sync (it never synced a version)" : $"v{based}";

    private static string StaleMessage(int based, int? current) =>
        $"Changed here and in the registry: the registry has v{current}, but this copy is based on " +
        (based == 0 ? "no registry version (it was never pulled or pushed here)." : $"v{based}.") + " Nothing was overwritten.";

    // Why a copy of a removed skill was not backed up, and the ways out. Pull follows a rename for an unedited copy; an
    // edited one, or a deleted skill, is the user's call: remove the copy, or restore it on purpose with --force.
    internal static string RemovedMessage(string name, RegistryRemovedSkill removal, bool edited)
    {
        var quoted = name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;
        var changed = edited ? "Changed here, but " : "";
        var kept = edited ? "Nothing was uploaded and your changes are kept here." : "It was not uploaded again.";
        if (removal.Replaced)
        {
            return $"{changed}'{name}' was deleted from the registry (at v{removal.LastVersion}) after this copy last synced, and a new skill named '{name}' " +
                $"has been created since. This copy is of the old one. {kept} Run `protostar skills push {quoted} --as NEW-NAME` to keep it as a " +
                $"separate skill, or `protostar skills pull {quoted} --force` to replace it with the new one.";
        }

        if (removal.RenamedTo is not { } renamedTo)
        {
            return $"{changed}'{name}' was deleted from the registry (at v{removal.LastVersion}) after this copy last synced. {kept} " +
                $"Run `protostar skills push {quoted} --force` to restore it{(edited ? " with your changes" : "")}, or remove this copy.";
        }

        var what = $"'{name}' was renamed to '{renamedTo}' in the registry (at v{removal.LastVersion}) after this copy last synced";
        return edited
            ? $"Changed here, but {what}. {kept} Move them into '{renamedTo}' (`protostar skills pull` installs it), or run `protostar skills push {quoted} --force` to keep this as a separate skill named '{name}'."
            : $"{what}, so it was not uploaded again under the old name. `protostar skills pull` renames this copy to '{renamedTo}'.";
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
