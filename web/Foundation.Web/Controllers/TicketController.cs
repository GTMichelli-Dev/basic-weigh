using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Models;
using Foundation.Web.Reports;
using Foundation.Web.Services;
using DevExpress.XtraReports.UI;
using DevExpress.Drawing;

namespace Foundation.Web.Controllers;

public class TicketController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly PrintQueueService _printQueue;
    private readonly IHubContext<ScaleHub> _hub;
    private readonly AppSetupCache _setupCache;
    private readonly ILogger<TicketController> _log;
    private static readonly string ReportPath = Path.Combine(
        Directory.GetCurrentDirectory(), "Reports", "TicketReport.repx");

    public TicketController(ScaleDbContext db, PrintQueueService printQueue, IHubContext<ScaleHub> hub,
        AppSetupCache setupCache, ILogger<TicketController> log)
    {
        _db = db;
        _printQueue = printQueue;
        _hub = hub;
        _setupCache = setupCache;
        _log = log;
    }

    public IActionResult Print(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();
        ViewBag.Header1 = setup.Header1;
        ViewBag.Header2 = setup.Header2;
        ViewBag.Header3 = setup.Header3;
        ViewBag.Header4 = setup.Header4;
        ViewBag.Theme = setup.Theme ?? "default";
        ViewBag.SavePicture = setup.SavePicture;

        // Check if camera images exist for this ticket
        var imgDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "tickets");
        ViewBag.HasInPhoto = System.IO.File.Exists(Path.Combine(imgDir, $"{id}_In.jpg"));
        ViewBag.HasOutPhoto = System.IO.File.Exists(Path.Combine(imgDir, $"{id}_Out.jpg"));

        return View(transaction);
    }

    [HttpGet("api/ticket/{id}")]
    public IActionResult GetTicketData(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();

        // If this ticket was awaiting remote print confirmation, broadcast it
        if (_printQueue.TryConfirm(id))
        {
            var label = setup.RemotePrintMode == "Scale" ? "Scale" : "Remote Printer";
            _ = _hub.Clients.All.SendAsync("PrintConfirmed", new { ticketId = id, printer = label });
        }

        return Json(new
        {
            ticket = transaction.Ticket,
            dateIn = transaction.DateIn.ToServerLocal().ToString("MM/dd/yyyy hh:mm tt"),
            dateOut = transaction.DateOut.ToServerLocal()?.ToString("MM/dd/yyyy hh:mm tt"),
            customer = transaction.Customer,
            carrier = transaction.Carrier,
            truckId = transaction.TruckId,
            commodity = transaction.Commodity,
            location = transaction.Location,
            destination = transaction.Destination,
            bin = transaction.Bin,
            grossWeight = transaction.GrossWeight,
            tareWeight = transaction.TareWeight,
            netWeight = transaction.NetWeight,
            notes = transaction.Notes,
            isVoid = transaction.Void,
            header1 = setup.Header1,
            header2 = setup.Header2,
            header3 = setup.Header3,
            header4 = setup.Header4
        });
    }

    // Report Designer - edit a ticket template
    public IActionResult Designer(string reportName = "TicketReport")
    {
        return View("Designer", reportName);
    }

    // Document Viewer - preview/print a specific ticket
    public new IActionResult View(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();

        // Load report template
        XtraReport report;
        if (System.IO.File.Exists(ReportPath))
        {
            report = XtraReport.FromFile(ReportPath);
        }
        else
        {
            report = new TicketReport();
        }

        // Set logo image on the picture box
        SetLogoImage(report, setup);
        ApplyFieldVisibility(report, setup);
        SetSignatureImage(report, setup, id);
        InjectBinRow(report, setup, transaction, "capSignature", "lblVoid");
        InjectCustomFields(report, id, "capSignature", "lblVoid");

        // Set parameters
        SetParam(report, "Ticket", transaction.Ticket);
        SetParam(report, "DateIn", transaction.DateIn.ToServerLocal().ToString("MM/dd/yyyy hh:mm tt"));
        SetParam(report, "DateOut", transaction.DateOut.ToServerLocal()?.ToString("MM/dd/yyyy hh:mm tt") ?? "");
        SetParam(report, "Customer", transaction.Customer ?? "");
        SetParam(report, "Carrier", transaction.Carrier ?? "");
        SetParam(report, "TruckId", transaction.TruckId ?? "");
        SetParam(report, "Commodity", transaction.Commodity ?? "");
        SetParam(report, "Location", transaction.Location ?? "");
        SetParam(report, "Destination", transaction.Destination ?? "");
        SetParam(report, "GrossWeight", transaction.GrossWeight.ToString("#,##0") + " lb");
        SetParam(report, "TareWeight", transaction.TareWeight.ToString("#,##0") + " lb");
        SetParam(report, "NetWeight", transaction.NetWeight.ToString("#,##0") + " lb");
        SetParam(report, "Notes", transaction.Notes ?? "");
        SetParam(report, "IsVoid", transaction.Void);
        SetParam(report, "Header1", setup.Header1 ?? "");
        SetParam(report, "Header2", setup.Header2 ?? "");
        SetParam(report, "Header3", setup.Header3 ?? "");
        SetParam(report, "Header4", setup.Header4 ?? "");

        ViewBag.TicketId = id;
        ViewBag.Header1 = setup.Header1 ?? "Foundation";
        ViewBag.Theme = setup.Theme ?? "default";
        return View(report);
    }

    // Kiosk Ticket - print inbound kiosk ticket with barcode
    public IActionResult KioskView(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();

        var kioskPath = Path.Combine(Directory.GetCurrentDirectory(), "Reports", "KioskTicketReport.repx");
        XtraReport report;
        if (System.IO.File.Exists(kioskPath))
        {
            report = XtraReport.FromFile(kioskPath);
        }
        else
        {
            report = new KioskTicketReport();
        }

        ApplyFieldVisibility(report, setup);
        InjectBinRow(report, setup, transaction, "capInWeight");
        InjectCustomFields(report, id, "capInWeight");
        SetParam(report, "Ticket", transaction.Ticket);
        SetParam(report, "DateIn", transaction.DateIn.ToServerLocal().ToString("MM/dd/yyyy hh:mm tt"));
        SetParam(report, "Customer", transaction.Customer ?? "");
        SetParam(report, "Carrier", transaction.Carrier ?? "");
        SetParam(report, "TruckId", transaction.TruckId ?? "");
        SetParam(report, "Commodity", transaction.Commodity ?? "");
        SetParam(report, "Location", transaction.Location ?? "");
        SetParam(report, "InWeight", transaction.InWeight.ToString("#,##0") + " lb");
        SetParam(report, "Header1", setup.Header1 ?? "");
        SetParam(report, "Header2", setup.Header2 ?? "");
        SetParam(report, "Header3", setup.Header3 ?? "");
        SetParam(report, "Header4", setup.Header4 ?? "");

        ViewBag.TicketId = id;
        return View(report);
    }

    [HttpGet("api/ticket/{id}/pdf")]
    // Alias for the mobile page, which sits outside the kiosk PIN gate and
    // hands the driver their finished ticket as a download instead of printing.
    [HttpGet("api/mobile/ticket/{id}/pdf")]
    public IActionResult GetTicketPdf(string id)
    {
        // Timed end to end. A ticket that takes ten seconds to come out of the
        // printer is either slow here (building and rasterising the report) or
        // slow getting here (the print service re-opening its HTTPS connection),
        // and those two need opposite fixes. The log line says which.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();

        // Use KioskTicketReport for inbound (not yet completed), TicketReport for completed
        bool isInbound = transaction.DateOut == null;

        XtraReport report;
        if (isInbound)
        {
            var kioskPath = Path.Combine(Directory.GetCurrentDirectory(), "Reports", "KioskTicketReport.repx");
            report = System.IO.File.Exists(kioskPath)
                ? XtraReport.FromFile(kioskPath)
                : new KioskTicketReport();

            ApplyFieldVisibility(report, setup);
            InjectBinRow(report, setup, transaction, "capInWeight");
            InjectCustomFields(report, id, "capInWeight");
            SetParam(report, "Ticket", transaction.Ticket);
            SetParam(report, "DateIn", transaction.DateIn.ToServerLocal().ToString("MM/dd/yyyy hh:mm tt"));
            SetParam(report, "Customer", transaction.Customer ?? "");
            SetParam(report, "Carrier", transaction.Carrier ?? "");
            SetParam(report, "TruckId", transaction.TruckId ?? "");
            SetParam(report, "Commodity", transaction.Commodity ?? "");
            SetParam(report, "Location", transaction.Location ?? "");
            SetParam(report, "InWeight", transaction.InWeight.ToString("#,##0") + " lb");
            SetParam(report, "Header1", setup.Header1 ?? "");
            SetParam(report, "Header2", setup.Header2 ?? "");
            SetParam(report, "Header3", setup.Header3 ?? "");
            SetParam(report, "Header4", setup.Header4 ?? "");
        }
        else
        {
            report = System.IO.File.Exists(ReportPath)
                ? XtraReport.FromFile(ReportPath)
                : new TicketReport();

            SetLogoImage(report, setup);
            ApplyFieldVisibility(report, setup);
            SetSignatureImage(report, setup, id);
            InjectBinRow(report, setup, transaction, "capSignature", "lblVoid");
            InjectCustomFields(report, id, "capSignature", "lblVoid");
            SetParam(report, "Ticket", transaction.Ticket);
            SetParam(report, "DateIn", transaction.DateIn.ToServerLocal().ToString("MM/dd/yyyy hh:mm tt"));
            SetParam(report, "DateOut", transaction.DateOut.ToServerLocal()?.ToString("MM/dd/yyyy hh:mm tt") ?? "");
            SetParam(report, "Customer", transaction.Customer ?? "");
            SetParam(report, "Carrier", transaction.Carrier ?? "");
            SetParam(report, "TruckId", transaction.TruckId ?? "");
            SetParam(report, "Commodity", transaction.Commodity ?? "");
            SetParam(report, "Location", transaction.Location ?? "");
            SetParam(report, "Destination", transaction.Destination ?? "");
            SetParam(report, "GrossWeight", transaction.GrossWeight.ToString("#,##0") + " lb");
            SetParam(report, "TareWeight", transaction.TareWeight.ToString("#,##0") + " lb");
            SetParam(report, "NetWeight", transaction.NetWeight.ToString("#,##0") + " lb");
            SetParam(report, "Notes", transaction.Notes ?? "");
            SetParam(report, "IsVoid", transaction.Void);
            SetParam(report, "Header1", setup.Header1 ?? "");
            SetParam(report, "Header2", setup.Header2 ?? "");
            SetParam(report, "Header3", setup.Header3 ?? "");
            SetParam(report, "Header4", setup.Header4 ?? "");
        }

        var builtAt = sw.ElapsedMilliseconds;

        using var ms = new MemoryStream();
        report.ExportToPdf(ms);
        ms.Position = 0;

        _log.LogInformation(
            "Ticket {Ticket} PDF: build {BuildMs}ms, export {ExportMs}ms, {Bytes} bytes",
            id, builtAt, sw.ElapsedMilliseconds - builtAt, ms.Length);

        // If this ticket was awaiting remote print confirmation, broadcast it
        if (_printQueue.TryConfirm(id))
        {
            var label = setup.RemotePrintMode == "Scale" ? "Scale" : "Remote Printer";
            _ = _hub.Clients.All.SendAsync("PrintConfirmed", new { ticketId = id, printer = label });
        }

        return File(ms.ToArray(), "application/pdf", $"ticket-{id}.pdf");
    }

    /// <summary>
    /// Reprint a ticket via Scale or Remote Printer.
    /// With <paramref name="printerId"/> ("serviceId:printerId") the operator
    /// picked a specific printer from the reprint dialog on the ticket grids,
    /// which addresses that one print service directly and bypasses the
    /// RemotePrintMode routing.
    /// </summary>
    [HttpPost("api/ticket/{id}/reprint")]
    public async Task<IActionResult> Reprint(string id, [FromQuery] string? printerId = null)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();

        // Operator picked a printer. This is the same dispatch the kiosk uses,
        // reached through the operator login instead of the kiosk PIN — the
        // grids are staff screens and their users have no kiosk PIN cookie.
        if (!string.IsNullOrEmpty(printerId))
        {
            var type = transaction.DateOut != null ? "weighout" : "weighin";
            var scaleName = type == "weighout"
                ? (transaction.OutScale ?? transaction.InScale)
                : transaction.InScale;

            await PrintDispatch.SendAsync(_hub, _db, setup, id, type, printerId, scaleName);
            return Json(new { success = true, mode = "RemotePrinter" });
        }

        if (setup.RemotePrintMode == "Scale")
        {
            _printQueue.Enqueue(id);
            _printQueue.AwaitConfirmation(id);
        }
        else if (setup.RemotePrintMode == "RemotePrinter")
        {
            _printQueue.Enqueue(id);
            _printQueue.AwaitConfirmation(id);
            await _hub.Clients.Group("PrintClients").SendAsync("PrintTicket",
                new { ticketId = id, pdfUrl = $"/api/ticket/{id}/pdf" });
        }
        else
        {
            return BadRequest(new { success = false, message = "Remote printing is not enabled." });
        }

        return Json(new { success = true, mode = setup.RemotePrintMode });
    }

    /// <summary>
    /// Show and fill the driver-signature block when a signature PNG exists for
    /// the ticket and printing is enabled; hide it otherwise. Older customized
    /// .repx templates without the picSignature control are left untouched.
    /// </summary>
    private void SetSignatureImage(XtraReport report, AppSetup setup, string ticketId)
    {
        var picBox = report.FindControl("picSignature", true) as XRPictureBox;
        var caption = report.FindControl("capSignature", true);
        if (picBox == null && caption == null) return;

        var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "tickets", $"{ticketId}_Signature.png");
        bool show = setup.PrintSignatureOnTicket && System.IO.File.Exists(path);

        if (show && picBox != null)
        {
            try
            {
                using var ms = new MemoryStream(System.IO.File.ReadAllBytes(path));
                var dxImage = DXImage.FromStream(ms);
                picBox.ImageSource = new DevExpress.XtraPrinting.Drawing.ImageSource(dxImage);
            }
            catch
            {
                show = false; // unreadable image - hide the block
            }
        }

        if (!show)
        {
            // Remove the block entirely so unsigned tickets print without a gap
            ReportFieldLayout.Collapse(report, "capSignature", "picSignature");
            return;
        }
        if (picBox != null) picBox.Visible = true;
        if (caption != null) caption.Visible = true;
    }

    /// <summary>Collapse the printed rows of fields hidden on Setup → Fields.</summary>
    private static void ApplyFieldVisibility(XtraReport report, AppSetup setup)
    {
        if (setup.HideCustomer) ReportFieldLayout.CollapseRow(report, "Customer");
        if (setup.HideCarrier) ReportFieldLayout.CollapseRow(report, "Carrier");
        if (setup.HideTruckId) ReportFieldLayout.CollapseRow(report, "TruckId");
        if (setup.HideCommodity) ReportFieldLayout.CollapseRow(report, "Commodity");
        if (setup.HideLocation) ReportFieldLayout.CollapseRow(report, "Location");
        if (setup.HideDestination) ReportFieldLayout.CollapseRow(report, "Destination");
        if (setup.HideNotes) ReportFieldLayout.CollapseRow(report, "Notes");
    }

    /// <summary>Append a "Bin:" row to the printed ticket (Bin Inventory
    /// feature). Uses the same auto-append mechanism as unplaced custom fields
    /// so existing .repx layouts print unchanged; a designer-placed "Bin"
    /// parameter takes over when the layout references it.</summary>
    private void InjectBinRow(XtraReport report, AppSetup setup, Transaction transaction, params string[] anchorNames)
    {
        if (!setup.UseBinInventory || string.IsNullOrEmpty(transaction.Bin)) return;

        var paramName = CustomFieldParams.ParamName("Bin");
        if (CustomFieldParams.IsReferenced(report, paramName))
        {
            CustomFieldParams.EnsureParameter(report, paramName, "Bin");
            report.Parameters[paramName].Value = transaction.Bin;
            return;
        }
        ReportFieldLayout.InsertRows(report, new List<(string, string)> { ("Bin:", transaction.Bin!) }, anchorNames);
    }

    /// <summary>Print this ticket's custom-field values. Hybrid: a field whose
    /// cf_ parameter is referenced in the layout (placed via the designer) gets
    /// its value through that parameter regardless of Show on Ticket; a
    /// Show-on-Ticket field NOT referenced keeps the legacy auto-appended row
    /// above the given anchor control, so existing layouts print unchanged. A
    /// field with Show on Ticket unchecked and no placement prints nothing —
    /// its parameter simply sits in the designer waiting to be placed.</summary>
    private void InjectCustomFields(XtraReport report, string ticketId, params string[] anchorNames)
    {
        var rows = (from v in _db.TransactionCustomValues
                    join f in _db.CustomFields on v.CustomFieldId equals f.Id
                    where v.Ticket == ticketId
                          && v.Value != null && v.Value != ""
                    orderby f.SortOrder, f.Name
                    select new { f.Name, f.ShowOnTicket, v.Value }).ToList();
        if (rows.Count == 0) return;

        var appendRows = new List<(string Label, string Value)>();
        foreach (var row in rows)
        {
            var paramName = CustomFieldParams.ParamName(row.Name);
            if (CustomFieldParams.IsReferenced(report, paramName))
            {
                CustomFieldParams.EnsureParameter(report, paramName, row.Name);
                report.Parameters[paramName].Value = row.Value;
            }
            else if (row.ShowOnTicket)
            {
                appendRows.Add((row.Name + ":", row.Value!));
            }
        }

        ReportFieldLayout.InsertRows(report, appendRows, anchorNames);
    }

    private void SetLogoImage(XtraReport report, AppSetup setup)
    {
        var picBox = report.FindControl("picLogo", true) as XRPictureBox;
        if (picBox == null) return;

        byte[]? iconBytes = setup.Icon;
        string? contentType = setup.IconContentType;

        // If custom icon is SVG, skip it (DevExpress can't render SVG in XRPictureBox)
        if (iconBytes != null && contentType != null && contentType.Contains("svg"))
        {
            iconBytes = null;
        }

        if (iconBytes == null)
        {
            // Fall back to default PNG icon
            var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "default-icon.png");
            if (System.IO.File.Exists(defaultPath))
                iconBytes = System.IO.File.ReadAllBytes(defaultPath);
        }

        if (iconBytes == null) return;

        try
        {
            using var ms = new MemoryStream(iconBytes);
            var dxImage = DXImage.FromStream(ms);
            picBox.ImageSource = new DevExpress.XtraPrinting.Drawing.ImageSource(dxImage);
        }
        catch
        {
            // Unsupported image format - skip logo
        }
    }

    private void SetParam(XtraReport report, string name, object value)
    {
        var param = report.Parameters[name];
        if (param != null)
            param.Value = value;
    }
}
