using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Shots;

internal sealed partial class ScreenshotTour
{
    /// <summary>
    /// Settings: every section is on the page, every control is reachable by its automation name, search filters the sections,
    /// the identity editor refuses a bad email, and one shot per group (the page scrolled to it, as the chip does).
    /// </summary>
    private async Task SettingsWalkAsync(string suffix)
    {
        await GoAsync("settings");
        SettingsViewModel vm = model.SettingsPage;
        vm.Search = string.Empty;
        await window.WaitForRenderAsync();

        foreach (SettingsSection section in SettingsSections.All)
        {
            if (section.Id is "downloads" or "editor" && model.Projects.Source is null)
            {
                continue;
            }

            Require(window.Find<StackPanel>("Settings section: " + section.Title) is not null, $"settings: the section '{section.Title}' is not on the page");
        }

        string[] buttons =
        [
            "Check now", "Send a test notification", "Add a GitHub account", "Refresh the accounts", "Add a project folder",
            "Copy scan report", "Copy diagnostics (version, flags, modules)", "Open log folder",
            "Open Windows Settings, Privacy and security", "Open Windows Security, Ransomware protection (Controlled folder access)",
        ];
        foreach (string name in buttons)
        {
            Require(window.Find<Button>(name) is not null, $"settings: the button '{name}' is not reachable");
        }

        foreach (string name in (string[])["Check for updates automatically", "Open DevSweep at login and keep it in the tray", "Scan on a schedule", "Alert when the disk is nearly full", "Measure exact sizes during the scan"])
        {
            Require(window.Find<ToggleSwitch>(name) is not null, $"settings: the switch '{name}' is not reachable");
        }

        Require(window.Find<TextBox>("Search settings") is not null, "settings: no search box");

        // Search shows only the matching sections.
        TextBox box = window.Find<TextBox>("Search settings")!;
        box.Text = "schedule";
        await window.WaitForRenderAsync();
        Require(vm.Shows("schedule") && !vm.Shows("alerts"), "settings: searching 'schedule' did not narrow the sections");
        Require(window.Find<StackPanel>("Settings section: Alert me when")!.Visibility == Visibility.Collapsed, "settings: the Alerts section stayed visible while searching");
        await ShotAsync("settings-search", suffix);
        box.Text = string.Empty;
        await window.WaitForRenderAsync();
        Require(vm.VisibleSections.Count == SettingsSections.All.Count, "settings: clearing the search did not bring every section back");

        // The identity editor refuses an email that is not one.
        if (window.Find<TextBox>("Email for commits as alex-acme") is { } email && window.Find<Button>("Save the commit identity for alex-acme") is { } save)
        {
            window.Find<TextBox>("Name for commits as alex-acme")!.Text = "Alex Doe";
            email.Text = "not-an-email";
            await window.WaitForRenderAsync();
            Require(!save.IsEnabled, "settings: Save was enabled for an invalid email");
            email.Text = "alex@acme.example";
            await window.WaitForRenderAsync();
            Require(save.IsEnabled, "settings: Save stayed disabled for a valid identity");
            email.Text = string.Empty;
            window.Find<TextBox>("Name for commits as alex-acme")!.Text = string.Empty;
            await window.WaitForRenderAsync();
        }

        Log.Write("settings: every section and control is reachable, search narrows the sections, the identity editor validates the email");

        foreach ((string group, _) in SettingsSections.Groups)
        {
            model.OpenSettings(SettingsSections.All.First(s => s.Group == group).Id);
            await window.WaitForRenderAsync();
            await ShotAsync("settings-" + group, suffix);
        }

        model.OpenSettings("appearance");
        await window.WaitForRenderAsync();
    }
}
