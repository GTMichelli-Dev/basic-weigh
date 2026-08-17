using System.ComponentModel.DataAnnotations;

namespace RfidReaderService.Models;

/// <summary>Global service settings — single row table.</summary>
public class ServiceSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>Unique identifier for this service instance (e.g. "site-a").
    /// A kiosk is mapped to "serviceId:readerId".</summary>
    [Required]
    [StringLength(50)]
    public string ServiceId { get; set; } = "default";

    /// <summary>Base URL of the BasicWeigh web application.</summary>
    [Required]
    [StringLength(200)]
    public string ServerUrl { get; set; } = "http://localhost:5110";

    /// <summary>SignalR hub path on the web app.</summary>
    [StringLength(100)]
    public string SignalRHub { get; set; } = "/scaleHub";
}
