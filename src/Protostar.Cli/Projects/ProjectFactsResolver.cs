using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Protostar.Cli.Projects;

/// <summary>Works out the <see cref="ProjectFacts"/> of a working directory by running git there.</summary>
/// <remarks>
/// The one place the CLI decides what a directory is. Session backups send its answer with each transcript,
/// and <c>protostar project</c> shows it, so the two can never disagree.
/// </remarks>
internal interface IProjectFactsResolver
{
    /// <summary>The facts for <paramref name="workingDirectory"/>.</summary>
    /// <remarks>
    /// Never throws: a missing directory is walked up to its nearest existing ancestor, and a folder outside git,
    /// or a machine without git, gives <see cref="ProjectFacts.NotGit"/>. Answers are cached per directory for
    /// the life of the resolver (one CLI run), because a history of thousands of transcripts lives in a handful
    /// of directories and each answer costs several git processes.
    /// </remarks>
    ProjectFacts Resolve(string workingDirectory);
}

/// <summary>Production <see cref="IProjectFactsResolver"/>, which runs the <c>git</c> executable.</summary>
/// <remarks>
/// <para>
/// ADR 0002's rules, as the CLI's half of them: the default branch is the first of <c>origin</c>'s remote
/// <c>HEAD</c>, any other remote's <c>HEAD</c>, local <c>main</c>, local <c>master</c>, then <c>HEAD</c>; the
/// key is that branch's first-parent root commit, because a repo can have several roots (merged histories,
/// subtrees, orphan branches); a shallow clone reports its shallow boundary as its root, so it sends none.
/// </para>
/// <para>
/// Git is run with the repository-locating environment variables removed, so a hook started from inside a
/// git operation cannot point it at a different repo.
/// </para>
/// </remarks>
internal sealed class ProjectFactsResolver : IProjectFactsResolver
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] RepoEnvironment =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_PREFIX", "GIT_CEILING_DIRECTORIES"];

    private readonly string _git;
    private readonly ConcurrentDictionary<string, ProjectFacts> _cache = new(StringComparer.Ordinal);

    public ProjectFactsResolver()
        : this("git")
    {
    }

    /// <summary>Uses <paramref name="git"/> as the git executable; tests pass one that does not exist.</summary>
    internal ProjectFactsResolver(string git) => _git = git;

    /// <inheritdoc />
    public ProjectFacts Resolve(string workingDirectory)
    {
        var directory = NearestExisting(workingDirectory);
        var facts = _cache.GetOrAdd(directory, Gather);
        return facts with { WorkingDirectory = workingDirectory };
    }

    private ProjectFacts Gather(string directory)
    {
        var notGit = ProjectFacts.NotGit(directory, directory);

        // One process for the layout. --path-format=absolute applies to the options after it.
        var layout = Git(directory, "rev-parse", "--is-inside-work-tree", "--is-shallow-repository", "--show-toplevel", "--show-prefix", "--path-format=absolute", "--git-common-dir");
        if (layout is not { Length: >= 5 } || layout[0] != "true")
            return notGit;

        var shallow = layout[1] == "true";
        var topLevel = Path.GetFullPath(layout[2]);
        var subpath = layout[3];
        var commonDir = Path.GetFullPath(layout[4]);
        var mainWorktree = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(commonDir)) ?? topLevel;

        var remotes = (Git(directory, "config", "--get-regexp", @"^remote\..*\.url$") ?? [])
            .Select(ParseRemote)
            .OfType<GitRemote>()
            .ToList();

        var branch = DefaultBranch(directory);
        string? root = null;
        IReadOnlyList<string> roots = [];
        if (!shallow)
        {
            root = Git(directory, "rev-list", "--first-parent", "--max-parents=0", branch)?.FirstOrDefault(l => l.Length > 0);
            roots = Git(directory, "rev-list", "--max-parents=0", "HEAD")?.Where(l => l.Length > 0).ToList() ?? [];
        }

        return new ProjectFacts(directory, directory, true, shallow, root, roots, branch, remotes, topLevel, mainWorktree, subpath);
    }

    private string DefaultBranch(string directory)
    {
        var refs = (Git(directory, "for-each-ref", "--format=%(refname)", "refs/remotes/*/HEAD", "refs/heads/main", "refs/heads/master") ?? [])
            .Where(r => r.Length > 0)
            .ToList();

        return refs.Contains("refs/remotes/origin/HEAD") ? "refs/remotes/origin/HEAD"
            : refs.FirstOrDefault(r => r.StartsWith("refs/remotes/", StringComparison.Ordinal))
            ?? (refs.Contains("refs/heads/main") ? "refs/heads/main"
            : refs.Contains("refs/heads/master") ? "refs/heads/master"
            : "HEAD");
    }

    // "remote.<name>.url <url>"; a remote name may itself contain dots.
    private static GitRemote? ParseRemote(string line)
    {
        var space = line.IndexOf(' ');
        if (space < 0 || !line.StartsWith("remote.", StringComparison.Ordinal))
            return null;
        var key = line[..space];
        if (!key.EndsWith(".url", StringComparison.Ordinal) || key.Length <= "remote..url".Length)
            return null;
        return new GitRemote(key["remote.".Length..^".url".Length], line[(space + 1)..].Trim());
    }

    private static string NearestExisting(string path)
    {
        string? current;
        try
        {
            current = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        while (current is not null && !Directory.Exists(current))
            current = Path.GetDirectoryName(current);
        return current ?? path;
    }

    // Runs git in directory and returns its stdout lines, or null when it failed, timed out, or is not installed.
    private string[]? Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(_git)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var name in RepoEnvironment)
            start.Environment.Remove(name);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? stdout.Result.Split('\n').Select(l => l.TrimEnd('\r')).ToArray() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // git is not installed, or could not start here: the directory is treated as not a repo.
            return null;
        }
    }
}
