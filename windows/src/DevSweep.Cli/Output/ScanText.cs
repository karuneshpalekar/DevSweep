using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.Cli.Output;

/// <summary>Human-readable output of <c>devsweep scan</c>.</summary>
internal static class ScanText
{
    public static void Write(TextWriter output, ScanResult result, bool explain)
    {
        ScanReport report = result.Report;
        output.WriteLine(report.Mode == SizeMode.Overview
            ? "Overview sizes (alternate data streams not counted; use --exact, or the app re-measures before cleaning)."
            : "Exact sizes (alternate data streams counted).");
        output.WriteLine(report.Summary);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Total: {SizeText.Format(report.TotalBytes)} in {result.Findings.Count} item{(result.Findings.Count == 1 ? "" : "s")}{Overlap(report)}"));
        output.WriteLine();

        foreach (Finding finding in result.Findings)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{SizeText.Format(finding.Size),10}  {finding.Title}  [{finding.Category.Title()} · {finding.Risk} · {finding.Confidence}{(finding.Measurement == SizeMeasurement.Overview ? " · overview" : string.Empty)}]"));
            output.WriteLine($"{string.Empty,10}  {finding.Subtitle}");
            if (explain)
            {
                WriteExplanation(output, finding);
            }
        }

        output.WriteLine();
        output.WriteLine("Checks:");
        foreach (CheckReport check in report.Checks)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {check.Outcome,-12} {check.Id,-28} {check.Seconds,6:0.00} s + {check.MeasureSeconds,6:0.00} s measuring, {check.Found} found{(check.Error is { } e ? $" ({e})" : string.Empty)}"));
            if (explain)
            {
                foreach (string line in check.Locations.Concat(check.Notes))
                {
                    output.WriteLine($"      {line}");
                }
            }
        }

        foreach (SourceReport source in report.Inventory)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  inventory: {source.Title}: {source.Count} apps{(source.Complete ? string.Empty : $" (incomplete: {source.Problems[0]})")}"));
        }

        foreach (string denied in report.AccessDenied)
        {
            output.WriteLine($"  not readable: {denied}");
        }

        foreach (RuleError error in report.RuleErrors)
        {
            output.WriteLine($"  rule pack problem: {error}");
        }

        foreach (string disabled in report.Disabled)
        {
            output.WriteLine($"  turned off: {disabled}");
        }
    }

    private static void WriteExplanation(TextWriter output, Finding finding)
    {
        if (finding.Explanation is { } e)
        {
            Field(output, "What", e.What);
            Field(output, "Why", e.Why);
            Field(output, "If deleted", e.IfDeleted);
            Field(output, "Won't lose", e.WontLose);
            Field(output, "Before", e.Before);
            Field(output, "Undo", e.Undo);
            Field(output, "Better", e.Better);
        }

        foreach (Check check in finding.Checks)
        {
            output.WriteLine($"{string.Empty,12}{(check.Status == CheckStatus.Passed ? "ok" : "!!")} {check.Text}");
        }

        foreach (string path in finding.Paths)
        {
            output.WriteLine($"{string.Empty,12}-> {path}");
        }

        output.WriteLine($"{string.Empty,12}Actions: {string.Join(" | ", finding.Actions.Select(a => a.DisplayLabel))}");
        output.WriteLine();
    }

    private static void Field(TextWriter output, string label, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            output.WriteLine($"{string.Empty,12}{label}: {text}");
        }
    }

    private static string Overlap(ScanReport report)
        => report.SharedBetweenFindingsBytes + report.NestedBytes > 0
            ? $" ({SizeText.Format(report.SharedBetweenFindingsBytes + report.NestedBytes)} shared between items counted once)"
            : string.Empty;
}
