using System.Globalization;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Health.Alerts;

/// <summary>Which problems are worth a notification (the Mac defaults).</summary>
public sealed record AlertSettings
{
    public bool DiskEnabled { get; init; } = true;

    /// <summary>Notify when the system drive is at least this full.</summary>
    public int DiskPercent { get; init; } = 85;

    public bool GrowthEnabled { get; init; } = true;

    /// <summary>Notify when something grew by at least this many GB (10^9 bytes) since last week.</summary>
    public int GrowthGB { get; init; } = 2;

    public bool EndOfLifeEnabled { get; init; } = true;

    public bool SecretsEnabled { get; init; } = true;

    /// <summary>
    /// Days before a problem that is still there is announced again (the app offers 3 to 7).
    /// 0 keeps the Mac defaults: 3 days for a full disk, 7 for everything else.
    /// </summary>
    public int ReminderDays { get; init; }
}

public sealed record AlertItem(string Id, string Text);

public sealed record AlertGrowth(string Title, long Bytes);

/// <summary>What the latest scan saw.</summary>
public sealed record AlertInputs
{
    public double? DiskUsedPercent { get; init; }

    public IReadOnlyList<AlertGrowth> Grew { get; init; } = [];

    public IReadOnlyList<AlertItem> EndOfLife { get; init; } = [];

    public IReadOnlyList<AlertItem> Secrets { get; init; } = [];

    /// <summary>Secret files seen by earlier scheduled scans; null on the first run, when everything is a baseline rather than "new".</summary>
    public IReadOnlySet<string>? KnownSecretIds { get; init; }

    /// <summary>End-of-life installs (not part of an IDE or CI) from a Health result.</summary>
    public static IReadOnlyList<AlertItem> EndOfLifeFrom(VersionsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Runtimes
            .SelectMany(r => r.Installs.Where(i => i.Support == SupportStatus.EndOfLife && !i.IsSystem)
                .Select(i => new AlertItem($"eol:{r.Id}:{i.Version}:{i.Path}", $"{i.Label(r.Name)} no longer gets security fixes.")))
            .ToList();
    }

    /// <summary>Secrets needing attention (not the ones already handled, like an ignored .env).</summary>
    public static IReadOnlyList<AlertItem> SecretsFrom(SecurityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Findings.Where(f => f.Level != SecurityLevel.Ok).Select(f => new AlertItem(f.Id, $"{f.Title}: {f.Path}")).ToList();
    }

    /// <summary>How full the drive holding <paramref name="path"/> is, or null when it cannot be read.</summary>
    public static double? DiskUsedPercentOf(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path);
            return drive.TotalSize > 0 ? 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

public enum AlertTarget
{
    CleanUp,
    HealthTools,
    HealthSecurity,
}

public sealed record AlertEvent(string Key, string Title, string Body, AlertTarget Target);

/// <summary>Remembers what was already announced, so the same problem isn't repeated every scan.</summary>
public sealed class AlertLedger
{
    public Dictionary<string, DateTimeOffset> Notified { get; init; } = new(StringComparer.Ordinal);

    public bool ShouldNotify(string key, DateTimeOffset now, double cooldownDays)
        => !Notified.TryGetValue(key, out DateTimeOffset last) || (now - last).TotalDays >= cooldownDays;
}

/// <summary>
/// Decides what to notify about (the Mac rules): each problem is announced once, then again only
/// after a cool-down (3 days for a full disk, 7 for the rest) if it is still there. A disk that drops
/// below the limit is forgotten, so crossing it again is announced at once. New secrets are only "new"
/// against a baseline from an earlier run.
/// </summary>
public static class AlertEvaluator
{
    public const double DiskCooldownDays = 3;
    public const double OtherCooldownDays = 7;

    public static IReadOnlyList<AlertEvent> Evaluate(AlertInputs input, AlertSettings settings, AlertLedger ledger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ledger);
        var events = new List<AlertEvent>();
        double Cooldown(double fallback) => settings.ReminderDays > 0 ? settings.ReminderDays : fallback;
        void Emit(string key, double cooldown, Func<AlertEvent> make)
        {
            if (ledger.ShouldNotify(key, now, cooldown))
            {
                ledger.Notified[key] = now;
                events.Add(make());
            }
        }

        if (settings.DiskEnabled && input.DiskUsedPercent is { } used)
        {
            if (used >= settings.DiskPercent)
            {
                Emit("disk", Cooldown(DiskCooldownDays), () => new AlertEvent("disk", string.Create(CultureInfo.InvariantCulture, $"Your disk is {Math.Round(used):0}% full"),
                    "DevSweep can show what's safe to clean.", AlertTarget.CleanUp));
            }
            else
            {
                ledger.Notified.Remove("disk");
            }
        }

        if (settings.GrowthEnabled)
        {
            long limit = settings.GrowthGB * 1_000_000_000L;
            List<AlertGrowth> big = input.Grew.Where(g => g.Bytes >= limit).OrderByDescending(g => g.Bytes).ToList();
            if (big.Count > 0)
            {
                string key = "growth:" + string.Join(',', big.Select(g => g.Title).Order(StringComparer.Ordinal));
                Emit(key, Cooldown(OtherCooldownDays), () => new AlertEvent(key, $"{big[0].Title} grew by {SizeText.Format(big[0].Bytes)}",
                    big.Count == 1 ? "It may be worth a look in Clean up." : string.Create(CultureInfo.InvariantCulture, $"{big.Count - 1} other item{(big.Count == 2 ? string.Empty : "s")} grew a lot too."),
                    AlertTarget.CleanUp));
            }
        }

        if (settings.EndOfLifeEnabled && input.EndOfLife.Count > 0)
        {
            string key = "eol:" + string.Join(',', input.EndOfLife.Select(i => i.Id).Order(StringComparer.Ordinal));
            Emit(key, Cooldown(OtherCooldownDays), () => new AlertEvent(key,
                input.EndOfLife.Count == 1 ? "A tool you use is past end of life" : string.Create(CultureInfo.InvariantCulture, $"{input.EndOfLife.Count} tools you use are past end of life"),
                input.EndOfLife[0].Text, AlertTarget.HealthTools));
        }

        if (settings.SecretsEnabled && input.KnownSecretIds is { } known)
        {
            List<AlertItem> fresh = input.Secrets.Where(s => !known.Contains(s.Id)).ToList();
            if (fresh.Count > 0)
            {
                string key = "secrets:" + string.Join(',', fresh.Select(s => s.Id).Order(StringComparer.Ordinal));
                Emit(key, Cooldown(OtherCooldownDays), () => new AlertEvent(key,
                    fresh.Count == 1 ? "A new file looks like a secret" : string.Create(CultureInfo.InvariantCulture, $"{fresh.Count} new files look like secrets"),
                    fresh[0].Text, AlertTarget.HealthSecurity));
            }
        }

        return events;
    }
}
