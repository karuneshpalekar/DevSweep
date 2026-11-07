using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// The last stop before anything changes: exact sizes, items grouped by what will actually
/// happen, the exact operations, blockers, a "can't be undone" warning, then outcomes with
/// freed-space reconciliation.
/// </summary>
internal static class ReviewSheet
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        ReviewViewModel vm = model.Review;
        var root = new Grid { Width = 680 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.MaxHeight = Math.Max(420, window.Root.ActualHeight - 80);

        TextBlock heading = Ui.Text(string.Empty, "DsSubtitle");
        TextBlock subheading = Ui.Secondary(string.Empty);
        var header = new Border { Padding = new Thickness(24, 22, 24, 12), Child = Ui.V(4, heading, subheading) };
        root.Children.Add(header);

        var body = new StackPanel { Spacing = 18 };
        var scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(24, 0, 24, 16), Child = body }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        TextBlock footer = Ui.Secondary(string.Empty);
        Button cancel = Ui.Button("Cancel", vm.Close);
        Button clean = Ui.Button("Clean", () => _ = vm.CleanAsync(), accent: true);
        Button history = Ui.Button("Show History", () =>
        {
            vm.Close();
            model.Go("history");
        });
        Button done = Ui.Button("Done", vm.Close, accent: true);
        var footerBar = new Border
        {
            Style = Ui.Style("DsFooterBar"),
            Child = Ui.Row(footer, cancel, clean, history, done),
        };
        Grid.SetRow(footerBar, 2);
        root.Children.Add(footerBar);

        void Render()
        {
            heading.Text = vm.Heading;
            subheading.Text = vm.Subheading;
            body.Children.Clear();
            ReviewStage stage = vm.Stage;
            Ui.Show(cancel, stage is ReviewStage.Measuring or ReviewStage.Ready);
            Ui.Show(clean, stage is ReviewStage.Ready or ReviewStage.Cleaning);
            Ui.Show(history, stage == ReviewStage.Done);
            Ui.Show(done, stage == ReviewStage.Done);
            clean.IsEnabled = vm.CleanCommand.CanExecute(null);
            clean.Content = stage == ReviewStage.Cleaning ? "Cleaning…" : vm.CleanLabel;
            AutomationProperties.SetName(clean, (string)clean.Content);
            footer.Text = stage == ReviewStage.Done ? "Every change is logged in History." : vm.Footer;
            footer.Style = Ui.Style(vm.FooterIsWarning && stage != ReviewStage.Done ? "DsWarningText" : "DsSecondary");
            switch (stage)
            {
                case ReviewStage.Measuring:
                case ReviewStage.Cleaning:
                    var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = vm.Progress };
                    AutomationProperties.SetName(bar, stage == ReviewStage.Measuring ? "Measuring progress" : "Cleaning progress");
                    body.Children.Add(Ui.V(8, Ui.Secondary(vm.Status), bar));
                    if (stage == ReviewStage.Cleaning)
                    {
                        RenderGroups(vm, body);
                    }

                    break;
                case ReviewStage.Ready:
                    RenderGroups(vm, body);
                    break;
                case ReviewStage.Done:
                    RenderOutcomes(vm, body);
                    break;
            }
        }

        subs.On(vm, null, Render);
        return root;
    }

    private static void RenderGroups(ReviewViewModel vm, StackPanel body)
    {
        if (vm.Warning is { } warning)
        {
            body.Children.Add(Ui.Card(Ui.H(10, Ui.Icon(""), Ui.Text(warning, "DsWarningText")), "DsWarningBox"));
        }

        foreach (ReviewGroup group in vm.Groups)
        {
            var rows = new StackPanel { Spacing = 8 };
            rows.Children.Add(Ui.Row(Ui.H(10, Ui.Badge(group.Title, group.Tone)), Ui.Secondary(group.SizeText)));
            rows.Children.Add(Ui.Caption(group.Note));
            foreach (ReviewRow row in group.Rows)
            {
                rows.Children.Add(Ui.Divider());
                var lines = new StackPanel { Spacing = 2 };
                lines.Children.Add(Ui.Text(row.Title));
                foreach (string blocker in row.Blockers)
                {
                    lines.Children.Add(Ui.Text(blocker, "DsWarningText"));
                }

                if (row.Blockers.Count == 0 && group.Outcome == Core.Review.ReviewOutcome.Blocked)
                {
                    lines.Children.Add(Ui.Text(row.Summary, "DsWarningText"));
                }

                if (row.Command is { } command)
                {
                    lines.Children.Add(Ui.Text(command, "DsMono"));
                }

                foreach (string note in row.Notes)
                {
                    lines.Children.Add(Ui.Caption(note));
                }

                rows.Children.Add(Ui.Row(lines, Ui.Size(row.SizeText, row.Badges, row.SizeTooltip)));
            }

            body.Children.Add(rows);
        }

        if (vm.CommandsText.Length > 0)
        {
            var expander = new Expander
            {
                Header = "Exactly what will be done",
                Content = Ui.Card(Ui.Text(vm.CommandsText, "DsMono"), "DsCodeBox"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(expander, "Exactly what will be done");
            body.Children.Add(expander);
        }
    }

    private static void RenderOutcomes(ReviewViewModel vm, StackPanel body)
    {
        foreach (OutcomeRow row in vm.Outcomes)
        {
            FontIcon icon = Ui.Icon(row.Succeeded ? "" : "");
            icon.VerticalAlignment = VerticalAlignment.Top;
            var lines = Ui.V(2, Ui.Text(row.Title), Ui.Caption(row.Message), row.Reconciliation.Length > 0 ? Ui.Caption(row.Reconciliation) : null);
            AutomationProperties.SetName(lines, $"{(row.Succeeded ? "Done" : "Not cleaned")}: {row.Title}. {row.Message}");
            body.Children.Add(Ui.Row(Ui.H(10, icon, lines), Ui.Secondary(row.FreedText)));
        }
    }
}
