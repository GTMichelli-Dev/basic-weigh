using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

public class KioskController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly IScaleService _scaleService;
    private readonly IHubContext<ScaleHub> _hub;
    private readonly AppSetupCache _setupCache;
    private readonly ILogger<KioskController> _log;

    public KioskController(ScaleDbContext db, IScaleService scaleService, IHubContext<ScaleHub> hub, AppSetupCache setupCache, ILogger<KioskController> log)
    {
        _db = db;
        _scaleService = scaleService;
        _hub = hub;
        _setupCache = setupCache;
        _log = log;
    }

    public IActionResult Index([FromQuery(Name = "service-id")] string? serviceId = null,
                               [FromQuery(Name = "printer-id")] string? printerId = null,
                               [FromQuery(Name = "scale-id")] int? scaleId = null,
                               [FromQuery(Name = "reader-id")] string? readerId = null)
    {
        var setup = _setupCache.Get();
        ViewBag.ServiceId = serviceId ?? "";
        ViewBag.PrinterId = printerId ?? "";
        ViewBag.HasPrinter = !string.IsNullOrEmpty(serviceId) && !string.IsNullOrEmpty(printerId);

        // Card reader this kiosk listens to, as "serviceId:readerId" (Launch
        // Kiosk dialog fills it in). Empty means this kiosk ignores card reads
        // and runs the normal touchscreen flow.
        ViewBag.ReaderId = setup.UseCardReader ? (readerId ?? "") : "";

        // Each kiosk device is mapped to one site scale, chosen in the Launch
        // Kiosk dialog (?scale-id=). Falls back to the default (first active)
        // scale so a bare /Kiosk URL still works on single-scale sites.
        var scale = SiteScales.Resolve(_db, scaleId);
        ViewBag.KioskScaleDbId = scale?.Id ?? 0;
        ViewBag.KioskScaleName = scale?.Name ?? "";
        // Hardware feed id, used to filter SignalR ScaleWeight pushes.
        ViewBag.ScaleId = scale?.HardwareId ?? "";
        return View(setup);
    }

    [HttpGet("api/kiosk/lists")]
    public IActionResult GetLists(int? scaleId = null)
    {
        // A kiosk is mapped to one scale; commodities and bins limited to a
        // location (Edit Tables) only show on kiosks whose scale is there.
        var siteId = scaleId.HasValue ? _db.Scales.Find(scaleId.Value)?.SiteId : null;

        var commodities = _db.Commodities
            .Where(c => c.Active && c.UseAtKiosk).ForSite(siteId)
            .OrderBy(c => c.CommodityName)
            .Select(c => c.CommodityName)
            .ToList();

        var customers = _db.Customers
            .Where(c => c.Active && c.UseAtKiosk)
            .OrderBy(c => c.CustomerName)
            .Select(c => c.CustomerName)
            .ToList();

        // Kiosk Carrier prompt: only show entries that are explicitly marked
        // as Carriers in master data. Customers who also haul can be added to
        // Carriers via the "Add to Carriers" button on the MasterData
        // Customers grid; from then on they appear in this list.
        var carriers = _db.Carriers
            .Where(c => c.Active && c.UseAtKiosk)
            .OrderBy(c => c.CarrierName)
            .Select(c => c.CarrierName)
            .ToList();

        var locations = _db.Locations
            .Where(l => l.Active && l.UseAtKiosk)
            .OrderBy(l => l.LocationName)
            .Select(l => l.LocationName)
            .ToList();

        var destinations = _db.Destinations
            .Where(d => d.Active && d.UseAtKiosk)
            .OrderBy(d => d.DestinationName)
            .Select(d => d.DestinationName)
            .ToList();

        // Bin prompt only exists while Bin Inventory is enabled — return an
        // empty list when off so a stale kiosk page auto-skips the prompt.
        var setup = _setupCache.Get();
        var bins = setup.UseBinInventory
            ? _db.Bins
                .Where(b => b.Active && b.UseAtKiosk).ForSite(siteId)
                .OrderBy(b => b.BinName)
                .Select(b => b.BinName)
                .ToList()
            : new List<string>();

        // Lock Bin to Commodity: bin → its current contents, so the bin
        // prompt can filter to bins that match the chosen commodity and
        // auto-set a skipped commodity from the chosen bin.
        var binCommodities = setup.UseBinInventory && setup.BinCommodityLock
            ? BinInventory.HeldCommodities(_db)
            : new Dictionary<string, string>();

        // Kiosk-enabled custom fields. Only constrained inputs prompt at the
        // kiosk: numeric fields and list-backed text fields (free text is
        // filtered out even if the flag was somehow set). Cascading sub-fields
        // also carry their parent name + per-parent choice map so the prompt
        // can filter by the answer already collected in this flow.
        var eligibleFields = _db.CustomFields
            .Where(f => f.Active && f.PromptAtKiosk)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToList()
            .Where(f => f.IsKioskEligible())
            .ToList();

        var dependentIds = eligibleFields.Where(f => f.ParentField != null).Select(f => f.Id).ToList();
        var valueMaps = _db.CustomFieldListValues
            .Where(v => dependentIds.Contains(v.CustomFieldId))
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Value)
            .AsEnumerable()
            .GroupBy(v => v.CustomFieldId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(v => v.ParentValue)
                      .ToDictionary(pg => pg.Key, pg => pg.Select(v => v.Value).ToList()));

        var customFields = eligibleFields
            .Select(f => new
            {
                id = f.Id,
                name = f.Name,
                fieldType = f.FieldType,
                required = f.Required,
                listValues = f.GetListValues(),
                minValue = f.MinValue,
                maxValue = f.MaxValue,
                precision = f.Precision,
                parentField = f.ParentField,
                valueMap = f.ParentField != null ? valueMaps.GetValueOrDefault(f.Id) ?? new Dictionary<string, List<string>>() : null
            })
            .ToList();

        return Json(new { commodities, customers, carriers, locations, destinations, bins, binCommodities, customFields });
    }

    [HttpGet("api/kiosk/trucks/{carrier}")]
    public IActionResult GetTrucks(string carrier)
    {
        var trucks = _db.Trucks
            .Where(t => t.CarrierName == carrier && t.UseAtKiosk)
            .OrderBy(t => t.TruckId)
            .Select(t => t.TruckId)
            .ToList();

        return Json(trucks);
    }

    /// <summary>
    /// Find the open inbound ticket (no DateOut, not voided) for a given
    /// (Carrier, TruckId). Used by the kiosk so a driver who walks through
    /// the inbound prompt sequence on a return trip is automatically
    /// switched to the weigh-out flow instead of being forced to back out
    /// and key in a ticket number.
    /// </summary>
    [HttpGet("api/kiosk/open-ticket-for-truck")]
    public IActionResult FindOpenTicketForTruck([FromQuery] string carrier, [FromQuery] string truckId)
    {
        if (string.IsNullOrWhiteSpace(carrier) || string.IsNullOrWhiteSpace(truckId))
            return BadRequest(new { message = "carrier and truckId are required" });

        var c = carrier.Trim();
        var t = truckId.Trim();
        var transaction = _db.Transactions
            .Where(x => !x.Void && x.DateOut == null && x.Carrier == c && x.TruckId == t)
            .OrderByDescending(x => x.DateIn)
            .FirstOrDefault();

        if (transaction == null) return NotFound(new { message = "No open ticket" });

        return Json(new
        {
            ticket = transaction.Ticket,
            inWeight = transaction.InWeight,
            dateIn = transaction.DateIn.AsUtc(),
            customer = transaction.Customer,
            carrier = transaction.Carrier,
            truckId = transaction.TruckId,
            commodity = transaction.Commodity,
            location = transaction.Location,
            destination = transaction.Destination,
            bin = transaction.Bin
        });
    }

    /// <summary>
    /// Resolve a card presented at a kiosk reader. Tells the kiosk whether this
    /// is a weigh-in or a weigh-out, what the card already answers, and which
    /// prompts it still has to run (fields the kiosk would refuse to skip and
    /// the card doesn't carry).
    /// </summary>
    [HttpPost("api/kiosk/card")]
    public IActionResult ResolveCard([FromBody] KioskCardRequest request)
    {
        var setup = _setupCache.Get();
        if (!setup.UseCardReader)
            return Ok(new { ok = false, reason = "disabled", message = "Card weighing is turned off" });

        var number = (request.CardNumber ?? "").Trim();
        if (number.Length == 0)
            return BadRequest(new { ok = false, reason = "empty", message = "No card number" });

        var card = _db.Cards.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.CardNumber, number, StringComparison.OrdinalIgnoreCase));

        if (card == null)
            return Ok(new { ok = false, reason = "unknown", message = "Card Not Recognized" });
        if (!card.Enabled)
            return Ok(new { ok = false, reason = "disabled-card", message = "Card Disabled" });

        // An open ticket outranks the issued flag: a driver who weighed in must
        // always be able to weigh out, even if the card was deactivated behind
        // them. Voided/closed tickets fall through to the weigh-in path.
        Transaction? openTicket = null;
        if (!string.IsNullOrEmpty(card.OpenTicket))
        {
            openTicket = _db.Transactions
                .FirstOrDefault(t => t.Ticket == card.OpenTicket && !t.Void && t.DateOut == null);
            if (openTicket == null) card.OpenTicket = null; // stale link
        }

        if (openTicket == null && !card.Issued)
            return Ok(new { ok = false, reason = "not-issued", message = "Card Not Active — See Loader Operator" });

        var values = CardFields.ValuesOf(_db, card);
        var siteId = request.ScaleId.HasValue ? _db.Scales.Find(request.ScaleId.Value)?.SiteId : null;
        var missing = CardFields.Describe(_db, setup, siteId)
            .Where(d => d.Required && !values.ContainsKey(d.Key))
            .Select(d => d.Key)
            .ToList();

        if (openTicket != null)
        {
            return Ok(new
            {
                ok = true,
                action = "weighout",
                card = new { id = card.Id, cardNumber = card.CardNumber, description = card.Description },
                values,
                ticket = new
                {
                    ticket = openTicket.Ticket,
                    inWeight = openTicket.InWeight,
                    dateIn = openTicket.DateIn.AsUtc(),
                    customer = openTicket.Customer,
                    carrier = openTicket.Carrier,
                    truckId = openTicket.TruckId,
                    commodity = openTicket.Commodity,
                    location = openTicket.Location,
                    destination = openTicket.Destination,
                    bin = openTicket.Bin
                }
            });
        }

        // No open ticket on the card, but the truck it names might already be
        // in the yard from a keyed-in weigh-in. Adopt that ticket rather than
        // opening a second one for the same truck.
        if (values.TryGetValue("carrier", out var cardCarrier)
            && values.TryGetValue("truck", out var cardTruck))
        {
            var truckTicket = _db.Transactions
                .Where(t => !t.Void && t.DateOut == null && t.Carrier == cardCarrier && t.TruckId == cardTruck)
                .OrderByDescending(t => t.DateIn)
                .FirstOrDefault();

            if (truckTicket != null)
            {
                return Ok(new
                {
                    ok = true,
                    action = "weighout",
                    card = new { id = card.Id, cardNumber = card.CardNumber, description = card.Description },
                    values,
                    ticket = new
                    {
                        ticket = truckTicket.Ticket,
                        inWeight = truckTicket.InWeight,
                        dateIn = truckTicket.DateIn.AsUtc(),
                        customer = truckTicket.Customer,
                        carrier = truckTicket.Carrier,
                        truckId = truckTicket.TruckId,
                        commodity = truckTicket.Commodity,
                        location = truckTicket.Location,
                        destination = truckTicket.Destination,
                        bin = truckTicket.Bin
                    }
                });
            }
        }

        // Retained tare: report it so the kiosk can tell the driver the load
        // will finish in one weighment. WeighIn does the actual auto-complete.
        int? retainedTare = null;
        if (setup.UseRetainedTare
            && values.TryGetValue("carrier", out var c2)
            && values.TryGetValue("truck", out var t2))
        {
            var truck = _db.Trucks.FirstOrDefault(x => x.TruckId == t2 && x.CarrierName == c2);
            if (truck?.RetainedTare.HasValue == true
                && (!setup.AutoClearStaleRetainedTare
                    || (truck.RetainedTareUpdated?.Date ?? DateTime.MinValue) >= DateTime.Today))
            {
                retainedTare = truck.RetainedTare;
            }
        }

        _db.SaveChanges(); // persist any stale-link clearing above

        return Ok(new
        {
            ok = true,
            action = "weighin",
            card = new { id = card.Id, cardNumber = card.CardNumber, description = card.Description },
            values,
            missingRequired = missing,
            retainedTare
        });
    }

    [HttpGet("api/kiosk/ticket/{ticketNumber}")]
    public IActionResult FindTicket(string ticketNumber)
    {
        var transaction = _db.Transactions
            .FirstOrDefault(t => t.Ticket == ticketNumber);

        if (transaction == null)
            return NotFound(new { message = "Ticket not found" });

        if (transaction.Void)
            return BadRequest(new { message = "Ticket is voided" });

        if (transaction.DateOut != null)
            return BadRequest(new { message = "Ticket already completed" });

        return Json(new
        {
            ticket = transaction.Ticket,
            inWeight = transaction.InWeight,
            dateIn = transaction.DateIn,
            customer = transaction.Customer,
            carrier = transaction.Carrier,
            truckId = transaction.TruckId,
            commodity = transaction.Commodity,
            location = transaction.Location,
            destination = transaction.Destination,
            bin = transaction.Bin
        });
    }

    [HttpPost("api/kiosk/weighin")]
    public async Task<IActionResult> WeighIn([FromBody] KioskWeighInRequest request)
    {
        var setup = _db.AppSetup.First();

        // Card-driven weigh-in: the card's stored values fill in anything the
        // kiosk didn't collect. Merging server-side (rather than trusting the
        // kiosk's merge) means a stale kiosk page can't drop card data, and a
        // card edited after the driver pulled up still wins on the blanks.
        Card? card = null;
        Dictionary<string, string> cardValues = new();
        if (setup.UseCardReader && !string.IsNullOrWhiteSpace(request.CardNumber))
        {
            card = _db.Cards.AsEnumerable().FirstOrDefault(c =>
                string.Equals(c.CardNumber, request.CardNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (card == null || !card.Enabled)
                return BadRequest(new { message = "Card not recognized." });
            if (!card.Issued && string.IsNullOrEmpty(card.OpenTicket))
                return BadRequest(new { message = "Card is not active — see the loader operator." });

            cardValues = CardFields.ValuesOf(_db, card);
            request.Commodity ??= cardValues.GetValueOrDefault("commodity");
            request.Customer ??= cardValues.GetValueOrDefault("customer");
            request.Carrier ??= cardValues.GetValueOrDefault("carrier");
            request.TruckId ??= cardValues.GetValueOrDefault("truck");
            request.Location ??= cardValues.GetValueOrDefault("location");
            request.Destination ??= cardValues.GetValueOrDefault("destination");
            request.Bin ??= cardValues.GetValueOrDefault("bin");
        }

        // Ensure ticket number doesn't collide with existing tickets
        while (_db.Transactions.Any(t => t.Ticket == setup.TicketNumber.ToString()))
        {
            setup.TicketNumber++;
        }
        var ticketNumber = setup.TicketNumber.ToString();

        // Look up retained tare for this truck. Match the (TruckId, Carrier) pair
        // that already uniquely identifies a truck (ScaleDbContext unique index).
        // Skip entirely if the feature toggle is off.
        Truck? truck = null;
        if (setup.UseRetainedTare
            && !string.IsNullOrEmpty(request.TruckId)
            && !string.IsNullOrEmpty(request.Carrier))
        {
            truck = _db.Trucks.FirstOrDefault(t =>
                t.TruckId == request.TruckId && t.CarrierName == request.Carrier);

            // Tares from a previous date are auto-expired — load may have changed
            // overnight, so a stale tare can't be trusted. Gated on
            // AutoClearStaleRetainedTare so an operator can disable midnight
            // expiry if their fleet's tares are stable across days.
            if (setup.AutoClearStaleRetainedTare
                && truck?.RetainedTare.HasValue == true
                && (truck.RetainedTareUpdated?.Date ?? DateTime.MinValue) < DateTime.Today)
            {
                Console.WriteLine($"[RetainedTare] cleared stale tare for '{truck.TruckId}' / '{truck.CarrierName}' (last seen {truck.RetainedTareUpdated:yyyy-MM-dd})");
                _log.LogInformation("RetainedTare: cleared stale tare for {TruckId}/{Carrier} (last seen {When})",
                    truck.TruckId, truck.CarrierName, truck.RetainedTareUpdated);
                truck.RetainedTare = null;
                truck.RetainedTareUpdated = null;
            }
        }
        bool tareApplied = truck?.RetainedTare.HasValue == true;
        var now = DateTime.UtcNow;

        // For a retained-tare auto-completion, DateIn represents when the truck
        // was originally tared (the "inbound" weighing that established the tare).
        // DateOut is the current visit. Without this, both dates would be the
        // same timestamp because the whole transaction happens in one call —
        // accurate but useless on the report.
        var dateIn = tareApplied
            ? (truck!.RetainedTareUpdated ?? now)
            : now;

        var transaction = new Transaction
        {
            Ticket = ticketNumber,
            InWeight = request.Weight,
            InScale = request.ScaleName,
            DateIn = dateIn,
            Commodity = request.Commodity,
            Customer = request.Customer,
            Carrier = request.Carrier,
            TruckId = request.TruckId,
            Location = request.Location,
            Destination = request.Destination,
            Bin = request.Bin,
            Notes = card?.Notes,
            CardNumber = card?.CardNumber,
            Void = false,
            ManualInbound = false
        };

        // Lock Bin to Commodity backstop — the prompt flow filters bins
        // client-side; this catches stale kiosk pages and direct posts.
        if (BinInventory.ValidateTicket(_db, setup, transaction) is { } binLockError)
            return BadRequest(new { message = binLockError });

        if (tareApplied)
        {
            transaction.OutWeight = truck!.RetainedTare;
            transaction.DateOut = now;
            transaction.ManualOutbound = false;
            // The retained tare came from a stored value, not this visit's
            // scale — only the live (gross) weighment records the kiosk scale.
        }

        setup.TicketNumber++;
        _db.AppSetup.Update(setup);
        _db.Transactions.Add(transaction);
        var savedFieldIds = SaveKioskCustomFields(ticketNumber, request.CustomFields);
        if (card != null) SaveCardCustomFields(ticketNumber, card, savedFieldIds);

        // Bind the card to this ticket, or release it outright when the
        // retained tare closed the load in one weighment.
        bool cardRecycled = false;
        if (card != null)
        {
            if (tareApplied)
            {
                cardRecycled = card.RecyclesUnder(setup);
                CardFields.Release(card, setup, ticketNumber);
            }
            else
            {
                card.OpenTicket = ticketNumber;
                card.LastUsedAt = DateTime.UtcNow;
            }
        }
        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, transaction);
        _setupCache.Invalidate();

        // Notify all clients
        if (tareApplied)
        {
            await _hub.Clients.All.SendAsync("TicketCompleted",
                new { ticket = ticketNumber, type = "weighout" });
        }
        else
        {
            await _hub.Clients.All.SendAsync("TicketCreated",
                new { ticket = ticketNumber, type = "weighin" });
        }

        // Camera capture: a tare-completed ticket is a weigh-out (use outbound camera);
        // a regular weigh-in uses the inbound camera. Same convention as the admin flow.
        if (setup.SavePicture)
        {
            await SendCameraCapture(
                ticketNumber,
                tareApplied ? "out" : "in",
                tareApplied ? setup.OutboundCameraId : setup.InboundCameraId);
        }

        // Print rules:
        //   - Tare-applied weigh-in → completed ticket → print on the outbound printer.
        //   - Plain weigh-in → normally prints to the inbound printer; SUPPRESSED when
        //     Retained Tare is on, since the in-leg is just data capture for the
        //     eventual closing ticket. The closing weigh-out (or the next visit's
        //     auto-completed weigh-in) is what gets printed.
        bool suppressInboundPrint = setup.UseRetainedTare && !tareApplied;
        if (!suppressInboundPrint)
        {
            await SendPrintCommand(ticketNumber, tareApplied ? "weighout" : "weighin", request.PrinterId, request.ScaleName);
        }

        return Json(new
        {
            ticket = ticketNumber,
            inWeight = transaction.InWeight,
            outWeight = transaction.OutWeight,
            dateOut = transaction.DateOut,
            tareApplied,
            retainedTare = truck?.RetainedTare,
            retainedTareUpdated = truck?.RetainedTareUpdated,
            suppressInboundPrint,
            // Card guidance for the completion screen: keep the card for the
            // next load, or hand it back to the loader operator.
            cardUsed = card != null,
            cardClosed = card != null && tareApplied,
            cardRecycled
        });
    }

    [HttpPost("api/kiosk/weighout")]
    public async Task<IActionResult> WeighOut([FromBody] KioskWeighOutRequest request)
    {
        var transaction = _db.Transactions
            .FirstOrDefault(t => t.Ticket == request.Ticket && !t.Void && t.DateOut == null);

        if (transaction == null)
            return NotFound(new { message = "Ticket not found" });

        transaction.OutWeight = request.Weight;
        transaction.OutScale = request.ScaleName;
        transaction.DateOut = DateTime.UtcNow;
        transaction.ManualOutbound = false;

        // Outbound-only prompts can override the values captured at weigh-in.
        // Empty / null means "no change" — the kiosk JS already coerces a blank
        // selection to null so it doesn't blow away an existing value here.
        if (!string.IsNullOrEmpty(request.Destination)) transaction.Destination = request.Destination;
        if (!string.IsNullOrEmpty(request.Commodity))   transaction.Commodity   = request.Commodity;
        if (!string.IsNullOrEmpty(request.Customer))    transaction.Customer    = request.Customer;
        if (!string.IsNullOrEmpty(request.Location))    transaction.Location    = request.Location;
        if (!string.IsNullOrEmpty(request.Bin))         transaction.Bin         = request.Bin;

        // Lock Bin to Commodity backstop on the merged ticket. Returning here
        // leaves the tracked entity dirty but unsaved — nothing persists.
        if (BinInventory.ValidateTicket(_db, _setupCache.Get(), transaction) is { } binLockError)
            return BadRequest(new { message = binLockError });

        // Persist retained tare on the matching truck (feature-gated). Tare = lower of
        // the two weights, matching how Transaction.TareWeight is computed.
        var useRetainedTare = _setupCache.Get().UseRetainedTare;
        var weighOutMsg = $"WeighOut ticket {transaction.Ticket}: UseRetainedTare={useRetainedTare} TruckId='{transaction.TruckId}' Carrier='{transaction.Carrier}'";
        _log.LogInformation(weighOutMsg);
        Console.WriteLine($"[RetainedTare] {weighOutMsg}");
        if (useRetainedTare)
        {
            UpdateRetainedTare(transaction);
        }

        // The load is done: give the card back to the pool. Recycling leaves it
        // issued with its stored values so the same driver can run another
        // load; otherwise it goes dead until the loader operator re-issues it.
        var outSetupForCard = _setupCache.Get();
        var card = CardFields.ForTicket(_db, transaction);
        bool cardRecycled = false;
        if (card != null)
        {
            cardRecycled = card.RecyclesUnder(outSetupForCard);
            CardFields.Release(card, outSetupForCard, transaction.Ticket);
            transaction.CardNumber ??= card.CardNumber;
        }

        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, transaction);

        // Notify all clients that a ticket was completed
        await _hub.Clients.All.SendAsync("TicketCompleted", new { ticket = transaction.Ticket, type = "weighout" });

        // Camera capture (outbound) — same convention as the admin web flow.
        var outSetup = _setupCache.Get();
        if (outSetup.SavePicture)
        {
            await SendCameraCapture(transaction.Ticket, "out", outSetup.OutboundCameraId);
        }

        // Print the ticket
        await SendPrintCommand(transaction.Ticket.ToString(), "weighout", request.PrinterId, request.ScaleName);

        return Json(new
        {
            ticket = transaction.Ticket,
            cardUsed = card != null,
            cardClosed = card != null,
            cardRecycled
        });
    }

    /// <summary>
    /// Send a CaptureImage command to the camera service identified by the
    /// "serviceId:cameraId" string from AppSetup. No-op if the setting is empty.
    /// </summary>
    private async Task SendCameraCapture(string ticketId, string direction, string? cameraIdSetting)
    {
        if (string.IsNullOrEmpty(cameraIdSetting)) return;
        var parts = cameraIdSetting.Split(':', 2);
        var serviceId = parts.Length > 1 ? parts[0] : "default";
        var cameraId = parts.Length > 1 ? parts[1] : parts[0];
        await _hub.Clients.Group($"Camera_{serviceId}").SendAsync("CaptureImage",
            new { ticket = ticketId, direction, cameraId });
    }

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

        // Match the kiosk's existing (TruckId, CarrierName) unique key. Use a
        // case-insensitive comparison so subtle casing/spacing drift between the
        // master row and the value typed at the kiosk doesn't silently skip the
        // update.
        var truck = _db.Trucks.FirstOrDefault(t =>
            t.TruckId.ToLower() == truckId.ToLower() &&
            t.CarrierName.ToLower() == carrier.ToLower());

        if (truck == null)
        {
            // Master data doesn't have this truck yet (e.g. it was deleted, or the
            // kiosk wrote a value not in the dropdown). Create it so the retained-
            // tare feature works without forcing the operator to set up master data
            // first. The admin page will show it and the operator can edit/clear.
            truck = new Truck
            {
                TruckId = truckId,
                CarrierName = carrier,
                UseAtKiosk = true,
                Description = "Auto-created from kiosk weigh-out",
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

    [HttpPost("api/kiosk/reprint/{ticketId}")]
    public async Task<IActionResult> Reprint(string ticketId, [FromQuery] string? printerId = null)
    {
        var transaction = _db.Transactions.Find(ticketId);
        if (transaction == null)
            return NotFound(new { message = "Ticket not found" });

        var type = transaction.DateOut != null ? "weighout" : "weighin";

        await SendPrintCommand(ticketId, type, printerId,
            type == "weighout" ? (transaction.OutScale ?? transaction.InScale) : transaction.InScale);

        return Ok(new { message = "Reprint requested" });
    }

    /// <summary>
    /// Sends a print command to the correct print service.
    /// printerId format: "serviceId:printerId" (e.g., "office-1:BIXOLON BK3-3")
    /// If not set and demo mode: uses "KioskPrinter"
    /// If not set and not demo: the capturing scale's printer assignment,
    /// falling back to the site-wide Setup defaults.
    /// </summary>
    private async Task SendPrintCommand(string ticketId, string type, string? printerId, string? scaleName = null)
    {
        var setup = _setupCache.Get();

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
                printerId = SiteScales.ResolvePrinter(_db, scaleName, type == "weighout", setup);
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
            await _hub.Clients.Group($"Print_{serviceId}").SendAsync("PrintTicket",
                new { ticketId, type, printerId = printerName });
        }
        else
        {
            // Broadcast to all print services
            await _hub.Clients.Group("PrintClients").SendAsync("PrintTicket",
                new { ticketId, type, printerId = printerName });
        }
    }

    /// <summary>
    /// Server-side backstop for kiosk-collected custom field values. The kiosk
    /// UI already constrains input (dropdown choices, numeric keypad with
    /// min/max/precision), so an invalid value here means a stale kiosk page
    /// or a hand-crafted request — it is silently dropped rather than failing
    /// the weigh-in, since a truck is standing on the scale. Caller SaveChanges().
    /// </summary>
    /// <returns>The custom-field ids actually written, so a card can fill in
    /// the rest without duplicating what the driver just answered.</returns>
    private HashSet<int> SaveKioskCustomFields(string ticket, Dictionary<string, string>? values)
    {
        var written = new HashSet<int>();
        if (values == null || values.Count == 0) return written;

        var fields = _db.CustomFields
            .Where(f => f.Active && f.PromptAtKiosk)
            .ToList()
            .Where(f => f.IsKioskEligible())
            .ToList();

        foreach (var f in fields)
        {
            if (!values.TryGetValue(f.Id.ToString(), out var raw)) continue;
            raw = raw?.Trim() ?? "";
            if (raw.Length == 0) continue;
            if (raw.Length > 200) raw = raw[..200];

            if (f.FieldType == "Integer")
            {
                if (!long.TryParse(raw, out var i)) continue;
                if (f.MinValue.HasValue && i < f.MinValue.Value) continue;
                if (f.MaxValue.HasValue && i > f.MaxValue.Value) continue;
            }
            else if (f.FieldType == "Real")
            {
                if (!double.TryParse(raw, out var d)) continue;
                if (f.MinValue.HasValue && d < f.MinValue.Value) continue;
                if (f.MaxValue.HasValue && d > f.MaxValue.Value) continue;
                if (f.Precision.HasValue)
                {
                    var dot = raw.IndexOf('.');
                    if (dot >= 0 && raw.Length - dot - 1 > f.Precision.Value) continue;
                }
            }
            else if (!f.GetListValues().Contains(raw))
            {
                continue;
            }

            _db.TransactionCustomValues.Add(new TransactionCustomValue
            {
                Ticket = ticket,
                CustomFieldId = f.Id,
                Value = raw
            });
            written.Add(f.Id);
        }

        return written;
    }

    /// <summary>
    /// Copy the card's custom-field values onto the new ticket, skipping any
    /// field the driver just answered at the kiosk. Card values were validated
    /// when the card was issued, and they cover fields the kiosk can't prompt
    /// for at all (free text), which is much of the point of the card.
    /// Caller SaveChanges().
    /// </summary>
    private void SaveCardCustomFields(string ticket, Card card, HashSet<int> alreadyWritten)
    {
        var cardValues = _db.CardCustomValues.Where(v => v.CardId == card.Id).ToList();
        foreach (var v in cardValues)
        {
            if (alreadyWritten.Contains(v.CustomFieldId)) continue;
            if (string.IsNullOrWhiteSpace(v.Value)) continue;

            _db.TransactionCustomValues.Add(new TransactionCustomValue
            {
                Ticket = ticket,
                CustomFieldId = v.CustomFieldId,
                Value = v.Value
            });
        }
    }

    public class KioskCardRequest
    {
        /// <summary>Card number exactly as the reader reported it.</summary>
        public string? CardNumber { get; set; }
        /// <summary>Site scale this kiosk is mapped to — scopes location-limited
        /// commodities and bins when working out which prompts are required.</summary>
        public int? ScaleId { get; set; }
    }

    public class KioskWeighInRequest
    {
        public int Weight { get; set; }
        public string? Commodity { get; set; }
        public string? Customer { get; set; }
        public string? Carrier { get; set; }
        public string? TruckId { get; set; }
        public string? Location { get; set; }
        public string? Destination { get; set; }
        public string? Bin { get; set; }
        /// <summary>Custom field values keyed by field id ("3" -> "12.5").</summary>
        public Dictionary<string, string>? CustomFields { get; set; }
        /// <summary>Card this weigh-in came from, when the driver presented one.
        /// The card's stored values fill in every field left null above.</summary>
        public string? CardNumber { get; set; }
        /// <summary>Name of the site scale this kiosk is mapped to.</summary>
        public string? ScaleName { get; set; }
        /// <summary>
        /// Optional printer in "serviceId:printerId" format.
        /// If not set: demo mode uses "demo:KioskPrinter", normal mode uses inbound printer from Setup.
        /// </summary>
        public string? PrinterId { get; set; }
    }

    public class KioskWeighOutRequest
    {
        public string Ticket { get; set; } = string.Empty;
        public int Weight { get; set; }
        /// <summary>Name of the site scale this kiosk is mapped to.</summary>
        public string? ScaleName { get; set; }
        public string? Destination { get; set; }
        // Outbound-only prompt values. Each is optional — empty means the
        // operator wasn't prompted, or skipped, and the existing transaction
        // value is preserved.
        public string? Commodity { get; set; }
        public string? Customer { get; set; }
        public string? Location { get; set; }
        public string? Bin { get; set; }
        /// <summary>
        /// Optional printer in "serviceId:printerId" format.
        /// If not set: demo mode uses "demo:KioskPrinter", normal mode uses outbound printer from Setup.
        /// </summary>
        public string? PrinterId { get; set; }
    }
}
