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
    // Messages returned from here land on the kiosk's overlays, so they follow
    // the language the kiosk page is already showing.
    private readonly Translator _t;

    public KioskController(ScaleDbContext db, IScaleService scaleService, IHubContext<ScaleHub> hub, AppSetupCache setupCache, ILogger<KioskController> log, Translator t)
    {
        _db = db;
        _scaleService = scaleService;
        _hub = hub;
        _setupCache = setupCache;
        _log = log;
        _t = t;
    }

    /// <summary>Cookie holding this display's kiosk identity. Long-lived and
    /// HttpOnly; the page mirrors the id to localStorage and restores it with
    /// ?device= if the cookie is ever lost.</summary>
    public const string DeviceCookie = "KioskDevice";

    public IActionResult Index([FromQuery(Name = "service-id")] string? serviceId = null,
                               [FromQuery(Name = "printer-id")] string? printerId = null,
                               [FromQuery(Name = "scale-id")] int? scaleId = null,
                               [FromQuery(Name = "reader-id")] string? readerId = null,
                               [FromQuery(Name = "device")] string? device = null,
                               [FromQuery(Name = "setup")] bool setupRequested = false)
    {
        var setup = _setupCache.Get();

        // A URL that carries its own hardware mapping is a kiosk installed
        // before self-enrollment existed. It keeps working exactly as it did,
        // and is never pushed into the setup wizard.
        var urlConfigured = !string.IsNullOrEmpty(serviceId) || !string.IsNullOrEmpty(printerId)
                            || scaleId.HasValue || !string.IsNullOrEmpty(readerId);

        // Registered kiosks supply everything below; the URL still wins where
        // it is explicit, so a one-off override is always possible.
        var deviceId = (device ?? Request.Cookies[DeviceCookie] ?? "").Trim();
        Kiosk? kiosk = null;
        if (deviceId.Length > 0)
        {
            kiosk = _db.Kiosks.FirstOrDefault(k => k.DeviceId == deviceId && k.Active);
            if (kiosk != null)
            {
                kiosk.LastSeenAt = DateTime.UtcNow;
                _db.SaveChanges();
                // Re-issue the cookie so a device restored via ?device= (or one
                // approaching the browser's cookie lifetime cap) stays known.
                WriteDeviceCookie(kiosk.DeviceId);
            }
        }

        // The wizard runs for a device we don't recognise, and on demand when
        // an installer asks for it from the kiosk itself.
        ViewBag.KioskNeedsSetup = setupRequested || (kiosk == null && !urlConfigured);
        ViewBag.KioskDeviceId = kiosk?.DeviceId ?? "";
        ViewBag.KioskName = kiosk?.Name ?? "";

        // Printer: "serviceId:printerId" on the kiosk record, or the legacy
        // pair of query parameters. Null/absent means this kiosk prints nothing.
        var kioskPrinter = SplitPair(kiosk?.PrinterId);
        var effectiveService = serviceId ?? kioskPrinter.Left;
        var effectivePrinter = printerId ?? kioskPrinter.Right;
        ViewBag.ServiceId = effectiveService ?? "";
        ViewBag.PrinterId = effectivePrinter ?? "";
        ViewBag.HasPrinter = !string.IsNullOrEmpty(effectiveService) && !string.IsNullOrEmpty(effectivePrinter);

        // Card reader this kiosk listens to, as "serviceId:readerId". Empty
        // means this kiosk ignores card reads and runs the touchscreen flow.
        ViewBag.ReaderId = setup.UseCardReader ? (readerId ?? kiosk?.ReaderId ?? "") : "";

        // Each kiosk device is mapped to one site scale. A stored scale that
        // has since been deleted is dropped rather than passed through, so the
        // kiosk falls back to the default instead of coming up weighing on
        // nothing. A bare /Kiosk URL lands on the default too, which is what a
        // single-scale site wants.
        var kioskScaleId = kiosk?.ScaleId is int ks && _db.Scales.Any(s => s.Id == ks) ? ks : (int?)null;
        var scale = SiteScales.Resolve(_db, scaleId ?? kioskScaleId);
        ViewBag.KioskScaleDbId = scale?.Id ?? 0;
        ViewBag.KioskScaleName = scale?.Name ?? "";
        // Hardware feed id, used to filter SignalR ScaleWeight pushes.
        ViewBag.ScaleId = scale?.HardwareId ?? "";
        return View(setup);
    }

    /// <summary>Cookie holding a validated kiosk PIN for this display. Written
    /// with a long life and refreshed on every kiosk load, so a commissioned
    /// kiosk is asked once and never again. It lives in the browser profile
    /// beside the device id, so a replaced Pi asks for both again.</summary>
    public const string PinCookie = "KioskPin";

    /// <summary>How long a validated PIN is remembered. Browsers cap cookie
    /// lifetime near 400 days; the middleware re-issues it on every load, so
    /// in practice it lasts as long as the kiosk keeps being used.</summary>
    public static readonly TimeSpan PinCookieLife = TimeSpan.FromDays(400);

    /// <summary>
    /// The kiosk's own PIN screen. A kiosk has no keyboard and cannot use the
    /// operator login page, so a display that has not been unlocked yet lands
    /// here and the installer taps the PIN in on the numpad. Reachable without
    /// a PIN — it is the thing that gets you one.
    /// </summary>
    [HttpGet("/Kiosk/Pin")]
    public IActionResult Pin([FromQuery] string? returnUrl = null)
    {
        var setup = _setupCache.Get();
        // Nothing to unlock when login is off; never leave a dead screen up.
        if (!setup.UseLogin) return Redirect(SafeReturnUrl(returnUrl));

        ViewBag.ReturnUrl = SafeReturnUrl(returnUrl);
        return View(setup);
    }

    [HttpPost("/Kiosk/Pin")]
    [ValidateAntiForgeryToken]
    public IActionResult Pin([FromForm] string? pin, [FromForm] string? returnUrl)
    {
        var setup = _setupCache.Get();
        var target = SafeReturnUrl(returnUrl);
        if (!setup.UseLogin) return Redirect(target);

        if ((pin ?? "") != (setup.KioskCode ?? ""))
        {
            _log.LogWarning("Kiosk PIN rejected from {Ip}", HttpContext.Connection.RemoteIpAddress);
            ViewBag.Error = "Incorrect PIN — try again.";
            ViewBag.ReturnUrl = target;
            return View(setup);
        }

        WritePinCookie(HttpContext, pin!);
        return Redirect(target);
    }

    /// <summary>
    /// Only ever bounce back to a kiosk path on this site. Without this, the
    /// PIN screen would be an open redirect: a crafted returnUrl could send a
    /// display somewhere else the moment the right PIN was entered.
    /// </summary>
    private static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/Kiosk";
        // Must be a site-relative path, and "//host" is not one.
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//")) return "/Kiosk";
        // Returning to the PIN screen itself would just ask again.
        if (UnderPath(returnUrl, "/Kiosk/Pin")) return "/Kiosk";
        // The signature pad is the other keyboard-less tablet sharing this PIN,
        // so it may be returned to as well; anything else falls back.
        var allowed = UnderPath(returnUrl, "/Kiosk") || UnderPath(returnUrl, "/SignaturePad");
        return allowed ? returnUrl : "/Kiosk";
    }

    /// <summary>
    /// Whether a URL is the given path or something beneath it, respecting
    /// segment boundaries. A plain StartsWith would let "/Kiosks" — the admin
    /// management page — pass as "/Kiosk".
    /// </summary>
    private static bool UnderPath(string url, string prefix) =>
        url.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || url.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith(prefix + "?", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remember a validated PIN on this display. Shared with the
    /// middleware, which refreshes it on every kiosk load.</summary>
    public static void WritePinCookie(HttpContext context, string pin)
    {
        context.Response.Cookies.Append(PinCookie, pin, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(PinCookieLife)
        });
    }

    /// <summary>Split "serviceId:thingId" into its two halves. Anything that
    /// isn't a pair yields nulls, so a malformed value disables the feature
    /// rather than half-configuring it.</summary>
    private static (string? Left, string? Right) SplitPair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var i = value.IndexOf(':');
        if (i <= 0 || i == value.Length - 1) return (null, null);
        return (value[..i], value[(i + 1)..]);
    }

    private void WriteDeviceCookie(string deviceId)
    {
        Response.Cookies.Append(DeviceCookie, deviceId, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // Browsers cap cookie lifetime around 400 days; the page keeps a
            // localStorage copy and restores the cookie if it is ever dropped.
            Expires = DateTimeOffset.UtcNow.AddDays(400)
        });
    }

    /// <summary>
    /// Everything the on-screen setup wizard offers: the site's scales, and
    /// whether the card-reader step applies at all. Printers and readers are
    /// announced over SignalR by the services themselves, so the wizard
    /// collects those live rather than from here.
    /// </summary>
    [HttpGet("api/kiosk/setup-options")]
    public IActionResult SetupOptions()
    {
        var setup = _setupCache.Get();
        var scales = _db.Scales.Where(s => s.Active)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name, siteId = s.SiteId })
            .ToList();

        return Json(new { scales, useCardReader = setup.UseCardReader });
    }

    /// <summary>
    /// Enroll (or re-configure) the display running the wizard. The device id
    /// is generated by the browser; everything else is what the installer just
    /// picked on screen. Printer and reader are both optional — a kiosk that
    /// prints nothing and reads no cards is a valid kiosk.
    /// </summary>
    [HttpPost("api/kiosk/register")]
    public IActionResult Register([FromBody] KioskRegisterRequest request)
    {
        var deviceId = (request.DeviceId ?? "").Trim();
        // Opaque, browser-generated, and used as a lookup key — keep it to a
        // shape we are willing to store and echo back into a page.
        if (deviceId.Length is < 8 or > 64 || !deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-'))
            return BadRequest(new { message = _t["That device id is not valid."] });

        var kiosk = _db.Kiosks.FirstOrDefault(k => k.DeviceId == deviceId);
        if (kiosk == null)
        {
            kiosk = new Kiosk { DeviceId = deviceId, CreatedAt = DateTime.UtcNow };
            _db.Kiosks.Add(kiosk);
        }

        var name = (request.Name ?? "").Trim();
        kiosk.Name = name.Length > 0
            ? (name.Length > 100 ? name[..100] : name)
            : (kiosk.Name.Length > 0 ? kiosk.Name : NextKioskName());

        // A scale that has since been deleted or deactivated must not be
        // written back — the kiosk would come up pointing at nothing.
        kiosk.ScaleId = request.ScaleId.HasValue
            && _db.Scales.Any(s => s.Id == request.ScaleId.Value && s.Active)
                ? request.ScaleId
                : null;

        kiosk.PrinterId = NormalizePair(request.PrinterId);
        kiosk.ReaderId = _setupCache.Get().UseCardReader ? NormalizePair(request.ReaderId) : null;
        kiosk.Active = true;
        kiosk.LastSeenAt = DateTime.UtcNow;
        _db.SaveChanges();

        WriteDeviceCookie(kiosk.DeviceId);
        _log.LogInformation("Kiosk {Name} registered (device {Device}, scale {Scale}, printer {Printer}, reader {Reader})",
            kiosk.Name, kiosk.DeviceId, kiosk.ScaleId, kiosk.PrinterId ?? "none", kiosk.ReaderId ?? "none");

        return Ok(new { ok = true, id = kiosk.Id, name = kiosk.Name, deviceId = kiosk.DeviceId });
    }

    /// <summary>"serviceId:thingId" or null. Blank, "none" and anything that
    /// isn't a pair all mean "this kiosk has none".</summary>
    private static string? NormalizePair(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = SplitPair(v);
        if (parts.Left == null || parts.Right == null) return null;
        return v.Length > 100 ? null : v;
    }

    /// <summary>"Kiosk 1", "Kiosk 2", … — the first number not already taken.
    /// A kiosk has no keyboard, so it never asks the installer to type a name;
    /// renaming happens on the web app's Kiosks page.</summary>
    private string NextKioskName()
    {
        var taken = _db.Kiosks.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var candidate = $"Kiosk {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    [HttpGet("api/kiosk/lists")]
    public IActionResult GetLists(int? scaleId = null)
        => Json(KioskLists.Build(_db, _setupCache.Get(), scaleId));

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

        if (transaction == null) return NotFound(new { message = _t["No open ticket"] });

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
            return Ok(new { ok = false, reason = "disabled", message = _t["Card weighing is turned off"] });

        var number = (request.CardNumber ?? "").Trim();
        if (number.Length == 0)
            return BadRequest(new { ok = false, reason = "empty", message = _t["No card number"] });

        var card = _db.Cards.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.CardNumber, number, StringComparison.OrdinalIgnoreCase));

        if (card == null)
            return Ok(new { ok = false, reason = "unknown", message = _t["Card Not Recognized"] });
        if (!card.Enabled)
            return Ok(new { ok = false, reason = "disabled-card", message = _t["Card Disabled"] });

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
            return Ok(new { ok = false, reason = "not-issued", message = _t["Card Not Active — See Loader Operator"] });

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

        // The kiosk picks its overlay from `reason`; `message` is for display
        // and is translated, so the two must not be conflated.
        if (transaction == null)
            return NotFound(new { reason = "not-found", message = _t["Ticket not found"] });

        if (transaction.Void)
            return BadRequest(new { reason = "voided", message = _t["Ticket is voided"] });

        if (transaction.DateOut != null)
            return BadRequest(new { reason = "completed", message = _t["Ticket already completed"] });

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
                return BadRequest(new { message = _t["Card not recognized."] });
            if (!card.Issued && string.IsNullOrEmpty(card.OpenTicket))
                return BadRequest(new { message = _t["Card is not active — see the loader operator."] });

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

        // The driver may reset a stored tare instead of finishing the load on
        // it — the kiosk asks whenever the truck has one. Clearing it here is
        // what makes the reset stick: the ticket stays open, and this visit's
        // real weigh-out captures a fresh tare. A null answer means the page
        // never asked (an older kiosk), which keeps the automatic behaviour.
        //
        // Gated per source, and re-checked here rather than trusted from the
        // page: a card presentation and a keyed weigh-in can be allowed to
        // re-tare independently, and a stale kiosk cached from before the gate
        // was turned off must not be able to throw a tare away.
        var mayResetTare = card != null ? setup.AllowTareResetCard : setup.AllowTareResetKiosk;
        if (tareApplied && request.UseRetainedTare == false && mayResetTare)
        {
            _log.LogInformation("RetainedTare: kiosk reset tare for {TruckId}/{Carrier} (was {Tare} lb)",
                truck!.TruckId, truck.CarrierName, truck.RetainedTare);
            truck.RetainedTare = null;
            truck.RetainedTareUpdated = null;
            tareApplied = false;
        }

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
            // The load is finished and the truck is leaving, so the gate opens
            // even though this was posted as a weigh-in.
            await GateDispatch.OpenForTicket(_hub, _db, _log, request.ScaleName, ticketNumber, "weighout");
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
            return NotFound(new { message = _t["Ticket not found"] });

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
        await GateDispatch.OpenForTicket(_hub, _db, _log, request.ScaleName, transaction.Ticket, "weighout");

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
            return NotFound(new { message = _t["Ticket not found"] });

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
    /// Kiosk-collected custom field values. Shared with the mobile page — see
    /// <see cref="KioskLists.SaveCustomFields"/>. Caller SaveChanges().
    /// </summary>
    private HashSet<int> SaveKioskCustomFields(string ticket, Dictionary<string, string>? values)
        => KioskLists.SaveCustomFields(_db, ticket, values);

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

    /// <summary>What the on-screen setup wizard sends when a display enrolls
    /// itself. PrinterId and ReaderId are null when the installer chose Skip.</summary>
    public class KioskRegisterRequest
    {
        public string? DeviceId { get; set; }
        public string? Name { get; set; }
        public int? ScaleId { get; set; }
        /// <summary>"serviceId:printerId", "Browser:Browser", or null for none.</summary>
        public string? PrinterId { get; set; }
        /// <summary>"serviceId:readerId", or null for none.</summary>
        public string? ReaderId { get; set; }
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
        /// <summary>The driver's answer to "finish now on this truck's stored
        /// empty weight?". True finishes the load in one weighment, false
        /// resets the stored tare and opens a normal ticket to weigh out, and
        /// null means the page never asked (older kiosks apply it silently).</summary>
        public bool? UseRetainedTare { get; set; }
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
