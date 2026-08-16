using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// When a schedule fires and what period it covers. All the date arithmetic
/// happens in the app's display timezone (Setup → System) and converts to UTC
/// only at the boundaries, so a 6am daily report lands at 6am local across DST.
/// </summary>
public static class ReportSchedulePlan
{
    /// <summary>
    /// The most recent time this schedule was due, at or before <paramref name="nowUtc"/>,
    /// or null when it hasn't come due since it was created.
    ///
    /// Only the latest occurrence is returned, never a backlog: an appliance
    /// that was powered off for a week sends one report when it comes back, not
    /// seven.
    /// </summary>
    public static DateTime? LastDueUtc(ReportSchedule schedule, DateTime nowUtc)
    {
        var now = AppTimeZone.FromUtc(nowUtc);
        var time = new TimeSpan(Clamp(schedule.SendHour, 0, 23), Clamp(schedule.SendMinute, 0, 59), 0);

        DateTime localDue;
        switch (schedule.Frequency)
        {
            case "Weekly":
            {
                var targetDow = (DayOfWeek)Clamp(schedule.DayOfWeek, 0, 6);
                // Walk back to the most recent occurrence of the target weekday.
                var daysBack = ((int)now.DayOfWeek - (int)targetDow + 7) % 7;
                localDue = now.Date.AddDays(-daysBack) + time;
                if (localDue > now) localDue = localDue.AddDays(-7);
                break;
            }
            case "Monthly":
            {
                // Capped at 28 so every month has the day.
                var day = Clamp(schedule.DayOfMonth, 1, 28);
                localDue = new DateTime(now.Year, now.Month, day) + time;
                if (localDue > now) localDue = localDue.AddMonths(-1);
                break;
            }
            default: // Daily
            {
                localDue = now.Date + time;
                if (localDue > now) localDue = localDue.AddDays(-1);
                break;
            }
        }

        var dueUtc = AppTimeZone.ToUtc(localDue);

        // Occurrences from before the schedule existed are not "missed" runs.
        if (dueUtc <= schedule.CreatedUtc) return null;
        return dueUtc;
    }

    /// <summary>
    /// The prior completed period a run covers, in local time, as
    /// [start, endExclusive).
    ///
    /// Daily = yesterday. Weekly = the seven days ending at midnight of the run
    /// date. Monthly = the previous calendar month. Periods never overlap at a
    /// given frequency, so every load lands in exactly one report.
    /// </summary>
    public static (DateTime LocalStart, DateTime LocalEndExclusive) Period(
        ReportSchedule schedule, DateTime runLocal)
    {
        var endExclusive = runLocal.Date;
        return schedule.Frequency switch
        {
            "Weekly" => (endExclusive.AddDays(-7), endExclusive),
            "Monthly" => (new DateTime(endExclusive.Year, endExclusive.Month, 1).AddMonths(-1),
                          new DateTime(endExclusive.Year, endExclusive.Month, 1)),
            _ => (endExclusive.AddDays(-1), endExclusive)
        };
    }

    /// <summary>The next time this schedule will fire, for display on the Setup
    /// tab. Always a future time.</summary>
    public static DateTime NextRunUtc(ReportSchedule schedule, DateTime nowUtc)
    {
        var now = AppTimeZone.FromUtc(nowUtc);
        var time = new TimeSpan(Clamp(schedule.SendHour, 0, 23), Clamp(schedule.SendMinute, 0, 59), 0);

        DateTime next;
        switch (schedule.Frequency)
        {
            case "Weekly":
            {
                var targetDow = (DayOfWeek)Clamp(schedule.DayOfWeek, 0, 6);
                var daysAhead = ((int)targetDow - (int)now.DayOfWeek + 7) % 7;
                next = now.Date.AddDays(daysAhead) + time;
                if (next <= now) next = next.AddDays(7);
                break;
            }
            case "Monthly":
            {
                var day = Clamp(schedule.DayOfMonth, 1, 28);
                next = new DateTime(now.Year, now.Month, day) + time;
                if (next <= now) next = next.AddMonths(1);
                break;
            }
            default:
            {
                next = now.Date + time;
                if (next <= now) next = next.AddDays(1);
                break;
            }
        }

        return AppTimeZone.ToUtc(next);
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
