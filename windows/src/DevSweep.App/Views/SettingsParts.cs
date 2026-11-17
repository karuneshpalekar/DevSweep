using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>The Settings sections that are about GitHub, folders, permissions and troubleshooting (the rest are in <see cref="SettingsView"/>).</summary>
internal static class SettingsParts
{
    /// <summary>GitHub accounts: each signed-in account with its commit identity (name and email, Save), Add account, Refresh.</summary>
    public static StackPanel Accounts(MainViewModel model, Subscriptions subs)
    {
        ProjectsViewModel projects = model.Projects;
        UIElement card;
        if (projects.Source is null)
        {
            card = Ds.Card(Ui.Secondary("Projects is turned off, so there are no GitHub accounts to manage. Turn it on in Modules."));
        }
        else
        {
            var notice = new Border();
            var list = new StackPanel { Spacing = Tokens.Space3 };
            void Render()
            {
                list.Children.Clear();
                if (projects.Report?.Snapshot is { } snapshot)
                {
                    if (snapshot.AccountSummaries.Count == 0)
                    {
                        list.Children.Add(Ds.Card(Ds.EmptyState(Icons.Person, "No accounts yet", "Sign in to GitHub to list and download your repositories.")));
                    }

                    foreach (DevSweep.Core.Projects.AccountSummary account in snapshot.AccountSummaries)
                    {
                        list.Children.Add(AccountCard(projects, account, snapshot.Identities));
                    }
                }
                else
                {
                    list.Children.Add(Ui.Secondary("Loading accounts…"));
                }

                list.Children.Add(FlowButtons.Build(
                    Ui.Button("Add account…", () => projects.OpenSheet(ProjectSheet.AddAccount), "Add a GitHub account", accent: true),
                    Ui.Button("Refresh", () => _ = projects.LoadAsync(), "Refresh the accounts")));
            }

            subs.On(projects, nameof(ProjectsViewModel.Report), Render);
            subs.On(projects, nameof(ProjectsViewModel.Notice), () => notice.Child = projects.Notice is { } n
                ? Ds.Banner(n.Kind switch { NoticeKind.Error => BannerKind.Critical, NoticeKind.Warning => BannerKind.Warning, _ => BannerKind.Info }, n.Text, null, "Dismiss", projects.Dismiss)
                : null);
            card = Ui.V(Tokens.Space3, notice, list);
            if (projects.Report is null && projects.State is PlaceState.NotChecked)
            {
                _ = projects.LoadAsync();
            }
        }

        return SettingsView.Section("GitHub accounts", card, "Each account's name and email are written into the projects you download with it, so commits are attributed correctly whatever your global Git identity is.");
    }

    private static UIElement AccountCard(ProjectsViewModel vm, DevSweep.Core.Projects.AccountSummary account, IReadOnlyDictionary<string, DevSweep.Core.Projects.GitIdentity> identities)
    {
        DevSweep.Core.Projects.GitIdentity saved = identities.FirstOrDefault(i => i.Key.Equals(account.Login, StringComparison.OrdinalIgnoreCase)).Value ?? new DevSweep.Core.Projects.GitIdentity();
        var name = new TextBox { Header = "Name for commits", Text = saved.Name, MinWidth = 200 };
        var email = new TextBox { Header = "Email for commits", Text = saved.Email, MinWidth = 220 };
        AutomationProperties.SetName(name, $"Name for commits as {account.Login}");
        AutomationProperties.SetName(email, $"Email for commits as {account.Login}");
        Button save = Ui.Button("Save", () => vm.SaveIdentity(account.Login, new DevSweep.Core.Projects.GitIdentity(name.Text.Trim(), email.Text.Trim())), $"Save the commit identity for {account.Login}");
        var stateHost = new Border();
        void Check()
        {
            var now = new DevSweep.Core.Projects.GitIdentity(name.Text.Trim(), email.Text.Trim());
            bool changed = now != saved;
            string? problem = IdentityValidation.Check(now);
            save.IsEnabled = changed && problem is null;
            stateHost.Child = problem is not null && changed ? Ds.CheckRow(false, problem)
                : now.IsBlank ? Ds.CheckRow(false, "No identity set. Projects downloaded with this account use your global Git identity.")
                : !changed ? Ds.CheckRow(true, $"Commits in projects downloaded with this account are made as {now}.") : null;
        }

        name.TextChanged += (_, _) => Check();
        email.TextChanged += (_, _) => Check();
        Check();
        StackPanel title = Ui.H(Tokens.Space2, Ui.Icon(Icons.Person, 20), Ui.Text(account.Login, "DsStrong"));
        if (account.IsActive)
        {
            title.Children.Add(Ds.StatusTag("Active in terminal", DevSweep.App.ViewModels.Text.Tone.Neutral, "The account gh and git use in a terminal. DevSweep never changes it."));
        }

        Grid fields = Ui.Row(Ui.H(Tokens.Space3, name, email), save);
        fields.ColumnDefinitions[0].Width = GridLength.Auto;
        fields.VerticalAlignment = VerticalAlignment.Bottom;
        Border card = Ds.Card(Ui.V(Tokens.Space3, title, fields, stateHost));
        AutomationProperties.SetName(card, "GitHub account " + account.Login);
        return card;
    }

