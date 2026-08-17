using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Foundation.Web.Models;

/// <summary>
/// An HID/prox card handed to a driver at the loader. The card carries the
/// ticket fields the driver would otherwise key in at the kiosk: the loader
/// operator recalls the card by its number on a phone (Cards → Card Setup),
/// changes whatever the load needs, and issues it. The driver then presents
/// the card at a kiosk reader to weigh in and out.
///
/// Two independent flags:
///   Enabled — the card exists as a usable piece of plastic (admin enrollment).
///             A lost or retired card is disabled and stops working everywhere.
///   Issued  — the card is live for a trip. Set when the operator saves it,
///             cleared when the transaction closes (unless it recycles).
/// </summary>
public class Card
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The full card ID exactly as the reader reports it — this is the match
    /// key at the kiosk and what the loader operator types on the setup page.
    /// Enrolled by an admin on the Cards page before the card can be used.
    /// </summary>
    [Required]
    [StringLength(50)]
    [Display(Name = "Card Number")]
    public string CardNumber { get; set; } = "";

    /// <summary>Human label for the physical card ("Blue #7", "Loader 2 spare").</summary>
    [StringLength(100)]
    [Display(Name = "Description")]
    public string? Description { get; set; }

    /// <summary>Enrolled and usable. Cleared to retire a lost/damaged card.</summary>
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Live for a trip. The kiosk refuses a card that isn't issued, telling
    /// the driver to see the loader operator.
    /// </summary>
    [Display(Name = "Issued")]
    public bool Issued { get; set; }

    [Display(Name = "Issued At")]
    public DateTime? IssuedAt { get; set; }

    [StringLength(50)]
    [Display(Name = "Issued By")]
    public string? IssuedBy { get; set; }

    /// <summary>
    /// What happens when the card's transaction closes:
    ///   "Default"    — follow the site-wide AppSetup.RecycleCards gate.
    ///   "Recycle"    — stay issued with its stored values, ready for the next trip.
    ///   "Deactivate" — clear Issued; the loader operator must re-issue it.
    /// </summary>
    [StringLength(20)]
    [Display(Name = "Recycle Mode")]
    public string RecycleMode { get; set; } = "Default";

    // ===== Stored ticket fields =====
    // Same standard fields the weigh forms and kiosk collect. Any of these may
    // be null: the kiosk prompts the driver for a field that is required by the
    // kiosk prompt config but has no value on the card.

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

    [StringLength(50)]
    public string? Bin { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    // ===== Trip state =====

    /// <summary>
    /// The open ticket this card weighed in on. Set at weigh-in, cleared when
    /// the ticket closes. A card presented while this is set goes straight to
    /// the weigh-out flow — no ticket number to key in.
    /// </summary>
    [StringLength(20)]
    [Display(Name = "Open Ticket")]
    public string? OpenTicket { get; set; }

    /// <summary>Last ticket this card completed — shown on the setup page so the
    /// operator can see what the card was used for.</summary>
    [StringLength(20)]
    [Display(Name = "Last Ticket")]
    public string? LastTicket { get; set; }

    [Display(Name = "Last Used")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Whether this card recycles once its ticket closes, resolving
    /// "Default" against the site-wide gate.</summary>
    public bool RecyclesUnder(AppSetup setup) => RecycleMode switch
    {
        "Recycle" => true,
        "Deactivate" => false,
        _ => setup.RecycleCards
    };

    /// <summary>Ready for a driver to present at a kiosk.</summary>
    [NotMapped]
    public bool IsUsable => Enabled && Issued;
}

/// <summary>
/// Per-card value for a CustomField — the card equivalent of
/// TransactionCustomValue. Copied onto the ticket at weigh-in.
/// </summary>
public class CardCustomValue
{
    [Key]
    public int Id { get; set; }

    public int CardId { get; set; }

    public int CustomFieldId { get; set; }

    [StringLength(200)]
    public string? Value { get; set; }
}
