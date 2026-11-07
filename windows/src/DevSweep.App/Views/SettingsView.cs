using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>Settings: appearance, project folders, ignored items, modules, scan options, updates, about.</summary>
internal static class SettingsView
{
    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        SettingsViewModel vm = model.SettingsPage;
        var page = new StackPanel { Spacing = 22, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ui.PageHeader("Settings", Ui.Secondary("Nothing is ever cleaned automatically.")));

        var appearance = new ComboBox { Width = 220 };
        foreach (string name in (string[])["Use Windows setting", "Light", "Dark"])
        {
            appearance.Items.Add(name);
        }

        appearance.SelectedIndex = (int)vm.Appearance;
        appearance.SelectionChanged += (_, _) => vm.Appearance = (AppearanceMode)Math.Max(0, appearance.SelectedIndex);
        AutomationProperties.SetName(appearance, "Appearance");
        page.Children.Add(Section("Appearance", Ui.Row(Ui.Secondary("Light or dark; by default DevSweep follows Windows."), appearance)));

        var folders = new StackPanel { Spacing = 6 };
        var path = new TextBox { PlaceholderText = @"D:\src", Width = 320 };
        AutomationProperties.SetName(path, "Project folder to add");
        Button add = Ui.Button("Add", () =>
        {
            if (path.Text.Trim() is { Length: > 0 } text && Path.IsPathFullyQualified(text))
            {
                vm.AddFolder(text);
                path.Text = string.Empty;
            }
        }, "Add the typed project folder");
        Button browse = Ui.Button("Browse…", async () =>
        {
            if (await window.PickFolderAsync() is { } picked)
            {
                vm.AddFolder(picked);
            }
        }, "Choose a project folder");
        subs.OnCollection(vm.ProjectFolders, () =>
        {
            folders.Children.Clear();
            if (vm.ProjectFolders.Count == 0)
            {
                folders.Children.Add(Ui.Secondary("Using the rules' own folders (your profile's usual project folders, such as source, src, Projects and repos)."));
            }

            foreach (string folder in vm.ProjectFolders)
            {
                folders.Children.Add(Ui.Row(Ui.Text(folder), Ui.Button("Remove", () => vm.RemoveFolder(folder), $"Remove {folder}")));
            }
        });
        page.Children.Add(Section("Project folders", Ui.V(10,
            Ui.Secondary("DevSweep looks for idle projects here. Only rebuildable folders (node_modules, .venv, bin and obj, target, .gradle) are suggested, and only in projects untouched for a while. Folders outside your profile are cleaned only because you list them here."),
            folders,
            Ui.H(8, path, add, browse, Ui.Button("Restore defaults", vm.RestoreDefaultFolders)))));

        var ignored = new StackPanel { Spacing = 6 };
        subs.OnCollection(vm.Ignored, () =>
        {
            ignored.Children.Clear();
            if (vm.Ignored.Count == 0)
            {
                ignored.Children.Add(Ui.Secondary("Nothing ignored. Items you choose to always ignore show up here, so you can bring them back."));
            }

            foreach (IgnoredItem item in vm.Ignored)
            {
                ignored.Children.Add(Ui.Row(Ui.Text(item.Title), Ui.Button("Stop ignoring", () => vm.StopIgnoring(item), $"Stop ignoring {item.Title}")));
            }
        });
        page.Children.Add(Section("Ignored items", Ui.V(8, Ui.Secondary("These are left out of every scan."), ignored)));

        var modules = new StackPanel { Spacing = 6 };
        foreach (ModuleToggle module in vm.Modules)
        {
            var toggle = new ToggleSwitch { IsOn = module.IsOn, IsEnabled = module.CanChange, OnContent = "On", OffContent = "Off" };
            AutomationProperties.SetName(toggle, module.Title);
            toggle.Toggled += (_, _) => module.IsOn = toggle.IsOn;
            modules.Children.Add(Ui.Row(Ui.V(0, Ui.Text(module.Title), module.Note.Length > 0 ? Ui.Caption(module.Note) : null), toggle));
        }

        TextBlock restart = Ui.Text("Changes to modules apply the next time DevSweep starts.", "DsWarningText");
        subs.On(vm, nameof(SettingsViewModel.RestartNeeded), () => Ui.Show(restart, vm.RestartNeeded));
        page.Children.Add(Section("Modules", Ui.V(8,
            Ui.Secondary("Each place is a module that can be turned off. A module that is off is left out of scans and its place is hidden. Saved in features.json; DEVSWEEP_FEATURES overrides it."),
            modules, restart)));

        var exact = new ToggleSwitch { IsOn = vm.ExactScan, OnContent = "Exact sizes", OffContent = "Overview sizes" };
        AutomationProperties.SetName(exact, "Measure exact sizes during the scan");
        exact.Toggled += (_, _) => vm.ExactScan = exact.IsOn;
        var timeout = new NumberBox { Value = vm.CheckTimeoutSeconds, Minimum = 5, Maximum = 600, SmallChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
        AutomationProperties.SetName(timeout, "Seconds before a slow check is skipped");
        timeout.ValueChanged += (_, _) => vm.CheckTimeoutSeconds = timeout.Value;
        page.Children.Add(Section("Scanning", Ui.V(10,
            Ui.Row(Ui.V(2, Ui.Text("Sizes during the scan"), Ui.Caption("Overview skips alternate data streams (faster, no handle per file). Review always measures exactly before anything changes.")), exact),
            Ui.Row(Ui.V(2, Ui.Text("Skip a check after (seconds)"), Ui.Caption("A slow check is skipped and the scan report says so.")), timeout))));

        var updates = new ToggleSwitch { IsOn = vm.CheckForUpdates };
        AutomationProperties.SetName(updates, "Check for updates automatically");
        updates.Toggled += (_, _) => vm.CheckForUpdates = updates.IsOn;
        TextBlock updateStatus = Ui.Caption(string.Empty);
        subs.On(vm, nameof(SettingsViewModel.UpdateStatus), () => updateStatus.Text = vm.UpdateStatus ?? string.Empty);
        page.Children.Add(Section("Updates", Ui.V(8,
            Ui.Row(Ui.V(2, Ui.Text("Check for updates automatically"), Ui.Caption("Asks GitHub for the latest release; sends nothing about your PC.")), updates, Ui.Button("Check now", () => _ = vm.CheckNowAsync())),
            updateStatus)));

        page.Children.Add(Section("About", Ui.V(6,
            Ui.Text($"DevSweep for Windows {SettingsViewModel.Version}"),
            Ui.Secondary(SettingsViewModel.Privacy),
            Ui.Caption($"Settings, History and the quarantine live in {vm.DataFolder}."),
            Ui.Caption("Unsigned: Windows SmartScreen may warn the first time (More info, then Run anyway)."))));
        return Ui.Scroll(page);
    }

    private static UIElement Section(string title, UIElement content) => Ui.V(8, Ui.Text(title, "DsStrong"), Ui.Card(content));
}
