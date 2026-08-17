using Microsoft.AspNetCore.Mvc;
using Foundation.Web.Data;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

/// <summary>
/// Card Reader management. Like the Camera and Scale pages, everything on this
/// page is driven over SignalR by the connected RFID Reader Service(s) — the
/// web app stores no reader configuration of its own, it just relays CRUD and
/// shows live card reads for commissioning.
/// </summary>
public class ReaderController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;

    public ReaderController(ScaleDbContext db, AppSetupCache setupCache)
    {
        _db = db;
        _setupCache = setupCache;
    }

    public IActionResult Index()
    {
        ViewBag.UseCardReader = _setupCache.Get().UseCardReader;
        return View();
    }
}
