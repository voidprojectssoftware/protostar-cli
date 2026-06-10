using System.Diagnostics;
using System.Text;

namespace Protostar.Cli.Install;

/// <summary>
/// Removes protostar's own files when an uninstall is deleting the binary that is currently running.
///
/// On Windows a running executable's image is locked, so a self-uninstall cannot delete its own
/// <c>protostar.exe</c> in-process — the delete fails with "Access to the path is denied". (On Unix
/// a running binary can be unlinked freely, so this is only needed on Windows.) The fix is to hand
/// the deletion to a short-lived, detached <c>cmd.exe</c> that retries removing the files until the
/// lock is released — i.e. the moment we exit — then deletes its own script. No admin rights, no
/// reboot, no residue.
/// </summary>
internal static class SelfRemoval
{
    // Backstop so a genuinely stuck file (some other process holding a handle) can never spin the
    // helper forever. Our process exits within milliseconds of spawning it, so in practice the
    // delete succeeds on the first or second pass; this cap is only ever hit when something is wrong.
    private const int MaxTries = 30;

    /// <summary>True when <paramref name="targetFile"/> is this process's own executable.</summary>
    public static bool IsRunningExecutable(string targetFile)
    {
        var self = Environment.ProcessPath;
        if (self is null)
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(self), Path.GetFullPath(targetFile), comparison);
    }

    /// <summary>
    /// Schedules removal of <paramref name="retryTargets"/> (files and/or directories) once this
    /// process exits, by spawning a detached helper that retries the delete until the running image
    /// is unlocked. When <paramref name="pruneEmptyDir"/> is given, the helper also removes that
    /// directory non-recursively at the end — so an install whose own files we deleted out of a
    /// shared directory leaves no empty folder, while a directory that still holds unrelated files is
    /// left untouched. Windows only.
    /// </summary>
    public static void ScheduleAfterExit(IReadOnlyList<string> retryTargets, string? pruneEmptyDir)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Deferred self-removal is only needed on Windows.");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"protostar-uninstall-{Guid.NewGuid():N}.cmd");
        // ASCII batch, no BOM: cmd.exe mis-parses a UTF-8 BOM on the first line.
        File.WriteAllText(scriptPath, BuildScript(retryTargets, pruneEmptyDir), new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            // The helper must not run from inside the directory it is about to remove, or that
            // directory would be in use as its working directory and rmdir would fail.
            WorkingDirectory = Path.GetTempPath(),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // /D skips any AutoRun command a user profile might have wired into cmd.
        psi.ArgumentList.Add("/D");
        psi.ArgumentList.Add("/C");
        psi.ArgumentList.Add(scriptPath);

        // Detached: .NET does not place the child in a kill-on-close job, so it outlives us.
        Process.Start(psi);
    }

    /// <summary>
    /// Builds the batch the helper runs: delete the targets, and while any survive (the running
    /// image is still locked) wait a beat and retry, then prune the emptied directory and delete the
    /// script itself. Internal for unit testing.
    /// </summary>
    internal static string BuildScript(IReadOnlyList<string> retryTargets, string? pruneEmptyDir)
    {
        // The only per-run lines are one delete and one "is it still there?" check per target,
        // because the target count varies; everything else is fixed below.
        var deletes = string.Join("\n", retryTargets.Select(DeleteCommand));
        var existsChecks = string.Join("\n", retryTargets.Select(target => $"if exist \"{target}\" goto wait"));
        // Only when clearing loose files out of a directory we do not own wholesale: a non-recursive
        // rmdir that succeeds only if those files left the directory empty, and is harmless otherwise.
        var prune = pruneEmptyDir is null ? "" : $"rmdir \"{pruneEmptyDir}\" 2>nul\n";

        // Notes on the fixed parts:
        //  - ping is a reliable ~1s sleep when detached without a console; `timeout` errors there
        //    because it cannot read the (redirected) console input.
        //  - `(goto)` with no label pops the batch context so cmd stops reading this file, after
        //    which `&` deletes the script itself.
        var script = $"""
            @echo off
            setlocal
            set /a tries=0
            :retry
            {deletes}
            {existsChecks}
            goto done
            :wait
            set /a tries+=1
            if %tries% geq {MaxTries} goto done
            ping -n 2 127.0.0.1 >nul 2>&1
            goto retry
            :done
            {prune}(goto) 2>nul & del /F /Q "%~f0"
            """;

        // cmd.exe wants CRLF line endings; normalise regardless of how this source file is checked out.
        return script.ReplaceLineEndings("\r\n");
    }

    // rmdir for a directory, del for a file; both decided now, while the target still exists.
    private static string DeleteCommand(string path) =>
        Directory.Exists(path)
            ? $"rmdir /S /Q \"{path}\" 2>nul"
            : $"del /F /Q \"{path}\" 2>nul";
}
