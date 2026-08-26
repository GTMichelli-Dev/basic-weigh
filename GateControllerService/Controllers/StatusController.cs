using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GateControllerService.Data;
using GateControllerService.Models;
using GateControllerService.Services;

namespace GateControllerService.Controllers;

/// <summary>
/// Local API for commissioning a box: check it is alive, edit the gates, and
/// fire an output by hand to confirm the wiring before a truck is involved.
/// </summary>
[ApiController]
[Route("api")]
public class StatusController : ControllerBase
{
    private readonly GateDbContext _db;
    private readonly RestartSignal _restart;
    private readonly GateCycleManager _cycles;
    private readonly GpioOutputs _outputs;

    public StatusController(GateDbContext db, RestartSignal restart, GateCycleManager cycles, GpioOutputs outputs)
    {
        _db = db;
        _restart = restart;
        _cycles = cycles;
        _outputs = outputs;
    }

    /// <summary>Liveness only — the installer polls this to know when the
    /// service is up enough to accept its settings.</summary>
    [HttpGet("status/health")]
    public IActionResult Health() => Ok(new { status = "ok", gates = _db.Gates.Count(g => g.Active) });

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var settings = await _db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        return Ok(new
        {
            service = "Gate Controller Service",
            version = typeof(GateWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            serviceId = settings?.ServiceId,
            serverUrl = settings?.ServerUrl,
            // False on a machine with no GPIO chip: the service runs and the
            // logic works, but nothing physically moves.
            gpioAvailable = _outputs.HardwareAvailable,
            openGates = _cycles.OpenGateIds,
            gateCount = await _db.Gates.CountAsync()
        });
    }

    [HttpGet("gates")]
    public async Task<IActionResult> Gates() =>
        Ok(await _db.Gates.OrderBy(g => g.GateId).ToListAsync());

    [HttpPost("gates")]
    public async Task<IActionResult> AddGate([FromBody] GateConfigEntity gate)
    {
        if (string.IsNullOrWhiteSpace(gate.GateId))
            return BadRequest(new { message = "GateId is required." });
        if (await _db.Gates.AnyAsync(g => g.GateId == gate.GateId))
            return Conflict(new { message = $"Gate '{gate.GateId}' already exists." });

        _db.Gates.Add(gate);
        await _db.SaveChangesAsync();
        _restart.TriggerRestart();
        return Ok(gate);
    }

    [HttpPut("gates/{gateId}")]
    public async Task<IActionResult> UpdateGate(string gateId, [FromBody] GateConfigEntity update)
    {
        var gate = await _db.Gates.FirstOrDefaultAsync(g => g.GateId == gateId);
        if (gate == null) return NotFound();

        gate.DisplayName = update.DisplayName ?? gate.DisplayName;
        // Pins are assigned straight across, nulls included — clearing one is
        // how a site removes a gate relay or a light.
        gate.GatePin = update.GatePin;
        gate.LightPin = update.LightPin;
        gate.InvertOutputs = update.InvertOutputs;
        gate.ScaleHardwareId = update.ScaleHardwareId;
        if (update.ReleaseWeightThreshold > 0) gate.ReleaseWeightThreshold = update.ReleaseWeightThreshold;
        if (update.MaxOpenSeconds > 0) gate.MaxOpenSeconds = update.MaxOpenSeconds;
        if (!string.IsNullOrWhiteSpace(update.TriggerOn)) gate.TriggerOn = update.TriggerOn;
        gate.Active = update.Active;

        await _db.SaveChangesAsync();
        _restart.TriggerRestart();
        return Ok(gate);
    }

    [HttpDelete("gates/{gateId}")]
    public async Task<IActionResult> DeleteGate(string gateId)
    {
        var gate = await _db.Gates.FirstOrDefaultAsync(g => g.GateId == gateId);
        if (gate == null) return NotFound();

        // Never delete a gate out from under an open cycle — the close would
        // have nothing left to drive and the output would stay energised.
        _cycles.Close(gateId, "gate deleted");
        _db.Gates.Remove(gate);
        await _db.SaveChangesAsync();
        _restart.TriggerRestart();
        return Ok(new { deleted = gateId });
    }

    /// <summary>
    /// Fire a gate for real, as if a ticket had finished on it. For checking
    /// the wiring at commissioning time — it runs the normal cycle, so it
    /// releases on weight or the timeout like any other.
    /// </summary>
    [HttpPost("gates/{gateId}/test")]
    public async Task<IActionResult> TestGate(string gateId)
    {
        var gate = await _db.Gates.FirstOrDefaultAsync(g => g.GateId == gateId);
        if (gate == null) return NotFound();

        _cycles.Open(gate, "TEST");
        return Ok(new { opened = gateId, closesAfterSeconds = gate.MaxOpenSeconds, gpio = _outputs.HardwareAvailable });
    }

    [HttpPost("gates/{gateId}/close")]
    public IActionResult CloseGate(string gateId)
    {
        _cycles.Close(gateId, "closed from the local API");
        return Ok(new { closed = gateId });
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings() =>
        Ok(await _db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync());

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] ServiceSettings update)
    {
        var settings = await _db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(update.ServiceId)) settings.ServiceId = update.ServiceId;
        if (!string.IsNullOrWhiteSpace(update.ServerUrl)) settings.ServerUrl = update.ServerUrl;
        if (!string.IsNullOrWhiteSpace(update.SignalRHub)) settings.SignalRHub = update.SignalRHub;

        await _db.SaveChangesAsync();
        _restart.TriggerRestart();
        return Ok(settings);
    }
}
