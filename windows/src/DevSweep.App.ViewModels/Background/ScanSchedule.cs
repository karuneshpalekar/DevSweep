using System.Globalization;

namespace DevSweep.App.ViewModels.Background;

public enum ScanFrequency
{
    Daily,
    Weekly,
}

/// <summary>
/// When DevSweep scans on its own (the Mac rules): daily, or weekly on a chosen day, at a chosen time.
/// A scheduled scan is read-only like every scan. It is due once the first run time after the
/// previous scan has passed, so a PC that was asleep or off at the time scans when it wakes (or when
/// DevSweep next starts); a schedule that was only just switched on waits for its first run time.
/// </summary>
public sealed record ScanSchedule
{
    public bool Enabled { get; init; }

    public ScanFrequency Frequency { get; init; } = ScanFrequency.Weekly;

    public DayOfWeek Day { get; init; } = DayOfWeek.Monday;

    public int Hour { get; init; } = 9;

    public int Minute { get; init; }

    /// <summary>When the schedule was last switched on or changed: the baseline until a scheduled scan has run.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The first run time strictly after <paramref name="after"/>, in <paramref name="zone"/> (the local time zone by default).</summary>
    public DateTimeOffset NextRun(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        int hour = Math.Clamp(Hour, 0, 23);
        int minute = Math.Clamp(Minute, 0, 59);
        DateTime local = TimeZoneInfo.ConvertTime(after, zone).DateTime.Date;
        for (int i = 0; i < 9; i++)
        {
            DateTime day = local.AddDays(i);
            if (Frequency == ScanFrequency.Weekly && day.DayOfWeek != Day)
            {
                continue;
            }

            var candidate = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddHours(1);
            }

            var result = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
            if (result > after)
            {
                return result;
            }
        }

        return after.AddDays(1);
    }

    /// <summary>True when a scan should run now.</summary>
    /// <param name="lastRun">When the last scheduled scan ran, or null if none has.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The time zone the schedule's clock time is in (local by default).</param>
    public bool IsDue(DateTimeOffset? lastRun, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (!Enabled)
        {
            return false;
        }

        DateTimeOffset? baseline = (lastRun, Since) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
        return baseline is { } from && now >= NextRun(from, zone);
    }

    public string Summary
    {
        get
        {
            string time = string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(Hour, 0, 23):00}:{Math.Clamp(Minute, 0, 59):00}");
            return Frequency == ScanFrequency.Daily ? $"Every day at {time}" : $"Every {Day} at {time}";
        }
    }
}
