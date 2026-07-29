using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// A storage bin for the Bin Inventory feature (Setup → Options →
/// Use Bin Inventory). Loads delivered into a bin add to its inventory,
/// loads hauled out deduct — direction is inferred from the ticket weights.
/// </summary>
public class Bin
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    [Display(Name = "Bin Name")]
    public string BinName { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    [Display(Name = "Use at Kiosk")]
    public bool UseAtKiosk { get; set; } = true;

    /// <summary>Optional physical location (Site) this bin sits at; null =
    /// offered at every location.</summary>
    [Display(Name = "Location")]
    public int? SiteId { get; set; }

    /// <summary>Assigned commodity: what this bin is designated to hold, set
    /// ahead of the first load or changed once the bin is back to zero. When
    /// set it locks tickets to that commodity even while the bin is empty;
    /// null = the bin takes whatever arrives first. Stored as text so the
    /// assignment survives commodity renames.</summary>
    [StringLength(50)]
    public string? Commodity { get; set; }
}
