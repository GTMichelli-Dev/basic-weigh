using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

public class TransactionController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly IScaleService _scaleService;
    private readonly PrintQueueService _printQueue;
    private readonly IHubContext<ScaleHub> _hub;
    private readonly AppSetupCache _setupCache;
    private readonly ILogger<TransactionController> _log;
    private readonly SiteScales _siteScales;

    public TransactionController(ScaleDbContext db, IScaleService scaleService,
        PrintQueueService printQueue, IHubContext<ScaleHub> hub, AppSetupCache setupCache,
        ILogger<TransactionController> log, SiteScales siteScales)
    {
        _db = db;
        _scaleService = scaleService;
        _printQueue = printQueue;
        _hub = hub;
        _setupCache = setupCache;
        _log = log;
        _siteScales = siteScales;
    }

    /// <summary>The operator's current location (navbar picker cookie).</summary>
    private int? CurrentSiteId => SiteContext.CurrentSiteId(HttpContext, _db);

    /// <summary>Default-scale weight for form prefills (Basic Ticket) — the
    /// first active scale at the operator's location.</summary>
    private int GetDefaultScaleWeight()
    {
        var scale = _db.Scales.Where(s => s.Active).ForSite(CurrentSiteId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).FirstOrDefault();
        return _siteScales.Read(scale, _setupCache.Get().DemoMode).Weight;
    }

    private void PopulateDropdowns()
    {
        var siteId = CurrentSiteId;

        // Scale picker on the weigh forms (shown when more than one is active),
        // limited to the operator's location.
        ViewBag.Scales = _db.Scales.Where(s => s.Active).ForSite(siteId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToList();

        ViewBag.Customers = _db.Customers.Where(c => c.Active).OrderBy(c => c.CustomerName).ToList();

        // Carrier dropdown — only entries explicitly marked as Carriers in
        // master data. Customers who also haul are added to Carriers via the
        // "Add to Carriers" button on MasterData → Customers; from then on
        // they appear in this list.
        ViewBag.CarrierOptions = _db.Carriers
            .Where(c => c.Active)
            .OrderBy(c => c.CarrierName)
            .Select(c => c.CarrierName)
            .ToList();

        // Trucks loaded via AJAX based on selected carrier. Commodities and
        // bins can be limited to a location (Edit Tables); unassigned ones
        // show everywhere.
        ViewBag.Commodities = _db.Commodities.Where(c => c.Active).ForSite(siteId).OrderBy(c => c.CommodityName).ToList();
        ViewBag.Locations = _db.Locations.Where(l => l.Active).OrderBy(l => l.LocationName).ToList();
        ViewBag.Destinations = _db.Destinations.Where(d => d.Active).OrderBy(d => d.DestinationName).ToList();
        ViewBag.Bins = _db.Bins.Where(b => b.Active).ForSite(siteId).OrderBy(b => b.BinName).ToList();

        var setup = _setupCache.Get();
        ViewBag.BinRequired = setup.UseBinInventory && setup.BinRequired;

        // Lock Bin to Commodity: bin → its current contents, so the form JS
        // can auto-set the commodity when a holding bin is picked (the save
        // paths enforce it server-side via ValidateRequiredBin).
        var binLock = setup.UseBinInventory && setup.BinCommodityLock;
        ViewBag.BinCommodityLock = binLock;
        ViewBag.BinCommodities = binLock
            ? BinInventory.HeldCommodities(_db)
            : new Dictionary<string, string>();
    }

    /// <summary>
    /// Server-side backstop for the bin gates (Setup → Options). Require Bin
    /// is enforced on the forms via HTML validation; this catches direct posts
    /// and stale pages. Lock Bin to Commodity auto-sets a blank commodity from
    /// the bin's current contents and rejects a mismatch (the form JS normally
    /// auto-sets it client-side). Returns an error message, or null when the
    /// ticket is fine.
    /// </summary>
    private string? ValidateRequiredBin(Transaction transaction)
    {
        var setup = _setupCache.Get();
        if (setup.UseBinInventory && setup.BinRequired && string.IsNullOrWhiteSpace(transaction.Bin))
            return "Bin is required.";
        return BinInventory.ValidateTicket(_db, setup, transaction);
    }

    // GET: Transaction/WeighIn (new)
    // GET: Transaction/WeighIn/5 (edit existing inbound)
    public IActionResult WeighIn(string? id)
    {
        PopulateDropdowns();
        ViewBag.CurrentWeight = GetDefaultScaleWeight();
        SetCustomFieldViewBags(id);

        var setup = _setupCache.Get();
        ViewBag.SavePicture = setup.SavePicture;

        if (!string.IsNullOrEmpty(id))
        {
            var existing = _db.Transactions.Find(id);
            if (existing == null) return NotFound();
            if (existing.DateOut != null) return RedirectToAction("Edit", new { id });

            ViewBag.IsEdit = true;
            if (setup.SavePicture)
            {
                var imgDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "tickets");
                ViewBag.HasInPhoto = System.IO.File.Exists(Path.Combine(imgDir, $"{id}_In.jpg"));
            }
            return View(existing);
        }

        ViewBag.NextTicket = setup.TicketNumber.ToString();
        ViewBag.IsEdit = false;

        var txn = new Transaction();
        // Recall last ticket values if enabled
        if (setup.RecallLastValues)
        {
            var lastTxn = _db.Transactions
                .OrderByDescending(t => t.DateIn)
                .FirstOrDefault();
            if (lastTxn != null)
            {
                txn.Commodity = lastTxn.Commodity;
                txn.Customer = lastTxn.Customer;
                txn.Location = lastTxn.Location;
                txn.Destination = lastTxn.Destination;
                txn.Bin = lastTxn.Bin;
            }
        }
        return View(txn);
    }

    // POST: Transaction/WeighIn
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WeighIn(Transaction transaction, bool isEdit, bool goToWeighOut, bool manualWeight, bool completeWithRetainedTare = false, string? inScaleUsed = null)
    {
        var customFields = GetActiveCustomFields();
        var cfErrors = ValidateCustomFieldValues(customFields);
        if (ValidateRequiredBin(transaction) is { } binError) cfErrors.Add(binError);
        if (cfErrors.Count > 0)
        {
            // Backstop only — the form's HTML validation normally catches this.
            TempData["Error"] = string.Join(" ", cfErrors);
            return RedirectToAction("WeighIn", new { id = isEdit ? transaction.Ticket : null });
        }

        var visibility = _setupCache.Get();

        if (isEdit)
        {
            var existing = _db.Transactions.Find(transaction.Ticket);
            if (existing == null) return NotFound();

            existing.InWeight = transaction.InWeight;
            existing.ManualInbound = manualWeight;
            existing.InScale = manualWeight ? null : inScaleUsed;
            existing.DateIn = transaction.DateIn;
            ApplyVisibleFields(existing, transaction, visibility);
            SaveCustomFieldValues(transaction.Ticket, customFields);

            _db.SaveChanges();
            FormulaFields.RecomputeAndSave(_db, existing);

            if (goToWeighOut)
                return RedirectToAction("WeighOut", new { id = transaction.Ticket });
        }
        else
        {
            var setup = _db.AppSetup.First();
            while (_db.Transactions.Any(t => t.Ticket == setup.TicketNumber.ToString()))
            {
                setup.TicketNumber++;
            }
            transaction.Ticket = setup.TicketNumber.ToString();
            transaction.DateIn = transaction.DateIn == default ? DateTime.UtcNow : transaction.DateIn;
            transaction.Void = false;
            transaction.ManualInbound = manualWeight;
            transaction.InScale = manualWeight ? null : inScaleUsed;

            // Complete-with-retained-tare path. Look up the truck server-side
            // (don't trust the client) and, if the feature is on and a tare is
            // stored, save the ticket as already completed using that tare.
            bool completed = false;
            if (completeWithRetainedTare && setup.UseRetainedTare
                && !string.IsNullOrEmpty(transaction.TruckId)
                && !string.IsNullOrEmpty(transaction.Carrier))
            {
                var tid = transaction.TruckId.Trim().ToLower();
                var car = transaction.Carrier.Trim().ToLower();
                var truck = _db.Trucks.FirstOrDefault(x =>
                    x.TruckId.ToLower() == tid && x.CarrierName.ToLower() == car);

                // Tares from a previous date are auto-expired (gated on
                // AutoClearStaleRetainedTare).
                if (setup.AutoClearStaleRetainedTare
                    && truck?.RetainedTare.HasValue == true
                    && (truck.RetainedTareUpdated?.Date ?? DateTime.MinValue) < DateTime.Today)
                {
                    Console.WriteLine($"[RetainedTare] cleared stale tare for '{truck.TruckId}' / '{truck.CarrierName}' (last seen {truck.RetainedTareUpdated:yyyy-MM-dd})");
                    _log.LogInformation("RetainedTare: cleared stale tare for {TruckId}/{Carrier}", truck.TruckId, truck.CarrierName);
                    truck.RetainedTare = null;
                    truck.RetainedTareUpdated = null;
                }

                if (truck?.RetainedTare.HasValue == true)
                {
                    transaction.OutWeight = truck.RetainedTare;
                    transaction.DateOut = transaction.DateIn;
                    transaction.ManualOutbound = false;
                    completed = true;
                    var msg = $"WeighIn (admin) ticket {transaction.Ticket}: completed with recalled tare {truck.RetainedTare} lb (truck '{truck.TruckId}' / '{truck.CarrierName}')";
                    _log.LogInformation(msg);
                    Console.WriteLine($"[RetainedTare] {msg}");
                }
            }

            setup.TicketNumber++;
            _db.AppSetup.Update(setup);

            _db.Transactions.Add(transaction);
            SaveCustomFieldValues(transaction.Ticket, customFields);
            _db.SaveChanges();
            FormulaFields.RecomputeAndSave(_db, transaction);
            _setupCache.Invalidate();

            // A tare-completed ticket is effectively a weigh-out — route the
            // camera capture and the print to the outbound side and use type
            // "weighout" so the print agent picks the right ticket layout.
            // The regular open-ticket flow uses the inbound side below.
            if (completed)
            {
                if (setup.SavePicture && !manualWeight && !string.IsNullOrEmpty(setup.OutboundCameraId))
                {
                    var parts = setup.OutboundCameraId.Split(':', 2);
                    var serviceId = parts.Length > 1 ? parts[0] : "default";
                    var cameraId = parts.Length > 1 ? parts[1] : parts[0];
                    await _hub.Clients.Group($"Camera_{serviceId}").SendAsync("CaptureImage",
                        new { ticket = transaction.Ticket, direction = "out", cameraId });
                }

                // Retained-tare completion: the truck is physically on the
                // in-scale, so its printer (or the site default) gets the ticket.
                var outboundPrinter = SiteScales.ResolvePrinter(_db, transaction.InScale, outbound: true, setup);
                if (!string.IsNullOrEmpty(outboundPrinter))
                {
                    if (outboundPrinter.Equals("Browser:Browser", StringComparison.OrdinalIgnoreCase))
                    {
                        TempData["AutoPrintTicket"] = transaction.Ticket;
                        TempData["AutoPrintType"] = "weighout";
                    }
                    else
                    {
                        var parts2 = outboundPrinter.Split(':', 2);
                        var svcId = parts2.Length > 1 ? parts2[0] : "";
                        var pName = parts2.Length > 1 ? parts2[1] : parts2[0];
                        var group = !string.IsNullOrEmpty(svcId) ? $"Print_{svcId}" : "PrintClients";
                        await _hub.Clients.Group(group).SendAsync("PrintTicket",
                            new { ticketId = transaction.Ticket, type = "weighout", printerId = pName });
                    }
                }

                // Same destination as the WeighOut page so the operator lands on
                // the ticket they just completed and can print from there even
                // when no printer is configured.
                return RedirectToAction("View", "Ticket", new { id = transaction.Ticket });
            }

            // Camera capture on inbound (only if not manual weight)
            if (setup.SavePicture && !manualWeight && !string.IsNullOrEmpty(setup.InboundCameraId))
            {
                var parts = setup.InboundCameraId.Split(':', 2);
                var serviceId = parts.Length > 1 ? parts[0] : "default";
                var cameraId = parts.Length > 1 ? parts[1] : parts[0];
                await _hub.Clients.Group($"Camera_{serviceId}").SendAsync("CaptureImage",
                    new { ticket = transaction.Ticket, direction = "in", cameraId });
            }

            // Auto-print: the capturing scale's inbound printer, else the site default
            var inboundPrinter = SiteScales.ResolvePrinter(_db, transaction.InScale, outbound: false, setup);
            if (!string.IsNullOrEmpty(inboundPrinter))
            {
                if (inboundPrinter.Equals("Browser:Browser", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AutoPrintTicket"] = transaction.Ticket;
                    TempData["AutoPrintType"] = "weighin";
                }
                else
                {
                    var parts2 = inboundPrinter.Split(':', 2);
                    var svcId = parts2.Length > 1 ? parts2[0] : "";
                    var pName = parts2.Length > 1 ? parts2[1] : parts2[0];
                    var group = !string.IsNullOrEmpty(svcId) ? $"Print_{svcId}" : "PrintClients";
                    await _hub.Clients.Group(group).SendAsync("PrintTicket",
                        new { ticketId = transaction.Ticket, type = "weighin", printerId = pName });
                }
            }
        }

        return RedirectToAction("InboundTrucks");
    }

    // GET: Transaction/WeighOut/5
    public IActionResult WeighOut(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        ViewBag.CurrentWeight = GetDefaultScaleWeight();
        PopulateDropdowns();
        SetSignatureViewBags(id);
        SetCustomFieldViewBags(id);
        return View(transaction);
    }

    // ===== CUSTOM FIELDS (Setup → Fields) =====

    private List<CustomField> GetActiveCustomFields() =>
        _db.CustomFields.Where(f => f.Active)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToList();

    /// <summary>ViewBags consumed by the weigh forms: FieldSlots is the unified
    /// standard + custom field order (rendered via _TicketFieldSlot), CustomFields
    /// and CustomValues feed the custom-field inputs.</summary>
    private void SetCustomFieldViewBags(string? ticket)
    {
        var fields = GetActiveCustomFields();
        ViewBag.CustomFields = fields;
        ViewBag.FieldSlots = FieldOrdering.GetFormSlots(_setupCache.Get(), fields);
        ViewBag.CustomValues = string.IsNullOrEmpty(ticket)
            ? new Dictionary<int, string>()
            : _db.TransactionCustomValues
                .Where(v => v.Ticket == ticket && v.Value != null)
                .ToDictionary(v => v.CustomFieldId, v => v.Value!);

        // Parent → choices maps for cascading sub-fields, embedded on the
        // child selects so the forms can filter by the selected parent value.
        var dependentIds = fields.Where(f => f.ParentField != null).Select(f => f.Id).ToList();
        ViewBag.CustomFieldValueMaps = _db.CustomFieldListValues
            .Where(v => dependentIds.Contains(v.CustomFieldId))
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Value)
            .AsEnumerable()
            .GroupBy(v => v.CustomFieldId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(v => v.ParentValue)
                      .ToDictionary(pg => pg.Key, pg => pg.Select(v => v.Value).ToList()));
    }

    /// <summary>
    /// Server-side backstop for the cf_{id} inputs (the forms also enforce
    /// required/type via HTML validation). Returns error messages, empty if OK.
    /// </summary>
    private List<string> ValidateCustomFieldValues(List<CustomField> fields)
    {
        var errors = new List<string>();
        foreach (var f in fields)
        {
            var raw = Request.Form[$"cf_{f.Id}"].FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                if (f.Required) errors.Add($"{f.Name} is required.");
                continue;
            }
            if (f.FieldType == "Integer")
            {
                if (!long.TryParse(raw, out var intVal))
                    errors.Add($"{f.Name} must be a whole number.");
                else if (f.MinValue.HasValue && intVal < f.MinValue.Value)
                    errors.Add($"{f.Name} must be at least {f.MinValue.Value}.");
                else if (f.MaxValue.HasValue && intVal > f.MaxValue.Value)
                    errors.Add($"{f.Name} must be at most {f.MaxValue.Value}.");
            }
            else if (f.FieldType == "Real")
            {
                if (!double.TryParse(raw, out var realVal))
                    errors.Add($"{f.Name} must be a number.");
                else if (f.MinValue.HasValue && realVal < f.MinValue.Value)
                    errors.Add($"{f.Name} must be at least {f.MinValue.Value}.");
                else if (f.MaxValue.HasValue && realVal > f.MaxValue.Value)
                    errors.Add($"{f.Name} must be at most {f.MaxValue.Value}.");
                else if (f.Precision.HasValue)
                {
                    var dot = raw.IndexOf('.');
                    var decimals = dot < 0 ? 0 : raw.Length - dot - 1;
                    if (decimals > f.Precision.Value)
                        errors.Add($"{f.Name} allows at most {f.Precision.Value} decimal place(s).");
                }
            }
            else
            {
                // List-backed text: the posted value must be one of the
                // configured choices, or the ticket's previously stored value
                // (a retired choice kept selectable when editing history).
                var choices = f.GetListValues();
                if (choices.Count > 0 && !choices.Contains(raw))
                {
                    var stored = _db.TransactionCustomValues
                        .Any(v => v.CustomFieldId == f.Id && v.Value == raw);
                    if (!stored)
                        errors.Add($"{f.Name} must be one of the configured list values.");
                }
            }
        }
        return errors;
    }

    /// <summary>Upserts posted cf_{id} values for a ticket. Caller SaveChanges().
    /// Formula fields are skipped — they have no form input, and their stored
    /// values are managed by FormulaFields.RecomputeAndSave.</summary>
    private void SaveCustomFieldValues(string ticket, List<CustomField> fields)
    {
        foreach (var f in fields)
        {
            if (f.FieldType == "Formula") continue;
            var raw = Request.Form[$"cf_{f.Id}"].FirstOrDefault()?.Trim();
            var existing = _db.TransactionCustomValues
                .FirstOrDefault(v => v.Ticket == ticket && v.CustomFieldId == f.Id);
            if (string.IsNullOrEmpty(raw))
            {
                if (existing != null) _db.TransactionCustomValues.Remove(existing);
                continue;
            }
            if (raw.Length > 200) raw = raw[..200];
            if (existing == null)
                _db.TransactionCustomValues.Add(new TransactionCustomValue
                {
                    Ticket = ticket,
                    CustomFieldId = f.Id,
                    Value = raw
                });
            else
                existing.Value = raw;
        }
    }

    /// <summary>fieldId(string) -> value maps for a set of tickets, for grid APIs.</summary>
    private Dictionary<string, Dictionary<string, string?>> GetCustomValueMap(List<string> tickets)
    {
        return _db.TransactionCustomValues
            .Where(v => tickets.Contains(v.Ticket))
            .AsEnumerable()
            .GroupBy(v => v.Ticket)
            .ToDictionary(g => g.Key,
                g => g.ToDictionary(v => v.CustomFieldId.ToString(), v => v.Value));
    }

    /// <summary>
    /// Copy posted standard-field values onto a tracked transaction, skipping
    /// hidden fields — a hidden field isn't on the form, so the posted value is
    /// null and assigning it would silently erase stored data.
    /// </summary>
    private static void ApplyVisibleFields(Transaction target, Transaction posted, AppSetup setup)
    {
        if (!setup.HideCustomer) target.Customer = posted.Customer;
        if (!setup.HideCarrier) target.Carrier = posted.Carrier;
        if (!setup.HideTruckId) target.TruckId = posted.TruckId;
        if (!setup.HideCommodity) target.Commodity = posted.Commodity;
        if (!setup.HideLocation) target.Location = posted.Location;
        if (!setup.HideDestination) target.Destination = posted.Destination;
        // Bin and Transfer To are only on the form while Bin Inventory is enabled.
        if (setup.UseBinInventory)
        {
            target.Bin = posted.Bin;
            target.TransferToBin = string.IsNullOrWhiteSpace(posted.TransferToBin) ? null : posted.TransferToBin;
        }
        if (!setup.HideNotes) target.Notes = posted.Notes;
    }

    private void SetSignatureViewBags(string ticket)
    {
        var setup = _setupCache.Get();
        ViewBag.SignatureMode = setup.SignatureMode ?? "None";
        ViewBag.SignatureRequired = setup.SignatureRequired;
        ViewBag.SignaturePadId = string.IsNullOrWhiteSpace(setup.SignaturePadId) ? "default" : setup.SignaturePadId;
        ViewBag.HasSignature = System.IO.File.Exists(Path.Combine(TicketsImageDir, $"{ticket}_Signature.png"));
    }

    // POST: Transaction/WeighOut/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WeighOut(string id, Transaction transaction, bool manualOutWeight, bool manualInWeight, string? inScaleUsed = null, string? outScaleUsed = null)
    {
        var existing = _db.Transactions.Find(id);
        if (existing == null) return NotFound();

        var setup = _setupCache.Get();
        var customFields = GetActiveCustomFields();

        existing.InWeight = transaction.InWeight;
        existing.ManualInbound = manualInWeight;
        existing.InScale = manualInWeight ? null : inScaleUsed;
        existing.OutWeight = transaction.OutWeight;
        existing.ManualOutbound = manualOutWeight;
        existing.OutScale = manualOutWeight ? null : outScaleUsed;
        existing.DateOut = transaction.DateOut ?? DateTime.UtcNow;
        existing.DateIn = transaction.DateIn;

        // Server-side backstops. The Weigh Out page enforces both client-side;
        // this catches direct posts and stale pages. Bin validation runs on
        // the posted values BEFORE they're copied so its auto-set commodity
        // (Lock Bin to Commodity) lands on the saved ticket.
        foreach (var err in ValidateCustomFieldValues(customFields))
            ModelState.AddModelError("", err);
        if (ValidateRequiredBin(transaction) is { } binError)
            ModelState.AddModelError("", binError);
        ApplyVisibleFields(existing, transaction, setup);
        if (setup.SignatureMode != "None" && setup.SignatureRequired
            && !System.IO.File.Exists(Path.Combine(TicketsImageDir, $"{id}_Signature.png")))
        {
            ModelState.AddModelError("", "A driver signature is required before this ticket can be saved.");
        }
        if (!ModelState.IsValid)
        {
            ViewBag.CurrentWeight = GetDefaultScaleWeight();
            PopulateDropdowns();
            SetSignatureViewBags(id);
            SetCustomFieldViewBags(id);
            return View(existing);
        }

        SaveCustomFieldValues(id, customFields);

        var weighOutMsg = $"WeighOut (admin) ticket {existing.Ticket}: UseRetainedTare={setup.UseRetainedTare} TruckId='{existing.TruckId}' Carrier='{existing.Carrier}'";
        _log.LogInformation(weighOutMsg);
        Console.WriteLine($"[RetainedTare] {weighOutMsg}");
        if (setup.UseRetainedTare)
        {
            UpdateRetainedTare(existing);
        }

        // A card-driven load finished in the office instead of at the kiosk —
        // release the card here too, or it would stay stuck on a closed ticket.
        var card = CardFields.ForTicket(_db, existing);
        if (card != null) CardFields.Release(card, setup, existing.Ticket);

        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, existing);

        // Camera capture on outbound (only if not manual weight)
        if (setup.SavePicture && !manualOutWeight && !string.IsNullOrEmpty(setup.OutboundCameraId))
        {
            var parts = setup.OutboundCameraId.Split(':', 2);
            var serviceId = parts.Length > 1 ? parts[0] : "default";
            var cameraId = parts.Length > 1 ? parts[1] : parts[0];
            await _hub.Clients.Group($"Camera_{serviceId}").SendAsync("CaptureImage",
                new { ticket = id, direction = "out", cameraId });
        }

        // Open the exit for the scale that weighed the truck out. A manually
        // keyed weight means the truck was never on the deck, so there is
        // nothing to let out and OutScale is null — the gate stays shut.
        await GateDispatch.OpenForTicket(_hub, _db, _log, existing.OutScale, id, "weighout");

        // Auto-print: the out-weighing scale's printer, else the site default
        var outboundPrinter = SiteScales.ResolvePrinter(_db, existing.OutScale ?? existing.InScale, outbound: true, setup);
        if (!string.IsNullOrEmpty(outboundPrinter))
        {
            if (outboundPrinter.Equals("Browser:Browser", StringComparison.OrdinalIgnoreCase))
            {
                // Redirect to ticket view with auto-print — opens in new window via CompletedTrucks
                TempData["AutoPrintTicket"] = id;
            }
            else
            {
                var parts2 = outboundPrinter.Split(':', 2);
                var svcId = parts2.Length > 1 ? parts2[0] : "";
                var pName = parts2.Length > 1 ? parts2[1] : parts2[0];
                var group = !string.IsNullOrEmpty(svcId) ? $"Print_{svcId}" : "PrintClients";
                await _hub.Clients.Group(group).SendAsync("PrintTicket",
                    new { ticketId = id, type = "weighout", printerId = pName });
            }
        }

        return RedirectToAction("View", "Ticket", new { id });
    }

    // GET: Transaction/InboundTrucks
    public IActionResult InboundTrucks()
    {
        var setup = _setupCache.Get();
        ViewBag.RemotePrintMode = setup.RemotePrintMode ?? "None";
        ViewBag.KioskCount = setup.KioskCount;
        ViewBag.DemoMode = setup.DemoMode;
        ViewBag.SavePicture = setup.SavePicture;
        return View();
    }

    // GET: Transaction/CompletedTrucks
    public IActionResult CompletedTrucks()
    {
        var setup = _setupCache.Get();
        ViewBag.RemotePrintMode = setup.RemotePrintMode ?? "None";
        ViewBag.KioskCount = setup.KioskCount;
        ViewBag.DemoMode = setup.DemoMode;
        ViewBag.UseQuickBooks = setup.UseQuickBooks;
        ViewBag.SavePicture = setup.SavePicture;
        return View();
    }

    // GET: Transaction/BasicTicket
    public IActionResult BasicTicket()
    {
        var setup = _setupCache.Get();
        ViewBag.NextTicket = setup.TicketNumber.ToString();
        ViewBag.CurrentWeight = GetDefaultScaleWeight();
        PopulateDropdowns();
        SetCustomFieldViewBags(null);
        return View();
    }

    // POST: Transaction/BasicTicket
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult BasicTicket(Transaction transaction, string? scaleUsed = null)
    {
        var customFields = GetActiveCustomFields();
        var cfErrors = ValidateCustomFieldValues(customFields);
        if (ValidateRequiredBin(transaction) is { } binError) cfErrors.Add(binError);
        if (cfErrors.Count > 0)
        {
            TempData["Error"] = string.Join(" ", cfErrors);
            return RedirectToAction("BasicTicket");
        }

        // Single-weigh ticket: the one scale captured both weights.
        transaction.InScale = scaleUsed;
        transaction.OutScale = scaleUsed;

        var setup = _db.AppSetup.First();
        while (_db.Transactions.Any(t => t.Ticket == setup.TicketNumber.ToString()))
        {
            setup.TicketNumber++;
        }
        transaction.Ticket = setup.TicketNumber.ToString();
        transaction.DateIn = transaction.DateIn == default ? DateTime.UtcNow : transaction.DateIn;
        transaction.DateOut = transaction.DateIn;
        transaction.OutWeight = transaction.InWeight;
        transaction.Void = false;

        setup.TicketNumber++;
        _db.AppSetup.Update(setup);

        _db.Transactions.Add(transaction);
        SaveCustomFieldValues(transaction.Ticket, customFields);
        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, transaction);
        _setupCache.Invalidate();

        return RedirectToAction("CompletedTrucks", new { reset = "true" });
    }

    // POST: Transaction/Void/5
    [HttpPost("Transaction/Void/{id}")]
    public IActionResult ToggleVoid(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        transaction.Void = !transaction.Void;

        // Voiding an open card ticket strands the card on a ticket that will
        // never close — cut it loose so the loader operator can re-issue it.
        if (transaction.Void)
        {
            var voidedCard = _db.Cards.FirstOrDefault(c => c.OpenTicket == transaction.Ticket);
            if (voidedCard != null) CardFields.Release(voidedCard, _setupCache.Get(), transaction.Ticket);
        }
        _db.SaveChanges();

        return Json(new { success = true, isVoid = transaction.Void });
    }

