using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// Emails one message per completed load (Setup → Email). Filter by customer,
/// by commodity, by both, or by neither — an unfiltered rule mails every load.
/// Several rules can match the same ticket; each sends its own email, so a
/// customer and a commodity broker can both be notified.
///
/// Details go in the message body (no attachment): ticket, the enabled ticket
/// fields, net pounds and the commodity's reporting unit.
/// </summary>
public class LoadEmailRule
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Display(Name = "Active")]
    public bool Active { get; set; } = true;

    /// <summary>One or more addresses separated by comma, semicolon or newline.</summary>
    [Required]
    [StringLength(2000)]
    [Display(Name = "To")]
    public string Recipients { get; set; } = "";

    /// <summary>Comma-separated customer names; empty = any customer.</summary>
    [StringLength(2000)]
    [Display(Name = "Customers")]
    public string? CustomerFilter { get; set; }

    /// <summary>Comma-separated commodity names; empty = any commodity.</summary>
    [StringLength(2000)]
    [Display(Name = "Commodities")]
    public string? CommodityFilter { get; set; }

    /// <summary>Optional subject override. Supports {ticket}, {customer},
    /// {commodity}, {net}, {units}, {company} — see EmailTemplates.</summary>
    [StringLength(200)]
    [Display(Name = "Subject")]
    public string? SubjectTemplate { get; set; }

    /// <summary>Loads that completed at or before this are never mailed, so a
    /// new rule doesn't blast the whole ticket history.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSentUtc { get; set; }

    [StringLength(500)]
    public string? LastError { get; set; }
}

/// <summary>
/// One (ticket, rule) send attempt. Doubles as the de-duplication key — the
/// sweep in ReportScheduleService only considers loads with no row here, so a
/// restart mid-sweep can't double-send. Failed attempts stay with Success =
/// false and are retried until MaxAttempts.
/// </summary>
public class LoadEmailLog
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(20)]
    public string Ticket { get; set; } = "";

    public int RuleId { get; set; }

    public bool Success { get; set; }

    public int Attempts { get; set; }

    public DateTime LastAttemptUtc { get; set; }

    [StringLength(500)]
    public string? Error { get; set; }
}
