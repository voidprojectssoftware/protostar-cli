using System.Security.Cryptography;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Memory;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="IMemorySyncService"/>, over a fixture Claude Code home and an in-memory registry that versions by content.</summary>
public sealed class MemorySyncTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-memory-").FullName;
    private readonly ClaudeCodeHarness _harness = new(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());
    private readonly FakeRegistry _registry = new();
    private readonly FakeLedger _ledger = new();
    private readonly FakeProjects _projects = new();
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IMemorySyncService Sut() => new MemorySyncService(
        new FakeCatalog(_harness), new FakeTokens(() => SessionLookups.Of(_token)), _registry, _registry, _registry, _ledger, new FakeMachine(), _projects, _registry);

    private Task<MemoryPushResult> Push(string? sessionPath = null) =>
        Sut().PushAsync(new MemoryPushRequest(Registry, _root, sessionPath), CancellationToken.None);

    private string Folder(string directory) => Path.Combine(_root, "projects", _harness.ProjectFolderName(directory));

    // A project folder for `directory`, holding a session transcript that records `cwd` (none when null). The line is
    // serialised, as Claude Code writes it, so a Windows path's backslashes are escaped and the transcript stays JSON.
    private string Project(string directory, string? cwd)
    {
        var folder = Folder(directory);
        Directory.CreateDirectory(folder);
        if (cwd is not null)
            File.WriteAllText(Path.Combine(folder, Guid.NewGuid() + ".jsonl"), System.Text.Json.JsonSerializer.Serialize(new { type = "user", cwd }) + "\n");
        return folder;
    }

    private static void Memory(string folder, string path, string text)
    {
        var file = Path.Combine(folder, "memory", path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private string Transcript(string folder) => Directory.GetFiles(folder, "*.jsonl").Single();

    [Fact]
    public async Task First_push_uploads_every_memory_file_with_the_facts_of_the_folder_a_transcript_records()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        Memory(alpha, "notes/port.md", "port 1234");
        var beta = Project("/src/beta", "/src/beta");
        Memory(beta, "MEMORY.md", "beta");

        var result = await Push();

        Assert.True(result.Complete);
        Assert.Equal(3, result.Folders.Sum(f => f.Uploaded));
        Assert.Equal(["/src/alpha", "/src/beta"], _registry.Pushes.Select(p => p.Facts.WorkingDirectory).Order());
        Assert.Equal(["MEMORY.md", "notes/port.md"], _registry.Pushes.Single(p => p.Facts.WorkingDirectory == "/src/alpha").Files.Order());
        Assert.Equal("test", result.Machine);
    }

    [Fact]
    public async Task A_second_push_with_nothing_changed_makes_no_network_call()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        await Push();
        _registry.Calls = 0;
        _token = null;

        var result = await Push();

        Assert.True(result.Complete);
        Assert.Equal(0, _registry.Calls);
        Assert.Equal(1, Assert.Single(result.Folders).UpToDate);
    }

    [Fact]
    public async Task Only_a_changed_file_is_uploaded_and_becomes_a_new_version()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        Memory(alpha, "port.md", "port 1234");
        await Push();
        _registry.Pushes.Clear();

        Memory(alpha, "port.md", "port 5678 now");
        var result = await Push();

        Assert.Equal(["port.md"], Assert.Single(_registry.Pushes).Files);
        Assert.Equal(1, Assert.Single(result.Folders).Uploaded);
        Assert.Equal(2, _registry.Versions("/src/alpha", "port.md"));
    }

    [Fact]
    public async Task A_file_touched_without_changing_is_not_uploaded()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        await Push();
        _registry.Pushes.Clear();

        File.SetLastWriteTimeUtc(Path.Combine(alpha, "memory", "MEMORY.md"), DateTime.UtcNow.AddMinutes(5));
        var result = await Push();

        Assert.Empty(_registry.Pushes);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_deleted_file_is_sent_as_a_deletion_once()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        Memory(alpha, "old.md", "stale");
        await Push();
        _registry.Pushes.Clear();

        File.Delete(Path.Combine(alpha, "memory", "old.md"));
        var first = await Push();
        var second = await Push();

        Assert.Equal(["old.md"], Assert.Single(_registry.Pushes).Deleted);
        Assert.Equal(1, Assert.Single(first.Folders).Deleted);
        Assert.Equal(0, Assert.Single(second.Folders).Deleted);
        Assert.True(_registry.IsDeleted("/src/alpha", "old.md"));
    }

    [Fact]
    public async Task A_memory_folder_without_a_transcript_is_skipped_by_name_and_nothing_is_guessed()
    {
        var orphan = Project("/src/orphan", cwd: null);
        Memory(orphan, "MEMORY.md", "orphan");

        var result = await Push();

        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(Path.GetFileName(orphan), skipped.Folder);
        Assert.Empty(_registry.Pushes);
        Assert.Empty(_projects.Asked);
    }

    [Fact]
    public async Task Two_memory_folders_of_one_checkout_are_uploaded_once_from_the_folder_named_after_it()
    {
        _projects.Worktree("/src/beta-wt", main: "/src/beta");
        _projects.Worktree("/src/beta", main: "/src/beta");
        var main = Project("/src/beta", "/src/beta");
        Memory(main, "MEMORY.md", "beta");
        var worktree = Project("/src/beta-wt", "/src/beta-wt");
        Memory(worktree, "stray.md", "stray");

        var result = await Push();

        Assert.Equal(["MEMORY.md"], Assert.Single(_registry.Pushes).Files);
        Assert.Equal(Path.GetFileName(worktree), Assert.Single(result.Skipped).Folder);
    }

    [Fact]
    public async Task A_memory_folder_with_no_transcript_of_its_own_uses_a_worktree_transcript_whose_checkout_it_is_named_after()
    {
        _projects.Worktree("/src/beta-wt", main: "/src/beta");
        var main = Project("/src/beta", cwd: null);
        Memory(main, "MEMORY.md", "beta");
        Project("/src/beta-wt", "/src/beta-wt");

        var result = await Push();

        Assert.Empty(result.Skipped);
        Assert.Equal("/src/beta-wt", Assert.Single(_registry.Pushes).Facts.WorkingDirectory);
        Assert.Equal("/src/beta", Assert.Single(result.Folders).Directory);
    }

    [Fact]
    public async Task A_folder_whose_name_merely_extends_the_memory_folders_does_not_lend_its_project()
    {
        var main = Project("/src/beta", cwd: null);
        Memory(main, "MEMORY.md", "beta");
        Project("/src/beta-other", "/src/beta-other");

        var result = await Push();

        Assert.Single(result.Skipped);
        Assert.Empty(_registry.Pushes);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_reported_and_never_uploaded()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        Memory(alpha, "huge.md", new string('x', MemorySyncService.MaxFileBytes + 1));

        var result = await Push();

        Assert.Equal(["MEMORY.md"], Assert.Single(_registry.Pushes).Files);
        var oversized = Assert.Single(Assert.Single(result.Folders).Oversized);
        Assert.Equal("huge.md", oversized.Path);
    }

    [Fact]
    public async Task Status_reports_new_changed_and_deleted_files_without_the_registry()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        Memory(alpha, "port.md", "1234");
        Memory(alpha, "old.md", "stale");
        await Push();
        _registry.Calls = 0;
        _token = null;

        Memory(alpha, "port.md", "5678");
        Memory(alpha, "new.md", "fresh");
        File.Delete(Path.Combine(alpha, "memory", "old.md"));
        var status = Sut().Status(Registry, _root);

        var folder = Assert.Single(status.Folders);
        Assert.Equal(["new.md"], folder.New);
        Assert.Equal(["port.md"], folder.Changed);
        Assert.Equal(["old.md"], folder.Deleted);
        Assert.Equal(1, folder.UpToDate);
        Assert.False(status.FullySynced);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task A_hooks_push_backs_up_the_memory_of_its_sessions_checkout_even_from_a_worktree()
    {
        _projects.Worktree("/src/beta-wt", main: "/src/beta");
        var main = Project("/src/beta", "/src/beta");
        Memory(main, "MEMORY.md", "beta");
        var worktree = Project("/src/beta-wt", "/src/beta-wt");
        var other = Project("/src/alpha", "/src/alpha");
        Memory(other, "MEMORY.md", "alpha");

        var result = await Push(sessionPath: Transcript(worktree));

        Assert.True(result.Complete);
        var push = Assert.Single(_registry.Pushes);
        Assert.Equal("/src/beta-wt", push.Facts.WorkingDirectory);
        Assert.Equal(["MEMORY.md"], push.Files);
    }

    [Fact]
    public async Task A_push_with_changes_while_signed_out_fails_and_records_nothing()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        _token = null;

        var result = await Push();

        Assert.Equal(SessionSyncFailure.NotLoggedIn, result.Failure);
        Assert.Equal(0, Assert.Single(Sut().Status(Registry, _root).Folders).UpToDate);
    }

    [Fact]
    public async Task A_registry_that_forgot_the_machine_gets_it_registered_again_and_the_push_retried()
    {
        var alpha = Project("/src/alpha", "/src/alpha");
        Memory(alpha, "MEMORY.md", "index");
        _registry.ForgetMachineOnce = true;

        var result = await Push();

        Assert.True(result.Complete);
        Assert.Equal(1, Assert.Single(result.Folders).Uploaded);
        Assert.Equal(2, _registry.MachineRegistrations);
    }

    // ── pull ───────────────────────────────────────────────────────────────────────

    private Task<MemoryPullResult> Pull(params string[] scan) =>
        Sut().PullAsync(new MemoryPullRequest(Registry, _root, scan), CancellationToken.None);

    // A real folder for a checkout, since a pull only resolves folders that exist.
    private string Checkout(string name)
    {
        var directory = Path.Combine(_root, "checkouts", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string MemoryFile(string directory, string path) => Path.Combine(Folder(directory), "memory", path);

    [Fact]
    public async Task Pull_writes_a_projects_memory_into_the_folder_encoded_from_the_local_checkout_a_transcript_records()
    {
        var here = Checkout("protostar");
        Project(here, here);
        var project = _registry.RemoteProject("protostar", here, ("MEMORY.md", "index"), ("notes/port.md", "port 1234"));

        var result = await Pull();

        Assert.True(result.Complete);
        Assert.Equal(2, result.Written);
        Assert.Equal("index", File.ReadAllText(MemoryFile(here, "MEMORY.md")));
        Assert.Equal("port 1234", File.ReadAllText(MemoryFile(here, "notes/port.md")));
        Assert.Equal(project, _registry.ContentAsked.Single());
    }

    [Fact]
    public async Task A_push_after_a_pull_sends_nothing_back()
    {
        var here = Checkout("protostar");
        Project(here, here);
        _registry.RemoteProject("protostar", here, ("MEMORY.md", "index"));
        await Pull();
        _registry.Calls = 0;

        var push = await Push();

        Assert.Empty(_registry.Pushes);
        Assert.Equal(0, _registry.Calls);
        Assert.Equal(1, Assert.Single(push.Folders).UpToDate);
    }

    [Fact]
    public async Task A_project_with_no_checkout_here_is_reported_and_nothing_is_written_for_it()
    {
        var here = Checkout("alpha");
        Project(here, here);
        _registry.RemoteProject("alpha", here, ("MEMORY.md", "alpha"));
        _registry.RemoteProject("beta", "/elsewhere/beta", ("MEMORY.md", "beta"));

        var result = await Pull();

        Assert.Equal("beta", Assert.Single(result.NotHere).ProjectName);
        Assert.Equal("alpha", Assert.Single(result.Folders).ProjectName);
        Assert.False(Directory.Exists(Folder("/elsewhere/beta")));
    }

    [Fact]
    public async Task A_file_unchanged_here_since_the_last_sync_is_fast_forwarded_to_another_machines_newer_version()
    {
        var here = Checkout("alpha");
        Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "port 1234"));
        await Pull();
        _registry.Change(project, "port.md", "port 5678", "laptop");

        var result = await Pull();
        await Push();

        Assert.Equal("port 5678", File.ReadAllText(MemoryFile(here, "port.md")));
        Assert.Equal(1, Assert.Single(result.Folders).Updated);
        Assert.Empty(Assert.Single(result.Folders).Notes);
        Assert.Empty(_registry.Pushes);
    }

    [Fact]
    public async Task A_version_made_in_parallel_without_this_machines_base_conflicts_even_with_the_file_unchanged_here()
    {
        var here = Checkout("alpha");
        Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "mine, pushed"));
        await Pull();
        _registry.Change(project, "port.md", "the laptop's, never having seen mine", "laptop", parallel: true);

        var result = await Pull();

        Assert.Equal("mine, pushed", File.ReadAllText(MemoryFile(here, "port.md")));
        Assert.Equal("the laptop's, never having seen mine", File.ReadAllText(MemoryFile(here, "port.from-laptop.md")));
        Assert.Equal(MemoryPullNoteKind.Conflicted, Assert.Single(Assert.Single(result.Folders).Notes).Kind);
    }

    [Fact]
    public async Task An_index_made_in_parallel_is_merged_not_fast_forwarded_so_no_line_is_lost()
    {
        var here = Checkout("alpha");
        Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [Deploy](deploy.md): deploy\n"));
        await Pull();
        _registry.Change(project, "MEMORY.md", "- [Tests](tests.md): tests\n", "desktop", parallel: true);

        await Pull();

        Assert.Equal("- [Deploy](deploy.md): deploy\n- [Tests](tests.md): tests\n", File.ReadAllText(MemoryFile(here, "MEMORY.md")));
    }

    [Fact]
    public async Task An_older_version_another_copy_still_holds_is_never_taken()
    {
        var here = Checkout("alpha");
        Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        _registry.Change(project, "port.md", "v2", "laptop");
        await Pull();
        _registry.Revert(project, "port.md", "v1", 1);

        var result = await Pull();

        Assert.Equal("v2", File.ReadAllText(MemoryFile(here, "port.md")));
        Assert.Empty(Assert.Single(result.Folders).Notes);
    }

    [Fact]
    public async Task A_push_sends_the_version_each_file_was_edited_from()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        await Pull();
        Memory(folder, "port.md", "v2");
        Memory(folder, "new.md", "new");

        await Push();

        Assert.Equal(1, _registry.SentBase["port.md"]);
        Assert.Null(_registry.SentBase["new.md"]);
    }

    [Fact]
    public async Task A_file_changed_only_here_is_left_for_the_next_push_and_not_reported()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        _registry.RemoteProject("alpha", here, ("port.md", "port 1234"));
        await Pull();
        Memory(folder, "port.md", "port 9999, mine");

        var result = await Pull();
        await Push();

        Assert.Equal("port 9999, mine", File.ReadAllText(MemoryFile(here, "port.md")));
        Assert.Empty(Assert.Single(result.Folders).Notes);
        Assert.Equal(["port.md"], Assert.Single(_registry.Pushes).Files);
    }

    [Fact]
    public async Task A_file_changed_here_and_on_another_machine_keeps_both_with_the_other_beside_it_and_a_pointer_in_the_index()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [Port](notes/port.md): the port\n"), ("notes/port.md", "port 1234"));
        await Pull();
        Memory(folder, "notes/port.md", "port 1234, then mine");
        _registry.Change(project, "notes/port.md", "port 1234, then the laptop's", "Blake's Laptop");

        var result = await Pull();

        Assert.Equal("port 1234, then mine", File.ReadAllText(MemoryFile(here, "notes/port.md")));
        Assert.Equal("port 1234, then the laptop's", File.ReadAllText(MemoryFile(here, "notes/port.from-Blake-s-Laptop.md")));
        var note = Assert.Single(Assert.Single(result.Folders).Notes);
        Assert.Equal(("notes/port.md", MemoryPullNoteKind.Conflicted, "notes/port.from-Blake-s-Laptop.md"), (note.Path, note.Kind, note.Detail));
        Assert.Equal(["Blake's Laptop"], note.Machines);
        var index = File.ReadAllLines(MemoryFile(here, "MEMORY.md"));
        Assert.StartsWith("- [Conflict: notes/port.md as Blake-s-Laptop changed it](notes/port.from-Blake-s-Laptop.md)", index[0]);
        Assert.Equal("- [Port](notes/port.md): the port", index[1]);
    }

    [Fact]
    public async Task Pulling_a_conflict_again_makes_no_second_copy_and_reports_nothing_new()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        await Pull();
        Memory(folder, "port.md", "mine");
        _registry.Change(project, "port.md", "theirs", "laptop");
        await Pull();
        var index = File.ReadAllText(MemoryFile(here, "MEMORY.md"));

        var again = await Pull();

        Assert.Empty(Assert.Single(again.Folders).Notes);
        Assert.Equal(["MEMORY.md", "port.from-laptop.md", "port.md"], Directory.GetFiles(Path.Combine(folder, "memory")).Select(Path.GetFileName).Order());
        Assert.Equal(index, File.ReadAllText(MemoryFile(here, "MEMORY.md")));
    }

    [Fact]
    public async Task A_conflict_copy_never_overwrites_a_different_file_of_that_name()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        await Pull();
        Memory(folder, "port.md", "mine");
        Memory(folder, "port.from-laptop.md", "an older conflict");
        _registry.Change(project, "port.md", "theirs", "laptop");

        var result = await Pull();

        Assert.Equal("an older conflict", File.ReadAllText(MemoryFile(here, "port.from-laptop.md")));
        Assert.Equal("theirs", File.ReadAllText(MemoryFile(here, "port.from-laptop-2.md")));
        Assert.Equal("port.from-laptop-2.md", Assert.Single(Assert.Single(result.Folders).Notes).Detail);
    }

    [Fact]
    public async Task The_next_push_sends_the_kept_file_but_never_the_conflict_copy_or_its_pointer()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [Port](port.md): the port\n"), ("port.md", "v1"));
        await Pull();
        Memory(folder, "port.md", "mine");
        _registry.Change(project, "port.md", "theirs", "laptop");
        await Pull();
        File.AppendAllText(MemoryFile(here, "MEMORY.md"), "- [Build](build.md): how to build\n");

        await Push();
        var status = Sut().Status(Registry, _root);

        Assert.Equal(["MEMORY.md", "port.md"], Assert.Single(_registry.Pushes).Files.Order());
        Assert.Equal("- [Port](port.md): the port\n- [Build](build.md): how to build\n", _registry.Sent["MEMORY.md"]);
        Assert.Equal(["port.from-laptop.md"], Assert.Single(status.Folders).Conflicts);
        Assert.Equal(0, Assert.Single(status.Folders).Pending);
        Assert.False(status.FullySynced);
    }

    [Fact]
    public async Task A_conflict_copy_the_user_deleted_loses_its_pointer_and_status_is_clear_again()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [Port](port.md): the port\n"), ("port.md", "v1"));
        await Pull();
        Memory(folder, "port.md", "mine");
        _registry.Change(project, "port.md", "theirs", "laptop");
        await Pull();
        await Push();

        File.Delete(MemoryFile(here, "port.from-laptop.md"));
        await Pull();

        Assert.Equal("- [Port](port.md): the port\n", File.ReadAllText(MemoryFile(here, "MEMORY.md")));
        Assert.True(Sut().Status(Registry, _root).FullySynced);
    }

    [Fact]
    public async Task The_index_changed_on_both_sides_is_merged_line_by_line_keeping_local_order_and_every_line()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [A](a.md): a\n- [B](b.md): b\n"));
        await Pull();
        Memory(folder, "MEMORY.md", "- [B](b.md): b\n- [A](a.md): a\n- [Mine](mine.md): mine\n");
        _registry.Change(project, "MEMORY.md", "- [A](a.md): a\n- [Theirs](theirs.md): theirs\n", "laptop");

        var result = await Pull();
        await Push();

        Assert.Equal(
            "- [B](b.md): b\n- [A](a.md): a\n- [Mine](mine.md): mine\n- [Theirs](theirs.md): theirs\n",
            File.ReadAllText(MemoryFile(here, "MEMORY.md")));
        var note = Assert.Single(Assert.Single(result.Folders).Notes);
        Assert.Equal((MemoryPullNoteKind.Merged, "1"), (note.Kind, note.Detail));
        Assert.Equal(["MEMORY.md"], Assert.Single(_registry.Pushes).Files);
    }

    [Fact]
    public async Task An_index_that_already_has_every_remote_line_is_merged_once_silently_and_not_again()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        Memory(folder, "MEMORY.md", "- [A](a.md): a\n- [B](b.md): b\n");
        _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [B](b.md): b\n"));

        var first = await Pull();
        var second = await Pull();

        Assert.Empty(Assert.Single(first.Folders).Notes);
        Assert.Empty(Assert.Single(second.Folders).Notes);
        Assert.Equal("- [A](a.md): a\n- [B](b.md): b\n", File.ReadAllText(MemoryFile(here, "MEMORY.md")));
    }

    [Fact]
    public async Task A_merged_index_longer_than_claude_code_loads_is_reported()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        Memory(folder, "MEMORY.md", string.Concat(Enumerable.Range(0, 150).Select(i => $"- [Mine {i}](mine{i}.md): mine\n")));
        _registry.RemoteProject("alpha", here, ("MEMORY.md", string.Concat(Enumerable.Range(0, 100).Select(i => $"- [Theirs {i}](theirs{i}.md): theirs\n"))));

        var result = await Pull();

        Assert.Equal(250, File.ReadAllLines(MemoryFile(here, "MEMORY.md")).Length);
        var note = Assert.Single(Assert.Single(result.Folders).Notes, n => n.Kind == MemoryPullNoteKind.IndexTooLong);
        Assert.Equal("250", note.Detail);
    }

    [Fact]
    public async Task An_index_here_that_was_never_synced_is_merged_with_the_registrys_not_replaced()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        Memory(folder, "MEMORY.md", "- [Mine](mine.md): mine\n");
        _registry.RemoteProject("alpha", here, ("MEMORY.md", "- [Theirs](theirs.md): theirs\n"), ("theirs.md", "1234"));

        var result = await Pull();

        Assert.Equal("- [Mine](mine.md): mine\n- [Theirs](theirs.md): theirs\n", File.ReadAllText(MemoryFile(here, "MEMORY.md")));
        Assert.Equal("1234", File.ReadAllText(MemoryFile(here, "theirs.md")));
        Assert.Equal(MemoryPullNoteKind.Merged, Assert.Single(Assert.Single(result.Folders).Notes).Kind);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_file_deleted_here_and_changed_on_another_machine_since_comes_back_and_says_so()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        await Pull();
        File.Delete(MemoryFile(here, "port.md"));
        await Push();
        _registry.Change(project, "port.md", "v2 from the laptop", "laptop");

        var result = await Pull();
        _registry.Pushes.Clear();
        await Push();

        Assert.Equal("v2 from the laptop", File.ReadAllText(MemoryFile(here, "port.md")));
        var note = Assert.Single(Assert.Single(result.Folders).Notes);
        Assert.Equal((MemoryPullNoteKind.Restored, "laptop"), (note.Kind, Assert.Single(note.Machines)));
        // The registry recorded it deleted on this machine; the push tells it the file is here again.
        Assert.Equal(["port.md"], Assert.Single(_registry.Pushes).Files);
    }

    [Fact]
    public async Task A_pulled_file_deleted_here_stays_deleted_after_a_push_the_registry_answers_unknown()
    {
        var here = Checkout("alpha");
        Project(here, here);
        _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        await Pull();
        File.Delete(MemoryFile(here, "port.md"));
        var push = await Push();

        var result = await Pull();

        Assert.Equal(1, Assert.Single(push.Folders).Deleted);
        Assert.False(File.Exists(MemoryFile(here, "port.md")));
        Assert.Equal(MemoryPullNoteKind.DeletedHere, Assert.Single(Assert.Single(result.Folders).Notes).Kind);
    }

    [Fact]
    public async Task This_checkouts_own_deletion_is_not_reported_as_a_deletion_elsewhere()
    {
        var here = Checkout("alpha");
        Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("port.md", "v1"));
        _registry.MarkDeleted(project, "port.md", "test", everywhere: false, machineId: FakeMachine.Id, folder: here);
        Memory(Folder(here), "port.md", "v1");

        var result = await Pull();

        Assert.Empty(Assert.Single(result.Folders).Notes);
    }

    [Fact]
    public async Task A_conflict_copy_is_never_sent_by_a_push()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        Memory(folder, "MEMORY.md", "index");
        Memory(folder, "port.from-laptop.md", "theirs");

        await Push();

        Assert.Equal(["MEMORY.md"], Assert.Single(_registry.Pushes).Files);
    }

    [Fact]
    public async Task A_file_already_here_with_the_registrys_content_is_counted_and_recorded()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        Memory(folder, "MEMORY.md", "same");
        _registry.RemoteProject("alpha", here, ("MEMORY.md", "same"));

        var result = await Pull();
        await Push();

        Assert.Equal(1, Assert.Single(result.Folders).UpToDate);
        Assert.Empty(_registry.Pushes);
    }

    [Fact]
    public async Task Deletions_are_reported_and_never_applied_here()
    {
        var here = Checkout("alpha");
        var folder = Project(here, here);
        Memory(folder, "kept.md", "still here");
        var project = _registry.RemoteProject("alpha", here, ("kept.md", "still here"), ("gone.md", "everywhere"), ("mine.md", "deleted here"));
        _registry.MarkDeleted(project, "kept.md", "laptop", everywhere: false);
        _registry.MarkDeleted(project, "gone.md", "laptop", everywhere: true);
        _ledger.Save(new Uri(Registry), Path.GetFullPath(Path.Combine(folder, "memory")),
            new Dictionary<string, MemoryLedgerEntry> { ["mine.md"] = new("x", 1, DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch) });

        var result = await Pull();

        Assert.Equal("still here", File.ReadAllText(MemoryFile(here, "kept.md")));
        Assert.False(File.Exists(MemoryFile(here, "gone.md")));
        Assert.False(File.Exists(MemoryFile(here, "mine.md")));
        var notes = Assert.Single(result.Folders).Notes.Select(n => (n.Path, n.Kind)).ToList();
        Assert.Equal(
            [("gone.md", MemoryPullNoteKind.DeletedEverywhere), ("kept.md", MemoryPullNoteKind.DeletedElsewhere), ("mine.md", MemoryPullNoteKind.DeletedHere)],
            notes);
        Assert.Equal(["laptop"], Assert.Single(result.Folders).Notes.Single(n => n.Path == "kept.md").Machines);
    }

    [Fact]
    public async Task Two_checkouts_of_one_project_each_get_its_memory()
    {
        var first = Checkout("one");
        var second = Checkout("two");
        Project(first, first);
        Project(second, second);
        var project = _registry.RemoteProject("repo", first, ("MEMORY.md", "index"));
        _registry.SameProject(second, project);

        var result = await Pull();

        Assert.Equal(2, result.Folders.Count);
        Assert.True(File.Exists(MemoryFile(first, "MEMORY.md")));
        Assert.True(File.Exists(MemoryFile(second, "MEMORY.md")));
        Assert.Single(_registry.ContentAsked);
    }

    [Fact]
    public async Task Memory_goes_to_the_main_worktrees_folder_when_the_transcript_is_from_a_linked_worktree()
    {
        var main = Checkout("beta");
        var worktree = Checkout("beta-wt");
        _projects.Worktree(worktree, main);
        Project(worktree, worktree);
        _registry.RemoteProject("beta", main, ("MEMORY.md", "beta"));

        await Pull();

        Assert.True(File.Exists(MemoryFile(main, "MEMORY.md")));
        Assert.False(Directory.Exists(Path.Combine(Folder(worktree), "memory")));
    }

    [Fact]
    public async Task A_transcript_of_a_folder_that_no_longer_exists_is_not_a_checkout()
    {
        var gone = Path.Combine(_root, "checkouts", "deleted");
        Project(gone, gone);
        _registry.RemoteProject("deleted", gone, ("MEMORY.md", "x"));

        var result = await Pull();

        Assert.Single(result.NotHere);
        Assert.DoesNotContain(gone, _projects.Asked);
    }

    [Fact]
    public async Task Scan_finds_a_checkout_the_harness_has_never_opened()
    {
        var parent = Path.Combine(_root, "src");
        var repo = Path.Combine(parent, "gamma");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(parent, "not-a-repo"));
        _registry.RemoteProject("gamma", repo, ("MEMORY.md", "gamma"));

        var result = await Pull(parent);

        Assert.Equal(1, result.Written);
        Assert.True(File.Exists(MemoryFile(repo, "MEMORY.md")));
        Assert.DoesNotContain(Path.Combine(parent, "not-a-repo"), _projects.Asked);
    }

    [Fact]
    public async Task A_path_that_leaves_the_memory_folder_or_content_that_fails_its_hash_is_refused()
    {
        var here = Checkout("alpha");
        Project(here, here);
        var project = _registry.RemoteProject("alpha", here, ("../escape.md", "x"), ("ok.md", "fine"), ("tampered.md", "y"));
        _registry.Tamper(project, "tampered.md");

        var result = await Pull();

        Assert.False(File.Exists(Path.Combine(Folder(here), "escape.md")));
        Assert.False(File.Exists(MemoryFile(here, "tampered.md")));
        Assert.True(File.Exists(MemoryFile(here, "ok.md")));
        Assert.Equal(
            [("../escape.md", MemoryPullNoteKind.Refused), ("tampered.md", MemoryPullNoteKind.Refused)],
            Assert.Single(result.Folders).Notes.Select(n => (n.Path, n.Kind)));
    }

    [Fact]
    public async Task A_memory_folder_that_is_a_symbolic_link_is_never_written_through()
    {
        if (OperatingSystem.IsWindows())
            return;
        var here = Checkout("alpha");
        var folder = Project(here, here);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(folder, "memory"), elsewhere);
        _registry.RemoteProject("alpha", here, ("MEMORY.md", "x"));

        var result = await Pull();

        Assert.NotNull(Assert.Single(result.Folders).Error);
        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task Pull_signed_out_fails_before_writing_anything()
    {
        var here = Checkout("alpha");
        Project(here, here);
        _registry.RemoteProject("alpha", here, ("MEMORY.md", "x"));
        _token = null;

        var result = await Pull();

        Assert.Equal(SessionSyncFailure.NotLoggedIn, result.Failure);
        Assert.False(File.Exists(MemoryFile(here, "MEMORY.md")));
    }

    [Theory]
    [InlineData("/home/me/src/protostar", "-home-me-src-protostar")]
    [InlineData(@"C:\Users\Blake\source\repos\protostar", "C--Users-Blake-source-repos-protostar")]
    [InlineData("/home/me/b-fac/.claude/worktrees/agent-1", "-home-me-b-fac--claude-worktrees-agent-1")]
    public void The_project_folder_name_is_the_path_with_every_non_alphanumeric_character_replaced(string directory, string expected) =>
        Assert.Equal(expected, _harness.ProjectFolderName(directory));

    // ── fakes ──────────────────────────────────────────────────────────────────────

    private sealed class FakeRegistry : IRegistryApi, ITranscriptApi, IMemoryApi, IProjectApi
    {
        private readonly Dictionary<(string Folder, string Path), (List<string> Versions, bool Deleted)> _files = [];
        private readonly Dictionary<Guid, (string Name, List<RemoteMemoryFile> Files)> _remote = [];
        private readonly Dictionary<string, Guid> _folders = new(StringComparer.Ordinal);

        public List<Guid> ContentAsked { get; } = [];

        /// <summary>A project with memory whose checkouts resolve by <paramref name="folder"/> (their main worktree).</summary>
        public Guid RemoteProject(string name, string folder, params (string Path, string Text)[] files)
        {
            var id = Guid.NewGuid();
            _remote[id] = (name, [.. files.Select(f =>
            {
                var bytes = Encoding.UTF8.GetBytes(f.Text);
                return new RemoteMemoryFile { Path = f.Path, Version = 1, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Content = bytes };
            })]);
            _folders[folder] = id;
            return id;
        }

        public void SameProject(string folder, Guid project) => _folders[folder] = project;

        /// <summary>Makes an earlier version, <paramref name="text"/> as <paramref name="version"/>, current again, as when the copy holding the newest is deleted.</summary>
        public void Revert(Guid project, string path, string text, int version)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Replace(project, path, f => f with { Version = version, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Content = bytes });
        }

        public void MarkDeleted(Guid project, string path, string machine, bool everywhere, Guid machineId = default, string folder = "/laptop/repo") =>
            Replace(project, path, f => f with { DeletedEverywhere = everywhere, DeletedOn = [new RemoteMemoryCopy { MachineId = machineId, MachineName = machine, Folder = folder }] });

        /// <summary>
        /// Makes <paramref name="text"/>, sent by <paramref name="machine"/>, the file's current version: the next number,
        /// edited from the current one, or with no base when <paramref name="parallel"/>.
        /// </summary>
        public void Change(Guid project, string path, string text, string machine, bool parallel = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var files = _remote[project].Files;
            var index = files.FindIndex(f => f.Path == path);
            var current = files[index];
            var history = current.History.Count > 0 ? current.History : [new RemoteMemoryVersionBase { Version = current.Version }];
            files[index] = new RemoteMemoryFile
            {
                Path = path, Version = current.Version + 1, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Content = bytes,
                MachineId = Guid.NewGuid(), MachineName = machine,
                History = [.. history, new RemoteMemoryVersionBase { Version = current.Version + 1, Base = parallel ? null : current.Version }],
            };
        }

        public void Tamper(Guid project, string path) => Replace(project, path, f => f with { Content = [.. f.Content, (byte)'!'] });

        private void Replace(Guid project, string path, Func<RemoteMemoryFile, RemoteMemoryFile> change)
        {
            var files = _remote[project].Files;
            var index = files.FindIndex(f => f.Path == path);
            files[index] = change(files[index]);
        }

        public Task<ApiResult<IReadOnlyList<RemoteMemoryProject>>> ListMemoryProjectsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ApiResult<IReadOnlyList<RemoteMemoryProject>>.Success(
                [.. _remote.Select(r => new RemoteMemoryProject { ProjectId = r.Key, ProjectName = r.Value.Name, Files = r.Value.Files.Count })]));
        }

        public Task<ApiResult<RemoteMemoryContent>> GetMemoryContentAsync(Uri registry, string accessToken, Guid projectId, CancellationToken cancellationToken)
        {
            Calls++;
            ContentAsked.Add(projectId);
            var (name, files) = _remote[projectId];
            return Task.FromResult(ApiResult<RemoteMemoryContent>.Success(new RemoteMemoryContent { ProjectId = projectId, ProjectName = name, Files = files }));
        }

        // A pull never reads redacted memory; only protostar mcp does.
        public Task<ApiResult<RemoteMemoryContent>> GetRedactedMemoryContentAsync(
            Uri registry, string accessToken, Guid projectId, string? path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A pull must read the stored bytes, never redacted memory.");

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken)
        {
            Calls++;
            var folder = facts.MainWorktree ?? facts.Directory;
            return Task.FromResult(ApiResult<RemoteProject>.Success(
                new RemoteProject { ProjectId = _folders.TryGetValue(folder, out var id) ? id : null, Folder = folder }));
        }

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public int Calls { get; set; }
        public int MachineRegistrations { get; private set; }
        public bool ForgetMachineOnce { get; set; }
        public List<(ProjectFacts Facts, List<string> Files, List<string> Deleted)> Pushes { get; } = [];

        /// <summary>The text last sent for each path.</summary>
        public Dictionary<string, string> Sent { get; } = [];

        /// <summary>The base version last sent for each path.</summary>
        public Dictionary<string, int?> SentBase { get; } = [];

        public int Versions(string folder, string path) => _files[(folder, path)].Versions.Count;

        public bool IsDeleted(string folder, string path) => _files[(folder, path)].Deleted;

        public Task<ApiResult<MemoryPushResponse>> PushMemoryAsync(
            Uri registry, string accessToken, Guid machineId, ProjectFacts facts, IReadOnlyList<MemoryUpload> files, IReadOnlyList<string> deleted,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (ForgetMachineOnce)
            {
                ForgetMachineOnce = false;
                return Task.FromResult(ApiResult<MemoryPushResponse>.UnknownMachine("Machine is not registered."));
            }

            Pushes.Add((facts, files.Select(f => f.Path).ToList(), deleted.ToList()));
            var folder = facts.MainWorktree ?? facts.Directory;
            var outcomes = new List<MemoryFileOutcome>();
            foreach (var file in files)
            {
                Sent[file.Path] = Encoding.UTF8.GetString(file.Content);
                SentBase[file.Path] = file.BaseVersion;
                var hash = Convert.ToHexString(SHA256.HashData(file.Content));
                var (versions, _) = _files.GetValueOrDefault((folder, file.Path), ([], false));
                var change = versions.Count == 0 ? "created" : versions.Contains(hash) ? "unchanged" : "versioned";
                if (!versions.Contains(hash))
                    versions.Add(hash);
                _files[(folder, file.Path)] = (versions, false);
                outcomes.Add(new MemoryFileOutcome { Path = file.Path, Change = change, Version = versions.IndexOf(hash) + 1 });
            }

            foreach (var path in deleted)
            {
                if (!_files.TryGetValue((folder, path), out var known))
                {
                    outcomes.Add(new MemoryFileOutcome { Path = path, Change = "unknown" });
                    continue;
                }

                _files[(folder, path)] = (known.Versions, true);
                outcomes.Add(new MemoryFileOutcome { Path = path, Change = known.Deleted ? "unchanged" : "markedDeleted", Version = known.Versions.Count });
            }

            return Task.FromResult(ApiResult<MemoryPushResponse>.Success(
                new MemoryPushResponse { ProjectId = Guid.NewGuid(), ProjectName = Path.GetFileName(folder), Folder = folder, Files = outcomes }));
        }

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<RegistryMeta?>(new RegistryMeta { ApiMajors = [1] });
        }

        public Task<ApiResult<bool>> RegisterMachineAsync(Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken)
        {
            Calls++;
            MachineRegistrations++;
            return Task.FromResult(ApiResult<bool>.Success(true));
        }

        public Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(
            Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, ProjectFacts? project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppendResult> AppendAsync(
            Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<TranscriptLookup>>> LookupAsync(
            Uri registry, string accessToken, IReadOnlyList<LocalTranscript> transcripts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<TranscriptDetail>> GetAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<SessionSkill>>> GetSkillsAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<Stream>> OpenRedactedContentAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SkillPushOutcome> PushSkillAsync(
            Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, SkillProject? project, int? baseVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillDownload> GetSkillFilesAsync(Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<string>> RenameSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, string newName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<bool>> DeleteSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjects : IProjectFactsResolver
    {
        private readonly Dictionary<string, ProjectFacts> _git = [];

        public List<string> Asked { get; } = [];

        /// <summary>Makes <paramref name="directory"/> a git checkout whose main worktree is <paramref name="main"/>.</summary>
        public void Worktree(string directory, string main) =>
            _git[directory] = new ProjectFacts(directory, directory, true, false, new string('a', 40), [new string('a', 40)], "main", [], directory, main, "");

        public ProjectFacts Resolve(string workingDirectory)
        {
            Asked.Add(workingDirectory);
            return _git.GetValueOrDefault(workingDirectory) ?? ProjectFacts.NotGit(workingDirectory, workingDirectory);
        }
    }

    private sealed class FakeLedger : IMemoryLedger
    {
        private readonly Dictionary<string, Dictionary<string, MemoryLedgerEntry>> _folders = [];

        public IReadOnlyDictionary<string, MemoryLedgerEntry> Get(Uri registry, string memoryFolder) =>
            _folders.GetValueOrDefault(memoryFolder) ?? [];

        public void Save(Uri registry, string memoryFolder, IReadOnlyDictionary<string, MemoryLedgerEntry> entries) =>
            _folders[memoryFolder] = new Dictionary<string, MemoryLedgerEntry>(entries);

        public IDisposable? TryLock(Uri registry, string memoryFolder) => new MemoryStream();
    }

    private sealed class FakeMachine : IMachineIdentity
    {
        public static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private bool _registered;

        public MachineInfo Get() => new(Id, "test", "linux");

        public bool IsRegisteredWith(Uri registry) => _registered;

        public void MarkRegistered(Uri registry) => _registered = true;
    }

    private sealed class FakeTokens(Func<SessionLookup> session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session());
    }

    private sealed class FakeCatalog(params IHarness[] harnesses) : IHarnessCatalog
    {
        public IReadOnlyList<IHarness> All => harnesses;
        public string KnownIds => string.Join(", ", harnesses.Select(h => h.Id));
        public IHarness? ById(string id) => harnesses.FirstOrDefault(h => h.Id == id);
    }
}
