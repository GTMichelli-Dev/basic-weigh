using Microsoft.AspNetCore.Mvc;
using Foundation.Web.Data;
using Foundation.Web.Models;
using Foundation.Web.Services;

namespace Foundation.Web.Controllers;

/// <summary>
/// CRUD API for admin-defined custom ticket fields (managed on Setup → Fields).
/// </summary>
public class CustomFieldsController : Controller
{
    private static readonly string[] ValidTypes = { "Text", "Integer", "Real", "Formula" };

    /// <summary>Ticket built-ins a formula can reference.</summary>
    private static readonly string[] FormulaBuiltins =
        { "NetWeight", "GrossWeight", "TareWeight", "InWeight", "OutWeight", "NetTons" };

    /// <summary>Standard fields a sub-field can cascade under (their form
    /// input name doubles as the ParentField key).</summary>
    private static readonly string[] StandardParents =
        { "Commodity", "Customer", "Carrier", "Location", "Destination", "Bin" };

    private readonly ScaleDbContext _db;

    public CustomFieldsController(ScaleDbContext db)
    {
        _db = db;
    }

    [HttpGet("api/customfields")]
    public IActionResult GetAll()
    {
        var fields = _db.CustomFields
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .ToList();
        return Json(fields.Select(f => new
        {
            f.Id, f.Name, f.FieldType, f.Required, f.Active, f.ShowOnTicket, f.SortOrder,
            f.ListValues, f.MinValue, f.MaxValue, f.Precision, f.PromptAtKiosk, f.ParentField, f.Formula
        }));
    }

    [HttpPost("api/customfields")]
    public IActionResult Add([FromBody] CustomField field)
    {
        var error = Validate(field) ?? ValidateParent(field, selfId: null);
        if (error != null) return BadRequest(new { error });
        if (_db.CustomFields.Any(f => f.Name.ToLower() == field.Name.Trim().ToLower()))
            return BadRequest(new { error = "A field with that name already exists." });

        field.Id = 0;
        field.Name = field.Name.Trim();
        Normalize(field);
        _db.CustomFields.Add(field);
        _db.SaveChanges();
        // A new formula applies to history too, not just tickets saved later.
        if (field.FieldType == "Formula") FormulaFields.RecomputeAll(_db);
        return Json(field);
    }

    [HttpPut("api/customfields/{id}")]
    public IActionResult Update(int id, [FromBody] CustomField field)
    {
        var existing = _db.CustomFields.Find(id);
        if (existing == null) return NotFound();

        var error = Validate(field) ?? ValidateParent(field, selfId: id);
        if (error != null) return BadRequest(new { error });
        if (_db.CustomFields.Any(f => f.Id != id && f.Name.ToLower() == field.Name.Trim().ToLower()))
            return BadRequest(new { error = "A field with that name already exists." });

        Normalize(field);

        // Changing (or clearing) the parent orphans the per-parent mapping —
        // its keys were the old parent's values. Drop the mapping but keep the
        // flattened union in ListValues so no choices are silently lost.
        if (existing.ParentField != field.ParentField)
        {
            var stale = _db.CustomFieldListValues.Where(v => v.CustomFieldId == id);
            _db.CustomFieldListValues.RemoveRange(stale);
        }

        existing.Name = field.Name.Trim();
        existing.FieldType = field.FieldType;
        existing.Required = field.Required;
        existing.Active = field.Active;
        existing.ShowOnTicket = field.ShowOnTicket;
        existing.SortOrder = field.SortOrder;
        // A sub-field's ListValues is the synced union of its mapping — don't
        // overwrite it from the modal (the textarea is hidden for sub-fields).
        if (field.ParentField == null) existing.ListValues = field.ListValues;
        existing.MinValue = field.MinValue;
        existing.MaxValue = field.MaxValue;
        existing.Precision = field.Precision;
        existing.PromptAtKiosk = field.PromptAtKiosk;
        existing.ParentField = field.ParentField;
        existing.Formula = field.Formula;
        if (!existing.IsKioskEligible()) existing.PromptAtKiosk = false;
        _db.SaveChanges();
        // Any definition change can shift formula results (edited formula,
        // renamed/deactivated referenced field) — re-sync stored history.
        // No-op inside when no formula fields exist.
        FormulaFields.RecomputeAll(_db);
        return Json(existing);
    }

