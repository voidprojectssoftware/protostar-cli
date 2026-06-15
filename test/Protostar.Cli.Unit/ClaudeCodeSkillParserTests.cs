using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Black-box contract tests for <see cref="IClaudeCodeSkillParser"/>. Every expectation is derived
/// from the interface and supporting-type doc comments, not from the parser implementation.
/// </summary>
public sealed class ClaudeCodeSkillParserTests : IDisposable
{
    private readonly string _root;

    public ClaudeCodeSkillParserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "protostar-parsertest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    private static IClaudeCodeSkillParser NewSut() => new ClaudeCodeSkillParser();

    private string WriteSkill(string name, string manifest)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), manifest);
        return dir;
    }

    private string WriteDirWithoutManifest(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ----- Manifest presence gate -------------------------------------------------

    [Fact]
    public void TryParse_with_no_manifest_returns_false()
    {
        var dir = WriteDirWithoutManifest("no-manifest");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.False(ok);
        Assert.Null(skill);
    }

    [Fact]
    public void TryParse_with_manifest_present_returns_true()
    {
        var dir = WriteSkill("present", "---\nname: present\n---\nbody\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.NotNull(skill);
    }

    // ----- Core identity fields ---------------------------------------------------

    [Fact]
    public void TryParse_with_name_sets_name_to_front_matter_value()
    {
        var dir = WriteSkill("dir-name", "---\nname: the-skill\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal("the-skill", skill!.Name);
    }

    [Fact]
    public void TryParse_sets_directory_to_passed_directory()
    {
        var dir = WriteSkill("dir-echo", "---\nname: dir-echo\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal(dir, skill!.Directory);
    }

    [Theory]
    // Token passed as a string because the SkillScope enum is internal and cannot appear in the
    // signature of a public xUnit theory method (CS0051); parsed back to the enum in the body.
    [InlineData("Global")]
    [InlineData("Project")]
    public void TryParse_sets_scope_to_passed_scope(string scopeName)
    {
        var scope = Enum.Parse<SkillScope>(scopeName);
        var dir = WriteSkill("scope-echo-" + scopeName, "---\nname: scope-echo\n---\n");

        var ok = NewSut().TryParse(dir, scope, out var skill);

        Assert.True(ok);
        Assert.Equal(scope, skill!.Scope);
    }

    // ----- Name fallback ----------------------------------------------------------

    [Fact]
    public void TryParse_with_name_absent_falls_back_to_directory_name()
    {
        var dir = WriteSkill("fallback-here", "---\ndescription: something\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal("fallback-here", skill!.Name);
    }

    [Fact]
    public void TryParse_with_blank_name_falls_back_to_directory_name()
    {
        var dir = WriteSkill("blank-name-dir", "---\nname: \"\"\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal("blank-name-dir", skill!.Name);
    }

    [Fact]
    public void TryParse_with_no_front_matter_at_all_falls_back_to_directory_name()
    {
        var dir = WriteSkill("plain-body-dir", "# Just a heading\n\nSome prose.\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal("plain-body-dir", skill!.Name);
        Assert.False(string.IsNullOrWhiteSpace(skill.Name));
    }

    [Fact]
    public void TryParse_name_is_never_blank()
    {
        var dir = WriteSkill("never-blank", "---\nname: \"   \"\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.False(string.IsNullOrWhiteSpace(skill!.Name));
    }

    // ----- Description ------------------------------------------------------------

    [Fact]
    public void TryParse_with_description_sets_description()
    {
        var dir = WriteSkill("desc", "---\nname: desc\ndescription: does a thing\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal("does a thing", skill!.Description);
    }

    [Fact]
    public void TryParse_with_description_absent_leaves_description_null()
    {
        var dir = WriteSkill("no-desc", "---\nname: no-desc\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Null(skill!.Description);
    }

    [Fact]
    public void TryParse_with_blank_description_leaves_description_null()
    {
        var dir = WriteSkill("blank-desc", "---\nname: blank-desc\ndescription: \"\"\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Null(skill!.Description);
    }

    // ----- Body -------------------------------------------------------------------

    [Fact]
    public void TryParse_preserves_markdown_body_after_front_matter()
    {
        const string body = "# Title\n\nThis is the body of the skill.\n";
        var dir = WriteSkill("body-skill", "---\nname: body-skill\n---\n" + body);

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Contains("This is the body of the skill.", skill!.Body);
    }

    [Fact]
    public void TryParse_with_no_body_leaves_body_empty()
    {
        var dir = WriteSkill("empty-body", "---\nname: empty-body\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.True(string.IsNullOrEmpty(skill!.Body.Trim()));
    }

    // ----- Defaults for omitted fields --------------------------------------------

    [Fact]
    public void TryParse_minimal_manifest_uses_documented_defaults()
    {
        var dir = WriteSkill("defaults", "---\nname: defaults\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.NotNull(skill);

        Assert.False(skill!.DisableModelInvocation);
        Assert.True(skill.UserInvocable);

        Assert.Null(skill.WhenToUse);
        Assert.Null(skill.ArgumentHint);
        Assert.Null(skill.Model);
        Assert.Null(skill.Effort);
        Assert.Null(skill.Context);
        Assert.Null(skill.Agent);
        Assert.Null(skill.License);
        Assert.Null(skill.Compatibility);
        Assert.Null(skill.InterceptTool);
        Assert.Null(skill.InterceptAfterTool);

        Assert.Empty(skill.Arguments);
        Assert.Empty(skill.AllowedTools);
        Assert.Empty(skill.DisallowedTools);
        Assert.Empty(skill.Paths);
        Assert.Empty(skill.Metadata);
        Assert.Empty(skill.Hooks);
        Assert.Empty(skill.UnknownFields);
    }

    // ----- Malformed front matter does not throw ----------------------------------

    [Fact]
    public void TryParse_with_malformed_front_matter_does_not_throw()
    {
        var dir = WriteSkill("malformed", "---\nthis: is: not: : valid yaml [\n  - broken\n");

        var ex = Record.Exception(() => NewSut().TryParse(dir, SkillScope.Project, out _));

        Assert.Null(ex);
    }

    [Fact]
    public void TryParse_with_malformed_front_matter_falls_back_to_directory_name()
    {
        var dir = WriteSkill("malformed-name", "---\n  : : broken [ {\nname\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.NotNull(skill);
        Assert.Equal("malformed-name", skill!.Name);
    }

    // ----- UnknownFields preservation ---------------------------------------------

    [Fact]
    public void TryParse_preserves_unnamed_front_matter_key_in_unknown_fields()
    {
        var dir = WriteSkill(
            "unknown-fields",
            "---\nname: unknown-fields\ntotally-not-a-known-field: some-value\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Contains("totally-not-a-known-field", skill!.UnknownFields.Keys);
    }

    [Fact]
    public void TryParse_does_not_route_known_field_into_unknown_fields()
    {
        var dir = WriteSkill(
            "known-not-unknown",
            "---\nname: known-not-unknown\ndescription: a known field\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.DoesNotContain("description", skill!.UnknownFields.Keys);
    }

    // ----- Effort mapping (confident spellings only) ------------------------------

    [Theory]
    // ASSUMES: the lowercase enum-member spelling is the front-matter token for these levels.
    // Token passed as a string (the SkillEffort enum is internal, so it cannot be an InlineData
    // parameter on a public theory method, CS0051); the expected enum is parsed in the body.
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void TryParse_maps_effort_token_to_enum(string token)
    {
        var expected = Enum.Parse<SkillEffort>(token, ignoreCase: true);
        var dir = WriteSkill("effort-" + token, $"---\nname: effort\neffort: {token}\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal(expected, skill!.Effort);
    }

    // ----- Context mapping (confident spellings only) -----------------------------

    [Fact]
    public void TryParse_maps_context_fork_to_enum()
    {
        // ASSUMES: `fork` is the front-matter token for SkillContext.Fork.
        var dir = WriteSkill("ctx-fork", "---\nname: ctx-fork\ncontext: fork\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal(SkillContext.Fork, skill!.Context);
    }

    [Fact]
    public void TryParse_maps_context_default_to_enum()
    {
        // ASSUMES: `default` is the front-matter token for SkillContext.Default.
        var dir = WriteSkill("ctx-default", "---\nname: ctx-default\ncontext: default\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal(SkillContext.Default, skill!.Context);
    }

    // ----- allowed-tools as a YAML sequence ---------------------------------------

    [Fact]
    public void TryParse_reads_allowed_tools_yaml_sequence()
    {
        // ASSUMES: a YAML block sequence is a supported shape for `allowed-tools`.
        var dir = WriteSkill(
            "allowed-tools",
            "---\nname: allowed-tools\nallowed-tools:\n  - Read\n  - Edit\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.Equal(new[] { "Read", "Edit" }, skill!.AllowedTools);
    }

    // ----- disable-model-invocation / user-invocable booleans ---------------------

    [Fact]
    public void TryParse_reads_disable_model_invocation_true()
    {
        var dir = WriteSkill(
            "disable-invoke",
            "---\nname: disable-invoke\ndisable-model-invocation: true\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.True(skill!.DisableModelInvocation);
    }

    [Fact]
    public void TryParse_reads_user_invocable_false()
    {
        var dir = WriteSkill(
            "hidden",
            "---\nname: hidden\nuser-invocable: false\n---\n");

        var ok = NewSut().TryParse(dir, SkillScope.Project, out var skill);

        Assert.True(ok);
        Assert.False(skill!.UserInvocable);
    }
}
