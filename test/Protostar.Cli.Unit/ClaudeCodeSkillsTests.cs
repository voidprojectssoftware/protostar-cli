using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Black-box tests for the <see cref="ISkillCapability"/> contract as implemented by
/// <c>ClaudeCodeHarness</c>. Derived from the documented contract only, not the implementation.
/// </summary>
public sealed class ClaudeCodeSkillsTests : IDisposable
{
    private readonly string _root;

    public ClaudeCodeSkillsTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "protostar-skillstest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // Hermetic ceiling. Project discovery walks UP from projectStart looking for a `.claude`
        // directory. The system temp dir lives under the real user home, which has its own
        // `~/.claude`; without a boundary the walk would escape this sandbox and find real skills.
        // A `.claude` here (with no `skills` subdir) stops the walk at _root, so every test sees only
        // the fixtures it creates. Project fixtures place their own nearer `.claude`, found first.
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort; temp cleanup must not fail a test run.
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static ISkillCapability NewSut() =>
        new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

    private static HarnessLocation LocationFor(string configDir) =>
        new(configDir, Path.Combine(configDir, "settings.json"));

    private static string Norm(string p) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private string Sub(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteSkill(string skillDir, string manifest)
    {
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), manifest);
        return skillDir;
    }

    private static string Manifest(string name, string? description)
    {
        var desc = description is null ? "" : $"description: {description}\n";
        return $"---\nname: {name}\n{desc}---\n\n# {name}\n";
    }

    private static string AddGlobalSkill(string configDir, string name, string? description)
    {
        var skillDir = Path.Combine(configDir, "skills", name);
        return WriteSkill(skillDir, Manifest(name, description));
    }

    private static string AddProjectSkill(string projectRoot, string name, string? description)
    {
        var skillDir = Path.Combine(projectRoot, ".claude", "skills", name);
        return WriteSkill(skillDir, Manifest(name, description));
    }

    private static DiscoveredSkill Single(IReadOnlyList<DiscoveredSkill> skills, string name) =>
        Assert.Single(skills.Where(s => s.Name == name));

    // ---- happy paths -------------------------------------------------------

    [Fact]
    public void Discovers_a_global_skill_and_tags_it_Global()
    {
        // ASSUMES: directory name == front-matter name, so Name is unambiguous either way.
        var config = Sub("config");
        var skillDir = AddGlobalSkill(config, "alpha", "first skill");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        var skill = Single(result, "alpha");
        Assert.Equal(SkillScope.Global, skill.Scope);
        Assert.Equal(Norm(skillDir), Norm(skill.Path));
    }

    [Fact]
    public void Discovers_a_project_skill_when_projectStart_is_given_and_tags_it_Project()
    {
        // ASSUMES: directory name == front-matter name, so Name is unambiguous either way.
        var config = Sub("config");
        var project = Sub("project");
        var skillDir = AddProjectSkill(project, "beta", "project skill");
        var start = Path.Combine(project, "src", "nested");
        Directory.CreateDirectory(start);
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), start);

        var skill = Single(result, "beta");
        Assert.Equal(SkillScope.Project, skill.Scope);
        Assert.Equal(Norm(skillDir), Norm(skill.Path));
    }

    [Fact]
    public void Discovers_both_global_and_project_skills_together()
    {
        var config = Sub("config");
        var project = Sub("project");
        AddGlobalSkill(config, "alpha", "g");
        AddProjectSkill(project, "beta", "p");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), project);

        Assert.Equal(2, result.Count);
        Assert.Equal(SkillScope.Global, Single(result, "alpha").Scope);
        Assert.Equal(SkillScope.Project, Single(result, "beta").Scope);
    }

    [Fact]
    public void Reads_a_plain_description_from_front_matter()
    {
        var config = Sub("config");
        AddGlobalSkill(config, "alpha", "a tidy description");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        Assert.Equal("a tidy description", Single(result, "alpha").Description);
    }

    [Fact]
    public void Reads_a_folded_block_scalar_description_as_single_spaced_text()
    {
        // ASSUMES: a YAML folded block scalar (>-) is joined into one line, single-spaced,
        //          with the ">-" marker stripped.
        var config = Sub("config");
        var skillDir = Path.Combine(config, "skills", "folded");
        var manifest =
            "---\n" +
            "name: folded\n" +
            "description: >-\n" +
            "  A long description that wraps\n" +
            "  across several indented lines.\n" +
            "---\n\n# folded\n";
        WriteSkill(skillDir, manifest);
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        Assert.Equal(
            "A long description that wraps across several indented lines.",
            Single(result, "folded").Description);
    }

    [Fact]
    public void Global_and_project_skills_of_the_same_name_are_both_returned_tagged_separately()
    {
        var config = Sub("config");
        var project = Sub("project");
        var globalDir = AddGlobalSkill(config, "dup", "global one");
        var projectDir = AddProjectSkill(project, "dup", "project one");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), project);

        Assert.Equal(2, result.Count);
        var asGlobal = Assert.Single(result.Where(s => s.Scope == SkillScope.Global));
        var asProject = Assert.Single(result.Where(s => s.Scope == SkillScope.Project));
        Assert.Equal("dup", asGlobal.Name);
        Assert.Equal("dup", asProject.Name);
        Assert.Equal(Norm(globalDir), Norm(asGlobal.Path));
        Assert.Equal(Norm(projectDir), Norm(asProject.Path));
    }

    // ---- negative / edge cases --------------------------------------------

    [Fact]
    public void Null_projectStart_skips_the_project_scope_and_returns_only_global()
    {
        var config = Sub("config");
        var project = Sub("project");
        AddGlobalSkill(config, "alpha", "g");
        AddProjectSkill(project, "beta", "p");

        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        var only = Assert.Single(result);
        Assert.Equal("alpha", only.Name);
        Assert.Equal(SkillScope.Global, only.Scope);
    }

    [Fact]
    public void ConfigDir_with_no_skills_directory_returns_empty_without_throwing()
    {
        var config = Sub("config");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        Assert.Empty(result);
    }

    [Fact]
    public void A_skills_subdirectory_without_SKILL_md_is_not_reported()
    {
        var config = Sub("config");
        Directory.CreateDirectory(Path.Combine(config, "skills", "not-a-skill"));
        AddGlobalSkill(config, "real", "ok");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        var only = Assert.Single(result);
        Assert.Equal("real", only.Name);
    }

    [Fact]
    public void ProjectStart_in_a_project_without_a_skills_directory_yields_no_project_skills()
    {
        // The nearest `.claude` ancestor of this start path is the sandbox ceiling at _root, which has
        // no `skills` subdir. A project root with no skills yields nothing in the project scope.
        var config = Sub("config");
        AddGlobalSkill(config, "alpha", "g");
        var orphan = Path.Combine(_root, "orphan", "deep", "leaf");
        Directory.CreateDirectory(orphan);
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), orphan);

        var only = Assert.Single(result);
        Assert.Equal("alpha", only.Name);
        Assert.Equal(SkillScope.Global, only.Scope);
    }

    [Fact]
    public void A_manifest_without_a_description_yields_null_Description()
    {
        var config = Sub("config");
        AddGlobalSkill(config, "nodesc", description: null);
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        Assert.Null(Single(result, "nodesc").Description);
    }

    [Fact]
    public void Everything_empty_returns_an_empty_list()
    {
        var config = Sub("config");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Missing_ConfigDir_on_disk_returns_empty_without_throwing()
    {
        // ConfigDir that was never created. Contract: returns empty, never throws,
        // including when the harness has no skills directory at all.
        var config = Path.Combine(_root, "does-not-exist");
        var sut = NewSut();

        var result = sut.DiscoverSkills(LocationFor(config), projectStart: null);

        Assert.Empty(result);
    }
}
