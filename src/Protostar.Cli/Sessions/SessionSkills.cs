using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Sessions;

/// <summary>The skills a shown session used, from the registry, each marked with whether it is installed on this machine.</summary>
internal interface ISessionSkills
{
    /// <summary>The skills <paramref name="session"/> used (its subagents' included), in the registry's order; <c>null</c> when they cannot be listed.</summary>
    /// <param name="harnessHome">The harness config root to look for local skills in; <c>null</c> for the harness's default.</param>
    /// <param name="workingDirectory">Where the command runs; its project's skills count as installed when it is the session's project.</param>
    /// <remarks>
    /// Optional decoration for <c>sessions show</c>, so it never fails: a registry that lists capabilities without
    /// <c>session-skills</c> is not asked, and one that lists none is asked anyway, with any refusal (a 404 on an older
    /// registry) or failure giving <c>null</c>.
    /// </remarks>
    Task<IReadOnlyList<UsedSkill>?> GetAsync(OpenedSession session, string? harnessHome, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>One skill a session used, as the registry reports it, and whether a skill of that name is installed here.</summary>
/// <param name="InstalledHere">
/// <c>null</c> when it was not looked for: a plugin skill (its name is <c>plugin:skill</c>), which comes from a plugin
/// rather than a skill folder, or a harness protostar cannot list skills for.
/// </param>
internal sealed record UsedSkill(SessionSkill Skill, bool? InstalledHere)
{
    /// <summary>Whether it is a plugin's skill, named <c>plugin:skill</c>.</summary>
    public bool FromPlugin => IsPluginName(Skill.Name);

    /// <summary>Whether <paramref name="name"/> is a plugin skill's: skill folder names cannot hold a colon.</summary>
    public static bool IsPluginName(string name) => name.Contains(':', StringComparison.Ordinal);
}

/// <summary>Production <see cref="ISessionSkills"/>.</summary>
/// <remarks>
/// <para>
/// One request to the registry (<c>GET /v1/transcripts/{harness}/{key}/skills</c>), sent before the local skills are
/// read so the two overlap, and meant to be started alongside the session's content download.
/// </para>
/// <para>
/// "Installed here" follows the order Claude Code loads skills in, as the registry's match does: a personal skill of
/// that name, then a project skill of the project the command runs in, but only when that is the session's project.
/// Telling whether it is costs a request (the registry resolves the folder to its project), so it is asked only when a
/// used skill is missing from the personal skills and present in the folder's project, and at most once.
/// </para>
/// </remarks>
internal sealed class SessionSkills : ISessionSkills
{
    private readonly IRegistryCapabilities _capabilities;
    private readonly ITranscriptApi _api;
    private readonly ISkillService _skills;
    private readonly IProjectFactsResolver _facts;
    private readonly IProjectApi _projects;
    private readonly IMachineIdentity _machine;

    public SessionSkills(
        IRegistryCapabilities capabilities, ITranscriptApi api, ISkillService skills, IProjectFactsResolver facts, IProjectApi projects,
        IMachineIdentity machine)
    {
        _capabilities = capabilities;
        _api = api;
        _skills = skills;
        _facts = facts;
        _projects = projects;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsedSkill>?> GetAsync(OpenedSession session, string? harnessHome, string workingDirectory, CancellationToken cancellationToken)
    {
        // Opening the session already read /v1/meta, which is kept, so this asks nothing.
        var check = await _capabilities.RequireAsync(session.Registry, cancellationToken);
        if (check.Supports(RegistryCapability.SessionSkills) == CapabilitySupport.Missing)
            return null;

        var transcript = session.Detail.Transcript;
        var asked = _api.GetSkillsAsync(session.Registry, session.AccessToken, transcript.Harness, transcript.Key, cancellationToken);
        var local = _skills.Discover(transcript.Harness, harnessHome, workingDirectory);
        var listed = await asked;
        if (!listed.Ok)
            return null;

        var personal = local.Skills.Where(s => s.Scope == SkillScope.Global).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var here = local.Skills.Where(s => s.Scope == SkillScope.Project).DistinctBy(s => s.Name, StringComparer.Ordinal).ToDictionary(s => s.Name, s => s.ProjectRoot!, StringComparer.Ordinal);
        bool? inSessionProject = null;
        var used = new List<UsedSkill>();
        foreach (var skill in listed.Value!)
        {
            bool? installed;
            if (local.Failure != SkillQueryFailure.None || UsedSkill.IsPluginName(skill.Name))
                installed = null;
            else if (personal.Contains(skill.Name))
                installed = true;
            else if (here.TryGetValue(skill.Name, out var projectRoot))
                installed = inSessionProject ??= await IsSessionProjectAsync(session, projectRoot, cancellationToken);
            else
                installed = false;
            used.Add(new UsedSkill(skill, installed));
        }

        return used;
    }

    // Whether the project folder is the session's project, as the registry files it. A folder it cannot resolve is not.
    private async Task<bool> IsSessionProjectAsync(OpenedSession session, string projectRoot, CancellationToken cancellationToken)
    {
        if (session.Detail.Transcript.ProjectId is not { } sessionProject)
            return false;
        var facts = _facts.Resolve(Path.GetFullPath(projectRoot));
        var resolved = await _projects.ResolveProjectAsync(session.Registry, session.AccessToken, _machine.Get().Id, facts, cancellationToken);
        return resolved.Ok && resolved.Value!.ProjectId == sessionProject;
    }
}
