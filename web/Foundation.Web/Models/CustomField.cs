using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// Admin-defined extra ticket field (Setup → Fields). Rendered on the
/// Weigh In / Weigh Out / Edit / Basic Ticket forms, shown as a grid column,
/// and printed on tickets when ShowOnTicket is set.
/// </summary>
public class CustomField
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    [Display(Name = "Field Name")]
    public string Name { get; set; } = "";

    // Text, Integer, Real, Formula
    [StringLength(10)]
    [Display(Name = "Type")]
    public string FieldType { get; set; } = "Text";

    /// <summary>
    /// Formula fields only: an arithmetic expression over the ticket's
    /// numeric fields, e.g. "NetWeight / 56" or "[Price] * NetTons".
    /// Built-ins: NetWeight, GrossWeight, TareWeight, InWeight, OutWeight,
    /// NetTons. Custom numeric fields by name — [brackets] when the name has
    /// spaces. Operators + - * / ( ), functions round(x,n), abs, min, max.
    /// The result is computed and stored whenever the ticket is saved
    /// (FormulaFields.RecomputeAndSave); a missing referenced value leaves
    /// the result blank.
    /// </summary>
    [StringLength(500)]
    public string? Formula { get; set; }

    /// <summary>When true the weigh forms refuse to save without a value.</summary>
    [Display(Name = "Required")]
    public bool Required { get; set; }

    /// <summary>Inactive fields stay in history but stop appearing on forms.</summary>
    [Display(Name = "Active")]
    public bool Active { get; set; } = true;

    [Display(Name = "Show on Ticket")]
    public bool ShowOnTicket { get; set; } = true;

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// Text fields only: newline-separated dropdown choices. When set, the
    /// weigh forms render a select instead of a free-text input. Managed on
    /// Setup → Fields (one value per line = full add/edit/delete/reorder).
    /// For a sub-field (ParentField set) this holds the flattened union of
    /// every parent's choices — kept in sync by CustomFieldsController — so
    /// validation and kiosk-eligibility keep working unchanged.
    /// </summary>
    [StringLength(4000)]
    public string? ListValues { get; set; }

    /// <summary>
    /// Makes this a cascading sub-field: its dropdown choices are grouped
    /// under each value of the parent field, and the forms/kiosk filter the
    /// choices by the selected parent value (e.g. Variety under Commodity,
    /// Grade under Variety). Either a standard field name ("Commodity",
    /// "Customer", "Carrier", "Location", "Destination", "Bin") or another
    /// list-backed custom field as "cf_{id}". Text list fields only. The
    /// per-parent choices live in CustomFieldListValues (Edit Tables).
    /// </summary>
    [StringLength(20)]
    public string? ParentField { get; set; }

    /// <summary>Integer/Real fields only: inclusive minimum allowed value.</summary>
    public double? MinValue { get; set; }

    /// <summary>Integer/Real fields only: inclusive maximum allowed value.</summary>
    public double? MaxValue { get; set; }

    /// <summary>Real fields only: max decimal places (0–6). Null = unlimited.</summary>
    public int? Precision { get; set; }

    /// <summary>
    /// Ask for this field during the kiosk weigh-in flow. Only meaningful for
    /// constrained inputs — numeric fields and list-backed text fields; a
    /// free-text field can't prompt at the kiosk (no keyboard-driven prose).
    /// </summary>
    public bool PromptAtKiosk { get; set; }

    /// <summary>True when this field is allowed to prompt at the kiosk.
    /// A sub-field stays eligible even while its choice map is empty — the
    /// kiosk auto-skips a prompt whose list resolves to no items. Formula
    /// fields are computed, never prompted.</summary>
    public bool IsKioskEligible() =>
        FieldType != "Formula"
        && (FieldType != "Text" || ParentField != null || GetListValues().Count > 0);

    /// <summary>ListValues split into clean entries (empty for free-text fields).</summary>
    public List<string> GetListValues() =>
        (ListValues ?? "")
            .Split('\n')
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToList();
}

/// <summary>
/// One dropdown choice of a cascading sub-field (CustomField.ParentField set),
/// scoped to one value of the parent field — e.g. field "Variety",
/// ParentValue "Corn", Value "Yellow Dent". ParentValue is stored as text so
/// the mapping survives parent renames the same way ticket values do.
/// </summary>
public class CustomFieldListValue
{
    [Key]
    public int Id { get; set; }

    public int CustomFieldId { get; set; }

    [Required]
    [StringLength(200)]
    public string ParentValue { get; set; } = "";

    [Required]
    [StringLength(200)]
    public string Value { get; set; } = "";

    /// <summary>Display order within the parent value's list.</summary>
    public int SortOrder { get; set; }
}

/// <summary>Per-ticket value for a CustomField. Stored as text; the field's
/// FieldType drives input rendering and validation, not storage.</summary>
public class TransactionCustomValue
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string Ticket { get; set; } = "";

    public int CustomFieldId { get; set; }

    [StringLength(200)]
    public string? Value { get; set; }
}
