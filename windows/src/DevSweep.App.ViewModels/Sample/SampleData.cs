using DevSweep.App.ViewModels.Places;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Review;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels.Sample;

/// <summary>
/// Made-up, deterministic data for screenshots and tests (the Mac app's screenshot
/// sample data, on Windows paths). Never used outside screenshot mode.
/// </summary>
public static class SampleData
{
    public const string Profile = @"C:\Users\alex";

    public const long GB = 1L << 30;

    public const long MB = 1L << 20;

    public static DateTimeOffset Now { get; } = new(2026, 11, 2, 10, 30, 0, TimeSpan.Zero);

    public static IReadOnlyList<Finding> Findings { get; } =
    [
        Make("npm-cache", "npm cache", Category.PackageCaches, Risk.Rebuilds, @"C:\Users\alex\AppData\Local\npm-cache", 3_412 * MB,
            "Every package npm has downloaded, kept so the next install can skip the download.",
            "3.3 GB of downloaded packages in C:\\Users\\alex\\AppData\\Local\\npm-cache.",
            "npm downloads packages again the next time a project installs them.",
            [Core.Cleaning.Check.Passed("npm cache: C:\\Users\\alex\\AppData\\Local\\npm-cache (the default; no npm_config_cache, no cache= in .npmrc)"), Core.Cleaning.Check.Passed("No npm or node process is running.")],
            [new CleanAction(CleanActionKind.Delete), new CleanAction(CleanActionKind.Manual, "Show npm's command", ["npm", "cache", "clean", "--force"], "Run npm cache clean --force in a terminal.")],
            wontLose: "Your projects, their node_modules and package-lock.json.", better: "Nothing to do regularly: npm trims nothing on its own, so clearing it twice a year is plenty."),
        Make("gradle-caches", "Gradle caches", Category.PackageCaches, Risk.Rebuilds, @"C:\Users\alex\.gradle\caches", 5_870 * MB,
            "Downloaded dependencies and build outputs Gradle keeps for every project.", "5.7 GB in C:\\Users\\alex\\.gradle\\caches.",
            "The next build downloads what it needs again; the first build is slower.",
            [Core.Cleaning.Check.Passed("Gradle home: C:\\Users\\alex\\.gradle (no GRADLE_USER_HOME)"), Core.Cleaning.Check.Warning("A Gradle daemon (java.exe) may be running; Review checks which files are in use.")],
            [new CleanAction(CleanActionKind.Delete)], measurement: SizeMeasurement.Overview),
        Make("pnpm-store", "pnpm store", Category.PackageCaches, Risk.Rebuilds, @"C:\Users\alex\AppData\Local\pnpm\store", 2_204 * MB,
            "Packages pnpm links into every project's node_modules.", "2.2 GB in the pnpm store; most of it is hard-linked into your projects.",
            "Projects keep working (their hard links keep the files); pnpm downloads again for new installs.",
            [Core.Cleaning.Check.Passed("Hard links counted once."), Core.Cleaning.Check.Warning("1.9 GB is shared with node_modules folders, so removing the store frees only 310 MB.")],
            [new CleanAction(CleanActionKind.Trash)], confidence: SizeConfidence.SharedLinks, freeable: 310 * MB),
        Make("chrome-cache", "Chrome cache · Default profile", Category.Browsers, Risk.Rebuilds, @"C:\Users\alex\AppData\Local\Google\Chrome\User Data\Default\Cache", 870 * MB,
            "Pages and images Chrome saved to load sites faster.", "870 MB in the Default profile's cache.",
            "Sites load a little slower the first time. You stay signed in.",
            [Core.Cleaning.Check.Passed("Only the Cache and Code Cache folders; never cookies, history or passwords.")],
            [new CleanAction(CleanActionKind.Delete)], blockers: ["chrome"]),
        Make("jetbrains-old:IntelliJIdea2024.1", "IntelliJ IDEA 2024.1 settings and caches", Category.IdeVersions, Risk.OldVersion, @"C:\Users\alex\AppData\Local\JetBrains\IntelliJIdea2024.1", 1_310 * MB,
            "Settings, caches and logs of IntelliJ IDEA 2024.1.", "IntelliJ IDEA 2025.2 is installed and has its own folders.",
            "Nothing you use: 2025.2 imported these settings when it first started.",
            [Core.Cleaning.Check.Passed("Newer version found: IntelliJIdea2025.2"), Core.Cleaning.Check.Passed("Not in use by any running program.")],
            [new CleanAction(CleanActionKind.Trash)], undo: "Restore it from History while it's in the Recycle Bin."),
        Make("android-images:android-30", "Android 11 system image (x86_64)", Category.Android, Risk.OldVersion, @"C:\Users\alex\AppData\Local\Android\Sdk\system-images\android-30", 6_120 * MB,
            "An emulator image for Android 11.", "No emulator (AVD) uses it.", "Android Studio can download it again from the SDK Manager.",
            [Core.Cleaning.Check.Passed("Checked 2 AVDs in C:\\Users\\alex\\.android\\avd: none uses android-30.")],
            [new CleanAction(CleanActionKind.Trash)]),
        Make("leftover:postman", "Postman", Category.Leftovers, Risk.Leftover, @"C:\Users\alex\AppData\Roaming\Postman", 412 * MB,
            "Data Postman kept in AppData.", "Postman isn't installed any more.", "Its local collections and settings are gone (synced ones stay in your account).",
            [Core.Cleaning.Check.Passed("Not in the Uninstall registry keys (4 views, 428 apps listed)"), Core.Cleaning.Check.Passed("Not a Store package (48 listed)"), Core.Cleaning.Check.Passed("No Start menu shortcut, App Paths entry or running process")],
            [new CleanAction(CleanActionKind.Trash)], confidence: SizeConfidence.AccessDenied),
        Make("stale:old-web", "node_modules in old-web", Category.Projects, Risk.Rebuilds, @"D:\src\old-web\node_modules", 1_034 * MB,
            "Packages installed for old-web.", "Nobody has committed to or changed old-web for 4 months.", "npm install brings them back when you work on it again.",
            [Core.Cleaning.Check.Passed("Last commit 3 July 2026; newest file 3 July 2026."), Core.Cleaning.Check.Passed("package.json next to it.")],
            [new CleanAction(CleanActionKind.Delete)]),
        Make("wsl-vhdx:Ubuntu", "WSL disk for Ubuntu", Category.Wsl, Risk.HoldsData, @"C:\Users\alex\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu\LocalState\ext4.vhdx", 38 * GB,
            "The virtual disk holding your Ubuntu files.", "It's 38 GB on disk but Linux uses about 21 GB: the disk grows and never shrinks.",
            "Compacting keeps every file and returns the unused space.",
            [Core.Cleaning.Check.Passed("wsl --shutdown is needed first; nothing runs until you do it.")],
            [new CleanAction(CleanActionKind.Manual, "Show the compact commands", ["powershell", "-NoProfile", "-Command", "wsl --shutdown; Optimize-VHD -Path \"$env:LOCALAPPDATA\\Packages\\CanonicalGroupLimited.Ubuntu\\LocalState\\ext4.vhdx\" -Mode Full"], "Run in an administrator PowerShell; Windows asks first (UAC).")],
            measurement: SizeMeasurement.Reported),
        Make("windows-update-cleanup", "Windows Update cleanup", Category.Windows, Risk.NeedsAdmin, @"C:\Windows\WinSxS", 0,
            "Old component versions Windows keeps after updates.", "Windows can remove superseded components itself.", "You can't uninstall the updates that are already installed.",
            [Core.Cleaning.Check.Passed("Outside your user folder: DevSweep never changes it.")],
            [new CleanAction(CleanActionKind.Manual, "Show the command", ["dism", "/Online", "/Cleanup-Image", "/StartComponentCleanup"], "Run in an administrator terminal; Windows asks first (UAC).")],
            measurement: SizeMeasurement.Reported),
    ];

