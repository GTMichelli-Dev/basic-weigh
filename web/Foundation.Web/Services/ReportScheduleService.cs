using Foundation.Web.Data;
using Foundation.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Foundation.Web.Services;

/// <summary>
/// Ticks once a minute and does two things: fires any scheduled report that has
/// come due, and sweeps completed loads for per-load emails (Setup → Email).
///
/// Everything is wrapped so a failure — a dead SMTP relay, a site that lost its
/// uplink, a malformed schedule — logs and waits for the next tick instead of
/// tearing down the hosted service. Sites run unattended and often offline; the
/// worker has to survive that.
/// </summary>
public class ReportScheduleService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Let the app finish starting (migrations, seeding) before the
    /// first tick touches the database.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportScheduleService> _logger;

    public ReportScheduleService(IServiceScopeFactory scopeFactory,
        ILogger<ReportScheduleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunScheduledReports(stoppingToken);
                await SweepLoadEmails(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email schedule tick failed; retrying next minute.");
            }
        }
        while (await SafeWait(timer, stoppingToken));
    }

    private async Task RunScheduledReports(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();

        var nowUtc = DateTime.UtcNow;
        var due = (await db.ReportSchedules.Where(s => s.Active).ToListAsync(ct))
            .Select(s => (Schedule: s, DueUtc: ReportSchedulePlan.LastDueUtc(s, nowUtc)))
            .Where(x => x.DueUtc != null
                        && (x.Schedule.LastRunUtc == null || x.Schedule.LastRunUtc < x.DueUtc))
            .ToList();
        if (due.Count == 0) return;

        var runner = scope.ServiceProvider.GetRequiredService<ReportEmailRunner>();
        foreach (var (schedule, dueUtc) in due)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                // The period is anchored to the occurrence that came due, not to
                // now: an appliance that was powered off overnight still sends
                // the report for the day it missed, not for the day it booted.
                var result = await runner.RunScheduleAsync(schedule, AppTimeZone.FromUtc(dueUtc!.Value),
                    markRun: true, ct);
                if (!result.Sent && result.Error != null)
                    _logger.LogWarning("Scheduled report \"{Name}\" failed: {Error}",
                        schedule.Name, result.Error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled report \"{Name}\" threw.", schedule.Name);
                await MarkFailed(db, schedule, ex.Message, ct);
            }
        }
    }

    private async Task SweepLoadEmails(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ReportEmailRunner>();
        try
        {
            await runner.SweepLoadEmailsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Per-load email sweep failed; retrying next minute.");
        }
    }

    /// <summary>A schedule that threw still gets its watermark advanced, so one
    /// broken schedule can't retry every minute forever.</summary>
    private static async Task MarkFailed(ScaleDbContext db, ReportSchedule schedule,
        string message, CancellationToken ct)
    {
        try
        {
            var row = await db.ReportSchedules.FindAsync(new object[] { schedule.Id }, ct);
            if (row == null) return;
            row.LastRunUtc = DateTime.UtcNow;
            row.LastError = message.Length > 400 ? message[..400] : message;
            await db.SaveChangesAsync(ct);
        }
        catch { /* the DB is the thing that's broken — nothing useful to do */ }
    }

    private static async Task<bool> SafeWait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
