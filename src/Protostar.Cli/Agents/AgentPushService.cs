using System.Security.Cryptography;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Agents;

/// <summary>Production <see cref="IAgentPushService"/>.</summary>
/// <remarks>
/// <para>
/// Copied from <see cref="Skills.SkillPushService"/> and cut down to what agents have in 7.1 (ADR 0003: copy the
/// versioning rules, do not share them; a fix to one copy must be checked against the other). Everything that can stop
/// the run (bad URL, unknown agent, no session, an unreachable registry or one without agents) is checked before the
/// first upload.
/// </para>
/// <para>
/// The base each push names is the version the ledger says this copy last synced, or 0 for a copy the ledger has no
/// record of (it can create an agent but never overwrite one it has not seen). An unchanged copy costs the hook nothing;
/// the command confirms it with one list request per run.
/// </para>
/// <para>
/// A recorded base is a version of the project the record names, so a project agent is sent with it only when its folder is
/// still in that project (#138, as <see cref="Skills.SkillPushService"/>): the command and the hook ask once per folder, and
/// only for a copy about to be uploaded. A copy recorded under another project is sent with base 0, and one whose folder's
/// project cannot be told is not sent.
/// </para>
/// <para>
/// A record of a project agent that a link on another machine moved is followed through
/// <see cref="MovedAgentLedger.FollowAsync"/>: an agent of that name in another project, none left in the recorded
/// project, and the recorded version's content (from the list, or one download when the agent moved on since) the same.
/// </para>
/// </remarks>
internal sealed class AgentPushService : IAgentPushService
{
    private readonly IHarnessCatalog _catalog;
    private readonly IAccessTokenProvider _tokens;
    private readonly IAgentApi _api;
    private readonly IRegistryCapabilities _capabilities;
    private readonly IAgentLedger _ledger;
    private readonly IProjectFactsResolver _facts;
    private readonly IMachineIdentity _machine;
    private readonly IProjectApi _projects;

