using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Hooks;

/// <summary>
/// The console face of <see cref="HookInstallService"/>: the interactive harness picker (the service's
/// injected selector) and the rendering of a <see cref="HookRunResult"/>. Kept out of the service so
/// the orchestration stays pure and testable, and shared by every command that installs or removes
/// hooks (<c>install-hooks</c>, <c>install</c>, <c>uninstall</c>) so the output is written one way.
/// </summary>
internal static class HookInstallPresenter
{
    // Multiselect over the detected harnesses, all pre-selected. Used as the service's HarnessSelector
    // only when the command is running interactively.
    public static IReadOnlyList<HarnessTarget> Prompt(IReadOnlyList<HarnessTarget> detected)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title("Install protostar capture hooks into which harness(es)?")
            .NotRequired()
            .InstructionsText("[grey](space to toggle, enter to confirm)[/]");
        foreach (var target in detected)
            prompt.AddChoice(target.Harness.DisplayName).Select();

        var chosen = AnsiConsole.Prompt(prompt);
        return detected.Where(t => chosen.Contains(t.Harness.DisplayName)).ToList();
    }

    /// <summary>Render a run's outcome and return the process exit code (0 success, 1 any failure).</summary>
    public static int Render(HookRunResult result, bool dryRun, IHarnessCatalog catalog)
    {
        switch (result.Failure)
        {
            case HookRunFailure.UnknownHarness:
                AnsiConsole.MarkupLine(
                    $"[red]Unknown harness '{Markup.Escape(result.OffendingHarnessId!)}'.[/] " +
                    $"Known: {catalog.KnownIds}");
                return 1;
            case HookRunFailure.Unsupported:
                AnsiConsole.MarkupLine(
                    $"[red]Harness '{Markup.Escape(result.OffendingHarnessId!)}' does not support capture hooks.[/]");
                return 1;
            case HookRunFailure.MissingExecutable:
                AnsiConsole.MarkupLine("[red]Could not determine the protostar binary for the hook command.[/]");
                return 1;
        }

        if (result.Results.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No supported harnesses detected. Nothing to do.[/]");
            return 0;
        }

        var failed = false;
        foreach (var entry in result.Results)
        {
            if (entry.Failed)
            {
                failed = true;
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(entry.Harness.DisplayName)}: {Markup.Escape(entry.Error!)}[/]");
                continue;
            }

            var tag = dryRun ? "[grey](dry-run)[/] " : string.Empty;
            AnsiConsole.MarkupLine(
                $"{tag}[aqua]{Markup.Escape(entry.Harness.DisplayName)}[/]: " +
                $"{Markup.Escape(entry.Change!.Detail)} [grey]({Markup.Escape(entry.Change.SettingsPath)})[/]");
        }

        return failed ? 1 : 0;
    }
}
