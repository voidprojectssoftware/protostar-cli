using CliWrap;
using CliWrap.Buffered;

namespace Protostar.Cli.Acceptance.Support;

/// <summary>
/// Locates the built protostar binary and runs it as a child process, the way a user would.
/// Output capture is buffered; stdout/stderr and the exit code are returned for assertions.
/// </summary>
public static class CliRunner
{
    private static readonly Lazy<string> LazyBinary = new(ResolveBinary);

    // Redirect protostar's config dir at a throwaway temp folder so acceptance runs never read or
    // write the real ~/.protostar (credentials, etc.).
    private static readonly Lazy<string> LazyConfigDir = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "protostar-accept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    });

    /// <summary>Absolute path to the protostar binary under test.</summary>
    public static string BinaryPath => LazyBinary.Value;

    public static async Task<BufferedCommandResult> RunAsync(IEnumerable<string> args)
    {
        return await CliWrap.Cli.Wrap(BinaryPath)
            .WithArguments(args)
            .WithEnvironmentVariables(env => env.Set("PROTOSTAR_CONFIG_DIR", LazyConfigDir.Value).Build())
            // Non-zero exits are expected in some scenarios; assert on them rather than throwing.
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
    }

    private static string ResolveBinary()
    {
        // CI publishes a self-contained binary and points us at it.
        var fromEnv = Environment.GetEnvironmentVariable("PROTOSTAR_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        // Local/dev: the ProjectReference builds the CLI; find its apphost under src/Protostar.Cli/bin.
        var repoRoot = FindRepoRoot();
        var exeName = OperatingSystem.IsWindows() ? "protostar.exe" : "protostar";
        var binDir = Path.Combine(repoRoot, "src", "Protostar.Cli", "bin");
        if (!Directory.Exists(binDir))
            throw new FileNotFoundException(
                $"protostar build output not found under '{binDir}'. Build the solution first, or set PROTOSTAR_BIN.");

        var match = Directory.EnumerateFiles(binDir, exeName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return match ?? throw new FileNotFoundException(
            $"Could not find '{exeName}' under '{binDir}'. Build the solution first, or set PROTOSTAR_BIN.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "protostar.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the repo root (protostar.sln) from the test output directory.");
    }
}
