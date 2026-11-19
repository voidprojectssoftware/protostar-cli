using System.Security.Cryptography;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Registry;
using Protostar.Cli.Skills;
using Xunit;
using static Protostar.Cli.Unit.SkillPushServiceTests;

namespace Protostar.Cli.Unit;

/// <summary>
/// Contract tests for <see cref="ISkillPullService"/>: the real Claude Code harness over a temporary config
/// root, with in-memory fakes for the session, the registry, and the ledger.
/// </summary>
public sealed class SkillPullServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private static readonly Uri RegistryUri = new(Registry);

    private readonly string _home = Directory.CreateTempSubdirectory("pstar-pull-").FullName;
    private readonly FakeLedger _ledger = new();
    private readonly FakeRegistryApi _api = new();

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string SkillsDir => Path.Combine(_home, "skills");

    private ISkillPullService Build(StoredToken? session = null)
    {
        var harness = new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());
        var catalog = new HarnessCatalog([harness]);
        return new SkillPullService(catalog, new Tokens(session ?? Session()), _api, _ledger);
    }

    private static StoredToken Session(bool expired = false) => new()
    {
        Registry = Registry,
        AccessToken = "token-1",
        ExpiresAtUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddHours(1),
    };

    private SkillPullRequest Pull(params string[] names) => new(names, null, _home, Registry);

    private SkillRemoteRequest Remote() => new(null, _home, Registry);

    private static string Manifest(string name, string body = "body") => $"---\nname: {name}\ndescription: d\n---\n{body}";

    // Puts a skill version in the fake registry and returns its files.
    private RegistrySkillContent Publish(string name, int version, params (string Path, string Text)[] files)
    {
        var all = files.Length == 0 ? [("SKILL.md", Manifest(name))] : files;
        var registryFiles = all
            .Select(f => new RegistrySkillFile(f.Path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Text))), Encoding.UTF8.GetBytes(f.Text)))
            .ToList();
        var hash = SkillFileReader.RegistryHash(registryFiles.Select(f => new SkillUploadFile(f.RelativePath, f.Content)));
        var content = new RegistrySkillContent(name, version, hash, registryFiles);

        _api.Skills.RemoveAll(s => s.Name == name);
        _api.Skills.Add(new RegistrySkill(name, "d", version, hash, DateTimeOffset.UtcNow));
        _api.Contents[name] = content;
        return content;
    }

    private string Local(string dir, params (string Path, string Text)[] files)
    {
        var root = Path.Combine(SkillsDir, dir);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return root;
    }

    private void RecordSynced(string path, int version) =>
        _ledger.Record(RegistryUri, path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(path)), version));

    [Fact]
    public async Task Pull_installs_a_registry_skill_that_is_not_here()
    {
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha")), ("scripts/run.sh", "#!/bin/sh\necho hi"));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(SkillPullStatus.Installed, entry.Status);
        Assert.Equal(2, entry.Version);
        Assert.Equal(Manifest("alpha"), File.ReadAllText(Path.Combine(SkillsDir, "alpha", "SKILL.md")));
        Assert.Equal("#!/bin/sh\necho hi", File.ReadAllText(Path.Combine(SkillsDir, "alpha", "scripts", "run.sh")));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Pull_records_the_installed_content_so_a_later_push_sees_it_unchanged()
    {
        Publish("alpha", 3);

        await Build().PullAsync(Pull(), CancellationToken.None);

        var path = Path.Combine(SkillsDir, "alpha");
        var recorded = _ledger.Get(RegistryUri, Path.GetFullPath(path));
        Assert.NotNull(recorded);
        Assert.Equal(SkillFileReader.Hash(SkillFileReader.Read(path)), recorded.ContentHash);
        Assert.Equal(3, recorded.Version);
    }

    [Fact]
    public async Task Pull_leaves_an_identical_skill_alone()
    {
        Publish("alpha", 1);
        var path = Local("alpha", ("SKILL.md", Manifest("alpha")));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.UpToDate, Assert.Single(result.Entries).Status);
        Assert.Empty(_api.Downloaded);
        Assert.NotNull(_ledger.Get(RegistryUri, path));
    }

    [Fact]
    public async Task Pull_updates_an_unedited_older_copy_and_removes_files_the_new_version_dropped()
    {
        var path = Local("alpha", ("SKILL.md", Manifest("alpha", "old")), ("old.md", "gone"));
        RecordSynced(path, 1);
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha", "new")));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.Updated, Assert.Single(result.Entries).Status);
        Assert.Equal(Manifest("alpha", "new"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(path, "old.md")));
    }

    [Fact]
    public async Task Pull_keeps_a_skill_edited_here_unless_forced()
    {
        var path = Local("alpha", ("SKILL.md", Manifest("alpha", "old")));
        RecordSynced(path, 1);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), Manifest("alpha", "my edit"));
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha", "new")));

        var kept = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.LocalChanges, Assert.Single(kept.Entries).Status);
        Assert.Equal(Manifest("alpha", "my edit"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
        Assert.True(kept.Succeeded);

        var forced = await Build().PullAsync(Pull() with { Force = true }, CancellationToken.None);

        var entry = Assert.Single(forced.Entries);
        Assert.Equal(SkillPullStatus.Updated, entry.Status);
        Assert.NotNull(entry.Message);
        Assert.Equal(Manifest("alpha", "new"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
    }

    [Fact]
    public async Task A_differing_skill_with_no_sync_record_counts_as_local_changes()
    {
        Local("alpha", ("SKILL.md", Manifest("alpha", "mine")));
        Publish("alpha", 1);

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.LocalChanges, Assert.Single(result.Entries).Status);
    }

    [Fact]
    public async Task Pull_keeps_version_control_folders_inside_an_updated_skill()
    {
        var path = Local("alpha", ("SKILL.md", Manifest("alpha", "old")), (".git/HEAD", "ref"));
        RecordSynced(path, 1);
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha", "new")));

        await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal("ref", File.ReadAllText(Path.Combine(path, ".git", "HEAD")));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b.md")]
    [InlineData("C:/x.md")]
    [InlineData("a\\..\\b.md")]
    public async Task Pull_refuses_a_download_with_an_unsafe_path_and_writes_nothing(string unsafePath)
    {
        Publish("alpha", 1, ("SKILL.md", Manifest("alpha")), (unsafePath, "evil"));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.Failed, Assert.Single(result.Entries).Status);
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "alpha")));
        Assert.False(File.Exists(Path.Combine(_home, "escape.md")));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Pull_refuses_a_download_whose_bytes_do_not_match_their_checksum()
    {
        var content = Publish("alpha", 1);
        _api.Contents["alpha"] = content with
        {
            Files = [content.Files[0] with { Content = Encoding.UTF8.GetBytes("tampered") }],
        };

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.Failed, Assert.Single(result.Entries).Status);
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "alpha")));
    }

    [Fact]
    public async Task Pull_refuses_to_write_through_a_symbolic_link_in_the_skill()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_home, "outside")).FullName;
        var path = Local("alpha", ("SKILL.md", Manifest("alpha", "old")));
        Directory.CreateSymbolicLink(Path.Combine(path, "refs"), outside);
        RecordSynced(path, 1);
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha", "new")), ("refs/x.md", "x"));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.Failed, Assert.Single(result.Entries).Status);
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task Pull_of_a_name_not_in_the_registry_fails_before_writing()
    {
        Publish("alpha", 1);

        var result = await Build().PullAsync(Pull("nope"), CancellationToken.None);

        Assert.Equal(SkillPullRunFailure.SkillNotFound, result.Failure);
        Assert.Equal("nope", result.Detail);
        Assert.False(Directory.Exists(SkillsDir));
    }

    [Fact]
    public async Task Pull_by_name_matches_case_insensitively_and_pulls_only_that_skill()
    {
        Publish("alpha", 1);
        Publish("beta", 1);

        var result = await Build().PullAsync(Pull("ALPHA"), CancellationToken.None);

        Assert.Equal("alpha", Assert.Single(result.Entries).Name);
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "beta")));
    }

    [Fact]
    public async Task Pull_without_a_session_fails_before_contacting_the_registry_list()
    {
        var sut = new SkillPullService(
            new HarnessCatalog([new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())]),
            new Tokens(null), _api, _ledger);

        var result = await sut.PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullRunFailure.NotLoggedIn, result.Failure);
    }

    [Fact]
    public async Task Pull_with_an_expired_session_fails()
    {
        var result = await Build(Session(expired: true)).PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullRunFailure.SessionExpired, result.Failure);
    }

    [Fact]
    public async Task A_registry_list_error_is_reported()
    {
        var api = new FakeRegistryApi { ListError = "boom" };
        var sut = new SkillPullService(
            new HarnessCatalog([new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())]),
            new Tokens(Session()), api, _ledger);

        var result = await sut.CompareAsync(Remote(), CancellationToken.None);

        Assert.Equal(SkillPullRunFailure.ListFailed, result.Failure);
        Assert.Equal("boom", result.Detail);
    }

    [Fact]
    public async Task Compare_reports_every_state()
    {
        Publish("current", 1);
        Local("current", ("SKILL.md", Manifest("current")));

        Publish("missing", 1);

        var stale = Local("stale", ("SKILL.md", Manifest("stale", "old")));
        RecordSynced(stale, 1);
        Publish("stale", 2, ("SKILL.md", Manifest("stale", "new")));

        Local("edited", ("SKILL.md", Manifest("edited", "mine")));
        Publish("edited", 1);

        Local("mine-only", ("SKILL.md", Manifest("mine-only")));

        var result = await Build().CompareAsync(Remote(), CancellationToken.None);

        var states = result.Skills.ToDictionary(s => s.Name, s => s.State);
        Assert.Equal(SkillSyncState.UpToDate, states["current"]);
        Assert.Equal(SkillSyncState.NotInstalled, states["missing"]);
        Assert.Equal(SkillSyncState.UpdateAvailable, states["stale"]);
        Assert.Equal(SkillSyncState.LocalChanges, states["edited"]);
        Assert.Equal(SkillSyncState.LocalOnly, states["mine-only"]);
        Assert.Equal(1, result.Skills.Single(s => s.Name == "stale").LocalVersion);
    }

    [Fact]
    public async Task Compare_writes_nothing()
    {
        Publish("alpha", 1);

        await Build().CompareAsync(Remote(), CancellationToken.None);

        Assert.False(Directory.Exists(SkillsDir));
        Assert.Empty(_api.Downloaded);
    }

    // ----- Item 1: never write into a folder that is not the matched skill -----

    [Fact]
    public async Task Pull_never_writes_into_a_folder_that_holds_a_skill_with_another_name_even_with_force()
    {
        var path = Local("foo", ("SKILL.md", Manifest("bar", "bar's work")), ("notes.md", "unpushed"));
        Publish("foo", 1, ("SKILL.md", Manifest("foo", "registry")));

        var result = await Build().PullAsync(Pull() with { Force = true }, CancellationToken.None);

        var entry = Assert.Single(result.Entries, e => e.Name == "foo");
        Assert.Equal(SkillPullStatus.Conflict, entry.Status);
        Assert.Equal(path, entry.Path);
        Assert.Equal(Manifest("bar", "bar's work"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
        Assert.Equal("unpushed", File.ReadAllText(Path.Combine(path, "notes.md")));
        Assert.Empty(_api.Downloaded);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Pull_never_writes_into_a_work_in_progress_folder_with_no_skill_md()
    {
        var path = Local("foo", ("draft.md", "half done"));
        Publish("foo", 1);

        var result = await Build().PullAsync(Pull("foo") with { Force = true }, CancellationToken.None);

        Assert.Equal(SkillPullStatus.Conflict, Assert.Single(result.Entries).Status);
        Assert.Equal(["draft.md"], Directory.GetFiles(path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Pull_treats_a_folder_differing_only_by_case_as_taken()
    {
        var path = Local("FOO", ("draft.md", "half done"));
        Publish("foo", 1);

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.Conflict, Assert.Single(result.Entries).Status);
        Assert.Equal(["FOO"], Directory.GetFileSystemEntries(SkillsDir).Select(Path.GetFileName));
        Assert.Equal(["draft.md"], Directory.GetFiles(path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Compare_shows_a_taken_folder_as_a_conflict_with_the_reason()
    {
        var path = Local("foo", ("SKILL.md", Manifest("bar")));
        Publish("foo", 1);

        var result = await Build().CompareAsync(Remote(), CancellationToken.None);

        var foo = result.Skills.Single(s => s.Name == "foo");
        Assert.Equal(SkillSyncState.Conflict, foo.State);
        Assert.Equal(path, foo.LocalPath);
        Assert.Contains("already exists", foo.Note);
        Assert.Equal(SkillSyncState.LocalOnly, result.Skills.Single(s => s.Name == "bar").State);
    }

    // ----- Item 2: registry names that differ only by case -----

    [Fact]
    public async Task Pull_of_registry_names_differing_only_by_case_does_not_crash_and_pulls_neither()
    {
        Publish("foo", 1, ("SKILL.md", Manifest("foo", "lower")));
        Publish("Foo", 1, ("SKILL.md", Manifest("Foo", "upper")));

        var result = await Build().PullAsync(Pull() with { Force = true }, CancellationToken.None);

        Assert.Equal(SkillPullRunFailure.None, result.Failure);
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(SkillPullStatus.Conflict, e.Status));
        Assert.False(Directory.Exists(SkillsDir));
        Assert.Empty(_api.Downloaded);
    }

    [Fact]
    public async Task Pull_by_exact_name_still_refuses_when_a_case_twin_exists()
    {
        var path = Local("foo", ("SKILL.md", Manifest("foo", "old")));
        RecordSynced(path, 1);
        Publish("foo", 2, ("SKILL.md", Manifest("foo", "lower")));
        Publish("Foo", 5, ("SKILL.md", Manifest("Foo", "upper")));

        var result = await Build().PullAsync(Pull("foo"), CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(SkillPullStatus.Conflict, entry.Status);
        Assert.Equal(Manifest("foo", "old"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
    }

    [Fact]
    public async Task Compare_reports_both_case_twins_as_conflicts_and_not_the_local_copy_as_only_here()
    {
        Local("foo", ("SKILL.md", Manifest("foo")));
        Publish("foo", 1);
        Publish("Foo", 1, ("SKILL.md", Manifest("Foo")));

        var result = await Build().CompareAsync(Remote(), CancellationToken.None);

        Assert.Equal(2, result.Skills.Count);
        Assert.All(result.Skills, s => Assert.Equal(SkillSyncState.Conflict, s.State));
    }

    [Fact]
    public async Task A_sync_record_for_another_registry_name_does_not_make_a_folder_look_unedited()
    {
        // The folder was synced as 'foo'; the registry now only has 'Foo'. Without the name in the record,
        // the matching hash would read as "unedited, update available" and Foo's content would replace it.
        var path = Local("foo", ("SKILL.md", Manifest("foo", "mine")));
        _ledger.Record(RegistryUri, path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(path)), 1, "foo"));
        Publish("Foo", 3, ("SKILL.md", Manifest("Foo", "theirs")));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.LocalChanges, Assert.Single(result.Entries).Status);
        Assert.Equal(Manifest("foo", "mine"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
    }

    [Fact]
    public async Task Pull_records_the_registry_name_in_the_ledger()
    {
        Publish("alpha", 1);

        await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal("alpha", _ledger.Get(RegistryUri, Path.GetFullPath(Path.Combine(SkillsDir, "alpha")))!.Name);
    }

    // ----- Item 3: paths under folders the reader ignores -----

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("node_modules/x/index.js")]
    [InlineData("scripts/__pycache__/a.pyc")]
    [InlineData(".GIT/config")]
    public async Task Pull_refuses_a_download_with_a_path_inside_an_ignored_folder(string ignored)
    {
        Publish("alpha", 1, ("SKILL.md", Manifest("alpha")), (ignored, "#!/bin/sh\nevil"));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(SkillPullStatus.Failed, entry.Status);
        Assert.Contains("never reads", entry.Message);
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "alpha")));
    }

    // ----- Item 4: normalisation -----

    [Theory]
    [InlineData("notes.md ")]
    [InlineData(" lead/x.md")]
    [InlineData("dir /x.md")]
    public async Task Pull_refuses_a_download_with_a_padded_path_segment(string padded)
    {
        Publish("alpha", 1, ("SKILL.md", Manifest("alpha")), (padded, "x"));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(SkillPullStatus.Failed, Assert.Single(result.Entries).Status);
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "alpha")));
    }

    [Fact]
    public async Task Pull_on_a_case_sensitive_file_system_prunes_a_file_differing_only_by_case()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return; // Needs a case-sensitive file system, where a.md and A.md are two real files.

        var path = Local("alpha", ("SKILL.md", Manifest("alpha", "old")), ("A.md", "upper"), ("a.md", "lower"));
        RecordSynced(path, 1);
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha", "new")), ("a.md", "new lower"));

        await Build().PullAsync(Pull(), CancellationToken.None);

        Assert.Equal(["SKILL.md", "a.md"], SkillFileReader.Read(path).Select(f => f.RelativePath));
        Assert.Equal("new lower", File.ReadAllText(Path.Combine(path, "a.md")));
    }

    [Fact]
    public async Task Pull_refuses_to_write_when_the_skill_folder_itself_is_a_symbolic_link_and_says_so()
    {
        if (OperatingSystem.IsWindows())
            return;

        var real = Directory.CreateDirectory(Path.Combine(_home, "real-alpha")).FullName;
        File.WriteAllText(Path.Combine(real, "SKILL.md"), Manifest("alpha", "old"));
        Directory.CreateDirectory(SkillsDir);
        var link = Path.Combine(SkillsDir, "alpha");
        Directory.CreateSymbolicLink(link, real);
        RecordSynced(link, 1);
        Publish("alpha", 2, ("SKILL.md", Manifest("alpha", "new")));

        var result = await Build().PullAsync(Pull(), CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(SkillPullStatus.Failed, entry.Status);
        Assert.Contains("is a symbolic link (to", entry.Message);
        Assert.Equal(Manifest("alpha", "old"), File.ReadAllText(Path.Combine(real, "SKILL.md")));
    }

    private sealed class Tokens(StoredToken? session) : IAccessTokenProvider
    {
        public Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session);
    }
}

