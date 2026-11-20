using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Spectre.Console;

namespace Protostar.Cli.Setup;

/// <summary>The console face of <see cref="ISetupService"/>: one line per step as it finishes, the backup prompt and progress, and the closing line.</summary>
/// <remarks>
/// Each step line reads "Label: done", "already done", "skipped" or "failed", then what happened. Failure wording comes
/// from the presenters of the commands each step shares a service with, so a failure reads the same either way.
/// </remarks>
internal static class SetupPresenter
{
    private const int MaxSkillLines = 20;

    private const string RegistryUnreachable = "Needs the registry, which could not be reached.";

    private const string HooksNote = "[grey]The capture hooks back up new activity from here on.[/]";

    /// <summary>The interaction a setup run reports to, printing each step and prompting on this console.</summary>
    /// <param name="catalog">Names harnesses in failure messages.</param>
    /// <param name="pushCommand">The exact command that backs up sessions later, printed when the backup is skipped.</param>
    public static SetupInteraction Interaction(IHarnessCatalog catalog, string pushCommand) => new(
        step => Step(step, catalog, pushCommand),
        ConfirmBackup,
        RunBackupAsync(catalog));

    /// <summary>The <c>sessions push</c> command matching the options setup ran with.</summary>
    public static string PushCommand(string? registry, string? harnessHome) =>
        "protostar sessions push" +
        (registry is { Length: > 0 } ? $" --registry {Quote(registry)}" : string.Empty) +
        (harnessHome is { Length: > 0 } ? $" --harness-home {Quote(harnessHome)}" : string.Empty);

