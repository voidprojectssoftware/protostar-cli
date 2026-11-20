using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Protostar.Cli.Acceptance.Support;

/// <summary>
/// A loopback "registry" that answers <c>GET /v1/meta</c> with a chosen document and every other request with 500,
/// recording the paths asked for. One per scenario (Reqnroll resolves and disposes it).
/// </summary>
/// <remarks>
/// Plain HTTP over a <see cref="TcpListener"/>, one request per connection (<c>Connection: close</c>), so it needs no
/// certificate and no URL reservation on Windows. Enough to show how a command treats a registry's capabilities
/// without running one.
/// </remarks>
public sealed class StubRegistry : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _requests = [];
    private string _meta = "{}";
    private bool _started;

    /// <summary>The registry's base URL.</summary>
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>The request lines received, such as <c>GET /v1/meta HTTP/1.1</c>, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>Starts answering <c>/v1/meta</c> with <paramref name="meta"/>.</summary>
    public void Start(string meta)
    {
        _meta = meta;
        if (_started)
            return;
        _listener.Start();
        _started = true;
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token)))
                {
                    // Headers; the stub needs none of them.
                }

                lock (_requests)
                    _requests.Add(requestLine);
                var isMeta = requestLine.StartsWith("GET /v1/meta ", StringComparison.Ordinal);
                var body = Encoding.UTF8.GetBytes(isMeta ? _meta : """{"title":"The stub registry answers only /v1/meta."}""");
                var head = $"HTTP/1.1 {(isMeta ? "200 OK" : "500 Internal Server Error")}\r\n" +
                           $"Content-Type: {(isMeta ? "application/json" : "application/problem+json")}\r\n" +
                           $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // A client that went away; nothing to answer.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
