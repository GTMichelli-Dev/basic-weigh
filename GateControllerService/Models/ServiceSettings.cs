namespace GateControllerService.Models;

/// <summary>
/// Where this service reports to, and what it calls itself. One row, edited
/// through the API or seeded from appsettings.json on first run.
/// </summary>
public class ServiceSettings
{
    public int Id { get; set; }

    /// <summary>Names this box on the web app. Gates are addressed as
    /// "serviceId:gateId", so two Pis at one site must not share it.</summary>
    public string ServiceId { get; set; } = "default";

    public string ServerUrl { get; set; } = "http://localhost:5110";

    public string SignalRHub { get; set; } = "/scaleHub";
}
