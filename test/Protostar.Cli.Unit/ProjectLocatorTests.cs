using Protostar.Cli.Harness;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// In-process tests for <see cref="ProjectLocator"/>, the harness-agnostic "walk up to a project
/// root" helper. Each test builds a throwaway directory tree under the system temp folder and is
/// torn down in <see cref="Dispose"/>, so nothing depends on the real working directory. Markers use
/// a per-test GUID so the upward walk can never collide with a real directory above the temp root.
/// </summary>
public sealed class ProjectLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _marker = "marker-" + Guid.NewGuid().ToString("N");

    public ProjectLocatorTests() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "protostar-locatortest-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Finds_the_marker_in_the_starting_directory_itself()
    {
        var start = Dir("a", "b", "c");
        MarkerDir(start);

        Assert.Equal(Normalize(start), Normalize(ProjectLocator.FindAncestorContaining(start, _marker)));
    }

    [Fact]
    public void Walks_up_to_the_nearest_ancestor_that_contains_the_marker()
    {
        var ancestor = Dir("a");
        MarkerDir(ancestor);
        var start = Dir("a", "b", "c");

        Assert.Equal(Normalize(ancestor), Normalize(ProjectLocator.FindAncestorContaining(start, _marker)));
    }

    [Fact]
    public void Matches_a_marker_that_is_a_file_not_a_directory()
    {
        var ancestor = Dir("a");
        File.WriteAllText(Path.Combine(ancestor, _marker), "");
        var start = Dir("a", "b", "c");

        Assert.Equal(Normalize(ancestor), Normalize(ProjectLocator.FindAncestorContaining(start, _marker)));
    }

    [Fact]
    public void Returns_null_when_no_ancestor_contains_any_marker()
    {
        var start = Dir("a", "b", "c");

        Assert.Null(ProjectLocator.FindAncestorContaining(start, _marker));
    }

    [Fact]
    public void Returns_the_closest_ancestor_when_the_marker_exists_at_several_levels()
    {
        MarkerDir(_root);
        var middle = Dir("a", "b");
        MarkerDir(middle);
        var start = Dir("a", "b", "c");

        Assert.Equal(Normalize(middle), Normalize(ProjectLocator.FindAncestorContaining(start, _marker)));
    }

    [Fact]
    public void Matches_when_any_one_of_several_markers_is_present()
    {
        var ancestor = Dir("a");
        MarkerDir(ancestor);
        var start = Dir("a", "b");

        // The present marker is last in the list, so this also proves all markers are considered.
        var result = ProjectLocator.FindAncestorContaining(start, "absent-" + Guid.NewGuid().ToString("N"), _marker);

        Assert.Equal(Normalize(ancestor), Normalize(result));
    }

    [Fact]
    public void Returns_null_when_no_markers_are_supplied()
    {
        var start = Dir("a", "b", "c");

        Assert.Null(ProjectLocator.FindAncestorContaining(start));
    }

    [Fact]
    public void Returns_null_when_the_starting_directory_does_not_exist()
    {
        // A caller may pass a path that has not been created yet. The walk should run over the
        // non-existent path's ancestors without throwing and simply find no marker.
        var start = Path.Combine(_root, "does-not-exist", "nor-this");

        Assert.Null(ProjectLocator.FindAncestorContaining(start, _marker));
    }

    private string Dir(params string[] parts) =>
        Directory.CreateDirectory(Path.Combine(new[] { _root }.Concat(parts).ToArray())).FullName;

    private void MarkerDir(string parent) => Directory.CreateDirectory(Path.Combine(parent, _marker));

    private static string? Normalize(string? path) =>
        path is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
