using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Black-box contract tests for <see cref="ISkillService"/> as implemented by <c>SkillService</c>.
/// Every expectation is derived from the interface and the documented result/failure types, not from
/// the implementation. The service is a pure orchestrator over <see cref="IHarnessCatalog"/> and the
/// harness capabilities, so all collaborators here are in-memory fakes; no disk or environment.
/// </summary>
public sealed class SkillServiceTests
{
    private static ISkillService NewSut(IHarnessCatalog catalog) => new SkillService(catalog);

    private static DiscoveredSkill Skill(string name, SkillScope scope) =>
        new() { Name = name, Scope = scope, Path = "/skills/" + name };

    // ---- happy path & fan-out ------------------------------------------------

    [Fact]
    public void Single_located_skill_harness_returns_its_skills_with_no_failure()
    {
        var h = new FakeSkillHarness { Id = "cc", Skills = [Skill("alpha", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(h)).Discover(null, null, null);

        Assert.Equal(SkillQueryFailure.None, r.Failure);
        Assert.Null(r.OffendingHarnessId);
        Assert.Equal("alpha", Assert.Single(r.Skills).Name);
    }

    [Fact]
    public void Null_harnessId_queries_every_skill_capable_harness_and_merges_results()
    {
        var a = new FakeSkillHarness { Id = "a", Skills = [Skill("alpha", SkillScope.Global)] };
        var b = new FakeSkillHarness { Id = "b", Skills = [Skill("beta", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(a, b)).Discover(null, null, null);

        Assert.Equal(SkillQueryFailure.None, r.Failure);
        Assert.Equal(2, r.Skills.Count);
        Assert.Contains(r.Skills, s => s.Name == "alpha");
        Assert.Contains(r.Skills, s => s.Name == "beta");
    }

    [Fact]
    public void Null_harnessId_excludes_harnesses_that_do_not_support_discovery()
    {
        var skillful = new FakeSkillHarness { Id = "a", Skills = [Skill("alpha", SkillScope.Global)] };
        var plain = new FakeHarness { Id = "b" };
        var r = NewSut(new FakeCatalog(skillful, plain)).Discover(null, null, null);

        Assert.Equal(SkillQueryFailure.None, r.Failure);
        Assert.Equal("alpha", Assert.Single(r.Skills).Name);
    }

    [Fact]
    public void Null_harnessId_with_a_skill_harness_that_is_not_located_contributes_nothing()
    {
        var located = new FakeSkillHarness { Id = "a", Located = true, Skills = [Skill("alpha", SkillScope.Global)] };
        var unlocated = new FakeSkillHarness { Id = "b", Located = false, Skills = [Skill("beta", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(located, unlocated)).Discover(null, null, null);

        Assert.Equal(SkillQueryFailure.None, r.Failure);
        Assert.Equal("alpha", Assert.Single(r.Skills).Name);
        // ASSUMES: a harness whose TryLocate returns false is never asked for skills.
        Assert.False(unlocated.DiscoverCalled);
    }

    [Fact]
    public void Empty_catalog_returns_empty_skills_and_no_failure()
    {
        var r = NewSut(new FakeCatalog()).Discover(null, null, null);

        Assert.Equal(SkillQueryFailure.None, r.Failure);
        Assert.Empty(r.Skills);
    }

    // ---- explicit harness selection -----------------------------------------

    [Fact]
    public void Explicit_harnessId_limits_the_query_to_that_one_harness()
    {
        var a = new FakeSkillHarness { Id = "a", Skills = [Skill("alpha", SkillScope.Global)] };
        var b = new FakeSkillHarness { Id = "b", Skills = [Skill("beta", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(a, b)).Discover("a", null, null);

        Assert.Equal("alpha", Assert.Single(r.Skills).Name);
        Assert.False(b.DiscoverCalled);
    }

    [Fact]
    public void Unknown_harnessId_reports_UnknownHarness_and_echoes_the_input_with_empty_skills()
    {
        var a = new FakeSkillHarness { Id = "a", Skills = [Skill("alpha", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(a)).Discover("nope", null, null);

        Assert.Equal(SkillQueryFailure.UnknownHarness, r.Failure);
        Assert.Equal("nope", r.OffendingHarnessId);
        Assert.Empty(r.Skills);
    }

    [Fact]
    public void Known_harnessId_without_skill_capability_reports_Unsupported_and_echoes_the_input()
    {
        var plain = new FakeHarness { Id = "plain" };
        var r = NewSut(new FakeCatalog(plain)).Discover("plain", null, null);

        Assert.Equal(SkillQueryFailure.Unsupported, r.Failure);
        Assert.Equal("plain", r.OffendingHarnessId);
        Assert.Empty(r.Skills);
    }

    [Fact]
    public void Explicit_harnessId_for_a_supported_but_unlocated_harness_returns_empty_without_failure()
    {
        // Resolves a contract gap: a known, skill-capable harness that simply is not installed is
        // neither UnknownHarness (it is known) nor Unsupported (it is capable). The only coherent
        // outcome from the two defined failure codes is "no skills found", Failure.None.
        var h = new FakeSkillHarness { Id = "a", Located = false, Skills = [Skill("alpha", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(h)).Discover("a", null, null);

        Assert.Equal(SkillQueryFailure.None, r.Failure);
        Assert.Empty(r.Skills);
    }

    // ---- ordering ------------------------------------------------------------

    [Fact]
    public void Results_are_ordered_by_scope_global_before_project()
    {
        var h = new FakeSkillHarness
        {
            Id = "a",
            Skills = [Skill("zzz", SkillScope.Project), Skill("aaa", SkillScope.Global)],
        };
        var r = NewSut(new FakeCatalog(h)).Discover(null, null, null);

        Assert.Equal(SkillScope.Global, r.Skills[0].Scope);
        Assert.Equal(SkillScope.Project, r.Skills[1].Scope);
    }

    [Fact]
    public void Within_a_scope_results_are_ordered_by_name()
    {
        var h = new FakeSkillHarness
        {
            Id = "a",
            Skills = [Skill("banana", SkillScope.Global), Skill("apple", SkillScope.Global)],
        };
        var r = NewSut(new FakeCatalog(h)).Discover(null, null, null);

        Assert.Equal(new[] { "apple", "banana" }, r.Skills.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Within_a_scope_name_ordering_is_case_insensitive()
    {
        // ASSUMES: "ordered by name for stable display" is a case-insensitive sort, so "apple"
        // precedes "Zebra" rather than ordinal (all-uppercase ahead of all-lowercase).
        var h = new FakeSkillHarness
        {
            Id = "a",
            Skills = [Skill("Zebra", SkillScope.Global), Skill("apple", SkillScope.Global)],
        };
        var r = NewSut(new FakeCatalog(h)).Discover(null, null, null);

        Assert.Equal(new[] { "apple", "Zebra" }, r.Skills.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Ordering_applies_across_merged_harnesses_scope_then_name()
    {
        var h1 = new FakeSkillHarness { Id = "a", Skills = [Skill("delta", SkillScope.Project)] };
        var h2 = new FakeSkillHarness { Id = "b", Skills = [Skill("charlie", SkillScope.Global)] };
        var r = NewSut(new FakeCatalog(h1, h2)).Discover(null, null, null);

        // charlie (Global) sorts ahead of delta (Project) despite coming from the later harness.
        Assert.Equal(new[] { "charlie", "delta" }, r.Skills.Select(s => s.Name).ToArray());
    }

    // ---- argument forwarding -------------------------------------------------

    [Fact]
    public void HarnessHome_is_forwarded_as_the_rootOverride_to_TryLocate()
    {
        var h = new FakeSkillHarness { Id = "a", Skills = [] };
        NewSut(new FakeCatalog(h)).Discover(null, "/my/home", null);

        Assert.Equal("/my/home", h.LastRootOverride);
    }

    [Fact]
    public void ProjectStart_is_forwarded_to_DiscoverSkills()
    {
        var h = new FakeSkillHarness { Id = "a", Skills = [] };
        NewSut(new FakeCatalog(h)).Discover(null, null, "/proj/start");

        Assert.True(h.DiscoverCalled);
        Assert.Equal("/proj/start", h.LastProjectStart);
    }

    [Fact]
    public void Located_harness_DiscoverSkills_receives_the_location_from_TryLocate()
    {
        var loc = new HarnessLocation("/cfg/x", "/cfg/x/settings.json");
        var h = new FakeSkillHarness { Id = "a", Location = loc, Skills = [] };
        NewSut(new FakeCatalog(h)).Discover(null, null, null);

        Assert.Equal(loc, h.LastDiscoverLocation);
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeCatalog : IHarnessCatalog
    {
        private readonly List<IHarness> _all;
        public FakeCatalog(params IHarness[] all) => _all = all.ToList();
        public IReadOnlyList<IHarness> All => _all;
        public IHarness? ById(string id) =>
            _all.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));
        public string KnownIds => string.Join(", ", _all.Select(h => h.Id));
    }

    private sealed class FakeHarness : IHarness
    {
        public string Id { get; init; } = "fake";
        public string DisplayName { get; init; } = "Fake";
        public bool Located { get; init; } = true;
        public HarnessLocation Location { get; init; } = new("/cfg", "/cfg/settings.json");
        public string? LastRootOverride { get; private set; }

        public bool TryLocate(string? rootOverride, out HarnessLocation location)
        {
            LastRootOverride = rootOverride;
            location = Location;
            return Located;
        }
    }

    private sealed class FakeSkillHarness : IHarness, ISkillCapability
    {
        public string Id { get; init; } = "skillful";
        public string DisplayName { get; init; } = "Skillful";
        public bool Located { get; init; } = true;
        public HarnessLocation Location { get; init; } = new("/cfg", "/cfg/settings.json");
        public IReadOnlyList<DiscoveredSkill> Skills { get; init; } = [];

        public string? LastRootOverride { get; private set; }
        public HarnessLocation? LastDiscoverLocation { get; private set; }
        public string? LastProjectStart { get; private set; }
        public bool DiscoverCalled { get; private set; }

        public bool TryLocate(string? rootOverride, out HarnessLocation location)
        {
            LastRootOverride = rootOverride;
            location = Location;
            return Located;
        }

        public IReadOnlyList<DiscoveredSkill> DiscoverSkills(HarnessLocation location, string? projectStart)
        {
            DiscoverCalled = true;
            LastDiscoverLocation = location;
            LastProjectStart = projectStart;
            return Skills;
        }
    }
}
