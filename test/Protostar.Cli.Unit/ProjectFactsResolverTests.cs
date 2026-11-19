using System.Diagnostics;
using Protostar.Cli.Projects;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="ProjectFactsResolver"/>, against real git repos built in a temp folder.</summary>
/// <remarks>The cases are ADR 0002's: worktrees, subfolders, shallow clones, several roots, deleted directories, no git.</remarks>
public sealed class ProjectFactsResolverTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("pstar-projects-").FullName);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Test";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    // A repo with two commits on main, an origin remote, and a src/ folder.
    private string Repo(string name = "widget")
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "src", "a.txt"), "a");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "root");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "b");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "second");
        Git(repo, "remote", "add", "origin", "git@github.com:acme/widget.git");
        return repo;
    }

    private static string RootOf(string repo) => Git(repo, "rev-list", "--max-parents=0", "HEAD");

    [Fact]
    public void A_repo_reports_its_root_commit_remote_and_layout()
    {
        var repo = Repo();

        var facts = new ProjectFactsResolver().Resolve(repo);

        Assert.True(facts.IsGit);
        Assert.False(facts.Shallow);
        Assert.Equal(RootOf(repo), facts.RootCommit);
        Assert.Equal([RootOf(repo)], facts.Roots);
        Assert.Equal("refs/heads/main", facts.DefaultBranch);
        Assert.Equal([new GitRemote("origin", "git@github.com:acme/widget.git")], facts.Remotes);
        Assert.Equal(repo, facts.TopLevel);
        Assert.Equal(repo, facts.MainWorktree);
        Assert.Equal("", facts.Subpath);
    }

    [Fact]
    public void A_subfolder_reports_its_path_within_the_repo()
    {
        var repo = Repo();

        var facts = new ProjectFactsResolver().Resolve(Path.Combine(repo, "src"));

        Assert.Equal("src/", facts.Subpath);
        Assert.Equal(RootOf(repo), facts.RootCommit);
    }

    [Fact]
    public void A_worktree_has_the_same_root_and_main_worktree_as_its_repo()
    {
        var repo = Repo();
        var worktree = Path.Combine(_root, "widget-wt");
        Git(repo, "worktree", "add", "-q", "-b", "feature", worktree);

        var facts = new ProjectFactsResolver().Resolve(worktree);

        Assert.Equal(RootOf(repo), facts.RootCommit);
        Assert.Equal(repo, facts.MainWorktree);
        Assert.Equal(worktree, facts.TopLevel);
    }

    [Fact]
    public void A_shallow_clone_sends_no_root_commit()
    {
        var repo = Repo();
        var shallow = Path.Combine(_root, "shallow");
        Git(_root, "clone", "-q", "--depth", "1", new Uri(repo).AbsoluteUri, shallow);

        var facts = new ProjectFactsResolver().Resolve(shallow);

        Assert.True(facts.IsGit);
        Assert.True(facts.Shallow);
        Assert.Null(facts.RootCommit);
        Assert.Empty(facts.Roots);
        Assert.Equal("refs/remotes/origin/HEAD", facts.DefaultBranch);
    }

    [Fact]
    public void Only_the_first_parent_root_of_the_default_branch_is_the_key()
    {
        var repo = Repo();
        var mainRoot = RootOf(repo);
        Git(repo, "checkout", "-q", "--orphan", "screenshots");
        File.WriteAllText(Path.Combine(repo, "shot.txt"), "s");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "orphan root");

        // Checked out on the orphan branch, HEAD's root is the orphan's, but the key stays main's.
        var facts = new ProjectFactsResolver().Resolve(repo);

        Assert.Equal(mainRoot, facts.RootCommit);
        Assert.DoesNotContain(mainRoot, facts.Roots);
    }

    [Fact]
    public void A_folder_outside_git_gives_only_its_path()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "notes")).FullName;

        var facts = new ProjectFactsResolver().Resolve(plain);

        Assert.False(facts.IsGit);
        Assert.Equal(plain, facts.Directory);
        Assert.Null(facts.RootCommit);
        Assert.Empty(facts.Remotes);
    }

    [Fact]
    public void A_deleted_working_directory_is_resolved_from_its_nearest_existing_folder()
    {
        var repo = Repo();
        var gone = Path.Combine(repo, "src", "deleted", "deeper");

        var facts = new ProjectFactsResolver().Resolve(gone);

        Assert.Equal(gone, facts.WorkingDirectory);
        Assert.Equal(Path.Combine(repo, "src"), facts.Directory);
        Assert.Equal(RootOf(repo), facts.RootCommit);
    }

    [Fact]
    public void Without_git_a_repo_is_treated_as_a_plain_folder()
    {
        var repo = Repo();

        var facts = new ProjectFactsResolver(Path.Combine(_root, "no-such-git")).Resolve(repo);

        Assert.False(facts.IsGit);
        Assert.Equal(repo, facts.Directory);
    }

    [Fact]
    public void A_directory_is_resolved_once_per_resolver()
    {
        var repo = Repo();
        var resolver = new ProjectFactsResolver();
        var first = resolver.Resolve(repo);

        // Without its .git folder the repo would no longer be one if git ran again.
        Directory.Delete(Path.Combine(repo, ".git"), recursive: true);
        var second = resolver.Resolve(repo);

        Assert.Equal(first, second);
    }
}
