using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// One column of an emailed weigh report: a header, how to pull the value off a
/// row, and enough type information for ClosedXML to format the cell.
/// </summary>
public record WeighColumn(
    string Header,
    Func<WeighRow, object?> Value,
    WeighColumnKind Kind = WeighColumnKind.Text);

public enum WeighColumnKind
{
    Text,
    DateTime,
    /// <summary>Whole pounds — thousands separator, no decimals.</summary>
    Weight,
    /// <summary>Converted quantity — two decimals.</summary>
    Quantity
}

/// <summary>A completed load plus its resolved custom-field values and unit.</summary>
public class WeighRow
{
    public required Transaction Transaction { get; init; }

    /// <summary>CustomFieldId → stored text value.</summary>
    public Dictionary<int, string?> CustomValues { get; init; } = new();

    /// <summary>Reporting unit for this row's commodity, falling back to the
    /// site default (Setup → Options → Default Reporting Unit).</summary>
    public string UnitName { get; init; } = "lbs";

    /// <summary>Pounds per <see cref="UnitName"/>.</summary>
    public double UnitPounds { get; init; } = 1.0;

    public double NetUnits => UnitPounds > 0
        ? Math.Round(Transaction.NetWeight / UnitPounds, 2)
        : 0;
}

/// <summary>
/// Builds the column set for scheduled Excel reports from the enabled ticket
/// fields, so the workbook shows exactly what the operator sees on the weigh
/// forms — hide Carrier in Setup → Fields and it disappears here too.
///
/// Ticket and the weigh timestamps are always present (a report row has to be
/// identifiable), as are Net lbs, Unit and Net in that unit. Everything between
/// comes from FieldOrdering.GetFormSlots, which is the same ordering the weigh
/// forms use, so the spreadsheet reads in the operator's field order.
/// </summary>
public static class WeighReportColumns
{
    public static List<WeighColumn> Build(
        AppSetup setup,
        IEnumerable<CustomField> customFields,
        bool includeGrossTare)
    {
        var cols = new List<WeighColumn>
        {
            new("Ticket", r => r.Transaction.Ticket),
            new("Date In", r => AppTimeZone.FromUtc(r.Transaction.DateIn), WeighColumnKind.DateTime),
            new("Date Out", r => r.Transaction.DateOut.HasValue
                ? AppTimeZone.FromUtc(r.Transaction.DateOut.Value)
                : null, WeighColumnKind.DateTime)
        };

        // Formula fields are excluded from the weigh forms (nothing to type) but
        // they're computed values that belong on a report, so add them back.
        var fields = customFields.ToList();
        var slots = FieldOrdering.GetFormSlots(setup, fields);
        var formulaSlots = fields
            .Where(f => f.Active && f.FieldType == "Formula")
            .Select(f => new FieldSlot($"cf_{f.Id}", f.SortOrder, f));
        var ordered = slots.Concat(formulaSlots).OrderBy(s => s.Order).ThenBy(s => s.Key);

        foreach (var slot in ordered)
        {
            if (slot.Field is { } cf)
            {
                var id = cf.Id;
                var kind = cf.FieldType is "Integer" or "Real" or "Formula"
                    ? WeighColumnKind.Quantity
                    : WeighColumnKind.Text;
                cols.Add(new WeighColumn(cf.Name, r => CustomValue(r, id, kind), kind));
                continue;
            }

            cols.Add(slot.Key switch
            {
                "Commodity"   => new WeighColumn("Commodity", r => r.Transaction.Commodity),
                "Customer"    => new WeighColumn("Customer", r => r.Transaction.Customer),
                "Carrier"     => new WeighColumn("Carrier", r => r.Transaction.Carrier),
                "TruckId"     => new WeighColumn("Truck ID", r => r.Transaction.TruckId),
                "Location"    => new WeighColumn("Location", r => r.Transaction.Location),
                "Destination" => new WeighColumn("Destination", r => r.Transaction.Destination),
                "Bin"         => new WeighColumn("Bin", r => r.Transaction.Bin),
                "Notes"       => new WeighColumn("Notes", r => r.Transaction.Notes),
                _             => new WeighColumn(slot.Key, _ => null)
            });
        }

        if (includeGrossTare)
        {
            cols.Add(new WeighColumn("Gross (lbs)", r => r.Transaction.GrossWeight, WeighColumnKind.Weight));
            cols.Add(new WeighColumn("Tare (lbs)", r => r.Transaction.TareWeight, WeighColumnKind.Weight));
        }

        cols.Add(new WeighColumn("Net (lbs)", r => r.Transaction.NetWeight, WeighColumnKind.Weight));
        cols.Add(new WeighColumn("Unit", r => r.UnitName));
        cols.Add(new WeighColumn("Net (Units)", r => r.NetUnits, WeighColumnKind.Quantity));

        return cols;
    }

    /// <summary>Custom values are stored as text; numeric ones become real
    /// numbers so Excel can total them, and anything unparseable falls back to
    /// the raw text rather than blanking the cell.</summary>
    private static object? CustomValue(WeighRow row, int fieldId, WeighColumnKind kind)
    {
        var raw = row.CustomValues.GetValueOrDefault(fieldId);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (kind == WeighColumnKind.Quantity
            && double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        return raw;
    }
}
