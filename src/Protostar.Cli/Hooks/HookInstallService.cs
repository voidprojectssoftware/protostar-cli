using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Hooks;

/// <summary>
/// Orchestrates capture-hook install/remove across harnesses: detect what's present, let the user
/// choose (interactively, or via flags for non-interactive/CI use), then apply and report. Shared
/// by the standalone <c>install-hooks</c> command and the <c>install</c>/<c>uninstall</c> lifecycle
/// so there is one code path, not a command invoking another.
/// </summary>
internal sealed class HookInstallService
{
    public sealed record Options
    {
        /// <summary><c>--harness-home</c>: override the harness config root.</summary>
        public string? RootOverride { get; init; }

        /// <summary><c>--harness</c>: target these harness ids explicitly (implies non-interactive).</summary>
        public IReadOnlyList<string>? HarnessIds { get; init; }

        /// <summary><c>--all</c>: select every detected harness without prompting.</summary>
        public bool All { get; init; }

        /// <summary><c>--yes</c> or a non-TTY context: skip the prompt, default to all detected.</summary>
        public bool NonInteractive { get; init; }

        /// <summary><c>--dry-run</c>: report intended changes without writing.</summary>
        public bool DryRun { get; init; }

        /// <summary>Path to the protostar binary the hooks should invoke. Defaults to this process.</summary>
        public string? ExePathOverride { get; init; }
    }

    public int Install(Options opts) => Run(opts, remove: false);

    public int Uninstall(Options opts) => Run(opts, remove: true);

    private int Run(Options opts, bool remove)
    {
        if (!TryResolveTargets(opts, out var targets, out var exitCode))
            return exitCode;
        if (targets.Count == 0)
            return 0;

        string? exePath = null;
        if (!remove)
        {
            exePath = opts.ExePathOverride ?? Environment.ProcessPath;
            if (exePath is null || !File.Exists(exePath))
            {
                AnsiConsole.MarkupLine("[red]Could not determine the protostar binary for the hook command.[/]");
                return 1;
            }
        }

        var failed = false;
        foreach (var (harness, location) in targets)
        {
            try
            {
                var result = remove
                    ? harness.RemoveHooks(location, opts.DryRun)
                    : harness.InstallHooks(location, exePath!, opts.DryRun);
                Report(harness, result, opts.DryRun);
            }
            catch (Exception ex)
            {
                failed = true;
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(harness.DisplayName)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        return failed ? 1 : 0;
    }

    // Build the list of (harness, location) to act on. Explicit --harness ids are targeted even if
    // not currently present (the user asked for them); otherwise we detect and select.
    private static bool TryResolveTargets(
        Options opts,
        out List<(IHarness harness, HarnessLocation location)> targets,
        out int exitCode)
    {
        targets = [];
        exitCode = 0;

        if (opts.HarnessIds is { Count: > 0 })
        {
            foreach (var id in opts.HarnessIds)
            {
                var harness = HarnessRegistry.ById(id);
                if (harness is null)
                {
                    AnsiConsole.MarkupLine($"[red]Unknown harness '{Markup.Escape(id)}'.[/] Known: {string.Join(", ", HarnessRegistry.All.Select(h => h.Id))}");
                    exitCode = 1;
                    return false;
                }
                harness.TryLocate(opts.RootOverride, out var location);
                targets.Add((harness, location));
            }
            return true;
        }

        var detected = new List<(IHarness harness, HarnessLocation location)>();
        foreach (var harness in HarnessRegistry.All)
            if (harness.TryLocate(opts.RootOverride, out var location))
                detected.Add((harness, location));

        if (detected.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No supported harnesses detected. Nothing to do.[/]");
            return true; // empty targets, exit 0
        }

        var interactive = !opts.NonInteractive && !opts.All && AnsiConsole.Profile.Capabilities.Interactive;
        targets = interactive ? PromptForHarnesses(detected) : detected;
        return true;
    }

    private static List<(IHarness harness, HarnessLocation location)> PromptForHarnesses(
        List<(IHarness harness, HarnessLocation location)> detected)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title("Install protostar capture hooks into which harness(es)?")
            .NotRequired()
            .InstructionsText("[grey](space to toggle, enter to confirm)[/]");
        foreach (var (harness, _) in detected)
            prompt.AddChoice(harness.DisplayName).Select();

        var chosen = AnsiConsole.Prompt(prompt);
        return detected.Where(t => chosen.Contains(t.harness.DisplayName)).ToList();
    }

    private static void Report(IHarness harness, HookChangeSet result, bool dryRun)
    {
        var tag = dryRun ? "[grey](dry-run)[/] " : string.Empty;
        AnsiConsole.MarkupLine(
            $"{tag}[aqua]{Markup.Escape(harness.DisplayName)}[/]: {Markup.Escape(result.Detail)} [grey]({Markup.Escape(result.SettingsPath)})[/]");
    }
}
