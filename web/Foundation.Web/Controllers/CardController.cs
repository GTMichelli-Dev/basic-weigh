using Microsoft.AspNetCore.Mvc;
using Foundation.Web.Data;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

/// <summary>
/// Prox-card weighing. Two audiences:
///
///   /Card       — enrollment. An admin registers each physical card's number
///                 once (api/cardadmin/*). Only enrolled cards can be issued
///                 or used at a kiosk.
///   /Card/Setup — the loader operator's phone page (api/cards/*). Type the
///                 card number, the card's last-issued values come back, edit
///                 what this load needs, save. The driver takes it to the scale.
/// </summary>
public class CardController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;

    public CardController(ScaleDbContext db, AppSetupCache setupCache)
    {
        _db = db;
        _setupCache = setupCache;
    }

    // ===== PAGES =====

    /// <summary>Card enrollment (Manager / Admin — enforced in Program.cs).</summary>
    public IActionResult Index()
    {
        var setup = _setupCache.Get();
        ViewBag.RecycleCards = setup.RecycleCards;
        ViewBag.UseCardReader = setup.UseCardReader;
        return View();
    }

    /// <summary>The loader operator's mobile card-issue page.</summary>
    public IActionResult Setup()
    {
        var setup = _setupCache.Get();
        ViewBag.RecycleCards = setup.RecycleCards;
        ViewBag.UseCardReader = setup.UseCardReader;
        ViewBag.UseRetainedTare = setup.UseRetainedTare;
        return View();
    }

    // ===== OPERATOR API =====

    /// <summary>
    /// The fields a card can carry, in weigh-form order, with their choice
    /// lists and which of them the kiosk would refuse to skip.
    /// </summary>
    [HttpGet("api/cards/fields")]
    public IActionResult GetFields()
    {
        var siteId = SiteContext.CurrentSiteId(HttpContext, _db);
        var setup = _setupCache.Get();
        return Json(new
        {
            fields = CardFields.Describe(_db, setup, siteId),
            recycleCards = setup.RecycleCards
        });
    }

    /// <summary>
    /// Recall a card by its number. Returns the card's stored values so the
    /// operator only changes what this load needs. A card that isn't enrolled
    /// comes back found=false — enrollment is an admin job on /Card.
    /// </summary>
    [HttpGet("api/cards/lookup")]
    public IActionResult Lookup([FromQuery] string cardNumber)
    {
        var number = (cardNumber ?? "").Trim();
        if (number.Length == 0)
            return BadRequest(new { message = "Enter a card number." });

        var card = FindByNumber(number);
        if (card == null)
            return Ok(new { found = false, cardNumber = number });

        return Ok(new { found = true, card = Describe(card) });
    }

    /// <summary>
    /// Save the card's field values and hand it to the driver. Issuing a card
    /// that is mid-trip is refused — its open ticket would be orphaned.
    /// </summary>
    [HttpPost("api/cards/{id:int}/issue")]
    public IActionResult Issue(int id, [FromBody] IssueRequest body)
    {
        var card = _db.Cards.Find(id);
        if (card == null) return NotFound(new { message = "Card not found." });
        if (!card.Enabled)
            return BadRequest(new { message = "This card is disabled. Re-enable it on the Cards page first." });

        if (!string.IsNullOrEmpty(card.OpenTicket)
            && _db.Transactions.Any(t => t.Ticket == card.OpenTicket && !t.Void && t.DateOut == null))
        {
            return BadRequest(new
            {
                message = $"Card is on open ticket #{card.OpenTicket}. Weigh that load out before re-issuing."
            });
        }
        // Stale link (ticket voided or closed elsewhere) — clear it and carry on.
        card.OpenTicket = null;

        var setup = _setupCache.Get();
        var siteId = SiteContext.CurrentSiteId(HttpContext, _db);
        var descriptors = CardFields.Describe(_db, setup, siteId);
        var values = body.Values ?? new Dictionary<string, string?>();

        // Truck IDs are validated against the carrier being saved, which the
        // generic descriptor validation can't do without the database.
        if (values.TryGetValue("truck", out var truckId) && !string.IsNullOrWhiteSpace(truckId))
        {
            var carrier = values.GetValueOrDefault("carrier")?.Trim() ?? card.Carrier;
            var t = truckId.Trim();
            var known = !string.IsNullOrWhiteSpace(carrier)
                && _db.Trucks.Any(x => x.TruckId == t && x.CarrierName == carrier && x.UseAtKiosk);
            if (!known)
                return BadRequest(new { message = $"Truck \"{t}\" is not assigned to that carrier." });
        }

        CardFields.ApplyValues(_db, card, values, descriptors);

        if (body.RecycleMode is "Default" or "Recycle" or "Deactivate")
            card.RecycleMode = body.RecycleMode;

        card.Issued = true;
        card.IssuedAt = DateTime.UtcNow;
        card.IssuedBy = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        _db.SaveChanges();

        // Missing-required is a warning, not a rejection: the kiosk will ask
        // the driver for anything the card doesn't answer.
        var stored = CardFields.ValuesOf(_db, card);
        var missing = descriptors
            .Where(d => d.Required && !stored.ContainsKey(d.Key))
            .Select(d => d.Label)
            .ToList();

        return Ok(new { card = Describe(card), missingRequired = missing });
    }

    /// <summary>Take a card out of service now (driver never came back, wrong
    /// card handed out). Refused while a ticket is open on it.</summary>
    [HttpPost("api/cards/{id:int}/release")]
    public IActionResult Release(int id)
    {
        var card = _db.Cards.Find(id);
        if (card == null) return NotFound(new { message = "Card not found." });

        if (!string.IsNullOrEmpty(card.OpenTicket)
            && _db.Transactions.Any(t => t.Ticket == card.OpenTicket && !t.Void && t.DateOut == null))
        {
            return BadRequest(new
            {
                message = $"Card is on open ticket #{card.OpenTicket}. Weigh that load out first."
            });
        }

        card.OpenTicket = null;
        card.Issued = false;
        card.IssuedAt = null;
        _db.SaveChanges();
        return Ok(new { card = Describe(card) });
    }

    /// <summary>Trucks for a carrier. The kiosk has its own copy of this at
    /// /api/kiosk/trucks, but that path is PIN-gated for unattended terminals —
    /// the card pages are behind a normal login.</summary>
    [HttpGet("api/cards/trucks")]
    public IActionResult GetTrucks([FromQuery] string carrier)
    {
        var c = (carrier ?? "").Trim();
        if (c.Length == 0) return Json(Array.Empty<string>());
        return Json(_db.Trucks
            .Where(t => t.CarrierName == c && t.UseAtKiosk)
            .OrderBy(t => t.TruckId)
            .Select(t => t.TruckId)
            .ToList());
    }

    // ===== ENROLLMENT API (Manager / Admin — enforced in Program.cs) =====

    [HttpGet("api/cardadmin")]
    public IActionResult ListCards()
    {
        var cards = _db.Cards
            .OrderByDescending(c => c.Issued)
            .ThenBy(c => c.CardNumber)
            .ToList()
            .Select(Describe)
            .ToList();
        return Json(cards);
    }

    [HttpPost("api/cardadmin")]
    public IActionResult Enroll([FromBody] EnrollRequest body)
    {
        var number = (body.CardNumber ?? "").Trim();
        if (number.Length == 0)
            return BadRequest(new { message = "Card number is required." });
        if (number.Length > 50)
            return BadRequest(new { message = "Card number is too long (50 characters max)." });
        if (_db.Cards.Any(c => c.CardNumber == number))
            return BadRequest(new { message = $"Card \"{number}\" is already enrolled." });

        var card = new Card
        {
            CardNumber = number,
            Description = Trim(body.Description, 100),
            Enabled = body.Enabled ?? true,
            RecycleMode = Normalize(body.RecycleMode)
        };
        _db.Cards.Add(card);
        _db.SaveChanges();
        return Json(Describe(card));
    }

    [HttpPut("api/cardadmin/{id:int}")]
    public IActionResult UpdateCard(int id, [FromBody] EnrollRequest body)
    {
        var card = _db.Cards.Find(id);
        if (card == null) return NotFound(new { message = "Card not found." });

        var number = (body.CardNumber ?? "").Trim();
        if (number.Length == 0)
            return BadRequest(new { message = "Card number is required." });
        if (_db.Cards.Any(c => c.Id != id && c.CardNumber == number))
            return BadRequest(new { message = $"Card \"{number}\" is already enrolled." });

        card.CardNumber = number;
        card.Description = Trim(body.Description, 100);
        if (body.Enabled.HasValue) card.Enabled = body.Enabled.Value;
        card.RecycleMode = Normalize(body.RecycleMode);

        // Disabling a card pulls it out of service immediately.
        if (!card.Enabled)
        {
            card.Issued = false;
            card.IssuedAt = null;
        }
        _db.SaveChanges();
        return Json(Describe(card));
    }

    [HttpDelete("api/cardadmin/{id:int}")]
    public IActionResult DeleteCard(int id)
    {
        var card = _db.Cards.Find(id);
        if (card == null) return NotFound(new { message = "Card not found." });

        if (!string.IsNullOrEmpty(card.OpenTicket)
            && _db.Transactions.Any(t => t.Ticket == card.OpenTicket && !t.Void && t.DateOut == null))
        {
            return BadRequest(new
            {
                message = $"Card is on open ticket #{card.OpenTicket}. Weigh that load out first."
            });
        }

        _db.Cards.Remove(card);
        _db.SaveChanges();
        return Ok(new { deleted = id });
    }

    // ===== HELPERS =====

    /// <summary>Card numbers are compared case-insensitively so a reader that
    /// emits lower-case hex still matches a number enrolled in upper case.</summary>
    private Card? FindByNumber(string number) =>
        _db.Cards.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.CardNumber, number, StringComparison.OrdinalIgnoreCase));

    private object Describe(Card card)
    {
        var setup = _setupCache.Get();
        var openTicketLive = !string.IsNullOrEmpty(card.OpenTicket)
            && _db.Transactions.Any(t => t.Ticket == card.OpenTicket && !t.Void && t.DateOut == null);

        return new
        {
            id = card.Id,
            cardNumber = card.CardNumber,
            description = card.Description,
            enabled = card.Enabled,
            issued = card.Issued,
            issuedAt = card.IssuedAt,
            issuedBy = card.IssuedBy,
            recycleMode = card.RecycleMode,
            recycles = card.RecyclesUnder(setup),
            openTicket = openTicketLive ? card.OpenTicket : null,
            lastTicket = card.LastTicket,
            lastUsedAt = card.LastUsedAt,
            values = CardFields.ValuesOf(_db, card)
        };
    }

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length > max ? v[..max] : v;
    }

    private static string Normalize(string? recycleMode) =>
        recycleMode is "Recycle" or "Deactivate" ? recycleMode : "Default";

    public class IssueRequest
    {
        /// <summary>Field values keyed the same way the descriptors are
        /// ("commodity", "truck", "cf3"). A blank value clears the field.</summary>
        public Dictionary<string, string?>? Values { get; set; }
        public string? RecycleMode { get; set; }
    }

    public class EnrollRequest
    {
        public string? CardNumber { get; set; }
        public string? Description { get; set; }
        public bool? Enabled { get; set; }
        public string? RecycleMode { get; set; }
    }
}
