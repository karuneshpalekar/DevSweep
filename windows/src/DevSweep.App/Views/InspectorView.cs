using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.Core.Cleaning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// The ⓘ panel: what it is, why it's flagged, what was checked, if you clean it, what you
/// won't lose, before you do, a better option, how to undo, what to do, and where it is.
/// </summary>
internal static class InspectorView
{
    public static UIElement Build(MainViewModel model, FindingViewModel item, Subscriptions subs, MainWindow window)
    {
        CleanUpViewModel vm = model.CleanUp;
        Button close = Ui.IconButton("", "Close explanation (Esc)", () => vm.Inspected = null);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Ui.Row(Ui.Badge(item.RiskTitle, item.RiskTone, item.RiskSummary), close));
        panel.Children.Add(Ui.V(4,
            Ui.Size(item.SizeText, item.Badges, item.SizeTooltip, "DsSubtitle"),
            Ui.Text(item.Title, "DsSubtitle"),
            Selectable(Ui.Caption(item.Subtitle))));
        if (item.Badges.Count > 0)
        {
            panel.Children.Add(Ui.Caption(item.SizeTooltip));
        }

        Section(panel, "What it is", item.What);
        Section(panel, "Why it's flagged", item.Why);
        if (item.Checks.Count > 0)
        {
            var checks = new StackPanel { Spacing = 6 };
            checks.Children.Add(Ui.Heading("What was checked"));
            foreach (Check check in item.Checks)
            {
                FontIcon icon = Ui.Icon(check.Status == CheckStatus.Passed ? "" : "", 12);
                icon.VerticalAlignment = VerticalAlignment.Top;
                icon.Margin = new Thickness(0, 3, 0, 0);
                TextBlock text = Ui.Text(check.Text, check.Status == CheckStatus.Passed ? "DsBody" : "DsWarningText");
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(text, 1);
                row.Children.Add(icon);
                row.Children.Add(text);
                AutomationProperties.SetName(row, (check.Status == CheckStatus.Passed ? "Checked: " : "Warning: ") + check.Text);
                checks.Children.Add(row);
            }

            panel.Children.Add(checks);
        }

        Section(panel, "If you clean it", item.IfDeleted);
        Section(panel, "You won't lose", item.WontLose);
        Section(panel, "Before you do", item.Before);
        if (item.BlockingApps.Count > 0)
        {
            panel.Children.Add(Ui.Card(Ui.Text($"{string.Join(", ", item.BlockingApps)} is open. Close it first; Review checks again.", "DsWarningText"), "DsWarningBox"));
        }

        if (item.Better is { Length: > 0 } better)
        {
            TextBlock heading = Ui.Text("Better option", "DsAccentText");
            panel.Children.Add(Ui.Card(Ui.V(4, heading, Ui.Text(better)), "DsBetter"));
        }

        Section(panel, "How to undo", item.Undo ?? (item.ChosenAction.Kind == CleanActionKind.Trash ? "Restore it from History while it's in the Recycle Bin or DevSweep's quarantine." : null));
        panel.Children.Add(Actions(model, item, subs));
        panel.Children.Add(Locations(item));
        return Ui.Scroll(panel, 20);
    }

    private static UIElement Actions(MainViewModel model, FindingViewModel item, Subscriptions subs)
    {
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(Ui.Heading("What to do"));
        var host = new StackPanel { Spacing = 8 };
        box.Children.Add(host);
        void Render()
        {
            host.Children.Clear();
            if (item.ManualCommand is { } command)
            {
                host.Children.Add(Ui.Secondary(FindingViewModel.Detail(item.ChosenAction)));
                host.Children.Add(Ui.Card(Ui.Text(command, "DsMono"), "DsCodeBox"));
                bool admin = item.Finding.Risk == Risk.NeedsAdmin;
                host.Children.Add(Ui.H(8,
                    Ui.Button("Copy", () => Shell.Copy(command), "Copy the command"),
                    Ui.Button(admin ? "Open in PowerShell (asks for admin)" : "Open in PowerShell", () => model.Backend.OpenInTerminal(item.Title, new[] { command }, admin),
                        "Open the command in a PowerShell window that waits for Enter")));
                host.Children.Add(Ui.Caption(admin
                    ? "The window lists the command and waits for Enter. It offers to reopen as administrator; Windows asks first (UAC). DevSweep never elevates itself."
                    : "The window lists the command and waits for Enter. Nothing runs before that."));
            }

            if (item.Actions.Count > 1 || item.ManualCommand is null)
            {
                foreach (CleanAction action in item.Actions.Where(a => a.Kind != CleanActionKind.Manual || item.Actions.Count > 1))
                {
                    var radio = new RadioButton
                    {
                        GroupName = "action-" + item.Id,
                        IsChecked = item.ChosenAction == action,
                        Content = Ui.V(2, Ui.Text(action.DisplayLabel, "DsStrong"), Ui.Caption(FindingViewModel.Detail(action))),
                    };
                    AutomationProperties.SetName(radio, action.DisplayLabel);
                    radio.Checked += (_, _) =>
                    {
                        item.ChosenAction = action;
                        if (action.Kind != CleanActionKind.Manual)
                        {
                            item.IsSelected = true;
                        }
                    };
                    host.Children.Add(radio);
                }
            }

            host.Children.Add(Ui.Link("Always ignore this", () => model.CleanUp.Ignore(item)));
        }

        subs.On(item, nameof(FindingViewModel.ChosenAction), Render);
        return box;
    }

    private static UIElement Locations(FindingViewModel item)
    {
        var list = new StackPanel { Spacing = 4 };
        for (int i = 0; i < item.Locations.Count; i++)
        {
            string path = item.Finding.Paths[i];
            list.Children.Add(Ui.Row(Selectable(Ui.Caption(item.Locations[i])), Ui.IconButton("", "Show in File Explorer", () => Shell.Reveal(path))));
        }

        var expander = new Expander
        {
            Header = item.Locations.Count == 1 ? "1 location" : $"{item.Locations.Count} locations",
            Content = list,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(expander, "Locations");
        return expander;
    }

    private static TextBlock Selectable(TextBlock text)
    {
        text.IsTextSelectionEnabled = true;
        return text;
    }

    private static void Section(StackPanel panel, string title, string? body)
    {
        if (body is { Length: > 0 })
        {
            panel.Children.Add(Ui.V(4, Ui.Heading(title), Ui.Text(body)));
        }
    }
}