/// <summary>Tests for <see cref="SkillFileReader.RegistryHash"/>, which must agree with the registry's digest.</summary>
public sealed class RegistryHashTests
{
    [Fact]
    public void Matches_the_registry_digest_for_a_known_skill()
    {
        // The registry hashes "<path>\n<UPPER hex sha256>\n" per file, in ordinal path order.
        var a = Encoding.UTF8.GetBytes("hello");
        var b = Encoding.UTF8.GetBytes("world");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SKILL.md\n{Convert.ToHexString(SHA256.HashData(a))}\nscripts/x.sh\n{Convert.ToHexString(SHA256.HashData(b))}\n")));

        var actual = SkillFileReader.RegistryHash([new SkillUploadFile("scripts/x.sh", b), new SkillUploadFile("SKILL.md", a)]);

        Assert.Equal(expected, actual);
    }
}

/// <summary>Tests for <see cref="SkillInstaller.FindCaseOnlyEntry"/>, the case-only rename decision.</summary>
public sealed class SkillInstallerCaseTests
{
    [Fact]
    public void Renames_a_folder_the_file_system_resolved_only_by_ignoring_case()
    {
        // Case-insensitive file system: "docs" resolves, but the entry on disk is still spelled "Docs".
        Assert.Equal("Docs", SkillInstaller.FindCaseOnlyEntry(["Docs", "SKILL.md"], "docs", wantedResolves: true));
    }

    [Fact]
    public void Leaves_a_differently_cased_entry_alone_on_a_case_sensitive_file_system()
    {
        // Case-sensitive: "docs" does not resolve, so "Docs" is a separate folder, not an old spelling.
        Assert.Null(SkillInstaller.FindCaseOnlyEntry(["Docs", "SKILL.md"], "docs", wantedResolves: false));
    }

    [Fact]
    public void Does_nothing_when_the_exact_spelling_is_already_there()
    {
        Assert.Null(SkillInstaller.FindCaseOnlyEntry(["Docs", "docs"], "docs", wantedResolves: true));
    }
}
