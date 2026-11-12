using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Skills;

/// <summary>
/// Production <see cref="ISkillPushService"/>: discovery through <see cref="ISkillService"/>, the session
/// through <see cref="IAccessTokenProvider"/>, and uploads through <see cref="IRegistryApi"/>.
/// </summary>
/// <remarks>
/// Everything that can stop the whole run (bad URL, unknown skill, no session, unreachable or
/// incompatible registry) is checked before the first upload, so a run either uploads every skill it
/// can or uploads nothing.
/// </remarks>
internal sealed class SkillPushService : ISkillPushService
{
    private readonly ISkillService _skills;
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _registry;

    public SkillPushService(ISkillService skills, IAccessTokenProvider tokens, IRegistryApi registry)
    {
        _skills = skills;
        _tokens = tokens;
        _registry = registry;
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

        if (selected.Count == 0)
            return new SkillPushRunResult(SkillPushRunFailure.None, null, registry, []);

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

        var entries = new List<SkillPushEntry>(selected.Count);
        var pushed = new Dictionary<string, DiscoveredSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in selected)
        {
            if (pushed.TryGetValue(skill.Name, out var first))
            {
                entries.Add(new SkillPushEntry(skill, new SkillPushOutcome(
                    SkillPushStatus.Skipped, null, $"Same name as the {first.Scope.ToString().ToLowerInvariant()} skill at {first.Path}.")));
                continue;
            }

            pushed[skill.Name] = skill;
            entries.Add(new SkillPushEntry(skill, await PushOneAsync(registry, session.AccessToken, skill, cancellationToken)));
        }

        return new SkillPushRunResult(SkillPushRunFailure.None, null, registry, entries);
    }

    private async Task<SkillPushOutcome> PushOneAsync(Uri registry, string accessToken, DiscoveredSkill skill, CancellationToken cancellationToken)
    {
        IReadOnlyList<SkillUploadFile> files;
        try
        {
            files = SkillFileReader.Read(skill.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SkillPushOutcome(SkillPushStatus.Failed, null, $"Could not read the skill's files: {ex.Message}");
        }

        return await _registry.PushSkillAsync(registry, accessToken, skill.Name, files, cancellationToken);
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