    public static ScanReport Report { get; } = new()
    {
        Date = Now,
        TotalSeconds = 14.2,
        MeasuringSeconds = 6.1,
        CheckTimeoutSeconds = 90,
        MeasureTimeoutSeconds = 60,
        Mode = SizeMode.Overview,
        TotalBytes = Findings.Sum(f => f.Size) - (1_900 * MB),
        SharedBetweenFindingsBytes = 1_900 * MB,
        Unmeasured = 1,
        AccessDenied = [@"C:\Users\alex\AppData\Roaming\Postman\Partitions\locked"],
        Checks =
        [
            CheckRow("npm-cache", "npm cache", "packageCaches", "paths", 0.02, 1.4, 1),
            CheckRow("gradle-caches", "Gradle caches", "packageCaches", "paths", 0.01, 3.2, 1),
            CheckRow("pnpm-store", "pnpm store", "packageCaches", "paths", 0.01, 0.9, 1),
            CheckRow("pip-cache", "pip cache", "packageCaches", "paths", 0.01, 0, 0),
            CheckRow("chrome-cache", "Browser caches", "browsers", "browserCaches", 0.30, 0.4, 1),
            CheckRow("jetbrains-old", "Old JetBrains versions", "ideVersions", "versionedSiblings", 0.05, 0.6, 1),
            CheckRow("android-images", "Unused Android system images", "android", "androidSystemImages", 0.12, 0.5, 1),
            CheckRow("leftover", "Leftovers of removed apps", "leftovers", "orphanedAppData", 1.80, 0.3, 1) with { PartialSizes = 1, AccessDenied = [@"C:\Users\alex\AppData\Roaming\Postman\Partitions\locked"] },
            CheckRow("stale", "Idle project dependencies", "projects", "staleProjectArtifacts", 2.40, 0.8, 1),
            CheckRow("wsl-vhdx", "WSL and Docker disks", "wsl", "vhdxDisks", 0.20, 0, 1),
            CheckRow("docker", "Docker build cache", "docker", "docker", 90, 0, 0) with { Outcome = CheckOutcome.TimedOut },
            CheckRow("windows-update-cleanup", "Windows Update cleanup", "windows", "paths", 0.01, 0, 1),
        ],
    };

