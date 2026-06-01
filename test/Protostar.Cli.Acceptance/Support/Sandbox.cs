namespace Protostar.Cli.Acceptance.Support;

/// <summary>
/// A throwaway directory tree for install/uninstall scenarios. Scenarios install into
/// <see cref="InstallDir"/> with <c>--no-modify-path</c> so a test never touches the real
/// machine's filesystem outside this sandbox or its PATH. Disposed at the end of each scenario.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "protostar-acceptance", Guid.NewGuid().ToString("n"));
        InstallDir = Path.Combine(Root, "bin");
        Directory.CreateDirectory(Root);
    }

    /// <summary>Root of the sandbox; removed on dispose.</summary>
    public string Root { get; }

    /// <summary>Directory passed to <c>protostar install --dir</c>.</summary>
    public string InstallDir { get; }

    /// <summary>Where an install is expected to place the binary.</summary>
    public string InstalledBinary =>
        Path.Combine(InstallDir, OperatingSystem.IsWindows() ? "protostar.exe" : "protostar");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a leaked temp dir must never fail a test.
        }
    }
}
