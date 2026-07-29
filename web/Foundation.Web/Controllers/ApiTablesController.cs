using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Foundation.Web.Data;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

/// <summary>
/// Developer-facing table API (see Swagger, Setup → API):
///
///   GET    api/tables                 — every table with its columns + types,
///                                       so a client can build get/set JSON
///   GET    api/tables/{table}         — rows (transactions: ?startDate&amp;endDate)
///   POST   api/tables/{table}         — insert one row
///   PUT    api/tables/{table}/{id}    — update one row (transactions: id = ticket)
///   DELETE api/tables/{table}/{id}    — delete one row (master tables only)
///   PUT    api/tables/{table}         — custom-field list tables only:
///                                       replace the whole choice list
///
/// Standard tables: customers, carriers, trucks, commodities, locations,
/// destinations, bins, sites, transactions. Each active dropdown-backed
/// custom field is its own table named customfield_{id}. Column names in the
/// JSON match the camelCase names reported by GET api/tables.
/// </summary>
public class ApiTablesController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;

    public ApiTablesController(ScaleDbContext db, AppSetupCache setupCache)
    {
        _db = db;
        _setupCache = setupCache;
    }

    // ===== Schema =====

    private static object Col(string name, string type, bool required = false, int? maxLength = null,
        bool readOnly = false, bool key = false, string? description = null) =>
        new { name, type, required, maxLength, readOnly, key, description };

    [HttpGet("api/tables")]
    public IActionResult GetTables()
    {
        var setup = _setupCache.Get();
        var tables = new List<object>();

        object NameTable(string table, string label, string nameCol, bool hasSite, IEnumerable<object>? extraCols = null) => new
        {
            table,
            label,
            kind = "standard",
            operations = new[] { "get", "insert", "update", "delete" },
            columns = new List<object>
            {
                Col("id", "integer", readOnly: true, key: true),
                Col(nameCol, "string", required: true, maxLength: table == "sites" ? 100 : 50),
                Col("active", "boolean"),
                Col("useAtKiosk", "boolean", description: "Forced false while active is false")
            }.Concat(hasSite
                ? new[] { Col("siteId", "integer", description: "Optional location (sites.id); null = every location") }
                : Array.Empty<object>())
             .Concat(extraCols ?? Array.Empty<object>()).ToList()
        };

        tables.Add(NameTable("customers", "Customers", "customerName", hasSite: false));
        tables.Add(NameTable("carriers", "Carriers", "carrierName", hasSite: false));
        tables.Add(NameTable("locations", "Locations (ticket field)", "locationName", hasSite: false));
        tables.Add(NameTable("destinations", "Destinations", "destinationName", hasSite: false));
        tables.Add(NameTable("commodities", "Commodities", "commodityName", hasSite: true, extraCols: new[]
        {
            Col("unitName", "string", maxLength: 20, description: "Reporting unit label (Bushels, CWT, …)"),
            Col("unitPounds", "number", description: "Pounds per reporting unit (corn bushel 56, CWT 100)")
        }));
        if (setup.UseBinInventory)
            tables.Add(NameTable("bins", "Bins", "binName", hasSite: true, extraCols: new[]
            {
                Col("commodity", "string", maxLength: 50,
                    description: "Assigned commodity — only changeable while the bin's balance is zero")
            }));

        tables.Add(new
        {
            table = "trucks",
            label = "Trucks",
            kind = "standard",
            operations = new[] { "get", "insert", "update", "delete" },
            columns = new List<object>
            {
                Col("id", "integer", readOnly: true, key: true),
                Col("truckId", "string", required: true, maxLength: 50),
                Col("carrierName", "string", required: true, maxLength: 50, description: "Carrier is auto-created when missing"),
                Col("description", "string", maxLength: 200),
                Col("notes", "string", maxLength: 500),
                Col("useAtKiosk", "boolean"),
                Col("retainedTare", "integer", description: "Stored empty weight (lb); null = none"),
                Col("retainedTareUpdated", "datetime", readOnly: true)
            }
        });

        tables.Add(new
        {
            table = "sites",
            label = "Locations (physical sites)",
            kind = "standard",
            operations = new[] { "get", "insert", "update", "delete" },
            columns = new List<object>
            {
                Col("id", "integer", readOnly: true, key: true),
                Col("name", "string", required: true, maxLength: 100),
                Col("address", "string", maxLength: 200),
                Col("city", "string", maxLength: 50),
                Col("state", "string", maxLength: 20),
                Col("zip", "string", maxLength: 20),
                Col("phone", "string", maxLength: 30),
                Col("notes", "string", maxLength: 200),
                Col("sortOrder", "integer"),
                Col("active", "boolean")
            }
        });

        // Transactions: standard columns plus one cf_{id} column per active
        // custom field, typed from the field definition.
        var customFields = _db.CustomFields.Where(f => f.Active)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToList();

        var txnColumns = new List<object>
        {
            Col("ticket", "string", readOnly: true, key: true, maxLength: 20, description: "Assigned automatically on insert"),
            Col("void", "boolean"),
            Col("inWeight", "integer", required: true, description: "First weighment (lb)"),
            Col("outWeight", "integer", description: "Second weighment (lb); null = truck still in yard"),
            Col("dateIn", "datetime"),
            Col("dateOut", "datetime"),
            Col("customer", "string", maxLength: 50),
            Col("carrier", "string", maxLength: 50),
            Col("truckId", "string", maxLength: 50),
            Col("commodity", "string", maxLength: 50),
            Col("location", "string", maxLength: 50),
            Col("destination", "string", maxLength: 50),
            Col("bin", "string", maxLength: 50, description: setup.UseBinInventory ? null : "Bin Inventory is currently disabled"),
            Col("transferToBin", "string", maxLength: 50, description: "Destination bin of a weighed bin-to-bin transfer; null = ordinary ticket"),
            Col("notes", "string", maxLength: 500),
            Col("netWeight", "integer", readOnly: true),
            Col("sentToQuickBooks", "boolean", readOnly: true)
        };
        foreach (var f in customFields)
        {
            var type = f.FieldType switch { "Integer" => "integer", "Real" or "Formula" => "number", _ => "string" };
            txnColumns.Add(Col("cf_" + f.Id, type,
                required: f.Required && f.FieldType != "Formula",
                maxLength: 200,
                readOnly: f.FieldType == "Formula",
                description: $"Custom field \"{f.Name}\""
                    + (f.ParentField != null ? $" (sub-field of {f.ParentField})" : "")
                    + (f.FieldType == "Formula" ? $" — computed: {f.Formula}" : "")));
        }
        tables.Add(new
        {
            table = "transactions",
            label = "Tickets",
            kind = "standard",
            operations = new[] { "get", "insert", "update" },
            columns = txnColumns
        });

        // Each dropdown-backed custom field is a little choice table.
        foreach (var f in customFields.Where(f => f.FieldType == "Text" && (f.ListValues != null || f.ParentField != null)))
        {
            tables.Add(new
            {
                table = "customfield_" + f.Id,
                label = f.Name + " (dropdown choices)",
                kind = "customField",
                operations = new[] { "get", "replace" },
                columns = f.ParentField != null
                    ? new List<object>
                    {
                        Col("parentValue", "string", required: true, maxLength: 200,
                            description: $"A value of the parent field ({f.ParentField})"),
                        Col("value", "string", required: true, maxLength: 200)
                    }
                    : new List<object> { Col("value", "string", required: true, maxLength: 200) }
            });
        }

        return Json(tables);
    }

    // ===== JSON helpers =====

    private static string? Str(JsonElement b, string prop) =>
        b.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool? Bool(JsonElement b, string prop) =>
        b.TryGetProperty(prop, out var e)
            ? e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }
            : null;

    private static int? Int(JsonElement b, string prop) =>
        b.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private static double? Dbl(JsonElement b, string prop) =>
        b.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private static bool HasNull(JsonElement b, string prop) =>
        b.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Null;

    private static DateTime? Date(JsonElement b, string prop) =>
        b.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String
            && DateTime.TryParse(e.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private IActionResult Bad(string message) => BadRequest(new { message });

    private static bool IsCustomFieldTable(string table, out int fieldId)
    {
        fieldId = 0;
        return table.StartsWith("customfield_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(table["customfield_".Length..], out fieldId);
    }

    // ===== GET rows =====

    [HttpGet("api/tables/{table}")]
    public IActionResult GetRows(string table, DateTime? startDate = null, DateTime? endDate = null)
    {
        if (IsCustomFieldTable(table, out var fieldId))
        {
            var field = _db.CustomFields.Find(fieldId);
            if (field == null) return NotFound(new { message = "Custom field not found." });
            if (field.ParentField != null)
            {
                return Json(_db.CustomFieldListValues.Where(v => v.CustomFieldId == fieldId)
                    .OrderBy(v => v.ParentValue).ThenBy(v => v.SortOrder).ThenBy(v => v.Value)
                    .Select(v => new { v.ParentValue, v.Value }).ToList());
            }
            return Json(field.GetListValues().Select(v => new { value = v }).ToList());
        }

        switch (table.ToLowerInvariant())
        {
            case "customers": return Json(_db.Customers.OrderBy(x => x.CustomerName).ToList());
            case "carriers": return Json(_db.Carriers.OrderBy(x => x.CarrierName).ToList());
            case "locations": return Json(_db.Locations.OrderBy(x => x.LocationName).ToList());
            case "destinations": return Json(_db.Destinations.OrderBy(x => x.DestinationName).ToList());
            case "commodities": return Json(_db.Commodities.OrderBy(x => x.CommodityName).ToList());
            case "bins": return Json(_db.Bins.OrderBy(x => x.BinName).ToList());
            case "sites": return Json(_db.Sites.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToList());
            case "trucks":
                return Json(_db.Trucks.OrderBy(x => x.CarrierName).ThenBy(x => x.TruckId).ToList()
                    .Select(t => new { t.Id, t.TruckId, t.CarrierName, t.Description, t.Notes, t.UseAtKiosk, t.RetainedTare, RetainedTareUpdated = t.RetainedTareUpdated.AsUtc() }));
            case "transactions":
            {
                var localStart = startDate ?? DateTime.Today.AddDays(-30);
                var localEnd = (endDate ?? DateTime.Today).Date.AddDays(1);
                var start = AppTimeZone.ToUtc(localStart);
                var end = AppTimeZone.ToUtc(localEnd);

                var rows = _db.Transactions
                    .Where(t => t.DateIn >= start && t.DateIn < end)
                    .OrderByDescending(t => t.DateIn)
                    .ToList();
                var ids = rows.Select(t => t.Ticket).ToList();
                var cvs = _db.TransactionCustomValues.Where(v => ids.Contains(v.Ticket))
                    .AsEnumerable().GroupBy(v => v.Ticket)
                    .ToDictionary(g => g.Key, g => g.ToDictionary(v => "cf_" + v.CustomFieldId, v => v.Value));

                return Json(rows.Select(t => new Dictionary<string, object?>
                {
                    ["ticket"] = t.Ticket,
                    ["void"] = t.Void,
                    ["inWeight"] = t.InWeight,
                    ["outWeight"] = t.OutWeight,
                    ["dateIn"] = t.DateIn.AsUtc(),
                    ["dateOut"] = t.DateOut.AsUtc(),
                    ["customer"] = t.Customer,
                    ["carrier"] = t.Carrier,
                    ["truckId"] = t.TruckId,
                    ["commodity"] = t.Commodity,
                    ["location"] = t.Location,
                    ["destination"] = t.Destination,
                    ["bin"] = t.Bin,
                    ["transferToBin"] = t.TransferToBin,
                    ["notes"] = t.Notes,
                    ["netWeight"] = t.NetWeight,
                    ["sentToQuickBooks"] = t.SentToQuickBooks
                }.Concat(cvs.GetValueOrDefault(t.Ticket, new Dictionary<string, string?>())
                        .Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)));
            }
            default:
                return NotFound(new { message = $"Unknown table '{table}'. GET api/tables lists the available tables." });
        }
    }

    // ===== INSERT =====

    [HttpPost("api/tables/{table}")]
    public IActionResult Insert(string table, [FromBody] JsonElement body)
    {
        if (IsCustomFieldTable(table, out _))
            return Bad("Custom-field choice tables are replaced whole: PUT api/tables/" + table + " with the full list.");

        try
        {
            switch (table.ToLowerInvariant())
            {
                case "customers":
                {
                    var name = Str(body, "customerName");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("customerName is required.");
                    var row = new Customer { CustomerName = name.Trim(), Active = Bool(body, "active") ?? true, UseAtKiosk = Bool(body, "useAtKiosk") ?? true };
                    if (!row.Active) row.UseAtKiosk = false;
                    _db.Customers.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "carriers":
                {
                    var name = Str(body, "carrierName");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("carrierName is required.");
                    var row = new Carrier { CarrierName = name.Trim(), Active = Bool(body, "active") ?? true, UseAtKiosk = Bool(body, "useAtKiosk") ?? true };
                    if (!row.Active) row.UseAtKiosk = false;
                    _db.Carriers.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "locations":
                {
                    var name = Str(body, "locationName");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("locationName is required.");
                    var row = new Location { LocationName = name.Trim(), Active = Bool(body, "active") ?? true, UseAtKiosk = Bool(body, "useAtKiosk") ?? true };
                    if (!row.Active) row.UseAtKiosk = false;
                    _db.Locations.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "destinations":
                {
                    var name = Str(body, "destinationName");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("destinationName is required.");
                    var row = new Destination { DestinationName = name.Trim(), Active = Bool(body, "active") ?? true, UseAtKiosk = Bool(body, "useAtKiosk") ?? true };
                    if (!row.Active) row.UseAtKiosk = false;
                    _db.Destinations.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "commodities":
                {
                    var name = Str(body, "commodityName");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("commodityName is required.");
                    var row = new Commodity { CommodityName = name.Trim(), Active = Bool(body, "active") ?? true, UseAtKiosk = Bool(body, "useAtKiosk") ?? true, SiteId = Int(body, "siteId"), UnitName = Str(body, "unitName"), UnitPounds = Dbl(body, "unitPounds") };
                    if (!row.Active) row.UseAtKiosk = false;
                    _db.Commodities.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "bins":
                {
                    var name = Str(body, "binName");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("binName is required.");
                    var row = new Bin { BinName = name.Trim(), Active = Bool(body, "active") ?? true, UseAtKiosk = Bool(body, "useAtKiosk") ?? true, SiteId = Int(body, "siteId"), Commodity = Str(body, "commodity")?.Trim() };
                    if (!row.Active) row.UseAtKiosk = false;
                    _db.Bins.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "sites":
                {
                    var name = Str(body, "name");
                    if (string.IsNullOrWhiteSpace(name)) return Bad("name is required.");
                    var row = new Site
                    {
                        Name = name.Trim(),
                        Address = Str(body, "address"), City = Str(body, "city"), State = Str(body, "state"),
                        Zip = Str(body, "zip"), Phone = Str(body, "phone"), Notes = Str(body, "notes"),
                        SortOrder = Int(body, "sortOrder") ?? 0, Active = Bool(body, "active") ?? true
                    };
                    _db.Sites.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "trucks":
                {
                    var truckId = Str(body, "truckId");
                    var carrier = Str(body, "carrierName");
                    if (string.IsNullOrWhiteSpace(truckId) || string.IsNullOrWhiteSpace(carrier))
                        return Bad("truckId and carrierName are required.");
                    if (!_db.Carriers.Any(c => c.CarrierName == carrier))
                        _db.Carriers.Add(new Carrier { CarrierName = carrier.Trim(), Active = true });
                    var row = new Truck
                    {
                        TruckId = truckId.Trim(), CarrierName = carrier.Trim(),
                        Description = Str(body, "description"), Notes = Str(body, "notes"),
                        UseAtKiosk = Bool(body, "useAtKiosk") ?? true,
                        RetainedTare = Int(body, "retainedTare"),
                        RetainedTareUpdated = Int(body, "retainedTare") != null ? DateTime.UtcNow : null
                    };
                    _db.Trucks.Add(row); _db.SaveChanges(); return Json(row);
                }
                case "transactions":
                {
                    var inWeight = Int(body, "inWeight");
                    if (inWeight == null) return Bad("inWeight is required.");

                    var setup = _db.AppSetup.First();
                    while (_db.Transactions.Any(t => t.Ticket == setup.TicketNumber.ToString()))
                        setup.TicketNumber++;

                    var t = new Transaction
                    {
                        Ticket = setup.TicketNumber.ToString(),
                        InWeight = inWeight.Value,
                        OutWeight = Int(body, "outWeight"),
                        DateIn = Date(body, "dateIn") ?? DateTime.UtcNow,
                        Customer = Str(body, "customer"), Carrier = Str(body, "carrier"),
                        TruckId = Str(body, "truckId"), Commodity = Str(body, "commodity"),
                        Location = Str(body, "location"), Destination = Str(body, "destination"),
                        Bin = Str(body, "bin"), TransferToBin = Str(body, "transferToBin"),
                        Notes = Str(body, "notes"),
                        Void = Bool(body, "void") ?? false,
                        ManualInbound = true // API-supplied weights aren't scale captures
                    };
                    if (t.OutWeight != null)
                    {
                        t.DateOut = Date(body, "dateOut") ?? DateTime.UtcNow;
                        t.ManualOutbound = true;
                    }
                    setup.TicketNumber++;
                    _db.Transactions.Add(t);
                    ApplyCustomFieldValues(t.Ticket, body);
                    _db.SaveChanges();
                    FormulaFields.RecomputeAndSave(_db, t);
                    _setupCache.Invalidate();
                    return Json(new { ticket = t.Ticket });
                }
                default:
                    return NotFound(new { message = $"Unknown table '{table}'." });
            }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Bad("Insert failed — a row with that name/key probably already exists.");
        }
    }

    // ===== UPDATE =====

    [HttpPut("api/tables/{table}/{id}")]
    public IActionResult Update(string table, string id, [FromBody] JsonElement body)
    {
        if (IsCustomFieldTable(table, out _))
            return Bad("Custom-field choice tables are replaced whole: PUT api/tables/" + table + " with the full list.");

        try
        {
            if (table.Equals("transactions", StringComparison.OrdinalIgnoreCase))
            {
                var t = _db.Transactions.Find(id);
                if (t == null) return NotFound(new { message = "Ticket not found." });

                if (Int(body, "inWeight") is { } iw) t.InWeight = iw;
                if (Int(body, "outWeight") is { } ow) t.OutWeight = ow;
                else if (HasNull(body, "outWeight")) { t.OutWeight = null; t.DateOut = null; }
                if (Date(body, "dateIn") is { } di) t.DateIn = di;
                if (Date(body, "dateOut") is { } dn) t.DateOut = dn;
                else if (t.OutWeight != null && t.DateOut == null) t.DateOut = DateTime.UtcNow;
                if (body.TryGetProperty("customer", out _)) t.Customer = Str(body, "customer");
                if (body.TryGetProperty("carrier", out _)) t.Carrier = Str(body, "carrier");
                if (body.TryGetProperty("truckId", out _)) t.TruckId = Str(body, "truckId");
                if (body.TryGetProperty("commodity", out _)) t.Commodity = Str(body, "commodity");
                if (body.TryGetProperty("location", out _)) t.Location = Str(body, "location");
                if (body.TryGetProperty("destination", out _)) t.Destination = Str(body, "destination");
                if (body.TryGetProperty("bin", out _)) t.Bin = Str(body, "bin");
                if (body.TryGetProperty("transferToBin", out _)) t.TransferToBin = Str(body, "transferToBin");
                if (body.TryGetProperty("notes", out _)) t.Notes = Str(body, "notes");
                if (Bool(body, "void") is { } v) t.Void = v;
                t.SentToQuickBooks = false; // edited tickets need re-sending
                ApplyCustomFieldValues(t.Ticket, body);
                _db.SaveChanges();
                FormulaFields.RecomputeAndSave(_db, t);
                return Json(new { success = true, ticket = t.Ticket });
            }

            if (!int.TryParse(id, out var rowId)) return Bad("id must be an integer for this table.");

            switch (table.ToLowerInvariant())
            {
                case "customers":
                {
                    var row = _db.Customers.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "customerName") is { } n) row.CustomerName = n.Trim();
                    ApplyActiveKiosk(body, v => row.Active = v, () => row.Active, v => row.UseAtKiosk = v);
                    _db.SaveChanges(); return Json(row);
                }
                case "carriers":
                {
                    var row = _db.Carriers.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "carrierName") is { } n) row.CarrierName = n.Trim();
                    ApplyActiveKiosk(body, v => row.Active = v, () => row.Active, v => row.UseAtKiosk = v);
                    _db.SaveChanges(); return Json(row);
                }
                case "locations":
                {
                    var row = _db.Locations.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "locationName") is { } n) row.LocationName = n.Trim();
                    ApplyActiveKiosk(body, v => row.Active = v, () => row.Active, v => row.UseAtKiosk = v);
                    _db.SaveChanges(); return Json(row);
                }
                case "destinations":
                {
                    var row = _db.Destinations.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "destinationName") is { } n) row.DestinationName = n.Trim();
                    ApplyActiveKiosk(body, v => row.Active = v, () => row.Active, v => row.UseAtKiosk = v);
                    _db.SaveChanges(); return Json(row);
                }
                case "commodities":
                {
                    var row = _db.Commodities.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "commodityName") is { } n) row.CommodityName = n.Trim();
                    ApplyActiveKiosk(body, v => row.Active = v, () => row.Active, v => row.UseAtKiosk = v);
                    if (Int(body, "siteId") is { } s) row.SiteId = s; else if (HasNull(body, "siteId")) row.SiteId = null;
                    if (Str(body, "unitName") is { } un) row.UnitName = un.Trim(); else if (HasNull(body, "unitName")) row.UnitName = null;
                    if (Dbl(body, "unitPounds") is { } up) row.UnitPounds = up; else if (HasNull(body, "unitPounds")) row.UnitPounds = null;
                    _db.SaveChanges(); return Json(row);
                }
                case "bins":
                {
                    var row = _db.Bins.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "binName") is { } n) row.BinName = n.Trim();
                    ApplyActiveKiosk(body, v => row.Active = v, () => row.Active, v => row.UseAtKiosk = v);
                    if (Int(body, "siteId") is { } s) row.SiteId = s; else if (HasNull(body, "siteId")) row.SiteId = null;
                    // Assigned commodity only changes while the bin is at zero
                    // (or to match what it already holds) — same rule as the
                    // Bin Report's Set Commodity action.
                    if (body.TryGetProperty("commodity", out _))
                    {
                        var commodity = Str(body, "commodity")?.Trim();
                        if (string.IsNullOrEmpty(commodity)) commodity = null;
                        if (!string.Equals(commodity, row.Commodity, StringComparison.OrdinalIgnoreCase))
                        {
                            if (BinInventory.ValidateAssignment(_db, row.BinName, commodity) is { } err)
                                return Bad(err);
                            row.Commodity = commodity;
                        }
                    }
                    _db.SaveChanges(); return Json(row);
                }
                case "sites":
                {
                    var row = _db.Sites.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "name") is { } n) row.Name = n.Trim();
                    if (body.TryGetProperty("address", out _)) row.Address = Str(body, "address");
                    if (body.TryGetProperty("city", out _)) row.City = Str(body, "city");
                    if (body.TryGetProperty("state", out _)) row.State = Str(body, "state");
                    if (body.TryGetProperty("zip", out _)) row.Zip = Str(body, "zip");
                    if (body.TryGetProperty("phone", out _)) row.Phone = Str(body, "phone");
                    if (body.TryGetProperty("notes", out _)) row.Notes = Str(body, "notes");
                    if (Int(body, "sortOrder") is { } so) row.SortOrder = so;
                    if (Bool(body, "active") is { } a) row.Active = a;
                    _db.SaveChanges(); return Json(row);
                }
                case "trucks":
                {
                    var row = _db.Trucks.Find(rowId); if (row == null) return NotFound();
                    if (Str(body, "truckId") is { } n) row.TruckId = n.Trim();
                    if (Str(body, "carrierName") is { } c) row.CarrierName = c.Trim();
                    if (body.TryGetProperty("description", out _)) row.Description = Str(body, "description");
                    if (body.TryGetProperty("notes", out _)) row.Notes = Str(body, "notes");
                    if (Bool(body, "useAtKiosk") is { } k) row.UseAtKiosk = k;
                    if (Int(body, "retainedTare") is { } rt) { row.RetainedTare = rt; row.RetainedTareUpdated = DateTime.UtcNow; }
                    else if (HasNull(body, "retainedTare")) { row.RetainedTare = null; row.RetainedTareUpdated = null; }
                    _db.SaveChanges(); return Json(row);
                }
                default:
                    return NotFound(new { message = $"Unknown table '{table}'." });
            }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Bad("Update failed — a row with that name/key probably already exists.");
        }
    }

    /// <summary>Kiosk implies active (the same invariant the UI enforces).</summary>
    private static void ApplyActiveKiosk(JsonElement body, Action<bool> setActive, Func<bool> getActive, Action<bool> setKiosk)
    {
        if (Bool(body, "active") is { } a)
        {
            setActive(a);
            if (!a) setKiosk(false);
        }
        if (Bool(body, "useAtKiosk") is { } k) setKiosk(k && getActive());
    }

    /// <summary>Upsert cf_{id} properties (or a nested customFields object)
    /// on a ticket. Values are stored as text, capped at 200 chars.</summary>
    private void ApplyCustomFieldValues(string ticket, JsonElement body)
    {
        // Formula fields are computed server-side — API-supplied values for
        // them are ignored rather than stored.
        var fields = _db.CustomFields.Where(f => f.Active && f.FieldType != "Formula").ToList();
        foreach (var f in fields)
        {
            string? raw = null;
            var found = false;
            if (body.TryGetProperty("cf_" + f.Id, out var direct) && direct.ValueKind != JsonValueKind.Undefined)
            {
                found = true;
                raw = direct.ValueKind switch
                {
                    JsonValueKind.String => direct.GetString(),
                    JsonValueKind.Number => direct.GetDouble().ToString("0.######"),
                    JsonValueKind.Null => null,
                    _ => null
                };
            }
            else if (body.TryGetProperty("customFields", out var nested) && nested.ValueKind == JsonValueKind.Object
                     && nested.TryGetProperty(f.Id.ToString(), out var nv))
            {
                found = true;
                raw = nv.ValueKind switch
                {
                    JsonValueKind.String => nv.GetString(),
                    JsonValueKind.Number => nv.GetDouble().ToString("0.######"),
                    _ => null
                };
            }
            if (!found) continue;

            raw = raw?.Trim();
            if (raw is { Length: > 200 }) raw = raw[..200];
            var existing = _db.TransactionCustomValues
                .FirstOrDefault(v => v.Ticket == ticket && v.CustomFieldId == f.Id);
            if (string.IsNullOrEmpty(raw))
            {
                if (existing != null) _db.TransactionCustomValues.Remove(existing);
            }
            else if (existing == null)
            {
                _db.TransactionCustomValues.Add(new TransactionCustomValue { Ticket = ticket, CustomFieldId = f.Id, Value = raw });
            }
            else
            {
                existing.Value = raw;
            }
        }
    }

    // ===== DELETE =====

    [HttpDelete("api/tables/{table}/{id:int}")]
    public IActionResult Delete(string table, int id)
    {
        if (IsCustomFieldTable(table, out _))
            return Bad("Custom-field choice tables are replaced whole: PUT api/tables/" + table + " with the full list.");

        switch (table.ToLowerInvariant())
        {
            case "customers": return DeleteRow(_db.Customers.Find(id));
            case "carriers": return DeleteRow(_db.Carriers.Find(id));
            case "locations": return DeleteRow(_db.Locations.Find(id));
            case "destinations": return DeleteRow(_db.Destinations.Find(id));
            case "commodities": return DeleteRow(_db.Commodities.Find(id));
            case "bins": return DeleteRow(_db.Bins.Find(id));
            case "trucks": return DeleteRow(_db.Trucks.Find(id));
            case "sites":
            {
                var site = _db.Sites.Find(id);
                if (site == null) return NotFound();
                foreach (var s in _db.Scales.Where(s => s.SiteId == id)) s.SiteId = null;
                foreach (var b in _db.Bins.Where(b => b.SiteId == id)) b.SiteId = null;
                foreach (var c in _db.Commodities.Where(c => c.SiteId == id)) c.SiteId = null;
                return DeleteRow(site);
            }
            case "transactions":
                return Bad("Tickets can't be deleted over the API — set void: true via PUT instead.");
            default:
                return NotFound(new { message = $"Unknown table '{table}'." });
        }
    }

    private IActionResult DeleteRow(object? entity)
    {
        if (entity == null) return NotFound();
        _db.Remove(entity);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    // ===== Custom-field choice lists: replace whole =====

    /// <summary>
    /// Replace a custom field's dropdown choices. Flat fields take
    /// ["A","B"] or [{"value":"A"}]; sub-fields take
    /// [{"parentValue":"Corn","value":"Yellow Dent"}, …].
    /// </summary>
    [HttpPut("api/tables/{table}")]
    public IActionResult ReplaceList(string table, [FromBody] JsonElement body)
    {
        if (!IsCustomFieldTable(table, out var fieldId))
            return Bad("Whole-table replace is only for customfield_{id} tables; use PUT api/tables/{table}/{id} for rows.");
        var field = _db.CustomFields.Find(fieldId);
        if (field == null) return NotFound(new { message = "Custom field not found." });
        if (field.FieldType != "Text") return Bad("Only text dropdown fields have choice lists.");
        if (body.ValueKind != JsonValueKind.Array) return Bad("Body must be a JSON array.");

        if (field.ParentField != null)
        {
            var rows = new List<(string Parent, string Value)>();
            foreach (var e in body.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return Bad("Sub-field choices need objects with parentValue and value.");
                var p = Str(e, "parentValue")?.Trim();
                var v = Str(e, "value")?.Trim();
                if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(v)) return Bad("parentValue and value are both required.");
                if (p.Length > 200 || v.Length > 200) return Bad("Values must be 200 characters or fewer.");
                if (!rows.Contains((p, v))) rows.Add((p, v));
            }

            _db.CustomFieldListValues.RemoveRange(_db.CustomFieldListValues.Where(v => v.CustomFieldId == fieldId));
            var order = 0;
            string? lastParent = null;
            foreach (var (p, v) in rows)
            {
                if (p != lastParent) { order = 0; lastParent = p; }
                _db.CustomFieldListValues.Add(new CustomFieldListValue
                {
                    CustomFieldId = fieldId,
                    ParentValue = p,
                    Value = v,
                    SortOrder = (order += 10)
                });
            }
            var union = rows.Select(r => r.Value).Distinct().ToList();
            field.ListValues = union.Count > 0 ? string.Join("\n", union) : null;
            if (!field.IsKioskEligible()) field.PromptAtKiosk = false;
            _db.SaveChanges();
            return Json(new { success = true, count = rows.Count });
        }

        var values = new List<string>();
        foreach (var e in body.EnumerateArray())
        {
            var v = e.ValueKind == JsonValueKind.String ? e.GetString()
                : e.ValueKind == JsonValueKind.Object ? Str(e, "value") : null;
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (v.Length > 200) return Bad("Values must be 200 characters or fewer.");
            if (!values.Contains(v)) values.Add(v);
        }
        var joined = string.Join("\n", values);
        if (joined.Length > 4000) return Bad("Dropdown list is too long (4000 characters max).");
        field.ListValues = values.Count > 0 ? joined : null;
        if (!field.IsKioskEligible()) field.PromptAtKiosk = false;
        _db.SaveChanges();
        return Json(new { success = true, count = values.Count });
    }
}
