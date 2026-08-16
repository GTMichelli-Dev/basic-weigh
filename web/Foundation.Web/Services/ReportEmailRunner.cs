using Foundation.Web.Data;
using Foundation.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Foundation.Web.Services;

/// <summary>Outcome of one schedule run, for the Setup tab's status line.</summary>
public record ScheduleRunResult(bool Sent, int LoadCount, string? Error, string? Skipped = null);

/// <summary>
/// Runs a scheduled report end to end (query → workbook → email) and sweeps
/// completed loads for per-load emails.
///
/// Scoped so it can be injected into EmailController for "Run now" and
/// resolved per tick by ReportScheduleService — both paths go through the same
/// code, so what the operator tests by hand is what fires at 6am.
/// </summary>
public class ReportEmailRunner
{
    /// <summary>Give edits made right after weigh-out a chance to land before
    /// the ticket is mailed — an operator fixing a mistyped customer has a
    /// couple of minutes before the email goes out.</summary>
    private static readonly TimeSpan LoadEmailGrace = TimeSpan.FromMinutes(2);

    /// <summary>How far back the sweep looks. A site that was offline for
    /// longer sends what it can and lets the rest go rather than mailing a
    /// month of stale loads when the uplink returns.</summary>
    private static readonly TimeSpan LoadEmailLookback = TimeSpan.FromDays(7);

    /// <summary>Retries per (ticket, rule) before giving up. A bad recipient
    /// address shouldn't be retried forever.</summary>
    private const int MaxAttempts = 5;

    private readonly ScaleDbContext _db;
    private readonly AppSetupCache _setupCache;
    private readonly EmailSender _sender;
    private readonly ILogger<ReportEmailRunner> _logger;

    public ReportEmailRunner(ScaleDbContext db, AppSetupCache setupCache,
        EmailSender sender, ILogger<ReportEmailRunner> logger)
    {
        _db = db;
        _setupCache = setupCache;
        _sender = sender;
        _logger = logger;
    }

    // ===== Scheduled reports =====

    /// <summary>Builds the workbook for a schedule's period without sending it.
    /// Backs the Setup tab's Preview button so an operator can see the columns
    /// and filters before trusting a schedule.</summary>
    public (byte[] Bytes, string FileName, int LoadCount) BuildWorkbook(
        ReportSchedule schedule, DateTime localStart, DateTime localEndExclusive)
    {
        var setup = _setupCache.Get();
        var rows = LoadRows(schedule, setup, localStart, localEndExclusive);
        var columns = Columns(setup, schedule.IncludeGrossTare);

        var bytes = WeighReportWorkbook.Build(
            schedule.Name,
            setup.Header1 ?? "Foundation",
            localStart, localEndExclusive,
            rows, columns,
            schedule.GroupCommoditiesIntoSheets);

        return (bytes, FileName(schedule, localStart, localEndExclusive), rows.Count);
    }

    /// <summary>
    /// Runs one schedule for the period ending at <paramref name="runLocal"/>.
    /// </summary>
    /// <param name="markRun">True for the worker (advances LastRunUtc so the
    /// occurrence isn't repeated); false for a manual "Run now", which sends
    /// without disturbing the schedule's own cadence.</param>
    public async Task<ScheduleRunResult> RunScheduleAsync(
        ReportSchedule schedule, DateTime runLocal, bool markRun, CancellationToken ct = default)
    {
        var settings = _sender.GetUsableSettings(_db);
        if (settings == null)
            return new ScheduleRunResult(false, 0, "Email is not enabled or not fully configured (Setup → Email).");

        var setup = _setupCache.Get();
        var (localStart, localEnd) = ReportSchedulePlan.Period(schedule, runLocal);
        var rows = LoadRows(schedule, setup, localStart, localEnd);

        if (rows.Count == 0 && !schedule.SendWhenEmpty)
        {
            if (markRun) await MarkRun(schedule, DateTime.UtcNow, null, ct);
            return new ScheduleRunResult(false, 0, null, "No loads in the period; \"Send when empty\" is off.");
        }

        var company = setup.Header1 ?? "Foundation";
        var columns = Columns(setup, schedule.IncludeGrossTare);
        var bytes = WeighReportWorkbook.Build(schedule.Name, company, localStart, localEnd,
            rows, columns, schedule.GroupCommoditiesIntoSheets);
        var fileName = FileName(schedule, localStart, localEnd);

        var message = new EmailMessage
        {
            To = EmailRecipients.Parse(schedule.Recipients),
            Cc = EmailRecipients.Parse(schedule.Cc),
            Subject = EmailTemplates.ScheduleSubject(schedule, company, localStart, localEnd, rows.Count),
            HtmlBody = EmailTemplates.ScheduleBody(schedule, company, localStart, localEnd, rows, fileName),
            AttachmentBytes = bytes,
            AttachmentName = fileName
        };

        var (sent, error) = await _sender.SendAndRecordAsync(settings, message, ct);

        // LastRunUtc advances on failure too: the run is recorded as attempted
        // so a dead relay doesn't re-send the same period every tick.
        if (markRun) await MarkRun(schedule, DateTime.UtcNow, error, ct);
        else await RecordManualResult(schedule, error, ct);

        if (sent)
            _logger.LogInformation("Scheduled report \"{Name}\" sent to {Count} recipient(s), {Loads} load(s).",
                schedule.Name, message.To.Count, rows.Count);

        return new ScheduleRunResult(sent, rows.Count, error);
    }

    private List<WeighRow> LoadRows(ReportSchedule schedule, AppSetup setup,
        DateTime localStart, DateTime localEndExclusive) =>
        WeighReportQuery.Load(_db, setup,
            AppTimeZone.ToUtc(localStart), AppTimeZone.ToUtc(localEndExclusive),
            schedule.CustomerFilter, schedule.CommodityFilter, schedule.SiteId);

