using Foundation.Web.Data;
using Foundation.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Foundation.Web.Services;

/// <summary>
/// Loads completed, non-voided loads for an emailed report and hydrates them
/// into WeighRows (custom-field values + per-commodity reporting unit resolved).
///
/// Shares the Reports page's conventions: the range is filtered on DateOut in
/// the display timezone, and NetWeight is a computed property so the rows are
/// materialized before anything touches it.
/// </summary>
public static class WeighReportQuery
{
    /// <param name="startUtc">Inclusive lower bound on DateOut.</param>
    /// <param name="endUtc">Exclusive upper bound on DateOut.</param>
    public static List<WeighRow> Load(
        ScaleDbContext db,
        AppSetup setup,
        DateTime startUtc,
        DateTime endUtc,
        string? customerFilter = null,
        string? commodityFilter = null,
        int? siteId = null)
    {
        var query = db.Transactions
            .Where(t => t.DateOut != null && !t.Void && t.DateOut >= startUtc && t.DateOut < endUtc);

        var customers = EmailRecipients.SplitNames(customerFilter);
        if (customers.Count > 0)
            query = query.Where(t => t.Customer != null && customers.Contains(t.Customer));

        var commodities = EmailRecipients.SplitNames(commodityFilter);
        if (commodities.Count > 0)
            query = query.Where(t => t.Commodity != null && commodities.Contains(t.Commodity));

        var rows = query.OrderBy(t => t.DateOut).ToList();

        // Tickets record the scale by name, not the location, so the location
        // filter resolves through the Scales table. Prefer the outbound scale
        // (the weighment the report is keyed on) and fall back to the inbound.
        // Tickets with neither — pre-multi-scale history, or imports — can't be
        // attributed to a location and are excluded rather than guessed at.
        if (siteId.HasValue)
        {
            var siteScaleNames = db.Scales
                .Where(s => s.SiteId == siteId.Value)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            rows = rows.Where(t =>
            {
                var scale = !string.IsNullOrWhiteSpace(t.OutScale) ? t.OutScale : t.InScale;
                return scale != null && siteScaleNames.Contains(scale);
            }).ToList();
        }

        return Hydrate(db, setup, rows);
    }

    /// <summary>Attaches custom-field values and reporting units to already
    /// loaded transactions. Used by the per-load emails too, which pick their
    /// tickets a different way.</summary>
    public static List<WeighRow> Hydrate(ScaleDbContext db, AppSetup setup, List<Transaction> rows)
    {
        if (rows.Count == 0) return new List<WeighRow>();

        var tickets = rows.Select(t => t.Ticket).ToList();
        var customValues = db.TransactionCustomValues
            .Where(v => tickets.Contains(v.Ticket))
            .AsEnumerable()
            .GroupBy(v => v.Ticket)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(v => v.CustomFieldId, v => v.Value));

        var units = CommodityUnits(db);
        var fallback = DefaultUnit(setup);

        return rows.Select(t =>
        {
            var unit = t.Commodity != null && units.TryGetValue(t.Commodity, out var u) ? u : fallback;
            return new WeighRow
            {
                Transaction = t,
                CustomValues = customValues.GetValueOrDefault(t.Ticket) ?? new Dictionary<int, string?>(),
                UnitName = unit.Name,
                UnitPounds = unit.Pounds
            };
        }).ToList();
    }

    /// <summary>Commodity name → its configured reporting unit (Edit Tables →
    /// Commodities). Mirrors ReportController.CommodityUnits.</summary>
    public static Dictionary<string, (string Name, double Pounds)> CommodityUnits(ScaleDbContext db) =>
        db.Commodities
            .Where(c => c.UnitName != null && c.UnitName != "" && c.UnitPounds > 0)
            .ToList()
            .GroupBy(c => c.CommodityName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().UnitName!, g.First().UnitPounds!.Value),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback for commodities with no unit of their own.</summary>
    public static (string Name, double Pounds) DefaultUnit(AppSetup setup) =>
        string.Equals(setup.DefaultReportUnit, "kg", StringComparison.OrdinalIgnoreCase)
            ? ("kg", 2.20462262)
            : ("lbs", 1.0);
}
