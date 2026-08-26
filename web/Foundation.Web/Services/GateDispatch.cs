using Microsoft.AspNetCore.SignalR;
using Foundation.Web.Data;
using Foundation.Web.Hubs;

namespace Foundation.Web.Services;

/// <summary>
/// Sends the "open" command to the gate controller Pi when a ticket finishes.
///
/// Which gate is decided by the scale that captured the weighment, the same way
/// a ticket's printer is: the scale row carries "serviceId:gateId", and a scale
/// with none set controls nothing. Everything about how long the gate stays
/// open — the weight that releases it, the time limit — lives on the Pi, not
/// here, so a gate cannot be left open by the web app going away mid-cycle.
/// </summary>
public static class GateDispatch
{
    /// <summary>
    /// Fire the gate for a completed weighment. `scaleName` is what the ticket
    /// recorded, `direction` is "weighin" or "weighout" — a retained-tare
    /// weigh-in that closes the load in one pass should pass "weighout",
    /// because as far as the yard is concerned the truck is leaving.
    ///
    /// Never throws: a gate is an accessory to the weighment, and failing to
    /// open one must not fail the ticket that was already written.
    /// </summary>
    public static async Task OpenForTicket(IHubContext<ScaleHub> hub, ScaleDbContext db,
        ILogger log, string? scaleName, string ticket, string direction)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(scaleName)) return;

            var gateId = db.Scales
                .Where(s => s.Name == scaleName)
                .Select(s => s.GateId)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(gateId)) return;

            // "serviceId:gateId" — the service half addresses the box, the gate
            // half picks the output on it.
            var parts = gateId.Split(':', 2);
            var serviceId = parts.Length > 1 ? parts[0] : "default";
            var gate = parts.Length > 1 ? parts[1] : parts[0];

            await hub.Clients.Group($"Gate_{serviceId}").SendAsync("OpenGate",
                new { gateId = gate, ticket, direction });

            log.LogInformation("Gate: asked {Service}/{Gate} to open for ticket {Ticket} ({Direction})",
                serviceId, gate, ticket, direction);
        }
        catch (Exception ex)
        {
            log.LogWarning("Gate: could not signal the gate for ticket {Ticket}: {Msg}", ticket, ex.Message);
        }
    }
}
