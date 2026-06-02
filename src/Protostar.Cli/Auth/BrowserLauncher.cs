using System.Diagnostics;

namespace Protostar.Cli.Auth;

/// <summary>Opens a URL in the user's default browser, cross-platform. Returns false on failure.</summary>
internal static class BrowserLauncher
{
    public static bool TryOpen(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
