using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

public class Commodity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    [Display(Name = "Commodity Name")]
    public string CommodityName { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    [Display(Name = "Use at Kiosk")]
    public bool UseAtKiosk { get; set; } = true;

    /// <summary>Optional physical location (Site) this commodity is handled
    /// at; null = offered at every location.</summary>
    [Display(Name = "Location")]
    public int? SiteId { get; set; }

    /// <summary>Reporting unit label ("Bushels", "CWT", …). When set together
    /// with UnitPounds, reports show quantities in this unit alongside pounds.</summary>
    [StringLength(20)]
    [Display(Name = "Reporting Unit")]
    public string? UnitName { get; set; }

    /// <summary>Pounds per reporting unit (corn bushel 56, wheat bushel 60,
    /// CWT 100). Null or 0 = no unit conversion for this commodity.</summary>
    [Display(Name = "Lbs per Unit")]
    public double? UnitPounds { get; set; }
}
