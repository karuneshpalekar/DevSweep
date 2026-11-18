using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// "Quit &lt;app&gt;" for the apps that block a clean-up (the Mac's Quit buttons). It asks first, then asks the app to close its windows
/// the way clicking their X would; an app with unsaved work shows its own prompt, and nothing is ever ended by force.
/// </summary>
internal static class QuitButtons
{
    /// <summary>The name on the button: the rule's process name with a capital, "chrome" becomes "Chrome".</summary>
    public static string Pretty(string processName)
    {
        string bare = ProcessQuitter.Bare(processName);
        return bare.Length == 0 ? bare : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    /// <summary>One button per app, with a line under them that says how it went. <paramref name="closed"/> runs after an app really closed.</summary>
    public static UIElement Build(MainViewModel model, MainWindow window, IReadOnlyList<string> apps, Action? closed = null, bool small = false)
    {
        var host = new StackPanel { Spacing = Tokens.Space2 };
        TextBlock status = Ui.Caption(string.Empty);
        status.Visibility = Visibility.Collapsed;
        var buttons = new List<UIElement>();
        foreach (string app in apps)
        {
            string name = Pretty(app);
            Button button = Ui.Button($"Quit {name}", () => { }, $"Quit {name}");
            Ui.Tip(button, $"Asks {name} to close its windows, like clicking their X. It's never forced.");
            if (small)
            {
                button.Padding = new Thickness(Tokens.Space2, 2, Tokens.Space2, 2);
            }

            button.Click += async (_, _) =>
            {
                bool yes = await window.ConfirmAsync(
                    $"Quit {name}?",
                    $"DevSweep asks {name} to close its windows, the same as clicking their X. If it has unsaved work it will ask you what to do first. DevSweep never force-closes anything.",
                    $"Quit {name}");
                if (!yes)
                {
                    return;
                }

                button.IsEnabled = false;
                status.Text = $"Asking {name} to close…";
                status.Visibility = Visibility.Visible;
                QuitResult result = await model.Quitter.QuitAsync(app);
                status.Text = result.Done ? $"{name} is closed." : result.Message;
                button.IsEnabled = !result.Done;
                if (result.Done)
                {
                    closed?.Invoke();
                }
            };
            buttons.Add(button);
        }

        host.Children.Add(FlowButtons.Build([.. buttons]));
        host.Children.Add(status);
        return host;
    }
}
