using System.ComponentModel;
using Protostar.Cli.Hooks;
using Protostar.Cli.Install;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Self-installs the running binary: copies it into a per-user directory and (unless told not to)
/// ensures that directory is on PATH. A published self-contained single-file binary is copied as
/// one file; a framework-dependent build (e.g. a local `dotnet build`, where the .exe is just an
/// apphost that needs its .dll beside it) has its whole build output copied so the install actually
/// runs. Then capture hooks are wired into detected harnesses (opt out with --no-hooks).
/// </summary>
internal sealed class InstallCommand : Command<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--dir <DIR>")]
        [Description("Install directory. Defaults to a per-user location.")]
        public string? Dir { get; init; }

        [CommandOption("--no-modify-path")]
        [Description("Do not add the install directory to PATH.")]
        public bool NoModifyPath { get; init; }

        [CommandOption("--no-hooks")]
        [Description("Do not install capture hooks into detected harnesses.")]
        public bool NoHooks { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root when installing hooks.")]
        public string? HarnessHome { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var source = Environment.ProcessPath;
        if (source is null || !File.Exists(source))
        {
            AnsiConsole.MarkupLine("[red]Could not determine the running executable to install.[/]");
            return 1;
        }

        var dir = settings.Dir ?? InstallLocations.DefaultDir();
        var dest = Path.Combine(dir, InstallLocations.ExecutableName);

        if (PathsEqual(source, dest))
        {
            AnsiConsole.MarkupLine($"[green]protostar[/] is already installed at [grey]{Markup.Escape(dest)}[/].");
            ReportPath(dir, settings.NoModifyPath);
            return InstallHooksTail(settings, dest);
        }

        // A framework-dependent build leaves "<name>.dll" beside the apphost ".exe"; that whole set
        // must travel together or the installed launcher cannot find its program. A single-file
        // self-contained publish has no such sibling and is copied alone.
        var sourceDir = Path.GetDirectoryName(source)!;
        var isSingleFile = !File.Exists(Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(source) + ".dll"));

        try
        {
            Directory.CreateDirectory(dir);
            if (isSingleFile)
                File.Copy(source, dest, overwrite: true);
            else
                CopyDirectory(sourceDir, dir);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dest,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Install failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"Installed [aqua]protostar[/] [grey]v{CliInfo.Version}[/] → [grey]{Markup.Escape(dest)}[/]");
        if (!isSingleFile)
            AnsiConsole.MarkupLine("[grey]Framework-dependent build: copied the full build output; requires the .NET runtime.[/]");
        ReportPath(dir, settings.NoModifyPath);
        return InstallHooksTail(settings, dest);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
    }

    // After placing the binary, wire capture hooks into every detected harness (non-interactive,
    // pointing the hooks at the binary we just installed). Opt out with --no-hooks.
    private static int InstallHooksTail(Settings settings, string dest)
    {
        if (settings.NoHooks)
            return 0;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Installing capture hooks into detected harnesses...[/]");
        return new HookInstallService().Install(new HookInstallService.Options
        {
            RootOverride = settings.HarnessHome,
            All = true,
            NonInteractive = true,
            ExePathOverride = dest,
        });
    }

    private static void ReportPath(string dir, bool noModifyPath)
    {
        if (noModifyPath)
        {
            if (!PathManager.IsOnPath(dir))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(dir)} is not on PATH (left unchanged).[/]");
            return;
        }

        var hint = PathManager.EnsureOnPath(dir);
        if (hint is not null)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(hint)}[/]");
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }
}