    /// <summary>Downloads: where new downloads go (shared with the Clone sheet), removable drives, and the deep search.</summary>
    public static StackPanel Downloads(MainViewModel model, IProjectsSource source, MainWindow window, Subscriptions subs)
    {
        TextBlock root = Ui.Text(source.Preferences.WorkspaceRoot, "DsMono");
        root.TextWrapping = TextWrapping.Wrap;
        Button choose = Ui.Button("Choose…", async () =>
        {
            if (await window.PickFolderAsync() is { } picked)
            {
                source.SetWorkspaceRoot(picked);
                root.Text = picked;
            }
        }, "Choose the Downloads folder for projects");
        var removable = new CheckBox { Content = "Include removable drives when looking for projects", IsChecked = source.Preferences.SearchRemovable };
        removable.Checked += (_, _) => source.SetSearchRemovable(true);
        removable.Unchecked += (_, _) => source.SetSearchRemovable(false);
        AutomationProperties.SetName(removable, "Include removable drives when looking for projects");
        Button find = Ui.Button("Find all repos on this PC", () =>
        {
            model.Go("projects");
            _ = model.Projects.StartDeepSearchAsync();
        }, "Find all repos on this PC");
        return SettingsView.Section(
            "Downloads",
            [
                SettingsView.Setting("Downloads go to", "New downloads go to <this folder>\\<account>\\<project>. The Clone sheet uses the same folder.", root, choose),
                new Border { Padding = new Thickness(Tokens.Space3), Child = Ui.V(Tokens.Space2, removable, FlowButtons.Build(find), SettingsView.Note("Reads folder names only, on every fixed drive, skipping system and dependency folders.")) },
            ]);
    }

    /// <summary>Project folders: the ones added and the usual ones, Add folder, Remove, Restore defaults.</summary>
    public static StackPanel Folders(MainViewModel model, SettingsViewModel vm, Subscriptions subs, MainWindow window)
    {
        var host = new Border();
        void Render()
        {
            var rows = new List<UIElement>();
            List<FolderRow> present = [.. vm.FolderRows.Where(r => r.State != FolderState.Missing)];
            List<FolderRow> missing = [.. vm.FolderRows.Where(r => r.State == FolderState.Missing)];
            foreach (FolderRow row in present)
            {
                rows.Add(FolderLine(row, vm, model));
            }

            if (present.Count == 0)
            {
                rows.Add(new Border { Padding = new Thickness(Tokens.Space3), Child = Ui.Secondary("None of the usual folders are on this PC. Add the folder you keep your projects in.") });
            }

            if (missing.Count > 0)
            {
                var more = new Expander
                {
                    Header = FolderRows.MissingTitle(missing.Count),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = Ui.V(Tokens.Space2, [.. missing.Select(m => FolderLine(m, vm, model)), SettingsView.Note("These are common names for project folders. DevSweep skips any that don't exist on this PC, so they do no harm.")]),
                };
                AutomationProperties.SetName(more, FolderRows.MissingTitle(missing.Count));
                rows.Add(new Border { Padding = new Thickness(Tokens.Space2), Child = more });
            }

            Button add = Ui.Button("Add folder…", async () =>
            {
                if (await window.PickFolderAsync() is { } picked)
                {
                    AddFolder(model, vm, picked);
                }
            }, "Add a project folder");
            Button restore = Ui.Button("Restore defaults", () => vm.RestoreDefaultFolders(), "Restore the usual project folders (removes the ones you added)");
            restore.IsEnabled = vm.ProjectFolders.Count > 0;
            Button openSecurity = Ui.Button("Open Windows Security", () => _ = Windows.System.Launcher.LaunchUriAsync(SettingsViewModel.RansomwareProtectionUri), "Open Windows Security, Ransomware protection");
            Ui.Show(openSecurity, vm.FolderRows.Any(r => r.State == FolderState.Denied));
            rows.Add(new Border { Padding = new Thickness(Tokens.Space3), Child = FlowButtons.Build(add, restore, openSecurity) });
            host.Child = Ds.RowsCard(rows);
        }

        subs.OnCollection(vm.FolderRows, Render);
        return SettingsView.Section(
            "Project folders",
            host,
            "DevSweep looks for idle projects, and for the repos Projects lists, in these folders. Only rebuildable folders (node_modules, .venv, bin and obj, target, .gradle) are suggested, and only in projects untouched for a while. The usual folders are always searched; folders you add are searched too (and \"Find all repos on this PC\" covers the rest). Folders that don't exist on this PC are skipped.");
    }