    public static ScanChanges Changes { get; } = new(Now.AddDays(-7),
        [new ScanChange("gradle-caches", "Gradle caches", 1_240 * MB), new ScanChange("npm-cache", "npm cache", 610 * MB)],
        [new ScanChange("android-images:android-30", "Android 11 system image (x86_64)", 6_120 * MB)]);

    public static IReadOnlyList<HistoryEntry> History { get; } =
    [
        new HistoryEntry
        {
            Id = new Guid("8f2e0c1a-0000-4000-8000-000000000001"),
            Date = Now.AddHours(-2),
            Title = "Yarn cache",
            ActionKind = CleanActionKind.Delete,
            ActionLabel = "Delete",
            Size = 1_820 * MB,
            Items = [new MovedItem { Original = @"C:\Users\alex\AppData\Local\Yarn\Cache", Destination = ItemDestination.Deleted, Bytes = 1_820 * MB }],
            Reconciliation = new FreedSpace { Volume = @"C:\", ShownBytes = 1_820 * MB, ExpectedFreedBytes = 1_820 * MB, FreeBefore = 80 * GB, FreeAfter = (80 * GB) + (1_819 * MB) },
        },
        new HistoryEntry
        {
            Id = new Guid("8f2e0c1a-0000-4000-8000-000000000002"),
            Date = Now.AddHours(-2).AddMinutes(1),
            Title = "PyCharm 2023.3 settings and caches",
            ActionKind = CleanActionKind.Trash,
            ActionLabel = "Move to Recycle Bin",
            Size = 940 * MB,
            Items = [new MovedItem { Original = @"C:\Users\alex\AppData\Local\JetBrains\PyCharm2023.3", Destination = ItemDestination.RecycleBin, Location = @"C:\$Recycle.Bin\S-1-5-21\$RX29OH", Bytes = 940 * MB }],
        },
        new HistoryEntry
        {
            Id = new Guid("8f2e0c1a-0000-4000-8000-000000000003"),
            Date = Now.AddDays(-1),
            Title = "Old installers in Downloads",
            ActionKind = CleanActionKind.Trash,
            ActionLabel = "Move to Recycle Bin",
            Size = 14 * GB,
            Items = [new MovedItem { Original = @"C:\Users\alex\Downloads\Win11_25H2.iso", Destination = ItemDestination.Quarantine, Location = @"C:\Users\alex\AppData\Local\DevSweep\Quarantine\q1\Win11_25H2.iso", QuarantineId = "q1", Bytes = 14 * GB, Note = "Bigger than the Recycle Bin's limit (9.5 GB), so Windows would delete it for good." }],
        },
        new HistoryEntry
        {
            Id = new Guid("8f2e0c1a-0000-4000-8000-000000000004"),
            Date = Now.AddDays(-3),
            Title = "Docker build cache",
            ActionKind = CleanActionKind.Command,
            ActionLabel = "Run docker",
            Size = 3_100 * MB,
            Command = ["docker", "builder", "prune", "--force"],
        },
        new HistoryEntry
        {
            Id = new Guid("8f2e0c1a-0000-4000-8000-000000000005"),
            Date = Now.AddDays(-3).AddMinutes(4),
            Title = "Slack",
            ActionKind = CleanActionKind.Trash,
            ActionLabel = "Move to Recycle Bin",
            Size = 260 * MB,
            Items = [new MovedItem { Original = @"C:\Users\alex\AppData\Roaming\Slack", Destination = ItemDestination.RecycleBin, Bytes = 260 * MB }],
            RestoredAt = Now.AddDays(-2),
        },
    ];

    /// <summary>History ids whose items are still restorable in the sample.</summary>
    public static IReadOnlySet<Guid> Restorable { get; } = new HashSet<Guid> { History[1].Id, History[2].Id };

    public static IReadOnlyList<QuarantineEntry> Quarantine { get; } =
    [
        new QuarantineEntry
        {
            Id = "q1",
            OriginalPath = @"C:\Users\alex\Downloads\Win11_25H2.iso",
            StoredPath = @"C:\Users\alex\AppData\Local\DevSweep\Quarantine\q1\Win11_25H2.iso",
            Bytes = 14 * GB,
            LogicalBytes = 14 * GB,
            QuarantinedAt = Now.AddDays(-1),
            Reason = "Bigger than the Recycle Bin's limit.",
        },
    ];

    public static IReadOnlyList<Services.VolumeSpace> Volumes { get; } =
    [
        new(@"C:\", "Windows", 81 * GB, 476 * GB, true),
        new(@"D:\", "Dev Drive", 212 * GB, 500 * GB, false),
    ];

    public static HealthReport Health { get; } = new(
        new VersionsResult
        {
            Date = Now,
            Runtimes =
            [
                new RuntimeReport
                {
                    Id = "node",
                    Name = "Node.js",
                    Recommended = "22.21.1",
                    Installs =
                    [
                        Install("22.21.0", "22", @"C:\Users\alex\AppData\Local\nvm\v22.21.0", InstallSource.Nvm, true, SupportStatus.Supported, "22.21.1", lts: true),
                        Install("18.20.4", "18", @"C:\Users\alex\AppData\Local\nvm\v18.20.4", InstallSource.Nvm, false, SupportStatus.EndOfLife, "18.20.8"),
                    ],
                    Issues = [new RuntimeIssue(IssueLevel.Warning, "18.20.4 reached end of life on 30 April 2025; old-web still asks for 18.")],
                    Steps =
                    [
                        new RuntimeStep { Id = "node:remove:1", Kind = StepKind.Remove, Title = "Remove Node.js 18.20.4", Detail = "nvm-windows installed it; nvm removes it.", Commands = ["nvm uninstall 18.20.4"] },
                        new RuntimeStep { Id = "node:upgrade:1", Kind = StepKind.Upgrade, Title = "Update 22.21.0 to 22.21.1", Detail = "A newer patch of the same line.", Commands = ["nvm install 22.21.1", "nvm use 22.21.1"] },
                    ],
                    Resolution = new CommandResolution { Command = "node", Runs = @"C:\nvm4w\nodejs\node.exe", Via = "nvm-windows link" },
                },
                new RuntimeReport
                {
                    Id = "python",
                    Name = "Python",
                    Installs =
                    [
                        Install("3.12.7", "3.12", @"C:\Users\alex\AppData\Local\Programs\Python\Python312", InstallSource.PythonOrg, true, SupportStatus.Supported, "3.12.7"),
                        Install("3.8.10", "3.8", @"C:\Users\alex\AppData\Local\Programs\Python\Python38", InstallSource.PythonOrg, false, SupportStatus.EndOfLife, "3.8.20"),
                    ],
                    Issues = [new RuntimeIssue(IssueLevel.Info, "The Store's python.exe alias is not an install; a new terminal runs Python 3.12.7.")],
                    NotInstalls = [@"C:\Users\alex\AppData\Local\Microsoft\WindowsApps\python.exe (opens the Microsoft Store)"],
                    Steps = [new RuntimeStep { Id = "python:remove:1", Kind = StepKind.Remove, Title = "Remove Python 3.8.10", Detail = "Installed from python.org for this user; removed with its own uninstaller.", Commands = ["winget uninstall --id Python.Python.3.8"] }],
                },
                new RuntimeReport
                {
                    Id = "dotnet",
                    Name = ".NET SDK",
                    Installs =
                    [
                        Install("10.0.100", "10.0", @"C:\Program Files\dotnet\sdk\10.0.100", InstallSource.DotnetInstaller, true, SupportStatus.Supported, "10.0.100", lts: true),
                        Install("9.0.306", "9.0", @"C:\Program Files\dotnet\sdk\9.0.306", InstallSource.DotnetInstaller, false, SupportStatus.EndingSoon, "9.0.306"),
                    ],
                    Issues = [new RuntimeIssue(IssueLevel.Info, ".NET 9 support ends on 10 November 2026.")],
                },
                new RuntimeReport
                {
                    Id = "postgresql",
                    Name = "PostgreSQL",
                    Installs = [Install("16.4", "16", @"C:\Program Files\PostgreSQL\16", InstallSource.PostgresInstaller, true, SupportStatus.Supported, "16.10")],
                    Issues = [new RuntimeIssue(IssueLevel.Info, "16.10 is available in the same line.")],
                },
            ],
            Projects =
            [
                new ProjectRequirement { Project = "web-shop", Path = @"D:\src\web-shop", Tool = "node", ToolName = "Node.js", Spec = "22", File = ".nvmrc", Message = "22.21.0 is installed." },
                new ProjectRequirement { Project = "old-web", Path = @"D:\src\old-web", Tool = "node", ToolName = "Node.js", Spec = ">=18 <19", File = "package.json", Status = RequirementStatus.EndOfLife, Message = "Asks for Node.js 18, which is past end of life." },
                new ProjectRequirement { Project = "ml-notebooks", Path = @"D:\src\ml-notebooks", Tool = "python", ToolName = "Python", Spec = "3.13", File = ".python-version", Status = RequirementStatus.NotInstalled, Message = "Python 3.13 isn't installed.", Fix = ["winget install --id Python.Python.3.13"] },
            ],
        },
        new SecurityResult
        {
            Date = Now,
            Looked = [@"C:\Users\alex\Downloads", @"C:\Users\alex\Desktop", @"C:\Users\alex\Documents", @"D:\src"],
            Findings =
            [
                Secret(SecurityKind.CloudAccessKeys, SecurityLevel.Critical, "AWS access keys", @"C:\Users\alex\Downloads\credentials (1).csv", "Plain text", "Anyone with this file can use your AWS account.", "Store the key in a password manager or the AWS CLI's credentials file, then delete this file."),
                Secret(SecurityKind.EnvFile, SecurityLevel.Warning, ".env file", @"D:\src\web-shop\.env", "Not ignored", "It isn't in .gitignore, so one git add . away from being committed.", "Add .env to .gitignore."),
                Secret(SecurityKind.RecoveryCodes, SecurityLevel.Warning, "GitHub recovery codes", @"C:\Users\alex\Documents\github-recovery-codes.txt", "Plain text", "Recovery codes let anyone past your two-factor sign-in.", "Keep them in a password manager, then delete this file."),
                Secret(SecurityKind.PrivateKey, SecurityLevel.Ok, "SSH private key", @"C:\Users\alex\Documents\keys\deploy_ed25519", "Passphrase protected", "Protected by a passphrase.", "Nothing to do; consider moving it to .ssh."),
            ],
        },
        new PortsResult
        {
            Date = Now,
            OthersHidden = 31,
            Ports =
            [
                Port(5173, 4120, "node", "Node.js", @"D:\src\web-shop", Exposure.AllInterfaces, true, "Allowed through Windows Firewall on Private networks"),
                Port(5432, 3184, "postgres", "PostgreSQL", null, Exposure.Loopback, true, "Only this PC"),
                Port(8080, 9932, "java", "Java", @"D:\src\inventory-api", Exposure.Loopback, true, "Only this PC"),
                Port(5040, 7012, "svchost", "Windows service host", null, Exposure.AllInterfaces, false, "Blocked by Windows Firewall"),
            ],
        });

    public static ProjectsReport Projects { get; } = new(
        [
            new ProjectInfo("alex/web-shop", "alex", @"D:\src\web-shop", 1_420 * MB, Now.AddHours(-3), "2 commits not pushed", false),
            new ProjectInfo("alex/inventory-api", "alex", @"D:\src\inventory-api", 380 * MB, Now.AddDays(-2), "Fully pushed", true),
            new ProjectInfo("acme/payments", "alex-acme", @"D:\work\payments", 2_210 * MB, Now.AddDays(-41), "Fully pushed", true),
            new ProjectInfo("acme/design-system", "alex-acme", @"D:\work\design-system", 940 * MB, Now.AddDays(-95), "Uncommitted changes", false),
            new ProjectInfo("alex/old-web", "alex", @"D:\src\old-web", 1_160 * MB, Now.AddDays(-122), "Fully pushed", true),
            new ProjectInfo("alex/dotfiles", "alex", null, 0, Now.AddDays(-12), "On GitHub only", true),
        ],
        [
            new AccountInfo("alex", "Alex Doe", "alex@example.com", 24, 3, 2_960 * MB, true),
            new AccountInfo("alex-acme", "Alex Doe", "alex.doe@acme.example", 61, 2, 3_150 * MB, true),
        ],
        [
            new ActivityInfo(Now.AddHours(-3), "Pushed 3 commits to alex/web-shop", "push"),
            new ActivityInfo(Now.AddDays(-1), "Downloaded acme/payments with alex-acme", "download"),
            new ActivityInfo(Now.AddDays(-4), "Removed alex/tutorial-app from this PC (fully pushed; in the Recycle Bin)", "remove"),
            new ActivityInfo(Now.AddDays(-9), "Signed in as alex-acme", "account"),
        ]);

    private static Finding Make(
        string id, string title, Category category, Risk risk, string path, long size,
        string what, string why, string ifDeleted, IReadOnlyList<Check> checks, IReadOnlyList<CleanAction> actions,
        string? wontLose = null, string? better = null, string? undo = null,
        SizeConfidence confidence = SizeConfidence.Exact, SizeMeasurement measurement = SizeMeasurement.Exact,
        long? freeable = null, IReadOnlyList<string>? blockers = null)
        => new()
        {
            Id = id,
            RuleId = id.Split(':')[0],
            Title = title,
            Subtitle = path.StartsWith(Profile, StringComparison.OrdinalIgnoreCase) ? "~" + path[Profile.Length..] : path,
            Category = category,
            Risk = risk,
            Paths = [path],
            PathSizes = [size],
            Size = size,
            FreeableBytes = freeable ?? size,
            Files = size / (64 * 1024),
            Confidence = confidence,
            Measurement = measurement,
            Explanation = new Explanation(what, why, ifDeleted) { WontLose = wontLose, Better = better, Undo = undo },
            Checks = checks,
            Actions = actions,
            Blockers = blockers ?? [],
            BlockingApps = blockers ?? [],
        };

    private static Installation Install(string version, string cycle, string path, InstallSource source, bool isDefault, SupportStatus support, string latest, bool lts = false) => new()
    {
        Version = version,
        Cycle = cycle,
        Path = path,
        Source = source,
        IsDefault = isDefault,
        Support = support,
        LatestInCycle = latest,
        IsLts = lts,
    };

    private static SecurityFinding Secret(SecurityKind kind, SecurityLevel level, string title, string path, string status, string why, string whatToDo) => new()
    {
        Kind = kind,
        Level = level,
        Title = title,
        Path = path,
        Status = status,
        Why = why,
        WhatToDo = whatToDo,
    };

    private static ListeningPort Port(int port, int pid, string process, string label, string? folder, Exposure exposure, bool development, string firewall) => new()
    {
        Port = port,
        Protocol = PortProtocol.Tcp,
        Pid = pid,
        ProcessName = process,
        Label = label,
        Folder = folder,
        FolderSource = folder is null ? FolderSource.Unknown : FolderSource.WorkingDirectory,
        Addresses = [exposure == Exposure.Loopback ? "127.0.0.1" : "0.0.0.0"],
        Exposure = exposure,
        IsDevelopment = development,
        OwnedByYou = development,
        Firewall = new FirewallVerdict(true, [], firewall),
    };

    private static CheckReport CheckRow(string id, string title, string category, string kind, double seconds, double measure, int found) => new()
    {
        Id = id,
        Title = title,
        Category = category,
        Kind = kind,
        Seconds = seconds,
        MeasureSeconds = measure,
        Found = found,
        Outcome = found > 0 ? CheckOutcome.Found : CheckOutcome.NothingFound,
    };
}
