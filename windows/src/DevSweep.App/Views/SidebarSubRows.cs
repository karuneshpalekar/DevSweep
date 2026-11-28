using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>One row under an open section of the sidebar.</summary>
/// <param name="Tag">"place:key", such as <c>cleanup:Caches</c>, <c>cleanup:all</c> or <c>health:Ports</c>.</param>
internal sealed record SubRow(string Tag, string Title, string Count, bool Selected);

/// <summary>
/// The rows under the open section of the sidebar, as on the Mac: Clean up's kinds with their sizes once there is a scan,
/// and the tabs of Projects and Health with their counts. Picking one opens the section on it; the row's quiet highlight
/// eases between rows.
/// </summary>
internal static class SidebarSubRows
{
    public static bool Has(string placeId) => placeId is "cleanup" or "projects" or "health";

    public static IReadOnlyList<SubRow> For(MainViewModel model, string placeId)
    {
        ArgumentNullException.ThrowIfNull(model);
        return placeId switch
        {
            "cleanup" when model.Scan.HasScanned && model.CleanUp.Filters.Count > 1 => [.. model.CleanUp.Filters.Select(f =>
                new SubRow("cleanup:" + (f.Kind?.ToString() ?? "all"), f.Title, f.Detail, model.CleanUp.SelectedKind == f.Kind))],
            "projects" => [.. model.Projects.Tabs.Select(t =>
                new SubRow("projects:" + t, ProjectsViewModel.TabTitle(t), model.Projects.Count(t), model.Projects.Tab == t))],
            "health" => [.. model.Health.Tabs.Select(t =>
                new SubRow("health:" + t, HealthViewModel.TabTitle(t), model.Health.Count(t), model.Health.Tab == t))],
            _ => [],
        };
    }

    /// <summary>Selects what a row stands for; returns its place.</summary>
    public static string Open(MainViewModel model, string tag)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tag);
        int colon = tag.IndexOf(':', StringComparison.Ordinal);
        string place = tag[..colon];
        string key = tag[(colon + 1)..];
        switch (place)
        {
            case "cleanup":
                model.CleanUp.SelectedKind = key == "all" ? null : Enum.Parse<CleanupKind>(key);
                break;
            case "projects":
                model.Projects.Tab = Enum.Parse<ProjectsTab>(key);
                break;
            case "health":
                model.Health.Tab = Enum.Parse<HealthTab>(key);
                break;
        }

        return place;
    }

    /// <summary>The row's content, and how to update it in place (the highlight eases in and out).</summary>
    public static (UIElement Root, Action<SubRow> Update) Content(SubRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // No Foreground on purpose: the text follows the navigation item's own colours (high contrast included).
        var title = new TextBlock { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { FontSize = 12, Opacity = 0.8, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnSpacing = Tokens.Space2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(count, 1);
        grid.Children.Add(title);
        grid.Children.Add(count);
        var surface = new Border { Child = grid, HorizontalAlignment = HorizontalAlignment.Stretch };
        Motion.SmoothFill(surface);

        void Update(SubRow r)
        {
            title.Text = r.Title;
            count.Text = r.Count;
            surface.Style = Ui.Style(r.Selected ? "DsSubRowSelected" : "DsSubRow");
        }

        Update(row);
        return (surface, Update);
    }
}
