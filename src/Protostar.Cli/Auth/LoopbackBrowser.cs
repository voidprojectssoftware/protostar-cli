using System.Net;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;
using Spectre.Console;

namespace Protostar.Cli.Auth;

/// <summary>
/// The <see cref="IBrowser"/> OidcClient drives for the native-app loopback flow (RFC 8252). It binds
/// the ephemeral 127.0.0.1 port chosen at construction, opens the system browser at the authorize URL
/// (or prints it when <c>--no-browser</c> was passed), waits for the OAuth redirect to
/// <c>/callback</c>, serves a "you can close this tab" page, and hands the full callback URL back to
/// OidcClient for the PKCE code exchange. OpenIddict ignores the port when matching the registered
/// loopback redirect URI, so any free port works.
/// </summary>
internal sealed class LoopbackBrowser : IBrowser
{
    private readonly bool _openBrowser;

    public LoopbackBrowser(bool openBrowser)
    {
        _openBrowser = openBrowser;
        Port = FreeLoopbackPort();
        RedirectUri = $"http://127.0.0.1:{Port}{AuthConstants.CallbackPath}";
    }

    public int Port { get; }

    /// <summary>The redirect URI OidcClient must advertise; the listener answers on the same port.</summary>
    public string RedirectUri { get; }

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();

        // Skip the launch attempt on a display-less session: xdg-open can "succeed" with no browser
        // actually shown, which would otherwise hide the URL and strand a headless user.
        var opened = _openBrowser && !IsHeadless() && BrowserLauncher.TryOpen(options.StartUrl);

        AnsiConsole.MarkupLine(opened
            ? "Opening your browser to sign in. If it doesn't open, use the URL below, then return here."
            : "Open this URL to sign in:");

        // Always print the URL raw (not through Spectre) so it is never word-wrapped, stays
        // copy-pasteable, and is available even when we did open a browser.
        Console.WriteLine(options.StartUrl);

        await using var registration = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { /* already stopped */ }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.Timeout,
                    Error = "Timed out waiting for the browser sign-in.",
                };
            }

            if (!string.Equals(context.Request.Url!.AbsolutePath, AuthConstants.CallbackPath, StringComparison.Ordinal))
            {
                await RespondAsync(context, 404, "Not found.");
                continue;
            }

            var failed = context.Request.Url!.Query.Contains("error=", StringComparison.Ordinal);
            await RespondAsync(context, 200, failed
                ? "protostar sign-in failed. You can close this tab and return to the terminal."
                : "protostar sign-in complete. You can close this tab and return to the terminal.");

            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = context.Request.Url!.AbsoluteUri,
            };
        }
    }

    // True when no GUI session is available to open a browser. Only the xdg-open family (anything
    // that isn't Windows or macOS) depends on an X11/Wayland display; on Windows/macOS the system
    // launcher works without these variables, so they are never treated as headless here.
    private static bool IsHeadless()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return false;

        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><body style=\"font-family:sans-serif;padding:2rem\"><p>{message}</p></body></html>");
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
