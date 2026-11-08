using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>The sidebar card shown when a newer Windows release exists: opens its release page, or "Later".</summary>
internal static class UpdateCard
{
    public static UIElement Build(UpdateViewModel updates, Subscriptions subs)
    {
        TextBlock title = Ui.Text("Update available", "DsStrong");
        TextBlock detail = Ui.Caption(string.Empty);
        detail.TextWrapping = TextWrapping.Wrap;
        Button open = Ui.Button("See what's new", updates.OpenRelease, "Open the release page for the new version", accent: true);
        Button later = Ui.Button("Later", updates.Later, "Not now: do not offer this version again");
        var card = Ui.Card(Ui.V(8, Ui.H(8, Ui.Icon("", 16), title), detail, Ui.H(8, open, later)));
        card.Margin = new Thickness(12);
        subs.On(updates, null, () =>
        {
            Ui.Show(card, updates.ShowCard);
            if (updates.Available is { } info)
            {
                detail.Text = $"DevSweep {info.Version} is out. It is downloaded from GitHub, never installed behind your back.";
            }
        });
        return card;
    }
}
