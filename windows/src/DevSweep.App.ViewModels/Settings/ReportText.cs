using System.Globalization;
using System.Runtime.InteropServices;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels.Settings;

/// <summary>
/// The text Settings, Troubleshooting copies: the Mac's scan report (each check with its time and result, how many sizes are partial,
/// which folders Windows blocked, the Windows build) and a short diagnostics block. Neither names a file or a project: blocked folders
/// are cut to their first two levels under the profile, check titles are rule titles, and error messages are left out.
/// </summary>
public static class ReportText
{
    /// <summary>"Windows 11 (10.0.26100), 64-bit".</summary>
    public static string WindowsBuild()
        => $"{RuntimeInformation.OSDescription.Trim()} ({Environment.OSVersion.Version}), {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";

    /// <summary>The scan report. With no scan it says so (after the version and Windows lines).</summary>
    public static string Scan(ScanReport? report, int findingCount, long totalBytes, VolumeSpace? disk, string version, string windows, string? profile)
    {
        var lines = new List<string> { $"DevSweep for Windows {version}", windows };
        if (disk is not null)
        {
            lines.Add($"Disk: {SizeLabels.Size(disk.FreeBytes)} free of {SizeLabels.Size(disk.TotalBytes)} (system drive)");
        }

        if (report is null)
        {
            lines.Add("No scan yet.");
            return string.Join("\n", lines);
        }

        lines.Add("Last scan: " + report.Date.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        lines.Add(report.Summary);
        lines.Add($"Found: {findingCount} items, {SizeLabels.Size(totalBytes)} ({(report.Mode == SizeMode.Exact ? "exact sizes" : "overview sizes")})");
        if (report.Disabled.Count > 0)
        {
            lines.Add("Switched off: " + string.Join(", ", report.Disabled));
        }

        lines.Add(string.Empty);
        lines.Add("Folders Windows blocked: " + (report.AccessDenied.Count == 0 ? "none" : report.AccessDenied.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (string folder in report.AccessDenied.Select(f => Redact(f, profile)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
        {
            lines.Add("  " + folder + " (blocked by Windows)");
        }

        lines.Add(string.Empty);
        lines.Add("Checks (slowest first, finding + measuring):");
        foreach (CheckReport c in report.Checks.OrderByDescending(c => c.Seconds + c.MeasureSeconds))
        {
            string result = c.Outcome switch
            {
                CheckOutcome.Found => $"{c.Found} found",
                CheckOutcome.NothingFound => "nothing found",
                CheckOutcome.TimedOut => $"SKIPPED, over {report.CheckTimeoutSeconds.ToString("0", CultureInfo.InvariantCulture)} s",
                _ => "FAILED",
            };
            if (c.PartialSizes > 0)
            {
                result += $", {c.PartialSizes} partly measured";
            }

            if (c.AccessDenied.Count > 0)
            {
                result += $", {c.AccessDenied.Count} folder{(c.AccessDenied.Count == 1 ? string.Empty : "s")} blocked";
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {c.Seconds,5:0.0} + {c.MeasureSeconds,5:0.0} s  ") + $"{c.Title}: {result}");
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"Measuring: {report.MeasuringSeconds:0.0} s · total: {report.TotalSeconds:0.0} s"));
        if (report.RuleErrors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Rule errors: " + report.RuleErrors.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var error in report.RuleErrors.Take(10))
            {
                lines.Add("  " + error);
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>Version, Windows, process, the modules and the feature switch, and where the data lives (the folder's user name left out).</summary>
    public static string Diagnostics(string version, string windows, IReadOnlyList<ModuleInfo> modules, string? featureFlags, string dataFolder, string? profile)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var lines = new List<string>
        {
            $"DevSweep for Windows {version}",
            windows,
            "Process: " + RuntimeInformation.ProcessArchitecture + (Environment.IsPrivilegedProcess ? ", running elevated" : ", not elevated (as designed)"),
            "Modules: " + string.Join(", ", modules.Select(m => $"{m.Id} {(m.State == ModuleState.On ? "on" : m.State == ModuleState.TurnedOff ? "off" : "not in this build")}")),
            "DEVSWEEP_FEATURES: " + (string.IsNullOrWhiteSpace(featureFlags) ? "(not set)" : featureFlags),
            "Data folder: " + Redact(dataFolder, profile),
        };
        return string.Join("\n", lines);
    }

    /// <summary>Puts <c>~</c> for the profile and cuts a longer path to two levels, so nothing that names a project or a person is copied.</summary>
    public static string Redact(string path, string? profile)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string rest = path;
        string root = string.Empty;
        if (!string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            root = "~";
            rest = path[profile.Length..];
        }
        else if (path.Length >= 3 && path[1] == ':')
        {
            root = path[..2];
            rest = path[2..];
        }

        string[] parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string kept = string.Join("\\", parts.Take(2));
        return root + (kept.Length > 0 ? "\\" + kept : string.Empty) + (parts.Length > 2 ? "\\…" : string.Empty);
    }
}
