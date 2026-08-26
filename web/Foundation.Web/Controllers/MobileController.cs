using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

/// <summary>
/// Phone version of the kiosk. A driver opens /Mobile on their own handset and
/// weighs themselves in and out; the page holds one truck's load per session in
/// a cookie instead of asking for a ticket number, and never prints — the
/// finished ticket is offered as a PDF download instead.
/// </summary>
public class MobileController : Controller
{
    /// <summary>Open ticket this handset is carrying. The only handle the phone
    /// has on its load, so losing it means the office has to close the ticket.</summary>
    private const string TicketCookie = "bw_mobile_ticket";
    private const int SessionHours = 12;

    /// <summary>
    /// Lightest reading that can be a truck on the scale. The page refuses to
    /// capture below this; this is the backstop for a stale page or a direct
    /// post. Motion and scale errors are deliberately not re-checked here — by
    /// the time the request lands the scale has moved on, and judging the
    /// capture against a later reading would reject good weighments.
    /// </summary>
    private const int MinCaptureWeight = 1000;

    private readonly ScaleDbContext _db;
    private readonly IHubContext<ScaleHub> _hub;
    private readonly AppSetupCache _setupCache;
    private readonly ILogger<MobileController> _log;
    // Messages returned from here are shown in the phone's banner, so they
    // follow the language the page is already showing.
    private readonly Translator _t;

    public MobileController(ScaleDbContext db, IHubContext<ScaleHub> hub, AppSetupCache setupCache, ILogger<MobileController> log, Translator t)
    {
        _db = db;
        _hub = hub;
        _setupCache = setupCache;
        _log = log;
        _t = t;
    }

    public IActionResult Index()
    {
        var setup = _setupCache.Get();
        return View(setup);
    }

    // ===== SESSION COOKIE =====

