using System.ComponentModel.DataAnnotations;

namespace GateControllerService.Models;

/// <summary>
/// One controlled exit: a gate relay, a light, or both, driven off the Pi's
/// GPIO header when a ticket finishes on the scale this gate serves.
/// </summary>
public class GateConfigEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>Unique id within this service (e.g. "gate-1"). The web app
    /// addresses a gate as "serviceId:gateId", the same shape it uses for
    /// printers and cameras.</summary>
    [Required]
    [StringLength(50)]
    public string GateId { get; set; } = "";

    /// <summary>Human-friendly name shown on the web app's scale screen.</summary>
    [StringLength(100)]
    public string DisplayName { get; set; } = "";

    // ---- Outputs ----
    // Both are optional and independent: a site can wire a gate relay only, a
    // lamp only, or both. A null pin is never touched.

    /// <summary>BCM pin driving the gate relay, or null when no gate is wired.</summary>
    public int? GatePin { get; set; }

    /// <summary>BCM pin driving the light, or null when no light is wired.</summary>
    public int? LightPin { get; set; }

    /// <summary>
    /// True when the relay board is active-low, which most opto-isolated boards
    /// are. Applies to both pins — they are normally on the same board.
    /// </summary>
    public bool InvertOutputs { get; set; }

    // ---- Release ----

    /// <summary>
    /// Hardware feed of the scale that releases this gate, as
    /// "serviceId:scaleId" exactly as the reader service reports it. The gate
    /// closes once that scale reads light. Null means this gate has no weight
    /// to watch and is released by its timeout alone.
    /// </summary>
    [StringLength(100)]
    public string? ScaleHardwareId { get; set; }

    /// <summary>
    /// Below this weight the deck counts as clear and the gate closes. Wants to
    /// be well under an empty truck but above the drift and debris a deck
    /// carries when nothing is on it.
    /// </summary>
    public int ReleaseWeightThreshold { get; set; } = 1000;

    /// <summary>
    /// Hard limit on how long the output can stay energised. Reached when the
    /// truck parks on the deck, or when the weight feed dies mid-cycle — the
    /// gate must not be left open on a scale that stopped reporting.
    /// </summary>
    public int MaxOpenSeconds { get; set; } = 120;

    /// <summary>
    /// Which weighments open this gate: "WeighOut" (the default — the truck is
    /// leaving), "WeighIn", or "Both". A retained-tare weigh-in that closes the
    /// load in one pass counts as a weigh-out, because the truck is leaving.
    /// </summary>
    [StringLength(10)]
    public string TriggerOn { get; set; } = "WeighOut";

    /// <summary>Inactive gates are ignored without being deleted.</summary>
    public bool Active { get; set; } = true;
}
