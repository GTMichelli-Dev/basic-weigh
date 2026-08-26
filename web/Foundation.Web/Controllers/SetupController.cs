using Microsoft.AspNetCore.Mvc;
using Foundation.Web.Data;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

public class SetupController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly IConfiguration _config;
    private readonly AppSetupCache _setupCache;

    public SetupController(ScaleDbContext db, IConfiguration config, AppSetupCache setupCache)
    {
        _db = db;
        _config = config;
        _setupCache = setupCache;
    }

    public IActionResult Index()
    {
        var setup = _setupCache.Get();
        ViewBag.ShowResetDatabase = _config.GetValue<bool>("ShowResetDatabase", false);
        return View(setup);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Index(AppSetup setup, IFormFile? iconFile, bool removeIcon = false, string? activeTab = null)
    {
        var existing = _db.AppSetup.Find(setup.Id);
        if (existing == null) return NotFound();

        // Preserve the tab the operator was on so the redirect lands them back there.
        if (!string.IsNullOrEmpty(activeTab)) TempData["ActiveTab"] = activeTab;

        existing.Header1 = setup.Header1;
        existing.Header2 = setup.Header2;
        existing.Header3 = setup.Header3;
        existing.Header4 = setup.Header4;
        existing.TicketNumber = setup.TicketNumber;
        existing.TicketsPerPage = setup.TicketsPerPage;
        existing.DemoMode = setup.DemoMode;
        existing.KioskCount = setup.KioskCount;
        existing.Theme = setup.Theme;
        // Master switch for the bilingual driver screens, and the default they
        // start in. An unrecognised code (hand-posted form, older client) falls
        // back to English rather than storing something Lang.Resolve ignores.
        existing.EnableSpanish = setup.EnableSpanish;
        existing.Language = Lang.Normalize(setup.Language) ?? Lang.English;
        existing.PromptKioskCommodityOnInbound = setup.PromptKioskCommodityOnInbound;
        existing.PromptKioskCommodityOnOutbound = setup.PromptKioskCommodityOnOutbound;
        existing.AllowSkipCommodity = setup.AllowSkipCommodity;

        existing.PromptKioskCustomerOnInbound = setup.PromptKioskCustomerOnInbound;
        existing.PromptKioskCustomerOnOutbound = setup.PromptKioskCustomerOnOutbound;
        existing.AllowSkipCustomer = setup.AllowSkipCustomer;

        existing.PromptKioskCarrier = setup.PromptKioskCarrier;
        existing.AllowSkipCarrier = setup.AllowSkipCarrier;

        existing.PromptKioskLocationOnInbound = setup.PromptKioskLocationOnInbound;
        existing.PromptKioskLocationOnOutbound = setup.PromptKioskLocationOnOutbound;
        existing.AllowSkipLocation = setup.AllowSkipLocation;

        existing.PromptKioskTruckId = setup.PromptKioskTruckId;
        existing.AllowSkipTruckId = setup.AllowSkipTruckId;

        existing.PromptKioskDestinationOnInbound = setup.PromptKioskDestinationOnInbound;
        existing.PromptKioskDestinationOnOutbound = setup.PromptKioskDestinationOnOutbound;
        existing.AllowSkipDestination = setup.AllowSkipDestination;

        existing.PromptKioskBinOnInbound = setup.PromptKioskBinOnInbound;
        existing.PromptKioskBinOnOutbound = setup.PromptKioskBinOnOutbound;
        existing.AllowSkipBin = setup.AllowSkipBin;

        existing.HideKioskOnScreenButtons = setup.HideKioskOnScreenButtons;
        existing.TimeZoneId = string.IsNullOrWhiteSpace(setup.TimeZoneId) ? "America/Chicago" : setup.TimeZoneId;
        // Apply the new TZ to the running app immediately so subsequent
        // requests (and the redirect's GET render) use it without a restart.
        AppTimeZone.Configure(existing.TimeZoneId);
        existing.KioskDarkMode = setup.KioskDarkMode;
        existing.UseLogin = setup.UseLogin;
        existing.KioskCode = setup.KioskCode ?? "12345";
        existing.ApiDefinitionPin = setup.ApiDefinitionPin ?? "12345";
        existing.RecallLastValues = setup.RecallLastValues;
        existing.RemotePrintMode = setup.RemotePrintMode ?? "None";
        existing.UseQuickBooks = setup.UseQuickBooks;
        existing.UseBinInventory = setup.UseBinInventory;
        existing.BinRequired = setup.BinRequired;
        existing.BinCommodityLock = setup.BinCommodityLock;
        existing.DefaultReportUnit = setup.DefaultReportUnit == "kg" ? "kg" : "lbs";
        existing.SavePicture = setup.SavePicture;
        existing.UseRetainedTare = setup.UseRetainedTare;
        existing.AutoClearStaleRetainedTare = setup.AutoClearStaleRetainedTare;
        existing.AllowTareResetKiosk = setup.AllowTareResetKiosk;
        existing.AllowTareResetMobile = setup.AllowTareResetMobile;
        existing.AllowTareResetCard = setup.AllowTareResetCard;
        existing.UseCardReader = setup.UseCardReader;
        existing.RecycleCards = setup.RecycleCards;
        existing.SignatureMode = setup.SignatureMode ?? "None";
        existing.SignaturePadId = setup.SignaturePadId;
        existing.SignatureRequired = setup.SignatureRequired;
        existing.PrintSignatureOnTicket = setup.PrintSignatureOnTicket;

        existing.HideCustomer = setup.HideCustomer;
        existing.HideCarrier = setup.HideCarrier;
        existing.HideTruckId = setup.HideTruckId;
        existing.HideCommodity = setup.HideCommodity;
        existing.HideLocation = setup.HideLocation;
        existing.HideDestination = setup.HideDestination;
        existing.HideNotes = setup.HideNotes;

        existing.FieldOrderCommodity = setup.FieldOrderCommodity;
        existing.FieldOrderCustomer = setup.FieldOrderCustomer;
        existing.FieldOrderCarrier = setup.FieldOrderCarrier;
        existing.FieldOrderTruckId = setup.FieldOrderTruckId;
        existing.FieldOrderLocation = setup.FieldOrderLocation;
        existing.FieldOrderDestination = setup.FieldOrderDestination;
        existing.FieldOrderBin = setup.FieldOrderBin;
        existing.FieldOrderNotes = setup.FieldOrderNotes;

        // Field-visibility rules:
        // - Trucks are selected per carrier, so a visible Truck ID without a
        //   Carrier is unusable — hiding Carrier hides Truck ID too.
        // - Retained Tare identifies trucks by Carrier + Truck ID, so those two
        //   stay visible while it's enabled (mirrors the kiosk-prompt forcing).
        if (existing.HideCarrier) existing.HideTruckId = true;
        if (existing.UseRetainedTare)
        {
            existing.HideCarrier = false;
            existing.HideTruckId = false;
        }

        // Retained Tare needs both carrier and truck to identify the truck.
        // Force the prompts on AND disable Allow Skip so the kiosk flow always
        // captures the identifying fields with real values. Also force every
        // OnOutbound flag false — under Retained Tare the weigh-out is
        // auto-completed when the truck weighs in, so an outbound prompt would
        // never fire and would be a footgun if left checked.
        if (existing.UseRetainedTare)
        {
            existing.PromptKioskCarrier = true;
            existing.PromptKioskTruckId = true;
            existing.AllowSkipCarrier = false;
            existing.AllowSkipTruckId = false;

            existing.PromptKioskCommodityOnOutbound = false;
            existing.PromptKioskCustomerOnOutbound = false;
            existing.PromptKioskLocationOnOutbound = false;
            existing.PromptKioskDestinationOnOutbound = false;
            existing.PromptKioskBinOnOutbound = false;
        }

        // A hidden field must never be prompted for at the kiosk — force its
        // prompt flags off so the two settings can't contradict each other.
        // (Runs after the Retained Tare block; RT already forces Carrier and
        // Truck ID visible above, so there's no conflict.)
        if (existing.HideCustomer)
        {
            existing.PromptKioskCustomerOnInbound = false;
            existing.PromptKioskCustomerOnOutbound = false;
        }
        if (existing.HideCarrier) existing.PromptKioskCarrier = false;
        if (existing.HideTruckId) existing.PromptKioskTruckId = false;
        if (existing.HideCommodity)
        {
            existing.PromptKioskCommodityOnInbound = false;
            existing.PromptKioskCommodityOnOutbound = false;
        }
        if (existing.HideLocation)
        {
            existing.PromptKioskLocationOnInbound = false;
            existing.PromptKioskLocationOnOutbound = false;
        }
        if (existing.HideDestination)
        {
            existing.PromptKioskDestinationOnInbound = false;
            existing.PromptKioskDestinationOnOutbound = false;
        }
        // Bin has no Hide flag — the feature toggle IS its visibility. Turning
        // Bin Inventory off forces the kiosk prompts off so they can't fire.
        if (!existing.UseBinInventory)
        {
            existing.PromptKioskBinOnInbound = false;
            existing.PromptKioskBinOnOutbound = false;
        }
        // A required bin can't be skippable at the kiosk.
        if (existing.UseBinInventory && existing.BinRequired)
            existing.AllowSkipBin = false;
        // Camera assignments are managed on the Camera page and scales on the
        // Scales page (named site scales), so the posted AppSetup never carries
        // those fields — don't overwrite the saved values with the nulls that
        // come back from model binding. (ScaleId itself is legacy: the
        // AddMultiScale migration converted it into the first Scales row.)

        if (removeIcon)
        {
            existing.Icon = null;
            existing.IconContentType = null;
        }
        else if (iconFile is { Length: > 0 })
        {
            using var ms = new MemoryStream();
            iconFile.CopyTo(ms);
            existing.Icon = ms.ToArray();
            existing.IconContentType = iconFile.ContentType;
        }

        _db.SaveChanges();
        _setupCache.Invalidate();

        // Auto-save posts (fetch) get JSON back; a plain form post (no-JS
        // fallback) keeps the classic redirect + banner.
        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
            return Json(new { success = true });

        TempData["Message"] = "Settings saved successfully.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// QR code (PNG) for the signature-pad URL so the tablet can scan instead
    /// of typing. Only same-app URLs are encoded — padId is the pad-id query
    /// value; the URL is built server-side from the current request host.
    /// </summary>
    [HttpGet("api/setup/signature-pad-qr")]
    public IActionResult SignaturePadQr(string? padId = null)
    {
        var url = $"{Request.Scheme}://{Request.Host}/SignaturePad?pad-id={Uri.EscapeDataString(string.IsNullOrWhiteSpace(padId) ? "default" : padId)}";
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule: 5);
        return File(png, "image/png");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResetTicket(string reportName)
    {
        var reportsDir = Path.Combine(Directory.GetCurrentDirectory(), "Reports");
        var path = Path.Combine(reportsDir, reportName + ".repx");
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);

        var label = reportName == "KioskTicketReport" ? "Kiosk Inbound Ticket" : "Completed Ticket";
        TempData["Message"] = $"{label} has been reset to default format.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Wipe the weighing history: tickets and everything keyed to a ticket
    /// number. Master data (customers, carriers, commodities, trucks, bins) is
    /// untouched, so the site keeps weighing straight afterwards.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ClearTransactions()
    {
        if (!_config.GetValue<bool>("ShowResetDatabase", false))
            return Forbid();

        var removed = _db.Transactions.Count();
        ClearTransactionData();
        _db.SaveChanges();
        _setupCache.Invalidate();

        TempData["Message"] = removed == 0
            ? "No transactions to clear. Master data is unchanged."
            : $"{removed:N0} transaction{(removed == 1 ? "" : "s")} cleared. Master data is unchanged and the ticket number is back to 1.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ClearDatabase()
    {
        if (!_config.GetValue<bool>("ShowResetDatabase", false))
            return Forbid();

        ClearTransactionData();
        _db.Customers.RemoveRange(_db.Customers);
        _db.Carriers.RemoveRange(_db.Carriers);
        _db.Commodities.RemoveRange(_db.Commodities);
        _db.Locations.RemoveRange(_db.Locations);
        _db.Destinations.RemoveRange(_db.Destinations);
        _db.Trucks.RemoveRange(_db.Trucks);
        _db.Bins.RemoveRange(_db.Bins);
        _db.BinAdjustments.RemoveRange(_db.BinAdjustments);

        _db.SaveChanges();
        _setupCache.Invalidate();

        TempData["Message"] = "Database cleared. All transactions and master data removed.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Remove every ticket and the rows and files that hang off a ticket
    /// number, then send the ticket counter back to 1. Shared by "Clear
    /// Transactions" and the full "Clear Database" so neither can leave custom
    /// values, email logs, card links or ticket images pointing at tickets that
    /// no longer exist. Caller SaveChanges().
    /// </summary>
    private void ClearTransactionData()
    {
        _db.TransactionCustomValues.RemoveRange(_db.TransactionCustomValues);
        _db.LoadEmailLogs.RemoveRange(_db.LoadEmailLogs);
        _db.Transactions.RemoveRange(_db.Transactions);

        // A card left bound to a deleted ticket would send its driver into the
        // weigh-out flow for a load that no longer exists.
        var setup = _db.AppSetup.First();
        foreach (var card in _db.Cards.Where(c => c.OpenTicket != null).ToList())
            CardFields.Release(card, setup, card.OpenTicket!);

        DeleteTicketImages();

        setup.TicketNumber = 1;
    }

    /// <summary>
    /// Drop the per-ticket signature and camera captures under
    /// wwwroot/images/tickets. Best effort: a locked or missing file must not
    /// fail the clear, since the database rows are the record that matters.
    /// </summary>
    private static void DeleteTicketImages()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "tickets");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try { System.IO.File.Delete(file); }
            catch (IOException) { /* in use — leave it */ }
            catch (UnauthorizedAccessException) { /* read-only — leave it */ }
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult LoadTestData(string sampleType = "gravel")
    {
        if (!_config.GetValue<bool>("ShowResetDatabase", false))
            return Forbid();

        // Clear everything
        _db.Transactions.RemoveRange(_db.Transactions);
        _db.Customers.RemoveRange(_db.Customers);
        _db.Carriers.RemoveRange(_db.Carriers);
        _db.Commodities.RemoveRange(_db.Commodities);
        _db.Locations.RemoveRange(_db.Locations);
        _db.Destinations.RemoveRange(_db.Destinations);
        _db.Trucks.RemoveRange(_db.Trucks);
        _db.Bins.RemoveRange(_db.Bins);
        _db.BinAdjustments.RemoveRange(_db.BinAdjustments);
        _db.SaveChanges();

        if (string.Equals(sampleType, "farming", StringComparison.OrdinalIgnoreCase))
            return LoadFarmingSample();

        // ===== GRAVEL PIT / QUARRY SAMPLE =====

        // --- Master Data (matching QuickBooks sample data) ---
        var customerNames = new[]
        {
            "ABC Construction LLC", "Greenfield Landscaping", "City of Springfield - Public Works",
            "Riverstone Builders", "Martin Paving Co", "Horizon Development Group",
            "Lakeview Concrete", "Prairie Homebuilders", "Tri-County Excavating",
            "Heartland Asphalt", "Sangamon County Highway Dept", "Cornerstone Ready Mix",
            "Iron Bridge Contractors", "Flatland Grading", "Stonewall Erosion Control",
            "Capital City Concrete", "Aggregate Haulers Midwest", "Lakeside Site Development"
        };
        foreach (var c in customerNames)
            _db.Customers.Add(new Customer { CustomerName = c, Active = true, UseAtKiosk = true });

        var commodityNames = new[]
        {
            "Fill Sand", "Mason Sand", "Concrete Sand", "Pea Gravel",
            "3/4 Crushed Stone", "1-1/2 Crushed Stone", "CA-6 Grade 8 Limestone",
            "CA-7 Chip Rock", "Rip Rap", "Screened Topsoil", "Road Gravel", "Recycled Concrete",
            "Hauling Fee"
        };
        foreach (var c in commodityNames)
            _db.Commodities.Add(new Commodity { CommodityName = c, Active = true, UseAtKiosk = true });

        var carrierData = new[]
        {
            "ABC Construction LLC", "Martin Paving Co", "Tri-County Excavating",
            "Flatland Grading", "Aggregate Haulers Midwest", "Heartland Asphalt",
            "River Road Transport", "Central IL Trucking", "Prairie State Hauling"
        };
        foreach (var c in carrierData)
            _db.Carriers.Add(new Carrier { CarrierName = c, Active = true, UseAtKiosk = true });

        var truckData = new (string TruckId, string Carrier)[]
        {
            ("ABC-101", "ABC Construction LLC"), ("ABC-102", "ABC Construction LLC"),
            ("MP-201", "Martin Paving Co"), ("MP-202", "Martin Paving Co"),
            ("TC-301", "Tri-County Excavating"), ("TC-302", "Tri-County Excavating"), ("TC-303", "Tri-County Excavating"),
            ("FG-401", "Flatland Grading"), ("FG-402", "Flatland Grading"),
            ("AH-501", "Aggregate Haulers Midwest"), ("AH-502", "Aggregate Haulers Midwest"), ("AH-503", "Aggregate Haulers Midwest"),
            ("HA-601", "Heartland Asphalt"), ("HA-602", "Heartland Asphalt"),
            ("RR-701", "River Road Transport"), ("RR-702", "River Road Transport"), ("RR-703", "River Road Transport"),
            ("CI-801", "Central IL Trucking"), ("CI-802", "Central IL Trucking"),
            ("PS-901", "Prairie State Hauling"), ("PS-902", "Prairie State Hauling")
        };
        foreach (var (tid, carrier) in truckData)
            _db.Trucks.Add(new Truck { TruckId = tid, CarrierName = carrier, UseAtKiosk = true });

        var locationNames = new[] { "Pit A - North", "Pit B - South", "Stockpile Yard", "Wash Plant", "Crusher" };
        foreach (var l in locationNames)
            _db.Locations.Add(new Location { LocationName = l, Active = true, UseAtKiosk = true });

        var destinationNames = new[]
        {
            "Highway 50 Job Site", "Downtown Springfield Project", "Chatham Subdivision",
            "County Road 12 Repair", "Lincoln Acres Development", "Riverton Bridge",
            "Industrial Park West", "Airport Expansion"
        };
        foreach (var d in destinationNames)
            _db.Destinations.Add(new Destination { DestinationName = d, Active = true, UseAtKiosk = true });

        _db.SaveChanges();

        // --- System settings: Demo mode, 1 kiosk, light mode ---
        var setup = _db.AppSetup.First();
        setup.DemoMode = true;
        setup.KioskCount = 1;
        setup.KioskDarkMode = false;
        // Aggregate yards don't track bins — keep the feature off for this sample.
        setup.UseBinInventory = false;
        setup.BinRequired = false;

        // --- Build lookup lists for transactions ---
        var customers = customerNames.ToList();
        var commodities = commodityNames.ToList();
        var carriers = carrierData.ToList();
        var locations = locationNames.ToList();
        var destinations = destinationNames.ToList();
        var trucks = truckData.ToList();

        var rng = new Random(42);
        var now = DateTime.Now;
        int ticketNum = 1000;
        int completedCount = 0, voidedCount = 0;

        // Generate ~1 year of completed tickets, ~20 per weekday, 8am-5pm
        var startDate = now.AddYears(-1).Date;
        var endDate = now.Date;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                continue;

            int ticketsToday = rng.Next(17, 24);
            for (int i = 0; i < ticketsToday; i++)
            {
                ticketNum++;
                var customer = customers[rng.Next(customers.Count)];
                var commodity = commodities[rng.Next(commodities.Count)];
                var carrier = carriers[rng.Next(carriers.Count)];

                // Randomly omit location and destination (30% chance each)
                string? location = rng.NextDouble() < 0.3 ? null : locations[rng.Next(locations.Count)];
                string? destination = rng.NextDouble() < 0.3 ? null : destinations[rng.Next(destinations.Count)];

                // Pick a truck for this carrier
                var carrierTrucks = trucks.Where(t => t.Carrier == carrier).ToList();
                var truckId = carrierTrucks.Count > 0
                    ? carrierTrucks[rng.Next(carrierTrucks.Count)].TruckId
                    : "T-" + rng.Next(100, 999);

                // Date in: 8am-4pm (leaves room for out time before 5pm)
                var dateIn = date.AddHours(rng.Next(8, 16)).AddMinutes(rng.Next(0, 60));
                // Date out: 5-20 min after date in
                var dateOut = dateIn.AddMinutes(rng.Next(5, 21));

                // Weights: one is tare (9000-25000), the other is gross (higher)
                // Net is ~30% of the larger weight
                var tareWeight = rng.Next(9000, 25001);
                var netWeight = (int)(tareWeight * (0.25 + rng.NextDouble() * 0.15)); // 25-40% of tare
                var grossWeight = tareWeight + netWeight;

                // Randomly inbound > outbound (~40% of time, e.g. dumping material)
                int inWeight, outWeight;
                if (rng.NextDouble() < 0.4)
                {
                    // Inbound is heavy (loaded coming in, empty going out)
                    inWeight = grossWeight;
                    outWeight = tareWeight;
                }
                else
                {
                    // Outbound is heavy (picking up material)
                    inWeight = tareWeight;
                    outWeight = grossWeight;
                }

                var isVoid = rng.NextDouble() < 0.005;

                _db.Transactions.Add(new Transaction
                {
                    Ticket = ticketNum.ToString(),
                    DateIn = dateIn,
                    DateOut = dateOut,
                    InWeight = inWeight,
                    OutWeight = outWeight,
                    Customer = customer,
                    Carrier = carrier,
                    TruckId = truckId,
                    Commodity = commodity,
                    Location = location,
                    Destination = destination,
                    Void = isVoid
                });

                if (isVoid) voidedCount++;
                else completedCount++;
            }
        }

        // Add 5 inbound (trucks in yard) tickets
        for (int i = 0; i < 5; i++)
        {
            ticketNum++;
            var customer = customers[rng.Next(customers.Count)];
            var commodity = commodities[rng.Next(commodities.Count)];
            var carrier = carriers[rng.Next(carriers.Count)];
            string? location = rng.NextDouble() < 0.3 ? null : locations[rng.Next(locations.Count)];
            string? destination = rng.NextDouble() < 0.3 ? null : destinations[rng.Next(destinations.Count)];
            var carrierTrucks = trucks.Where(t => t.Carrier == carrier).ToList();
            var truckId = carrierTrucks.Count > 0
                ? carrierTrucks[rng.Next(carrierTrucks.Count)].TruckId
                : "T-" + rng.Next(100, 999);

            _db.Transactions.Add(new Transaction
            {
                Ticket = ticketNum.ToString(),
                DateIn = now.AddMinutes(-(rng.Next(10, 180))),
                InWeight = rng.Next(9000, 110001),
                Customer = customer,
                Carrier = carrier,
                TruckId = truckId,
                Commodity = commodity,
                Location = location,
                Destination = destination,
                Void = false
            });
        }

        ticketNum++;
        setup.TicketNumber = ticketNum;
        _db.SaveChanges();
        _setupCache.Invalidate();

        TempData["Message"] = $"Test data loaded: {completedCount} completed, 5 in yard, and {voidedCount} voided tickets. Demo mode enabled with 1 kiosk (light).";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Farm / grain sample: fields haul harvest loads INTO numbered bins
    /// (truck arrives heavy, leaves light), sales haul OUT of the bins to
    /// grain buyers (arrives light, leaves heavy), plus a post-harvest
    /// shrinkage true-up per bin — so the Bin Inventory report demos with
    /// realistic numbers. Enables Bin Inventory; caller has already cleared
    /// the database.
    /// </summary>
    private IActionResult LoadFarmingSample()
    {
        // --- Master data ---
        var buyerNames = new[]
        {
            "ADM Grain - Decatur", "Cargill - Bloomington", "Prairie Central Co-op",
            "Bunge - Danville", "One Earth Energy (Ethanol)", "Heartland Feed Mill"
        };
        foreach (var c in buyerNames)
            _db.Customers.Add(new Customer { CustomerName = c, Active = true, UseAtKiosk = true });

        var commodityNames = new[] { "Corn", "Soybeans", "Wheat" };
        foreach (var c in commodityNames)
            _db.Commodities.Add(new Commodity { CommodityName = c, Active = true, UseAtKiosk = true });

        var carrierNames = new[] { "Farm Trucks", "Johnson Custom Hauling", "B&K Trucking" };
        foreach (var c in carrierNames)
            _db.Carriers.Add(new Carrier { CarrierName = c, Active = true, UseAtKiosk = true });

        var truckData = new (string TruckId, string Carrier)[]
        {
            ("FT-1", "Farm Trucks"), ("FT-2", "Farm Trucks"), ("FT-3", "Farm Trucks"),
            ("JH-88", "Johnson Custom Hauling"), ("JH-89", "Johnson Custom Hauling"),
            ("BK-12", "B&K Trucking")
        };
        foreach (var (tid, carrier) in truckData)
            _db.Trucks.Add(new Truck { TruckId = tid, CarrierName = carrier, UseAtKiosk = true });

        // Fields the grain comes from
        var fieldNames = new[] { "North 80", "South Quarter", "Home 160", "River Bottom", "Miller Lease", "East 40" };
        foreach (var f in fieldNames)
            _db.Locations.Add(new Location { LocationName = f, Active = true, UseAtKiosk = true });

        // Elevators the grain ships to
        var elevatorNames = new[] { "ADM Decatur", "Cargill Bloomington", "Prairie Central Elevator", "One Earth Energy" };
        foreach (var d in elevatorNames)
            _db.Destinations.Add(new Destination { DestinationName = d, Active = true, UseAtKiosk = true });

        // Bins, each dedicated to one commodity
        var binCommodity = new (string Bin, string Commodity)[]
        {
            ("Bin 1", "Corn"), ("Bin 2", "Corn"), ("Bin 3", "Corn"),
            ("Bin 4", "Soybeans"), ("Bin 5", "Soybeans"), ("Bin 6", "Wheat")
        };
        foreach (var (bin, _) in binCommodity)
            _db.Bins.Add(new Bin { BinName = bin, Active = true, UseAtKiosk = true });

        _db.SaveChanges();

        // --- System settings: Demo mode, 1 kiosk, light mode, Bin Inventory on ---
        var setup = _db.AppSetup.First();
        setup.DemoMode = true;
        setup.KioskCount = 1;
        setup.KioskDarkMode = false;
        setup.UseBinInventory = true;
        setup.PromptKioskBinOnInbound = true;

        var rng = new Random(7);
        var now = DateTime.UtcNow;
        int ticketNum = 1000;
        int harvestLoads = 0, saleLoads = 0;

        // Harvest windows by month/day (any year in the trailing 12 months)
        static bool InWindow(DateTime d, int fromMonth, int fromDay, int toMonth, int toDay)
        {
            var from = new DateTime(d.Year, fromMonth, fromDay);
            var to = new DateTime(d.Year, toMonth, toDay);
            return d.Date >= from && d.Date <= to;
        }

        var binsIn = binCommodity.ToDictionary(b => b.Bin, _ => 0L);   // harvested lbs per bin
        var binBalance = binCommodity.ToDictionary(b => b.Bin, _ => 0L);

        string PickBin(string commodity)
        {
            var candidates = binCommodity.Where(b => b.Commodity == commodity).Select(b => b.Bin).ToList();
            // Fill bins in order-ish: prefer the least-full so loads spread out.
            return candidates.OrderBy(b => binBalance[b]).ThenBy(_ => rng.Next()).First();
        }

        Transaction NewLoad(DateTime dateIn, string commodity, int minutesOnSite)
        {
            ticketNum++;
            var (tid, carrier) = truckData[rng.Next(truckData.Length)];
            return new Transaction
            {
                Ticket = ticketNum.ToString(),
                DateIn = dateIn,
                DateOut = dateIn.AddMinutes(minutesOnSite),
                Commodity = commodity,
                Carrier = carrier,
                TruckId = tid,
                Void = false
            };
        }

        // --- Harvest: loads INTO the bins (truck in heavy, out light) ---
        var startDate = now.AddYears(-1).Date;
        for (var date = startDate; date <= now.Date; date = date.AddDays(1))
        {
            if (date.DayOfWeek == DayOfWeek.Sunday) continue;

            string? commodity = null;
            if (InWindow(date, 7, 1, 7, 15)) commodity = "Wheat";
            else if (InWindow(date, 9, 20, 10, 15)) commodity = InWindow(date, 10, 5, 10, 15) && rng.NextDouble() < 0.4 ? "Corn" : "Soybeans";
            else if (InWindow(date, 10, 5, 11, 15)) commodity = "Corn";
            if (commodity == null) continue;

            int loadsToday = rng.Next(6, 13);
            for (int i = 0; i < loadsToday; i++)
            {
                var tare = rng.Next(28000, 34001);
                var net = rng.Next(38000, 52001);
                var bin = PickBin(commodity);

                var t = NewLoad(date.AddHours(rng.Next(8, 19)).AddMinutes(rng.Next(0, 60)), commodity, rng.Next(6, 18));
                t.InWeight = tare + net;   // arrives loaded from the field
                t.OutWeight = tare;        // leaves empty
                t.Location = fieldNames[rng.Next(fieldNames.Length)];
                t.Bin = bin;
                if (rng.NextDouble() < 0.25)
                    t.Notes = $"Moisture {Math.Round(13 + rng.NextDouble() * 6, 1)}%";
                _db.Transactions.Add(t);

                binsIn[bin] += net;
                binBalance[bin] += net;
                harvestLoads++;
            }
        }

        // --- Post-harvest true-up per bin: drying shrink of ~0.5–1.5% ---
        foreach (var (bin, commodity) in binCommodity)
        {
            if (binsIn[bin] == 0) continue;
            var shrink = -(int)(binsIn[bin] * (0.005 + rng.NextDouble() * 0.01));
            _db.BinAdjustments.Add(new BinAdjustment
            {
                Bin = bin,
                Commodity = commodity,
                AmountLbs = shrink,
                Date = now.AddDays(-rng.Next(30, 120)),
                Note = "True-up to measured inventory (drying shrink)"
            });
            binBalance[bin] += shrink;
        }

        // --- Sales: loads OUT of the bins (truck in light, out heavy) ---
        for (var date = startDate.AddDays(30); date <= now.Date; date = date.AddDays(rng.Next(3, 8)))
        {
            int loadsToday = rng.Next(1, 4);
            for (int i = 0; i < loadsToday; i++)
            {
                var tare = rng.Next(28000, 34001);
                var net = rng.Next(40000, 52001);
                // Only sell out of a bin that can cover the load with margin.
                var candidates = binCommodity.Where(b => binBalance[b.Bin] > net + 60000).ToList();
                if (candidates.Count == 0) continue;
                var (bin, commodity) = candidates[rng.Next(candidates.Count)];

                var t = NewLoad(date.AddHours(rng.Next(8, 17)).AddMinutes(rng.Next(0, 60)), commodity, rng.Next(10, 25));
                t.InWeight = tare;         // arrives empty
                t.OutWeight = tare + net;  // leaves loaded for the elevator
                t.Customer = buyerNames[rng.Next(buyerNames.Length)];
                t.Destination = elevatorNames[rng.Next(elevatorNames.Length)];
                t.Bin = bin;
                _db.Transactions.Add(t);

                binBalance[bin] -= net;
                saleLoads++;
            }
        }

        // --- A few trucks currently in the yard (open harvest tickets) ---
        for (int i = 0; i < 4; i++)
        {
            ticketNum++;
            var (tid, carrier) = truckData[rng.Next(truckData.Length)];
            var (bin, commodity) = binCommodity[rng.Next(binCommodity.Length)];
            _db.Transactions.Add(new Transaction
            {
                Ticket = ticketNum.ToString(),
                DateIn = now.AddMinutes(-rng.Next(10, 180)),
                InWeight = rng.Next(66000, 86001),
                Commodity = commodity,
                Carrier = carrier,
                TruckId = tid,
                Location = fieldNames[rng.Next(fieldNames.Length)],
                Bin = bin,
                Void = false
            });
        }

        ticketNum++;
        setup.TicketNumber = ticketNum;
        _db.SaveChanges();
        _setupCache.Invalidate();

        TempData["Message"] = $"Farming sample loaded: {harvestLoads} harvest loads into 6 bins, {saleLoads} sale loads out, "
            + "shrinkage true-ups, and 4 trucks in yard. Bin Inventory and demo mode enabled with 1 kiosk (light).";
        return RedirectToAction("Index");
    }

    [HttpGet("api/setup/icon")]
    public IActionResult GetIcon()
    {
        var setup = _setupCache.Get();
        if (setup.Icon != null && setup.IconContentType != null)
            return File(setup.Icon, setup.IconContentType);

        // Fall back to default SVG
        var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "default-icon.svg");
        return PhysicalFile(defaultPath, "image/svg+xml");
    }

    // Camera settings endpoint moved to CameraController

    /// <summary>
    /// Returns the current system settings (excludes sensitive data like icon bytes).
    /// </summary>
    [HttpGet("api/setup/settings")]
    public IActionResult GetSettings()
    {
        var setup = _setupCache.Get();
        var version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
        return Json(new
        {
            version,
            company = new
            {
                header1 = setup.Header1,
                header2 = setup.Header2,
                header3 = setup.Header3,
                header4 = setup.Header4
            },
            ticket = new
            {
                nextTicketNumber = setup.TicketNumber,
                ticketsPerPage = setup.TicketsPerPage
            },
            system = new
            {
                demoMode = setup.DemoMode,
                theme = setup.Theme,
                hasCustomIcon = setup.Icon != null,
                recallLastValues = setup.RecallLastValues,
                useBinInventory = setup.UseBinInventory,
                binRequired = setup.BinRequired
            },
            kiosk = new
            {
                kioskCount = setup.KioskCount,
                kioskDarkMode = setup.KioskDarkMode,
                promptCommodityOnInbound = setup.PromptKioskCommodityOnInbound,
                promptCommodityOnOutbound = setup.PromptKioskCommodityOnOutbound,
                allowSkipCommodity = setup.AllowSkipCommodity,
                promptCustomerOnInbound = setup.PromptKioskCustomerOnInbound,
                promptCustomerOnOutbound = setup.PromptKioskCustomerOnOutbound,
                allowSkipCustomer = setup.AllowSkipCustomer,
                promptCarrier = setup.PromptKioskCarrier,
                allowSkipCarrier = setup.AllowSkipCarrier,
                promptTruckId = setup.PromptKioskTruckId,
                allowSkipTruckId = setup.AllowSkipTruckId,
                promptLocationOnInbound = setup.PromptKioskLocationOnInbound,
                promptLocationOnOutbound = setup.PromptKioskLocationOnOutbound,
                allowSkipLocation = setup.AllowSkipLocation,
                promptDestinationOnInbound = setup.PromptKioskDestinationOnInbound,
                promptDestinationOnOutbound = setup.PromptKioskDestinationOnOutbound,
                allowSkipDestination = setup.AllowSkipDestination,
                promptBinOnInbound = setup.PromptKioskBinOnInbound,
                promptBinOnOutbound = setup.PromptKioskBinOnOutbound,
                allowSkipBin = setup.AllowSkipBin
            },
            security = new
            {
                useLogin = setup.UseLogin
            }
        });
    }
}
