using Protostar.Cli.Harness;

namespace Protostar.Cli.Hooks;

/// <summary>One harness to act on, with its resolved config location.</summary>
internal sealed record HarnessTarget(IHarness Harness, HarnessLocation Location);

/// <summary>Narrow the detected targets, e.g. via an interactive prompt. Returns the chosen subset.</summary>
internal delegate IReadOnlyList<HarnessTarget> HarnessSelector(IReadOnlyList<HarnessTarget> detected);

/// <summary>Why target resolution stopped before any harness was touched.</summary>
internal enum HookRunFailure
{
    /// <summary>No failure; <see cref="HookRunResult.Results"/> is authoritative.</summary>
    None,
    /// <summary>An explicit <c>--harness</c> id matched no registered harness.</summary>
    UnknownHarness,
    /// <summary>The named harness exists but does not support capture hooks.</summary>
    Unsupported,
    /// <summary>Installing, but the protostar binary the hooks would invoke could not be located.</summary>
    MissingExecutable,
}

/// <summary>Outcome of installing or removing hooks against one harness: a change set, or an error.</summary>
internal sealed record HookApplyResult(IHarness Harness, HookChangeSet? Change, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Result of a hook run. Either a pre-flight <see cref="Failure"/> (bad id, unsupported harness,
/// missing binary) that touched nothing, or per-target <see cref="Results"/> once application ran.
/// Carries no console markup; the caller renders it.
/// </summary>
internal sealed record HookRunResult(
    HookRunFailure Failure,
    string? OffendingHarnessId,
    IReadOnlyList<HookApplyResult> Results)
{
    public static HookRunResult Fail(HookRunFailure failure, string? offendingId = null) =>
        new(failure, offendingId, []);

    public static readonly HookRunResult Nothing = new(HookRunFailure.None, null, []);
}

/// <summary>
/// Orchestrates capture-hook install/remove across harnesses: resolve targets, apply, and return the
/// per-target outcome. Shared by the standalone <c>install-hooks</c> command and the
/// <c>install</c>/<c>uninstall</c> lifecycle so there is one code path, not a command invoking another.
/// Pure logic: it returns data and never writes to the console. The one interactive concern (letting a
/// human narrow the detected set) is injected as a <see cref="HarnessSelector"/> so the prompt stays in
/// the presentation layer; non-interactive callers omit it and every detected target is acted on.
/// </summary>
internal sealed class HookInstallService : IHookInstallService
{
    /// <inheritdoc />
    public HookRunResult Install(HookInstallOptions opts, HarnessSelector? select = null) => Run(opts, remove: false, select);

    /// <inheritdoc />
    public HookRunResult Uninstall(HookInstallOptions opts, HarnessSelector? select = null) => Run(opts, remove: true, select);

    private static HookRunResult Run(HookInstallOptions opts, bool remove, HarnessSelector? select)
    {
        if (!TryResolveTargets(opts, out var targets, out var failure, out var offendingId))
            return HookRunResult.Fail(failure, offendingId);
        if (targets.Count == 0)
            return HookRunResult.Nothing;

        // Presentation hook: let the caller narrow the detected set (the interactive multiselect).
        // Explicit-id and non-interactive callers pass no selector and act on every detected target.
        if (select is not null)
        {
            targets = select(targets).ToList();
            if (targets.Count == 0)
                return HookRunResult.Nothing;
        }

        string? exePath = null;
        if (!remove)
        {
            exePath = opts.ExePathOverride ?? Environment.ProcessPath;
            if (exePath is null || !File.Exists(exePath))
                return HookRunResult.Fail(HookRunFailure.MissingExecutable);
        }

        var results = new List<HookApplyResult>(targets.Count);
        foreach (var (harness, location) in targets)
        {
            // Resolution guarantees every target supports hooks, so this cast cannot fail.
            var hooks = (IHookCapability)harness;
            try
            {
                var change = remove
                    ? hooks.RemoveHooks(location, opts.DryRun)
                    : hooks.InstallHooks(location, exePath!, opts.DryRun);
                results.Add(new HookApplyResult(harness, change, null));
            }
            catch (Exception ex)
            {
                results.Add(new HookApplyResult(harness, null, ex.Message));
            }
        }

        return new HookRunResult(HookRunFailure.None, null, results);
    }

    // Build the list of targets to act on. Explicit --harness ids are targeted even if not currently
    // present (the user asked for them); otherwise we detect every harness that supports hooks.
    private static bool TryResolveTargets(
        HookInstallOptions opts,
        out List<HarnessTarget> targets,
        out HookRunFailure failure,
        out string? offendingId)
    {
        targets = [];
        failure = HookRunFailure.None;
        offendingId = null;

        if (opts.HarnessIds is { Count: > 0 })
        {
            foreach (var id in opts.HarnessIds)
            {
                var harness = HarnessRegistry.ById(id);
                if (harness is null)
                {
                    failure = HookRunFailure.UnknownHarness;
                    offendingId = id;
                    return false;
                }
                if (harness is not IHookCapability)
                {
                    failure = HookRunFailure.Unsupported;
                    offendingId = id;
                    return false;
                }
                harness.TryLocate(opts.RootOverride, out var location);
                targets.Add(new HarnessTarget(harness, location));
            }
            return true;
        }

        foreach (var harness in HarnessRegistry.All)
            if (harness is IHookCapability && harness.TryLocate(opts.RootOverride, out var location))
                targets.Add(new HarnessTarget(harness, location));

        return true;
    }
}
