using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Foundation.Web.Models;

public class Transaction
{
    [Key]
    [StringLength(20)]
    public string Ticket { get; set; } = string.Empty;

    public bool Void { get; set; }

    [Display(Name = "In Weight")]
    public int InWeight { get; set; }

    [Display(Name = "Date In")]
    public DateTime DateIn { get; set; }

    [Display(Name = "Date Out")]
    public DateTime? DateOut { get; set; }

    [Display(Name = "Out Weight")]
    public int? OutWeight { get; set; }

    [StringLength(50)]
    public string? Customer { get; set; }

    [StringLength(50)]
    public string? Commodity { get; set; }

    [StringLength(50)]
    public string? Carrier { get; set; }

    [StringLength(50)]
    [Display(Name = "Truck ID")]
    public string? TruckId { get; set; }

    [StringLength(50)]
    public string? Location { get; set; }

    [StringLength(50)]
    public string? Destination { get; set; }

    /// <summary>Storage bin this load was delivered into (truck arrived heavy)
    /// or hauled out of (truck left heavy). Only used when Bin Inventory is
    /// enabled in Setup. Stored as text so history survives bin renames.</summary>
    [StringLength(50)]
    public string? Bin { get; set; }

    /// <summary>Set on a weighed bin-to-bin transfer: the destination bin. The
    /// ticket's net weight leaves Bin and lands here regardless of which weigh
    /// came first. Null for ordinary in/out tickets.</summary>
    [StringLength(50)]
    [Display(Name = "Transfer To")]
    public string? TransferToBin { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    /// <summary>Name of the site scale that captured the in-weight (multi-scale
    /// sites). Stored as text so history survives scale renames/deletes.</summary>
    [StringLength(50)]
    [Display(Name = "In Scale")]
    public string? InScale { get; set; }

    /// <summary>Name of the site scale that captured the out-weight.</summary>
    [StringLength(50)]
    [Display(Name = "Out Scale")]
    public string? OutScale { get; set; }

    /// <summary>Card this ticket was weighed with (HID/prox card weighing).
    /// Stored as the card number rather than a key so the audit trail survives
    /// a card being re-enrolled or deleted. Null for keyed-in tickets.</summary>
    [StringLength(50)]
    [Display(Name = "Card")]
    public string? CardNumber { get; set; }

    [Display(Name = "Manual Inbound")]
    public bool ManualInbound { get; set; }

    [Display(Name = "Manual Outbound")]
    public bool ManualOutbound { get; set; }

    [Display(Name = "Sent to QuickBooks")]
    public bool SentToQuickBooks { get; set; }

    [NotMapped]
    public int GrossWeight => Math.Max(InWeight, OutWeight ?? 0);

    [NotMapped]
    public int TareWeight => OutWeight.HasValue ? Math.Min(InWeight, OutWeight.Value) : 0;

    [NotMapped]
    public int NetWeight => OutWeight.HasValue ? Math.Abs(InWeight - OutWeight.Value) : 0;

    [NotMapped]
    public bool IsComplete => OutWeight.HasValue && DateOut.HasValue;
}
