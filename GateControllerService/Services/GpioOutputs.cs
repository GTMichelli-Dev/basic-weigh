using System.Collections.Concurrent;
using System.Device.Gpio;

namespace GateControllerService.Services;

/// <summary>
/// Drives the gate and light relays on the Pi's GPIO header.
///
/// Unlike the detector inputs on the reader service, this one is not allowed to
/// fail quietly in both directions: failing to OPEN a gate is an inconvenience,
/// but failing to CLOSE one leaves a barrier up or a light on with nobody
/// watching. So every close is attempted even if the matching open failed, the
/// pins are driven to their released state on shutdown, and a write that throws
/// is logged loudly rather than swallowed.
///
/// On a machine with no GPIO (a dev box, a Windows scale house) the whole thing
/// degrades to logging what it would have done, so the release logic can still
/// be exercised without hardware.
/// </summary>
public sealed class GpioOutputs : IDisposable
{
    private readonly ILogger<GpioOutputs> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, bool> _opened = new();

    private GpioController? _controller;
    private bool _unavailable;

    public GpioOutputs(ILogger<GpioOutputs> log) => _log = log;

    /// <summary>True when there is no GPIO here, so callers can say so once at
    /// startup instead of pretending the hardware is live.</summary>
    public bool HardwareAvailable => !_unavailable;

    /// <summary>
    /// Energise or release one output. `on` is the logical state — "gate open",
    /// "light lit" — and the inversion for active-low relay boards is applied
    /// here so no caller has to think about wiring.
    /// </summary>
    public void Write(int? pin, bool on, bool invert)
    {
        if (pin == null || _unavailable) return;

        try
        {
            lock (_gate)
            {
                var controller = _controller ??= new GpioController();

                if (!_opened.ContainsKey(pin.Value))
                {
                    controller.OpenPin(pin.Value, PinMode.Output);
                    _opened[pin.Value] = true;
                    // Start from released so a service restart mid-cycle cannot
                    // leave a gate latched open by whatever the pin held.
                    controller.Write(pin.Value, invert ? PinValue.High : PinValue.Low);
                    _log.LogInformation("Gate output: opened GPIO {Pin} as output (released)", pin.Value);
                }

                var value = on
                    ? (invert ? PinValue.Low : PinValue.High)
                    : (invert ? PinValue.High : PinValue.Low);
                controller.Write(pin.Value, value);
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or TypeInitializationException or DllNotFoundException)
        {
            _unavailable = true;
            _log.LogWarning("No GPIO on this machine — gate outputs are simulated only. ({Msg})", ex.Message);
        }
        catch (Exception ex)
        {
            // Loud: a failed write here means a gate did not move when the
            // control logic believes it did.
            _log.LogError(ex, "Gate output: failed to drive GPIO {Pin} {State}", pin, on ? "ON" : "OFF");
        }
    }


    public void Dispose()
    {
        lock (_gate)
        {
            if (_controller == null) return;
            foreach (var pin in _opened.Keys)
            {
                try { _controller.ClosePin(pin); } catch { /* shutting down */ }
            }
            try { _controller.Dispose(); } catch { /* shutting down */ }
            _controller = null;
        }
    }
}
