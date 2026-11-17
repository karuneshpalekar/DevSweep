using System.Globalization;
using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Views;

/// <summary>The side panels of Health: a tool ("What DevSweep noticed", Installed, What you can do), the package managers, project requirements, a security finding and a port.</summary>
internal static class HealthPanels
{
    public static UIElement Build(MainViewModel model, string key, Subscriptions subs, MainWindow window)
    {
        HealthViewModel vm = model.Health;
        string[] parts = key.Split(':', 2);
        string id = parts[^1];
        return parts[0] switch
        {
            "sec" => vm.Report?.Security?.Findings.FirstOrDefault(f => f.Id == id) is { } finding ? Security(vm, finding, subs) : new Grid(),
            "port" => vm.Report?.Ports?.Ports.FirstOrDefault(p => p.Id == id) is { } port ? Port(vm, port, subs) : new Grid(),
            _ => id switch
            {
                "packages" => Packages(vm, subs),
                "projects" => Projects(vm),
                _ => vm.AllToolRows.FirstOrDefault(r => r.Id == id)?.Report is { } report ? Tool(vm, report) : new Grid(),
            },
        };
    }

    // ---- Shared pieces ------------------------------------------------------------------------------------------------------

    private static Button Close(HealthViewModel vm) => Ui.IconButton(Icons.Close, "Close details (Esc)", () =>
    {
        switch (vm.Tab)
        {
            case HealthTab.Tools:
                vm.SelectedTool = null;
                break;
            case HealthTab.Security:
                vm.SelectedSecurity = null;
                break;
            default:
                vm.SelectedPort = null;
                break;
        }
    });

    private static ScrollViewer Frame(StackPanel content) => Ui.Scroll(content, Tokens.Space6);

    private static StackPanel Panel() => new() { Spacing = Tokens.Space4 };

    private static TextBlock Selectable(TextBlock block, bool wrap = true)
    {
        block.IsTextSelectionEnabled = true;
        block.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        return block;
    }

    private static UIElement Section(string title, params UIElement?[] children)
    {
        StackPanel box = Ui.V(Tokens.Space2, Ds.SectionTitle(title));
        foreach (UIElement? child in children)
        {
            if (child is not null)
            {
                box.Children.Add(child);
            }
        }

        return box;
    }

    private static UIElement Header(HealthViewModel vm, string title, string? subtitle, UIElement? tag = null)
    {
        StackPanel text = Ui.V(Tokens.Space1, Ui.Text(title, "DsSubtitle"));
        if (subtitle is not null)
        {
            text.Children.Add(Selectable(Ui.Secondary(subtitle)));
        }

        return Ui.Row(text, tag, Close(vm));
    }

