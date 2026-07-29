using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Foundation.Web.Data;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

public class MasterDataController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;

    public MasterDataController(ScaleDbContext db, AppSetupCache setupCache)
    {
        _db = db;
        _setupCache = setupCache;
    }

    public IActionResult Index()
    {
        var setup = _setupCache.Get();
        ViewBag.KioskCount = setup.KioskCount;
        ViewBag.UseQuickBooks = setup.UseQuickBooks;
        ViewBag.UseRetainedTare = setup.UseRetainedTare;
        // Hidden standard fields lose their Edit Tables tab too (Setup → Fields).
        ViewBag.Setup = setup;
        // Active dropdown-backed custom fields each get their own edit tab.
        // Sub-fields (ParentField set) get a tab even while empty — their
        // choices are created here, per parent value.
        ViewBag.CustomFieldLists = _db.CustomFields
            .Where(f => f.Active && f.FieldType == "Text"
                        && ((f.ListValues != null && f.ListValues != "") || f.ParentField != null))
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToList();
        // For resolving "cf_{id}" parent references to display names.
        ViewBag.CustomFieldNames = _db.CustomFields.ToDictionary(f => f.Id, f => f.Name);
        return View();
    }

    // ---- Customers ----
    [HttpGet("api/masterdata/customers")]
    public IActionResult GetCustomers()
    {
        var carrierNames = _db.Carriers.Select(c => c.CarrierName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customers = _db.Customers
            .OrderBy(c => c.CustomerName)
            .ToList()
            .Select(c => new
            {
                c.Id,
                c.CustomerName,
                c.Active,
                c.UseAtKiosk,
                IsCarrier = carrierNames.Contains(c.CustomerName)
            });
        return Json(customers);
    }

    /// <summary>
    /// Add a customer's name to the Carriers table so the kiosk's Carrier
    /// prompt picks them up. No-op if the carrier already exists.
    /// </summary>
    [HttpPost("api/masterdata/customers/{id:int}/promote-to-carrier")]
    public IActionResult PromoteCustomerToCarrier(int id)
    {
        var customer = _db.Customers.Find(id);
        if (customer == null) return NotFound();
        if (!_db.Carriers.Any(c => c.CarrierName == customer.CustomerName))
        {
            _db.Carriers.Add(new Carrier
            {
                CarrierName = customer.CustomerName,
                Active = true,
                UseAtKiosk = customer.UseAtKiosk
            });
            _db.SaveChanges();
        }
        return Ok(new { customerId = id, customerName = customer.CustomerName });
    }

    [HttpPost("api/masterdata/customers")]
    public IActionResult AddCustomer([FromBody] Customer customer)
    {
        _db.Customers.Add(customer);
        _db.SaveChanges();
        return Json(customer);
    }

    [HttpPut("api/masterdata/customers")]
    public IActionResult UpdateCustomer([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Customers.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("customerName", out var name)) existing.CustomerName = name.GetString()!;
        if (body.TryGetProperty("active", out var active))
        {
            existing.Active = active.GetBoolean();
            if (!existing.Active) existing.UseAtKiosk = false;
        }
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/customers/{id:int}")]
    public IActionResult DeleteCustomer(int id)
    {
        var entity = _db.Customers.Find(id);
        if (entity == null) return NotFound();
        _db.Customers.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ---- Customers not already carriers (for carrier name suggestions) ----
    [HttpGet("api/masterdata/customers-not-carriers")]
    public IActionResult GetCustomersNotCarriers()
    {
        var carrierNames = _db.Carriers.Select(c => c.CarrierName).ToHashSet();
        var customers = _db.Customers
            .Where(c => c.Active && !carrierNames.Contains(c.CustomerName))
            .OrderBy(c => c.CustomerName)
            .Select(c => c.CustomerName)
            .ToList();
        return Json(customers);
    }

    // ---- Carriers ----
    [HttpGet("api/masterdata/carriers")]
    public IActionResult GetCarriers()
    {
        // Group trucks by carrier name once, then project carriers with their
        // truck count so the MasterData grid can highlight zero-truck carriers.
        var truckCountsByCarrier = _db.Trucks
            .GroupBy(t => t.CarrierName)
            .Select(g => new { Carrier = g.Key, Count = g.Count() })
            .ToDictionary(x => x.Carrier, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var carriers = _db.Carriers
            .OrderBy(c => c.CarrierName)
            .ToList()
            .Select(c => new
            {
                c.Id,
                c.CarrierName,
                c.Active,
                c.UseAtKiosk,
                TruckCount = truckCountsByCarrier.TryGetValue(c.CarrierName, out var n) ? n : 0
            });
        return Json(carriers);
    }

    [HttpPost("api/masterdata/carriers")]
    public IActionResult AddCarrier([FromBody] Carrier carrier)
    {
        _db.Carriers.Add(carrier);
        _db.SaveChanges();
        return Json(carrier);
    }

    [HttpPut("api/masterdata/carriers")]
    public IActionResult UpdateCarrier([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Carriers.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("carrierName", out var name)) existing.CarrierName = name.GetString()!;
        if (body.TryGetProperty("active", out var active))
        {
            existing.Active = active.GetBoolean();
            if (!existing.Active) existing.UseAtKiosk = false;
        }
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/carriers/{id:int}")]
    public IActionResult DeleteCarrier(int id)
    {
        var entity = _db.Carriers.Find(id);
        if (entity == null) return NotFound();
        _db.Carriers.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ---- Locations ----
    [HttpGet("api/masterdata/locations")]
    public IActionResult GetLocations()
    {
        return Json(_db.Locations.OrderBy(l => l.LocationName).ToList());
    }

    [HttpPost("api/masterdata/locations")]
    public IActionResult AddLocation([FromBody] Location location)
    {
        _db.Locations.Add(location);
        _db.SaveChanges();
        return Json(location);
    }

    [HttpPut("api/masterdata/locations")]
    public IActionResult UpdateLocation([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Locations.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("locationName", out var name)) existing.LocationName = name.GetString()!;
        if (body.TryGetProperty("active", out var active))
        {
            existing.Active = active.GetBoolean();
            if (!existing.Active) existing.UseAtKiosk = false;
        }
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/locations/{id:int}")]
    public IActionResult DeleteLocation(int id)
    {
        var entity = _db.Locations.Find(id);
        if (entity == null) return NotFound();
        _db.Locations.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ---- Destinations ----
    [HttpGet("api/masterdata/destinations")]
    public IActionResult GetDestinations()
    {
        return Json(_db.Destinations.OrderBy(d => d.DestinationName).ToList());
    }

    [HttpPost("api/masterdata/destinations")]
    public IActionResult AddDestination([FromBody] Destination destination)
    {
        _db.Destinations.Add(destination);
        _db.SaveChanges();
        return Json(destination);
    }

    [HttpPut("api/masterdata/destinations")]
    public IActionResult UpdateDestination([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Destinations.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("destinationName", out var name)) existing.DestinationName = name.GetString()!;
        if (body.TryGetProperty("active", out var active))
        {
            existing.Active = active.GetBoolean();
            if (!existing.Active) existing.UseAtKiosk = false;
        }
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/destinations/{id:int}")]
    public IActionResult DeleteDestination(int id)
    {
        var entity = _db.Destinations.Find(id);
        if (entity == null) return NotFound();
        _db.Destinations.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ---- Bins (Bin Inventory feature) ----
    [HttpGet("api/masterdata/bins")]
    public IActionResult GetBins()
    {
        return Json(_db.Bins.OrderBy(b => b.BinName).ToList());
    }

    [HttpPost("api/masterdata/bins")]
    public IActionResult AddBin([FromBody] Bin bin)
    {
        _db.Bins.Add(bin);
        _db.SaveChanges();
        return Json(bin);
    }

    [HttpPut("api/masterdata/bins")]
    public IActionResult UpdateBin([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Bins.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("binName", out var name)) existing.BinName = name.GetString()!;
        if (body.TryGetProperty("active", out var active))
        {
            existing.Active = active.GetBoolean();
            if (!existing.Active) existing.UseAtKiosk = false;
        }
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        if (body.TryGetProperty("siteId", out var site))
            existing.SiteId = site.ValueKind == JsonValueKind.Number ? site.GetInt32() : null;
        // Assigned commodity only changes while the bin is at zero (or to
        // match what it already holds) — same rule as the Bin Report's Set
        // Commodity action.
        if (body.TryGetProperty("commodity", out var commodityProp))
        {
            var commodity = commodityProp.ValueKind == JsonValueKind.String ? commodityProp.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(commodity)) commodity = null;
            if (!string.Equals(commodity, existing.Commodity, StringComparison.OrdinalIgnoreCase))
            {
                if (BinInventory.ValidateAssignment(_db, existing.BinName, commodity) is { } err)
                    return BadRequest(new { message = err });
                existing.Commodity = commodity;
            }
        }
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/bins/{id:int}")]
    public IActionResult DeleteBin(int id)
    {
        var entity = _db.Bins.Find(id);
        if (entity == null) return NotFound();
        _db.Bins.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ---- Trucks ----
    [HttpGet("api/masterdata/trucks")]
    public IActionResult GetTrucks()
    {
        return Json(_db.Trucks
            .OrderBy(t => t.CarrierName).ThenBy(t => t.TruckId)
            .ToList()
            .Select(ProjectTruck));
    }

    [HttpGet("api/masterdata/trucks/{carrierName}")]
    public IActionResult GetTrucksByCarrier(string carrierName)
    {
        var trucks = _db.Trucks
            .Where(t => t.CarrierName == carrierName)
            .OrderBy(t => t.TruckId)
            .ToList()
            .Select(ProjectTruck)
            .ToList();
        return Json(trucks);
    }

    /// <summary>
    /// Stamp Kind=Utc on RetainedTareUpdated so the JSON output carries the
    /// 'Z' suffix and the DevExtreme datetime column converts to the user's
    /// local timezone instead of rendering the raw UTC value.
    /// </summary>
    private static object ProjectTruck(Truck t) => new
    {
        t.Id,
        t.TruckId,
        t.CarrierName,
        t.Description,
        t.Notes,
        t.UseAtKiosk,
        t.RetainedTare,
        RetainedTareUpdated = t.RetainedTareUpdated.AsUtc()
    };

    [HttpPost("api/masterdata/trucks")]
    public IActionResult AddTruck([FromBody] Truck truck)
    {
        var existing = _db.Trucks.FirstOrDefault(t => t.TruckId == truck.TruckId && t.CarrierName == truck.CarrierName);
        if (existing != null)
            return Json(existing);

        // Auto-create carrier if it doesn't exist (customer used as carrier)
        if (!_db.Carriers.Any(c => c.CarrierName == truck.CarrierName))
        {
            _db.Carriers.Add(new Carrier { CarrierName = truck.CarrierName, Active = true });
        }

        _db.Trucks.Add(truck);
        _db.SaveChanges();
        return Json(truck);
    }

    [HttpPut("api/masterdata/trucks")]
    public IActionResult UpdateTruck([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Trucks.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("truckId", out var truckId)) existing.TruckId = truckId.GetString()!;
        if (body.TryGetProperty("description", out var desc)) existing.Description = desc.ValueKind == JsonValueKind.Null ? null : desc.GetString();
        if (body.TryGetProperty("notes", out var notes)) existing.Notes = notes.ValueKind == JsonValueKind.Null ? null : notes.GetString();
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        // Inline edit of the retained tare — null/empty clears it; any other
        // value updates and stamps the timestamp so the "Tare Updated" column
        // reflects the change immediately.
        if (body.TryGetProperty("retainedTare", out var tare))
        {
            int? newTare = tare.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number => tare.GetInt32(),
                JsonValueKind.String => int.TryParse(tare.GetString(), out var parsed) ? parsed : (int?)null,
                _ => null
            };
            if (newTare != existing.RetainedTare)
            {
                existing.RetainedTare = newTare;
                existing.RetainedTareUpdated = newTare.HasValue ? DateTime.UtcNow : (DateTime?)null;
            }
        }
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/trucks/{id:int}")]
    public IActionResult DeleteTruck(int id)
    {
        var entity = _db.Trucks.Find(id);
        if (entity == null) return NotFound();
        _db.Trucks.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ---- Bulk-set Active / UseAtKiosk for an entire table (header toggle) ----

    public class BulkSetRequest
    {
        public string Field { get; set; } = "";
        public bool Value { get; set; }
    }

    [HttpPost("api/masterdata/{table}/bulk-set")]
    public IActionResult BulkSet(string table, [FromBody] BulkSetRequest req)
    {
        if (req == null) return BadRequest();
        var field = (req.Field ?? "").ToLowerInvariant();
        if (field != "active" && field != "useatkiosk")
            return BadRequest(new { message = $"Unknown field '{req.Field}'" });

        int count = 0;
        switch ((table ?? "").ToLowerInvariant())
        {
            case "customers":
                foreach (var x in _db.Customers)
                {
                    if (field == "active") { x.Active = req.Value; if (!req.Value) x.UseAtKiosk = false; }
                    else { x.UseAtKiosk = req.Value && x.Active; }
                    count++;
                }
                break;
            case "carriers":
                foreach (var x in _db.Carriers)
                {
                    if (field == "active") { x.Active = req.Value; if (!req.Value) x.UseAtKiosk = false; }
                    else { x.UseAtKiosk = req.Value && x.Active; }
                    count++;
                }
                break;
            case "locations":
                foreach (var x in _db.Locations)
                {
                    if (field == "active") { x.Active = req.Value; if (!req.Value) x.UseAtKiosk = false; }
                    else { x.UseAtKiosk = req.Value && x.Active; }
                    count++;
                }
                break;
            case "destinations":
                foreach (var x in _db.Destinations)
                {
                    if (field == "active") { x.Active = req.Value; if (!req.Value) x.UseAtKiosk = false; }
                    else { x.UseAtKiosk = req.Value && x.Active; }
                    count++;
                }
                break;
            case "bins":
                foreach (var x in _db.Bins)
                {
                    if (field == "active") { x.Active = req.Value; if (!req.Value) x.UseAtKiosk = false; }
                    else { x.UseAtKiosk = req.Value && x.Active; }
                    count++;
                }
                break;
            case "commodities":
                foreach (var x in _db.Commodities)
                {
                    if (field == "active") { x.Active = req.Value; if (!req.Value) x.UseAtKiosk = false; }
                    else { x.UseAtKiosk = req.Value && x.Active; }
                    count++;
                }
                break;
            default:
                return NotFound(new { message = $"Unknown table '{table}'" });
        }

        _db.SaveChanges();
        return Ok(new { count });
    }

    // ---- Sync from QuickBooks ----

    public class SyncRequest
    {
        public List<string> Names { get; set; } = new();
    }

    [HttpPost("api/masterdata/sync/customers")]
    public IActionResult SyncCustomers([FromBody] SyncRequest request)
    {
        if (request.Names == null || request.Names.Count == 0)
            return BadRequest(new { error = "Names list is required." });

        var qbNames = new HashSet<string>(request.Names, StringComparer.OrdinalIgnoreCase);
        var existing = _db.Customers.ToList();
        var existingByName = existing.ToDictionary(c => c.CustomerName, c => c, StringComparer.OrdinalIgnoreCase);

        int added = 0, reactivated = 0, deactivated = 0, unchanged = 0;

        // Existing records keep their Active and UseAtKiosk flags — even when
        // QB lists them. Operators who marked a customer inactive in Foundation
        // want that choice respected on every sync. New customers default to
        // Active=true, UseAtKiosk=true.
        foreach (var name in qbNames)
        {
            if (existingByName.TryGetValue(name, out _))
            {
                unchanged++;
            }
            else
            {
                _db.Customers.Add(new Customer
                {
                    CustomerName = name,
                    Active = true,
                    UseAtKiosk = true
                });
                added++;
            }
        }

        // Customers removed from QB are deactivated. Inactive rows must also
        // have UseAtKiosk=false (invariant: kiosk implies active).
        foreach (var customer in existing)
        {
            if (customer.Active && !qbNames.Contains(customer.CustomerName))
            {
                customer.Active = false;
                customer.UseAtKiosk = false;
                deactivated++;
            }
        }

        _db.SaveChanges();
        return Json(new { added, reactivated, deactivated, unchanged });
    }

    [HttpPost("api/masterdata/sync/commodities")]
    public IActionResult SyncCommodities([FromBody] SyncRequest request)
    {
        if (request.Names == null || request.Names.Count == 0)
            return BadRequest(new { error = "Names list is required." });

        var qbNames = new HashSet<string>(request.Names, StringComparer.OrdinalIgnoreCase);
        var existing = _db.Commodities.ToList();
        var existingByName = existing.ToDictionary(c => c.CommodityName, c => c, StringComparer.OrdinalIgnoreCase);

        int added = 0, reactivated = 0, deactivated = 0, unchanged = 0;

        // Existing records keep their Active and UseAtKiosk flags. New
        // commodities default to Active=true, UseAtKiosk=true.
        foreach (var name in qbNames)
        {
            if (existingByName.TryGetValue(name, out _))
            {
                unchanged++;
            }
            else
            {
                _db.Commodities.Add(new Commodity
                {
                    CommodityName = name,
                    Active = true,
                    UseAtKiosk = true
                });
                added++;
            }
        }

        // Commodities removed from QB are deactivated; force UseAtKiosk=false
        // since kiosk implies active.
        foreach (var commodity in existing)
        {
            if (commodity.Active && !qbNames.Contains(commodity.CommodityName))
            {
                commodity.Active = false;
                commodity.UseAtKiosk = false;
                deactivated++;
            }
        }

        _db.SaveChanges();
        return Json(new { added, reactivated, deactivated, unchanged });
    }

    // ---- Commodities ----
    [HttpGet("api/masterdata/commodities")]
    public IActionResult GetCommodities()
    {
        return Json(_db.Commodities.OrderBy(c => c.CommodityName).ToList());
    }

    [HttpPost("api/masterdata/commodities")]
    public IActionResult AddCommodity([FromBody] Commodity commodity)
    {
        _db.Commodities.Add(commodity);
        _db.SaveChanges();
        return Json(commodity);
    }

    [HttpPut("api/masterdata/commodities")]
    public IActionResult UpdateCommodity([FromBody] JsonElement body)
    {
        var id = body.GetProperty("id").GetInt32();
        var existing = _db.Commodities.Find(id);
        if (existing == null) return NotFound();
        if (body.TryGetProperty("commodityName", out var name)) existing.CommodityName = name.GetString()!;
        if (body.TryGetProperty("active", out var active))
        {
            existing.Active = active.GetBoolean();
            if (!existing.Active) existing.UseAtKiosk = false;
        }
        if (body.TryGetProperty("useAtKiosk", out var kiosk)) existing.UseAtKiosk = kiosk.GetBoolean();
        if (body.TryGetProperty("siteId", out var site))
            existing.SiteId = site.ValueKind == JsonValueKind.Number ? site.GetInt32() : null;
        if (body.TryGetProperty("unitName", out var unitName))
            existing.UnitName = unitName.ValueKind == JsonValueKind.String ? unitName.GetString() : null;
        if (body.TryGetProperty("unitPounds", out var unitPounds))
            existing.UnitPounds = unitPounds.ValueKind == JsonValueKind.Number ? unitPounds.GetDouble() : null;
        _db.SaveChanges();
        return Json(existing);
    }

    [HttpDelete("api/masterdata/commodities/{id:int}")]
    public IActionResult DeleteCommodity(int id)
    {
        var entity = _db.Commodities.Find(id);
        if (entity == null) return NotFound();
        _db.Commodities.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }
}