    private List<WeighColumn> Columns(AppSetup setup, bool includeGrossTare) =>
        WeighReportColumns.Build(setup, _db.CustomFields.AsNoTracking().ToList(), includeGrossTare);

    private static string FileName(ReportSchedule schedule, DateTime localStart, DateTime localEndExclusive)
    {
        var slug = new string(schedule.Name
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');
        if (slug.Length == 0) slug = "WeighReport";
        var lastDay = localEndExclusive.Date.AddDays(-1);
        var range = lastDay <= localStart.Date
            ? localStart.ToString("yyyy-MM-dd")
            : $"{localStart:yyyy-MM-dd}_to_{lastDay:yyyy-MM-dd}";
        return $"{slug}_{range}.xlsx";
    }

    private async Task MarkRun(ReportSchedule schedule, DateTime nowUtc, string? error, CancellationToken ct)
    {
        var row = await _db.ReportSchedules.FindAsync(new object[] { schedule.Id }, ct);
        if (row == null) return;
        row.LastRunUtc = nowUtc;
        row.LastError = error;
        if (error == null) row.LastSuccessUtc = nowUtc;
        await _db.SaveChangesAsync(ct);
    }

    private async Task RecordManualResult(ReportSchedule schedule, string? error, CancellationToken ct)
    {
        var row = await _db.ReportSchedules.FindAsync(new object[] { schedule.Id }, ct);
        if (row == null) return;
        row.LastError = error;
        if (error == null) row.LastSuccessUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ===== Per-load emails =====

    /// <summary>
    /// Mails every completed load that a rule matches and that hasn't been
    /// mailed for that rule yet.
    ///
    /// This runs as a sweep rather than a hook on weigh-out because loads
    /// complete through half a dozen paths (weigh out, basic ticket, retained
    /// tare, kiosk, the tables API) and because a send must not be able to slow
    /// down or fail a weighment. The LoadEmailLogs rows are the de-dup key, so
    /// a restart mid-sweep can't double-send.
    /// </summary>
    public async Task SweepLoadEmailsAsync(CancellationToken ct = default)
    {
        var rules = await _db.LoadEmailRules.Where(r => r.Active).ToListAsync(ct);
        if (rules.Count == 0) return;

        var settings = _sender.GetUsableSettings(_db);
        if (settings == null) return;

        var nowUtc = DateTime.UtcNow;
        var cutoff = nowUtc - LoadEmailGrace;
        var earliest = rules.Min(r => r.CreatedUtc);
        var floor = nowUtc - LoadEmailLookback;
        if (earliest > floor) floor = earliest;

        var candidates = await _db.Transactions
            .Where(t => t.DateOut != null && !t.Void && t.DateOut > floor && t.DateOut <= cutoff)
            .OrderBy(t => t.DateOut)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var tickets = candidates.Select(t => t.Ticket).ToList();
        var logs = await _db.LoadEmailLogs
            .Where(l => tickets.Contains(l.Ticket))
            .ToListAsync(ct);
        var logByKey = logs.ToDictionary(l => (l.Ticket, l.RuleId));

        var setup = _setupCache.Get();
        var company = setup.Header1 ?? "Foundation";
        var columns = WeighReportColumns.Build(setup, _db.CustomFields.AsNoTracking().ToList(), true);
        var rows = WeighReportQuery.Hydrate(_db, setup, candidates);

        foreach (var row in rows)
        {
            foreach (var rule in rules)
            {
                if (ct.IsCancellationRequested) return;
                if (row.Transaction.DateOut <= rule.CreatedUtc) continue;
                if (!Matches(rule, row.Transaction)) continue;

                var log = logByKey.GetValueOrDefault((row.Transaction.Ticket, rule.Id));
                if (log is { Success: true }) continue;
                if (log != null && log.Attempts >= MaxAttempts) continue;

                var message = new EmailMessage
                {
                    To = EmailRecipients.Parse(rule.Recipients),
                    Subject = EmailTemplates.LoadSubject(rule, row, company),
                    HtmlBody = EmailTemplates.LoadBody(row, company, columns)
                };

                var (sent, error) = await _sender.SendAndRecordAsync(settings, message, ct);

                if (log == null)
                {
                    log = new LoadEmailLog { Ticket = row.Transaction.Ticket, RuleId = rule.Id };
                    _db.LoadEmailLogs.Add(log);
                }
                log.Success = sent;
                log.Attempts++;
                log.LastAttemptUtc = DateTime.UtcNow;
                log.Error = error;

                var ruleRow = await _db.LoadEmailRules.FindAsync(new object[] { rule.Id }, ct);
                if (ruleRow != null)
                {
                    ruleRow.LastError = error;
                    if (sent) ruleRow.LastSentUtc = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync(ct);

                if (sent)
                    _logger.LogInformation("Load email \"{Rule}\" sent for ticket {Ticket}.",
                        rule.Name, row.Transaction.Ticket);

                // A relay that just refused one message will refuse the rest of
                // this tick too; stop and let the next sweep retry.
                if (!sent) return;
            }
        }
    }

    /// <summary>An empty filter matches everything, so a rule can filter by
    /// customer, by commodity, by both, or by neither.</summary>
    public static bool Matches(LoadEmailRule rule, Transaction t)
    {
        var customers = EmailRecipients.SplitNames(rule.CustomerFilter);
        if (customers.Count > 0 &&
            !customers.Contains(t.Customer ?? "", StringComparer.OrdinalIgnoreCase))
            return false;

        var commodities = EmailRecipients.SplitNames(rule.CommodityFilter);
        if (commodities.Count > 0 &&
            !commodities.Contains(t.Commodity ?? "", StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
