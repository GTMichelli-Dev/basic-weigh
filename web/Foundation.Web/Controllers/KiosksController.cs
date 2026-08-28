using Microsoft.AspNetCore.Mvc;
using Foundation.Web.Data;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

/// <summary>
/// Kiosk management — the roster of displays that have enrolled themselves.
/// Kiosks are created by the kiosks: a display that opens /Kiosk without a
/// known device runs its own setup wizard and registers from there. This page
/// exists to see them, rename them, re-point their hardware from the office,
/// and remove one that has been retired.
///
/// Deliberately not "/Kiosk", which is the driver-facing display itself and is
/// gated by the kiosk PIN rather than an operator login.
/// </summary>
public class KiosksController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;

    public KiosksController(ScaleDbContext db, AppSetupCache setupCache)
    {
        _db = db;
        _setupCache = setupCache;
    }

    public IActionResult Index()
    {
        var setup = _setupCache.Get();
        ViewBag.UseCardReader = setup.UseCardReader;
        // Demo sites have no reader service to announce anything, so the
        // simulated reader is offered here too — same value the kiosk's own
        // setup wizard writes.
        ViewBag.DemoMode = setup.DemoMode;
        return View();
    }

    [HttpGet("api/kiosks")]
    public IActionResult GetKiosks()
    {
        // Scale names are resolved here rather than in the page so a kiosk
        // pointed at a deleted scale reads as "no scale" instead of an id.
        var scales = _db.Scales.ToDictionary(s => s.Id, s => s.Name);

        var kiosks = _db.Kiosks
            .OrderBy(k => k.Name)
            .ToList()
            .Select(k => new
            {
                k.Id,
                k.Name,
                k.DeviceId,
                k.ScaleId,
                ScaleName = k.ScaleId.HasValue && scales.ContainsKey(k.ScaleId.Value)
                    ? scales[k.ScaleId.Value]
                    : null,
                k.PrinterId,
                k.ReaderId,
                k.Active,
                k.CreatedAt,
                k.LastSeenAt
            })
            .ToList();

        return Json(kiosks);
    }

    [HttpPut("api/kiosks/{id:int}")]
    public IActionResult UpdateKiosk(int id, [FromBody] KioskUpdateDto dto)
    {
        var kiosk = _db.Kiosks.Find(id);
        if (kiosk == null) return NotFound(new { message = "Kiosk not found" });

        var name = (dto.Name ?? "").Trim();
        if (name.Length == 0) return BadRequest(new { message = "A kiosk needs a name." });
        if (name.Length > 100) name = name[..100];
        if (_db.Kiosks.Any(k => k.Id != id && k.Name == name))
            return BadRequest(new { message = "Another kiosk already has that name." });
        kiosk.Name = name;

        // An assignment pointing at a scale that no longer exists would leave
        // the kiosk weighing on nothing; store null and let it fall back.
        kiosk.ScaleId = dto.ScaleId.HasValue && _db.Scales.Any(s => s.Id == dto.ScaleId.Value)
            ? dto.ScaleId
            : null;

        kiosk.PrinterId = Blank(dto.PrinterId);
        kiosk.ReaderId = Blank(dto.ReaderId);
        kiosk.Active = dto.Active;
        _db.SaveChanges();

        return Ok(new { success = true });
    }

    /// <summary>
    /// Forget a kiosk. The display keeps working until it is reloaded, then
    /// finds itself unregistered and runs the setup wizard again — which is
    /// also how you re-commission a kiosk that was set up wrong.
    /// </summary>
    [HttpDelete("api/kiosks/{id:int}")]
    public IActionResult DeleteKiosk(int id)
    {
        var kiosk = _db.Kiosks.Find(id);
        if (kiosk == null) return NotFound(new { message = "Kiosk not found" });

        _db.Kiosks.Remove(kiosk);
        _db.SaveChanges();
        return Ok(new { success = true });
    }

    private static string? Blank(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 ? null : (v.Length > 100 ? v[..100] : v);
    }

    public class KioskUpdateDto
    {
        public string? Name { get; set; }
        public int? ScaleId { get; set; }
        public string? PrinterId { get; set; }
        public string? ReaderId { get; set; }
        public bool Active { get; set; } = true;
    }
}