    private static void AddFolder(MainViewModel model, SettingsViewModel vm, string folder)
    {
        if (model.Projects.Source is not null)
        {
            model.Projects.AddFolder(folder);
        }
        else
        {
            vm.AddFolder(folder);
        }
    }

    private static UIElement FolderLine(FolderRow row, SettingsViewModel vm, MainViewModel model)
    {
        FontIcon icon = Ui.Icon(row.State == FolderState.Denied ? Icons.Lock : Icons.Folder, 16);
        icon.VerticalAlignment = VerticalAlignment.Center;
        TextBlock path = Ui.Text(row.Path, "DsMono");
        path.TextWrapping = TextWrapping.Wrap;
        StackPanel text = Ui.V(2, path);
        if (row.IsDefault)
        {
            text.Children.Add(SettingsView.Note(row.State == FolderState.Missing ? "Usual folder · " + row.StateText : row.State == FolderState.Denied ? "Usual folder · " + row.StateText : "Usual folder"));
        }
        else if (row.State != FolderState.Ok)
        {
            text.Children.Add(SettingsView.Note(row.StateText));
        }

        Button? remove = null;
        if (!row.IsDefault)
        {
            remove = Ui.IconButton(Icons.Delete, "Remove " + row.Path, () =>
            {
                if (model.Projects.Source is not null)
                {
                    model.Projects.RemoveFolder(row.Path);
                }
                else
                {
                    vm.RemoveFolder(row.Path);
                }
            });
        }

        var line = new Grid { ColumnSpacing = Tokens.Space3, Padding = new Thickness(Tokens.Space3, Tokens.Space2, Tokens.Space3, Tokens.Space2) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        line.Children.Add(icon);
        line.Children.Add(text);
        if (remove is not null)
        {
            Grid.SetColumn(remove, 2);
            line.Children.Add(remove);
        }

        AutomationProperties.SetName(line, "Project folder " + row.Path);
        return line;
    }

    /// <summary>Default editor for "Open in editor".</summary>
    public static StackPanel Editor(IProjectsSource source)
    {
        var editor = new ComboBox { MinWidth = 260 };
        IReadOnlyList<DevSweep.Core.Projects.Editors.EditorInfo> editors = source.Editors;
        editor.Items.Add("First one found (or File Explorer)");
        foreach (DevSweep.Core.Projects.Editors.EditorInfo e in editors)
        {
            editor.Items.Add(e.Name);
        }

        int selected = editors.ToList().FindIndex(e => e.Id == source.Preferences.DefaultEditor);
        editor.SelectedIndex = selected + 1;
        editor.SelectionChanged += (_, _) => source.SetDefaultEditor(editor.SelectedIndex <= 0 ? null : editors[editor.SelectedIndex - 1].Id);
        AutomationProperties.SetName(editor, "Editor that Open in editor uses");
        return SettingsView.Section("Default editor", [SettingsView.Setting("Open in editor uses", "Detected: VS Code, VS Code Insiders, Visual Studio, Rider, Cursor, Windsurf. With none, File Explorer opens.", editor)]);
    }

    /// <summary>Ignored items, each with Stop ignoring.</summary>
    public static StackPanel Ignored(SettingsViewModel vm, Subscriptions subs)
    {
        var host = new Border();
        subs.OnCollection(vm.Ignored, () =>
        {
            var rows = new List<UIElement>();
            if (vm.Ignored.Count == 0)
            {
                rows.Add(new Border { Padding = new Thickness(Tokens.Space3), Child = Ui.Secondary("Nothing ignored. Items you choose to always ignore show up here, so you can bring them back.") });
            }

            foreach (IgnoredItem item in vm.Ignored)
            {
                rows.Add(SettingsView.Setting(item.Title, null, Ui.Button("Stop ignoring", () => vm.StopIgnoring(item), $"Stop ignoring {item.Title}")));
            }

            host.Child = Ds.RowsCard(rows);
        });
        return SettingsView.Section("Ignored items", host, "These are left out of every scan.");
    }

    /// <summary>Permissions and privacy, in Windows words: what is read, what leaves the PC, Controlled folder access.</summary>
    public static StackPanel Permissions(SettingsViewModel vm, Subscriptions subs)
    {
        FontIcon shield = Ui.Icon(Icons.Shield, 28);
        TextBlock title = Ui.Text(string.Empty, "DsStrong");
        TextBlock text = SettingsView.Note(string.Empty);
        Button security = Ui.Button("Open Windows Security", () => _ = Windows.System.Launcher.LaunchUriAsync(SettingsViewModel.RansomwareProtectionUri), "Open Windows Security, Ransomware protection (Controlled folder access)");
        Button privacy = Ui.Button("Open Privacy settings", () => _ = Windows.System.Launcher.LaunchUriAsync(SettingsViewModel.PrivacySettingsUri), "Open Windows Settings, Privacy and security");
        subs.On(vm, null, () =>
        {
            bool blocked = vm.BlockedFolderCount > 0;
            title.Text = blocked ? "Windows blocked " + (vm.BlockedFolderCount == 1 ? "a folder" : vm.BlockedFolderCount + " folders") : "Nothing is blocked";
            text.Text = vm.PermissionsText;
            Ui.Show(security, blocked);
            shield.Glyph = blocked ? Icons.Warning : Icons.CheckCircle;
        });
        var status = new Grid { ColumnSpacing = Tokens.Space3, Padding = new Thickness(Tokens.Space3) };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel info = Ui.V(Tokens.Space2, title, text, FlowButtons.Build(security, privacy));
        Grid.SetColumn(info, 1);
        shield.VerticalAlignment = VerticalAlignment.Top;
        status.Children.Add(shield);
        status.Children.Add(info);
        AutomationProperties.SetName(status, "Windows permissions status");
        return SettingsView.Section(
            "Permissions and privacy",
            [
                status,
                SettingsView.Setting("What DevSweep reads", "File and folder names, sizes and dates in the places it looks (caches, tool folders, your project folders), and which apps and tools are installed. Never the contents of your documents, and online-only OneDrive files are not opened."),
                SettingsView.Setting("What leaves this PC", "Nothing about your PC. The only network requests are public support dates from endoflife.date (Health), the latest release from GitHub (unless you turn it off in Updates), and the GitHub calls Projects makes for the accounts you sign in with."),
                SettingsView.Setting("If Windows blocks a folder", "Controlled folder access (Windows Security, Virus and threat protection, Ransomware protection) can stop programs from reading protected folders. If scans report blocked folders, allow DevSweep there, or ignore it: DevSweep only misses a few leftovers. It never changes the setting itself and never runs as administrator."),
            ]);
    }

    /// <summary>Troubleshooting: copy the scan report (no file or project names), copy diagnostics, open the log and data folders.</summary>
    public static StackPanel Troubleshooting(SettingsViewModel vm, Subscriptions subs)
    {
        TextBlock last = Ui.Secondary(string.Empty);
        last.TextWrapping = TextWrapping.Wrap;
        Button copyReport = Ui.Button("Copy report", () => { }, "Copy scan report");
        copyReport.Click += (_, _) => Copy(copyReport, "Copy report", vm.ScanReportText());
        TextBlock hint = Ui.Secondary("Run a scan first.");
        Button copyDiagnostics = Ui.Button("Copy diagnostics", () => { }, "Copy diagnostics (version, flags, modules)");
        copyDiagnostics.Click += (_, _) => Copy(copyDiagnostics, "Copy diagnostics", vm.DiagnosticsText());
        string logFolder = Path.GetDirectoryName(Log.File) ?? Path.GetTempPath();
        subs.On(vm, null, () =>
        {
            last.Text = vm.ScanReportSummary;
            copyReport.IsEnabled = vm.HasScanReport;
            Ui.Show(hint, !vm.HasScanReport);
        });
        return SettingsView.Section(
            "Troubleshooting",
            [
                SettingsView.Setting("Last scan", null, last),
                SettingsView.Setting("Scan report", "Lists each check, how long it took and what it found, how many sizes are partial, which folders Windows blocked, and your Windows build. It has no file or project names. Paste it into an email or a GitHub issue.", hint, copyReport),
                SettingsView.Setting("Diagnostics", "DevSweep's version, the Windows build, which modules are on and the feature switch. No file names.", copyDiagnostics),
                SettingsView.Setting("Log folder", "DevSweep's log file is in your temporary folder.", Ui.Button("Open log folder", () => Shell.OpenFolder(logFolder), "Open the log folder")),
            ]);
    }

    private static void Copy(Button button, string label, string text)
    {
        try
        {
            Shell.Copy(text);
            button.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            button.Content = "Couldn't copy";
        }

        _ = Reset(button, label);
    }

    private static async Task Reset(Button button, string label)
    {
        await Task.Delay(2000);
        button.Content = label;
    }
}
