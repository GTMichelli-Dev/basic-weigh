using System.Collections.Concurrent;
using GateControllerService.Models;

namespace GateControllerService.Services;

/// <summary>
/// Holds the open gates and decides when each one closes.
///
/// A cycle starts when a ticket finishes on the gate's scale, and ends on
/// whichever of these comes first:
///   * the scale reads below the gate's release threshold — the truck has
///     driven off, which is the normal case; or
///   * the cycle hits its time limit — the truck parked on the deck, the weight
///     feed died, or the web app never sent another reading.
///
/// The timeout is not a nicety. Weight is the only signal that says "the truck
/// left", and it arrives over a network from another process, so it can simply
/// stop. Without the clock a single dropped feed would hold a gate open
/// indefinitely.
/// </summary>
public sealed class GateCycleManager
{
    private readonly GpioOutputs _outputs;
    private readonly ILogger<GateCycleManager> _log;

    /// <summary>Open cycles keyed by GateId.</summary>
    private readonly ConcurrentDictionary<string, Cycle> _open = new();

    public GateCycleManager(GpioOutputs outputs, ILogger<GateCycleManager> log)
    {
        _outputs = outputs;
        _log = log;
    }

    private sealed class Cycle
    {
        public required GateConfigEntity Gate { get; init; }
        public required DateTime OpenedUtc { get; init; }
        public required string Ticket { get; init; }

        /// <summary>
        /// Set once the scale has been seen loaded during this cycle. Until then
        /// a light deck cannot close the gate: the truck may not have pulled on
        /// yet, and closing on the reading that is already there would slam the
        /// gate the instant it opened.
        /// </summary>
        public bool SawLoaded { get; set; }
    }

    public IReadOnlyCollection<string> OpenGateIds => _open.Keys.ToList();

    /// <summary>
    /// Energise a gate for a finished ticket. Re-triggering an already-open gate
    /// restarts its clock rather than opening a second cycle — two trucks close
    /// tickets back to back at a busy scale and the gate should simply stay open.
    /// </summary>
    public void Open(GateConfigEntity gate, string ticket)
    {
        var existing = _open.TryGetValue(gate.GateId, out var c) ? c : null;
        if (existing != null)
        {
            _open[gate.GateId] = new Cycle
            {
                Gate = gate,
                OpenedUtc = DateTime.UtcNow,
                Ticket = ticket,
                // Keep what the previous cycle learned: the truck that is on the
                // deck right now still has to leave before this closes.
                SawLoaded = existing.SawLoaded
            };
            _log.LogInformation("Gate {Gate}: still open, clock restarted for ticket {Ticket}", gate.GateId, ticket);
            return;
        }

        _open[gate.GateId] = new Cycle { Gate = gate, OpenedUtc = DateTime.UtcNow, Ticket = ticket, SawLoaded = false };
        _outputs.Write(gate.GatePin, true, gate.InvertOutputs);
        _outputs.Write(gate.LightPin, true, gate.InvertOutputs);
        _log.LogInformation(
            "Gate {Gate}: OPEN for ticket {Ticket} (gate pin {GatePin}, light pin {LightPin}, closes under {Lb} lb or after {Secs}s)",
            gate.GateId, ticket, gate.GatePin, gate.LightPin, gate.ReleaseWeightThreshold, gate.MaxOpenSeconds);
    }

    /// <summary>
    /// Feed a scale reading in. Closes any open gate watching this scale once
    /// the deck has been loaded and then goes light again.
    /// </summary>
    public void OnWeight(string hardwareId, int weight)
    {
        foreach (var kvp in _open)
        {
            var cycle = kvp.Value;
            var watching = cycle.Gate.ScaleHardwareId;
            if (string.IsNullOrEmpty(watching) ||
                !string.Equals(watching, hardwareId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (weight >= cycle.Gate.ReleaseWeightThreshold)
            {
                cycle.SawLoaded = true;
                continue;
            }

            if (!cycle.SawLoaded) continue;   // never had a truck on it yet
            Close(kvp.Key, $"scale read {weight} lb");
        }
    }

    /// <summary>
    /// Close anything that has run out of time. Called on a timer by the worker.
    /// </summary>
    public void CloseExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _open)
        {
            var age = now - kvp.Value.OpenedUtc;
            if (age.TotalSeconds >= kvp.Value.Gate.MaxOpenSeconds)
                Close(kvp.Key, $"time limit of {kvp.Value.Gate.MaxOpenSeconds}s reached");
        }
    }

    /// <summary>Release a gate and forget the cycle.</summary>
    public void Close(string gateId, string why)
    {
        if (!_open.TryRemove(gateId, out var cycle)) return;
        _outputs.Write(cycle.Gate.GatePin, false, cycle.Gate.InvertOutputs);
        _outputs.Write(cycle.Gate.LightPin, false, cycle.Gate.InvertOutputs);
        _log.LogInformation("Gate {Gate}: CLOSED after {Secs:F0}s — {Why} (ticket {Ticket})",
            gateId, (DateTime.UtcNow - cycle.OpenedUtc).TotalSeconds, why, cycle.Ticket);
    }

    /// <summary>Release everything — used on shutdown.</summary>
    public void CloseAll(string why)
    {
        foreach (var gateId in _open.Keys) Close(gateId, why);
    }

    /// <summary>Does this weighment open that gate?</summary>
    public static bool TriggerMatches(GateConfigEntity gate, string direction) =>
        gate.TriggerOn switch
        {
            "Both" => true,
            "WeighIn" => direction.Equals("weighin", StringComparison.OrdinalIgnoreCase),
            _ => direction.Equals("weighout", StringComparison.OrdinalIgnoreCase),
        };
}
