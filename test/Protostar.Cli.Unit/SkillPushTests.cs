using System.Net;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="ISkillPushService"/> with in-memory fakes for discovery, session, and registry.</summary>
public sealed class SkillPushServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-push-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DiscoveredSkill Skill(string name, SkillScope scope = SkillScope.Global, string? dir = null)
    {
        var path = Path.Combine(_root, dir ?? $"{scope}-{name}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\n---\nbody");
        var projectRoot = scope == SkillScope.Project ? Path.Combine(_root, "repo") : null;
        return new DiscoveredSkill { Name = name, Scope = scope, Path = path, ProjectRoot = projectRoot };
    }

    private static StoredToken Session(bool expired = false) => new()
    {
        Registry = Registry,
        AccessToken = "token-1",
        ExpiresAtUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddHours(1),
    };

    private static SkillPushRequest Request(params string[] names) => new(names, null, null, null, Registry);

    private readonly FakeLedger _ledger = new();

    // Registry skills the ledger records were synced from, listed by every registry Build makes.
    private readonly List<RegistrySkill> _inRegistry = [];
    private readonly FakeFacts _facts = new();
    private readonly FakeProjects _projects = new();

    private (ISkillPushService Sut, FakeRegistryApi Api) Build(
        IEnumerable<DiscoveredSkill> skills, StoredToken? session = null, FakeRegistryApi? api = null, SessionLookup? lookup = null)
    {
        api ??= new FakeRegistryApi();
        api.Skills.AddRange(_inRegistry.Where(r => !api.Skills.Contains(r)));
        var sut = new SkillPushService(new FakeSkillService(skills.ToList()), new FakeTokens(lookup ?? SessionLookups.Of(session)), api, new RegistryCapabilities(api), _ledger, _facts, new FakeMachine(), _projects);
        return (sut, api);
    }

    [Fact]
    public async Task A_successful_push_is_recorded_and_the_next_run_skips_the_network()
    {
        var (sut, api) = Build([Skill("alpha")], Session());

        await sut.PushAsync(Request(), CancellationToken.None);
        var second = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Single(api.Pushed);
        Assert.Equal(1, api.MetaCalls);
        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Unchanged, 1, null), Assert.Single(second.Entries).Outcome);
    }

    [Fact]
    public async Task The_hook_takes_the_ledgers_word_for_an_unchanged_skill_with_no_session_and_no_request()
    {
        var skill = Skill("alpha");
        var (seed, _) = Build([skill], Session());
        await seed.PushAsync(Request(), CancellationToken.None);
        var (sut, api) = Build([skill], session: null);

        var result = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Empty(api.Pushed);
        Assert.Equal(0, api.ListCalls);
        Assert.Equal(0, api.MetaCalls);
    }

    [Fact]
    public async Task The_command_confirms_unchanged_skills_with_one_list_request_and_nothing_else()
    {
        var alpha = Skill("alpha");
        var beta = Skill("beta");
        var (seed, api) = Build([alpha, beta], Session());
        await seed.PushAsync(Request(), CancellationToken.None);
        var (sut, _) = Build([alpha, beta], Session(), api);
        var metaBefore = api.MetaCalls;

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.All(result.Entries, e => Assert.Equal(SkillPushStatus.Unchanged, e.Outcome.Status));
        Assert.Equal(1, api.ListCalls);
        Assert.Equal(metaBefore, api.MetaCalls);
        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task A_registry_that_forgot_a_deleted_skill_gets_it_backed_up_again_as_new_and_says_why()
    {
        var skill = Skill("tidy");
        var (seed, api) = Build([skill], Session(), new FakeRegistryApi { RemembersRemovals = false });
        await seed.PushAsync(Request(), CancellationToken.None);
        await api.DeleteSkillAsync(RegistryUri, "t", "tidy", null, CancellationToken.None);
        api.Bases.Clear();

        var result = await seed.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal("'tidy' was renamed or deleted in the registry; backed it up again as 'tidy'.", outcome.Message);
        Assert.Equal([0], api.Bases);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_registry_that_forgot_a_rename_gets_the_copy_pushed_back_under_its_name_here()
    {
        var skill = Skill("tidy");
        Synced(skill, 2);
        var (sut, api) = Build([skill], Session(), new FakeRegistryApi { RemembersRemovals = false });
        await api.RenameSkillAsync(RegistryUri, "t", "tidy", null, "neat", CancellationToken.None);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Contains("renamed or deleted", outcome.Message);
        Assert.Equal("tidy", Assert.Single(api.Pushed).Name);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task An_edited_copy_of_a_skill_a_registry_forgot_is_pushed_as_new_rather_than_on_its_old_base()
    {
        var skill = Skill("tidy");
        Synced(skill, 3);
        Edit(skill, "edited");
        var (sut, api) = Build([skill], Session());
        api.Skills.Clear();

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Contains("renamed or deleted", Assert.Single(result.Entries).Outcome.Message);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task A_copy_of_a_skill_deleted_in_the_registry_is_not_uploaded_and_says_how_to_restore_or_remove_it()
    {
        var skill = Skill("tidy");
        var (seed, api) = Build([skill], Session());
        await seed.PushAsync(Request(), CancellationToken.None);
        await api.DeleteSkillAsync(RegistryUri, "t", "tidy", null, CancellationToken.None);
        api.Pushed.Clear();

        var result = await seed.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Removed, outcome.Status);
        Assert.Equal(1, outcome.Version);
        Assert.Null(outcome.RenamedTo);
        Assert.Contains("was deleted from the registry (at v1)", outcome.Message);
        Assert.Contains("protostar skills push tidy --force", outcome.Message);
        Assert.Empty(api.Pushed);
        Assert.Equal(1, api.ListCalls);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_copy_of_a_skill_renamed_in_the_registry_is_not_uploaded_under_its_old_name_and_points_at_pull()
    {
        var skill = Skill("greet-user");
        Synced(skill, 1);
        var (sut, api) = Build([skill], Session());
        await api.RenameSkillAsync(RegistryUri, "t", "greet-user", null, "hello", CancellationToken.None);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal((SkillPushStatus.Removed, "hello"), (outcome.Status, outcome.RenamedTo));
        Assert.Contains("renamed to 'hello'", outcome.Message);
        Assert.Contains("`protostar skills pull` renames this copy", outcome.Message);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task An_edited_copy_of_a_deleted_skill_is_refused_naming_the_delete_and_the_refusal_is_remembered()
    {
        var skill = Skill("tidy");
        Synced(skill, 3);
        Edit(skill, "edited");
        var (sut, api) = Build([skill], Session());
        await api.DeleteSkillAsync(RegistryUri, "t", "tidy", null, CancellationToken.None);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Removed, outcome.Status);
        Assert.StartsWith("Changed here, but 'tidy' was deleted from the registry (at v3)", outcome.Message);
        Assert.Contains("your changes are kept here", outcome.Message);
        Assert.Empty(api.Pushed);
        var recorded = _ledger.Get(RegistryUri, skill.Path)!;
        Assert.Equal((3, true, 3), (recorded.Version, recorded.RefusedAsRemoved, recorded.RefusedVersion));
        Assert.Equal(SkillFileReader.Hash(SkillFileReader.Read(skill.Path)), recorded.RefusedHash);
    }

    [Fact]
    public async Task The_hook_sends_an_edited_copy_of_a_deleted_skill_once_and_then_answers_from_the_ledger()
    {
        var skill = Skill("tidy");
        Synced(skill, 2);
        Edit(skill, "edited");
        var (sut, api) = Build([skill], Session());
        await api.DeleteSkillAsync(RegistryUri, "t", "tidy", null, CancellationToken.None);

        var first = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);
        var second = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        Assert.Equal([2], api.Bases);
        Assert.Equal(0, api.ListCalls);
        var refused = Assert.Single(first.Entries).Outcome;
        Assert.Equal((SkillPushStatus.Removed, false), (refused.Status, refused.Remembered));
        Assert.Contains("was deleted from the registry (at v2)", refused.Message);
        var remembered = Assert.Single(second.Entries).Outcome;
        Assert.Equal((SkillPushStatus.Removed, true), (remembered.Status, remembered.Remembered));
        Assert.EndsWith("Refused earlier; not sent again until it changes.", remembered.Message);
    }

    [Fact]
    public async Task Force_restores_a_deleted_skill_on_purpose_as_a_new_skill()
    {
        var skill = Skill("tidy");
        Synced(skill, 1);
        Edit(skill, "edited");
        var (sut, api) = Build([skill], Session());
        await api.DeleteSkillAsync(RegistryUri, "t", "tidy", null, CancellationToken.None);

        var result = await sut.PushAsync(Request("tidy") with { Force = true }, CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.StartsWith("Restored:", outcome.Message);
        Assert.Equal([0], api.Bases);
        Assert.Empty(api.Removed);
        Assert.False(_ledger.Get(RegistryUri, skill.Path)!.RefusedAsRemoved);
    }

    [Fact]
    public async Task A_new_skill_that_reuses_a_deleted_name_and_never_synced_is_backed_up()
    {
        var api = new FakeRegistryApi();
        api.Removed.Add(new RegistryRemovedSkill("tidy", null, 4, null));
        var (sut, _) = Build([Skill("tidy")], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Created, Assert.Single(result.Entries).Outcome.Status);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task A_copy_synced_after_the_removed_skill_s_last_version_is_not_taken_for_a_copy_of_it()
    {
        var skill = Skill("tidy");
        Synced(skill, 5);
        var (sut, api) = Build([skill], Session());
        api.Skills.Clear();
        api.Removed.Add(new RegistryRemovedSkill("tidy", null, 4, null));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Created, Assert.Single(result.Entries).Outcome.Status);
    }

    [Fact]
    public async Task A_copy_of_a_deleted_skill_is_not_taken_for_the_new_skill_that_reused_its_name()
    {
        var skill = Skill("tidy");
        Synced(skill, 1);
        var (sut, api) = Build([skill], Session());
        api.Skills.Clear();
        api.Skills.Add(new RegistrySkill("tidy", null, 3, "", DateTimeOffset.UnixEpoch, FirstVersion: 3));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Removed, outcome.Status);
        Assert.Contains("a new skill named 'tidy' has been created since", outcome.Message);
        Assert.Contains("--as NEW-NAME", outcome.Message);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task A_copy_synced_from_the_new_skill_is_unchanged()
    {
        var skill = Skill("tidy");
        Synced(skill, 3);
        var (sut, api) = Build([skill], Session());
        api.Skills.Clear();
        api.Skills.Add(new RegistrySkill("tidy", null, 3, "", DateTimeOffset.UnixEpoch, FirstVersion: 3));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
    }

    [Fact]
    public async Task A_removal_in_another_project_does_not_count_for_this_copy()
    {
        var skill = Skill("tidy");
        Synced(skill, 1);
        var (sut, api) = Build([skill], Session());
        api.Skills.Clear();
        api.Removed.Add(new RegistryRemovedSkill("tidy", Guid.NewGuid(), 3, null));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Contains("renamed or deleted", Assert.Single(result.Entries).Outcome.Message);
    }

    [Fact]
    public async Task Another_projects_skill_of_the_same_name_does_not_count_as_this_one()
    {
        var project = Skill("deploy", SkillScope.Project);
        var mine = Guid.NewGuid();
        Synced(project, 1, mine);
        var (sut, api) = Build([project], Session());
        api.Skills.Clear();
        api.Skills.Add(new RegistrySkill("deploy", null, 1, "", DateTimeOffset.UnixEpoch, null));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Contains("renamed or deleted", Assert.Single(result.Entries).Outcome.Message);
    }

    [Fact]
    public async Task A_skill_still_in_its_project_is_unchanged_while_the_same_name_elsewhere_is_ignored()
    {
        var project = Skill("deploy", SkillScope.Project);
        var mine = Guid.NewGuid();
        Synced(project, 1, mine);
        var (sut, api) = Build([project], Session());
        api.Skills.Add(new RegistrySkill("deploy", null, 7, "", DateTimeOffset.UnixEpoch, Guid.NewGuid()));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task A_skill_a_link_moved_to_another_project_is_followed_there_not_pushed_again()
    {
        var project = Skill("deploy", SkillScope.Project);
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        Synced(project, 1, from);
        var (sut, api) = Build([project], Session());
        api.Skills.Clear();
        api.Skills.Add(new RegistrySkill("deploy", null, 1, "", DateTimeOffset.UnixEpoch, to));
        var files = SkillFileReader.Read(project.Path);
        api.Contents[FakeRegistryApi.Key("deploy", to)] = new RegistrySkillContent(
            "deploy", 1, "", files.Select(f => new RegistrySkillFile(f.RelativePath, "", f.Content)).ToList());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Empty(api.Pushed);
        Assert.Equal(to, _ledger.Get(RegistryUri, project.Path)!.ProjectId);
    }

    [Fact]
    public async Task The_hook_does_not_check_the_list_so_an_unchanged_deleted_skill_costs_it_nothing()
    {
        var skill = Skill("tidy");
        Synced(skill, 2);
        var (sut, api) = Build([skill], Session());
        api.Skills.Clear();

        var result = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Equal(0, api.ListCalls);
        Assert.Equal(0, api.MetaCalls);
    }

    [Fact]
    public async Task A_list_that_fails_stops_the_run_with_its_reason()
    {
        var skill = Skill("tidy");
        Synced(skill, 2);
        var (sut, _) = Build([skill], Session(), new FakeRegistryApi { ListError = "boom" });

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.ListFailed, result.Failure);
        Assert.Equal("boom", result.Detail);
    }

    [Fact]
    public async Task A_list_that_fails_because_the_registry_is_down_says_so()
    {
        var skill = Skill("tidy");
        Synced(skill, 2);
        var (sut, _) = Build([skill], Session(), new FakeRegistryApi { ListError = "boom", Meta = null });

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.RegistryUnreachable, result.Failure);
    }

    [Fact]
    public async Task A_changed_skill_is_uploaded_again()
    {
        var skill = Skill("alpha");
        var (sut, api) = Build([skill], Session());
        await sut.PushAsync(Request(), CancellationToken.None);

        File.WriteAllText(Path.Combine(skill.Path, "SKILL.md"), "---\nname: alpha\n---\nedited");
        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task Force_uploads_even_when_the_ledger_matches()
    {
        var (sut, api) = Build([Skill("alpha")], Session());
        await sut.PushAsync(Request(), CancellationToken.None);

        await sut.PushAsync(Request() with { Force = true }, CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task A_refused_push_is_not_recorded()
    {
        var api = new FakeRegistryApi();
        api.Outcomes["alpha"] = new SkillPushOutcome(SkillPushStatus.Refused, null, "nope");
        var (sut, _) = Build([Skill("alpha")], Session(), api);

        await sut.PushAsync(Request(), CancellationToken.None);
        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task Pushes_every_discovered_skill_when_no_names_are_given()
    {
        var (sut, api) = Build([Skill("alpha"), Skill("beta")], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.True(result.Succeeded);
        Assert.Equal(["alpha", "beta"], api.Pushed.Select(p => p.Name));
        Assert.All(api.Pushed, p => Assert.Equal("token-1", p.Token));
        Assert.All(api.Pushed, p => Assert.Equal("SKILL.md", Assert.Single(p.Files).RelativePath));
    }

    [Fact]
    public async Task Pushes_only_the_named_skill_case_insensitively()
    {
        var (sut, api) = Build([Skill("alpha"), Skill("beta")], Session());

        var result = await sut.PushAsync(Request("BETA"), CancellationToken.None);

        Assert.Equal("beta", Assert.Single(result.Entries).Skill.Name);
        Assert.Equal("beta", Assert.Single(api.Pushed).Name);
    }

    [Fact]
    public async Task Unknown_skill_name_fails_before_any_upload()
    {
        var (sut, api) = Build([Skill("alpha")], Session());

        var result = await sut.PushAsync(Request("alpha", "ghost"), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.SkillNotFound, result.Failure);
        Assert.Equal("ghost", result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task No_session_reports_not_logged_in()
    {
        var (sut, api) = Build([Skill("alpha")], session: null);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.NotLoggedIn, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Expired_session_that_could_not_refresh_reports_session_expired()
    {
        var (sut, api) = Build([Skill("alpha")], Session(expired: true));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.SessionExpired, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Expired_session_the_registry_could_not_be_reached_to_refresh_reports_the_registry_unreachable()
    {
        var (sut, api) = Build([Skill("alpha")], lookup: SessionLookups.Unreachable(Registry));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.RegistryUnreachable, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Unreachable_registry_fails_before_any_upload()
    {
        var (sut, api) = Build([Skill("alpha")], Session(), new FakeRegistryApi { Meta = null });

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.RegistryUnreachable, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Registry_on_another_api_major_is_incompatible()
    {
        var api = new FakeRegistryApi { Meta = new RegistryMeta { ApiMajors = [2] } };
        var (sut, _) = Build([Skill("alpha")], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.Incompatible, result.Failure);
        Assert.Empty(api.Pushed);
    }

    // A registry that lists capabilities but not stale-skill-push stores whatever comes, so a push could overwrite another machine's version.
    private static RegistryMeta WithoutStaleProtection => new() { ApiMajors = [1], Capabilities = ["skills", "project-skills", "projects"] };

    [Fact]
    public async Task A_registry_without_stale_protection_is_refused_without_force()
    {
        var api = new FakeRegistryApi { Meta = WithoutStaleProtection };
        var (sut, _) = Build([Skill("alpha")], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.Incompatible, result.Failure);
        Assert.StartsWith(SkillPushService.NoStaleProtection, result.Detail);
        Assert.Contains("--force", result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task With_force_a_registry_without_stale_protection_is_pushed_to_with_a_warning()
    {
        var api = new FakeRegistryApi { Meta = WithoutStaleProtection };
        var (sut, _) = Build([Skill("alpha")], Session(), api);

        var result = await sut.PushAsync(Request() with { Force = true }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(api.Pushed);
        Assert.StartsWith(SkillPushService.NoStaleProtection, result.Warning);
    }

    [Fact]
    public async Task A_registry_too_old_to_list_capabilities_is_pushed_to_as_before()
    {
        var (sut, api) = Build([Skill("alpha")], Session(), new FakeRegistryApi { Meta = new RegistryMeta { ApiMajors = [1] } });

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Warning);
        Assert.Single(api.Pushed);
    }

    [Fact]
    public async Task A_project_skill_is_not_pushed_to_a_registry_without_project_skills()
    {
        var api = new FakeRegistryApi { Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["skills", "stale-skill-push"] } };
        var (sut, _) = Build([Skill("alpha", SkillScope.Project)], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.Incompatible, result.Failure);
        Assert.Equal("This registry is too old for project skills. Update the registry.", result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Invalid_registry_url_is_reported()
    {
        var (sut, _) = Build([Skill("alpha")], Session());

        var result = await sut.PushAsync(new SkillPushRequest([], null, null, null, "not a url"), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidRegistry, result.Failure);
    }

    [Fact]
    public async Task A_refused_skill_does_not_stop_the_others_and_the_run_is_not_successful()
    {
        var api = new FakeRegistryApi();
        api.Outcomes["alpha"] = new SkillPushOutcome(SkillPushStatus.Refused, null, "too large");
        var (sut, _) = Build([Skill("alpha"), Skill("beta")], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
        Assert.Equal(SkillPushStatus.Refused, result.Entries[0].Outcome.Status);
        Assert.Equal(SkillPushStatus.Created, result.Entries[1].Outcome.Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Second_skill_of_the_same_scope_and_name_with_identical_content_is_skipped()
    {
        var first = Skill("alpha", SkillScope.Global, "one-alpha");
        var second = Skill("alpha", SkillScope.Global, "two-alpha");
        var (sut, api) = Build([first, second], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Single(api.Pushed);
        Assert.Equal(SkillPushStatus.Skipped, result.Entries[1].Outcome.Status);
        Assert.Contains("Identical", result.Entries[1].Outcome.Message);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Second_skill_of_the_same_scope_and_name_with_different_content_is_a_conflict()
    {
        var first = Skill("alpha", SkillScope.Global, "one-alpha");
        var second = Skill("alpha", SkillScope.Global, "two-alpha");
        File.WriteAllText(Path.Combine(second.Path, "SKILL.md"), "---\nname: alpha\n---\nanother harness's version");
        var (sut, api) = Build([first, second], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var uploaded = Encoding.UTF8.GetString(Assert.Single(api.Pushed).Files[0].Content);
        Assert.EndsWith("body", uploaded);
        Assert.Equal(SkillPushStatus.Conflict, result.Entries[1].Outcome.Status);
        Assert.Contains(first.Path, result.Entries[1].Outcome.Message);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Same_name_conflict_is_detected_even_when_the_first_copy_is_unchanged_by_the_ledger()
    {
        var first = Skill("alpha", SkillScope.Global, "one-alpha");
        var (seed, api) = Build([first], Session());
        await seed.PushAsync(Request(), CancellationToken.None);
        var second = Skill("alpha", SkillScope.Global, "two-alpha");
        File.WriteAllText(Path.Combine(second.Path, "SKILL.md"), "different");
        var (sut, _) = Build([first, second], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Single(api.Pushed);
        Assert.Equal(SkillPushStatus.Unchanged, result.Entries[0].Outcome.Status);
        Assert.Equal(SkillPushStatus.Conflict, result.Entries[1].Outcome.Status);
    }

    [Fact]
    public async Task A_global_and_a_project_skill_of_the_same_name_are_both_pushed_each_in_its_own_scope()
    {
        var global = Skill("deploy", SkillScope.Global);
        var project = Skill("deploy", SkillScope.Project);
        File.WriteAllText(Path.Combine(project.Path, "SKILL.md"), "---\nname: deploy\n---\nthe project's own version");
        var (sut, api) = Build([global, project], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, api.Pushed.Count);
        Assert.Null(api.Pushed[0].Project);
        Assert.NotNull(api.Pushed[1].Project);
    }

    [Fact]
    public async Task A_project_skill_is_pushed_with_its_project_folders_facts_and_this_machines_id()
    {
        var project = Skill("deploy", SkillScope.Project);
        var (sut, api) = Build([project], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(api.Pushed).Project!;
        Assert.Equal(FakeMachine.Id, sent.MachineId);
        Assert.Equal(project.ProjectRoot, sent.Facts.WorkingDirectory);
        Assert.Equal([project.ProjectRoot!], _facts.Asked);
    }

    [Fact]
    public async Task A_global_skill_is_pushed_without_project_facts_and_resolves_nothing()
    {
        var (sut, api) = Build([Skill("deploy")], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Null(Assert.Single(api.Pushed).Project);
        Assert.Empty(_facts.Asked);
    }

    [Fact]
    public async Task A_project_skill_recorded_as_global_by_an_older_cli_is_pushed_again_as_the_projects()
    {
        var project = Skill("deploy", SkillScope.Project);
        _ledger.Record(new Uri(Registry), project.Path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(project.Path)), 1, "deploy"));
        var (sut, api) = Build([project], Session());

        await sut.PushAsync(Request(), CancellationToken.None);
        var again = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.NotNull(Assert.Single(api.Pushed).Project);
        Assert.NotNull(_ledger.Get(new Uri(Registry), project.Path)!.ProjectId);
        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(again.Entries).Outcome.Status);
    }

    [Fact]
    public async Task The_capture_hooks_request_backs_up_both_the_global_and_the_project_skill_of_the_used_name()
    {
        // The hook only knows the skill's name and the session's cwd (see CaptureService), so it asks for the
        // name with the cwd as the project start, and each local copy goes to its own registry skill.
        var global = Skill("deploy", SkillScope.Global);
        var project = Skill("deploy", SkillScope.Project);
        File.WriteAllText(Path.Combine(project.Path, "SKILL.md"), "---\nname: deploy\n---\nproject");
        var (sut, api) = Build([global, project, Skill("other")], Session());

        var result = await sut.PushAsync(new SkillPushRequest(["deploy"], null, null, _root, Registry), CancellationToken.None);

        Assert.Equal([SkillScope.Global, SkillScope.Project], result.Entries.Select(e => e.Skill.Scope));
        Assert.All(result.Entries, e => Assert.Equal(SkillPushStatus.Created, e.Outcome.Status));
        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task No_skills_found_succeeds_without_contacting_the_registry()
    {
        var (sut, api) = Build([], session: null);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.Empty(result.Entries);
        Assert.Equal(0, api.MetaCalls);
    }

    [Fact]
    public async Task A_file_with_a_padded_name_fails_the_skill_before_upload_with_a_clear_message()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows strips a trailing space from a file name, so such a file cannot exist there.

        var skill = Skill("alpha");
        File.WriteAllText(Path.Combine(skill.Path, "notes.md "), "x");
        var (sut, api) = Build([skill], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("starts or ends with whitespace", outcome.Message);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task A_successful_push_records_the_skill_name_in_the_ledger()
    {
        var skill = Skill("alpha");
        var (sut, _) = Build([skill], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal("alpha", _ledger.Get(new Uri(Registry), skill.Path)!.Name);
    }

    // ----- Base version and stale refusals (#52) -----

    private static readonly Uri RegistryUri = new(Registry);

    // A registry that is at `current` and, like the real one, refuses any push whose base is not current.
    private static FakeRegistryApi AtVersion(int current) => new()
    {
        Answer = (_, based) => based is null || based == current
            ? new SkillPushOutcome(SkillPushStatus.Created, current + 1, null)
            : new SkillPushOutcome(SkillPushStatus.Stale, current, "stale"),
    };

    private void Synced(DiscoveredSkill skill, int? version, Guid? projectId = null)
    {
        _ledger.Record(RegistryUri, skill.Path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(skill.Path)), version, skill.Name, projectId));
        _inRegistry.Add(new RegistrySkill(skill.Name, null, version ?? 1, "", DateTimeOffset.UnixEpoch, projectId));
    }

    private static void Edit(DiscoveredSkill skill, string body) =>
        File.WriteAllText(Path.Combine(skill.Path, "SKILL.md"), $"---\nname: {skill.Name}\n---\n{body}");

    // ----- A copy with no sync record that equals a version of a removed skill (cli#128) -----

    private static RegistryMeta WithRemovedHashes => new() { ApiMajors = [1], Capabilities = ["skills", "stale-skill-push", "skill-tombstone-hashes"] };

    // The registry's removal of `name` with v1 hashed as `copy`'s content is now, so `copy` is byte-identical to that v1.
    private static RegistryRemovedSkill RemovedAs(DiscoveredSkill copy, string? renamedTo, int lastVersion = 1) =>
        new(copy.Name, null, lastVersion, renamedTo)
        {
            Versions = [new RegistryRemovedVersion(1, SkillFileReader.RegistryHash(SkillFileReader.Read(copy.Path)))],
        };

    [Fact]
    public async Task An_unsynced_copy_identical_to_a_renamed_skills_version_is_reported_not_uploaded()
    {
        var skill = Skill("greet-user");
        var api = new FakeRegistryApi { Meta = WithRemovedHashes };
        api.Removed.Add(RemovedAs(skill, "hello", lastVersion: 2));
        var (sut, _) = Build([skill], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal((SkillPushStatus.Removed, "hello", 2), (outcome.Status, outcome.RenamedTo, outcome.Version));
        Assert.StartsWith("This copy has no sync record here, but it is identical to v1 of 'greet-user', which was renamed to 'hello'", outcome.Message);
        Assert.Contains("`protostar skills pull` renames this copy to 'hello'", outcome.Message);
        Assert.Empty(api.Pushed);
        Assert.Equal(new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(skill.Path)), 1, "greet-user"), _ledger.Get(RegistryUri, skill.Path));
    }

    [Fact]
    public async Task An_unsynced_copy_identical_to_a_deleted_skills_version_is_reported_with_the_way_to_restore_it()
    {
        var skill = Skill("tidy");
        var api = new FakeRegistryApi { Meta = WithRemovedHashes };
        api.Removed.Add(RemovedAs(skill, null));
        var (sut, _) = Build([skill], Session(), api);

        var outcome = Assert.Single((await sut.PushAsync(Request(), CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Removed, outcome.Status);
        Assert.Contains("which was deleted from the registry (at v1)", outcome.Message);
        Assert.Contains("protostar skills push tidy --force", outcome.Message);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task An_unsynced_copy_with_other_content_reusing_a_removed_name_is_still_a_new_skill()
    {
        var skill = Skill("greet-user");
        var api = new FakeRegistryApi { Meta = WithRemovedHashes };
        api.Removed.Add(RemovedAs(skill, "hello"));
        Edit(skill, "a different greet-user, written later");
        var (sut, _) = Build([skill], Session(), api);

        var outcome = Assert.Single((await sut.PushAsync(Request(), CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task The_hook_reports_an_unsynced_copy_of_a_renamed_skill_once_and_then_costs_nothing()
    {
        var skill = Skill("greet-user");
        var api = new FakeRegistryApi { Meta = WithRemovedHashes };
        api.Removed.Add(RemovedAs(skill, "hello"));
        var (sut, _) = Build([skill], Session(), api);

        var first = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);
        var second = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        Assert.Equal((SkillPushStatus.Removed, "hello"), (Assert.Single(first.Entries).Outcome.Status, first.Entries[0].Outcome.RenamedTo));
        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(second.Entries).Outcome.Status);
        Assert.Equal(1, api.ListCalls);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task The_hook_does_not_list_for_an_unsynced_copy_when_the_registry_sends_no_hashes()
    {
        var skill = Skill("greet-user");
        var api = new FakeRegistryApi { Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["skills", "stale-skill-push"] } };
        var (sut, _) = Build([skill], Session(), api);

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal(0, api.ListCalls);
    }

    [Fact]
    public async Task Force_restores_an_unsynced_copy_of_a_deleted_skill_as_a_new_skill()
    {
        var skill = Skill("tidy");
        var api = new FakeRegistryApi { Meta = WithRemovedHashes };
        api.Removed.Add(RemovedAs(skill, null));
        var (sut, _) = Build([skill], Session(), api);

        var outcome = Assert.Single((await sut.PushAsync(Request("tidy") with { Force = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.StartsWith("Restored:", outcome.Message);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task A_dismissed_copy_of_a_removed_skill_is_skipped_by_the_command_and_the_hook_without_a_request()
    {
        var skill = Skill("greet-user");
        Edit(skill, "my edit");
        var hash = SkillFileReader.Hash(SkillFileReader.Read(skill.Path));
        _ledger.Record(RegistryUri, skill.Path, new SkillLedgerEntry("old", 1, "greet-user", DismissedHash: hash));
        var (sut, api) = Build([skill], Session());

        var command = Assert.Single((await sut.PushAsync(Request(), CancellationToken.None)).Entries).Outcome;
        var hook = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal((SkillPushStatus.Skipped, SkillPushStatus.Skipped), (command.Status, hook.Status));
        Assert.StartsWith("Dismissed:", command.Message);
        Assert.Equal((0, 0, 0), (api.MetaCalls, api.ListCalls, api.Pushed.Count));
    }

    [Fact]
    public async Task A_push_names_the_version_the_copy_last_synced_as_its_base()
    {
        var skill = Skill("alpha");
        Synced(skill, 4);
        Edit(skill, "edited");
        var (sut, api) = Build([skill], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal([4], api.Bases);
    }

    [Fact]
    public async Task A_copy_with_no_record_pushes_with_base_zero_so_it_cannot_overwrite_a_skill_it_never_saw()
    {
        var (sut, api) = Build([Skill("alpha")], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task A_record_of_another_scope_or_name_does_not_lend_its_version_as_the_base()
    {
        var project = Skill("deploy", SkillScope.Project);
        Synced(project, 3);
        Edit(project, "edited");
        var renamed = Skill("beta");
        _ledger.Record(RegistryUri, renamed.Path, new SkillLedgerEntry("old", 5, "Beta"));
        var (sut, api) = Build([project, renamed], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal([0, 0], api.Bases);
    }

    [Fact]
    public async Task A_record_from_before_versions_were_kept_pushes_with_no_base_as_before()
    {
        var skill = Skill("alpha");
        Synced(skill, version: null);
        Edit(skill, "edited");
        var (sut, api) = Build([skill], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal([null], api.Bases);
    }

    [Fact]
    public async Task A_stale_push_is_not_retried_and_the_ledger_remembers_the_refused_content()
    {
        var skill = Skill("alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var (sut, api) = Build([skill], Session(), AtVersion(2));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Stale, outcome.Status);
        Assert.Equal(2, outcome.Version);
        Assert.Contains("based on v1", outcome.Message);
        Assert.False(result.Succeeded);
        Assert.Single(api.Pushed);
        var recorded = _ledger.Get(RegistryUri, skill.Path)!;
        Assert.Equal(1, recorded.Version);
        Assert.Equal(SkillFileReader.Hash(SkillFileReader.Read(skill.Path)), recorded.RefusedHash);
        Assert.Equal(2, recorded.RefusedVersion);
    }

    [Fact]
    public async Task A_stale_push_of_a_copy_that_never_synced_is_remembered_with_version_zero()
    {
        var skill = Skill("alpha");
        var (sut, _) = Build([skill], Session(), AtVersion(1));

        await sut.PushAsync(Request(), CancellationToken.None);

        var recorded = _ledger.Get(RegistryUri, skill.Path)!;
        Assert.Equal(0, recorded.Version);
        Assert.Equal(string.Empty, recorded.ContentHash);
        Assert.NotNull(recorded.RefusedHash);
    }

    [Fact]
    public async Task The_hook_does_not_send_content_the_registry_already_refused()
    {
        var skill = Skill("alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var (sut, api) = Build([skill], Session(), AtVersion(2));
        await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        var again = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        var outcome = Assert.Single(again.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Stale, outcome.Status);
        Assert.True(outcome.Remembered);
        Assert.Single(api.Pushed);
        Assert.Equal(1, api.MetaCalls);
    }

    [Fact]
    public async Task The_hook_sends_a_refused_copy_again_once_it_changes()
    {
        var skill = Skill("alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var (sut, api) = Build([skill], Session(), AtVersion(2));
        await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        Edit(skill, "B's second edit");
        var again = await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
        Assert.Equal([1, 1], api.Bases);
        Assert.False(Assert.Single(again.Entries).Outcome.Remembered);
    }

    [Fact]
    public async Task A_manual_push_asks_the_registry_again_after_a_refusal()
    {
        var skill = Skill("alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var (sut, api) = Build([skill], Session(), AtVersion(2));
        await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None);

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task Force_replaces_a_newer_registry_version_by_pushing_again_based_on_it_and_says_so()
    {
        var skill = Skill("alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var (sut, api) = Build([skill], Session(), AtVersion(2));

        var result = await sut.PushAsync(Request() with { Force = true }, CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal(3, outcome.Version);
        Assert.Contains("Replaced v2", outcome.Message);
        Assert.Contains("--version 2", outcome.Message);
        Assert.Equal([1, 2], api.Bases);
        var recorded = _ledger.Get(RegistryUri, skill.Path)!;
        Assert.Equal(3, recorded.Version);
        Assert.Null(recorded.RefusedHash);
    }

    [Fact]
    public async Task Force_does_not_replace_a_version_that_arrives_between_its_two_pushes()
    {
        var skill = Skill("alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var api = new FakeRegistryApi { Answer = (_, based) => new SkillPushOutcome(SkillPushStatus.Stale, based + 1, "stale") };
        var (sut, _) = Build([skill], Session(), api);

        var result = await sut.PushAsync(Request() with { Force = true }, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Stale, Assert.Single(result.Entries).Outcome.Status);
        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task A_successful_push_after_a_refusal_clears_it()
    {
        var skill = Skill("alpha");
        _ledger.Record(RegistryUri, skill.Path, new SkillLedgerEntry("old", 2, "alpha", null, "refused", 3));
        _inRegistry.Add(new RegistrySkill("alpha", null, 3, "", DateTimeOffset.UnixEpoch));
        var (sut, _) = Build([skill], Session(), AtVersion(2));

        await sut.PushAsync(Request(), CancellationToken.None);

        var recorded = _ledger.Get(RegistryUri, skill.Path)!;
        Assert.Equal(3, recorded.Version);
        Assert.Null(recorded.RefusedHash);
        Assert.Null(recorded.RefusedVersion);
    }

    [Fact]
    public async Task Push_as_sends_the_copy_as_a_new_skill_and_renames_it_here()
    {
        var skill = Skill("alpha", dir: "alpha");
        Synced(skill, 1);
        Edit(skill, "B's edit");
        var (sut, api) = Build([skill], Session());

        var result = await sut.PushAsync(Request("alpha") with { As = "alpha-b" }, CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Contains("skills pull alpha", outcome.Message);
        var (name, _, files, _) = Assert.Single(api.Pushed);
        Assert.Equal("alpha-b", name);
        Assert.Equal([0], api.Bases);
        Assert.Contains("name: alpha-b", Encoding.UTF8.GetString(Assert.Single(files).Content));
        var moved = Path.Combine(_root, "alpha-b");
        Assert.False(Directory.Exists(skill.Path));
        Assert.Contains("name: alpha-b", File.ReadAllText(Path.Combine(moved, "SKILL.md")));
        Assert.Contains("B's edit", File.ReadAllText(Path.Combine(moved, "SKILL.md")));
        Assert.Null(_ledger.Get(RegistryUri, skill.Path));
        var recorded = _ledger.Get(RegistryUri, moved)!;
        Assert.Equal(("alpha-b", 1), (recorded.Name, recorded.Version));
        Assert.Equal(SkillFileReader.Hash(SkillFileReader.Read(moved)), recorded.ContentHash);
    }

    [Fact]
    public async Task Push_as_never_overwrites_an_existing_skill_of_the_new_name()
    {
        var skill = Skill("alpha", dir: "alpha");
        var (sut, _) = Build([skill], Session(), AtVersion(4));

        var result = await sut.PushAsync(Request("alpha") with { As = "taken" }, CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Refused, outcome.Status);
        Assert.Contains("already has a skill named 'taken'", outcome.Message);
        Assert.True(Directory.Exists(skill.Path));
        Assert.Null(_ledger.Get(RegistryUri, skill.Path));
    }

    [Theory]
    [InlineData(new[] { "alpha", "beta" }, "alpha-b", "name exactly one")]
    [InlineData(new string[0], "alpha-b", "name exactly one")]
    [InlineData(new[] { "alpha" }, "a/b", "cannot name a skill")]
    [InlineData(new[] { "alpha" }, "alpha", "already this skill's name")]
    public async Task Push_as_needs_one_skill_and_a_usable_new_name(string[] names, string newName, string message)
    {
        var (sut, api) = Build([Skill("alpha"), Skill("beta")], Session());

        var result = await sut.PushAsync(Request(names) with { As = newName }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains(message, result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Push_as_refuses_a_name_that_matches_a_global_and_a_project_skill()
    {
        var (sut, _) = Build([Skill("alpha"), Skill("alpha", SkillScope.Project)], Session());

        var result = await sut.PushAsync(Request("alpha") with { As = "alpha-b" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains("matches 2 local skills", result.Detail);
    }

    [Fact]
    public async Task Push_as_refuses_the_name_of_a_local_skill_folder_beside_it_and_changes_nothing()
    {
        var hello = Skill("hello", dir: "hello");
        var zeta = Skill("zeta", dir: "zeta");
        Synced(hello, 1);
        var before = _ledger.Get(RegistryUri, hello.Path);
        var (sut, api) = Build([hello, zeta], Session());

        var result = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains("'zeta' is already taken", result.Detail);
        Assert.Contains(zeta.Path, result.Detail);
        Assert.Contains("such as 'zeta-2'", result.Detail);
        Assert.Empty(api.Pushed);
        Assert.Equal(0, api.ListCalls);
        Assert.Contains("name: hello", File.ReadAllText(Path.Combine(hello.Path, "SKILL.md")));
        Assert.Contains("name: zeta", File.ReadAllText(Path.Combine(zeta.Path, "SKILL.md")));
        Assert.Equal(before, _ledger.Get(RegistryUri, hello.Path));
        Assert.Null(_ledger.Get(RegistryUri, zeta.Path));
    }

    [Fact]
    public async Task Push_as_refuses_a_name_a_local_skills_manifest_uses_in_another_folder()
    {
        var hello = Skill("hello", dir: "hello");
        var other = Skill("Zeta", dir: "elsewhere");
        var (sut, api) = Build([hello, other], Session());

        var result = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains($"the global skill at {other.Path} is called 'Zeta'", result.Detail);
        Assert.Empty(api.Pushed);
    }

    // ----- a copy pulled while the folder was linked to another project, after an unlink (cli#95) -----

    private static readonly Guid Lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid App = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    // A project skill copy synced at v1 as project lib's, in a folder the registry now says is in `here`.
    private DiscoveredSkill PulledFromLib(Guid? here)
    {
        var copy = Skill("shared", SkillScope.Project);
        _ledger.Record(RegistryUri, copy.Path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(copy.Path)), 1, "shared", Lib));
        _inRegistry.Add(new RegistrySkill("shared", null, 1, "", DateTimeOffset.UnixEpoch, Lib, "lib"));
        _projects.HereKnown = true;
        _projects.Here = here;
        return copy;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // a folder with no project of its own yet
    public async Task After_an_unlink_an_unedited_copy_is_still_the_linked_projects_skill_and_unchanged(bool folderHasProject)
    {
        var copy = PulledFromLib(folderHasProject ? App : null);
        var (sut, api) = Build([copy], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal((SkillPushStatus.Unchanged, 1, Lib, "lib"), (outcome.Status, outcome.Version, outcome.ProjectId, outcome.ProjectName));
        Assert.StartsWith("Still project lib's skill", outcome.Message);
        Assert.Empty(api.Pushed);
        Assert.Equal(1, _projects.Calls);
    }

    [Fact]
    public async Task After_an_unlink_an_edited_copy_is_backed_up_in_the_folders_project_as_a_new_skill()
    {
        var copy = PulledFromLib(App);
        Edit(copy, "changed here");
        var (sut, api) = Build([copy], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Contains("it has changed here since, so it now belongs to this folder's project: backed it up there as a new skill.", outcome.Message);
        Assert.Equal([0], api.Bases);
        Assert.NotNull(Assert.Single(api.Pushed).Project);
    }

    [Fact]
    public async Task After_an_unlink_an_unedited_copy_of_a_skill_the_linked_project_lost_without_a_trace_is_backed_up_here()
    {
        var copy = PulledFromLib(App);
        var (sut, api) = Build([copy], Session(), new FakeRegistryApi { RemembersRemovals = false });
        api.Skills.Clear();

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Contains("that project no longer has it", outcome.Message);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task After_an_unlink_a_copy_of_a_skill_deleted_from_the_linked_project_is_reported_as_deleted_first()
    {
        var copy = PulledFromLib(App);
        var (sut, api) = Build([copy], Session());
        api.Skills.Clear();
        api.Removed.Add(new RegistryRemovedSkill("shared", Lib, 1, null));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Removed, Assert.Single(result.Entries).Outcome.Status);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Push_as_refuses_a_plain_folder_of_the_new_name_beside_the_skill()
    {
        var hello = Skill("hello", dir: "hello");
        Directory.CreateDirectory(Path.Combine(_root, "zeta"));
        var (sut, api) = Build([hello], Session());

        var result = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains("already exists beside it", result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Push_as_ignores_a_skill_of_the_name_in_the_other_scope()
    {
        var hello = Skill("hello", dir: "hello");
        var projectZeta = Skill("zeta", SkillScope.Project, dir: Path.Combine("repo-skills", "zeta"));
        var (sut, api) = Build([hello, projectZeta], Session());

        var result = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.Equal("zeta", Assert.Single(api.Pushed).Name);
    }

    [Fact]
    public async Task Push_as_refuses_the_name_of_a_registry_skill_in_the_scope_before_pushing()
    {
        var hello = Skill("hello", dir: "hello");
        var (sut, api) = Build([hello], Session());
        api.Skills.Add(new RegistrySkill("zeta", null, 2, "", DateTimeOffset.UnixEpoch));
        api.Skills.Add(new RegistrySkill("zeta-2", null, 1, "", DateTimeOffset.UnixEpoch));

        var result = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains("the registry already has a global skill named 'zeta'", result.Detail);
        Assert.Contains("such as 'zeta-3'", result.Detail);
        Assert.Empty(api.Pushed);
        Assert.True(Directory.Exists(hello.Path));
        Assert.Null(_ledger.Get(RegistryUri, hello.Path));
    }

    [Fact]
    public async Task Push_as_refuses_the_name_of_a_skill_removed_from_the_registry()
    {
        var hello = Skill("hello", dir: "hello");
        var (sut, api) = Build([hello], Session());
        api.Removed.Add(new RegistryRemovedSkill("zeta", null, 3, "omega"));

        var result = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidAs, result.Failure);
        Assert.Contains("renamed to 'omega' at v3", result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Push_as_checks_a_project_skills_new_name_against_its_own_projects_registry_skills()
    {
        var hello = Skill("hello", SkillScope.Project, dir: Path.Combine("repo-skills", "hello"));
        _projects.ByFolder = true;
        var (sut, api) = Build([hello], Session());
        var project = FakeRegistryApi.ProjectIdOf(ProjectFacts.NotGit(hello.ProjectRoot!, hello.ProjectRoot!));
        api.Skills.Add(new RegistrySkill("zeta", null, 1, "", DateTimeOffset.UnixEpoch));
        api.Skills.Add(new RegistrySkill("omega", null, 1, "", DateTimeOffset.UnixEpoch, project, "repo"));

        var global = await sut.PushAsync(Request("hello") with { As = "zeta" }, CancellationToken.None);
        Assert.Equal(SkillPushRunFailure.None, global.Failure);

        var renamed = Path.Combine(_root, "repo-skills", "zeta");
        var (again, _) = Build([hello with { Name = "zeta", Path = renamed }], Session(), api);
        var clash = await again.PushAsync(Request("zeta") with { As = "omega" }, CancellationToken.None);
        Assert.Equal(SkillPushRunFailure.InvalidAs, clash.Failure);
        Assert.Contains("skill in this project named 'omega'", clash.Detail);
    }

    [Fact]
    public async Task After_an_unlink_a_copy_whose_name_the_folders_project_has_too_is_the_folders_projects()
    {
        var copy = PulledFromLib(App);
        var (sut, api) = Build([copy], Session(), AtVersion(3));
        api.Skills.Add(new RegistrySkill("shared", null, 3, "", DateTimeOffset.UnixEpoch, App, "app"));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushStatus.Stale, Assert.Single(result.Entries).Outcome.Status);
        Assert.Equal([0], api.Bases);
    }

    [Fact]
    public async Task A_copy_refused_because_it_last_synced_as_another_projects_skill_says_so_not_that_it_never_synced()
    {
        // The re-link case of cli#129: synced (pushed) as one project's skill, now in a folder whose project has its own.
        var copy = PulledFromLib(App);
        Edit(copy, "changed here");
        var (sut, api) = Build([copy], Session(), AtVersion(3));
        api.Skills.Add(new RegistrySkill("shared", null, 3, "", DateTimeOffset.UnixEpoch, App, "app"));

        var outcome = Assert.Single((await sut.PushAsync(Request(), CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Stale, outcome.Status);
        Assert.Equal(
            "Changed here and in the registry: the registry has v3, but this copy last synced as another project's skill, not as this project's, " +
            "so it is based on none of its versions. Nothing was overwritten.", outcome.Message);
        Assert.DoesNotContain("never pulled or pushed", outcome.Message);

        // The hook, which resolves nothing, later sees only the record the refusal left, and says what that record can vouch for.
        Edit(copy, "changed again");
        var hook = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;
        Assert.Equal(SkillPushStatus.Stale, hook.Status);
        Assert.Contains("this machine has no record of this copy being based on any of its versions.", hook.Message);
    }

    [Fact]
    public async Task The_hook_that_backs_up_an_edited_copy_into_the_folders_project_says_which_project_it_left()
    {
        var copy = PulledFromLib(App);
        Edit(copy, "changed here");
        var (sut, api) = Build([copy], Session());

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal(Lib, outcome.LeftProject);
        Assert.NotEqual(Lib, outcome.ProjectId);
        Assert.Equal([0], api.Bases);
        Assert.Equal(1, _projects.Calls);
        Assert.Equal(0, api.ListCalls);
    }

    [Fact]
    public async Task A_hook_backup_into_the_project_the_copy_synced_in_left_no_project()
    {
        var copy = Skill("deploy", SkillScope.Project);
        _projects.ByFolder = true;
        var (sut, _) = Build([copy], Session());
        var first = Assert.Single((await sut.PushAsync(Request(), CancellationToken.None)).Entries).Outcome;
        Edit(copy, "changed here");

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal(first.ProjectId, outcome.ProjectId);
        Assert.Null(outcome.LeftProject);
    }

    // ----- a base version recorded against another project is never sent to this one (#138) -----

    [Fact]
    public async Task The_hook_does_not_send_a_base_recorded_against_the_project_the_folder_has_left()
    {
        // The repro of #138: backed up as lib's v1 while unlinked, then re-linked to app, which has its own 'shared' at v1.
        // Sending lib's v1 as the base would let app accept the edit as its v2 on top of a skill it was never based on.
        var copy = PulledFromLib(App);
        Edit(copy, "changed here");
        var (sut, api) = Build([copy], Session(), AtVersion(1));
        api.Skills.Add(new RegistrySkill("shared", null, 1, "", DateTimeOffset.UnixEpoch, App, "app"));

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Stale, outcome.Status);
        Assert.Equal([0], api.Bases);
        Assert.Contains("this copy last synced as another project's skill, not as this project's", outcome.Message);
        Assert.Equal(0, api.ListCalls);

        // Cached as any stale refusal is: the same content is not sent, or resolved, again.
        var again = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;
        Assert.Equal((SkillPushStatus.Stale, true), (again.Status, again.Remembered));
        Assert.Single(api.Pushed);
        Assert.Equal(1, _projects.Calls);
    }

    [Fact]
    public async Task The_hook_still_sends_its_base_to_the_project_the_record_names()
    {
        var copy = PulledFromLib(Lib);
        Edit(copy, "changed here");
        var (sut, api) = Build([copy], Session(), AtVersion(1));

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Created, outcome.Status);
        Assert.Equal([1], api.Bases);
    }

    [Fact]
    public async Task The_hook_asks_nothing_for_an_unchanged_project_skill()
    {
        var copy = PulledFromLib(App);
        var (sut, api) = Build([copy], Session());

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = true }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Unchanged, outcome.Status);
        Assert.Equal(0, _projects.Calls);
        Assert.Empty(api.Pushed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_copy_whose_folders_project_cannot_be_told_is_not_sent_with_its_recorded_base(bool fromHook)
    {
        var copy = PulledFromLib(App);
        _projects.HereKnown = false;
        Edit(copy, "changed here");
        var (sut, api) = Build([copy], Session(), AtVersion(1));

        var outcome = Assert.Single((await sut.PushAsync(Request() with { FromHook = fromHook }, CancellationToken.None)).Entries).Outcome;

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.StartsWith("Could not tell which project this folder is in now", outcome.Message);
        Assert.Empty(api.Pushed);
        Assert.Equal(1, _ledger.Get(RegistryUri, copy.Path)!.Version);
    }

    [Fact]
    public async Task A_record_a_link_moved_into_the_folders_project_is_followed_not_taken_for_a_copy_left_by_an_unlink()
    {
        var copy = PulledFromLib(App);
        var (sut, api) = Build([copy], Session());
        api.Skills.Clear();
        api.Skills.Add(new RegistrySkill("shared", null, 1, "", DateTimeOffset.UnixEpoch, App, "app"));
        api.Contents[FakeRegistryApi.Key("shared", App)] = new RegistrySkillContent(
            "shared", 1, "", SkillFileReader.Read(copy.Path).Select(f => new RegistrySkillFile(f.RelativePath, "", f.Content)).ToList());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Unchanged, outcome.Status);
        Assert.Null(outcome.Message);
        Assert.Empty(api.Pushed);
        Assert.Equal(App, _ledger.Get(RegistryUri, copy.Path)!.ProjectId);
    }

    /// <summary>Answers which project a folder is in: by folder, a fixed project, or not at all.</summary>
    internal sealed class FakeProjects : IProjectApi
    {
        public int Calls { get; private set; }

        /// <summary>Resolve each folder to the project <see cref="FakeRegistryApi.ProjectIdOf"/> gives it, as the registry keys them.</summary>
        public bool ByFolder { get; set; }

        /// <summary>
        /// With <see cref="HereKnown"/>, the project every folder is in. When neither is set the registry cannot say, so tests
        /// that do not care see push trust the ledger's project as before cli#95.
        /// </summary>
        public Guid? Here { get; set; }

        public bool HereKnown { get; set; }

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken)
        {
            Calls++;
            if (ByFolder)
                return Task.FromResult(ApiResult<RemoteProject>.Success(new RemoteProject { ProjectId = FakeRegistryApi.ProjectIdOf(facts), Name = Path.GetFileName(facts.Directory) }));
            return Task.FromResult(HereKnown
                ? ApiResult<RemoteProject>.Success(new RemoteProject { ProjectId = Here, Name = "app" })
                : ApiResult<RemoteProject>.Fail("cannot say"));
        }

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Facts for a folder outside git, recording which folders were asked about.</summary>
    internal sealed class FakeFacts : IProjectFactsResolver
    {
        public List<string> Asked { get; } = [];

        public ProjectFacts Resolve(string workingDirectory)
        {
            Asked.Add(workingDirectory);
            return ProjectFacts.NotGit(workingDirectory, workingDirectory);
        }
    }

    internal sealed class FakeMachine : IMachineIdentity
    {
        public static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        public MachineInfo Get() => new(Id, "test", "linux");

        public bool IsRegisteredWith(Uri registry) => true;

        public void MarkRegistered(Uri registry)
        {
        }
    }

    internal sealed class FakeLedger : ISkillLedger
    {
        private readonly Dictionary<string, SkillLedgerEntry> _entries = [];

        public SkillLedgerEntry? Get(Uri registry, string skillPath) => _entries.GetValueOrDefault(registry + "|" + skillPath);

        public void Record(Uri registry, string skillPath, SkillLedgerEntry entry) => _entries[registry + "|" + skillPath] = entry;

        public IReadOnlyDictionary<string, SkillLedgerEntry> Entries(Uri registry) =>
            _entries.Where(e => e.Key.StartsWith(registry + "|", StringComparison.Ordinal))
                .ToDictionary(e => e.Key[(registry + "|").Length..], e => e.Value);

        public void Forget(Uri registry, string skillPath) => _entries.Remove(registry + "|" + skillPath);
    }

    internal sealed class FakeSkillService(IReadOnlyList<DiscoveredSkill> skills) : ISkillService
    {
        public SkillDiscoveryResult Discover(string? harnessId, string? harnessHome, string? projectStart) =>
            new(skills, SkillQueryFailure.None, null);
    }

    private sealed class FakeTokens(SessionLookup session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session);
    }

    internal sealed class FakeRegistryApi : IRegistryApi
    {
        public RegistryMeta? Meta { get; init; } = new() { ApiMajors = [1] };
        public Dictionary<string, SkillPushOutcome> Outcomes { get; } = [];
        public List<(string Name, string Token, IReadOnlyList<SkillUploadFile> Files, SkillProject? Project)> Pushed { get; } = [];
        public int MetaCalls { get; private set; }

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            MetaCalls++;
            return Task.FromResult(Meta);
        }

        /// <summary>The base version each push named, in push order.</summary>
        public List<int?> Bases { get; } = [];

        /// <summary>When set, answers each push from its name and base instead of <see cref="Outcomes"/>.</summary>
        public Func<string, int?, SkillPushOutcome>? Answer { get; set; }

        public Task<SkillPushOutcome> PushSkillAsync(
            Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, SkillProject? project, int? baseVersion,
            CancellationToken cancellationToken)
        {
            Pushed.Add((name, accessToken, files, project));
            Bases.Add(baseVersion);
            var projectId = project is null ? (Guid?)null : ProjectIdOf(project.Facts);

            // Like the registry: a push from a copy of a removed skill is refused; any other push of the name drops its tombstone.
            if (!Skills.Any(s => s.Name == name && s.ProjectId == projectId) &&
                new RegistrySkillList([], null, Removed).RemovalOf(name, projectId, baseVersion) is { } removal)
            {
                return Task.FromResult(new SkillPushOutcome(SkillPushStatus.Removed, removal.LastVersion, "refused: removed", projectId, RenamedTo: removal.RenamedTo));
            }

            var outcome = Answer is { } answer
                ? answer(name, baseVersion)
                : Outcomes.GetValueOrDefault(name) ?? new SkillPushOutcome(SkillPushStatus.Created, 1, null, projectId);

            // Like the registry: a skill it stored is in its list from then on.
            if (outcome.Succeeded && !Skills.Any(s => s.Name == name && s.ProjectId == outcome.ProjectId))
            {
                Skills.Add(new RegistrySkill(name, null, outcome.Version ?? 1, "", DateTimeOffset.UnixEpoch, outcome.ProjectId));
                Removed.RemoveAll(r => r.Name == name && r.ProjectId == outcome.ProjectId);
            }
            return Task.FromResult(outcome);
        }

        /// <summary>A stable project id for a folder: by root commit for git, else by folder, as the registry keys them.</summary>
        public static Guid ProjectIdOf(ProjectFacts facts) =>
            new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(facts.RootCommit ?? facts.Directory)));

        /// <summary>The key <see cref="Contents"/> holds a skill under: its name, or <c>&lt;project id&gt;/&lt;name&gt;</c>.</summary>
        public static string Key(string name, Guid? projectId) => projectId is { } id ? $"{id}/{name}" : name;

        public List<RegistrySkill> Skills { get; } = [];

        /// <summary>The names deleted or renamed away, as the list reports them.</summary>
        public List<RegistryRemovedSkill> Removed { get; } = [];

        /// <summary>False for a registry from before registry#58, which forgets a deleted or renamed skill entirely.</summary>
        public bool RemembersRemovals { get; init; } = true;
        public string? ListError { get; init; }
        public Dictionary<string, RegistrySkillContent> Contents { get; } = new(StringComparer.Ordinal);
        public List<string> Downloaded { get; } = [];

        public int ListCalls { get; private set; }

        public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(ListError is null ? new RegistrySkillList(Skills.ToList(), null, Removed.ToList()) : new RegistrySkillList(null, ListError));
        }

        /// <summary>Earlier versions, served when a download names a version: keyed <c>&lt;key&gt;@&lt;version&gt;</c>.</summary>
        public Dictionary<string, RegistrySkillContent> History { get; } = new(StringComparer.Ordinal);

        public Task<RegistrySkillDownload> GetSkillFilesAsync(
            Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken)
        {
            Downloaded.Add(Key(name, projectId));
            if (version is { } v && History.TryGetValue($"{Key(name, projectId)}@{v}", out var earlier))
                return Task.FromResult(new RegistrySkillDownload(earlier, null));
            return Task.FromResult(Contents.TryGetValue(Key(name, projectId), out var content) && (version is null || content.Version == version)
                ? new RegistrySkillDownload(content, null)
                : new RegistrySkillDownload(null, "not found"));
        }

        /// <summary>Each rename asked for: (old key, new name).</summary>
        public List<(string Key, string NewName)> Renamed { get; } = [];

        /// <summary>When set, every rename is refused with this message.</summary>
        public string? RenameError { get; set; }

        /// <summary>Each delete asked for, by key.</summary>
        public List<string> Deleted { get; } = [];

        public Task<ApiResult<string>> RenameSkillAsync(
            Uri registry, string accessToken, string name, Guid? projectId, string newName, CancellationToken cancellationToken)
        {
            Renamed.Add((Key(name, projectId), newName));
            if (RenameError is { } error)
                return Task.FromResult(ApiResult<string>.Fail(error));

            // Like the registry: the same skill, versions and content, under the new name.
            var index = Skills.FindIndex(s => s.Name == name && s.ProjectId == projectId);
            if (index >= 0)
            {
                if (RemembersRemovals)
                    Removed.Add(new RegistryRemovedSkill(name, projectId, Skills[index].Version, newName));
                Skills[index] = Skills[index] with { Name = newName };
            }
            if (Contents.Remove(Key(name, projectId), out var content))
                Contents[Key(newName, projectId)] = content with { Name = newName };
            return Task.FromResult(ApiResult<string>.Success(newName));
        }

        public Task<ApiResult<bool>> DeleteSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, CancellationToken cancellationToken)
        {
            Deleted.Add(Key(name, projectId));
            if (RemembersRemovals && Skills.FirstOrDefault(s => s.Name == name && s.ProjectId == projectId) is { } deleted)
                Removed.Add(new RegistryRemovedSkill(name, projectId, deleted.Version, null));
            Skills.RemoveAll(s => s.Name == name && s.ProjectId == projectId);
            Contents.Remove(Key(name, projectId));
            return Task.FromResult(ApiResult<bool>.Success(true));
        }
    }
}

/// <summary>Tests for <see cref="SkillFileReader"/> against a real temporary directory.</summary>
public sealed class SkillFileReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-read-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Reads_nested_files_with_forward_slash_paths_in_ordinal_order()
    {
        Write("SKILL.md", "hello");
        Write(Path.Combine("scripts", "run.sh"));
        Write(Path.Combine("references", "deep", "a.md"));

        var files = SkillFileReader.Read(_root);

        Assert.Equal(["SKILL.md", "references/deep/a.md", "scripts/run.sh"], files.Select(f => f.RelativePath));
        Assert.Equal("hello", Encoding.UTF8.GetString(files[0].Content));
    }

    [Fact]
    public void Hash_is_stable_and_changes_when_a_file_moves_or_changes()
    {
        Write("SKILL.md", "a");
        Write("b.md", "b");
        var first = SkillFileReader.Hash(SkillFileReader.Read(_root));
        Assert.Equal(first, SkillFileReader.Hash(SkillFileReader.Read(_root)));

        File.Move(Path.Combine(_root, "b.md"), Path.Combine(_root, "c.md"));
        var moved = SkillFileReader.Hash(SkillFileReader.Read(_root));
        Write("c.md", "changed");
        var changed = SkillFileReader.Hash(SkillFileReader.Read(_root));

        Assert.NotEqual(first, moved);
        Assert.NotEqual(moved, changed);
    }

    [Fact]
    public void Skips_version_control_and_dependency_folders()
    {
        Write("SKILL.md");
        Write(Path.Combine(".git", "HEAD"));
        Write(Path.Combine("node_modules", "pkg", "index.js"));

        var files = SkillFileReader.Read(_root);

        Assert.Equal("SKILL.md", Assert.Single(files).RelativePath);
    }

    [Fact]
    public void Skips_a_file_with_an_ignored_name_such_as_a_submodule_git_file()
    {
        // The registry refuses these names at push, so reading one would fail the whole skill.
        Write("SKILL.md");
        Write(".git", "gitdir: ../.git/modules/alpha");
        Write(Path.Combine("lib", "node_modules"), "x");

        var files = SkillFileReader.Read(_root);

        Assert.Equal("SKILL.md", Assert.Single(files).RelativePath);
    }

    [Fact]
    public void Does_not_follow_symbolic_links_below_the_root()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows needs elevation or developer mode.

        Write("SKILL.md");
        var outside = Directory.CreateTempSubdirectory("pstar-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "nope");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
            File.CreateSymbolicLink(Path.Combine(_root, "file-link.txt"), Path.Combine(outside, "secret.txt"));

            var files = SkillFileReader.Read(_root);

            Assert.Equal("SKILL.md", Assert.Single(files).RelativePath);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}

/// <summary>Tests for <see cref="RegistryApi"/>'s request shape and response mapping, over a stub handler.</summary>
public sealed class RegistryApiTests
{
    private static readonly Uri Registry = new("https://registry.test");

    private static readonly IReadOnlyList<SkillUploadFile> Files =
        [new("SKILL.md", "---\nname: alpha\n---"u8.ToArray()), new("scripts/run.sh", "echo"u8.ToArray())];

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task Push_sends_bearer_token_name_field_and_one_part_per_file()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, """{"status":"created","version":3}"""));
        using var api = new RegistryApi(handler);

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Created, 3, null), outcome);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://registry.test/v1/skills", handler.Uri!.ToString());
        Assert.Equal("Bearer tok", handler.Authorization);
        Assert.Contains("name=name", handler.Body);
        Assert.Contains("filename=SKILL.md", handler.Body);
        Assert.Matches("filename=\"?scripts/run.sh\"?", handler.Body);
        Assert.Contains("echo", handler.Body);
    }

    [Fact]
    public async Task A_project_push_sends_the_project_folder_as_a_json_field_and_reads_back_the_project()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, $$"""{"status":"created","version":1,"projectId":"{{projectId}}","projectName":"github.com/acme/a"}"""));
        using var api = new RegistryApi(handler);
        var machine = Guid.NewGuid();
        var facts = ProjectFacts.NotGit("/w/a", "/w/a");

        var outcome = await api.PushSkillAsync(Registry, "tok", "deploy", Files, new SkillProject(machine, facts), null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Created, 1, null, projectId, "github.com/acme/a"), outcome);
        Assert.Contains("name=project", handler.Body);
        Assert.Contains($"\"machineId\":\"{machine}\"", handler.Body);
        Assert.Contains("\"workingDirectory\":\"/w/a\"", handler.Body);
    }

    [Fact]
    public async Task A_global_push_sends_no_project_field()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, """{"status":"created","version":1}"""));
        using var api = new RegistryApi(handler);

        await api.PushSkillAsync(Registry, "tok", "deploy", Files, null, null, CancellationToken.None);

        Assert.DoesNotContain("name=project", handler.Body);
    }

    [Fact]
    public async Task Listing_asks_for_project_skills_too_and_reads_their_project()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"skills":[{"name":"deploy","version":1,"contentHash":"AB","updatedAt":"2026-11-19T00:00:00Z","projectId":null},{"name":"deploy","version":2,"contentHash":"CD","updatedAt":"2026-11-19T00:00:00Z","projectId":"{{projectId}}","projectName":"a"}]}"""));
        using var api = new RegistryApi(handler);

        var list = await api.ListSkillsAsync(Registry, "tok", CancellationToken.None);

        Assert.Equal("https://registry.test/v1/skills?includeProjects=true", handler.Uri!.ToString());
        Assert.Equal([null, projectId], list.Skills!.Select(s => s.ProjectId));
        Assert.Equal("a", list.Skills![1].ProjectName);
    }

    [Fact]
    public async Task Listing_sessions_since_a_local_time_sends_it_in_utc()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "[]"));
        using var api = new RegistryApi(handler);
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-6));

        await api.ListAsync(Registry, "tok", new TranscriptListQuery(null, null, null, since, 25), CancellationToken.None);

        Assert.Equal("since=2026-11-01T06:00:00.0000000+00:00", Uri.UnescapeDataString(handler.Uri!.Query.Split('&')[1]));
    }

    [Fact]
    public async Task Downloading_a_project_skill_names_its_project()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        using var api = new RegistryApi(handler);

        await api.GetSkillFilesAsync(Registry, "tok", "deploy", projectId, 3, CancellationToken.None);

        Assert.Equal($"https://registry.test/v1/skills/deploy/files?projectId={projectId}&version=3", handler.Uri!.ToString());
    }

    [Fact]
    public async Task A_push_with_a_base_sends_it_as_a_form_field_and_one_without_sends_none()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, """{"status":"created","version":3}"""));
        using var api = new RegistryApi(handler);

        await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, 2, CancellationToken.None);
        Assert.Contains("name=baseVersion", handler.Body);
        Assert.Contains("\r\n\r\n2\r\n", handler.Body);

        await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);
        Assert.DoesNotContain("baseVersion", handler.Body);
    }

    [Fact]
    public async Task A_stale_problem_maps_to_stale_with_the_registrys_current_version_and_project()
    {
        var projectId = Guid.NewGuid();
        var body = $$"""{"type":"https://protostar.dev/problems/stale-skill-version","title":"changed here and in the registry","status":409,"currentVersion":3,"projectId":"{{projectId}}"}""";
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.Conflict, body, "application/problem+json")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, 1, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Stale, 3, "changed here and in the registry", projectId), outcome);
    }

    [Fact]
    public async Task A_renamed_problem_maps_to_removed_with_the_last_version_and_the_new_name()
    {
        var body = """{"type":"https://protostar.dev/problems/skill-renamed","title":"renamed","status":409,"lastVersion":2,"renamedTo":"hello","projectId":null}""";
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.Conflict, body, "application/problem+json")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "greet-user", Files, null, 1, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Removed, 2, "renamed", RenamedTo: "hello"), outcome);
    }

    [Fact]
    public async Task A_deleted_problem_maps_to_removed_with_no_new_name()
    {
        var body = """{"type":"https://protostar.dev/problems/skill-deleted","title":"deleted","status":409,"lastVersion":4}""";
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.Conflict, body, "application/problem+json")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "tidy", Files, null, 3, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Removed, 4, "deleted"), outcome);
    }

    [Fact]
    public async Task The_list_carries_the_registrys_removed_names_and_none_from_an_older_registry()
    {
        var body = """{"skills":[],"removed":[{"name":"greet-user","projectId":null,"lastVersion":1,"renamedTo":"hello","removedAt":"2026-11-19T00:00:00Z","versions":[{"version":1,"contentHash":"ABC"}]}]}""";
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, body)));
        using var older = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"skills":[]}""")));

        var list = await api.ListSkillsAsync(Registry, "tok", CancellationToken.None);
        var olderList = await older.ListSkillsAsync(Registry, "tok", CancellationToken.None);

        var removed = Assert.Single(list.Removed!);
        Assert.Equal(("greet-user", (Guid?)null, 1, "hello"), (removed.Name, removed.ProjectId, removed.LastVersion, removed.RenamedTo));
        Assert.Equal([new RegistryRemovedVersion(1, "ABC")], removed.Versions);
        Assert.Equal([(removed, 1)], list.CopiesOf("greet-user", "abc"));
        Assert.Empty(list.CopiesOf("greet-user", "DEF"));
        Assert.Empty(olderList.Removed!);
        Assert.Null(olderList.RemovalOf("greet-user", null, 1));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void A_copy_is_of_a_removed_skill_when_it_synced_a_version_up_to_the_last_one(int? synced, bool removed)
    {
        var list = new RegistrySkillList([], null, [new RegistryRemovedSkill("tidy", null, 3, null)]);

        Assert.Equal(removed, list.RemovalOf("tidy", null, synced) is not null);
    }

    [Fact]
    public async Task A_409_of_another_type_is_an_ordinary_refusal()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.Conflict, """{"title":"something else","status":409}""", "application/problem+json")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, 1, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Refused, null, "something else"), outcome);
    }

    [Fact]
    public async Task Ok_maps_to_unchanged_with_the_current_version()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"status":"unchanged","version":2}""")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Unchanged, 2, null), outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Only a skill's creator can push new versions of it.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "The skill exceeds 10 MB.")]
    [InlineData(HttpStatusCode.BadRequest, "The skill is missing a SKILL.md at its root.")]
    public async Task Client_errors_map_to_refused_with_the_problem_title(HttpStatusCode status, string title)
    {
        var body = $$"""{"title":"{{title}}","status":{{(int)status}}}""";
        using var api = new RegistryApi(new StubHandler(_ => Json(status, body, "application/problem+json")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Refused, null, title), outcome);
    }

    [Fact]
    public async Task Unauthorized_is_a_failure_pointing_at_login()
    {
        using var api = new RegistryApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("auth login", outcome.Message);
    }

    [Fact]
    public async Task Server_error_is_a_failure()
    {
        using var api = new RegistryApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("502", outcome.Message);
    }

    [Fact]
    public async Task Network_error_is_a_failure_not_an_exception()
    {
        using var api = new RegistryApi(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, null, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("connection refused", outcome.Message);
    }

    [Fact]
    public async Task Rename_posts_the_new_name_scoped_to_the_project_and_reads_back_the_name()
    {
        var project = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"oldName":"my notes","name":"journal","projectId":null}"""));
        using var api = new RegistryApi(handler);

        var result = await api.RenameSkillAsync(Registry, "tok", "my notes", project, "journal", CancellationToken.None);

        Assert.Equal("journal", result.Value);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"https://registry.test/v1/skills/my%20notes/rename?projectId={project}", handler.Uri!.AbsoluteUri);
        Assert.Contains("\"newName\":\"journal\"", handler.Body);
    }

    [Fact]
    public async Task A_refused_rename_carries_the_registrys_reason()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.Conflict, """{"title":"There is already a skill named 'journal' here."}""", "application/problem+json")));

        var result = await api.RenameSkillAsync(Registry, "tok", "notes", null, "journal", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("already a skill named 'journal'", result.Error);
    }

    [Fact]
    public async Task Delete_sends_a_delete_for_the_global_skill_when_there_is_no_project()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = new RegistryApi(handler);

        var result = await api.DeleteSkillAsync(Registry, "tok", "journal", null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("https://registry.test/v1/skills/journal", handler.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_link_reads_the_skills_that_moved()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"outcome":"linked","project":{"name":"repo"},"movedSkills":["deploy","notes"]}""")));

        var result = await api.LinkFolderAsync(Registry, "tok", Guid.NewGuid(), ProjectFacts.NotGit("/x", "/x"), "repo", CancellationToken.None);

        Assert.Equal(["deploy", "notes"], result.Value!.MovedSkills);
    }

    [Fact]
    public async Task Meta_is_parsed_and_an_unreachable_registry_gives_null()
    {
        using var ok = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"service":"protostar-registry","apiMajors":[1]}""")));
        using var down = new RegistryApi(new StubHandler(_ => throw new HttpRequestException("down")));

        var meta = await ok.GetMetaAsync(Registry, CancellationToken.None);
        Assert.Equal(new[] { 1 }, meta?.ApiMajors);
        Assert.Null(await down.GetMetaAsync(Registry, CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
