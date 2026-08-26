namespace GateControllerService.Services;

/// <summary>
/// Wakes the worker so it reconnects — triggered when settings change through
/// the API. Same shape as the other Pi services.
/// </summary>
public class RestartSignal
{
    private readonly ManualResetEventSlim _signal = new(false);

    public void TriggerRestart() => _signal.Set();
    public bool WaitForRestart(TimeSpan timeout) => _signal.Wait(timeout);
    public void Reset() => _signal.Reset();
}