// POST: Transaction/DeleteInbound/5
    [HttpPost("Transaction/DeleteInbound/{id}")]
    public IActionResult DeleteInbound(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        // Same as voiding: the card must not stay bound to a ticket that's gone.
        var strandedCard = _db.Cards.FirstOrDefault(c => c.OpenTicket == transaction.Ticket);
        if (strandedCard != null) CardFields.Release(strandedCard, _setupCache.Get(), transaction.Ticket);

        _db.Transactions.Remove(transaction);
        _db.SaveChanges();

        return Json(new { success = true });
    }

    // GET: Transaction/Edit/5
    public IActionResult Edit(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var setup = _setupCache.Get();
        ViewBag.SavePicture = setup.SavePicture;
        var ticketsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "tickets");
        ViewBag.HasInImage = System.IO.File.Exists(Path.Combine(ticketsDir, $"{id}_in.jpg"));
        ViewBag.HasOutImage = System.IO.File.Exists(Path.Combine(ticketsDir, $"{id}_out.jpg"));

        PopulateDropdowns();
        SetCustomFieldViewBags(id);
        return View(transaction);
    }

    // POST: Transaction/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(string id, Transaction transaction)
    {
        if (id != transaction.Ticket) return BadRequest();

        var existing = _db.Transactions.Find(id);
        if (existing == null) return NotFound();

        var customFields = GetActiveCustomFields();
        var cfErrors = ValidateCustomFieldValues(customFields);
        if (ValidateRequiredBin(transaction) is { } binError) cfErrors.Add(binError);
        if (cfErrors.Count > 0)
        {
            TempData["Error"] = string.Join(" ", cfErrors);
            return RedirectToAction("Edit", new { id });
        }

        // A weight change invalidates the driver signature — it certified the
        // weights shown when the driver signed.
        if (existing.InWeight != transaction.InWeight || existing.OutWeight != transaction.OutWeight)
            DeleteSignatureFile(id);

        existing.InWeight = transaction.InWeight;
        existing.OutWeight = transaction.OutWeight;
        existing.DateIn = transaction.DateIn;
        existing.DateOut = transaction.DateOut;
        ApplyVisibleFields(existing, transaction, _setupCache.Get());
        existing.Void = transaction.Void;
        existing.SentToQuickBooks = false; // Reset QB flag on edit

        SaveCustomFieldValues(id, customFields);
        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, existing);

        return RedirectToAction("CompletedTrucks");
    }

    private static string TicketsImageDir => Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "tickets");

    private static bool HasImage(string ticket, string direction)
        => System.IO.File.Exists(Path.Combine(TicketsImageDir, $"{ticket}_{direction}.jpg"));

    /// <summary>
    /// Persist the truck's empty weight so future weigh-ins for the same truck can
    /// auto-recall it. Mirrors KioskController.UpdateRetainedTare — same matching
    /// rules and auto-create-on-miss behavior.
    /// </summary>
    private void UpdateRetainedTare(Transaction tx)
    {
        if (tx.OutWeight == null)
        {
            var msg = $"skipped for ticket {tx.Ticket}: OutWeight is null";
            _log.LogWarning(msg);
            Console.WriteLine($"[RetainedTare] {msg}");
            return;
        }
        var truckId = tx.TruckId?.Trim();
        var carrier = tx.Carrier?.Trim();
        if (string.IsNullOrEmpty(truckId) || string.IsNullOrEmpty(carrier))
        {
            var msg = $"skipped for ticket {tx.Ticket}: TruckId='{tx.TruckId}' Carrier='{tx.Carrier}' — both required";
            _log.LogWarning(msg);
            Console.WriteLine($"[RetainedTare] {msg}");
            return;
        }

        var tare = Math.Min(tx.InWeight, tx.OutWeight.Value);
        var when = tx.DateOut ?? DateTime.UtcNow;

        var truck = _db.Trucks.FirstOrDefault(t =>
            t.TruckId.ToLower() == truckId.ToLower() &&
            t.CarrierName.ToLower() == carrier.ToLower());

        if (truck == null)
        {
            truck = new Truck
            {
                TruckId = truckId,
                CarrierName = carrier,
                UseAtKiosk = true,
                Description = "Auto-created from weigh-out",
                RetainedTare = tare,
                RetainedTareUpdated = when
            };
            _db.Trucks.Add(truck);
            var msg = $"auto-created Truck '{truckId}' / '{carrier}' with tare {tare} lb (ticket {tx.Ticket})";
            _log.LogInformation(msg);
            Console.WriteLine($"[RetainedTare] {msg}");
        }
        else
        {
            truck.RetainedTare = tare;
            truck.RetainedTareUpdated = when;
            var msg = $"updated Truck '{truck.TruckId}' / '{truck.CarrierName}' to {tare} lb (ticket {tx.Ticket})";
            _log.LogInformation(msg);
            Console.WriteLine($"[RetainedTare] {msg}");
        }
    }

    // API: GET api/transactions/inbound
    [HttpGet("api/transactions/inbound")]
    public IActionResult GetInbound()
    {
        var rows = _db.Transactions
            .Where(t => t.DateOut == null && !t.Void)
            .OrderByDescending(t => t.DateIn)
            .ToList();
        var customValues = GetCustomValueMap(rows.Select(t => t.Ticket).ToList());

        var transactions = rows
            .Select(t => new
            {
                t.Ticket,
                t.Customer,
                t.Carrier,
                t.TruckId,
                t.Commodity,
                t.InWeight,
                DateIn = t.DateIn.AsUtc(),
                t.Location,
                t.Destination,
                t.Bin,
                t.Notes,
                t.ManualInbound,
                HasInImage = HasImage(t.Ticket, "in"),
                CustomFields = customValues.GetValueOrDefault(t.Ticket)
            })
            .ToList();

        return Json(transactions);
    }

    // API: GET api/transactions/completed
    [HttpGet("api/transactions/completed")]
    public IActionResult GetCompleted(DateTime? startDate, DateTime? endDate)
    {
        // Filter range: query strings come in as user-local dates in the
        // configured display TZ. Convert to UTC bounds so the comparison
        // against stored UTC values is correct regardless of host TZ.
        // endInclusive = midnight at the START of (endDate + 1) in the
        // display TZ — so an endDate of 2026-04-29 includes everything up
        // through 2026-04-29 23:59:59.999 local time.
        var localStart = startDate ?? DateTime.Today.AddDays(-30);
        var localEnd   = (endDate ?? DateTime.Today).Date.AddDays(1);
        var start = AppTimeZone.ToUtc(localStart);
        var endInclusive = AppTimeZone.ToUtc(localEnd);

        var rows = _db.Transactions
            .Where(t => t.DateOut != null && t.DateIn >= start && t.DateIn < endInclusive)
            .OrderByDescending(t => t.DateIn)
            .ToList();
        var customValues = GetCustomValueMap(rows.Select(t => t.Ticket).ToList());

        var transactions = rows
            .Select(t => new
            {
                t.Ticket,
                t.Customer,
                t.Carrier,
                t.TruckId,
                t.Commodity,
                t.InWeight,
                t.OutWeight,
                t.NetWeight,
                t.GrossWeight,
                t.TareWeight,
                DateIn = t.DateIn.AsUtc(),
                DateOut = t.DateOut.AsUtc(),
                t.Location,
                t.Destination,
                t.Bin,
                t.Notes,
                t.Void,
                t.SentToQuickBooks,
                HasInImage = HasImage(t.Ticket, "in"),
                HasOutImage = HasImage(t.Ticket, "out"),
                CustomFields = customValues.GetValueOrDefault(t.Ticket)
            })
            .ToList();

        return Json(transactions);
    }

    // API: POST api/transactions/update
    [HttpPost("api/transactions/update")]
    public IActionResult UpdateTransaction([FromBody] Transaction transaction)
    {
        var existing = _db.Transactions.Find(transaction.Ticket);
        if (existing == null) return NotFound();

        if (ValidateRequiredBin(transaction) is { } binError)
            return BadRequest(new { message = binError });

        // A weight change invalidates the driver signature (see Edit POST).
        if (existing.InWeight != transaction.InWeight || existing.OutWeight != transaction.OutWeight)
            DeleteSignatureFile(existing.Ticket);

        existing.InWeight = transaction.InWeight;
        existing.OutWeight = transaction.OutWeight;
        existing.DateIn = transaction.DateIn;
        existing.DateOut = transaction.DateOut;
        // Skip hidden fields — the grid doesn't send them, and writing the
        // resulting nulls would erase stored values.
        ApplyVisibleFields(existing, transaction, _setupCache.Get());
        existing.Void = transaction.Void;
        existing.SentToQuickBooks = false; // Reset QB flag on edit

        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, existing);

        return Json(new { success = true });
    }

    // API: POST api/transactions/mark-sent-to-qb
    [HttpPost("api/transactions/mark-sent-to-qb")]
    public IActionResult MarkSentToQuickBooks([FromBody] List<string> ticketIds)
    {
        if (ticketIds == null || ticketIds.Count == 0)
            return BadRequest(new { error = "No ticket IDs provided." });

        var tickets = _db.Transactions
            .Where(t => ticketIds.Contains(t.Ticket))
            .ToList();

        foreach (var t in tickets)
            t.SentToQuickBooks = true;

        _db.SaveChanges();
        return Json(new { marked = tickets.Count });
    }

    // API: POST api/ticket/{id}/image?direction=in|out — upload ticket image
    [HttpPost("api/ticket/{id}/image")]
    public async Task<IActionResult> UploadTicketImage(string id, [FromQuery] string direction, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });
        if (direction != "in" && direction != "out")
            return BadRequest(new { error = "Direction must be 'in' or 'out'." });

        var dir = TicketsImageDir;
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, $"{id}_{direction}.jpg");
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Notify all web clients that an image is available
        await _hub.Clients.All.SendAsync("ImageCaptured", new { ticket = id, direction });

        return Ok(new { success = true });
    }

    // API: GET api/ticket/{id}/image?direction=in|out — serve ticket image
    [HttpGet("api/ticket/{id}/image")]
    public IActionResult GetTicketImage(string id, [FromQuery] string direction)
    {
        if (direction != "in" && direction != "out")
            return BadRequest();

        var filePath = Path.Combine(TicketsImageDir, $"{id}_{direction}.jpg");
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        return PhysicalFile(filePath, "image/jpeg");
    }

    public class SignatureUpload
    {
        public string? ImageData { get; set; } // "data:image/png;base64,..." from a capture canvas
    }

    // API: POST api/signature/{id} — save the driver signature for a ticket.
    // Called by the Weigh Out overlay (Operator mode) and the /SignaturePad
    // page (RemotePad mode). Overwrites any previous signature (re-sign).
    [HttpPost("api/signature/{id}")]
    public async Task<IActionResult> UploadSignature(string id, [FromBody] SignatureUpload payload)
    {
        // Ticket lookup doubles as path-traversal protection: the id is only
        // used in a filename once it's proven to be a real ticket key.
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var data = payload?.ImageData;
        if (string.IsNullOrEmpty(data))
            return BadRequest(new { error = "No image data provided." });

        const string prefix = "data:image/png;base64,";
        if (!data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Image data must be a PNG data URL." });

        byte[] bytes;
        try { bytes = Convert.FromBase64String(data[prefix.Length..]); }
        catch (FormatException) { return BadRequest(new { error = "Invalid base64 image data." }); }

        if (bytes.Length == 0 || bytes.Length > 1024 * 1024)
            return BadRequest(new { error = "Signature image must be between 1 byte and 1 MB." });

        Directory.CreateDirectory(TicketsImageDir);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(TicketsImageDir, $"{id}_Signature.png"), bytes);

        // Tell the operator's Weigh Out page (and anyone else) the signature landed
        await _hub.Clients.All.SendAsync("SignatureCaptured", new { ticket = id });

        return Ok(new { success = true });
    }

    // API: GET api/signature/{id} — serve the driver signature PNG
    [HttpGet("api/signature/{id}")]
    public IActionResult GetSignature(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        var filePath = Path.Combine(TicketsImageDir, $"{id}_Signature.png");
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        return PhysicalFile(filePath, "image/png");
    }

    /// <summary>
    /// Discard a captured signature. Called when the weigh-out is cancelled or
    /// the record changes after signing — the signature certified what was on
    /// screen when the driver signed, so it can't survive either.
    /// </summary>
    [HttpDelete("api/signature/{id}")]
    public IActionResult DeleteSignature(string id)
    {
        var transaction = _db.Transactions.Find(id);
        if (transaction == null) return NotFound();

        DeleteSignatureFile(id);
        return Ok(new { success = true });
    }

    /// <summary>Remove the stored signature PNG for a ticket, if any.</summary>
    private static void DeleteSignatureFile(string ticketId)
    {
        var filePath = Path.Combine(TicketsImageDir, $"{ticketId}_Signature.png");
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
            Console.WriteLine($"[Signature] cleared for ticket {ticketId}");
        }
    }
}