    /// <summary>Prints the closing line and returns the exit code: 0 when no step failed, otherwise 1.</summary>
    public static int Render(SetupResult result)
    {
        if (result.InvalidRegistry is { } invalid)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(invalid)}[/]");
            return 1;
        }

        var failed = result.Steps.Count(s => s.Outcome == SetupOutcome.Failed);
        // Configuration is skipped on every run until it exists, so it does not make a run "partly set up".
        var skipped = result.Steps.Count(s => s.Outcome == SetupOutcome.Skipped && s.Skip != SetupSkip.NotAvailableYet);
        AnsiConsole.MarkupLine(
            failed > 0 ? $"[red]Setup finished with {failed} failed step{(failed == 1 ? "" : "s")}.[/] Fix it and run `protostar setup` again; finished steps are not repeated."
            : skipped > 0 ? $"[yellow]Setup finished; {skipped} step{(skipped == 1 ? " was" : "s were")} skipped.[/] See above for how to finish {(skipped == 1 ? "it" : "them")}."
            : result.NothingChanged ? "[green]This machine is already set up.[/] Nothing changed."
            : "[green]This machine is set up.[/] Running `protostar setup` again changes nothing.");
        return failed > 0 ? 1 : 0;
    }

    private static void Step(SetupStepResult step, IHarnessCatalog catalog, string pushCommand)
    {
        var label = step.Step switch
        {
            SetupStep.SignIn => "Sign-in",
            SetupStep.Hooks => "Hooks",
            SetupStep.Skills => "Skills",
            SetupStep.Configuration => "Configuration",
            _ => "Sessions",
        };
        var outcome = step.Outcome switch
        {
            SetupOutcome.Done => "[green]done[/]",
            SetupOutcome.AlreadyDone => "[green]already done[/]",
            SetupOutcome.Skipped => "[yellow]skipped[/]",
            _ => "[red]failed[/]",
        };

        var lines = step.Step switch
        {
            SetupStep.SignIn => SignIn(step),
            SetupStep.Hooks => Hooks(step, catalog),
            SetupStep.Skills => Skills(step, catalog),
            SetupStep.Configuration => ["Not available yet: syncing harness configuration is still to come."],
            _ => Sessions(step, catalog, pushCommand),
        };

        AnsiConsole.MarkupLine($"[bold]{label}[/]: {outcome}. {lines[0]}");
        foreach (var line in lines.Skip(1))
            AnsiConsole.MarkupLine($"  {line}");

        // Written raw, like the sign-in URL, so a long command is never wrapped and stays copy-pasteable.
        if (step.Step == SetupStep.Sessions && step.Outcome == SetupOutcome.Skipped)
        {
            AnsiConsole.MarkupLine("  Back them up with:");
            Console.WriteLine($"    {pushCommand}");
            AnsiConsole.MarkupLine($"  {HooksNote}");
        }
    }

    private static List<string> SignIn(SetupStepResult step)
    {
        var check = step.SessionCheck!;
        var authority = Markup.Escape(check.Registry.GetLeftPart(UriPartial.Authority));
        if (step.SignIn is { } signIn)
        {
            if (!signIn.Succeeded)
                return [.. SignInPresenter.Problem(signIn)];
            return [$"Signed in to [grey]{authority}[/]{As(signIn.Login)}."];
        }

        return check.Verification == SessionVerification.Unreachable
            ? [$"[red]Could not reach the registry at[/] [grey]{authority}[/] to check your session."]
            : [$"Signed in to [grey]{authority}[/]{As(check.Login)}."];
    }

    private static List<string> Hooks(SetupStepResult step, IHarnessCatalog catalog)
    {
        var result = step.Hooks!;
        if (HookInstallPresenter.FailureMessage(result, catalog) is { } failure)
            return [failure];
        if (result.Results.Count == 0)
            return ["No supported harness was found on this machine."];

        return result.Results
            .Select(r => r.Failed
                ? $"[aqua]{Markup.Escape(r.Harness.DisplayName)}[/]: [red]{Markup.Escape(r.Error!)}[/]"
                : $"[aqua]{Markup.Escape(r.Harness.DisplayName)}[/]: {Markup.Escape(r.Change!.Detail)} [grey]({Markup.Escape(r.Change.SettingsPath)})[/]")
            .ToList();
    }

    private static List<string> Skills(SetupStepResult step, IHarnessCatalog catalog)
    {
        if (step.Skip == SetupSkip.NotSignedIn)
            return ["Needs sign-in."];
        if (step.Skip == SetupSkip.RegistryUnreachable)
            return [RegistryUnreachable];

        // The push ran first; its line comes first, then the pull's.
        var (pushed, pushLines) = Pushed(step.SkillPush!, catalog);
        var result = step.Skills!;
        if (SkillPullPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog) is { } failure)
            return [$"{pushed} Pull: [red]{Markup.Escape(failure)}[/]", .. pushLines];

        int Of(SkillPullStatus status) => result.Entries.Count(e => e.Status == status);
        var summary = result.Entries.Count == 0
            ? "Pulled: no skills in the registry yet."
            : $"Pulled: {Of(SkillPullStatus.Installed)} installed, {Of(SkillPullStatus.Updated)} updated, {Of(SkillPullStatus.UpToDate)} already up to date" +
            (Of(SkillPullStatus.LocalChanges) is > 0 and var held ? $", [yellow]{held} kept because of local changes[/]" : "") +
            (Of(SkillPullStatus.ChangedBoth) is > 0 and var both ? $", [yellow]{both} kept because they changed here and in the registry[/]" : "") +
            (Of(SkillPullStatus.Conflict) is > 0 and var conflicts ? $", [red]{conflicts} skipped as conflicts[/]" : "") +
            (Of(SkillPullStatus.Failed) is > 0 and var failed ? $", [red]{failed} failed[/]" : "") +
            (Of(SkillPullStatus.OtherProject) is > 0 and var elsewhere ? $", [grey]{elsewhere} left for other projects' checkouts[/]" : "") +
            (Of(SkillPullStatus.FollowedRename) is > 0 and var followed ? $", {followed} renamed here to follow the registry" : "") +
            (Of(SkillPullStatus.RemovedInRegistry) is > 0 and var gone ? $", [yellow]{gone} kept although deleted or renamed in the registry[/]" : "") + ".";

        // Condensed: one line per skill that changed or needs attention. Up-to-date and other projects' skills are only counted.
        var showHarness = result.Entries.Select(e => e.Harness.Id).Distinct().Count() > 1;
        var notable = result.Entries.Where(e => e.Status is not (SkillPullStatus.UpToDate or SkillPullStatus.OtherProject)).ToList();
        var lines = new List<string> { $"{pushed} {summary}" };
        lines.AddRange(pushLines);
        lines.AddRange(notable.Take(MaxSkillLines).Select(e =>
            (showHarness ? $"[grey]{Markup.Escape(e.Harness.Id)}[/] " : "") +
            Markup.Escape(e.Project is { } project ? $"{e.Name} ({project})" : e.Name) + ": " + SkillPullPresenter.Describe(e)));
        if (notable.Count > MaxSkillLines)
            lines.Add($"[grey]... and {notable.Count - MaxSkillLines} more. `protostar skills remote` lists them all.[/]");
        if (Of(SkillPullStatus.LocalChanges) > 0)
            lines.Add("[grey]Back up local changes with `protostar skills push`, or overwrite them with `protostar skills pull --force`.[/]");
        if (Of(SkillPullStatus.ChangedBoth) > 0 || step.SkillPush!.Entries.Any(e => e.Outcome.Status == SkillPushStatus.Stale))
            lines.Add("[grey]`protostar skills remote` shows how to keep yours, theirs, or both.[/]");
        return lines;
    }

    // The push's summary sentence, and one line per skill it pushed or could not back up. A skill refused as changed here
    // and in the registry, or left out as deleted or renamed in the registry, is only counted: the pull that follows lists
    // it, as kept or as renamed to follow.
    private static (string Summary, List<string> Lines) Pushed(SkillPushRunResult push, IHarnessCatalog catalog)
    {
        if (SkillPushPresenter.FailureMessage(push, catalog) is { } failure)
            return ($"Push: [red]{Markup.Escape(failure)}[/]", []);
        if (push.Entries.Count == 0)
            return ("Pushed: no skills on this machine.", []);

        int Of(SkillPushStatus status) => push.Entries.Count(e => e.Outcome.Status == status);
        var problems = push.Entries.Count(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Conflict or SkillPushStatus.Failed);
        var summary = $"Pushed: {Of(SkillPushStatus.Created)} backed up, {Of(SkillPushStatus.Unchanged)} already backed up" +
            (Of(SkillPushStatus.Stale) is > 0 and var stale ? $", [yellow]{stale} not pushed because they changed here and in the registry[/]" : "") +
            (Of(SkillPushStatus.Removed) is > 0 and var removed ? $", [yellow]{removed} not pushed because they were deleted or renamed in the registry[/]" : "") +
            (problems > 0 ? $", [red]{problems} not backed up[/]" : "") + ".";

        var notable = push.Entries
            .Where(e => e.Outcome.Status is not (SkillPushStatus.Unchanged or SkillPushStatus.Skipped or SkillPushStatus.Stale or SkillPushStatus.Removed))
            .ToList();
        var lines = notable.Take(MaxSkillLines).Select(e =>
            Markup.Escape(e.Outcome.ProjectName is { } project ? $"{e.Skill.Name} ({project})" : e.Skill.Name) + ": " +
            SkillPushPresenter.Describe(e.Outcome)).ToList();
        if (notable.Count > MaxSkillLines)
            lines.Add($"[grey]... and {notable.Count - MaxSkillLines} more pushed. `protostar skills remote` lists them all.[/]");
        return (summary, lines);
    }

    private static List<string> Sessions(SetupStepResult step, IHarnessCatalog catalog, string pushCommand)
    {
        var status = step.SessionStatus!;
        if (SessionsPresenter.FailureMessage(status.Failure, status.Detail, status.Registry, catalog) is { } failure)
            return [$"[red]{Markup.Escape(failure)}[/]"];

        var backlog = step.Backlog!;
        if (step.Backup is { } backup)
        {
            // The backup's own summary was printed when it finished; this line only places it among the steps.
            return backup.Complete
                ? [$"Backed up {Count(backlog.Sessions, "session")} ({SessionsPresenter.Bytes(backlog.Bytes)}).", HooksNote]
                : backup.Stopped
                    ? [$"Stopped part way. Nothing is lost: run `{Markup.Escape(pushCommand)}` to carry on.", HooksNote]
                    : [$"Not everything was backed up. Run `{Markup.Escape(pushCommand)}` to retry.", HooksNote];
        }

        if (backlog.Transcripts == 0)
            return [status.Transcripts.Count == 0 ? "No session transcripts on this machine yet." : $"All {Count(status.Transcripts.Count, "transcript")} backed up.", HooksNote];

        var pending = $"{Pending(backlog)}.";
        var reason = step.Skip switch
        {
            SetupSkip.NotSignedIn => "Needs sign-in.",
            SetupSkip.RegistryUnreachable => RegistryUnreachable,
            SetupSkip.Declined => "You chose not to back them up now.",
            _ => "No terminal to ask on, and --yes was not given.",
        };
        return [$"{reason} {pending}"];
    }

    private static bool ConfirmBackup(SessionBacklog backlog)
    {
        AnsiConsole.MarkupLine($"{Pending(backlog)}.");
        return AnsiConsole.Confirm("Back up now?", defaultValue: false);
    }

    private static Func<SessionBacklog, Func<IProgress<SessionSyncProgress>?, Task<SessionSyncResult>>, Task<SessionSyncResult>> RunBackupAsync(
        IHarnessCatalog catalog) => async (backlog, sync) =>
    {
        AnsiConsole.MarkupLine($"{Pending(backlog)}. Backing them up now, newest first; Ctrl+C stops cleanly and the next push carries on.");
        var result = await SessionsPresenter.WithProgressAsync(sync);
        SessionsPresenter.RenderSync(result, catalog);
        return result;
    };

    private static string Pending(SessionBacklog backlog) =>
        $"{Count(backlog.Sessions, "session")}, {SessionsPresenter.Bytes(backlog.Bytes)} not backed up yet";

    private static string As(string? login) => login is { Length: > 0 } ? $" as [aqua]{Markup.Escape(login)}[/]" : string.Empty;

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
