using System.Globalization;

namespace DevSweep.App.ViewModels.Background;

/// <summary>When the next scheduled scan is, in the words Home, the sidebar and Settings use.</summary>
public static class ScheduleText
{
    /// <summary>The next run time: counted from the last scheduled scan (or from when the schedule was switched on); null when scheduled scans are off.</summary>
    public static DateTimeOffset? NextRun(ScanSchedule schedule, DateTimeOffset? lastScheduledRun)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        DateTimeOffset? from = lastScheduledRun is { } last && (schedule.Since is not { } since || last > since) ? last : schedule.Since;
        return schedule.Enabled && from is { } start ? schedule.NextRun(start) : null;
    }

    /// <summary>"Monday, 9:00 AM" (today's and tomorrow's are "today, 9:00 AM" / "tomorrow, 9:00 AM"); "now" when it is due; null when scheduled scans are off.</summary>
    public static string? Next(ScanSchedule schedule, DateTimeOffset? lastScheduledRun, DateTimeOffset now)
    {
        if (NextRun(schedule, lastScheduledRun) is not { } next)
        {
            return null;
        }

        if (next <= now)
        {
            return "now";
        }

        DateTime local = next.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;
        string time = local.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (local.Date == today)
        {
            return "today, " + time;
        }

        return (local.Date == today.AddDays(1) ? "tomorrow" : local.ToString("dddd", CultureInfo.InvariantCulture)) + ", " + time;
    }

    /// <summary>"Last scan 5 minutes ago · next Monday, 9:00 AM" for the sidebar's footer.</summary>
    public static string StatusLine(bool scanning, DateTimeOffset? lastScan, ScanSchedule schedule, DateTimeOffset? lastScheduledRun, DateTimeOffset now)
    {
        if (scanning)
        {
            return "Scanning…";
        }

        string? next = Next(schedule, lastScheduledRun, now);
        if (lastScan is not { } last)
        {
            return schedule.Enabled ? "Never scanned" : "Never scanned · scheduled scans off";
        }

        string ago = "Last scan " + Text.Words.Relative(last, now);
        return next is null ? ago : ago + " · next " + next;
    }
}
