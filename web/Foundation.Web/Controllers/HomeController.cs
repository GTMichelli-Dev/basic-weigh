using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

public class HomeController : Controller
{
    private readonly IScaleService _scaleService;
    private readonly ScaleDbContext _db;
    private readonly PrintQueueService _printQueue;
    private readonly AppSetupCache _setupCache;
    private readonly IHubContext<ScaleHub> _hub;
    private readonly ScaleWeightStore _weightStore;
    private readonly SiteScales _siteScales;

    public HomeController(IScaleService scaleService, ScaleDbContext db, PrintQueueService printQueue, AppSetupCache setupCache, IHubContext<ScaleHub> hub, ScaleWeightStore weightStore, SiteScales siteScales)
    {
        _scaleService = scaleService;
        _db = db;
        _printQueue = printQueue;
        _setupCache = setupCache;
        _hub = hub;
        _weightStore = weightStore;
        _siteScales = siteScales;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Get weight from a site scale: ?scale={Scale.Id}, defaulting to the
    /// first active scale. Live mode reads the scale's hardware feed; demo
    /// mode reads its per-scale simulator. Passing serviceId/scaleId instead
    /// queries a raw hardware feed directly (Scale page live view).
    /// </summary>
    [HttpGet("api/scale/weight")]
    public IActionResult GetWeight([FromQuery] int? scale = null, [FromQuery] string? serviceId = null, [FromQuery] string? scaleId = null)
    {
        var setup = _setupCache.Get();

        // Raw hardware query (bypasses the named-scale list)
        if (!string.IsNullOrEmpty(scaleId))
        {
            var lookupId = string.IsNullOrEmpty(serviceId) ? scaleId : $"{serviceId}:{scaleId}";
            var raw = _weightStore.Get(lookupId);
            if (raw == null)
                return Json(new { weight = 0, motion = false, ok = false, error = true, comError = true });
            return Json(new
            {
                weight = raw.Weight,
                motion = raw.Motion,
                ok = raw.Ok,
                error = !raw.Ok,
                comError = raw.ComError,
                scaleId = raw.ScaleId,
                serviceId = raw.ServiceId
            });
        }

        var site = SiteScales.Resolve(_db, scale);
        var r = _siteScales.Read(site, setup.DemoMode);
        return Json(new
        {
            weight = r.Weight,
            motion = r.Motion,
            ok = r.Ok,
            error = r.Error,
            comError = r.ComError,
            status = r.Status,
            onScale = r.OnScale,
            scaleDbId = r.ScaleDbId,
            scaleName = r.ScaleName
        });
    }

    /// <summary>
    /// Live readings for every active scale at the operator's current location
    /// (navbar picker). Drives the click-to-select scale tiles in the Get
    /// Weight dialog on multi-scale sites.
    /// </summary>
    [HttpGet("api/scale/weights")]
    public IActionResult GetWeights()
    {
        var setup = _setupCache.Get();
        var siteId = SiteContext.CurrentSiteId(HttpContext, _db);
        var scales = _db.Scales.Where(s => s.Active).ForSite(siteId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToList();

        return Json(scales.Select(s =>
        {
            var r = _siteScales.Read(s, setup.DemoMode);
            return new
            {
                id = s.Id,
                name = s.Name,
                weight = r.Weight,
                motion = r.Motion,
                error = r.Error,
                status = r.Status,
                onScale = r.OnScale
            };
        }).ToList());
    }

    [HttpPost("api/scale/simulate")]
    public IActionResult Simulate([FromBody] SimulateRequest request)
    {
        var setup = _setupCache.Get();
        if (!setup.DemoMode)
            return BadRequest(new { success = false, message = "Not in demo mode. Enable Demo Mode in Setup to use the simulator." });

        var site = SiteScales.Resolve(_db, request.ScaleDbId);
        if (site == null)
            return BadRequest(new { success = false, message = "No scale configured. Add one on the Scales page." });

        _siteScales.Simulate(site, request.Weight, request.Motion, request.Error);

        // Keep the legacy single simulator in step so anything still reading
        // IScaleService (e.g. ScaleBroadcastService) sees the default scale.
        if (_scaleService is SimulatedScaleService sim)
        {
            sim.SetWeight(request.Weight);
            sim.SetMotion(request.Motion);
            sim.SetError(request.Error);
        }
        return Json(new { success = true, scaleDbId = site.Id, scaleName = site.Name });
    }

    /// <summary>
    /// Called by the physical scale indicator (or Pi bridge) to push weight readings.
    /// Only works when DemoMode is OFF. Updates the multi-scale weight store.
    /// Returns any pending print job in the response.
    /// </summary>
    [HttpPost("api/scale/weight")]
    public IActionResult UpdateWeight([FromBody] ScaleWeightRequest request)
    {
        var setup = _setupCache.Get();
        if (setup.DemoMode)
            return BadRequest(new { success = false, message = "System is in demo mode. Use api/scale/simulate instead." });

        var scaleId = request.ScaleId ?? "default";
        var serviceId = request.ServiceId ?? "api";

        // Update the multi-scale weight store
        _weightStore.Update(scaleId, serviceId, request.Weight, request.Motion, !request.Error,
            request.Error ? "Error" : (request.Motion ? "Motion" : "Ok"),
            onScale: request.OnScale);

        // Check for pending print jobs (Scale mode only — RemotePrinter uses SignalR)
        if (setup.RemotePrintMode == "Scale" && _printQueue.TryDequeue(out var ticketId) && ticketId != null)
        {
            return Json(new
            {
                success = true,
                print = new { ticketId, pdfUrl = $"/api/ticket/{ticketId}/pdf" }
            });
        }

        return Json(new { success = true });
    }

    public class ScaleWeightRequest
    {
        public string? ScaleId { get; set; }
        public string? ServiceId { get; set; }
        public int Weight { get; set; }
        public bool Motion { get; set; }
        public bool Error { get; set; }

        /// <summary>
        /// False when an end detector says the truck is not all the way on the
        /// deck. Defaults true so every existing pusher — which sends no such
        /// field — keeps weighing exactly as it does now.
        /// </summary>
        public bool OnScale { get; set; } = true;
    }

    public class SimulateRequest
    {
        public int Weight { get; set; }
        public bool Motion { get; set; }
        public bool Error { get; set; }
        /// <summary>Site scale to simulate (Scale.Id); null = default scale.</summary>
        public int? ScaleDbId { get; set; }
    }

    /// <summary>
    /// Zero the scale via SignalR to the ScaleReaderService.
    /// The service sends the zero command to the physical scale indicator.
    /// </summary>
    /// <summary>
    /// Zero the scale via SignalR. Pass serviceId/scaleId or uses the configured scale from Setup.
    /// </summary>
    [HttpPost("api/scale/zero")]
    public async Task<IActionResult> ZeroScale([FromQuery] int? scale = null, [FromQuery] string? serviceId = null, [FromQuery] string? scaleId = null)
    {
        // Determine target hardware feed: explicit serviceId/scaleId, or the
        // named site scale (?scale={Scale.Id}, default = first active).
        string targetScaleId;
        string targetServiceId;

        if (!string.IsNullOrEmpty(serviceId) && !string.IsNullOrEmpty(scaleId))
        {
            targetServiceId = serviceId;
            targetScaleId = scaleId;
        }
        else
        {
            var site = SiteScales.Resolve(_db, scale);
            if (site?.HardwareId == null || !site.HardwareId.Contains(':'))
                return Json(new { success = false, message = "No hardware scale configured. Link a hardware feed on the Scales page." });
            var parts = site.HardwareId.Split(':', 2);
            targetServiceId = parts[0];
            targetScaleId = parts[1];
        }

        // Send to the specific scale service group
        await _hub.Clients.Group($"Scale_{targetServiceId}").SendAsync("ZeroScale", targetScaleId);
        return Json(new { success = true, message = $"Zero command sent to {targetServiceId}:{targetScaleId}." });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
