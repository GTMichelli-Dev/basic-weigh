using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// A manual inventory correction for a bin — shrinkage, drying loss, spillage,
/// or a starting balance from before the feature was enabled. Amount is a
/// signed pound value added to the computed balance. A "true up" entry is
/// stored the same way: the UI takes the measured on-hand amount and records
/// the difference from the computed balance at that moment. Bin and Commodity
/// are stored as text (like Transaction fields) so history survives renames.
/// </summary>
public class BinAdjustment
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string Bin { get; set; } = string.Empty;

    [StringLength(50)]
    public string? Commodity { get; set; }

    /// <summary>Signed adjustment in pounds (negative = shrinkage/loss).</summary>
    [Display(Name = "Adjustment (lbs)")]
    public int AmountLbs { get; set; }

    public DateTime Date { get; set; }

    [StringLength(200)]
    public string? Note { get; set; }

    /// <summary>Set on bin-transfer adjustments: the out-of-source and
    /// into-destination rows share one id so they display as a Transfer and
    /// delete together. Null for ordinary adjustments/true-ups.</summary>
    [StringLength(40)]
    public string? TransferId { get; set; }
}