    /// <summary>
    /// ParentField rules: text list fields only; parent must be a known
    /// standard field or an existing list-backed text custom field; no
    /// self-reference or cycles (Variety under Commodity, Grade under Variety
    /// is fine — Grade under itself, or two fields under each other, is not).
    /// </summary>
    private string? ValidateParent(CustomField field, int? selfId)
    {
        var parent = string.IsNullOrWhiteSpace(field.ParentField) ? null : field.ParentField.Trim();
        field.ParentField = parent;
        if (parent == null) return null;

        if (field.FieldType != "Text")
            return "Only text dropdown fields can be a sub-field of another field.";
        if (StandardParents.Contains(parent)) return null;

        if (!parent.StartsWith("cf_") || !int.TryParse(parent[3..], out var parentId))
            return "Unknown parent field.";
        if (selfId.HasValue && parentId == selfId.Value)
            return "A field can't be a sub-field of itself.";

        var parentField = _db.CustomFields.Find(parentId);
        if (parentField == null) return "Parent field no longer exists.";
        if (parentField.FieldType != "Text")
            return "The parent must be a text dropdown field.";

        // Walk up the parent chain to reject cycles.
        var seen = new HashSet<int> { parentId };
        if (selfId.HasValue) seen.Add(selfId.Value);
        var cursor = parentField;
        while (cursor?.ParentField is { } up && up.StartsWith("cf_") && int.TryParse(up[3..], out var upId))
        {
            if (!seen.Add(upId))
                return "That parent would create a loop of sub-fields.";
            cursor = _db.CustomFields.Find(upId);
        }
        return null;
    }

    /// <summary>Deletes the definition AND all stored ticket values (cascade).
    /// To retire a field but keep history, set Active = false instead.</summary>
    [HttpDelete("api/customfields/{id}")]
    public IActionResult Delete(int id)
    {
        var existing = _db.CustomFields.Find(id);
        if (existing == null) return NotFound();

        var valueCount = _db.TransactionCustomValues.Count(v => v.CustomFieldId == id);
        _db.CustomFields.Remove(existing);
        _db.SaveChanges();
        // Formulas referencing the deleted field now resolve to blank — re-sync.
        FormulaFields.RecomputeAll(_db);
        return Json(new { success = true, deletedValues = valueCount });
    }

    // ===== Dropdown list values (Edit Tables → per-field tab) =====
    // Flat fields keep their choices as newline-separated text on
    // CustomField.ListValues. Sub-fields (ParentField set) keep them in
    // CustomFieldListValues, one row per (parent value, choice); pass
    // ?parent= to scope every call, and ListValues is re-synced to the
    // flattened union so validation / kiosk eligibility work unchanged.

    public record ListValueEdit(string? OldValue, string? NewValue);

    [HttpGet("api/customfields/{id}/values")]
    public IActionResult GetValues(int id, string? parent = null)
    {
        var field = _db.CustomFields.Find(id);
        if (field == null) return NotFound();
        if (field.ParentField != null && !string.IsNullOrEmpty(parent))
        {
            return Json(_db.CustomFieldListValues
                .Where(v => v.CustomFieldId == id && v.ParentValue == parent)
                .OrderBy(v => v.SortOrder).ThenBy(v => v.Value)
                .Select(v => new { value = v.Value })
                .ToList());
        }
        return Json(field.GetListValues().Select(v => new { value = v }));
    }

    /// <summary>Full parent → choices map for a sub-field. Used by the weigh
    /// forms and the kiosk to filter the dropdown by the selected parent.</summary>
    [HttpGet("api/customfields/{id}/valuemap")]
    public IActionResult GetValueMap(int id)
    {
        var field = _db.CustomFields.Find(id);
        if (field == null) return NotFound();
        return Json(BuildValueMap(id));
    }

