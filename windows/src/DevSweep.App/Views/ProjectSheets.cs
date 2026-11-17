using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// The Projects sheets: Download (blobless, shallow or full, with the size estimate, where it goes, which account, whose name the
/// commits carry), Publish (folder, name, visibility, the secret check and its explicit confirm), Add by URL and Add account
/// (identity first, then GitHub's sign-in in a visible window). Each shows progress while it works, says what went wrong in plain
/// words, and cannot be closed by Esc while it is working.
/// </summary>
internal static class ProjectSheets
{
    private static readonly string[] VisibilityChoices = ["Private", "Public"];

    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        ProjectsViewModel vm = model.Projects;
        return vm.Sheet switch
        {
            ProjectSheet.Clone when vm.SheetProject is { } project => Clone(vm, project, subs, window),
            ProjectSheet.Publish => Publish(vm, subs, window),
            ProjectSheet.AddByUrl => AddByUrl(vm, subs, window),
            ProjectSheet.AddAccount => AddAccount(vm, subs),
            _ => new Grid(),
        };
    }

    private static void Warn(TextBlock footer, string? text, bool warning = true)
    {
        footer.Text = text ?? string.Empty;
        footer.Style = Ui.Style(warning ? "DsWarningText" : "DsSecondary");
    }

    // ---- Download --------------------------------------------------------------------------------------------------------------------

    private static UIElement Clone(ProjectsViewModel vm, Project project, Subscriptions subs, MainWindow window)
    {
        var frame = new SheetFrame(580);
        frame.Heading.Text = "Download " + project.Name;
        frame.Subheading.Text = project.NameWithOwner + (project.IsPrivate == true ? " · private" : string.Empty);
        CloneStrategy strategy = project.Strategy ?? CloneStrategy.Blobless;
        string? destination = null;
        string? account = vm.CredentialAccount(project);
        bool working = false;
        var cts = new CancellationTokenSource();
        subs.Add(cts.Dispose);

        var options = new StackPanel { Spacing = Tokens.Space2 };
        var where = Ui.Text(string.Empty, "DsMono");
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        where.MaxLines = 1;
        var accountHost = new Border();
        var identityHost = new Border();
        var bar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(bar, "Downloading");
        Button cancelButton = Ui.Button("Cancel", () =>
        {
            if (working)
            {
                cts.Cancel();
            }
            else
            {
                vm.CloseSheet();
            }
        });
        Button download = Ui.Button("Download", () => { }, $"Download {project.NameWithOwner}", accent: true);
        Button change = Ui.Button("Change…", () => { }, "Choose another folder for the download");

        DownloadPlan Plan() => vm.Source!.PlanDownload(project, strategy, destination, account);

        void Render()
        {
            DownloadPlan plan = Plan();
            options.Children.Clear();
            foreach (CloneStrategy s in CloneStrategies.All)
            {
                CloneStrategy choice = s;
                bool selected = s == strategy;
                string? estimate = s.EstimateBytes(project.RemoteKB) is { } bytes ? "about " + SizeText.Format(bytes) : null;
                FontIcon radio = Ui.Icon(selected ? "" : "", 16);
                Ui.Tint(radio, selected ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush");
                radio.VerticalAlignment = VerticalAlignment.Top;
                radio.Margin = new Thickness(0, 2, 0, 0);
                StackPanel text = Ui.V(2, Ui.H(Tokens.Space2, Ui.Text(s.Title(), "DsStrong"), estimate is null ? null : Ui.Caption(estimate)), Ui.Secondary(s.Summary()));
                var grid = new Grid { ColumnSpacing = Tokens.Space3 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(text, 1);
                grid.Children.Add(radio);
                grid.Children.Add(text);
                var row = new ListRow(grid, () =>
                {
                    if (!working)
                    {
                        strategy = choice;
                        Render();
                    }
                }, $"{s.Title()}: {s.Summary()}{(estimate is null ? string.Empty : ", " + estimate)}") { Selected = selected };
                options.Children.Add(row.Root);
            }

            where.Text = plan.Destination;
            Ui.Tip(where, plan.Destination);
            IReadOnlyList<string> accounts = vm.Accounts;
            accountHost.Child = accounts.Count > 1
                ? AccountPicker(accounts, account, picked =>
                {
                    account = picked;
                    Render();
                })
                : account is null ? Ds.Banner(BannerKind.Warning, "No GitHub account is signed in, so DevSweep can't download this project. Sign in from the Accounts tab.")
                : Ui.Secondary($"Downloading as {account}.");
            GitIdentity? identity = vm.IdentityFor(account);
            identityHost.Child = account is null ? null
                : identity is { IsBlank: false }
                    ? Ds.Banner(BannerKind.Info, $"Commits here will be made as {identity}.")
                    : Ds.Banner(BannerKind.Warning, $"No commit identity is saved for {account}, so commits here will use your global Git identity. You can set one in the Accounts tab.");
            download.IsEnabled = !working && plan.Refusal is null && account is not null;
            Warn(frame.Footer, plan.Refusal);
        }

        frame.Body.Children.Add(options);
        frame.Body.Children.Add(Ui.V(Tokens.Space2, Ds.SectionTitle("Where it goes"), Ui.Row(Ui.Card(where, "DsCodeBox"), change)));
        frame.Body.Children.Add(accountHost);
        frame.Body.Children.Add(identityHost);
        frame.Body.Children.Add(bar);

        change.Click += async (_, _) =>
        {
            if (await window.PickFolderAsync() is { } folder)
            {
                destination = System.IO.Path.Join(folder, project.Name);
                Render();
            }
        };
        download.Click += async (_, _) =>
        {
            working = true;
            window.SheetBusy = true;
            bar.Visibility = Visibility.Visible;
            download.IsEnabled = false;
            change.IsEnabled = false;
            cancelButton.Content = "Cancel download";
            Warn(frame.Footer, "Downloading… a large project can take a few minutes. DevSweep asks git, so nothing else on your PC is touched.", warning: false);
            DownloadResult result = await vm.DownloadAsync(project, strategy, destination, account, cts.Token);
            working = false;
            window.SheetBusy = false;
            if (result.Succeeded)
            {
                vm.CloseSheet();
                return;
            }

            bar.Visibility = Visibility.Collapsed;
            change.IsEnabled = true;
            cancelButton.Content = "Cancel";
            Render();
            Warn(frame.Footer, result.Message);
        };
        frame.Buttons.Children.Add(cancelButton);
        frame.Buttons.Children.Add(download);
        Render();
        return frame.Root;
    }

    private static UIElement AccountPicker(IReadOnlyList<string> accounts, string? selected, Action<string> picked)
    {
        var combo = new ComboBox { MinWidth = 220, Header = "Download as" };
        foreach (string a in accounts)
        {
            combo.Items.Add(a);
        }

        combo.SelectedIndex = Math.Max(0, accounts.ToList().FindIndex(a => a.Equals(selected, StringComparison.OrdinalIgnoreCase)));
        AutomationProperties.SetName(combo, "Account to download with");
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && accounts[combo.SelectedIndex] != selected)
            {
                picked(accounts[combo.SelectedIndex]);
            }
        };
        return combo;
    }

    // ---- Publish -----------------------------------------------------------------------------------------------------------------------

    private static UIElement Publish(ProjectsViewModel vm, Subscriptions subs, MainWindow window)
    {
        var frame = new SheetFrame(600);
        frame.Heading.Text = "Publish a folder to GitHub";
        frame.Subheading.Text = "Creates a new repository from a folder on this PC, with its first commit. Nothing is uploaded until you press Publish.";
        string? folder = vm.SheetProject?.LocalPath;
        PublishPreview? preview = null;
        bool acknowledged = false;
        bool working = false;
        bool checking = false;
        bool isPrivate = true;
        var cts = new CancellationTokenSource();
        subs.Add(cts.Dispose);

        var folderText = Ui.Text(folder ?? "No folder chosen", "DsMono");
        folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        folderText.MaxLines = 1;
        Button choose = Ui.Button("Choose…", () => { }, "Choose the folder to publish");
        var name = new TextBox { Header = "Repository name", PlaceholderText = "my-project" };
        AutomationProperties.SetName(name, "Repository name");
        var summary = new TextBox { Header = "Description (optional)" };
        AutomationProperties.SetName(summary, "Description");
        var account = new ComboBox { Header = "Publish as", MinWidth = 220 };
        foreach (string a in vm.Accounts)
        {
            account.Items.Add(a);
        }

        string? active = vm.Report?.Snapshot.Accounts?.Active?.Login;
        account.SelectedIndex = Math.Max(0, vm.Accounts.ToList().FindIndex(a => a.Equals(active, StringComparison.OrdinalIgnoreCase)));
        AutomationProperties.SetName(account, "Account to publish with");
        var details = new StackPanel { Spacing = Tokens.Space3, Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible };
        var publicNote = Ui.Secondary("Public means anyone can see the code and its whole history.");
        FrameworkElement visibility = Ds.Segmented(VisibilityChoices, 0, i =>
        {
            isPrivate = i == 0;
            Ui.Show(publicNote, !isPrivate);
        }, "Visibility");
        Ui.Show(publicNote, false);
        var checkHost = new Border();
        var bar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(bar, "Working");
        details.Children.Add(name);
        details.Children.Add(account);
        details.Children.Add(summary);
        details.Children.Add(Ui.V(Tokens.Space1, Ds.SectionTitle("Visibility"), visibility, publicNote));
        details.Children.Add(checkHost);
        frame.Body.Children.Add(Ui.V(Tokens.Space2, Ds.SectionTitle("Folder"), Ui.Row(Ui.Card(folderText, "DsCodeBox"), choose)));
        frame.Body.Children.Add(details);
        frame.Body.Children.Add(bar);

        Button cancelButton = Ui.Button("Cancel", () =>
        {
            if (working)
            {
                cts.Cancel();
            }
            else
            {
                vm.CloseSheet();
            }
        });
        Button publish = Ui.Button("Publish", () => { }, "Publish to GitHub", accent: true);

        PublishRequest Request() => new()
        {
            Folder = folder!,
            Account = account.SelectedItem as string ?? string.Empty,
            Name = name.Text.Trim(),
            Description = summary.Text.Trim().Length == 0 ? null : summary.Text.Trim(),
            IsPrivate = isPrivate,
            AcknowledgeSecrets = acknowledged,
        };

        void Refresh()
        {
            bool secrets = preview is { Secrets.Count: > 0 };
            publish.IsEnabled = folder is not null && !working && !checking && name.Text.Trim().Length > 0 && account.SelectedItem is string && preview is { Refusal: null } && (!secrets || acknowledged);
            Ui.Show(bar, working || checking);
            if (folder is null)
            {
                checkHost.Child = null;
                return;
            }

            if (checking)
            {
                checkHost.Child = Ui.Secondary("Checking which files would be uploaded, and whether any look like secrets…");
                return;
            }

            if (preview is null)
            {
                checkHost.Child = null;
            }
            else if (preview.Refusal is { } refusal)
            {
                checkHost.Child = Ds.Banner(BannerKind.Warning, refusal);
            }
            else if (secrets)
            {
                var box = new StackPanel { Spacing = Tokens.Space2 };
                box.Children.Add(Ui.Text(Words.Count(preview.Secrets.Count, "file") + " in this folder look like secrets", "DsStrong"));
                foreach (SecretFile file in preview.Secrets.Take(4))
                {
                    box.Children.Add(Ds.CheckRow(false, System.IO.Path.GetFileName(file.Path) + ", " + file.What));
                }

                if (preview.Secrets.Count > 4)
                {
                    box.Children.Add(Ui.Caption($"and {preview.Secrets.Count - 4} more"));
                }

                box.Children.Add(Ui.Secondary("Everything in the folder is committed, and a repository keeps its history even after a file is deleted. Add these to .gitignore or move them out of the folder first."));
                var confirm = new CheckBox { Content = "Publish anyway", IsChecked = acknowledged };
                AutomationProperties.SetName(confirm, "Publish anyway, even though these files look like secrets");
                confirm.Checked += (_, _) =>
                {
                    acknowledged = true;
                    Refresh();
                };
                confirm.Unchecked += (_, _) =>
                {
                    acknowledged = false;
                    Refresh();
                };
                box.Children.Add(confirm);
                checkHost.Child = Ui.Card(box, "DsWarningBox");
            }
            else
            {
                checkHost.Child = Ds.CheckRow(true, $"{Words.Count(preview.Files.Count, "file")} will be uploaded; none look like secrets (.gitignore respected)");
            }
        }

        async Task CheckAsync()
        {
            if (folder is null || name.Text.Trim().Length == 0 || account.SelectedItem is not string)
            {
                preview = null;
                Refresh();
                return;
            }

            checking = true;
            preview = null;
            acknowledged = false;
            Refresh();
            preview = await vm.PreviewPublishAsync(Request(), cts.Token);
            checking = false;
            Refresh();
        }

        void Chosen(string picked)
        {
            folder = picked;
            folderText.Text = picked;
            name.Text = System.IO.Path.GetFileName(picked.TrimEnd('\\')).Replace(' ', '-');
            Ui.Show(details, true);
            _ = CheckAsync();
        }

        choose.Click += async (_, _) =>
        {
            if (await window.PickFolderAsync() is { } picked)
            {
                Chosen(picked);
            }
        };
        name.TextChanged += (_, _) => Refresh();
        account.SelectionChanged += (_, _) => _ = CheckAsync();
        publish.Click += async (_, _) =>
        {
            working = true;
            window.SheetBusy = true;
            Refresh();
            cancelButton.Content = "Cancel publish";
            Warn(frame.Footer, "Publishing… creating the repository and pushing the first commit.", warning: false);
            PublishResult result = await vm.PublishAsync(Request(), cts.Token);
            working = false;
            window.SheetBusy = false;
            if (result.Succeeded)
            {
                vm.CloseSheet();
                return;
            }

            cancelButton.Content = "Cancel";
            Refresh();
            Warn(frame.Footer, result.Message);
        };
        frame.Buttons.Children.Add(cancelButton);
        frame.Buttons.Children.Add(publish);
        if (folder is not null)
        {
            Chosen(folder);
        }
        else
        {
            Refresh();
        }

        return frame.Root;
    }

    // ---- Add by URL ---------------------------------------------------------------------------------------------------------------------

    private static UIElement AddByUrl(ProjectsViewModel vm, Subscriptions subs, MainWindow window)
    {
        var frame = new SheetFrame(520);
        frame.Heading.Text = "Add a project by URL";
        frame.Subheading.Text = "For a repository that isn't in your own lists, such as a fork or a team repo. DevSweep finds which of your signed-in accounts can see it, and it appears under On GitHub, ready to download.";
        var text = new TextBox { PlaceholderText = "github.com/owner/name or owner/name" };
        AutomationProperties.SetName(text, "Repository address");
        var hint = Ui.Caption(string.Empty);
        var bar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        bool working = false;
        var cts = new CancellationTokenSource();
        subs.Add(cts.Dispose);
        Button cancel = Ui.Button("Cancel", vm.CloseSheet);
        Button add = Ui.Button("Add", () => { }, "Add the repository", accent: true);

        void Check()
        {
            string? slug = RemoteUrls.ParseSlug(text.Text);
            hint.Text = text.Text.Trim().Length == 0 ? string.Empty
                : slug is null ? "That doesn't look like a GitHub repository yet. Use owner/name or a github.com address."
                : "Looks like " + slug;
            add.IsEnabled = slug is not null && !working;
        }

        text.TextChanged += (_, _) =>
        {
            Check();
            Warn(frame.Footer, null);
        };
        add.Click += async (_, _) =>
        {
            working = true;
            window.SheetBusy = true;
            Check();
            bar.Visibility = Visibility.Visible;
            (RemoteRepo? repo, string? error) = await vm.AddByUrlAsync(text.Text, cts.Token);
            working = false;
            window.SheetBusy = false;
            bar.Visibility = Visibility.Collapsed;
            if (repo is not null)
            {
                vm.CloseSheet();
                return;
            }

            Check();
            Warn(frame.Footer, error ?? "Couldn't add it.");
        };
        frame.Body.Children.Add(text);
        frame.Body.Children.Add(hint);
        frame.Body.Children.Add(bar);
        frame.Buttons.Children.Add(cancel);
        frame.Buttons.Children.Add(add);
        Check();
        return frame.Root;
    }

    // ---- Add account -----------------------------------------------------------------------------------------------------------------------

    private static UIElement AddAccount(ProjectsViewModel vm, Subscriptions subs)
    {
        var frame = new SheetFrame(520);
        frame.Heading.Text = "Add a GitHub account";
        frame.Subheading.Text = "Opens GitHub's sign-in in a window. The commit identity below is saved for the account first, so projects you download with it are attributed correctly from the start.";
        var user = new TextBox { Header = "GitHub username", PlaceholderText = "octocat" };
        var name = new TextBox { Header = "Name for commits", PlaceholderText = "Octo Cat" };
        var email = new TextBox { Header = "Email for commits", PlaceholderText = "octo@example.com" };
        AutomationProperties.SetName(user, "GitHub username");
        AutomationProperties.SetName(name, "Name for commits");
        AutomationProperties.SetName(email, "Email for commits");
        Button cancel = Ui.Button("Cancel", vm.CloseSheet);
        Button open = Ui.Button("Open sign-in in a window", () => { }, "Open GitHub's sign-in in a visible window", accent: true);

        void Check()
        {
            string login = user.Text.Trim();
            bool identity = name.Text.Trim().Length > 0 || email.Text.Trim().Length > 0;
            bool loginOk = login.Length == 0 || GitHubCli.IsLogin(login);
            open.IsEnabled = loginOk;
            Warn(frame.Footer,
                !loginOk ? "That isn't a valid GitHub username (letters, numbers and single hyphens)."
                : identity && login.Length == 0 ? "Enter the username too, so the name and email can be saved for that account."
                : null);
            if (identity && login.Length == 0)
            {
                open.IsEnabled = false;
            }
        }

        user.TextChanged += (_, _) => Check();
        name.TextChanged += (_, _) => Check();
        email.TextChanged += (_, _) => Check();
        open.Click += (_, _) =>
        {
            string login = user.Text.Trim();
            var identity = new GitIdentity(name.Text.Trim(), email.Text.Trim());
            vm.SignIn(login.Length == 0 ? null : login, identity.IsBlank ? null : identity);
            vm.CloseSheet();
        };
        frame.Body.Children.Add(user);
        frame.Body.Children.Add(name);
        frame.Body.Children.Add(email);
        frame.Body.Children.Add(Ui.Secondary("The window waits for you to press Enter, then runs GitHub's own sign-in in your browser. When you've finished, press Refresh on the Accounts tab and the account appears. The account your terminal uses stays the same."));
        frame.Buttons.Children.Add(cancel);
        frame.Buttons.Children.Add(open);
        Check();
        return frame.Root;
    }
}
