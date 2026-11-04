using System.Globalization;

namespace DevSweep.Cli.Output;

/// <summary>Human-readable output of <c>devsweep measure</c>, laid out for comparing with Explorer and fsutil.</summary>
internal static class MeasureText
{
    public static void Write(TextWriter output, MeasureReport report)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(report);

        Line(output, "Path", report.Path);
        Line(output, "Size on disk", Bytes(report.Bytes));
        Line(output, "Size (logical)", Bytes(report.LogicalBytes));
        Line(output, "Freeable", Bytes(report.FreeableBytes));
        Line(output, "Files", Count(report.Files));
        Line(output, "Folders", Count(report.Directories));
        Line(output, "Confidence", report.Confidence.ToString());
        Line(output, "Complete", report.Complete ? "yes" : "no (time limit reached; sizes are a lower bound)");

        if (report.HardLinkDuplicates > 0 || report.SharedLinkFiles > 0)
        {
            Line(output, "Hard links", $"{Count(report.HardLinkDuplicates)} extra names counted once; "
                + $"{Count(report.SharedLinkFiles)} files ({Bytes(report.SharedLinkBytes)}) also linked from elsewhere");
        }

        if (report.CloudPlaceholderCount > 0)
        {
            Line(output, "Cloud-only files", $"{Count(report.CloudPlaceholderCount)} ({Bytes(report.CloudPlaceholderLogicalBytes)} in the cloud, 0 on disk)");
        }

        if (report.AccessDenied > 0 || report.Unreadable > 0)
        {
            Line(output, "Not readable", $"{Count(report.AccessDenied)} access denied, {Count(report.Unreadable)} other errors");
        }

        Line(output, "Links not followed", Count(report.ReparsePointCount));
        foreach (ReparsePointReport link in report.ReparsePoints)
        {
            output.WriteLine($"    {link.Kind,-14} {link.Path}");
        }

        Line(output, "Elapsed", string.Create(CultureInfo.InvariantCulture, $"{report.ElapsedSeconds:0.000} s"));
    }

    private static void Line(TextWriter output, string label, string value) => output.WriteLine($"{label + ":",-20}{value}");

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = bytes;
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        string human = unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{scaled:0.##} {units[unit]}");
        return string.Create(CultureInfo.InvariantCulture, $"{human} ({bytes:N0} bytes)");
    }
}