    /// <summary>A block that is rebuilt whenever one of the view model's properties changes (the panel's action state).</summary>
    private static Border Live(Subscriptions subs, HealthViewModel vm, Func<UIElement> build, params string[] properties)
    {
        var host = new Border();
        void Update() => host.Child = build();
        Update();
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name && properties.Contains(name))
            {
                Update();
            }
        }

        vm.PropertyChanged += Handler;
        subs.Add(() => vm.PropertyChanged -= Handler);
        return host;
    }

    private static UIElement IssueRow(RuntimeIssue issue)
    {
        (string glyph, string brush) = issue.Level switch
        {
            IssueLevel.Critical => (Icons.Error, "SystemFillColorCriticalBrush"),
            IssueLevel.Warning => (Icons.Warning, "SystemFillColorCautionBrush"),
            _ => (Icons.Info, "TextFillColorSecondaryBrush"),
        };
        FontIcon icon = Ui.Icon(glyph, 14);
        Ui.Tint(icon, brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        TextBlock text = Selectable(Ui.Text(issue.Text));
        var row = new Grid { ColumnSpacing = Tokens.Space2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        AutomationProperties.SetName(row, issue.Text);
        return row;
    }

    private static UIElement Noticed(IEnumerable<RuntimeIssue> issues, string? resolution = null)
    {
        RuntimeIssue[] list = [.. issues];
        if (list.Length == 0 && resolution is null)
        {
            return Ui.Secondary("Nothing to flag.");
        }

        StackPanel box = Ui.V(Tokens.Space3);
        foreach (RuntimeIssue issue in list)
        {
            box.Children.Add(IssueRow(issue));
        }

        if (resolution is not null)
        {
            box.Children.Add(IssueRow(new RuntimeIssue(IssueLevel.Info, resolution)));
        }

        return box;
    }

    /// <summary>What you can do: the commands, Copy, and Run in PowerShell in a visible window that waits for Enter.</summary>
    internal static UIElement StepCard(HealthViewModel vm, RuntimeStep step)
    {
        string glyph = step.Kind switch { StepKind.Upgrade => Icons.Up, StepKind.Remove => Icons.Delete, StepKind.SwitchDefault => Icons.Refresh, _ => Icons.Info };
        FontIcon icon = Ui.Icon(glyph, 14);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var commands = new StackPanel { Spacing = Tokens.Space1 };
        bool long_ = step.Commands.Count > 4;
        bool showAll = false;
        void Fill()
        {
            commands.Children.Clear();
            IEnumerable<string> shown = showAll || !long_ ? step.Commands : step.Commands.Take(4);
            commands.Children.Add(Selectable(Ui.Text(string.Join(Environment.NewLine, shown), "DsMono")));
        }

        Fill();
        var more = Ui.Link(string.Empty, () => { });
        if (long_)
        {
            more.Content = $"Show the full script ({step.Commands.Count} lines)";
            more.Click += (_, _) =>
            {
                showAll = !showAll;
                more.Content = showAll ? "Show less" : $"Show the full script ({step.Commands.Count} lines)";
                Fill();
            };
        }

        string run = step.NeedsAdmin ? "Run in PowerShell (asks for admin)" : "Run in PowerShell";
        Button runButton = Ui.Button(run, () => vm.OpenFix(step), $"Open {step.Title} in PowerShell", accent: true);
        Button copy = Ui.Button("Copy", () => Shell.Copy(string.Join(Environment.NewLine, step.Commands)), $"Copy the commands for {step.Title}");
        UIElement? admin = step.NeedsAdmin
            ? Ui.H(Tokens.Space1, Ui.Icon(Icons.Lock, 12), Ui.Caption("Needs an administrator. Windows asks first."))
            : null;
        StackPanel content = Ui.V(Tokens.Space2,
            StepTitle(icon, step.Title),
            Ui.Secondary(step.Detail),
            Ui.Card(commands, "DsCodeBox"),
            long_ ? more : null,
            admin,
            FlowButtons.Build(copy, runButton));
        Border card = Ds.Card(content, Tokens.Space3);
        AutomationProperties.SetName(card, step.Title);
        return card;
    }

    private static UIElement StepTitle(FontIcon icon, string title)
    {
        TextBlock text = Ui.Text(title, "DsStrong");
        text.TextWrapping = TextWrapping.Wrap;
        var row = new Grid { ColumnSpacing = Tokens.Space2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    private static UIElement Steps(HealthViewModel vm, IReadOnlyList<RuntimeStep> steps)
    {
        StackPanel box = Ui.V(Tokens.Space3);
        if (steps.Count == 0)
        {
            box.Children.Add(Ui.Secondary("Nothing needed right now."));
        }

        foreach (RuntimeStep step in steps)
        {
            box.Children.Add(StepCard(vm, step));
        }

        if (steps.Count > 0)
        {
            box.Children.Add(Ui.Caption("Run in PowerShell opens a window that lists the commands and waits for you to press Enter. Nothing runs before that, and nothing is elevated without Windows asking you (UAC)."));
        }

        return Section("What you can do", box);
    }

    private static UIElement Line(string label, string value)
    {
        TextBlock name = Ui.Caption(label);
        TextBlock text = Selectable(Ui.Caption(value));
        Ui.Tint(text, "TextFillColorPrimaryBrush");
        var row = new Grid { ColumnSpacing = Tokens.Space2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        return row;
    }

    // ---- A tool -------------------------------------------------------------------------------------------------------------

    private static UIElement Tool(HealthViewModel vm, RuntimeReport report)
    {
        StackPanel panel = Panel();
        panel.Children.Add(Header(vm, report.Name, report.Recommended is { } r ? "Recommended: " + r : null));
        string? runs = report.Resolution?.Runs is { } path ? $"A new terminal runs {path}{(report.Resolution.Via is { } via ? $" (via {via})" : string.Empty)}." : null;
        panel.Children.Add(Section("What DevSweep noticed", Noticed(report.Issues, runs)));
        panel.Children.Add(Steps(vm, report.Steps));

        if (report.Installs.Count > 0)
        {
            StackPanel installs = Ui.V(Tokens.Space2);
            foreach (Installation install in report.Installs)
            {
                installs.Children.Add(InstallCard(install));
            }

            panel.Children.Add(Section("Installed", installs));
        }

        if (report.NotInstalls.Count > 0)
        {
            StackPanel notes = Ui.V(Tokens.Space2);
            foreach (string note in report.NotInstalls)
            {
                notes.Children.Add(Ds.Card(Ui.H(Tokens.Space2, Ui.Icon(Icons.Info, 14), Selectable(Ui.Secondary(note))), Tokens.Space3));
            }

            panel.Children.Add(Section("Not an install", notes));
        }

        IEnumerable<ProjectRequirement> requirements = vm.Report?.Versions?.Projects.Where(p => string.Equals(p.Tool, report.Id, StringComparison.Ordinal)) ?? [];
        ProjectRequirement[] needing = [.. requirements];
        if (needing.Length > 0)
        {
            panel.Children.Add(Section("Projects that need it", RequirementCards(vm, needing)));
        }

        if (report.Note is { } note2)
        {
            panel.Children.Add(Ui.Caption(note2));
        }

        if (report.DataSource is { } source)
        {
            panel.Children.Add(Ui.Caption($"Support dates: {source}{(report.CheckedAt is { } at ? ", checked " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : string.Empty)}"));
        }

        return Frame(panel);
    }

    private static UIElement InstallCard(Installation install)
    {
        Border support = Ds.StatusTag(HealthView.SupportText(install.Support), HealthView.SupportTone(install.Support));
        var tags = Ui.H(Tokens.Space1, support);
        if (install.IsDefault)
        {
            tags.Children.Add(Ds.StatusTag("A new terminal runs this", Tone.Accent, "It's first on the PATH Explorer gives new programs."));
        }

        if (install.IsRunning is { } running)
        {
            tags.Children.Add(Ds.StatusTag(running ? "Running" : "Not running", running ? Tone.Good : Tone.Neutral));
        }

        TextBlock version = Ui.Text(install.Version, "DsStrong");
        StackPanel box = Ui.V(Tokens.Space1, version, FlowButtons.Build(tags));
        box.Children.Add(Line("Installed by", install.SourceTitle + (install.SourceDetail is { } detail ? $" ({detail})" : string.Empty)));
        if (install.ReleaseDate is { } released)
        {
            box.Children.Add(Line("Released", released.ToString("d MMM yyyy", CultureInfo.CurrentCulture)));
        }

        if (install.SupportEnds is { } ends)
        {
            box.Children.Add(Line(install.Support == SupportStatus.EndOfLife ? "Support ended" : "Support ends", ends.ToString("d MMM yyyy", CultureInfo.CurrentCulture)));
        }

        if (install.PatchAvailable && install.LatestInCycle is { } latest)
        {
            box.Children.Add(Line("Latest patch", latest));
        }

        if (install.DataPath is { } data)
        {
            box.Children.Add(Line("Data", data));
        }

        box.Children.Add(Selectable(Ui.Text(install.Path, "DsMono")));
        string label = $"{install.Version} from {install.SourceTitle}";
        box.Children.Add(FlowButtons.Build(
            Ui.Button("Show in File Explorer", () => Shell.Reveal(install.Path), $"Show {label} in File Explorer"),
            Ui.Button("Copy path", () => Shell.Copy(install.Path), $"Copy the path of {label}")));
        Border card = Ds.Card(box, Tokens.Space3);
        AutomationProperties.SetName(card, label);
        return card;
    }

    // ---- Projects that ask for tools ----------------------------------------------------------------------------------------

    private static UIElement RequirementCards(HealthViewModel vm, IEnumerable<ProjectRequirement> requirements)
    {
        StackPanel box = Ui.V(Tokens.Space2);
        foreach (ProjectRequirement requirement in requirements)
        {
            Tone tone = requirement.Status switch { RequirementStatus.Ok => Tone.Good, RequirementStatus.EndOfLife => Tone.Critical, _ => Tone.Caution };
            string status = requirement.Status switch { RequirementStatus.Ok => "OK", RequirementStatus.NotInstalled => "Not installed", _ => "End of life" };
            StackPanel card = Ui.V(Tokens.Space1,
                Ui.Row(Ui.Text(requirement.Project, "DsStrong"), Ds.StatusTag(status, tone)),
                Ui.Caption($"Asks for {requirement.ToolName} {requirement.Spec} · {requirement.File}"),
                Selectable(Ui.Secondary(requirement.Message)));
            if (requirement.Fix is { Count: > 0 } fix)
            {
                card.Children.Add(StepCard(vm, new RuntimeStep
                {
                    Id = $"requirement:{requirement.Tool}:{requirement.Project}",
                    Kind = StepKind.Upgrade,
                    Title = $"Install {requirement.ToolName} {requirement.Spec}",
                    Detail = "Installs it alongside what you have; your default doesn't change unless the commands say so.",
                    Commands = fix,
                }));
            }

            Border outer = Ds.Card(card, Tokens.Space3);
            AutomationProperties.SetName(outer, $"{requirement.Project} asks for {requirement.ToolName} {requirement.Spec}: {status}");
            box.Children.Add(outer);
        }

        return box;
    }

    private static UIElement Projects(HealthViewModel vm)
    {
        StackPanel panel = Panel();
        panel.Children.Add(Header(vm, "Your projects", "Versions your projects ask for, from files like .nvmrc, package.json engines, .python-version, global.json and Gradle toolchains."));
        panel.Children.Add(RequirementCards(vm, vm.Report?.Versions?.Projects ?? []));
        panel.Children.Add(Ui.Caption("Project folders can be changed in Settings, Folders."));
        return Frame(panel);
    }

    // ---- Package managers ---------------------------------------------------------------------------------------------------

    private static UIElement Packages(HealthViewModel vm, Subscriptions subs)
    {
        return Live(subs, vm, () =>
        {
            PackagesResult? packages = vm.Packages;
            StackPanel panel = Panel();
            string subtitle = packages is null ? "Checking…" : $"{packages.Outdated} outdated · {packages.Deprecated} no longer offered";
            panel.Children.Add(Header(vm, "Package managers", subtitle));
            if (packages is null)
            {
                panel.Children.Add(Ds.Skeleton(3));
                return Frame(panel);
            }

            if (packages.Duplicates.Count > 0)
            {
                panel.Children.Add(Section("Installed twice", [.. packages.Duplicates.Select(d => IssueRow(new RuntimeIssue(IssueLevel.Warning, d.Text)))]));
            }

            foreach (PackageManagerReport manager in packages.Managers)
            {
                panel.Children.Add(Ds.Divider());
                panel.Children.Add(Manager(vm, manager));
            }

            panel.Children.Add(Ui.Caption("DevSweep only reads what each manager says. Nothing is upgraded or removed until you run a step, in a window you can see."));
            return Frame(panel);
        }, nameof(HealthViewModel.Packages));
    }

    private static UIElement Manager(HealthViewModel vm, PackageManagerReport manager)
    {
        StackPanel box = Ui.V(Tokens.Space3);
        string installed = manager.InstalledCount is { } n ? $" · {n} installed" : string.Empty;
        box.Children.Add(Ui.V(Tokens.Space1, Ui.Text(manager.Name, "DsSubtitle"), manager.Location is { } where ? Selectable(Ui.Caption(where + installed)) : Ui.Caption(installed.TrimStart(' ', '·'))));
        if (manager.Problem is { } problem)
        {
            box.Children.Add(Ds.Banner(BannerKind.Info, problem, "Couldn't read the result"));
        }

        if (manager.Deprecated.Count > 0)
        {
            StackPanel list = Ui.V(Tokens.Space2);
            foreach (DeprecatedPackage package in manager.Deprecated)
            {
                list.Children.Add(Ui.V(0, Ui.Text($"{package.Name} {package.Installed}", "DsStrong"), Selectable(Ui.Caption(package.Reason))));
            }

            box.Children.Add(Section("Deprecated", list));
        }

        if (manager.Outdated.Count > 0)
        {
            var rows = new StackPanel { Spacing = Tokens.Space1 };
            foreach (OutdatedPackage package in manager.Outdated)
            {
                TextBlock versions = Ui.Caption($"{package.Installed} → {package.Latest}");
                versions.TextWrapping = TextWrapping.NoWrap;
                var tags = Ui.H(Tokens.Space2, package.Pinned ? Ds.StatusTag("Held back", Tone.Neutral, "The manager is told to leave it alone.") : null, versions);
                rows.Children.Add(Ui.Row(Ui.Text(package.Name, "DsBody", 1), tags));
            }

            var expander = new Expander
            {
                Header = $"{manager.Outdated.Count} outdated package{(manager.Outdated.Count == 1 ? string.Empty : "s")}",
                Content = rows,
                IsExpanded = manager.Outdated.Count <= 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(expander, $"{manager.Name} outdated packages");
            box.Children.Add(Section("Outdated", expander));
        }

        if (manager.Caches.Count > 0)
        {
            StackPanel list = Ui.V(Tokens.Space2);
            foreach (PackageCache cache in manager.Caches)
            {
                list.Children.Add(Ui.Row(Ui.V(0, Ui.Text(cache.Title, "DsStrong"), Selectable(Ui.Caption(cache.Path))),
                    Ui.Text((cache.Exact ? string.Empty : "at least ") + SizeLabels.Size(cache.Bytes)),
                    Ui.IconButton(Icons.OpenFolder, $"Show {cache.Title} in File Explorer", () => Shell.Reveal(cache.Path))));
            }

            box.Children.Add(Section("Caches", list));
        }

        if (manager.Orphans.Count > 0)
        {
            StackPanel list = Ui.V(Tokens.Space2);
            foreach (OrphanFolder orphan in manager.Orphans)
            {
                list.Children.Add(Ui.Row(Ui.V(0, Selectable(Ui.Text(orphan.Path, "DsMono")), Ui.Caption(orphan.Why)),
                    Ui.IconButton(Icons.OpenFolder, $"Show {orphan.Path} in File Explorer", () => Shell.Reveal(orphan.Path))));
            }

            box.Children.Add(Section("Leftover folders", list));
        }

        if (manager.Steps.Count > 0)
        {
            StackPanel steps = Ui.V(Tokens.Space3);
            foreach (RuntimeStep step in manager.Steps)
            {
                steps.Children.Add(StepCard(vm, step));
            }

            box.Children.Add(Section("What you can do", steps));
        }
        else if (manager.Problem is null)
        {
            box.Children.Add(Ds.CheckRow(true, "Nothing outdated, nothing removed."));
        }

        if (manager.Note is { } note)
        {
            box.Children.Add(Ui.Caption(note));
        }

        return box;
    }

    // ---- A security finding -------------------------------------------------------------------------------------------------

    private static UIElement Security(HealthViewModel vm, SecurityFinding finding, Subscriptions subs)
    {
        Tone tone = finding.Level switch { SecurityLevel.Critical => Tone.Critical, SecurityLevel.Warning => Tone.Caution, _ => Tone.Good };
        StackPanel panel = Panel();
        panel.Children.Add(Header(vm, finding.Title, null, Ds.StatusTag(finding.Status, tone)));
        panel.Children.Add(Selectable(Ui.Caption(finding.Path)));
        panel.Children.Add(Section("Why it matters", Selectable(Ui.Text(finding.Why))));
        panel.Children.Add(Section("What to do", Selectable(Ui.Text(finding.WhatToDo))));
        if (finding.Kind == SecurityKind.EnvFile)
        {
            panel.Children.Add(Section("Git", Ui.Text(finding.Status switch
            {
                "Committed to git" => "Git tracks this file, so it is in the project's history.",
                "Not ignored" => "Git doesn't track it yet, but .gitignore doesn't list it either.",
                "Kept out of git" => "Git ignores it.",
                "Not in git" => "The folder isn't a git repository.",
                _ => "Whether git tracks or ignores it is not known.",
            })));
        }

        if (finding.Checks.Count > 0)
        {
            panel.Children.Add(Section("What DevSweep checked", [.. finding.Checks.Select(c => Ds.CheckRow(c.Status == CheckStatus.Passed, c.Text))]));
        }

        if (finding.Fix is { Count: > 0 } fix)
        {
            panel.Children.Add(StepCard(vm, new RuntimeStep
            {
                Id = "security-fix:" + finding.Kind,
                Kind = StepKind.Remove,
                Title = finding.Kind switch { SecurityKind.PrivateKey or SecurityKind.PuttyKey => "Move it to your .ssh folder", SecurityKind.EnvFile => "Fix it in git", _ => "Fix it" },
                Detail = "Runs in PowerShell. You see the commands first.",
                Commands = fix,
            }));
        }

        panel.Children.Add(Ui.Caption("Moving to the Recycle Bin goes through DevSweep's cleaner and is logged in History, where you can put it back. Ignoring hides it until you undo that in Settings, Ignored items. DevSweep never shows or stores the secret itself."));
        var footer = new PanelFooter("Finding actions");
        footer.Set(Live(subs, vm, () =>
        {
            string? blocked = vm.TrashBlockedReason(finding);
            Button trash = Ui.Button("Move to Recycle Bin", () => _ = vm.TrashAsync(finding), $"Move {finding.Title} to the Recycle Bin");
            trash.IsEnabled = blocked is null;
            StackPanel box = Ui.V(Tokens.Space2);
            if (vm.Notice is { } notice)
            {
                box.Children.Add(Ds.Banner(BannerKind.Info, notice));
            }

            box.Children.Add(FlowButtons.Build(
                trash,
                Ui.Button("Show in File Explorer", () => Shell.Reveal(finding.Path), $"Show {finding.Title} in File Explorer"),
                Ui.Button("Ignore", () => vm.Ignore(finding), $"Ignore {finding.Title}")));
            if (blocked is not null)
            {
                box.Children.Add(Ui.Caption(blocked));
            }

            return box;
        }, nameof(HealthViewModel.Notice)));
        return PanelFrame.Build(Frame(panel), footer);
    }

    // ---- A port -------------------------------------------------------------------------------------------------------------

    private static string FolderNote(ListeningPort port) => port.FolderSource switch
    {
        FolderSource.WorkingDirectory => "from the process's working folder",
        FolderSource.CommandLine => "from a path in its command line",
        FolderSource.ProgramPath => "from where its program is stored",
        _ => "a best guess",
    };

    private static UIElement Port(HealthViewModel vm, ListeningPort port, Subscriptions subs)
    {
        StackPanel panel = Panel();
        panel.Children.Add(Ui.Row(Ui.Text("Port " + port.Port.ToString(CultureInfo.InvariantCulture), "DsCaption"), Close(vm)));
        panel.Children.Add(Ui.Text(port.Label, "DsSubtitle"));
        StackPanel lines = Ui.V(Tokens.Space1, Line("Process", $"{port.ProcessName} (PID {port.Pid})"));
        if (port.Started is { } started)
        {
            lines.Children.Add(Line("Started", Words.Relative(started, vm.Now)));
        }

        if (port.Folder is { } folder)
        {
            lines.Children.Add(Line("Folder", folder));
            lines.Children.Add(Line(string.Empty, $"Project folder {FolderNote(port)}."));
        }

        lines.Children.Add(Line("Reachable", port.ReachableFromNetwork ? "From other devices on your network" : "Only from this PC"));
        if (port.Firewall is { } firewall && port.ReachableFromNetwork)
        {
            lines.Children.Add(Line("Firewall", firewall.Summary));
        }

        panel.Children.Add(lines);
        if (port.ReachableFromNetwork && port.IsDevelopment)
        {
            panel.Children.Add(Ds.Banner(BannerKind.Warning, "It listens on every network interface. That's handy for testing on a phone, but on public Wi-Fi others can connect too"
                + (port.Firewall is { Known: true } fw ? $" ({fw.Summary})" : string.Empty) + ". Most dev servers have an option to listen on localhost only.", "Reachable from your network"));
        }

        if (port.CommandLine is { Length: > 0 } command)
        {
            panel.Children.Add(Section("Command", Ui.Card(Selectable(Ui.Text(command, "DsMono")), "DsCodeBox")));
        }

        string? url = HealthViewModel.UrlFor(port);
        string address = url ?? $"localhost:{port.Port}";
        StackPanel actions = Ui.V(Tokens.Space2);
        UIElement[] buttons =
        [
            .. url is null ? [] : new UIElement[] { Ui.Button("Open in browser", () => Shell.OpenUrl(url), $"Open {url} in your browser") },
            Ui.Button("Copy URL", () => Shell.Copy(address), $"Copy {address}"),
            Ui.Button("Copy port", () => Shell.Copy(port.Port.ToString(CultureInfo.InvariantCulture)), $"Copy port {port.Port}"),
            .. port.Folder is { } f && Directory.Exists(f) ? new UIElement[] { Ui.Button("Show folder", () => Shell.Reveal(f), $"Show {f} in File Explorer") } : [],
        ];
        actions.Children.Add(FlowButtons.Build(buttons));
        panel.Children.Add(actions);

        bool confirming = false;
        string? refusal = vm.StopRefusal(port);
        var stopHost = new Border();
        void Redraw() => stopHost.Child = StopBlock();
        UIElement StopBlock()
        {
            StackPanel box = Ui.V(Tokens.Space2, Ds.SectionTitle("Stop it"));
            if (refusal is not null)
            {
                box.Children.Add(Ds.Banner(BannerKind.Info, refusal, "DevSweep leaves this one alone"));
                return box;
            }

            switch (vm.Stop)
            {
                case StopPhase.Asking:
                    box.Children.Add(Ui.H(Tokens.Space3, new ProgressRing { IsActive = true, Width = 18, Height = 18 }, Ui.Secondary(vm.StopMessage ?? "Stopping…")));
                    break;
                case StopPhase.ConfirmEnd:
                    box.Children.Add(Ds.Banner(BannerKind.Warning, (vm.StopMessage ?? $"{port.ProcessName} is still running.") + " Ending it now can't be undone and unsaved work in it is lost.", "It's still running"));
                    box.Children.Add(FlowButtons.Build(
                        Ui.Button("End it now", () => _ = vm.EndAsync(port), $"End {port.ProcessName} now", accent: true),
                        Ui.Button("Leave it running", vm.CancelStop, "Leave it running")));
                    break;
                default:
                    if (vm.StopMessage is { } done)
                    {
                        box.Children.Add(Ds.Banner(BannerKind.Info, done));
                    }

                    if (confirming)
                    {
                        box.Children.Add(Ui.Secondary($"Stop {port.Label}? This is like pressing Ctrl+C in its terminal. Unsaved work in that program may be lost."));
                        box.Children.Add(FlowButtons.Build(
                            Ui.Button("Stop", () =>
                            {
                                confirming = false;
                                _ = vm.StopAsync(port);
                            }, $"Stop {port.Label}", accent: true),
                            Ui.Button("Cancel", () =>
                            {
                                confirming = false;
                                vm.CancelStop();
                                Redraw();
                            }, "Cancel")));
                    }
                    else
                    {
                        box.Children.Add(Ui.Button("Stop…", () =>
                        {
                            confirming = true;
                            vm.CancelStop();
                            Redraw();
                        }, $"Stop {port.Label}"));
                        box.Children.Add(Ui.Caption("DevSweep asks it to stop the way Ctrl+C does. If it ignores that, you decide whether to end it."));
                    }

                    break;
            }

            return box;
        }

        void OnStop(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(HealthViewModel.Stop) or nameof(HealthViewModel.StopMessage))
            {
                Redraw();
            }
        }

        vm.PropertyChanged += OnStop;
        subs.Add(() => vm.PropertyChanged -= OnStop);
        Redraw();
        var footer = new PanelFooter("Stop the program");
        footer.Set(stopHost);
        return PanelFrame.Build(Frame(panel), footer);
    }
}
