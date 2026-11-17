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
                        body.Children.Add(CleaningRows(vm));
                    }

                    break;
                case ReviewStage.Ready:
                    RenderGroups(model, window, vm, body);
                    break;
                case ReviewStage.Done:
                    RenderOutcomes(vm, body);
                    break;
            }
        }

        subs.On(vm, null, Render);
        return frame.Root;
    }

    private static void RenderGroups(MainViewModel model, MainWindow window, ReviewViewModel vm, StackPanel body)
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

                IReadOnlyList<string> quit = row.QuitApps;
                UIElement? quitButtons = quit.Count > 0 ? QuitButtons.Build(model, window, quit, () => _ = vm.RecheckAsync(), small: true) : null;
                rows.Add(new ListRow(Ui.Row(lines, quitButtons, Ui.Size(row.SizeText, row.Badges, row.SizeTooltip))).Root);
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

    /// <summary>While cleaning: every item with its state (done, in progress, waiting), so a long clean is never a silent bar.</summary>
    private static UIElement CleaningRows(ReviewViewModel vm)
    {
        var rows = new List<UIElement>();
        for (int i = 0; i < vm.RunTitles.Count; i++)
        {
            string state = i < vm.CleanedCount ? "Done" : i == vm.CleanedCount ? "Cleaning now" : "Waiting";
            FrameworkElement mark;
            if (i < vm.CleanedCount)
            {
                FontIcon tick = Ui.Icon(Icons.CheckCircle, 16);
                Ui.Tint(tick, "SystemFillColorSuccessBrush");
                mark = tick;
            }
            else if (i == vm.CleanedCount)
            {
                mark = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
            }
            else
            {
                mark = Ui.Icon(Icons.Clock, 16);
            }

            mark.VerticalAlignment = VerticalAlignment.Center;
            var line = new Grid { ColumnSpacing = Tokens.Space3 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = Ui.Text(vm.RunTitles[i], i == vm.CleanedCount ? "DsStrong" : "DsBody", maxLines: 1);
            TextBlock status = Ui.Caption(state);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(status, 2);
            line.Children.Add(mark);
            line.Children.Add(title);
            line.Children.Add(status);
            AutomationProperties.SetName(line, $"{vm.RunTitles[i]}: {state}");
            rows.Add(new ListRow(line).Root);
        }

        return Ds.RowsCard(rows);
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
            Ui.Tint(icon, row.Succeeded ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
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
