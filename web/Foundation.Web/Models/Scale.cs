using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// A named site scale (Scale page), grain-management style: operators pick a
/// scale by name on the weigh forms and kiosks, and tickets record which scale
/// captured each weighment. HardwareId links the scale to a live indicator
/// feed ("serviceId:scaleId" as reported by a ScaleReaderService); a scale
/// with no HardwareId is driven by the per-scale simulator in demo mode.
/// </summary>
public class Scale
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    [Display(Name = "Scale Name")]
    public string Name { get; set; } = "";

    /// <summary>"serviceId:scaleId" of the indicator feed, null = simulated.</summary>
    [StringLength(100)]
    [Display(Name = "Hardware Feed")]
    public string? HardwareId { get; set; }

    /// <summary>Physical location (Site) this scale belongs to; null = shown
    /// at every location.</summary>
    [Display(Name = "Location")]
    public int? SiteId { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    /// <summary>Inactive scales disappear from pickers but stay on old tickets.</summary>
    [Display(Name = "Active")]
    public bool Active { get; set; } = true;

    /// <summary>
    /// Optional per-scale ticket printers ("serviceId:printerId", or
    /// "Browser:Browser"). Null falls back to the site-wide defaults
    /// (AppSetup.InboundPrinterId / OutboundPrinterId).
    /// </summary>
    [StringLength(100)]
    [Display(Name = "Inbound Printer")]
    public string? InboundPrinterId { get; set; }

    [StringLength(100)]
    [Display(Name = "Outbound Printer")]
    public string? OutboundPrinterId { get; set; }

    /// <summary>
    /// Gate/light output to fire when a ticket completes on this scale, as
    /// "serviceId:gateId" reported by a GateControllerService. Null means this
    /// scale controls nothing, which is every site that has not wired a gate.
    /// </summary>
    [StringLength(100)]
    [Display(Name = "Gate")]
    public string? GateId { get; set; }
}
