using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Install;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Removes an installed protostar binary and (on Windows) its PATH entry.</summary>
internal sealed class UninstallCommand : Command<UninstallCommand.Settings>
{
    private readonly IHookInstallService _hooks;
    private readonly IHarnessCatalog _catalog;

    public UninstallCommand(IHookInstallService hooks, IHarnessCatalog catalog)
    {
        _hooks = hooks;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--dir <DIR>")]
        [Description("Install directory to remove from. Defaults to the per-user location.")]
        public string? Dir { get; init; }

        [CommandOption("--no-modify-path")]
        [Description("Do not remove the install directory from PATH.")]
        public bool NoModifyPath { get; init; }

        [CommandOption("--no-hooks")]
        [Description("Do not remove capture hooks from detected harnesses.")]
        public bool NoHooks { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root when removing hooks.")]
        public string? HarnessHome { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var dir = settings.Dir ?? InstallLocations.DefaultDir();
        var dest = Path.Combine(dir, InstallLocations.ExecutableName);

        // Remove the capture hooks first: they point at the binary we are about to delete, so
        // leaving them would dangle. Opt out with --no-hooks.
        if (!settings.NoHooks)
        {
            // Hook removal is best-effort: render its outcome but never let it fail the uninstall.
            var hooks = _hooks.Uninstall(new HookInstallOptions
            {
                RootOverride = settings.HarnessHome,
            });
            HookInstallPresenter.Render(hooks, dryRun: false, _catalog);
        }

        if (!File.Exists(dest))
        {
            AnsiConsole.MarkupLine($"[grey]Nothing to remove — {Markup.Escape(dest)} does not exist.[/]");
            return 0;
        }

        // On Windows the running image is locked, so a self-uninstall — the common case, where the
        // user typed `protostar uninstall` and PATH resolved to the installed binary — cannot delete
        // its own protostar.exe in-process; it fails with "Access to the path is denied". Hand that
        // one case to a detached helper that finishes the removal once we exit. Everywhere else (and
        // on Unix, where a running binary can be unlinked) we delete in-process below.
        if (OperatingSystem.IsWindows() && SelfRemoval.IsRunningExecutable(dest))
        {
            try
            {
                var owns = OwnsDirectory(dir);
                var targets = owns ? new[] { dir } : OwnFiles(dir).Where(File.Exists).ToArray();
                SelfRemoval.ScheduleAfterExit(targets, pruneEmptyDir: owns ? null : dir);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Uninstall failed:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            if (!settings.NoModifyPath)
                PathManager.RemoveFromPath(dir);
            AnsiConsole.MarkupLine($"Removed [aqua]protostar[/] from [grey]{Markup.Escape(dir)}[/].");
            AnsiConsole.MarkupLine("[grey]The running executable finishes deleting itself as this process exits.[/]");
            return 0;
        }

        try
        {
            if (OwnsDirectory(dir))
            {
                // A protostar-dedicated directory: clear it entirely (a multi-file install left its
                // .dll and runtime config here too).
                Directory.Delete(dir, recursive: true);
            }
            else
            {
                // A shared or custom directory: remove only protostar's own files so we never delete
                // unrelated binaries that happen to live alongside it.
                foreach (var file in OwnFiles(dir))
                    if (File.Exists(file))
                        File.Delete(file);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Uninstall failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (!settings.NoModifyPath)
            PathManager.RemoveFromPath(dir);
        AnsiConsole.MarkupLine($"Removed [aqua]protostar[/] from [grey]{Markup.Escape(dir)}[/].");
        return 0;
    }

    // True when the directory is protostar's own (the default location, or a dir literally named
    // "protostar"), so clearing it wholesale is safe.
    private static bool OwnsDirectory(string dir)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(InstallLocations.DefaultDir()), comparison))
            return true;
        var leaf = new DirectoryInfo(dir).Name;
        return string.Equals(leaf, "protostar", comparison);
    }

    // The files a protostar install owns: the launcher plus the framework-dependent companions.
    private static IEnumerable<string> OwnFiles(string dir)
    {
        var name = Path.GetFileNameWithoutExtension(InstallLocations.ExecutableName);
        yield return Path.Combine(dir, InstallLocations.ExecutableName);
        foreach (var ext in new[] { ".dll", ".deps.json", ".runtimeconfig.json", ".pdb" })
            yield return Path.Combine(dir, name + ext);
    }
}
