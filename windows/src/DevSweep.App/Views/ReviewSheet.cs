using DevSweep.App.Design;
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
        var frame = new SheetFrame(720);
        frame.Root.MaxHeight = Math.Max(420, window.Root.ActualHeight - 80);
        StackPanel body = frame.Body;

        Button cancel = Ui.Button("Cancel", vm.Close);
        Button clean = Ui.Button("Clean", () => _ = vm.CleanAsync(), accent: true);
        Button history = Ui.Button("Show History", () =>
        {
            vm.Close();
            model.Go("history");
        });
        Button done = Ui.Button("Done", vm.Close, accent: true);
        frame.Buttons.Children.Add(cancel);
        frame.Buttons.Children.Add(clean);
        frame.Buttons.Children.Add(history);
        frame.Buttons.Children.Add(done);

        void Render()
        {
            frame.Heading.Text = vm.Heading;
            frame.Subheading.Text = vm.Subheading;
            body.Children.Clear();
            ReviewStage stage = vm.Stage;
            Ui.Show(cancel, stage is ReviewStage.Measuring or ReviewStage.Ready);
            Ui.Show(clean, stage is ReviewStage.Ready or ReviewStage.Cleaning);
            Ui.Show(history, stage == ReviewStage.Done);
            Ui.Show(done, stage == ReviewStage.Done);
            clean.IsEnabled = vm.CleanCommand.CanExecute(null);
            clean.Content = stage == ReviewStage.Cleaning ? "Cleaning…" : vm.CleanLabel;
            AutomationProperties.SetName(clean, (string)clean.Content);
            frame.Footer.Text = stage == ReviewStage.Done ? "Every change is logged in History." : vm.Footer;
            frame.Footer.Style = Ui.Style(vm.FooterIsWarning && stage != ReviewStage.Done ? "DsWarningText" : "DsSecondary");
            switch (stage)
            {
                case ReviewStage.Measuring:
                case ReviewStage.Cleaning:
                    var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = vm.Progress };
                    AutomationProperties.SetName(bar, stage == ReviewStage.Measuring ? "Measuring progress" : "Cleaning progress");
                    body.Children.Add(Ui.V(Tokens.Space2, Ui.Secondary(vm.Status), bar));
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
        return frame.Root;
    }

    private static void RenderGroups(ReviewViewModel vm, StackPanel body)
    {
        if (vm.Warning is { } warning)
        {
            body.Children.Add(Ds.Banner(BannerKind.Warning, warning));
        }

        foreach (ReviewGroup group in vm.Groups)
        {
            var rows = new List<UIElement>();
            foreach (ReviewRow row in group.Rows)
            {
                var lines = new StackPanel { Spacing = 2 };
                lines.Children.Add(Ui.Text(row.Title, "DsStrong"));
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

                rows.Add(new ListRow(Ui.Row(lines, Ui.Size(row.SizeText, row.Badges, row.SizeTooltip))).Root);
            }

            Grid header = Ui.Row(Ui.H(Tokens.Space3, Ds.StatusTag(group.Title, group.Tone)), Ui.Secondary(group.SizeText));
            header.Margin = new Thickness(Tokens.Space1, 0, Tokens.Space1, 0);
            TextBlock note2 = Ui.Caption(group.Note);
            note2.Margin = new Thickness(Tokens.Space1, 0, Tokens.Space1, 0);
            body.Children.Add(Ui.V(Tokens.Space2, header, note2, Ds.RowsCard(rows)));
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
        var rows = new List<UIElement>();
        foreach (OutcomeRow row in vm.Outcomes)
        {
            StackPanel lines = Ui.V(2, Ui.Text(row.Title, "DsStrong"), Ui.Caption(row.Message), row.Reconciliation.Length > 0 ? Ui.Caption(row.Reconciliation) : null);
            var grid = new Grid { ColumnSpacing = Tokens.Space3 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FontIcon icon = Ui.Icon(row.Succeeded ? Icons.CheckCircle : Icons.Warning, 16);
            icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[row.Succeeded ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 0, 0);
            TextBlock freed = Ui.Secondary(row.FreedText);
            Grid.SetColumn(lines, 1);
            Grid.SetColumn(freed, 2);
            grid.Children.Add(icon);
            grid.Children.Add(lines);
            grid.Children.Add(freed);
            AutomationProperties.SetName(grid, $"{(row.Succeeded ? "Done" : "Not cleaned")}: {row.Title}. {row.Message}");
            rows.Add(new ListRow(grid).Root);
        }

        body.Children.Add(Ds.RowsCard(rows));
    }
}
