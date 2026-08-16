using Foundation.Web.Data;
using Foundation.Web.Models;
using Foundation.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Foundation.Web.Controllers;

/// <summary>
/// Backs the Setup → Email tab: SMTP settings, scheduled Excel reports, and
/// per-load email rules.
///
/// Follows the Locations-tab pattern — its own JSON endpoints that save
/// immediately, rather than riding the Setup form's field-by-field auto-save
/// POST.
/// </summary>
public class EmailController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;
    private readonly EmailSender _sender;
    private readonly ReportEmailRunner _runner;

    public EmailController(ScaleDbContext db, AppSetupCache setupCache,
        EmailSender sender, ReportEmailRunner runner)
    {
        _db = db;
        _setupCache = setupCache;
        _sender = sender;
        _runner = runner;
    }

    // ===== SMTP settings =====

    [HttpGet("api/email/settings")]
    public IActionResult GetSettings()
    {
        var s = Settings();
        return Json(new
        {
            s.Id,
            s.Enabled,
            s.Host,
            s.Port,
            s.SecurityMode,
            s.Username,
            // The password itself never leaves the server — the UI only needs
            // to know whether one is stored so it can say so in the empty box.
            HasPassword = !string.IsNullOrEmpty(s.PasswordProtected),
            s.FromAddress,
            s.FromName,
            s.ReplyTo,
            s.LastError,
            LastSendUtc = s.LastSendUtc.AsUtc()
        });
    }

    public class EmailSettingsRequest
    {
        public bool Enabled { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? SecurityMode { get; set; }
        public string? Username { get; set; }

        /// <summary>Only applied when <see cref="ChangePassword"/> is set, so an
        /// ordinary save can't wipe the stored password just because the UI's
        /// password box is empty.</summary>
        public string? Password { get; set; }

        /// <summary>True when the operator actually typed in the password box.
        /// With Password empty, that means "clear it".</summary>
        public bool ChangePassword { get; set; }

        public string? FromAddress { get; set; }
        public string? FromName { get; set; }
        public string? ReplyTo { get; set; }
    }

    [HttpPut("api/email/settings")]
    public IActionResult SaveSettings([FromBody] EmailSettingsRequest? request)
    {
        if (request == null) return BadRequest(new { error = "Invalid request body." });

        var row = _db.EmailSettings.FirstOrDefault();
        if (row == null)
        {
            row = new EmailSettings { Id = 1 };
            _db.EmailSettings.Add(row);
        }

        if (request.Enabled)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return BadRequest(new { error = "SMTP server is required to enable email." });
            if (string.IsNullOrWhiteSpace(request.FromAddress))
                return BadRequest(new { error = "From address is required to enable email." });
        }
        if (!string.IsNullOrWhiteSpace(request.FromAddress)
            && !EmailRecipients.IsValidAddress(request.FromAddress.Trim()))
            return BadRequest(new { error = $"\"{request.FromAddress.Trim()}\" is not a valid email address." });
        if (!string.IsNullOrWhiteSpace(request.ReplyTo)
            && !EmailRecipients.IsValidAddress(request.ReplyTo.Trim()))
            return BadRequest(new { error = $"\"{request.ReplyTo.Trim()}\" is not a valid reply-to address." });
        if (request.Port is < 1 or > 65535)
            return BadRequest(new { error = "Port must be between 1 and 65535." });

        row.Enabled = request.Enabled;
        row.Host = Clean(request.Host) ?? "";
        row.Port = request.Port;
        row.SecurityMode = request.SecurityMode is "None" or "SslOnConnect" ? request.SecurityMode : "StartTls";
        row.Username = Clean(request.Username);
        row.FromAddress = Clean(request.FromAddress);
        row.FromName = Clean(request.FromName);
        row.ReplyTo = Clean(request.ReplyTo);

        if (request.ChangePassword)
        {
            row.PasswordProtected = string.IsNullOrEmpty(request.Password)
                ? null
                : _sender.Protect(request.Password);
        }

        _db.SaveChanges();
        return Json(new { success = true });
    }

    public class TestEmailRequest
    {
        public string? To { get; set; }
    }

    /// <summary>Sends a plain confirmation message so the operator can prove the
    /// relay works before trusting a 6am schedule to it.</summary>
    [HttpPost("api/email/test")]
    public async Task<IActionResult> SendTest([FromBody] TestEmailRequest? request)
    {
        if (request == null) return BadRequest(new { error = "Invalid request body." });

        var to = EmailRecipients.Parse(request.To);
        if (to.Count == 0) return BadRequest(new { error = "Enter an address to send the test to." });
        var invalid = EmailRecipients.FirstInvalid(request.To);
        if (invalid != null) return BadRequest(new { error = $"\"{invalid}\" is not a valid email address." });

        var settings = _db.EmailSettings.FirstOrDefault();
        if (settings == null || string.IsNullOrWhiteSpace(settings.Host)
            || string.IsNullOrWhiteSpace(settings.FromAddress))
            return BadRequest(new { error = "Save the SMTP server and From address first." });

        var company = _setupCache.Get().Header1 ?? "Foundation";
        var (sent, error) = await _sender.SendAndRecordAsync(settings, new EmailMessage
        {
            To = to,
            Subject = $"{company} — Foundation test email",
            HtmlBody = "<div style=\"font-family:sans-serif;font-size:14px;\">"
                + $"<p>This is a test message from <strong>{System.Net.WebUtility.HtmlEncode(company)}</strong>.</p>"
                + "<p>Your SMTP settings are working — scheduled reports and load emails can be delivered.</p>"
                + "</div>"
        });

        if (!sent) return BadRequest(new { error = error ?? "Send failed." });
        return Json(new { success = true });
    }

    // ===== Scheduled reports =====

    [HttpGet("api/email/schedules")]
    public IActionResult GetSchedules()
    {
        var nowUtc = DateTime.UtcNow;
        return Json(_db.ReportSchedules.OrderBy(s => s.Name).ToList().Select(s => new
        {
            s.Id,
            s.Name,
            s.Active,
            s.Recipients,
            s.Cc,
            s.Frequency,
            s.SendHour,
            s.SendMinute,
            s.DayOfWeek,
            s.DayOfMonth,
            s.GroupCommoditiesIntoSheets,
            s.IncludeGrossTare,
            s.SendWhenEmpty,
            s.CustomerFilter,
            s.CommodityFilter,
            s.SiteId,
            s.SubjectTemplate,
            LastRunUtc = s.LastRunUtc.AsUtc(),
            LastSuccessUtc = s.LastSuccessUtc.AsUtc(),
            s.LastError,
            NextRunUtc = ((DateTime?)ReportSchedulePlan.NextRunUtc(s, nowUtc)).AsUtc()
        }).ToList());
    }

    [HttpPost("api/email/schedules")]
    public IActionResult AddSchedule([FromBody] ReportSchedule? schedule)
    {
        if (schedule == null) return BadRequest(new { error = "Invalid request body." });

        var error = ValidateSchedule(schedule, null);
        if (error != null) return BadRequest(new { error });

        schedule.Id = 0;
        schedule.CreatedUtc = DateTime.UtcNow;
        schedule.LastRunUtc = null;
        schedule.LastSuccessUtc = null;
        schedule.LastError = null;
        Normalize(schedule);
        _db.ReportSchedules.Add(schedule);
        _db.SaveChanges();
        return Json(new { schedule.Id });
    }

    [HttpPut("api/email/schedules/{id:int}")]
    public IActionResult UpdateSchedule(int id, [FromBody] ReportSchedule? schedule)
    {
        if (schedule == null) return BadRequest(new { error = "Invalid request body." });

        var existing = _db.ReportSchedules.Find(id);
        if (existing == null) return NotFound();

        var error = ValidateSchedule(schedule, id);
        if (error != null) return BadRequest(new { error });

        Normalize(schedule);
        existing.Name = schedule.Name;
        existing.Active = schedule.Active;
        existing.Recipients = schedule.Recipients;
        existing.Cc = schedule.Cc;
        existing.Frequency = schedule.Frequency;
        existing.SendHour = schedule.SendHour;
        existing.SendMinute = schedule.SendMinute;
        existing.DayOfWeek = schedule.DayOfWeek;
        existing.DayOfMonth = schedule.DayOfMonth;
        existing.GroupCommoditiesIntoSheets = schedule.GroupCommoditiesIntoSheets;
        existing.IncludeGrossTare = schedule.IncludeGrossTare;
        existing.SendWhenEmpty = schedule.SendWhenEmpty;
        existing.CustomerFilter = schedule.CustomerFilter;
        existing.CommodityFilter = schedule.CommodityFilter;
        existing.SiteId = schedule.SiteId;
        existing.SubjectTemplate = schedule.SubjectTemplate;
        _db.SaveChanges();
        return Json(new { success = true });
    }

    [HttpDelete("api/email/schedules/{id:int}")]
    public IActionResult DeleteSchedule(int id)
    {
        var existing = _db.ReportSchedules.Find(id);
        if (existing == null) return NotFound();
        _db.ReportSchedules.Remove(existing);
        _db.SaveChanges();
        return Json(new { success = true });
    }

    /// <summary>Sends the schedule immediately for its normal period, without
    /// changing when it next fires on its own.</summary>
    [HttpPost("api/email/schedules/{id:int}/run")]
    public async Task<IActionResult> RunSchedule(int id)
    {
        var schedule = _db.ReportSchedules.Find(id);
        if (schedule == null) return NotFound();

        var result = await _runner.RunScheduleAsync(schedule, AppTimeZone.FromUtc(DateTime.UtcNow),
            markRun: false);

        if (result.Skipped != null) return Json(new { success = false, message = result.Skipped });
        if (!result.Sent) return BadRequest(new { error = result.Error ?? "Send failed." });
        return Json(new
        {
            success = true,
            message = $"Sent — {result.LoadCount} load{(result.LoadCount == 1 ? "" : "s")}."
        });
    }

    /// <summary>Downloads the workbook this schedule would attach, so the
    /// columns and filters can be checked without emailing anyone.</summary>
    [HttpGet("api/email/schedules/{id:int}/preview")]
    public IActionResult PreviewSchedule(int id)
    {
        var schedule = _db.ReportSchedules.Find(id);
        if (schedule == null) return NotFound();

        var (localStart, localEnd) = ReportSchedulePlan.Period(schedule, AppTimeZone.FromUtc(DateTime.UtcNow));
        var (bytes, fileName, _) = _runner.BuildWorkbook(schedule, localStart, localEnd);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    private void Normalize(ReportSchedule s)
    {
        s.Name = s.Name.Trim();
        s.Recipients = string.Join(", ", EmailRecipients.Parse(s.Recipients));
        s.Cc = Clean(s.Cc) == null ? null : string.Join(", ", EmailRecipients.Parse(s.Cc));
        s.Frequency = s.Frequency is "Weekly" or "Monthly" ? s.Frequency : "Daily";
        s.SendHour = Math.Clamp(s.SendHour, 0, 23);
        s.SendMinute = Math.Clamp(s.SendMinute, 0, 59);
        s.DayOfWeek = Math.Clamp(s.DayOfWeek, 0, 6);
        s.DayOfMonth = Math.Clamp(s.DayOfMonth, 1, 28);
        s.CustomerFilter = Clean(s.CustomerFilter);
        s.CommodityFilter = Clean(s.CommodityFilter);
        s.SubjectTemplate = Clean(s.SubjectTemplate);
        if (s.SiteId is <= 0) s.SiteId = null;
    }

    private string? ValidateSchedule(ReportSchedule s, int? id)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) return "Report name is required.";
        if (s.Name.Trim().Length > 100) return "Report name must be 100 characters or fewer.";
        if (_db.ReportSchedules.Any(x => x.Id != id && x.Name.ToLower() == s.Name.Trim().ToLower()))
            return $"A scheduled report named \"{s.Name.Trim()}\" already exists.";

        var to = EmailRecipients.Parse(s.Recipients);
        if (to.Count == 0) return "At least one recipient is required.";
        var invalid = EmailRecipients.FirstInvalid(s.Recipients) ?? EmailRecipients.FirstInvalid(s.Cc);
        if (invalid != null) return $"\"{invalid}\" is not a valid email address.";

        if (s.SiteId is > 0 && !_db.Sites.Any(x => x.Id == s.SiteId))
            return "That location no longer exists.";
        return null;
    }

    // ===== Per-load email rules =====

    [HttpGet("api/email/rules")]
    public IActionResult GetRules() =>
        Json(_db.LoadEmailRules.OrderBy(r => r.Name).ToList().Select(r => new
        {
            r.Id,
            r.Name,
            r.Active,
            r.Recipients,
            r.CustomerFilter,
            r.CommodityFilter,
            r.SubjectTemplate,
            LastSentUtc = r.LastSentUtc.AsUtc(),
            r.LastError
        }).ToList());

    [HttpPost("api/email/rules")]
    public IActionResult AddRule([FromBody] LoadEmailRule? rule)
    {
        if (rule == null) return BadRequest(new { error = "Invalid request body." });

        var error = ValidateRule(rule, null);
        if (error != null) return BadRequest(new { error });

        rule.Id = 0;
        rule.CreatedUtc = DateTime.UtcNow;
        rule.LastSentUtc = null;
        rule.LastError = null;
        NormalizeRule(rule);
        _db.LoadEmailRules.Add(rule);
        _db.SaveChanges();
        return Json(new { rule.Id });
    }

    [HttpPut("api/email/rules/{id:int}")]
    public IActionResult UpdateRule(int id, [FromBody] LoadEmailRule? rule)
    {
        if (rule == null) return BadRequest(new { error = "Invalid request body." });

        var existing = _db.LoadEmailRules.Find(id);
        if (existing == null) return NotFound();

        var error = ValidateRule(rule, id);
        if (error != null) return BadRequest(new { error });

        NormalizeRule(rule);
        existing.Name = rule.Name;
        existing.Active = rule.Active;
        existing.Recipients = rule.Recipients;
        existing.CustomerFilter = rule.CustomerFilter;
        existing.CommodityFilter = rule.CommodityFilter;
        existing.SubjectTemplate = rule.SubjectTemplate;
        _db.SaveChanges();
        return Json(new { success = true });
    }

    [HttpDelete("api/email/rules/{id:int}")]
    public IActionResult DeleteRule(int id)
    {
        var existing = _db.LoadEmailRules.Find(id);
        if (existing == null) return NotFound();
        _db.LoadEmailRules.Remove(existing);  // cascades its LoadEmailLogs rows
        _db.SaveChanges();
        return Json(new { success = true });
    }

    private void NormalizeRule(LoadEmailRule r)
    {
        r.Name = r.Name.Trim();
        r.Recipients = string.Join(", ", EmailRecipients.Parse(r.Recipients));
        r.CustomerFilter = Clean(r.CustomerFilter);
        r.CommodityFilter = Clean(r.CommodityFilter);
        r.SubjectTemplate = Clean(r.SubjectTemplate);
    }

    private string? ValidateRule(LoadEmailRule r, int? id)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Rule name is required.";
        if (r.Name.Trim().Length > 100) return "Rule name must be 100 characters or fewer.";
        if (_db.LoadEmailRules.Any(x => x.Id != id && x.Name.ToLower() == r.Name.Trim().ToLower()))
            return $"A rule named \"{r.Name.Trim()}\" already exists.";

        var to = EmailRecipients.Parse(r.Recipients);
        if (to.Count == 0) return "At least one recipient is required.";
        var invalid = EmailRecipients.FirstInvalid(r.Recipients);
        if (invalid != null) return $"\"{invalid}\" is not a valid email address.";
        return null;
    }

    // ===== Pickers =====

    /// <summary>Active customer and commodity names for the filter pickers, so
    /// a filter can't be built from a typo'd name that matches nothing.</summary>
    [HttpGet("api/email/filter-options")]
    public IActionResult GetFilterOptions() => Json(new
    {
        customers = _db.Customers.Where(c => c.Active).OrderBy(c => c.CustomerName)
            .Select(c => c.CustomerName).ToList(),
        commodities = _db.Commodities.Where(c => c.Active).OrderBy(c => c.CommodityName)
            .Select(c => c.CommodityName).ToList(),
        sites = _db.Sites.Where(s => s.Active).OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Name }).ToList()
    });

    private EmailSettings Settings() =>
        _db.EmailSettings.FirstOrDefault() ?? new EmailSettings { Id = 1 };

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
