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
/// A record of a project agent that a link on another machine moved is followed when the list shows an agent of that
/// name in another project at exactly the version and content the record synced, and none left in the recorded project.
/// That is the cheap half of <see cref="Skills.MovedSkillLedger.FollowAsync"/>; the half that downloads an older version
/// waits for agent downloads (7.2).
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

    public AgentPushService(
        IHarnessCatalog catalog, IAccessTokenProvider tokens, IAgentApi api, IRegistryCapabilities capabilities, IAgentLedger ledger,
        IProjectFactsResolver facts, IMachineIdentity machine)
    {
        _catalog = catalog;
        _tokens = tokens;
        _api = api;
        _capabilities = capabilities;
        _ledger = ledger;
        _facts = facts;
        _machine = machine;
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

        var entries = new AgentPushEntry?[selected.Count];
        var pending = new List<(int Index, byte[] Content, string Hash)>();
        var unconfirmed = new List<(int Index, byte[] Content, string Hash)>();
        var verify = !request.FromHook;
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
            if (!request.Force && recorded is not null && Vouches(recorded, agent) && recorded.ContentHash == hash)
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

        var gone = new HashSet<int>();
        if (mustList)
        {
            var list = await _api.ListAgentsAsync(registry, session.AccessToken, cancellationToken);
            if (list.Agents is not { } agents)
                return AgentPushRunResult.Fail(AgentPushRunFailure.ListFailed, list.Error, registry);

            // A copy whose registry agent is gone: of an agent deleted or renamed since it synced, reported and not uploaded
            // (that would undo the delete or rename) unless forced; or of one the registry has no memory of, backed up again.
            foreach (var (index, _, hash) in pending.ToList())
            {
                var agent = selected[index];
                if (StillInRegistry(registry, agent, agents))
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
                if (StillInRegistry(registry, agent, agents))
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

        foreach (var (index, content, hash) in pending.OrderBy(p => p.Index))
            entries[index] = new AgentPushEntry(selected[index], await PushOneAsync(registry, session.AccessToken, selected[index], content, hash, request.Force, gone.Contains(index), cancellationToken));

        return new AgentPushRunResult(AgentPushRunFailure.None, null, registry, entries.Select(e => e!).ToList());
    }

    // Pushes one agent on the base its copy last synced, and records the result. With force a stale refusal is answered by
    // pushing on the version the registry just named (keep mine), and a removed one by pushing as new (base 0).
    private async Task<AgentPushOutcome> PushOneAsync(
        Uri registry, string accessToken, DiscoveredAgent agent, byte[] content, string hash, bool force, bool gone, CancellationToken cancellationToken)
    {
        var name = agent.Name!;
        var basis = gone ? null : Synced(registry, agent);
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

        if (gone && outcome.Succeeded && !replaced)
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
            outcome = outcome with { Message = StaleMessage(based, outcome.Version) };
        }
        else if (outcome.Status == AgentPushStatus.Removed && basis is not null)
        {
            _ledger.Record(registry, agent.Path, basis with { RefusedHash = hash, RefusedVersion = outcome.Version, RefusedAsRemoved = true, RefusedRenamedTo = outcome.RenamedTo });
            outcome = outcome with { Message = RemovedMessage(name, new RegistryRemovedAgent(name, outcome.ProjectId, outcome.Version ?? 0, outcome.RenamedTo), edited: true) };
        }

        return outcome;
    }

    // Whether the registry agent this copy last synced as is still listed. A copy that never synced counts as present.
    // An agent of that name whose versions start after the copy's is a new agent that reused the name, not this one. One
    // a link moved to another project, at exactly the synced version and content, is followed there.
    private bool StillInRegistry(Uri registry, DiscoveredAgent agent, IReadOnlyList<RegistryAgent> agents)
    {
        if (Synced(registry, agent) is not { } recorded)
            return true;

        if (agents.Any(a => a.ProjectId == recorded.ProjectId && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal) &&
                            !(recorded.Version >= 1 && recorded.Version < a.FirstVersion)))
            return true;

        if (recorded.ProjectId is null || agents.Any(a => a.ProjectId == recorded.ProjectId && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal)))
            return false;
        var moved = agents.FirstOrDefault(a => a.ProjectId is not null && string.Equals(a.Name, recorded.Name, StringComparison.Ordinal) &&
            a.Version == recorded.Version && string.Equals(a.ContentHash, recorded.ContentHash, StringComparison.OrdinalIgnoreCase));
        if (moved is null)
            return false;
        _ledger.Record(registry, agent.Path, recorded with { ProjectId = moved.ProjectId });
        return true;
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

    internal static string StaleMessage(int based, int? current) =>
        $"Changed here and in the registry: the registry has v{current}, but this copy is based on " +
        (based == 0 ? "no registry version (it was never pushed from here)." : $"v{based}.") + " Nothing was overwritten.";

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
