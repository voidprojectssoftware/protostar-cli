using Protostar.Cli.Install;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// In-process tests for <see cref="SelfRemoval"/>: the helper that lets a Windows self-uninstall
/// delete its own locked binary by deferring removal to a detached process. These assert detection
/// and the generated batch's shape; the end-to-end "binary actually deletes itself" path is covered
/// by the black-box Uninstall feature.
/// </summary>
public sealed class SelfRemovalTests
{
    [Fact]
    public void IsRunningExecutable_is_true_for_the_current_process_path()
    {
        var self = Environment.ProcessPath;
        Assert.NotNull(self);
        Assert.True(SelfRemoval.IsRunningExecutable(self!));
    }

    [Fact]
    public void IsRunningExecutable_is_false_for_an_unrelated_path()
    {
        var other = Path.Combine(Path.GetTempPath(), "definitely-not-me", "protostar.exe");
        Assert.False(SelfRemoval.IsRunningExecutable(other));
    }

    [Fact]
    public void BuildScript_for_an_owned_directory_rmdirs_it_and_waits_until_gone()
    {
        // A directory target stands in for "protostar owns this folder, clear it wholesale".
        var dir = Path.Combine(Path.GetTempPath(), "protostar-selfremoval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = SelfRemoval.BuildScript([dir], pruneEmptyDir: null);

            Assert.Contains($"rmdir /S /Q \"{dir}\"", script);
            // Retries until the locked image is released, then self-deletes.
            Assert.Contains($"if exist \"{dir}\" goto wait", script);
            Assert.Contains(":retry", script);
            Assert.Contains("(goto) 2>nul & del /F /Q \"%~f0\"", script);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildScript_for_loose_files_dels_each_and_prunes_the_emptied_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "protostar-selfremoval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "protostar.exe");
        var dll = Path.Combine(dir, "protostar.dll");
        File.WriteAllText(exe, "");
        File.WriteAllText(dll, "");
        try
        {
            var script = SelfRemoval.BuildScript([exe, dll], pruneEmptyDir: dir);

            Assert.Contains($"del /F /Q \"{exe}\"", script);
            Assert.Contains($"del /F /Q \"{dll}\"", script);
            // Non-recursive prune so a shared directory that still holds other files is left alone.
            Assert.Contains($"rmdir \"{dir}\" 2>nul", script);
            Assert.DoesNotContain($"rmdir /S /Q \"{dir}\"", script);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
