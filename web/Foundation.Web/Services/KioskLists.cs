using Foundation.Web.Data;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Builds the master-data payload the unattended weighing UIs prompt from.
/// Shared by the kiosk (touchscreen at the scale) and the mobile page so the
/// two can't drift apart — a change to what the kiosk asks for is automatically
/// a change to what the phone asks for.
/// </summary>
public static class KioskLists
{
    /// <param name="scaleId">Site scale the device is mapped to. Commodities and
    /// bins limited to a location (Edit Tables) only appear when the device's
    /// scale is at that location.</param>
    public static object Build(ScaleDbContext db, AppSetup setup, int? scaleId)
    {
        var siteId = scaleId.HasValue ? db.Scales.Find(scaleId.Value)?.SiteId : null;

        var commodities = db.Commodities
            .Where(c => c.Active && c.UseAtKiosk).ForSite(siteId)
            .OrderBy(c => c.CommodityName)
            .Select(c => c.CommodityName)
            .ToList();

        var customers = db.Customers
            .Where(c => c.Active && c.UseAtKiosk)
            .OrderBy(c => c.CustomerName)
            .Select(c => c.CustomerName)
            .ToList();

        // Carrier prompt: only entries explicitly marked as Carriers in master
        // data. Customers who also haul can be added to Carriers via the "Add to
        // Carriers" button on the MasterData Customers grid.
        var carriers = db.Carriers
            .Where(c => c.Active && c.UseAtKiosk)
            .OrderBy(c => c.CarrierName)
            .Select(c => c.CarrierName)
            .ToList();

        var locations = db.Locations
            .Where(l => l.Active && l.UseAtKiosk)
            .OrderBy(l => l.LocationName)
            .Select(l => l.LocationName)
            .ToList();

        var destinations = db.Destinations
            .Where(d => d.Active && d.UseAtKiosk)
            .OrderBy(d => d.DestinationName)
            .Select(d => d.DestinationName)
            .ToList();

        // Bin prompt only exists while Bin Inventory is enabled — return an
        // empty list when off so a stale page auto-skips the prompt.
        var bins = setup.UseBinInventory
            ? db.Bins
                .Where(b => b.Active && b.UseAtKiosk).ForSite(siteId)
                .OrderBy(b => b.BinName)
                .Select(b => b.BinName)
                .ToList()
            : new List<string>();

        // Lock Bin to Commodity: bin → its current contents, so the bin prompt
        // can filter to bins that match the chosen commodity and auto-set a
        // skipped commodity from the chosen bin.
        var binCommodities = setup.UseBinInventory && setup.BinCommodityLock
            ? BinInventory.HeldCommodities(db)
            : new Dictionary<string, string>();

        // Kiosk-enabled custom fields. Only constrained inputs prompt here:
        // numeric fields and list-backed text fields (free text is filtered out
        // even if the flag was somehow set). Cascading sub-fields also carry
        // their parent name + per-parent choice map so the prompt can filter by
        // the answer already collected in this flow.
        var eligibleFields = db.CustomFields
            .Where(f => f.Active && f.PromptAtKiosk)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToList()
            .Where(f => f.IsKioskEligible())
            .ToList();

        var dependentIds = eligibleFields.Where(f => f.ParentField != null).Select(f => f.Id).ToList();
        var valueMaps = db.CustomFieldListValues
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

        return new { commodities, customers, carriers, locations, destinations, bins, binCommodities, customFields };
    }

    /// <summary>
    /// Server-side backstop for prompt-collected custom field values. The kiosk
    /// and mobile UIs already constrain input (dropdown choices, numeric keypad
    /// with min/max/precision), so an invalid value here means a stale page or a
    /// hand-crafted request — it is silently dropped rather than failing the
    /// weigh-in, since a truck is standing on the scale. Caller SaveChanges().
    /// </summary>
    /// <returns>The custom-field ids actually written, so a card can fill in the
    /// rest without duplicating what the driver just answered.</returns>
    public static HashSet<int> SaveCustomFields(ScaleDbContext db, string ticket, Dictionary<string, string>? values)
    {
        var written = new HashSet<int>();
        if (values == null || values.Count == 0) return written;

        var fields = db.CustomFields
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

            db.TransactionCustomValues.Add(new TransactionCustomValue
            {
                Ticket = ticket,
                CustomFieldId = f.Id,
                Value = raw
            });
            written.Add(f.Id);
        }

        return written;
    }
}
