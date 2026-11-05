using System.Globalization;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DevSweep.App.Views;

/// <summary>"Scan again" (Ctrl+R) and "Stop scan" in every page header.</summary>
internal static class ScanButtons
{
    public static UIElement Build(ScanViewModel scan, Subscriptions subs)
    {
        Button again = Ui.Button("Scan again", () => _ = scan.ScanAsync());
        again.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.R, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        Ui.Tip(again, "Scan again (Ctrl+R). Scans only look; nothing changes.");
        Button stop = Ui.Button("Stop scan", scan.Cancel);
        subs.On(scan, nameof(ScanViewModel.IsScanning), () =>
        {
            Ui.Show(again, !scan.IsScanning);
            Ui.Show(stop, scan.IsScanning);
        });
        return Ui.H(8, again, stop);
    }
}

/// <summary>What the scan is doing: the running checks and what each found.</summary>
internal static class ScanProgressView
{
    public static UIElement Build(ScanViewModel scan, Subscriptions subs, bool compact)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = compact ? 360 : double.NaN, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(bar, "Scan progress");
        TextBlock status = Ui.Secondary(string.Empty);
        status.MaxLines = 2;
        var checks = new StackPanel { Spacing = 4 };
        StackPanel panel = Ui.V(8, status, bar, compact ? null : checks);
        subs.On(scan, null, () =>
        {
            Ui.Show(panel, scan.IsScanning);
            bar.Value = scan.Progress;
            status.Text = "Scanning · " + scan.Status;
        });
        if (!compact)
        {
            subs.OnCollection(scan.Checks, () =>
            {
                checks.Children.Clear();
                foreach (CheckProgressViewModel check in scan.Checks.Take(12))
                {
                    TextBlock state = Ui.Caption(check.State);
                    subs.On(check, null, () => state.Text = check.State);
                    checks.Children.Add(Ui.Row(Ui.H(8, check.IsRunning ? new ProgressRing { Width = 14, Height = 14, IsActive = true } : Ui.Icon("", 12), Ui.Text(check.Title, maxLines: 1)), state));
                }
            });
        }

        return panel;
    }
}

/// <summary>The scan report (Mac 0.9.6): summary, per-check outcome, timings, partial sizes and folders not readable.</summary>
internal static class ScanReportView
{
    public static UIElement Build(ScanReport report, bool expanded)
    {
        var details = new StackPanel { Spacing = 4 };
        foreach (IGrouping<string, CheckReport> category in report.Checks.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            details.Children.Add(new Border { Padding = new Thickness(0, 8, 0, 0), Child = Ui.Heading(Title(category.Key)) });
            foreach (CheckReport check in category.OrderBy(c => c.Title, StringComparer.Ordinal))
            {
                TextBlock result = Ui.Text(Result(report, check), check.Outcome is CheckOutcome.TimedOut or CheckOutcome.Failed ? "DsWarningText" : "DsSecondary");
                result.TextWrapping = TextWrapping.NoWrap;
                TextBlock time = Ui.Caption(Timing(check));
                time.Width = 150;
                time.TextAlignment = TextAlignment.Right;
                UIElement row = Ui.Row(Ui.H(8, Ui.Icon(Glyph(check.Outcome), 12), Ui.Text(check.Title, maxLines: 1)), result, time);
                details.Children.Add(row);
                if (check.Error is { } error)
                {
                    details.Children.Add(Ui.Caption("    " + error));
                }

                foreach (string folder in check.AccessDenied.Take(3))
                {
                    details.Children.Add(Ui.Caption($"    Couldn't read {folder}, so anything there wasn't counted."));
                }
            }
        }

        var expander = new Expander
        {
            Header = "What each check found",
            Content = details,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(expander, "What each check found");
        var lines = new List<UIElement> { Ui.Secondary(report.Summary) };
        lines.Add(Ui.Caption(string.Create(CultureInfo.InvariantCulture,
            $"{(report.Mode == SizeMode.Overview ? "Overview sizes (alternate data streams not counted; Review measures exactly)" : "Exact sizes")} · measuring took {ScanReport.Duration(report.MeasuringSeconds)} · checks are skipped after {report.CheckTimeoutSeconds:0} s")));
        if (report.SharedBetweenFindingsBytes > 0 || report.NestedBytes > 0)
        {
            lines.Add(Ui.Caption($"Counted once in the total: {SizeLabels.Size(report.SharedBetweenFindingsBytes)} hard-linked into several items, {SizeLabels.Size(report.NestedBytes)} inside other items."));
        }

        foreach (string folder in report.AccessDenied.Take(5))
        {
            lines.Add(Ui.H(8, Ui.Icon("", 12), Ui.Caption($"Windows didn't let DevSweep read {folder}, so anything there wasn't checked. DevSweep never changes permissions.")));
        }

        foreach (Core.Rules.RuleError error in report.RuleErrors.Take(5))
        {
            lines.Add(Ui.Text(error.ToString(), "DsCriticalText"));
        }

        if (report.Disabled.Count > 0)
        {
            lines.Add(Ui.Caption("Turned off: " + string.Join(", ", report.Disabled)));
        }

        lines.Add(expander);
        return Ui.V(8, [.. lines]);
    }

    private static string Title(string yamlName)
        => Core.Rules.Categories.TryParse(yamlName, out Core.Rules.Category category) ? Core.Rules.Categories.Title(category) : yamlName;

    private static string Glyph(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Found => "",
        CheckOutcome.NothingFound => "",
        _ => "",
    };

    private static string Result(ScanReport report, CheckReport check)
    {
        string text = check.Outcome switch
        {
            CheckOutcome.Found => Words.Count(check.Found, "item") + " found",
            CheckOutcome.NothingFound => "Nothing found",
            CheckOutcome.TimedOut => string.Create(CultureInfo.InvariantCulture, $"Skipped, took over {report.CheckTimeoutSeconds:0} s"),
            _ => "Failed",
        };
        if (check.PartialSizes > 0)
        {
            text += $" · {check.PartialSizes} only partly measured";
        }

        if (check.AccessDenied.Count > 0)
        {
            text += " · " + Words.Count(check.AccessDenied.Count, "folder") + " not readable";
        }

        return text;
    }

    private static string Timing(CheckReport check)
        => string.Create(CultureInfo.InvariantCulture, $"{check.Seconds:0.00} s + {check.MeasureSeconds:0.00} s measuring");
}
