using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// A recurring emailed Excel report of what was weighed (Setup → Email).
/// Each schedule owns its own recipient list, so one site can send the same
/// daily summary to the office and a customer-filtered copy to that customer.
///
/// The workbook columns come from the enabled ticket fields (Setup → Fields)
/// plus net weight in pounds and the commodity's own reporting unit — see
/// WeighReportColumns. Nothing is configured column-by-column here on purpose:
/// a field hidden on the weigh forms shouldn't reappear in a report.
/// </summary>
public class ReportSchedule
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Display(Name = "Active")]
    public bool Active { get; set; } = true;

    /// <summary>One or more addresses separated by comma, semicolon or newline.
    /// Parsed by EmailRecipients.Parse.</summary>
    [Required]
    [StringLength(2000)]
    [Display(Name = "To")]
    public string Recipients { get; set; } = "";

    [StringLength(2000)]
    [Display(Name = "CC")]
    public string? Cc { get; set; }

    /// <summary>Daily | Weekly | Monthly.</summary>
    [StringLength(10)]
    [Display(Name = "Frequency")]
    public string Frequency { get; set; } = "Daily";

    /// <summary>Send time in the app's display timezone (Setup → System).</summary>
    [Display(Name = "Hour")]
    public int SendHour { get; set; } = 6;

    [Display(Name = "Minute")]
    public int SendMinute { get; set; }

    /// <summary>Weekly only: 0 = Sunday … 6 = Saturday.</summary>
    [Display(Name = "Day of Week")]
    public int DayOfWeek { get; set; } = 1;

    /// <summary>Monthly only: 1–28. Capped at 28 so February never skips a
    /// month.</summary>
    [Display(Name = "Day of Month")]
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Put each commodity on its own worksheet instead of one flat
    /// sheet. A summary sheet with per-commodity totals leads the workbook.</summary>
    [Display(Name = "Separate Worksheet per Commodity")]
    public bool GroupCommoditiesIntoSheets { get; set; }

    /// <summary>Add Gross and Tare columns alongside Net. Off gives the bare
    /// "enabled fields + net" report.</summary>
    [Display(Name = "Include Gross / Tare")]
    public bool IncludeGrossTare { get; set; } = true;

    /// <summary>Send even when the period had no loads. Off (default) skips the
    /// run silently so a quiet weekend doesn't mail an empty workbook.</summary>
    [Display(Name = "Send When Empty")]
    public bool SendWhenEmpty { get; set; }

    /// <summary>Comma-separated customer names; empty = every customer.</summary>
    [StringLength(2000)]
    [Display(Name = "Customers")]
    public string? CustomerFilter { get; set; }

    /// <summary>Comma-separated commodity names; empty = every commodity.</summary>
    [StringLength(2000)]
    [Display(Name = "Commodities")]
    public string? CommodityFilter { get; set; }

    /// <summary>Limit to loads weighed on one location's scales; null = all
    /// locations. Matched through the ticket's out/in scale name.</summary>
    [Display(Name = "Location")]
    public int? SiteId { get; set; }

    /// <summary>Optional subject override. Supports {report}, {company},
    /// {start}, {end}, {count} — see EmailTemplates.</summary>
    [StringLength(200)]
    [Display(Name = "Subject")]
    public string? SubjectTemplate { get; set; }

    /// <summary>Watermark for the due check: a schedule fires when its most
    /// recent scheduled time is newer than this. Set on both success and
    /// failure so a broken relay can't queue up a backlog of catch-up sends.</summary>
    public DateTime? LastRunUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    [StringLength(500)]
    public string? LastError { get; set; }

    /// <summary>Occurrences at or before this are never sent, so adding a
    /// schedule at noon doesn't immediately fire this morning's 6am run.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