    private Dictionary<string, List<string>> BuildValueMap(int fieldId) =>
        _db.CustomFieldListValues
            .Where(v => v.CustomFieldId == fieldId)
            .OrderBy(v => v.ParentValue).ThenBy(v => v.SortOrder).ThenBy(v => v.Value)
            .AsEnumerable()
            .GroupBy(v => v.ParentValue)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Value).ToList());

    /// <summary>Add (OldValue null), rename, or delete (NewValue null) one
    /// choice. Order of the remaining choices is preserved. For sub-fields the
    /// edit applies to the given parent value only.</summary>
    [HttpPost("api/customfields/{id}/values")]
    public IActionResult EditValue(int id, [FromBody] ListValueEdit edit, string? parent = null)
    {
        var field = _db.CustomFields.Find(id);
        if (field == null) return NotFound();
        if (field.FieldType != "Text") return BadRequest(new { error = "Only text fields have dropdown values." });

        var newVal = edit.NewValue?.Trim();
        if (newVal is { Length: > 200 }) return BadRequest(new { error = "Values must be 200 characters or fewer." });

        if (field.ParentField != null)
        {
            if (string.IsNullOrEmpty(parent))
                return BadRequest(new { error = "Pick a parent value first." });

            var rows = _db.CustomFieldListValues
                .Where(v => v.CustomFieldId == id && v.ParentValue == parent)
                .OrderBy(v => v.SortOrder).ThenBy(v => v.Value)
                .ToList();

            if (edit.OldValue == null)
            {
                if (string.IsNullOrEmpty(newVal)) return BadRequest(new { error = "Value is required." });
                if (rows.Any(r => r.Value == newVal)) return BadRequest(new { error = "That value already exists." });
                _db.CustomFieldListValues.Add(new CustomFieldListValue
                {
                    CustomFieldId = id,
                    ParentValue = parent,
                    Value = newVal,
                    SortOrder = rows.Count == 0 ? 10 : rows.Max(r => r.SortOrder) + 10
                });
            }
            else
            {
                var row = rows.FirstOrDefault(r => r.Value == edit.OldValue);
                if (row == null) return NotFound(new { error = "Value not found." });
                if (string.IsNullOrEmpty(newVal))
                {
                    _db.CustomFieldListValues.Remove(row);
                }
                else
                {
                    if (newVal != edit.OldValue && rows.Any(r => r.Value == newVal))
                        return BadRequest(new { error = "That value already exists." });
                    row.Value = newVal;
                }
            }
            return SyncUnionAndSave(field);
        }

        var values = field.GetListValues();
        if (edit.OldValue == null)
        {
            if (string.IsNullOrEmpty(newVal)) return BadRequest(new { error = "Value is required." });
            if (values.Contains(newVal)) return BadRequest(new { error = "That value already exists." });
            values.Add(newVal);
        }
        else
        {
            var idx = values.IndexOf(edit.OldValue);
            if (idx < 0) return NotFound(new { error = "Value not found." });
            if (string.IsNullOrEmpty(newVal))
            {
                values.RemoveAt(idx);
            }
            else
            {
                if (newVal != edit.OldValue && values.Contains(newVal))
                    return BadRequest(new { error = "That value already exists." });
                values[idx] = newVal;
            }
        }

        return SaveValues(field, values);
    }

    /// <summary>Replace the full choice list (drag-reorder posts the new
    /// order). For sub-fields, replaces the list under the given parent value.</summary>
    [HttpPut("api/customfields/{id}/values")]
    public IActionResult ReorderValues(int id, [FromBody] List<string> values, string? parent = null)
    {
        var field = _db.CustomFields.Find(id);
        if (field == null) return NotFound();
        if (field.FieldType != "Text") return BadRequest(new { error = "Only text fields have dropdown values." });

        var clean = values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList();
        if (clean.Any(v => v.Length > 200)) return BadRequest(new { error = "Values must be 200 characters or fewer." });

        if (field.ParentField != null)
        {
            if (string.IsNullOrEmpty(parent))
                return BadRequest(new { error = "Pick a parent value first." });
            var rows = _db.CustomFieldListValues
                .Where(v => v.CustomFieldId == id && v.ParentValue == parent);
            _db.CustomFieldListValues.RemoveRange(rows);
            for (var i = 0; i < clean.Count; i++)
            {
                _db.CustomFieldListValues.Add(new CustomFieldListValue
                {
                    CustomFieldId = id,
                    ParentValue = parent,
                    Value = clean[i],
                    SortOrder = (i + 1) * 10
                });
            }
            return SyncUnionAndSave(field);
        }

        return SaveValues(field, clean);
    }

    /// <summary>Re-derive a sub-field's flat ListValues union from its mapping
    /// rows (pending changes included) and save everything.</summary>
    private IActionResult SyncUnionAndSave(CustomField field)
    {
        _db.SaveChanges(); // persist mapping edits so the union query sees them

        var union = _db.CustomFieldListValues
            .Where(v => v.CustomFieldId == field.Id)
            .OrderBy(v => v.ParentValue).ThenBy(v => v.SortOrder).ThenBy(v => v.Value)
            .Select(v => v.Value)
            .AsEnumerable()
            .Distinct()
            .ToList();

        var joined = string.Join("\n", union);
        field.ListValues = union.Count > 0 && joined.Length <= 4000 ? joined : (union.Count > 0 ? field.ListValues : null);
        if (!field.IsKioskEligible()) field.PromptAtKiosk = false;
        _db.SaveChanges();
        return Json(new { success = true, count = union.Count });
    }

    private IActionResult SaveValues(CustomField field, List<string> values)
    {
        var joined = string.Join("\n", values);
        if (joined.Length > 4000) return BadRequest(new { error = "Dropdown list is too long (4000 characters max)." });
        field.ListValues = values.Count > 0 ? joined : null;
        // A field that loses its last choice becomes free text — no longer
        // kiosk-eligible.
        if (!field.IsKioskEligible()) field.PromptAtKiosk = false;
        _db.SaveChanges();
        return Json(new { success = true, count = values.Count });
    }

    private string? Validate(CustomField field)
    {
        if (string.IsNullOrWhiteSpace(field.Name)) return "Field name is required.";
        if (field.Name.Trim().Length > 50) return "Field name must be 50 characters or fewer.";
        if (!ValidTypes.Contains(field.FieldType)) return "Type must be Text, Integer, or Real.";
        if (field.MinValue.HasValue && field.MaxValue.HasValue && field.MinValue > field.MaxValue)
            return "Min must be less than or equal to Max.";
        if (field.Precision is < 0 or > 6)
            return "Decimals must be between 0 and 6.";
        if (field.FieldType == "Text" && field.ListValues != null)
        {
            var values = field.GetListValues();
            if (values.Any(v => v.Length > 200))
                return "Each dropdown value must be 200 characters or fewer.";
            if (string.Join("\n", values).Length > 4000)
                return "Dropdown list is too long (4000 characters max).";
        }
        if (field.FieldType == "Formula")
        {
            if (string.IsNullOrWhiteSpace(field.Formula))
                return "A formula is required, e.g. NetWeight / 2000 or [Price] * NetTons.";
            var numericNames = _db.CustomFields
                .Where(f => f.FieldType == "Integer" || f.FieldType == "Real" || f.FieldType == "Formula")
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Exists(string name) =>
                FormulaBuiltins.Contains(name, StringComparer.OrdinalIgnoreCase) || numericNames.Contains(name);
            var error = FormulaEvaluator.Validate(field.Formula.Trim(), Exists);
            if (error != null) return error;
        }
        return null;
    }

    /// <summary>Clears settings that don't apply to the chosen type, and
    /// canonicalizes the dropdown list (trimmed lines, blanks dropped).</summary>
    private static void Normalize(CustomField field)
    {
        if (field.FieldType == "Text")
        {
            var values = field.GetListValues();
            field.ListValues = values.Count > 0 ? string.Join("\n", values) : null;
            field.MinValue = null;
            field.MaxValue = null;
            field.Precision = null;
        }
        else if (field.FieldType == "Formula")
        {
            // Computed field: no dropdown, no cascade, no min/max, never
            // required (there's nothing to enter), never prompted anywhere.
            field.Formula = field.Formula?.Trim();
            field.ListValues = null;
            field.ParentField = null;
            field.MinValue = null;
            field.MaxValue = null;
            field.Required = false;
        }
        else
        {
            field.ListValues = null;
            field.ParentField = null; // cascading applies to text dropdowns only
            if (field.FieldType == "Integer") field.Precision = null;
        }
        if (field.FieldType != "Formula") field.Formula = null;
        // Kiosk prompting requires a constrained input.
        if (!field.IsKioskEligible()) field.PromptAtKiosk = false;
    }
}
