using System.Globalization;
using Foundation.Web.Data;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Computes Formula custom fields for a ticket and stores the results as
/// ordinary TransactionCustomValues — so grids, printed tickets, reports,
/// exports, and the APIs all pick them up with no special handling. Called
/// after every save path that can change a ticket's weights or field values
/// (admin forms, grid edits, kiosk, table API). Formulas evaluate in
/// SortOrder so one formula can reference another defined above it. A
/// missing referenced value (or division by zero) clears the result.
/// </summary>
public static class FormulaFields
{
    private static List<CustomField> ActiveFormulas(List<CustomField> fields) =>
        fields
            .Where(f => f.FieldType == "Formula" && !string.IsNullOrWhiteSpace(f.Formula))
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToList();

    /// <summary>Recompute every formula value for the ticket and SaveChanges.
    /// No-op (and no save) when no formula fields are defined.</summary>
    public static void RecomputeAndSave(ScaleDbContext db, Transaction t)
    {
        var fields = db.CustomFields.Where(f => f.Active).ToList();
        var formulas = ActiveFormulas(fields);
        if (formulas.Count == 0) return;

        var stored = db.TransactionCustomValues.Where(v => v.Ticket == t.Ticket).ToList();
        ComputeTicket(db, fields, formulas, t, stored);
        db.SaveChanges();
    }

    /// <summary>
    /// Recompute formula values for EVERY ticket in one pass — called when a
    /// field definition changes (formula added/edited, referenced field
    /// renamed or deactivated) so history matches the new definition instead
    /// of only tickets saved afterwards. No-op when no formulas are defined.
    /// </summary>
    public static void RecomputeAll(ScaleDbContext db)
    {
        var fields = db.CustomFields.Where(f => f.Active).ToList();
        var formulas = ActiveFormulas(fields);
        if (formulas.Count == 0) return;

        var byTicket = db.TransactionCustomValues.ToList()
            .GroupBy(v => v.Ticket)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var t in db.Transactions.ToList())
        {
            if (!byTicket.TryGetValue(t.Ticket, out var stored))
                stored = new List<TransactionCustomValue>();
            ComputeTicket(db, fields, formulas, t, stored);
        }
        db.SaveChanges();
    }

    private static void ComputeTicket(ScaleDbContext db, List<CustomField> fields,
        List<CustomField> formulas, Transaction t, List<TransactionCustomValue> stored)
    {
        // Environment: built-in ticket numbers + this ticket's numeric custom
        // values, keyed case-insensitively by field name.
        var env = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NetWeight"] = t.NetWeight,
            ["GrossWeight"] = t.GrossWeight,
            ["TareWeight"] = t.TareWeight,
            ["InWeight"] = t.InWeight,
            ["OutWeight"] = t.OutWeight,
            ["NetTons"] = Math.Round(t.NetWeight / 2000.0, 4)
        };
        foreach (var f in fields.Where(f => f.FieldType is "Integer" or "Real"))
        {
            var raw = stored.FirstOrDefault(v => v.CustomFieldId == f.Id)?.Value;
            env[f.Name] = raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n : null;
        }

        foreach (var f in formulas)
        {
            double? result;
            try
            {
                result = FormulaEvaluator.Evaluate(f.Formula!,
                    name => env.TryGetValue(name, out var v) ? v : null);
            }
            catch (FormatException)
            {
                result = null; // formula went bad after fields changed — leave blank
            }

            if (result.HasValue && f.Precision.HasValue)
                result = Math.Round(result.Value, Math.Clamp(f.Precision.Value, 0, 6), MidpointRounding.AwayFromZero);
            env[f.Name] = result; // later formulas can chain on this one

            var row = stored.FirstOrDefault(v => v.CustomFieldId == f.Id);
            if (!result.HasValue)
            {
                if (row != null) db.TransactionCustomValues.Remove(row);
            }
            else
            {
                var text = result.Value.ToString("0.######", CultureInfo.InvariantCulture);
                if (row == null)
                {
                    row = new TransactionCustomValue { Ticket = t.Ticket, CustomFieldId = f.Id, Value = text };
                    db.TransactionCustomValues.Add(row);
                    stored.Add(row);
                }
                else
                {
                    row.Value = text;
                }
            }
        }
    }
}
