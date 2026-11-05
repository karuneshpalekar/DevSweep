using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;

namespace DevSweep.Cli.Commands.Projects;

/// <summary>
/// <c>devsweep projects</c> (module:projects): the list, and <c>check</c>, <c>cleanup</c>,
/// <c>accounts</c>, <c>activity</c>, <c>remove</c>, <c>download</c>, <c>add</c>,
/// <c>identity</c>, <c>publish</c>, <c>push</c> and <c>sign-in</c>.
/// </summary>
internal static class ProjectsCommand
{
    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Subcommands.Add(Create());
    }

    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print the list as JSON." };
        var roots = new Option<string[]>("--root") { Description = "Search these folders instead of the default project folders. Repeatable." };
        var offline = new Option<bool>("--offline") { Description = "Don't ask GitHub (local clones only)." };
        var command = new Command("projects", "Every project: clones on this PC found by their origin, repositories on GitHub for each gh account, and removed ones.");
        command.Options.Add(json);
        command.Options.Add(roots);
        command.Options.Add(offline);
        command.SetAction((parse, ct) => ListAsync(parse.GetValue(json), parse.GetValue(roots), parse.GetValue(offline), ct));
        command.Subcommands.Add(Check());
        command.Subcommands.Add(Cleanup());
        command.Subcommands.Add(Accounts());
        command.Subcommands.Add(Activity());
        command.Subcommands.Add(Remove());
        command.Subcommands.Add(Download());
        command.Subcommands.Add(Add());
        command.Subcommands.Add(Identity());
        command.Subcommands.Add(Publish());
        command.Subcommands.Add(Push());
        command.Subcommands.Add(SignIn());
        return command;
    }

    private static async Task<int> ListAsync(bool asJson, string[]? roots, bool offline, CancellationToken ct)
    {
        if (ProjectsCli.Open() is not { } service)
        {
            return ProjectsCli.TurnedOff;
        }

        ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = roots is { Length: > 0 } ? roots : null, IncludeGitHub = !offline }, ct).ConfigureAwait(false);
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, ProjectsCliJsonContext.Default.ProjectsSnapshot));
            return 0;
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{snapshot.Projects.Count} projects ({snapshot.Projects.Count(p => p.OnDisk)} on this PC) in {snapshot.Elapsed.TotalSeconds:0.0} s; searched {string.Join(", ", snapshot.Search.Searched)}"));
        foreach (Project p in snapshot.Projects)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {p.NameWithOwner,-40} {p.StatusText,-36} {(p.OnDisk ? ProjectsCli.Size(p.LocalSize) : string.Empty),10}  {p.LocalPath ?? "(" + (p.Account ?? p.Owner) + ")"}"));
        }

        foreach (string message in snapshot.Messages)
        {
            Console.Out.WriteLine("note: " + message);
        }

        return 0;
    }

    private static Command Check()
    {
        var path = new Argument<string>("path") { Description = "A project folder." };
        var fetch = new Option<bool>("--fetch") { Description = "Run git fetch first (uses the network and the project's account), so 'pushed' is measured against GitHub now." };
        var account = new Option<string?>("--account") { Description = "The gh account to fetch as." };
        var json = new Option<bool>("--json") { Description = "Print the check as JSON." };
        var command = new Command("check", "Check whether a project folder could leave this PC without losing anything. Exit code 0 safe, 1 not safe or unknown.");
        command.Arguments.Add(path);
        command.Options.Add(fetch);
        command.Options.Add(account);
        command.Options.Add(json);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open() is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            RemovalSafety s = await service.CheckAsync(Path.GetFullPath(parse.GetValue(path)!), parse.GetValue(fetch), parse.GetValue(account), ct).ConfigureAwait(false);
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(s, ProjectsCliJsonContext.Default.RemovalSafety));
            }
            else
            {
                ProjectsCli.PrintSafety(s);
            }

            return s.IsSafeToRemove ? 0 : ProjectsCli.NotSafe;
        });
        return command;
    }

    private static Command Cleanup()
    {
        var days = new Option<int?>("--days") { Description = "Not opened or changed for this many days (7 to 90; default the saved choice, 21)." };
        var roots = new Option<string[]>("--root") { Description = "Search these folders instead of the default project folders. Repeatable." };
        var fast = new Option<bool>("--fast") { Description = "Skip the exact re-measure (overview sizes)." };
        var json = new Option<bool>("--json") { Description = "Print the list as JSON." };
        var command = new Command("cleanup", "Projects untouched for a while, biggest first, with what removing them frees and whether each is safe.");
        command.Options.Add(days);
        command.Options.Add(roots);
        command.Options.Add(fast);
        command.Options.Add(json);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Cleanup, ProjectsFeatures.CleanupFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            string[]? r = parse.GetValue(roots);
            ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = r is { Length: > 0 } ? r : null, IncludeGitHub = false }, ct).ConfigureAwait(false);
            IdleCleanupResult idle = await service.IdleAsync(snapshot, parse.GetValue(days), !parse.GetValue(fast), ct).ConfigureAwait(false);
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(idle, ProjectsCliJsonContext.Default.IdleCleanupResult));
                return 0;
            }

            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Not opened or changed for {idle.Days} days: {idle.Projects.Count} projects, {ProjectsCli.Size(idle.TotalBytes)}; {idle.Safe.Count} safe to remove ({ProjectsCli.Size(idle.SafeBytes)})"));
            foreach (IdleProject p in idle.Projects)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {ProjectsCli.Size(p.FreeableBytes),10}{(p.Exact ? string.Empty : "~")}  {(p.IsSafe ? "safe" : "kept"),-5} {p.Project.NameWithOwner}  last used {ProjectsCli.When(p.Project.LastUsed)}  {p.Path}"));
                foreach (string reason in p.Reasons)
                {
                    Console.Out.WriteLine("        " + reason);
                }
            }

            return 0;
        });
        return command;
    }

    private static Command Accounts()
    {
        var json = new Option<bool>("--json") { Description = "Print the accounts as JSON." };
        var command = new Command("accounts", "The GitHub accounts gh is signed in to, which is active, and each one's commit identity. Never changes the active account.");
        command.Options.Add(json);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Accounts, ProjectsFeatures.AccountsFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            GitHubAccounts accounts = await service.GitHubCli.AccountsAsync(ct).ConfigureAwait(false);
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(accounts, ProjectsCliJsonContext.Default.GitHubAccounts));
                return 0;
            }

            Console.Out.WriteLine($"GitHub CLI: {service.GitHubCli.Program ?? "not installed"}; {accounts.Status}");
            foreach (GitHubAccount a in accounts.Accounts)
            {
                GitIdentity? identity = service.Store.Identity(a.Login);
                Console.Out.WriteLine($"  {a.Login}{(a.IsActive ? " (active in the terminal)" : string.Empty)}  identity: {(identity is { IsBlank: false } ? identity.ToString() : "not set (git's global identity is used)")}  [{a.Source}]");
            }

            if (accounts.Message is { } m)
            {
                Console.Out.WriteLine("note: " + m);
            }

            return 0;
        });
        return command;
    }

    private static Command Activity()
    {
        var json = new Option<bool>("--json") { Description = "Print the trail as JSON." };
        var command = new Command("activity", "Downloads, removals, publishes, pushes, added repositories, identity changes and sign-ins, newest first.");
        command.Options.Add(json);
        command.SetAction(parse =>
        {
            if (ProjectsCli.Open(f => f.Activity, ProjectsFeatures.ActivityFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            IReadOnlyList<ActivityEntry> trail = service.Activity();
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(trail.ToList(), ProjectsCliJsonContext.Default.ListActivityEntry));
                return 0;
            }

            if (trail.Count == 0)
            {
                Console.Out.WriteLine("No activity yet.");
            }

            foreach (ActivityEntry e in trail)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{e.Date.ToLocalTime():g}  {e.Kind,-9} {e.Subject}  {e.Detail}"));
            }

            return 0;
        });
        return command;
    }

    private static Command Remove()
    {
        var path = new Argument<string>("path") { Description = "The project folder." };
        var yes = new Option<bool>("--yes") { Description = "Really remove it (to the Recycle Bin, or DevSweep's quarantine; restorable from History). Without it, only checks." };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var command = new Command("remove", "Remove a fully backed-up project from this PC. Refused unless every check passes.");
        command.Arguments.Add(path);
        command.Options.Add(yes);
        command.Options.Add(json);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Cleanup, ProjectsFeatures.CleanupFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            string folder = Path.GetFullPath(parse.GetValue(path)!);
            Project? project = await FindAsync(service, folder, ct).ConfigureAwait(false);
            if (project is null)
            {
                Console.Error.WriteLine($"devsweep: {folder} is not a git project DevSweep can see.");
                return ProjectsCli.Failed;
            }

            if (!parse.GetValue(yes))
            {
                ProjectsCli.PrintSafety(project.Safety!);
                Console.Out.WriteLine(project.Safety!.IsSafeToRemove ? "Safe to remove. Run again with --yes to remove it." : "Not removed.");
                return project.Safety.IsSafeToRemove ? 0 : ProjectsCli.NotSafe;
            }

            RemoveResult result = await service.RemoveAsync(project, cancellationToken: ct).ConfigureAwait(false);
            if (parse.GetValue(json))
            {
                var output = new RemoveJson(result.Succeeded, result.Message, result.Safety, result.Outcome?.HistoryId, result.Outcome?.Freed ?? 0,
                    result.Outcome?.Items.Select(i => $"{i.Destination}: {i.Location ?? i.Original}").ToList() ?? []);
                Console.Out.WriteLine(JsonSerializer.Serialize(output, ProjectsCliJsonContext.Default.RemoveJson));
            }
            else
            {
                Console.Out.WriteLine(result.Message);
            }

            return result.Succeeded ? 0 : ProjectsCli.NotSafe;
        });
        return command;
    }

    private static Command Download()
    {
        var slug = new Argument<string>("repository") { Description = "owner/name or a github.com URL." };
        var strategy = new Option<string?>("--strategy") { Description = "blobless (default), shallow or full." };
        var to = new Option<string?>("--to") { Description = "The folder to create (default: where it lived before, else <workspace>\\<account>\\<name>)." };
        var account = new Option<string?>("--account") { Description = "The gh account to download as (default: the one that can read it, else the active one)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the git command and the size estimate only." };
        var command = new Command("download", "Download a repository with a strategy, as one account, writing that account's commit identity into the clone.");
        command.Arguments.Add(slug);
        command.Options.Add(strategy);
        command.Options.Add(to);
        command.Options.Add(account);
        command.Options.Add(dryRun);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Accounts, ProjectsFeatures.AccountsFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            if (RemoteUrls.ParseSlug(parse.GetValue(slug)!) is not { } name)
            {
                Console.Error.WriteLine("devsweep: use owner/name or a github.com URL.");
                return ProjectsCli.Failed;
            }

            CloneStrategy chosen = CloneStrategies.Parse(parse.GetValue(strategy)) ?? CloneStrategy.Blobless;
            ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions(), ct).ConfigureAwait(false);
            Project project = snapshot.Projects.FirstOrDefault(p => p.NameWithOwner.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? new Project { Id = name, Name = name.Split('/')[1], NameWithOwner = name, Owner = name.Split('/')[0], OnGitHub = true };
            string? who = parse.GetValue(account) ?? ProjectsService.CredentialAccount(project, snapshot.Accounts);
            string? destination = parse.GetValue(to) is { } t ? Path.GetFullPath(t) : null;
            DownloadPlan plan = service.PlanDownload(project, chosen, destination, who);
            Console.Out.WriteLine($"git {string.Join(' ', plan.Arguments)}   (as {who ?? "nobody signed in"}; estimate {(plan.EstimateBytes is { } e ? "~" + ProjectsCli.Size(e) : "unknown")})");
            if (parse.GetValue(dryRun) || plan.Refusal is not null)
            {
                if (plan.Refusal is { } refusal)
                {
                    Console.Error.WriteLine("devsweep: " + refusal);
                    return ProjectsCli.Failed;
                }

                return 0;
            }

            DownloadResult result = await service.DownloadAsync(project, chosen, destination, who, cancellationToken: ct).ConfigureAwait(false);
            Console.Out.WriteLine(result.Message);
            return result.Succeeded ? 0 : ProjectsCli.Failed;
        });
        return command;
    }

    private static Command Add()
    {
        var url = new Argument<string>("url") { Description = "owner/name or a github.com URL (forks, other organisations)." };
        var command = new Command("add", "Add a repository outside your own lists, so it is listed with Download.");
        command.Arguments.Add(url);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Accounts, ProjectsFeatures.AccountsFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            (RemoteRepo? repo, string? error) = await service.AddByUrlAsync(parse.GetValue(url)!, ct).ConfigureAwait(false);
            Console.Out.WriteLine(repo is null ? error : $"Added {repo.NameWithOwner} (readable by {repo.Account}).");
            return repo is null ? ProjectsCli.Failed : 0;
        });
        return command;
    }

    private static Command Identity()
    {
        var account = new Argument<string>("account") { Description = "The GitHub account." };
        var name = new Option<string?>("--name") { Description = "Name for commits." };
        var email = new Option<string?>("--email") { Description = "Email for commits." };
        var command = new Command("identity", "Show or set the commit identity written into projects downloaded with an account (never the global git config).");
        command.Arguments.Add(account);
        command.Options.Add(name);
        command.Options.Add(email);
        command.SetAction(parse =>
        {
            if (ProjectsCli.Open() is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            string login = parse.GetValue(account)!;
            if (parse.GetValue(name) is null && parse.GetValue(email) is null)
            {
                Console.Out.WriteLine(service.Store.Identity(login) is { IsBlank: false } i ? i.ToString() : "Not set: projects downloaded with this account use git's global identity.");
                return 0;
            }

            service.SaveIdentity(login, new GitIdentity(parse.GetValue(name) ?? string.Empty, parse.GetValue(email) ?? string.Empty));
            Console.Out.WriteLine($"Saved. Projects downloaded with {login} will commit as {service.Store.Identity(login)?.ToString() ?? "git's global identity"}.");
            return 0;
        });
        return command;
    }

    private static Command Publish()
    {
        var folder = new Argument<string>("folder") { Description = "The folder to publish." };
        var name = new Option<string?>("--name") { Description = "Repository name (default: the folder's name)." };
        var account = new Option<string>("--account") { Description = "The gh account to publish as.", Required = true };
        var owner = new Option<string?>("--owner") { Description = "An organisation of that account (default: the account)." };
        var isPublic = new Option<bool>("--public") { Description = "Public repository (default: private)." };
        var description = new Option<string?>("--description") { Description = "Description on GitHub." };
        var confirmSecrets = new Option<bool>("--publish-secrets") { Description = "Publish even though some files look like secrets (after you have seen the list)." };
        var check = new Option<bool>("--check") { Description = "Only list the files that would be published and the ones that look like secrets." };
        var command = new Command("publish", "Publish a folder as a new GitHub repository, after checking it for files that look like secrets.");
        command.Arguments.Add(folder);
        foreach (Option o in (Option[])[name, account, owner, isPublic, description, confirmSecrets, check])
        {
            command.Options.Add(o);
        }

        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Accounts, ProjectsFeatures.AccountsFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            string path = Path.GetFullPath(parse.GetValue(folder)!);
            var request = new PublishRequest
            {
                Folder = path,
                Account = parse.GetValue(account)!,
                Owner = parse.GetValue(owner),
                Name = parse.GetValue(name) ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                Description = parse.GetValue(description),
                IsPrivate = !parse.GetValue(isPublic),
                AcknowledgeSecrets = parse.GetValue(confirmSecrets),
            };
            if (parse.GetValue(check))
            {
                PublishPreview preview = await service.PreviewPublishAsync(request, ct).ConfigureAwait(false);
                Console.Out.WriteLine(JsonSerializer.Serialize(preview, ProjectsCliJsonContext.Default.PublishPreview));
                return preview.Refusal is null ? 0 : ProjectsCli.Failed;
            }

            PublishResult result = await service.PublishAsync(request, ct).ConfigureAwait(false);
            Console.Out.WriteLine(result.Message);
            return result.Succeeded ? 0 : ProjectsCli.Failed;
        });
        return command;
    }

    private static Command Push()
    {
        var path = new Argument<string>("path") { Description = "The project folder." };
        var command = new Command("push", "Push the project's current branch in a window you can see (Windows Terminal, else PowerShell), as its account.");
        command.Arguments.Add(path);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open() is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            string folder = Path.GetFullPath(parse.GetValue(path)!);
            ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = [Path.GetDirectoryName(folder) ?? folder], IncludeGitHub = false }, ct).ConfigureAwait(false);
            Project? project = snapshot.Projects.FirstOrDefault(p => p.LocalPath is { } l && SafetyChecker.SamePath(l, folder));
            if (project is null)
            {
                Console.Error.WriteLine($"devsweep: {folder} is not a git project.");
                return ProjectsCli.Failed;
            }

            GitHubAccounts? accounts = service.Features.Accounts ? await service.GitHubCli.AccountsAsync(ct).ConfigureAwait(false) : null;
            TerminalLaunch launch = service.Push(project, accounts);
            Console.Out.WriteLine($"Opened {(launch.WindowsTerminal ? "Windows Terminal" : "PowerShell")} with {launch.ScriptPath}");
            return 0;
        });
        return command;
    }

    private static Command SignIn()
    {
        var login = new Option<string?>("--user") { Description = "The account you are about to add (to save its identity first)." };
        var name = new Option<string?>("--name") { Description = "Name for commits with that account." };
        var email = new Option<string?>("--email") { Description = "Email for commits with that account." };
        var command = new Command("sign-in", "Open GitHub's sign-in (gh auth login) in a window you can see; the active account is switched back afterwards.");
        command.Options.Add(login);
        command.Options.Add(name);
        command.Options.Add(email);
        command.SetAction(async (parse, ct) =>
        {
            if (ProjectsCli.Open(f => f.Accounts, ProjectsFeatures.AccountsFlag) is not { } service)
            {
                return ProjectsCli.TurnedOff;
            }

            GitHubAccounts accounts = await service.GitHubCli.AccountsAsync(ct).ConfigureAwait(false);
            var identity = new GitIdentity(parse.GetValue(name) ?? string.Empty, parse.GetValue(email) ?? string.Empty);
            TerminalLaunch launch = service.SignIn(parse.GetValue(login), identity, accounts.Active?.Login);
            Console.Out.WriteLine($"Opened {(launch.WindowsTerminal ? "Windows Terminal" : "PowerShell")} with {launch.ScriptPath}");
            return 0;
        });
        return command;
    }

    /// <summary>The project at <paramref name="folder"/>, checked now (locks included).</summary>
    private static async Task<Project?> FindAsync(ProjectsService service, string folder, CancellationToken ct)
    {
        ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = [Path.GetDirectoryName(folder) ?? folder], IncludeGitHub = false }, ct).ConfigureAwait(false);
        Project? project = snapshot.Projects.FirstOrDefault(p => p.LocalPath is { } l && SafetyChecker.SamePath(l, folder));
        if (project is null)
        {
            return null;
        }

        RemovalSafety fresh = await service.CheckAsync(folder, cancellationToken: ct).ConfigureAwait(false);
        return project with { Safety = fresh };
    }
}
