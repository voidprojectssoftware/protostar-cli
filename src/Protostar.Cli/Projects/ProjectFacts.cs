namespace Protostar.Cli.Projects;

/// <summary>What the CLI found out about a working directory, for the registry to resolve to a project.</summary>
/// <remarks>
/// <para>
/// ADR 0002 (protostar-registry): only the CLI can run git in the checkout, so it gathers the raw facts and the
/// registry decides what they mean (normalising remotes, matching keys, applying links). The registry keeps them,
/// so it can recompute the mapping later without the CLI. Serialised camelCase as the <c>project</c> field of a
/// transcript registration.
/// </para>
/// <para>
/// For a folder that is not a git repo, or when git is not installed, only <see cref="WorkingDirectory"/> and
/// <see cref="Directory"/> are set.
/// </para>
/// </remarks>
/// <param name="WorkingDirectory">The working directory asked about, as the harness recorded it.</param>
/// <param name="Directory">The nearest existing folder at or above it, where git was run; differs when the working directory was deleted.</param>
/// <param name="IsGit">Whether <paramref name="Directory"/> is inside a git work tree.</param>
/// <param name="Shallow">Whether the repo is a shallow clone, whose root commit git reports wrongly.</param>
/// <param name="RootCommit">The first-parent root commit of <paramref name="DefaultBranch"/>; <c>null</c> for a shallow clone or a repo with no commits.</param>
/// <param name="Roots">Every root commit reachable from <c>HEAD</c>; empty for a shallow clone.</param>
/// <param name="DefaultBranch">The ref the root was computed from: a remote's <c>HEAD</c>, else <c>main</c>, <c>master</c>, then <c>HEAD</c>.</param>
/// <param name="Remotes">Remote names and their URLs as configured, unnormalised.</param>
/// <param name="TopLevel">The work tree's top folder.</param>
/// <param name="MainWorktree">The main worktree's folder, the same from every worktree of the repo.</param>
/// <param name="Subpath">The path of <paramref name="Directory"/> within the repo, with a trailing slash; empty at the top.</param>
internal sealed record ProjectFacts(
    string WorkingDirectory,
    string Directory,
    bool IsGit,
    bool Shallow,
    string? RootCommit,
    IReadOnlyList<string> Roots,
    string? DefaultBranch,
    IReadOnlyList<GitRemote> Remotes,
    string? TopLevel,
    string? MainWorktree,
    string? Subpath)
{
    /// <summary>Facts for a folder that is not in a git repo (or where git could not be run).</summary>
    public static ProjectFacts NotGit(string workingDirectory, string directory) =>
        new(workingDirectory, directory, false, false, null, [], null, [], null, null, null);
}

/// <summary>One configured git remote.</summary>
/// <param name="Name">For example <c>origin</c>.</param>
/// <param name="Url">The URL as configured.</param>
internal sealed record GitRemote(string Name, string Url);
