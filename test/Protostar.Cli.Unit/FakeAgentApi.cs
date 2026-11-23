using System.Text;
using System.Text.RegularExpressions;
using Protostar.Cli.Agents;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Unit;

/// <summary>
/// An in-memory registry with the registry's agent rules: unchanged by bytes, stale by base, tombstones by last version, and
/// rename that keeps the stored files (and so their old <c>name</c> line), as <c>POST /v1/agents/{name}/rename</c> does.
/// </summary>
internal sealed class FakeAgentApi : IAgentApi
{
    private readonly Dictionary<(Guid?, string), Stored> _agents = [];
    private readonly Dictionary<(Guid?, string), (int Last, string? RenamedTo)> _removed = [];

    public List<(string Path, SkillProject? Project)> Pushed { get; } = [];

    public List<int> Bases { get; } = [];

    public int ListCalls { get; private set; }

    public List<(string Name, int? Version)> Downloads { get; } = [];

    /// <summary>The text of an agent file named <paramref name="name"/>.</summary>
    public static string File(string name, string body) => $"---\nname: {name}\ndescription: A test agent.\n---\n{body}\n";

    /// <summary>Another machine pushes a new version of an existing agent with <paramref name="body"/>.</summary>
    public void PushFromElsewhere(Guid? projectId, string name, string body)
    {
        var agent = _agents[(projectId, name)];
        agent.Add(Encoding.UTF8.GetBytes(File(name, body)), agent.Path);
    }

    /// <summary>Puts a new personal or project agent (or a new version of one) in the registry, as a push from elsewhere would.</summary>
    public void Publish(Guid? projectId, string path, string name, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(File(name, body));
        if (_agents.TryGetValue((projectId, name), out var agent))
        {
            agent.Add(bytes, path);
            return;
        }

        var first = _removed.TryGetValue((projectId, name), out var removed) ? removed.Last + 1 : 1;
        _removed.Remove((projectId, name));
        var created = new Stored(name, first);
        created.Add(bytes, path);
        _agents[(projectId, name)] = created;
    }

    public void Delete(Guid? projectId, string name)
    {
        _removed[(projectId, name)] = (_agents[(projectId, name)].Version, null);
        _agents.Remove((projectId, name));
    }

    public void Rename(Guid? projectId, string name, string newName)
    {
        var agent = _agents[(projectId, name)];
        _agents.Remove((projectId, name));
        agent.Name = newName;
        _agents[(projectId, newName)] = agent;
        _removed[(projectId, name)] = (agent.Version, newName);
        _removed.Remove((projectId, newName));
    }

    public void MoveProject(Guid? from, Guid to, string name)
    {
        _agents[(to, name)] = _agents[(from, name)];
        _agents.Remove((from, name));
    }

    public Task<AgentPushOutcome> PushAgentAsync(
        Uri registry, string accessToken, string relativePath, byte[] content, SkillProject? project, int baseVersion, CancellationToken cancellationToken)
    {
        Pushed.Add((relativePath, project));
        Bases.Add(baseVersion);
        var name = Regex.Match(Encoding.UTF8.GetString(content), "^name: (.+)$", RegexOptions.Multiline).Groups[1].Value;
        Guid? projectId = project is null ? null : SkillPushServiceTests.FakeRegistryApi.ProjectIdOf(project.Facts);
        var key = (projectId, name);

        if (!_agents.TryGetValue(key, out var agent))
        {
            var last = _removed.GetValueOrDefault(key).Last;
            if (baseVersion >= 1 && baseVersion <= last)
                return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Removed, last, "removed", projectId, RenamedTo: _removed[key].RenamedTo));
            _removed.Remove(key);
            agent = new Stored(name, last + 1);
            agent.Add(content, relativePath);
            _agents[key] = agent;
            return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Created, agent.Version, null, projectId));
        }

        if (agent.Hash == Hash(content))
            return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Unchanged, agent.Version, null, projectId));
        if (baseVersion != agent.Version)
            return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Stale, agent.Version, "stale", projectId));
        agent.Add(content, agent.Path);
        return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Created, agent.Version, null, projectId));
    }

    public Task<ApiResult<RemoteAgentContent>> GetRedactedAgentContentAsync(
        Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ApiResult<RemoteAgentContent>> GetAgentContentAsync(
        Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken)
    {
        Downloads.Add((name, version));
        if (!_agents.TryGetValue((projectId, name), out var agent) || !agent.Versions.TryGetValue(version ?? agent.Version, out var stored))
            return Task.FromResult(ApiResult<RemoteAgentContent>.UnknownAgent($"You have no version {version} of agent '{name}'."));
        return Task.FromResult(ApiResult<RemoteAgentContent>.Success(new RemoteAgentContent
        {
            Name = agent.Name, ProjectId = projectId, Version = version ?? agent.Version, Path = stored.Path, ContentHash = Hash(stored.Bytes), Content = stored.Bytes,
        }));
    }

    public Task<RegistryAgentList> ListAgentsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
    {
        ListCalls++;
        return Task.FromResult(new RegistryAgentList(
            _agents.Select(a => new RegistryAgent(a.Value.Name, a.Value.Version, a.Value.Hash, a.Key.Item1, a.Key.Item1 is null ? null : "app", a.Value.First, "A test agent.", a.Value.Path)).ToList(),
            _removed.Select(r => new RegistryRemovedAgent(r.Key.Item2, r.Key.Item1, r.Value.Last, r.Value.RenamedTo)).ToList(),
            null));
    }

    private static string Hash(byte[] content) => AgentPushService.Hash(content).ToUpperInvariant();

    private sealed class Stored(string name, int first)
    {
        public string Name { get; set; } = name;

        public int First { get; } = first;

        public int Version { get; private set; } = first - 1;

        public Dictionary<int, (byte[] Bytes, string Path)> Versions { get; } = [];

        public string Path => Versions[Version].Path;

        public string Hash => FakeAgentApi.Hash(Versions[Version].Bytes);

        public void Add(byte[] bytes, string path) => Versions[++Version] = (bytes, path);
    }
}
