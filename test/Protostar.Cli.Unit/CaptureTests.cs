using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="ICaptureService"/> and the line <see cref="CapturePresenter"/> writes.</summary>
public sealed class CaptureServiceTests
{
    // A directory that exists on every OS, since the service ignores a cwd that is not on disk.
    private static readonly string Cwd = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

    private static readonly string SkillUse =
        $$$"""{"session_id":"s1","cwd":{{{System.Text.Json.JsonSerializer.Serialize(Cwd)}}},"hook_event_name":"PostToolUse","tool_name":"Skill","tool_input":{"skill":"alpha"}}""";

    private static (ICaptureService Sut, FakePush Push) Build(SkillPushRunResult? result = null)
    {
        var push = new FakePush(result ?? Ok(SkillPushStatus.Created, 4));
        return (new CaptureService(push), push);
    }

    private static SkillPushRunResult Ok(SkillPushStatus status, int? version) => new(
        SkillPushRunFailure.None, null, new Uri("https://registry.test"),
        [new SkillPushEntry(new DiscoveredSkill { Name = "alpha", Scope = SkillScope.Global, Path = "/s/alpha" }, new SkillPushOutcome(status, version, null))]);

    [Fact]
    public async Task A_skill_use_pushes_that_skill_from_the_session_directory()
    {
        var (sut, push) = Build();

        var result = await sut.HandleAsync("PostToolUse", SkillUse, CancellationToken.None);

        Assert.Equal(CaptureAction.SkillBackedUp, result.Action);
        var request = Assert.Single(push.Requests);
        Assert.Equal(["alpha"], request.Names);
        Assert.Equal(Cwd, request.ProjectStart);
        Assert.False(request.Force);
    }

    [Fact]
    public async Task Events_other_than_PostToolUse_are_ignored()
    {
        var (sut, push) = Build();

        var result = await sut.HandleAsync("SessionStart", SkillUse, CancellationToken.None);

        Assert.Equal(CaptureAction.Ignored, result.Action);
        Assert.Empty(push.Requests);
    }

    [Fact]
    public async Task A_cwd_that_does_not_exist_skips_project_skills_but_still_pushes()
    {
        var (sut, push) = Build();
        var payload = """{"tool_name":"Skill","cwd":"/definitely/not/here","tool_input":{"skill":"alpha"}}""";

        await sut.HandleAsync("PostToolUse", payload, CancellationToken.None);

        Assert.Null(Assert.Single(push.Requests).ProjectStart);
    }

    [Theory]
    [InlineData("PostToolUse", "")]
    [InlineData("PostToolUse", "not json")]
    [InlineData("PostToolUse", """{"tool_name":"Bash","tool_input":{"command":"ls"}}""")]
    [InlineData("PostToolUse", """{"tool_name":"Skill","tool_input":{}}""")]
    [InlineData("PostToolUse", """{"tool_name":"Skill","tool_input":{"skill":"some-plugin:alpha"}}""")]
    public async Task Anything_other_than_a_local_skill_use_is_ignored_without_pushing(string hook, string payload)
    {
        var (sut, push) = Build();

        var result = await sut.HandleAsync(hook, payload, CancellationToken.None);

        Assert.Equal(CaptureAction.Ignored, result.Action);
        Assert.Empty(push.Requests);
    }

    [Fact]
    public async Task A_push_that_throws_becomes_a_failed_capture_not_an_exception()
    {
        var push = new FakePush(null) { Throw = new InvalidOperationException("boom") };
        var sut = new CaptureService(push);

        var result = await sut.HandleAsync("PostToolUse", SkillUse, CancellationToken.None);

        Assert.Equal(CaptureAction.Failed, result.Action);
        Assert.Contains("boom", result.Message);
    }

    [Fact]
    public async Task A_timeout_becomes_a_failed_capture()
    {
        var push = new FakePush(null) { Throw = new OperationCanceledException() };
        var sut = new CaptureService(push);

        var result = await sut.HandleAsync("PostToolUse", SkillUse, CancellationToken.None);

        Assert.Equal(CaptureAction.Failed, result.Action);
        Assert.Contains("Timed out", result.Message);
    }

    [Theory]
    [InlineData((int)SkillPushStatus.Created, 4, "backed up skill 'alpha' as v4", false)]
    [InlineData((int)SkillPushStatus.Unchanged, 4, "skill 'alpha' unchanged", false)]
    [InlineData((int)SkillPushStatus.Refused, null, "not backed up", true)]
    public void Presenter_writes_one_line_logs_only_problems_and_always_exits_zero(
        int status, int? version, string expected, bool logged)
    {
        var result = new CaptureResult(CaptureAction.SkillBackedUp, "alpha", Ok((SkillPushStatus)status, version), null);
        var output = new StringWriter();
        var log = new List<string>();

        var exit = CapturePresenter.Render("PostToolUse", result, output, log.Add);

        Assert.Equal(0, exit);
        Assert.Contains(expected, output.ToString());
        Assert.Equal(logged, log.Count == 1);
    }

    [Fact]
    public void Presenter_logs_not_logged_in_but_not_an_unknown_skill()
    {
        var notLoggedIn = new CaptureResult(CaptureAction.SkillBackedUp, "alpha", SkillPushRunResult.Fail(SkillPushRunFailure.NotLoggedIn, null), null);
        var unknown = new CaptureResult(CaptureAction.SkillBackedUp, "alpha", SkillPushRunResult.Fail(SkillPushRunFailure.SkillNotFound, "alpha"), null);
        var log = new List<string>();

        CapturePresenter.Render("PostToolUse", notLoggedIn, TextWriter.Null, log.Add);
        CapturePresenter.Render("PostToolUse", unknown, TextWriter.Null, log.Add);

        Assert.Contains("not signed in", Assert.Single(log));
    }

    private sealed class FakePush(SkillPushRunResult? result) : ISkillPushService
    {
        public List<SkillPushRequest> Requests { get; } = [];
        public Exception? Throw { get; init; }

        public Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(result!);
        }
    }
}

/// <summary>Tests for the file-backed <see cref="SkillLedger"/>, redirected to a temp config dir.</summary>
[Collection("ProtostarConfigDir")]
public sealed class SkillLedgerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pstar-ledger-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("PROTOSTAR_CONFIG_DIR");

    public SkillLedgerTests() => Environment.SetEnvironmentVariable("PROTOSTAR_CONFIG_DIR", _dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PROTOSTAR_CONFIG_DIR", _previous);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Records_survive_a_new_instance_and_are_scoped_by_registry()
    {
        var a = new Uri("https://a.test");
        var b = new Uri("https://b.test");
        new SkillLedger().Record(a, "/skills/alpha", new SkillLedgerEntry("abc", 2));

        var ledger = new SkillLedger();

        Assert.Equal(new SkillLedgerEntry("abc", 2), ledger.Get(a, "/skills/alpha/"));
        Assert.Null(ledger.Get(b, "/skills/alpha"));
        Assert.Null(ledger.Get(a, "/skills/beta"));
    }

    [Fact]
    public void A_corrupt_ledger_reads_as_empty_and_is_overwritten()
    {
        File.WriteAllText(Path.Combine(_dir, "skill-ledger.json"), "{not json");
        var ledger = new SkillLedger();
        var registry = new Uri("https://a.test");

        Assert.Null(ledger.Get(registry, "/skills/alpha"));
        ledger.Record(registry, "/skills/alpha", new SkillLedgerEntry("abc", 1));
        Assert.NotNull(ledger.Get(registry, "/skills/alpha"));
    }
}
