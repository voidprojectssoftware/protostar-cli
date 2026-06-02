using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Protostar.Cli.Auth;

/// <summary>
/// A one-shot loopback HTTP listener for the OAuth redirect. Binds an ephemeral 127.0.0.1 port,
/// waits for the <c>/callback</c> request, hands back the authorization code (or error), and serves
/// a small "you can close this tab" page. Loopback redirects with a dynamic port are the
/// recommended pattern for native apps (RFC 8252); the registry ignores the port when matching.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public LoopbackServer()
    {
        Port = FreeLoopbackPort();
        RedirectUri = $"http://127.0.0.1:{Port}{AuthConstants.CallbackPath}";
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
    }

    public int Port { get; }

    public string RedirectUri { get; }

    public async Task<CallbackResult> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() =>
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (!string.Equals(context.Request.Url!.AbsolutePath, AuthConstants.CallbackPath, StringComparison.Ordinal))
            {
                await RespondAsync(context, 404, "Not found.");
                continue;
            }

            var query = ParseQuery(context.Request.Url!.Query);
            query.TryGetValue("error", out var error);

            var message = error is not null
                ? "protostar sign-in failed. You can close this tab and return to the terminal."
                : "protostar sign-in complete. You can close this tab and return to the terminal.";
            await RespondAsync(context, 200, message);

            query.TryGetValue("code", out var code);
            query.TryGetValue("state", out var state);
            query.TryGetValue("error_description", out var description);
            return new CallbackResult(code, state, error, description);
        }
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

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();
}

internal sealed record CallbackResult(string? Code, string? State, string? Error, string? ErrorDescription);
