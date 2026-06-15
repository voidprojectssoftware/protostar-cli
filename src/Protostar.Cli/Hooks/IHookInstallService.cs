namespace Protostar.Cli.Hooks;

/// <summary>
/// Orchestrates capture-hook install/remove across harnesses. Injected into the install, uninstall, and
/// install-hooks commands so the file-touching logic can be faked in tests. See
/// <see cref="HookInstallService"/> for the production implementation.
/// </summary>
internal interface IHookInstallService
{
    /// <summary>Install capture hooks into the resolved targets, narrowing them via <paramref name="select"/> when given.</summary>
    HookRunResult Install(HookInstallOptions opts, HarnessSelector? select = null);

    /// <summary>Remove capture hooks from the resolved targets, narrowing them via <paramref name="select"/> when given.</summary>
    HookRunResult Uninstall(HookInstallOptions opts, HarnessSelector? select = null);
}
