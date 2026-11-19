namespace Protostar.Cli.Infrastructure;

/// <summary>A cancellation token that the first Ctrl+C (or <c>SIGINT</c>) cancels, for a command that can stop cleanly.</summary>
/// <remarks>
/// Spectre.Console.Cli does not turn Ctrl+C into cancellation, so without this the runtime ends the process at once,
/// mid-write. Only commands that create one opt in; a second Ctrl+C is left to end the process as usual, so a command
/// that is slow to stop can still be killed. Dispose it when the command ends to restore the default handling.
/// </remarks>
internal sealed class CtrlCCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;

    public CtrlCCancellation(CancellationToken linked)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(linked);
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <summary>Cancelled by the first Ctrl+C, or when the linked token is.</summary>
    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _source.Dispose();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (_source.IsCancellationRequested)
            return;
        e.Cancel = true;
        _source.Cancel();
    }
}
