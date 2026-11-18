using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.Core.Cleaning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// The ⓘ panel: what it is, why it's flagged, what was checked, if you clean it, what you
/// won't lose, before you do (with Quit buttons for apps that must close), a better option,
/// how to undo, what to do (action cards), and where it is. The primary buttons are pinned
/// in a footer so they never scroll out of view.
/// </summary>
internal static class InspectorView
{
    public static UIElement Build(MainViewModel model, FindingViewModel item, Subscriptions subs, MainWindow window)
    {
        CleanUpViewModel vm = model.CleanUp;
        Button close = Ui.IconButton(Icons.Close, "Close explanation (Esc)", () => vm.Inspected = null);
        var panel = new StackPanel { Spacing = Tokens.Space4 };
        Border risk = Ui.Badge(item.RiskTitle, item.RiskTone, item.RiskSummary);
        risk.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(Ui.Row(risk, close));
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
            var checks = new StackPanel { Spacing = Tokens.Space2 };
            checks.Children.Add(Ui.Heading("What was checked"));
            foreach (Check check in item.Checks)
            {
                checks.Children.Add(Ds.CheckRow(check.Status == CheckStatus.Passed, check.Text));
            }

            panel.Children.Add(checks);
        }

        Section(panel, "If you clean it", item.IfDeleted);
        Section(panel, "You won't lose", item.WontLose);
        if (item.Before is { Length: > 0 } || item.BlockingApps.Count > 0)
        {
            StackPanel before = Ui.V(Tokens.Space2, Ui.Heading("Before you do"));
            if (item.Before is { Length: > 0 } text)
            {
                before.Children.Add(Ui.Text(text));
            }

            if (item.BlockingApps.Count > 0)
            {
                bool one = item.BlockingApps.Count == 1;
                before.Children.Add(Ds.Banner(BannerKind.Warning, $"{string.Join(", ", item.BlockingApps.Select(QuitButtons.Pretty))} {(one ? "is" : "are")} open. {(one ? "It has" : "They have")} to close first; Review checks again."));
                before.Children.Add(QuitButtons.Build(model, window, item.BlockingApps));
            }

            panel.Children.Add(before);
        }

        if (item.Better is { Length: > 0 } better)
        {
            TextBlock heading = Ui.Text("Better option", "DsAccentText");
            panel.Children.Add(Ui.Card(Ui.V(4, heading, Ui.Text(better)), "DsBetter"));
        }

        Section(panel, "How to undo", item.Undo ?? (item.ChosenAction.Kind == CleanActionKind.Trash ? "Restore it from History while it's in the Recycle Bin or DevSweep's quarantine." : null));
        var footer = new PanelFooter("Item actions");
        panel.Children.Add(Actions(model, item, subs, footer));
        panel.Children.Add(Locations(item));
        return PanelFrame.Build(Ui.Scroll(panel, Tokens.Space6), footer);
    }

    /// <summary>"What to do" (the action cards, or the commands of a manual item) in the body; the primary buttons go in the pinned footer.</summary>
    private static StackPanel Actions(MainViewModel model, FindingViewModel item, Subscriptions subs, PanelFooter footer)
    {
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(Ui.Heading("What to do"));
        var host = new StackPanel { Spacing = 8 };
        box.Children.Add(host);

        void Pin()
        {
            HyperlinkButton ignore = Ui.Link("Always ignore this", () => model.CleanUp.Ignore(item));
            if (item.ManualCommand is { } command)
            {
                bool admin = item.Finding.Risk == Risk.NeedsAdmin;
                footer.Set(FlowButtons.Build(
                    Ui.Button("Copy", () => Shell.Copy(command), "Copy the command"),
                    Ui.Button(admin ? "Open in PowerShell (asks for admin)" : "Open in PowerShell", () => model.Backend.OpenInTerminal(item.Title, new[] { command }, admin),
                        "Open the command in a PowerShell window that waits for Enter"),
                    ignore));
                return;
            }

            if (!item.CanSelect)
            {
                footer.Set(FlowButtons.Build(ignore));
                return;
            }

            bool selected = item.IsSelected;
            Button select = Ui.Button(selected ? "Deselect" : "Select for cleaning", () => item.IsSelected = !item.IsSelected,
                selected ? $"Deselect {item.Title}" : $"Select {item.Title} for cleaning", accent: !selected);
            footer.Set(FlowButtons.Build(select, ignore));
        }

        void Render()
        {
            host.Children.Clear();
            if (item.ManualCommand is { } command)
            {
                bool admin = item.Finding.Risk == Risk.NeedsAdmin;
                host.Children.Add(Ui.Secondary(FindingViewModel.Detail(item.ChosenAction)));
                host.Children.Add(Ui.Card(Ui.Text(command, "DsMono"), "DsCodeBox"));
                host.Children.Add(Ui.H(Tokens.Space1, Ui.Icon(Icons.Lock, 12), Ui.Caption(admin
                    ? "Needs an administrator. The window lists the command and waits for Enter, then offers to reopen as administrator; Windows asks first (UAC). DevSweep never elevates itself."
                    : "The window lists the command and waits for Enter. Nothing runs before that.")));
            }

            if (item.Actions.Count > 1 || item.ManualCommand is null)
            {
                foreach (CleanAction action in item.Actions.Where(a => a.Kind != CleanActionKind.Manual || item.Actions.Count > 1))
                {
                    host.Children.Add(ActionCard(item, action));
                }
            }

            Pin();
        }

        subs.On(item, nameof(FindingViewModel.ChosenAction), Render);
        subs.On(item, nameof(FindingViewModel.IsSelected), Pin);
        return box;
    }

    /// <summary>One way to clean the item, as a selectable card with its label and what it does (the Mac's radio cards).</summary>
    private static Border ActionCard(FindingViewModel item, CleanAction action)
    {
        bool chosen = item.ChosenAction == action;
        var radio = new RadioButton
        {
            GroupName = "action-" + item.Id,
            IsChecked = chosen,
            MinWidth = 0,
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
        var card = new Border { Style = Ui.Style(chosen ? "DsChoiceSelected" : "DsChoice"), Child = radio };
        card.Tapped += (_, _) => radio.IsChecked = true;
        return card;
    }

    private static UIElement Locations(FindingViewModel item)
    {
        var list = new StackPanel { Spacing = 4 };
        for (int i = 0; i < item.Locations.Count; i++)
        {
            string path = item.Finding.Paths[i];
            list.Children.Add(Ui.Row(Selectable(Ui.Caption(item.Locations[i])), Ui.IconButton(Icons.OpenFolder, "Show in File Explorer", () => Shell.Reveal(path))));
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