    private void SetTicketCookie(string ticket) =>
        Response.Cookies.Append(TicketCookie, ticket, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddHours(SessionHours)
        });

    private void ClearTicketCookie() =>
        Response.Cookies.Delete(TicketCookie);

    /// <summary>
    /// The open transaction this handset holds, or null. A ticket that was
    /// voided or closed elsewhere (office weigh-out) counts as gone, and the
    /// stale cookie is dropped so the phone falls back to the weigh-in screen.
    /// </summary>
    private Transaction? OpenTicket()
    {
        var ticket = Request.Cookies[TicketCookie];
        if (string.IsNullOrEmpty(ticket)) return null;

        var tx = _db.Transactions.FirstOrDefault(t => t.Ticket == ticket && !t.Void && t.DateOut == null);
        if (tx == null) ClearTicketCookie();
        return tx;
    }

    private static object TicketPayload(Transaction t) => new
    {
        ticket = t.Ticket,
        inWeight = t.InWeight,
        dateIn = t.DateIn.AsUtc(),
        inScale = t.InScale,
        customer = t.Customer,
        carrier = t.Carrier,
        truckId = t.TruckId,
        commodity = t.Commodity,
        location = t.Location,
        destination = t.Destination,
        bin = t.Bin
    };

    /// <summary>Custom-field values already on a ticket, keyed by field id, so
    /// the weigh-out screen can show and change what weigh-in captured.</summary>
    private Dictionary<string, string> TicketCustomValues(string ticket) =>
        _db.TransactionCustomValues
            .Where(v => v.Ticket == ticket)
            .AsEnumerable()
            .GroupBy(v => v.CustomFieldId)
            .ToDictionary(g => g.Key.ToString(), g => g.Last().Value ?? "");

    /// <summary>
    /// What the phone should show on load: the open ticket it is already
    /// carrying, if any.
    /// </summary>
    [HttpGet("api/mobile/session")]
    public IActionResult Session()
    {
        var open = OpenTicket();
        return Json(new
        {
            openTicket = open == null ? null : TicketPayload(open),
            customFields = open == null ? null : TicketCustomValues(open.Ticket)
        });
    }

    /// <summary>
    /// Active scales grouped by location. A scale with no location assigned is
    /// available at every location, matching the ForSite filter used elsewhere.
    /// needsPicker is false for the common single-location single-scale site, so
    /// the phone can skip the picker entirely and open straight on Weigh In.
    /// </summary>
    [HttpGet("api/mobile/scales")]
    public IActionResult GetScales()
    {
        var scales = _db.Scales.Where(s => s.Active)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToList();
        var sites = _db.Sites.Where(s => s.Active)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToList();

        var groups = new List<SiteGroup>();
        if (sites.Count == 0)
        {
            // No locations configured — one implicit site holding every scale.
            groups.Add(new SiteGroup(0, "",
                scales.Select(s => new ScaleOption(s.Id, s.Name)).ToList()));
        }
        else
        {
            foreach (var site in sites)
            {
                var forSite = scales
                    .Where(s => s.SiteId == null || s.SiteId == site.Id)
                    .Select(s => new ScaleOption(s.Id, s.Name))
                    .ToList();
                if (forSite.Count > 0) groups.Add(new SiteGroup(site.Id, site.Name, forSite));
            }
        }

        var needsPicker = groups.Count > 1 || groups.Any(g => g.Scales.Count > 1);
        return Json(new { sites = groups, needsPicker });
    }

    public record ScaleOption(int Id, string Name);
    public record SiteGroup(int Id, string Name, List<ScaleOption> Scales);

    [HttpGet("api/mobile/lists")]
    public IActionResult GetLists(int? scaleId = null)
        => Json(KioskLists.Build(_db, _setupCache.Get(), scaleId));

    /// <summary>
    /// The stored empty weight the phone may offer to reuse for this truck, or
    /// null when there is none, the feature is off, or the tare is stale. The
    /// same rule the weighments apply, so the page never offers a tare the
    /// server would then refuse.
    /// </summary>
    private (Truck? Truck, int? Tare, DateTime? Updated) UsableTare(AppSetup setup, string? carrier, string? truckId)
    {
        if (!setup.UseRetainedTare) return (null, null, null);
        if (string.IsNullOrWhiteSpace(carrier) || string.IsNullOrWhiteSpace(truckId)) return (null, null, null);

        var c = carrier.Trim();
        var t = truckId.Trim();
        var truck = _db.Trucks.FirstOrDefault(x => x.TruckId == t && x.CarrierName == c);
        if (truck?.RetainedTare == null) return (truck, null, null);

        // A tare from a previous day may no longer describe the truck.
        if (setup.AutoClearStaleRetainedTare
            && (truck.RetainedTareUpdated?.Date ?? DateTime.MinValue) < DateTime.Today)
            return (truck, null, null);

        return (truck, truck.RetainedTare, truck.RetainedTareUpdated);
    }

    /// <summary>
    /// Does this truck have a stored empty weight the driver could reuse? The
    /// phone asks before a weighment so it can offer the choice rather than
    /// silently finishing the load on a tare the driver never saw.
    /// </summary>
    [HttpGet("api/mobile/retained-tare")]
    public IActionResult GetRetainedTare([FromQuery] string? carrier, [FromQuery] string? truckId)
    {
        var (_, tare, updated) = UsableTare(_setupCache.Get(), carrier, truckId);
        return Json(new { available = tare.HasValue, tare, updated = updated.AsUtc() });
    }

    [HttpGet("api/mobile/trucks/{carrier}")]
    public IActionResult GetTrucks(string carrier)
        => Json(_db.Trucks
            .Where(t => t.CarrierName == carrier && t.UseAtKiosk)
            .OrderBy(t => t.TruckId)
            .Select(t => t.TruckId)
            .ToList());

    // ===== WEIGH IN =====

    [HttpPost("api/mobile/weighin")]
    public async Task<IActionResult> WeighIn([FromBody] MobileWeighInRequest request)
    {
        // One load per handset. A second weigh-in while a ticket is open would
        // orphan the first, so refuse and let the phone re-sync.
        var existing = OpenTicket();
        if (existing != null)
            return Conflict(new
            {
                message = _t["This phone already has an open ticket."],
                openTicket = TicketPayload(existing),
                customFields = TicketCustomValues(existing.Ticket)
            });

        var setup = _db.AppSetup.First();
        var scale = SiteScales.Resolve(_db, request.ScaleId);
        if (scale == null)
            return BadRequest(new { message = _t["No scale configured."] });

        if (request.Weight < MinCaptureWeight)
            return BadRequest(new { message = _t["No truck on the scale — nothing to weigh in."] });

        // Ensure ticket number doesn't collide with existing tickets
        while (_db.Transactions.Any(t => t.Ticket == setup.TicketNumber.ToString()))
        {
            setup.TicketNumber++;
        }
        var ticketNumber = setup.TicketNumber.ToString();

        // Retained tare can close the load in one weighment. The phone and the
        // kiosk both ask first: the driver is told the stored empty weight and
        // decides whether to finish now or reset it and weigh out for real.
        // UseRetainedTare null means the page never asked (an older page), and
        // falls back to applying the tare automatically.
        var (truck, usableTare, tareUpdated) = UsableTare(setup, request.Carrier, request.TruckId);
        bool tareApplied = usableTare.HasValue && request.UseRetainedTare != false;

        // Declining is a reset, not a skip for this load only: clearing the
        // stored value is what makes this visit's real weigh-out capture a
        // fresh tare instead of the old one surviving until it goes stale.
        //
        // Unless the site has taken that away from phone drivers — then
        // declining falls back to what it meant before the reset existed: this
        // load is weighed out for real, but the stored tare survives for the
        // next visit. Re-checked here so a stale page cannot clear anything.
        if (usableTare.HasValue && request.UseRetainedTare == false
            && truck != null && setup.AllowTareResetMobile)
        {
            _log.LogInformation("RetainedTare: mobile reset tare for {TruckId}/{Carrier} (was {Tare} lb)",
                truck.TruckId, truck.CarrierName, truck.RetainedTare);
            truck.RetainedTare = null;
            truck.RetainedTareUpdated = null;
        }

        var now = DateTime.UtcNow;
        var dateIn = tareApplied ? (tareUpdated ?? now) : now;

        var transaction = new Transaction
        {
            Ticket = ticketNumber,
            InWeight = request.Weight,
            InScale = scale.Name,
            DateIn = dateIn,
            Commodity = request.Commodity,
            Customer = request.Customer,
            Carrier = request.Carrier,
            TruckId = request.TruckId,
            Location = request.Location,
            Destination = request.Destination,
            Bin = request.Bin,
            Void = false,
            ManualInbound = false
        };

        // Lock Bin to Commodity backstop — the prompt flow filters bins
        // client-side; this catches a stale page or a direct post.
        if (BinInventory.ValidateTicket(_db, setup, transaction) is { } binLockError)
            return BadRequest(new { message = binLockError });

        if (tareApplied)
        {
            transaction.OutWeight = usableTare;
            transaction.DateOut = now;
            transaction.ManualOutbound = false;
        }

        setup.TicketNumber++;
        _db.AppSetup.Update(setup);
        _db.Transactions.Add(transaction);
        KioskLists.SaveCustomFields(_db, ticketNumber, request.CustomFields);
        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, transaction);
        _setupCache.Invalidate();

        if (tareApplied)
        {
            await _hub.Clients.All.SendAsync("TicketCompleted", new { ticket = ticketNumber, type = "weighout" });
            // Finished in one weighment on a stored tare — the truck is leaving.
            await GateDispatch.OpenForTicket(_hub, _db, _log, scale.Name, ticketNumber, "weighout");
        }
        else
        {
            await _hub.Clients.All.SendAsync("TicketCreated", new { ticket = ticketNumber, type = "weighin" });
            // Only an open load is worth carrying; a tare-completed ticket is done.
            SetTicketCookie(ticketNumber);
        }

        if (setup.SavePicture)
        {
            await SendCameraCapture(ticketNumber,
                tareApplied ? "out" : "in",
                tareApplied ? setup.OutboundCameraId : setup.InboundCameraId);
        }

        // No print command: the phone offers the ticket as a PDF instead.
        return Json(new
        {
            ticket = ticketNumber,
            inWeight = transaction.InWeight,
            outWeight = transaction.OutWeight,
            grossWeight = transaction.GrossWeight,
            tareWeight = transaction.TareWeight,
            netWeight = transaction.NetWeight,
            dateOut = transaction.DateOut.AsUtc(),
            tareApplied,
            retainedTare = usableTare,
            // The ticket as stored, not as posted: rules like Lock Bin to
            // Commodity fill in fields server-side, and the phone has to weigh
            // out against what was actually written or it will show — and then
            // save — blanks over them.
            openTicket = tareApplied ? null : TicketPayload(transaction),
            customFields = tareApplied ? null : TicketCustomValues(ticketNumber)
        });
    }

    // ===== WEIGH OUT =====

    [HttpPost("api/mobile/weighout")]
    public async Task<IActionResult> WeighOut([FromBody] MobileWeighOutRequest request)
    {
        // The ticket comes from the session cookie, never the request body — the
        // phone can only ever close the load it opened.
        var transaction = OpenTicket();
        if (transaction == null)
            return NotFound(new { message = _t["No open ticket on this phone."] });

        var scale = SiteScales.Resolve(_db, request.ScaleId);
        var setupForTare = _setupCache.Get();

        // The driver may close the load on the truck's stored empty weight
        // instead of driving back onto the scale. The value comes from the
        // server's own record, never the request, so the phone can choose to
        // reuse a tare but can never invent one.
        int? reusedTare = null;
        if (request.UseRetainedTare)
        {
            var (_, tare, _) = UsableTare(setupForTare, transaction.Carrier, transaction.TruckId);
            if (tare == null)
                return BadRequest(new { message = _t["That truck no longer has a stored empty weight. Weigh out on the scale."] });
            reusedTare = tare;
        }

        // A reused tare is a stored number, not a reading, so the floor only
        // applies to a live weighment.
        if (reusedTare == null && request.Weight < MinCaptureWeight)
            return BadRequest(new { message = _t["No truck on the scale — nothing to weigh out."] });

        transaction.OutWeight = reusedTare ?? request.Weight;
        // A reused tare was not measured here, so no scale gets the credit.
        transaction.OutScale = reusedTare.HasValue ? null : scale?.Name;
        transaction.DateOut = DateTime.UtcNow;
        transaction.ManualOutbound = false;

        // The weigh-out screen lets the driver correct any field the ticket
        // carries, not just the ones configured as outbound prompts. Null means
        // the phone did not send the field at all and it keeps its weigh-in
        // value; an empty string is an explicit "clear this", which is the only
        // way to undo a wrong entry from the way in.
        transaction.Carrier     = Applied(request.Carrier,     transaction.Carrier);
        transaction.TruckId     = Applied(request.TruckId,     transaction.TruckId);
        transaction.Destination = Applied(request.Destination, transaction.Destination);
        transaction.Commodity   = Applied(request.Commodity,   transaction.Commodity);
        transaction.Customer    = Applied(request.Customer,    transaction.Customer);
        transaction.Location    = Applied(request.Location,    transaction.Location);
        transaction.Bin         = Applied(request.Bin,         transaction.Bin);

        var setup = _setupCache.Get();
        if (BinInventory.ValidateTicket(_db, setup, transaction) is { } binLockError)
            return BadRequest(new { message = binLockError });

        // Only a real weighment may rewrite the stored tare. Refreshing it from a
        // reused value would keep restamping today's date on a number that was
        // last actually measured days ago, defeating the staleness expiry.
        if (setup.UseRetainedTare && !reusedTare.HasValue) UpdateRetainedTare(transaction);

        // Custom fields the driver corrected on the way out replace what weigh-in
        // stored. Only the ids the phone sent are touched, so a field it never
        // showed cannot be wiped by omission.
        if (request.CustomFields is { Count: > 0 })
        {
            var ids = request.CustomFields.Keys
                .Select(k => int.TryParse(k, out var id) ? id : -1)
                .Where(id => id > 0)
                .ToHashSet();
            var stale = _db.TransactionCustomValues
                .Where(v => v.Ticket == transaction.Ticket && ids.Contains(v.CustomFieldId));
            _db.TransactionCustomValues.RemoveRange(stale);
            _db.SaveChanges();
            KioskLists.SaveCustomFields(_db, transaction.Ticket, request.CustomFields);
        }

        _db.SaveChanges();
        FormulaFields.RecomputeAndSave(_db, transaction);
        ClearTicketCookie();

        await _hub.Clients.All.SendAsync("TicketCompleted", new { ticket = transaction.Ticket, type = "weighout" });
        // A load closed on a stored tare was never on this scale, so fall back
        // to the scale that captured the inbound weight — that is the deck the
        // truck is sitting on, and the gate that has to let it out.
        await GateDispatch.OpenForTicket(_hub, _db, _log,
            transaction.OutScale ?? transaction.InScale, transaction.Ticket, "weighout");

        if (setup.SavePicture)
            await SendCameraCapture(transaction.Ticket, "out", setup.OutboundCameraId);

        // No print command — the phone downloads the PDF.
        return Json(new
        {
            ticket = transaction.Ticket,
            inWeight = transaction.InWeight,
            outWeight = transaction.OutWeight,
            grossWeight = transaction.GrossWeight,
            tareWeight = transaction.TareWeight,
            netWeight = transaction.NetWeight,
            dateIn = transaction.DateIn.AsUtc(),
            dateOut = transaction.DateOut.AsUtc(),
            tareReused = reusedTare.HasValue
        });
    }

    /// <summary>
    /// Driver abandons the load. Voids the open weigh-in and clears the session
    /// so the phone starts over — the page warns before calling this. Scoped to
    /// the ticket in this handset's cookie, so it can only ever void its own.
    /// </summary>
    [HttpPost("api/mobile/reset")]
    public async Task<IActionResult> Reset()
    {
        var transaction = OpenTicket();
        ClearTicketCookie();

        if (transaction == null)
            return Json(new { voided = false });

        transaction.Void = true;
        _db.SaveChanges();
        _log.LogInformation("Mobile: voided open ticket {Ticket} on driver reset", transaction.Ticket);

        await _hub.Clients.All.SendAsync("TicketVoided", new { ticket = transaction.Ticket });
        return Json(new { voided = true, ticket = transaction.Ticket });
    }

    /// <summary>
    /// Fold one edited field into the ticket. Null leaves the existing value
    /// alone; an empty string clears it; anything else replaces it.
    /// </summary>
    private static string? Applied(string? sent, string? existing) =>
        sent == null ? existing : (sent.Trim().Length == 0 ? null : sent.Trim());

    private async Task SendCameraCapture(string ticketId, string direction, string? cameraIdSetting)
    {
        if (string.IsNullOrEmpty(cameraIdSetting)) return;
        var parts = cameraIdSetting.Split(':', 2);
        var serviceId = parts.Length > 1 ? parts[0] : "default";
        var cameraId = parts.Length > 1 ? parts[1] : parts[0];
        await _hub.Clients.Group($"Camera_{serviceId}").SendAsync("CaptureImage",
            new { ticket = ticketId, direction, cameraId });
    }

    /// <summary>
    /// Persist the retained tare for this truck. Mirrors the kiosk rule: tare is
    /// the lower of the two weights, and a truck missing from master data is
    /// created so the feature works without pre-registering every hauler.
    /// </summary>
    private void UpdateRetainedTare(Transaction tx)
    {
        if (tx.OutWeight == null) return;
        var truckId = tx.TruckId?.Trim();
        var carrier = tx.Carrier?.Trim();
        if (string.IsNullOrEmpty(truckId) || string.IsNullOrEmpty(carrier)) return;

        var tare = Math.Min(tx.InWeight, tx.OutWeight.Value);
        var when = tx.DateOut ?? DateTime.UtcNow;

        var truck = _db.Trucks.FirstOrDefault(t =>
            t.TruckId.ToLower() == truckId.ToLower() &&
            t.CarrierName.ToLower() == carrier.ToLower());

        if (truck == null)
        {
            _db.Trucks.Add(new Truck
            {
                TruckId = truckId,
                CarrierName = carrier,
                UseAtKiosk = true,
                Description = "Auto-created from mobile weigh-out",
                RetainedTare = tare,
                RetainedTareUpdated = when
            });
        }
        else
        {
            truck.RetainedTare = tare;
            truck.RetainedTareUpdated = when;
        }
        _log.LogInformation("RetainedTare: mobile set {TruckId}/{Carrier} to {Tare} lb (ticket {Ticket})",
            truckId, carrier, tare, tx.Ticket);
    }

    public class MobileWeighInRequest
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
        /// <summary>Site scale the driver picked; the ticket records its name.</summary>
        public int? ScaleId { get; set; }
        /// <summary>The driver's answer to "reuse this truck's stored empty
        /// weight and finish now?". True finishes the load in one weighment,
        /// false opens a normal ticket to be weighed out later, and null means
        /// the page never asked (falls back to applying it automatically).</summary>
        public bool? UseRetainedTare { get; set; }
    }

    public class MobileWeighOutRequest
    {
        public int Weight { get; set; }
        public int? ScaleId { get; set; }
        /// <summary>Close the load on the truck's stored empty weight instead of
        /// the live scale reading. The server supplies the value.</summary>
        public bool UseRetainedTare { get; set; }
        /// <summary>Corrections to what weigh-in captured. Null on a field means
        /// the phone left it alone; empty string clears it.</summary>
        public string? Carrier { get; set; }
        public string? TruckId { get; set; }
        /// <summary>Custom field values keyed by field id. Only the ids present
        /// are replaced.</summary>
        public Dictionary<string, string>? CustomFields { get; set; }
        // Outbound-only prompt values. Each is optional — empty means the driver
        // wasn't prompted, or skipped, and the weigh-in value is preserved.
        public string? Destination { get; set; }
        public string? Commodity { get; set; }
        public string? Customer { get; set; }
        public string? Location { get; set; }
        public string? Bin { get; set; }
    }
}
