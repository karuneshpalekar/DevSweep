using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Editors;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSweep.App.Views;

/// <summary>
/// The detail side panel: name and where it lives, description, Open in editor, Show in File Explorer, Open on GitHub, Download, the
/// "Is it safe to remove from this PC?" checklist with what it was compared with, a plain verdict, and Publish, Push or Remove.
/// WSL projects get only what is safe to offer from Windows, and the panel says why.
/// </summary>
internal static class ProjectPanel
{
    public static UIElement Build(MainViewModel model, ProjectsViewModel vm, Project first, Subscriptions subs, MainWindow window)
    {
        var host = new Border();
        void Render()
        {
            Project project = vm.All.FirstOrDefault(p => p.Id == first.Id) ?? first;
            host.Child = Content(model, vm, project, subs, window);
        }

        subs.On(vm, nameof(ProjectsViewModel.Report), Render);
        return Ui.Scroll(host, Tokens.Space6);
    }

    private static UIElement Content(MainViewModel model, ProjectsViewModel vm, Project project, Subscriptions subs, MainWindow window)
    {
        var panel = new StackPanel { Spacing = Tokens.Space4 };

        // Header: name, owner · private · clone strategy, close.
        Button close = Ui.IconButton(Icons.Close, "Close details (Esc)", () => vm.SelectedId = null);
        TextBlock name = Ui.Text(project.Name, "DsSubtitle");
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level2);
        panel.Children.Add(Ui.Row(Ui.V(Tokens.Space1, name, Ui.Caption(ProjectsViewModel.PanelSubtitle(project))), close));
        if (project.Description.Length > 0)
        {
            panel.Children.Add(Ui.Secondary(project.Description));
        }

        if (project.OnDisk)
        {
            TextBlock path = Ui.Caption(project.LocalPath!);
            path.IsTextSelectionEnabled = true;
            panel.Children.Add(path);
        }

        // What can be done with it.
        var buttons = new List<UIElement?>();
        if (project.OnDisk && !project.IsWsl)
        {
            string editor = EditorDetector.Choose(vm.Source?.Editors ?? [], vm.Source?.Preferences.DefaultEditor)?.Name ?? "File Explorer";
            Button open = Ui.Button("Open in editor", () => vm.OpenInEditor(project), $"Open {project.Name} in editor ({editor})");
            Ui.Tip(open, "Opens in " + editor + ". Change the editor in Settings.");
            buttons.Add(open);
        }

        if (project.OnDisk)
        {
            buttons.Add(Ui.Button("Show in File Explorer", () => Shell.Reveal(project.LocalPath!)));
        }
        else
        {
            buttons.Add(Ui.Button("Download…", () => vm.OpenSheet(ProjectSheet.Clone, project), $"Download {project.NameWithOwner}", accent: true));
        }

        if (project.OnGitHub)
        {
            buttons.Add(Ui.Button("Open on GitHub", () => Shell.OpenUrl(ProjectsViewModel.GitHubUrl(project))));
        }

        panel.Children.Add(FlowButtons.Build([.. buttons]));

        if (!project.OnDisk)
        {
            panel.Children.Add(NotOnDisk(vm, project));
        }
        else if (project.IsWsl)
        {
            (string verdict, _) = ProjectsViewModel.Verdict(project);
            panel.Children.Add(Ui.Card(Ui.V(Tokens.Space1, Ui.Text("Inside WSL", "DsStrong"), Ui.Text(verdict)), "DsCodeBox"));
            panel.Children.Add(Ui.Caption("Only Show in File Explorer and Open on GitHub are offered. To change or remove it, work inside the Linux distribution."));
        }
        else
        {
            Safety(panel, vm, project, subs, window);
        }

