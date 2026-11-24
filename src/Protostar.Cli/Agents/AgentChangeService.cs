using Protostar.Cli.Auth;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Agents;

/// <summary>Production <see cref="IAgentChangeService"/>.</summary>
internal sealed class AgentChangeService : IAgentChangeService
{
    private readonly IAccessTokenProvider _tokens;
    private readonly IAgentApi _api;
    private readonly IRegistryCapabilities _capabilities;
    private readonly IAgentLedger _ledger;
    private readonly IProjectFactsResolver _facts;
    private readonly IProjectApi _projects;
    private readonly IMachineIdentity _machine;

    public AgentChangeService(
        IAccessTokenProvider tokens, IAgentApi api, IRegistryCapabilities capabilities, IAgentLedger ledger, IProjectFactsResolver facts,
        IProjectApi projects, IMachineIdentity machine)
    {
        _tokens = tokens;
        _api = api;
        _capabilities = capabilities;
        _ledger = ledger;
        _facts = facts;
        _projects = projects;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<AgentTargetResult> FindAsync(AgentTargetRequest request, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(request.Registry);
        }
        catch (FormatException ex)
        {
            return AgentTargetResult.Fail(AgentPullRunFailure.InvalidRegistry, ex.Message);
        }

        var lookup = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (lookup is not { State: SessionState.Current, Session: { } session })
        {
            var failure = lookup.State switch
            {
                SessionState.None => AgentPullRunFailure.NotLoggedIn,
                SessionState.RegistryUnreachable => AgentPullRunFailure.RegistryUnreachable,
                _ => AgentPullRunFailure.SessionExpired,
            };
            return AgentTargetResult.Fail(failure, null, registry);
        }

        // Rename and delete are part of the `agents` capability; a project agent needs the registry's projects too.
        var check = request.ProjectStart is null
            ? await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Agents)
            : await _capabilities.RequireAsync(registry, cancellationToken, RegistryCapability.Agents, RegistryCapability.Projects);
        if (!check.Ok)
        {
            var failure = check.Failure == RegistryCheckFailure.Unreachable ? AgentPullRunFailure.RegistryUnreachable
                : check.Missing is not null ? AgentPullRunFailure.RegistryTooOld
                : AgentPullRunFailure.Incompatible;
            return AgentTargetResult.Fail(failure, check.Detail, registry);
        }

        var list = await _api.ListAgentsAsync(registry, session.AccessToken, cancellationToken);
        if (list.Agents is not { } agents)
            return AgentTargetResult.Fail(AgentPullRunFailure.ListFailed, list.Error, registry);

        Guid? hereProject = null;
        string? hereProblem = null;
        if (request.ProjectStart is { } start)
        {
            var resolved = await _projects.ResolveProjectAsync(registry, session.AccessToken, _machine.Get().Id, _facts.Resolve(Path.GetFullPath(start)), cancellationToken);
            if (resolved.Ok)
                hereProject = resolved.Value!.ProjectId;
            else
                hereProblem = resolved.Error;
        }

        var named = agents.Where(a => string.Equals(a.Name, request.Name, StringComparison.Ordinal)).ToList();
        var project = hereProject is { } id ? named.FirstOrDefault(a => a.ProjectId == id) : null;
        var personal = request.ProjectOnly ? null : named.FirstOrDefault(a => a.ProjectId is null);
        if (project is not null && personal is not null)
            return AgentTargetResult.Fail(AgentPullRunFailure.Ambiguous, Ambiguous(request.Name, project), registry);
        if ((project ?? personal) is not { } agent)
            return AgentTargetResult.Fail(AgentPullRunFailure.AgentNotFound, NotFound(request, registry, named, hereProject, hereProblem), registry);

