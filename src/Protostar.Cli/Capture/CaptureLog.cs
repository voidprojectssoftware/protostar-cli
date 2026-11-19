using Protostar.Cli.Auth;

namespace Protostar.Cli.Capture;

/// <summary>Appends lines to <c>~/.protostar/capture.log</c>, where hook and background problems are kept.</summary>
/// <remarks>Hooks and the pushes they start run out of sight, so anything a person should act on is written here.</remarks>
internal static class CaptureLog
{
    /// <summary>Appends <paramref name="line"/>. Never throws: logging is best effort.</summary>
    public static void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(ProtostarPaths.ConfigDir());
            File.AppendAllText(Path.Combine(ProtostarPaths.ConfigDir(), "capture.log"), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; the hook or push must still succeed.
        }
    }
}