        return panel;
    }

    private static UIElement NotOnDisk(ProjectsViewModel vm, Project project)
    {
        var box = new StackPanel { Spacing = Tokens.Space2 };
        box.Children.Add(Ui.Secondary("This project isn't on your PC. Download it when you need it, and remove it again when you're done."));
        if (project.RemoteKB > 0)
        {
            string sizes = string.Join(" · ", CloneStrategies.All.Select(s => $"{s.Title()} about {SizeText.Format(s.EstimateBytes(project.RemoteKB) ?? 0)}"));
            box.Children.Add(Ui.Caption("Roughly: " + sizes));
        }

        return box;
    }

    private static void Safety(StackPanel panel, ProjectsViewModel vm, Project project, Subscriptions subs, MainWindow window)
    {
        RemovalSafety s = project.Safety ?? new RemovalSafety { State = SafetyState.NotChecked, NotCheckedReason = "Not checked yet." };
        panel.Children.Add(Ds.SectionTitle("Is it safe to remove from this PC?"));
        var checks = new StackPanel { Spacing = Tokens.Space2 };
        foreach ((bool ok, string text) in ProjectsViewModel.SafetyLines(project))
        {
            checks.Children.Add(Ds.CheckRow(ok, text));
        }

        panel.Children.Add(checks);

        if (s.NotCheckedReason is null && s.HasRemote)
        {
            string host = project.OnGitHub ? "GitHub" : (project.OtherHost ?? "the remote");
            Button fetch = Ui.Button("Fetch now", () => _ = vm.FetchNowAsync(project), $"Compare {project.Name} with {host} now");
            Ui.Tip(fetch, "Runs git fetch as this project's account, then checks again. It changes nothing in your files.");
            var progress = new ProgressRing { IsActive = true, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
            TextBlock note = Ui.Caption($"Compared with {host} " + ProjectsViewModel.FetchNote(s) + ".");
            panel.Children.Add(Ui.V(Tokens.Space2, note, Ui.H(Tokens.Space2, fetch, progress)));
            subs.On(vm, nameof(ProjectsViewModel.IsFetching), () =>
            {
                fetch.IsEnabled = !vm.IsFetching;
                Ui.Show(progress, vm.IsFetching);
            });
        }

        (string verdict, ViewModels.Text.Tone tone) = ProjectsViewModel.Verdict(project);
        Border callout = Ui.Card(Ui.Text(verdict), tone == ViewModels.Text.Tone.Good ? "DsSuccessBox" : "DsWarningBox");
        AutomationProperties.SetName(callout, verdict);
        panel.Children.Add(callout);

        var actions = new List<UIElement?>();
        if (!s.HasRemote && s.State == SafetyState.NotSafe && vm.Setup == GitHubSetup.Ready)
        {
            actions.Add(Ui.Button("Publish to GitHub…", () => vm.OpenSheet(ProjectSheet.Publish, project), $"Publish {project.Name} to GitHub", accent: true));
        }
        else if (s.HasRemote && s.UnpushedCommits > 0)
        {
            actions.Add(Ui.Button($"Push {Words.Count(s.UnpushedCommits, "commit")}", () => vm.Push(project), $"Push {Words.Count(s.UnpushedCommits, "commit")} in a terminal window", accent: true));
        }

        Button remove = Ui.Button("Remove from this PC…", () => _ = RemoveAsync(vm, project, window), $"Remove {project.Name} from this PC");
        remove.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        remove.IsEnabled = !vm.Removing.Contains(project.Id);
        actions.Add(remove);
        panel.Children.Add(FlowButtons.Build([.. actions]));
        panel.Children.Add(Ui.Caption("Removing moves the folder to the Recycle Bin (or DevSweep's own quarantine when the Recycle Bin can't take it) and records it in History. Only a project that is fully backed up can be removed; anything that isn't on the remote has to be pushed or published first. A removed project stays in this list with a Download button."));
    }

    /// <summary>Confirm, then remove; a project that isn't fully backed up is explained instead (the warning lists exactly what would be lost).</summary>
    private static async Task RemoveAsync(ProjectsViewModel vm, Project project, MainWindow window)
    {
        RemovalSafety? s = project.Safety;
        if (s?.IsSafeToRemove == true)
        {
            bool yes = await window.ConfirmAsync(
                $"Remove {project.Name} from this PC?",
                $"{project.Name} is fully backed up, so nothing is lost. DevSweep moves the folder to the Recycle Bin (or its own quarantine), records it in History so you can restore it, and keeps {project.Name} in this list with a Download button.",
                "Remove from this PC");
            if (yes)
            {
                await vm.RemoveAsync(project);
            }

            return;
        }

        List<string> lost = s is null ? ["It hasn't been checked yet"]
            : s.State == SafetyState.NotSafe ? [.. s.Blockers]
            : s.State == SafetyState.NotChecked ? [s.Headline]
            : [.. s.Blockers, .. s.Problems.Select(p => "Couldn't check: " + p)];
        var content = new StackPanel { Spacing = Tokens.Space2, MaxWidth = 460 };
        content.Children.Add(Ui.Text(project.Name + " isn't fully saved anywhere else, so DevSweep won't remove it. Removing it now would lose:"));
        foreach (string line in lost.DefaultIfEmpty("Something DevSweep couldn't check"))
        {
            content.Children.Add(Ds.CheckRow(false, line));
        }

        content.Children.Add(Ui.Secondary("Push or publish your work first (the panel has the button), or check the folder yourself. Then it can be removed."));
        await window.InformAsync($"{project.Name} can't be removed yet", content);
    }
}
