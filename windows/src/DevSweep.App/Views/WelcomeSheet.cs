using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>First launch: what DevSweep looks at, what it never does, and what Windows may say.</summary>
internal static class WelcomeSheet
{
    public static UIElement Build(MainViewModel model, Subscriptions subs)
    {
        var columns = new Grid { ColumnSpacing = 22 };
        for (int i = 0; i < 3; i++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        AddColumn(columns, 0, "It looks at",
        [
            "Caches and settings in AppData (Local, Roaming, LocalLow)",
            "Developer tool folders like .nuget, .gradle and npm-cache",
            "Project folders you choose",
            "Which apps and tools are installed, from every source Windows has",
        ]);
        AddColumn(columns, 1, "It never",
        [
            "Changes anything without your review",
            "Reads the contents of your documents",
            "Sends anything about your PC anywhere",
            "Runs as administrator, or opens online-only OneDrive files",
        ]);
        AddColumn(columns, 2, "Windows may say",
        [
            "\"Windows protected your PC\" (SmartScreen) the first time, because DevSweep isn't signed: choose More info, then Run anyway",
            "No administrator rights are needed to scan or clean",
            "A UAC prompt appears only when you run an admin command DevSweep shows you",
        ]);

        Button start = Ui.Button("Start scanning", () => _ = model.FinishWelcomeAsync(), accent: true);
        StackPanel content = Ui.V(22,
            Ui.H(12, Ui.Icon("", 30), Ui.V(2, Ui.Text("Welcome to DevSweep", "DsSubtitle"), Ui.Secondary("Before the first scan, here's exactly what it does."))),
            columns,
            Ui.Card(Ui.V(4,
                Ui.Text("Where things are", "DsStrong"),
                Ui.Text($"{model.PlacesLine} are in the list on the left (Settings is at the bottom of it, and the gear at the top right opens it too). The sun and moon button at the top right switches between light and dark, or follows Windows.", "DsSecondary"))),
            Ui.Card(Ui.Text("Nothing is cleaned automatically. Every item explains what it is, what was checked, and how to undo it; things that can't be undone say so before you confirm.", "DsBody"), "DsBetter"),
            Ui.Row(Ui.Caption("Project folders can be changed in Settings."), start));
        return new Border { Padding = new Thickness(36, 32, 36, 26), Width = 780, Child = content };
    }

    private static void AddColumn(Grid grid, int column, string title, string[] items)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Text(title, "DsStrong"));
        foreach (string item in items)
        {
            panel.Children.Add(Ui.Text("• " + item, "DsSecondary"));
        }

        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }
}