    public AgentPushService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IAgentApi api, IRegistryCapabilities capabilities, IAgentLedger ledger,
        IProjectFactsResolver facts, IMachineIdentity machine, IProjectApi projects)
    {
        _catalog = catalog;
        _tokens = tokens;
        _api = api;
        _capabilities = capabilities;
        _ledger = ledger;
        _facts = facts;
        _machine = machine;
        _projects = projects;
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredAgent> Changed(string? harnessHome, string? projectStart, string? registry)
    {
        Uri uri;
        try
        {
            uri = RegistryEndpoint.Resolve(registry);
        }
        catch (FormatException)
        {
            return [];
        }

        var changed = new List<DiscoveredAgent>();
        foreach (var agent in Discover(null, harnessHome, projectStart, out _))
        {
            if (agent.Name is null || agent.Description is null || Hash(agent) is not { } hash)
                continue;
            var recorded = _ledger.Get(uri, agent.Path);
            if (recorded is not null && (Vouches(recorded, agent) && recorded.ContentHash == hash || recorded.RefusedHash == hash))
                continue;
            changed.Add(agent);
        }

        return changed;
    }

    /// <inheritdoc />
    public async Task<AgentPushRunResult> PushAsync(AgentPushRequest request, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(request.Registry);
        }
        catch (FormatException ex)
        {
            return AgentPushRunResult.Fail(AgentPushRunFailure.InvalidRegistry, ex.Message);
        }

        var discovered = Discover(request.HarnessId, request.HarnessHome, request.ProjectStart, out var failure);
        if (failure != AgentPushRunFailure.None)
            return AgentPushRunResult.Fail(failure, request.HarnessId, registry);

        var selected = discovered;
        if (request.Names.Count > 0)
        {
            if (request.Names.FirstOrDefault(n => !discovered.Any(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))) is { } missing)
                return AgentPushRunResult.Fail(AgentPushRunFailure.AgentNotFound, missing, registry);
            var wanted = new HashSet<string>(request.Names, StringComparer.OrdinalIgnoreCase);
            selected = discovered.Where(a => a.Name is not null && wanted.Contains(a.Name)).ToList();
        }

        // A file without a name or description is the user's to fix, and `agents push` says so. The hook leaves it out, so
        // it is not reported as a failed backup at the end of every session.
        if (request.FromHook)
            selected = selected.Where(a => a.Name is not null && a.Description is not null).ToList();

        // --as moves the file to <NEW>.md once the registry has the new agent, so a name or a file another agent of the scope
        // already uses here would leave two agents answering to it. Every harness's agents count, as they share the registry.
        var local = request.HarnessId is null ? discovered : Discover(null, request.HarnessHome, request.ProjectStart, out _);
        if (request.As is { } asName)
        {
            if (AsProblem(request.Names, selected, asName) is { } asProblem)
                return AgentPushRunResult.Fail(AgentPushRunFailure.InvalidAs, asProblem, registry);
            if (LocalHolder(selected[0], asName, local) is { } localHolder)
                return AgentPushRunResult.Fail(AgentPushRunFailure.InvalidAs, Taken(asName, localHolder, selected[0], local, null), registry);
        }

        var entries = new AgentPushEntry?[selected.Count];
        var pending = new List<(int Index, byte[] Content, string Hash)>();
        var unconfirmed = new List<(int Index, byte[] Content, string Hash)>();
        // --as pushes under a new name, so there is no record of it to confirm: it always uploads.
        var verify = !request.FromHook && request.As is null;
        var seen = new Dictionary<(AgentScope, string?, string), (DiscoveredAgent Agent, string? Hash)>();
        for (var i = 0; i < selected.Count; i++)
        {
            var agent = selected[i];
            if (agent.Name is null || agent.Description is null)
            {
                entries[i] = new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Refused, null, NoManifestMessage(agent)));
                continue;
            }

            byte[] content;
            try
            {
                content = File.ReadAllBytes(agent.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries[i] = new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Failed, null, $"Could not read the agent file: {ex.Message}"));
                continue;
            }

            var hash = Hash(content);
            var key = (agent.Scope, Root(agent), agent.Name.ToLowerInvariant());
            if (seen.TryGetValue(key, out var first))
            {
                entries[i] = new AgentPushEntry(agent, CompareWithFirst(first.Agent, first.Hash, hash));
                continue;
            }
            seen[key] = (agent, hash);

            var recorded = _ledger.Get(registry, agent.Path);
            if (!request.Force && request.As is null && recorded is not null && Vouches(recorded, agent) && recorded.ContentHash == hash)
            {
                if (verify)
                    unconfirmed.Add((i, content, hash));
                else
                    entries[i] = new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Unchanged, recorded.Version, null, recorded.ProjectId));
                continue;
            }

            // The registry already refused exactly these bytes. The hook answers from the ledger until the file changes.
            if (request.FromHook && recorded is not null && recorded.RefusedHash == hash)
            {
                const string remembered = " Refused earlier; not sent again until it changes.";
                entries[i] = new AgentPushEntry(agent, recorded.RefusedAsRemoved
                    ? new AgentPushOutcome(AgentPushStatus.Removed, recorded.RefusedVersion,
                        RemovedMessage(agent.Name, new RegistryRemovedAgent(agent.Name, recorded.ProjectId, recorded.RefusedVersion ?? 0, recorded.RefusedRenamedTo), edited: true) + remembered,
                        recorded.ProjectId, Remembered: true, RenamedTo: recorded.RefusedRenamedTo)
                    : new AgentPushOutcome(AgentPushStatus.Stale, recorded.RefusedVersion,
                        StaleMessage(Vouches(recorded, agent) ? recorded.Version : 0, recorded.RefusedVersion) + remembered, recorded.ProjectId, Remembered: true));
                continue;
            }

            pending.Add((i, content, hash));
        }

        var mustList = verify && (unconfirmed.Count > 0 || pending.Any(p => Synced(registry, selected[p.Index]) is not null));
        if (pending.Count == 0 && !mustList)
            return new AgentPushRunResult(AgentPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } session })
        {
            var noSession = lookup.State switch
            {
                SessionState.None => AgentPushRunFailure.NotLoggedIn,
                SessionState.RegistryUnreachable => AgentPushRunFailure.RegistryUnreachable,
                _ => AgentPushRunFailure.SessionExpired,
            };
            return AgentPushRunResult.Fail(noSession, null, registry);
        }

        // An older registry has no /v1/agents: say so before anything is sent, rather than failing on a 404.
        var projectAgents = pending.Concat(unconfirmed).Any(p => selected[p.Index].Scope == AgentScope.Project);
        var check = projectAgents
            ? await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Agents, RegistryCapability.Projects)
            : await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Agents);
        if (!check.Ok)
        {
            return AgentPushRunResult.Fail(
                check.Failure == RegistryCheckFailure.Unreachable ? AgentPushRunFailure.RegistryUnreachable : AgentPushRunFailure.Incompatible, check.Detail, registry);
        }

        if (request.As is { } asNew)
        {
            // Nothing pending means the one agent was refused locally (no name or description, unreadable): that is the answer.
            if (pending.FirstOrDefault() is not { Content: { } asContent } asOne)
                return new AgentPushRunResult(AgentPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
            var taken = await RegistryNamesAsync(registry, session.AccessToken, selected[0], cancellationToken);
            if (taken.Failure is { } takenFailure)
                return takenFailure;
            if (RegistryHolder(asNew, taken) is { } registryHolder)
                return AgentPushRunResult.Fail(AgentPushRunFailure.InvalidAs, Taken(asNew, registryHolder, selected[0], local, taken), registry);
            entries[asOne.Index] = new AgentPushEntry(selected[asOne.Index], await PushAsAsync(registry, session.AccessToken, selected[asOne.Index], asContent, asNew, cancellationToken));
            return new AgentPushRunResult(AgentPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
        }

        var gone = new HashSet<int>();

        // Which project each project agent's folder is in now, asked at most once per folder.
        var heres = new Dictionary<string, ApiResult<RemoteProject>>(StringComparer.Ordinal);
        async Task<ApiResult<RemoteProject>> HereAsync(DiscoveredAgent agent)
        {
            if (!heres.TryGetValue(agent.ProjectRoot!, out var here))
                heres[agent.ProjectRoot!] = here = await _projects.ResolveProjectAsync(registry, session.AccessToken, _machine.Get().Id, _facts.Resolve(agent.ProjectRoot!), cancellationToken);
            return here;
        }

        if (mustList)
        {
            var list = await _api.ListAgentsAsync(registry, session.AccessToken, cancellationToken);
            if (list.Agents is not { } agents)
                return AgentPushRunResult.Fail(AgentPushRunFailure.ListFailed, list.Error, registry);

            // An unedited project agent copy recorded under a project other than its folder's (pulled while the folder was
            // linked elsewhere) is still that project's agent, or this folder's project's and never synced there
            // (LinkedAgentCopy). An edited one is never still that project's: the folder check below sends it with no base.
            // A copy of an agent removed from the registry is reported as such below, like any other.
            var held = new Dictionary<int, RegistryAgent>();
            foreach (var (index, content, hash) in unconfirmed.ToList())
            {
                var agent = selected[index];
                if (agent.Scope != AgentScope.Project || Synced(registry, agent) is not { ProjectId: not null })
                    continue;

                // A record of an agent a link moved follows it first, so a move is never mistaken for a copy an unlink left.
                await StillInRegistryAsync(registry, session.AccessToken, agent, agents, cancellationToken);
                if (Synced(registry, agent) is not { ProjectId: { } syncedIn } recorded || list.RemovalOf(recorded.Name, syncedIn, recorded.Version) is not null)
                    continue;
                var here = await HereAsync(agent);
                if (!here.Ok || !LinkedAgentCopy.NamesOtherProject(recorded, here.Value!.ProjectId))
                    continue;

                if (LinkedAgentCopy.StillOf(recorded, hash, here.Value.ProjectId, agents) is { } owner)
                {
                    held[index] = owner;
                }
                else
                {
                    unconfirmed.RemoveAll(u => u.Index == index);
                    pending.Add((index, content, hash));
                }
            }

            foreach (var (index, owner) in held)
            {
                unconfirmed.RemoveAll(u => u.Index == index);
                entries[index] = new AgentPushEntry(selected[index], new AgentPushOutcome(AgentPushStatus.Unchanged, _ledger.Get(registry, selected[index].Path)!.Version,
                    LinkedAgentCopy.StillNote(owner), owner.ProjectId, owner.ProjectName));
            }

            // A copy whose registry agent is gone: of an agent deleted or renamed since it synced, reported and not uploaded
            // (that would undo the delete or rename) unless forced; or of one the registry has no memory of, backed up again.
            foreach (var (index, _, hash) in pending.ToList())
            {
                var agent = selected[index];
                if (await StillInRegistryAsync(registry, session.AccessToken, agent, agents, cancellationToken))
                    continue;
                gone.Add(index);
                if (Removal(registry, agent, list) is { } removal && !request.Force)
                {
                    pending.RemoveAll(p => p.Index == index);
                    entries[index] = new AgentPushEntry(agent, RefuseRemoved(registry, agent, hash, removal));
                }
            }

            foreach (var (index, content, hash) in unconfirmed)
            {
                var agent = selected[index];
                if (await StillInRegistryAsync(registry, session.AccessToken, agent, agents, cancellationToken))
                {
                    var recorded = _ledger.Get(registry, agent.Path)!;
                    entries[index] = new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Unchanged, recorded.Version, null, recorded.ProjectId));
                }
                else if (Removal(registry, agent, list) is { } removal)
                {
                    // Unedited since it synced, so nothing of this machine's is at risk: only report it.
                    entries[index] = new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Removed, removal.LastVersion,
                        RemovedMessage(agent.Name!, removal, edited: false), _ledger.Get(registry, agent.Path)?.ProjectId, RenamedTo: removal.RenamedTo));
                }
                else
                {
                    gone.Add(index);
                    pending.Add((index, content, hash));
                }
            }
        }

        // A recorded base is a version of the project the record names, so it is sent only to that project (#138). For each
        // project agent about to be sent with one, ask once per folder which project the folder is in now: a copy recorded under
        // another project goes with no base, and one whose folder's project cannot be told is not sent. The hook asks too, but
        // only for an edited copy it was about to upload anyway, so an unchanged agent still costs it nothing.
        var elsewhere = new HashSet<int>();
        foreach (var (index, _, _) in pending.ToList())
        {
            var agent = selected[index];
            if (agent.Scope != AgentScope.Project || gone.Contains(index) || Synced(registry, agent) is not { ProjectId: { } syncedIn } recorded)
                continue;
            var here = await HereAsync(agent);
            if (!here.Ok)
            {
                pending.RemoveAll(p => p.Index == index);
                entries[index] = new AgentPushEntry(agent, new AgentPushOutcome(AgentPushStatus.Failed, null,
                    $"Could not tell which project this folder is in now, so this copy's base (v{recorded.Version}, recorded against the project it last synced in) " +
                    "could not be checked against it. Nothing was sent; the next push tries again."));
            }
            else if (here.Value!.ProjectId != syncedIn)
            {
                elsewhere.Add(index);
            }
        }

        foreach (var (index, content, hash) in pending.OrderBy(p => p.Index))
            entries[index] = new AgentPushEntry(selected[index], await PushOneAsync(
                registry, session.AccessToken, selected[index], content, hash, request.Force, gone.Contains(index), elsewhere.Contains(index), cancellationToken));

        return new AgentPushRunResult(AgentPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
    }

    // Pushes one agent on the base its copy last synced, and records the result. With force a stale refusal is answered by
    // pushing on the version the registry just named (keep mine), and a removed one by pushing as new (base 0). A copy last
    // synced in a project its folder has since left (elsewhere) has no base in the folder's project, so it pushes base 0.
    private async Task<AgentPushOutcome> PushOneAsync(
        Uri registry, string accessToken, DiscoveredAgent agent, byte[] content, string hash, bool force, bool gone, bool elsewhere, CancellationToken cancellationToken)
    {
        var name = agent.Name!;
        var syncedIn = elsewhere ? Synced(registry, agent)?.ProjectId : null;
        var basis = gone || elsewhere ? null : Synced(registry, agent);
        var based = basis?.Version ?? 0;
        var project = ProjectOf(agent);

        var outcome = await _api.PushAgentAsync(registry, accessToken, agent.RelativePath, content, project, based, cancellationToken);
        var replaced = false;
        if (outcome.Status == AgentPushStatus.Removed && force)
        {
            gone = true;
            outcome = await _api.PushAgentAsync(registry, accessToken, agent.RelativePath, content, project, 0, cancellationToken);
        }
        else if (outcome is { Status: AgentPushStatus.Stale, Version: { } current } && force)
        {
            outcome = await _api.PushAgentAsync(registry, accessToken, agent.RelativePath, content, project, current, cancellationToken);
            replaced = outcome.Status == AgentPushStatus.Created;
            if (replaced)
                outcome = outcome with { Message = $"Replaced v{current}, which reached the registry after this copy's {Basis(based)}. v{current} stays in its history." };
        }

        if (elsewhere && outcome.Succeeded && !replaced)
        {
            // LeftProject lets the hook's line, which nobody watches, name where the copy went.
            outcome = outcome with
            {
                Message = $"Last synced as another project's agent, which this folder is no longer in, so it now belongs to this folder's project: backed it up there as a new agent.",
                LeftProject = syncedIn,
            };
        }
        else if (gone && outcome.Succeeded && !replaced)
        {
            outcome = outcome with
            {
                Message = force
                    ? $"Restored: '{name}' had been deleted or renamed in the registry; pushed this copy as a new agent named '{name}'."
                    : $"'{name}' was renamed or deleted in the registry; backed it up again as '{name}'.",
            };
        }

        if (outcome.Succeeded)
        {
            _ledger.Record(registry, agent.Path, new AgentLedgerEntry(hash, outcome.Version ?? 0, name, outcome.ProjectId));
        }
        else if (outcome.Status == AgentPushStatus.Stale)
        {
            var entry = basis ?? new AgentLedgerEntry(string.Empty, 0, name, outcome.ProjectId);
            _ledger.Record(registry, agent.Path, entry with { RefusedHash = hash, RefusedVersion = outcome.Version, RefusedAsRemoved = false, RefusedRenamedTo = null });
            outcome = outcome with { Message = StaleMessage(based, outcome.Version, syncedElsewhere: elsewhere) };
        }
        else if (outcome.Status == AgentPushStatus.Removed && basis is not null)
        {
            _ledger.Record(registry, agent.Path, basis with { RefusedHash = hash, RefusedVersion = outcome.Version, RefusedAsRemoved = true, RefusedRenamedTo = outcome.RenamedTo });
            outcome = outcome with { Message = RemovedMessage(name, new RegistryRemovedAgent(name, outcome.ProjectId, outcome.Version ?? 0, outcome.RenamedTo), edited: true) };
        }

        return outcome;
    }

    // Keep both: pushes the copy as a new registry agent named newName (its name line rewritten to match) at the path
    // <newName>.md beside the file, then moves the file there the same way. Base 0: newName must be a new agent, so an
    // existing agent of that name is refused, never overwritten.
    //
    // A new file, not the same one renamed in place as `agents rename` does: a rename replaces the agent, so its path stays
    // its own, but here both agents live on. Two registry agents stored at one path could never both be installed on a
    // machine, since a pull will not write over a file holding another agent, so the original's path is left free for it.
    private async Task<AgentPushOutcome> PushAsAsync(
        Uri registry, string accessToken, DiscoveredAgent agent, byte[] content, string newName, CancellationToken cancellationToken)
    {
        var renamed = AgentInstaller.Named(content, newName);
        if (renamed.AsSpan().SequenceEqual(content))
        {
            return new AgentPushOutcome(AgentPushStatus.Refused, null,
                "Its front matter has no `name:` line protostar can rewrite, so nothing was pushed. Set the name in the file by hand, then push it.");
        }

        var path = NewPath(agent, newName);
        var directory = Path.GetDirectoryName(agent.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var relative = (string.IsNullOrEmpty(directory) ? "" : directory.Replace(Path.DirectorySeparatorChar, '/') + "/") + newName + ".md";
        var outcome = await _api.PushAgentAsync(registry, accessToken, relative, renamed, ProjectOf(agent), 0, cancellationToken);
        if (outcome.Status == AgentPushStatus.Stale)
            return new AgentPushOutcome(AgentPushStatus.Refused, null, $"The registry already has an agent named '{newName}' with other content, so nothing was pushed. Choose another name.");
        if (!outcome.Succeeded)
            return outcome;

        var pushed = $"Pushed as '{newName}' (v{outcome.Version})";
        try
        {
            AgentInstaller.Write(path, renamed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return outcome with
            {
                Message = $"{pushed}, but {path} could not be written ({ex.Message}), so this file still holds '{agent.Name}' as it was. " +
                    $"`protostar agents pull {Quote(newName)}` installs the new agent there.",
            };
        }

        _ledger.Record(registry, path, new AgentLedgerEntry(Hash(renamed), outcome.Version ?? 0, newName, outcome.ProjectId));
        try
        {
            File.Delete(agent.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return outcome with
            {
                Message = $"{pushed} and written to {path}, but {agent.Path} could not be removed ({ex.Message}). Remove it by hand, then " +
                    $"`protostar agents pull {Quote(agent.Name!)}` installs the registry's '{agent.Name}' there.",
            };
        }

        _ledger.Forget(registry, agent.Path);
        return outcome with
        {
            Message = $"{pushed} and moved here to {path}. `protostar agents pull {Quote(agent.Name!)}` installs the registry's '{agent.Name}' back at {agent.Path}.",
        };
    }

    // Where --as moves the file: <newName>.md in the same folder.
    private static string NewPath(DiscoveredAgent agent, string newName) => Path.Combine(Path.GetDirectoryName(agent.Path)!, newName + ".md");

    // Why --as cannot go ahead: it pushes exactly one local agent under a name that can also be a file name.
    private static string? AsProblem(IReadOnlyList<string> names, IReadOnlyList<DiscoveredAgent> selected, string newName)
    {
        if (names.Count != 1)
            return "--as pushes one agent under a new name: name exactly one agent.";
        if (selected.Count != 1)
            return $"'{names[0]}' matches {selected.Count} local agents (a personal and a project agent, or several harnesses). Narrow it with --personal-only, --harness or --project.";
        if (string.IsNullOrWhiteSpace(newName) || newName != newName.Trim() || newName.EndsWith('.') ||
            newName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || newName.Any(char.IsControl))
        {
            return $"'{newName}' cannot name an agent: it becomes the file name too, so it must not be blank, start or end with a space, end with '.', " +
                "or contain / \\ : * ? \" < > | or control characters.";
        }

        if (string.Equals(newName, selected[0].Name, StringComparison.Ordinal))
            return $"'{newName}' is already this agent's name. --as takes a different one.";
        return null;
    }

    // What already answers to newName in the scope of the agent being pushed, on this machine: another agent of that name,
    // or the file it would move to. Null when free.
    private static string? LocalHolder(DiscoveredAgent agent, string newName, IReadOnlyList<DiscoveredAgent> local)
    {
        if (local.FirstOrDefault(a => a.Path != agent.Path && a.Scope == agent.Scope && Root(a) == Root(agent) && string.Equals(a.Name, newName, StringComparison.Ordinal)) is { } other)
            return $"the {Describe(other.Scope)} agent at {other.Path} is called '{other.Name}'";
        var path = NewPath(agent, newName);
        return File.Exists(path) || Directory.Exists(path) ? $"{path} already exists" : null;
    }

    // The registry's agent names, live and removed, in the scope of the agent being pushed: its folder's project's (or none
    // when the folder has no project yet), or the personal ones.
    private async Task<RegistryNames> RegistryNamesAsync(Uri registry, string accessToken, DiscoveredAgent agent, CancellationToken cancellationToken)
    {
        Guid? projectId = null;
        if (ProjectOf(agent) is { } project)
        {
            var resolved = await _projects.ResolveProjectAsync(registry, accessToken, project.MachineId, project.Facts, cancellationToken);
            if (!resolved.Ok)
                return new RegistryNames(null, [], [], AgentPushRunResult.Fail(AgentPushRunFailure.ListFailed, resolved.Error, registry));
            if (resolved.Value!.ProjectId is not { } id)
                return new RegistryNames(null, [], [], null);
            projectId = id;
        }

        var list = await _api.ListAgentsAsync(registry, accessToken, cancellationToken);
        if (list.Agents is not { } agents)
            return new RegistryNames(projectId, [], [], AgentPushRunResult.Fail(AgentPushRunFailure.ListFailed, list.Error, registry));
        return new RegistryNames(projectId, agents.Where(a => a.ProjectId == projectId).ToList(), list.Removed.Where(r => r.ProjectId == projectId).ToList(), null);
    }

    // What answers to newName in the registry's scope: an agent, or a removed one whose copies may still be about. Null when free.
    private static string? RegistryHolder(string newName, RegistryNames names)
    {
        var where = names.ProjectId is null ? "personal agent" : "agent in this project";
        if (names.Agents.Any(a => string.Equals(a.Name, newName, StringComparison.Ordinal)))
            return $"the registry already has a {where} named '{newName}'";
        if (names.Removed.FirstOrDefault(r => string.Equals(r.Name, newName, StringComparison.Ordinal)) is { } removed)
        {
            var how = removed.RenamedTo is { } renamedTo ? $"renamed to '{renamedTo}'" : "deleted";
            return $"the registry had a {where} named '{newName}' ({how} at v{removed.LastVersion}), and copies of it may still be synced under that name";
        }

        return null;
    }

    // Why --as cannot use newName, with a free name to try instead: the first of newName-2, newName-3, ... that nothing here,
    // and nothing in the registry when it was asked, answers to.
    private static string Taken(string newName, string holder, DiscoveredAgent agent, IReadOnlyList<DiscoveredAgent> local, RegistryNames? names)
    {
        var suggestion = Enumerable.Range(2, 98).Select(n => $"{newName}-{n}")
            .FirstOrDefault(candidate => LocalHolder(agent, candidate, local) is null && (names is null || RegistryHolder(candidate, names) is null));
        var instead = suggestion is null ? "Choose another name." : $"Choose another name, such as '{suggestion}'.";
        return $"'{newName}' is already taken: {holder}. Nothing was pushed or changed. {instead}";
    }

    private static string Quote(string name) => name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;

    // The registry's agent names in one scope (a project, or personal when ProjectId is null), or the failure that stops the run.
    private sealed record RegistryNames(Guid? ProjectId, IReadOnlyList<RegistryAgent> Agents, IReadOnlyList<RegistryRemovedAgent> Removed, AgentPushRunResult? Failure);

    // Whether the registry agent this copy last synced as is still listed. A copy that never synced counts as present.
    // An agent of that name whose versions start after the copy's is a new agent that reused the name, not this one. One
    // a link moved to another project is followed there (MovedAgentLedger.FollowAsync).
    private async Task<bool> StillInRegistryAsync(Uri registry, string accessToken, DiscoveredAgent agent, IReadOnlyList<RegistryAgent> agents, CancellationToken cancellationToken)
    {
        if (Synced(registry, agent) is not { } recorded)
            return true;

        if (agents.Any(a => a.ProjectId == recorded.ProjectId && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal) &&
                            !(recorded.Version >= 1 && recorded.Version < a.FirstVersion)))
            return true;

        foreach (var moved in agents.Where(a => a.ProjectId is not null && a.ProjectId != recorded.ProjectId && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal)))
        {
            var followed = await MovedAgentLedger.FollowAsync(_api, _ledger, registry, accessToken, agent.Path, recorded, moved, agents, cancellationToken);
            if (followed?.ProjectId == moved.ProjectId)
                return true;
        }

        return false;
    }

    private RegistryRemovedAgent? Removal(Uri registry, DiscoveredAgent agent, RegistryAgentList list) =>
        Synced(registry, agent) is { } recorded ? list.RemovalOf(recorded.Name, recorded.ProjectId, recorded.Version) : null;

    // An edited copy of a removed agent: nothing is uploaded, and the refusal is recorded so the hook does not send it again.
    private AgentPushOutcome RefuseRemoved(Uri registry, DiscoveredAgent agent, string hash, RegistryRemovedAgent removal)
    {
        var recorded = Synced(registry, agent)!;
        _ledger.Record(registry, agent.Path, recorded with { RefusedHash = hash, RefusedVersion = removal.LastVersion, RefusedAsRemoved = true, RefusedRenamedTo = removal.RenamedTo });
        return new AgentPushOutcome(AgentPushStatus.Removed, removal.LastVersion, RemovedMessage(agent.Name!, removal, edited: true), recorded.ProjectId, RenamedTo: removal.RenamedTo);
    }

    private AgentLedgerEntry? Synced(Uri registry, DiscoveredAgent agent) =>
        _ledger.Get(registry, agent.Path) is { } recorded && Vouches(recorded, agent) ? recorded : null;

    // A record vouches for a copy's base only when it was synced as this agent: the same scope and the same name.
    private static bool Vouches(AgentLedgerEntry recorded, DiscoveredAgent agent) =>
        recorded.ProjectId is not null == (agent.Scope == AgentScope.Project) &&
        string.Equals(recorded.Name, agent.Name, StringComparison.Ordinal) && recorded.ContentHash.Length > 0;

    private SkillProject? ProjectOf(DiscoveredAgent agent) =>
        agent.Scope == AgentScope.Project ? new SkillProject(_machine.Get().Id, _facts.Resolve(agent.ProjectRoot!)) : null;

    private List<DiscoveredAgent> Discover(string? harnessId, string? harnessHome, string? projectStart, out AgentPushRunFailure failure)
    {
        failure = AgentPushRunFailure.None;
        IEnumerable<IHarness> harnesses = _catalog.All;
        if (harnessId is not null)
        {
            var harness = _catalog.ById(harnessId);
            failure = harness is null ? AgentPushRunFailure.UnknownHarness : harness is IAgentCapability ? AgentPushRunFailure.None : AgentPushRunFailure.Unsupported;
            if (failure != AgentPushRunFailure.None)
                return [];
            harnesses = [harness!];
        }

        return harnesses
            .Where(h => h is IAgentCapability)
            .SelectMany(h => h.TryLocate(harnessHome, out var location) ? ((IAgentCapability)h).DiscoverAgents(location, projectStart) : [])
            .OrderBy(a => a.Scope)
            .ThenBy(a => a.Name ?? a.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Root(DiscoveredAgent agent) =>
        agent.ProjectRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(agent.ProjectRoot));

    private static string? Hash(DiscoveredAgent agent)
    {
        try
        {
            return Hash(File.ReadAllBytes(agent.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The lowercase hex SHA-256 of an agent file: what the ledger keeps, and the registry's content hash ignoring case.</summary>
    internal static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static AgentPushOutcome CompareWithFirst(DiscoveredAgent first, string? firstHash, string hash)
    {
        var where = $"the {Describe(first.Scope)} agent at {first.Path}";
        if (firstHash == hash)
            return new AgentPushOutcome(AgentPushStatus.Skipped, null, $"Identical to {where}.");
        return new AgentPushOutcome(AgentPushStatus.Conflict, null,
            $"Differs from {where}, which has the same name. The registry holds one {Describe(first.Scope)} agent per name, so this copy was not backed up.");
    }

    internal static string Describe(AgentScope scope) => scope.ToString().ToLowerInvariant();

    private static string NoManifestMessage(DiscoveredAgent agent)
    {
        var missing = agent.Name is null ? "name" : "description";
        return $"{agent.RelativePath} has no '{missing}' in its front matter, so Claude Code does not load it and it was not backed up. " +
            $"Add '{missing}: ...' between the '---' lines at the top of the file.";
    }

    private static string Basis(int based) => based == 0 ? "last sync (it never synced a version)" : $"v{based}";

    internal static string StaleMessage(int based, int? current, bool syncedElsewhere = false) =>
        $"Changed here and in the registry: the registry has v{current}, but " + (based != 0
            ? $"this copy is based on v{based}."
            : syncedElsewhere
                ? "this copy last synced as another project's agent, not as this project's, so it is based on none of its versions."
                : "this copy is based on no registry version (it was never pushed from here).") +
        " Nothing was overwritten.";

    // Why a copy of a removed agent was not backed up, and the ways out.
    internal static string RemovedMessage(string name, RegistryRemovedAgent removal, bool edited)
    {
        var quoted = name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;
        var changed = edited ? "Changed here, but " : "";
        var kept = edited ? "Nothing was uploaded and your changes are kept here." : "It was not uploaded again.";
        if (removal.Replaced)
        {
            return $"{changed}'{name}' was deleted from the registry (at v{removal.LastVersion}) after this copy last synced, and a new agent named '{name}' " +
                $"has been created since. This copy is of the old one. {kept} Run `protostar agents push {quoted} --force` to replace the new one with it.";
        }

        if (removal.RenamedTo is not { } renamedTo)
        {
            return $"{changed}'{name}' was deleted from the registry (at v{removal.LastVersion}) after this copy last synced. {kept} " +
                $"Run `protostar agents push {quoted} --force` to restore it{(edited ? " with your changes" : "")}, or remove this copy.";
        }

        return $"{changed}'{name}' was renamed to '{renamedTo}' in the registry (at v{removal.LastVersion}) after this copy last synced. {kept} " +
            $"Run `protostar agents push {quoted} --force` to keep it as a separate agent named '{name}', or remove this copy.";
    }
}