        return new AgentTargetResult(AgentPullRunFailure.None, null, registry,
            new AgentTarget(registry, session.AccessToken, agent, await CopiesOfAsync(registry, session.AccessToken, agent, agents, cancellationToken)));
    }

    /// <inheritdoc />
    public async Task<AgentDeleteResult> DeleteAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        var deleted = await _api.DeleteAgentAsync(target.Registry, target.AccessToken, target.Agent.Name, target.Agent.ProjectId, cancellationToken);
        return new AgentDeleteResult(target, deleted.Error);
    }

    /// <inheritdoc />
    public async Task<AgentRenameResult> RenameAsync(AgentTarget target, string newName, CancellationToken cancellationToken)
    {
        var agent = target.Agent;
        var edited = target.Copies.Where(c => c.HasLocalChanges).ToList();
        if (edited.Count > 0)
            return new AgentRenameResult(target, null, null, edited, []);

        var renamed = await _api.RenameAgentAsync(target.Registry, target.AccessToken, agent.Name, agent.ProjectId, newName, cancellationToken);
        if (!renamed.Ok)
            return new AgentRenameResult(target, null, renamed.Error, [], []);
        newName = renamed.Value!;
        if (newName == agent.Name)
            return new AgentRenameResult(target, newName, null, [], []);

        return new AgentRenameResult(target, newName, null, [], target.Copies.Select(c => RenameCopy(target.Registry, c, newName)).ToList());
    }

    // Sets an unedited copy's name line and records it under the new name, still at the version it synced. A copy that
    // cannot be rewritten keeps its old record, so the next pull here follows the rename as it would on any other machine.
    private LocalAgentRename RenameCopy(Uri registry, LocalAgentCopy copy, string newName)
    {
        byte[] bytes;
        try
        {
            var current = File.ReadAllBytes(copy.Path);
            bytes = AgentInstaller.Named(current, newName);
            if (bytes.AsSpan().SequenceEqual(current))
                return new LocalAgentRename(copy.Path, false, "its front matter has no `name:` line protostar can rewrite; set it by hand");
            AgentInstaller.Write(copy.Path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalAgentRename(copy.Path, false, $"its `name:` line could not be updated ({ex.Message}); `protostar agents pull` renames it to match");
        }

        _ledger.Record(registry, copy.Path, copy.Recorded with { Name = newName, ContentHash = AgentPushService.Hash(bytes) });
        return new LocalAgentRename(copy.Path, true, null);
    }

    // The files the ledger records as synced with exactly this registry agent (name and project) that still exist,
    // including those recorded under the project a link moved the agent out of, once their record follows it.
    private async Task<List<LocalAgentCopy>> CopiesOfAsync(
        Uri registry, string accessToken, RegistryAgent agent, IReadOnlyList<RegistryAgent> agents, CancellationToken cancellationToken)
    {
        var copies = new List<LocalAgentCopy>();
        foreach (var (path, entry) in _ledger.Entries(registry).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!string.Equals(entry.Name, agent.Name, StringComparison.Ordinal) || entry.ContentHash.Length == 0 || !File.Exists(path))
                continue;
            var recorded = await MovedAgentLedger.FollowAsync(_api, _ledger, registry, accessToken, path, entry, agent, agents, cancellationToken);
            if (recorded!.ProjectId != agent.ProjectId)
                continue;

            try
            {
                copies.Add(new LocalAgentCopy(path, recorded, AgentPushService.Hash(File.ReadAllBytes(path)) != recorded.ContentHash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: it cannot be shown to be free of local work.
                copies.Add(new LocalAgentCopy(path, recorded, true));
            }
        }

        return copies;
    }

    private static string Ambiguous(string name, RegistryAgent project) =>
        $"'{name}' is both a personal agent and an agent of project {project.ProjectName ?? project.ProjectId.ToString()}. " +
        "Pass --personal-only for the personal one, or --project <DIR> for the project's.";

    private static string NotFound(AgentTargetRequest request, Uri registry, IReadOnlyList<RegistryAgent> named, Guid? hereProject, string? hereProblem)
    {
        var authority = registry.GetLeftPart(UriPartial.Authority);
        var message = request.ProjectStart is null ? $"There is no personal agent named '{request.Name}' in {authority}."
            : request.ProjectOnly ? $"There is no agent named '{request.Name}' in this folder's project in {authority}."
            : $"There is no agent named '{request.Name}' among your personal agents or this folder's project agents in {authority}.";
        if (request.ProjectOnly && named.Any(a => a.ProjectId is null))
            message += " There is a personal agent of that name: pass --personal-only instead of --project.";
        var elsewhere = named.Where(a => a.ProjectId is { } p && p != hereProject).Select(a => a.ProjectName ?? a.ProjectId.ToString()).ToList();
        if (elsewhere.Count > 0)
            message += $" It is an agent of project {string.Join(", ", elsewhere)}: run this from a checkout of that project, or pass --project <DIR>.";
        if (hereProblem is not null)
            message += $" (Could not tell which project this folder is in: {hereProblem})";
        return message + " Run `protostar agents remote` to see what is there.";
    }
}
