using Microsoft.AspNetCore.SignalR;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Routes a print command to the print service that owns the printer.
/// Shared by the kiosk (an unattended display printing its own ticket) and by
/// the operator screens' reprint buttons, so both resolve the default printer
/// and address the SignalR group the same way.
/// </summary>
public static class PrintDispatch
{
    /// <summary>
    /// Send a ticket to a printer.
    /// <paramref name="printerId"/> is "serviceId:printerId" (e.g.
    /// "office-1:BIXOLON BK3-3"). When empty: the virtual "KioskPrinter" in
    /// demo mode, otherwise the capturing scale's printer assignment falling
    /// back to the site-wide Setup defaults.
    /// </summary>
    public static async Task SendAsync(IHubContext<ScaleHub> hub, ScaleDbContext db, AppSetup setup,
        string ticketId, string type, string? printerId, string? scaleName = null)
    {
        // If no printer specified, use defaults
        if (string.IsNullOrEmpty(printerId))
        {
            if (setup.DemoMode)
            {
                // In demo mode, use a virtual "KioskPrinter" so the flow works
                printerId = "demo:KioskPrinter";
            }
            else
            {
                // Per-scale printer assignment, else the Setup default
                printerId = SiteScales.ResolvePrinter(db, scaleName, type == "weighout", setup);
            }
        }

        if (string.IsNullOrEmpty(printerId)) return;

        // Browser printing — handled client-side, skip server-side print command
        if (printerId.Equals("Browser:Browser", StringComparison.OrdinalIgnoreCase)) return;

        // Split serviceId:printerId
        var parts = printerId.Split(':', 2);
        var serviceId = parts.Length > 1 ? parts[0] : "";
        var printerName = parts.Length > 1 ? parts[1] : parts[0];

        if (!string.IsNullOrEmpty(serviceId))
        {
            // Route to specific service
            await hub.Clients.Group($"Print_{serviceId}").SendAsync("PrintTicket",
                new { ticketId, type, printerId = printerName });
        }
        else
        {
            // Broadcast to all print services
            await hub.Clients.Group("PrintClients").SendAsync("PrintTicket",
                new { ticketId, type, printerId = printerName });
        }
    }
}
