namespace RfidReaderService.Services;

/// <summary>
/// Singleton signal used to restart the worker's read loops when settings or
/// reader configuration change.
/// </summary>
public class RestartSignal
{
    private CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public void TriggerRestart()
    {
        var old = _cts;
        _cts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
    }
}

/// <summary>Singleton signal to re-announce the reader list without a restart.</summary>
public class AnnounceSignal
{
    public event Action? OnAnnounceRequested;

    public void TriggerAnnounce() => OnAnnounceRequested?.Invoke();
}
