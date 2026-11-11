# M4: the WinUI 3 app

`src/DevSweep.App` (the window and views), `src/DevSweep.App.ViewModels` (view models and the
services they call, no WinUI) and `tests/DevSweep.App.Tests`. Every decision about files is made
in DevSweep.Core; the app only shows it and asks Core to act.

```powershell
cd windows
dotnet build src/DevSweep.App -c Release
dotnet test tests/DevSweep.App.Tests -c Release
dotnet publish src/DevSweep.App -c Release -r win-x64 -o artifacts/app    # Native AOT, self-contained
artifacts\app\DevSweep.exe                                                # the app
artifacts\app\DevSweep.exe --smoke                                        # engine + rules + window, exit 0
$env:DEVSWEEP_SHOTS = "$PWD\shots"; artifacts\app\DevSweep.exe            # screenshot tour, sample data
```

## Stack

| | |
|---|---|
| UI | WinUI 3, Windows App SDK **2.6.5** (latest stable), unpackaged (`WindowsPackageType=None`), self-contained (`WindowsAppSDKSelfContained`), x64, Windows 11 22H2+ (`TargetPlatformMinVersion` 10.0.22621.0) |
| Publish | **Native AOT** (`PublishAot`, `CsWinRTAotOptimizerEnabled`); CI fails if the published folder holds a managed `DevSweep.dll` |
| MVVM | CommunityToolkit.Mvvm 8.4.2 source generators (`[ObservableProperty]` partial properties, `[RelayCommand]`), no reflection |
| Look | Mica backdrop, content in the title bar, `NavigationView`, `SelectorBar` tabs, Fluent theme resources only, light/dark following Windows with a Settings override (Use Windows setting / Light / Dark); high contrast comes from Windows |
| Views | Built in C#, no XAML markup. There is no XAML compiler step and no `{Binding}`: views subscribe to view-model `PropertyChanged`/`CollectionChanged` (`Toolkit/Subscriptions`, disposed with each page). The one XAML string is the style dictionary (`Toolkit/Theme.cs`), parsed at start-up with `XamlReader.Load` and proved by the smoke test. `App` implements `IXamlMetadataProvider` over `XamlControlsXamlMetaDataProvider`. |

Why C#-built views: there is no .NET SDK in the agent sandbox, so CI is the only place a XAML
compiler would run. With views in C# the whole app (CsWinRT's AOT analyzers included) compiles and
is analysed locally with the same compiler and rules as CI, and AOT has nothing to trim away.

## Places and modules

`MainViewModel.BuildPlaces` decides the navigation from Core's registered modules
(`EngineBackend.DescribeModules`: `engine.Modules`, `engine.Disabled`, and known places not in this
build):

| Place | Shown when |
|---|---|
| Home, History, Settings | always |
| Clean up | `module:cleanup` or `module:packs` is on |
| Health | `module:health` is on (on by default since 0.1.1; it was off in 0.1.0); tabs follow `module:health.runtimes/.security/.ports` |
| Projects | `module:projects` is on (on by default since 0.1.1; it was off in 0.1.0); tabs Cleanup, Accounts, Activity follow `module:projects.cleanup/.accounts/.activity` |

A place whose module a flag turned off is hidden (a place whose module is not in the build at all
shows an "is off" page). Settings, Modules lists every module and the Health and Projects tab flags
with switches; they write `%LOCALAPPDATA%\DevSweep\features.json` (Core's
`FeatureSettings`, same file the CLI reads) and apply at the next start. `DEVSWEEP_FEATURES` still wins.

## What each place does (and the Core API it calls)

| Place | Behaviour | Core |
|---|---|---|
| Welcome | Once, before the first scan: what it looks at, what it never does, what Windows may say (SmartScreen "More info, Run anyway"; no administrator needed; UAC only for admin commands the user runs) | — |
| Home | Free space per fixed volume (`DriveInfo`, i.e. `GetDiskFreeSpaceExW`), cleanable part on the system volume, "Since <date>" grew/new (Mac rules: 200 MB minimum, baseline a week old) and "You cleaned N items", Needs attention (Health security/runtimes/ports, checks skipped, folders not readable, rule pack errors, items blocked by a running app) | `ScanSnapshotStore` (M3b, `scans.json`), `HistoryStore` |
| Scan | Per-check progress (each check as it starts, done, items found while measuring), Stop (cancels; previous results kept), scan report like Mac 0.9.6: summary, per category each check's outcome, found, partial sizes, folders not readable, detect + measure seconds, skipped (too slow), overview/exact mode, bytes counted once | `DevSweepEngine.ScanAsync`, `ScanProgress`, `ScanReport` |
| Clean up | Filter chips by kind (Caches, Old and unused, Large files, Docker and WSL, Backups, Leftovers, Project dependencies, Windows), search, grouped by risk with the Mac labels (Rebuilds itself, Old version, Leftover from a removed app, Holds your data, Needs admin), size with confidence badge (Overview, Reported, Partial, Access denied, Cloud files, Shared links; "at least" for lower bounds; tooltip on every size), action picker, select all per group, Always ignore, Show in File Explorer | `Finding`, `SizeConfidence`, `SizeMeasurement` |
| ⓘ Inspector | What it is, Why it's flagged, What was checked (`Finding.Checks`, warnings marked), If you clean it, You won't lose, Before you do, blocking apps, Better option, How to undo, What to do (actions, or the manual command with Copy and "Open in PowerShell"), locations with sizes | `Explanation`, `TerminalScript` (M3b) |
| Review | 1) `FindingRemeasurer` on every selected item (streams on); 2) `ReviewPlanner` (new, below) says what will actually happen; groups: Can't be cleaned right now, Deleted right away, Cleaned by the tool's own command, Moved to the Recycle Bin, Moved to DevSweep's quarantine, You run these yourself; exact operations ("Exactly what will be done"); blockers ("Close X first", Restart Manager); "can't be undone" warning when anything is deleted or run; 3) `Cleaner.RunAsync` with progress; outcomes with freed-space reconciliation ("Shown 3.3 GB · expected 3.3 GB · C: actually freed 3.3 GB, as expected", or "nothing is freed yet: it's in the Recycle Bin until you empty it") | `FindingRemeasurer`, `ReviewPlanner`, `Cleaner`, `FreedSpace` |
| History | By day; Restore for Recycle Bin and quarantine items while they are still there; Restored / Rebuilds itself / No longer in the Recycle Bin; quarantine size and Empty (confirmed) | `HistoryStore.Restore`, `QuarantineStore.List/Empty` |
| Projects | Tabs Projects (every clone and GitHub repo, size, safety badge, "Compared as of the last fetch (date UTC)" on every clone and a banner saying so; "Unknown: not safe", "In OneDrive, not checked"; Push… opens a visible terminal), Cleanup (1 week to 3 months, exact sizes, safe vs reasons), Accounts (per account repos, on this PC, identity or "Identity missing", active in gh, owners nobody is signed in as, Add account… in a terminal), Activity | `ProjectsService` (M3c): `ScanAsync`, `IdleAsync`, `Push`, `SignIn`, `Activity`; `ProjectsSnapshot`, `RemovalSafety`, `IdleCleanupResult` |
| Health | Tools and versions (`VersionsResult`: installs, default, support badges, issues, `RuntimeStep` fixes with Copy and "Run in PowerShell" through `TerminalScript.Write` + `Open`, project requirements), Security (`SecurityResult`: Needs action / Looks fine), Ports (`PortsResult`: Development / Apps and Windows, exposure, firewall verdict) | `HealthChecks` (M3b) |
| Settings | Appearance, project folders (type or Browse; used for scans and as the guard's dev roots), ignored items, modules and Health tab flags, scan options (overview/exact, check time limit), update check toggle and Check now (`IUpdateChecker`, a stub until M5 that contacts nothing), about and privacy | `FeatureSettings`, `SafetyGuard.ForCurrentUser(devRoots)` |

App settings live in `%LOCALAPPDATA%\DevSweep\app-settings.json` (source-generated JSON, Core's
`AtomicFile`).

## Core additions (additive)

- `Core/Review/ReviewPlanner.cs`: `ReviewPlanner.Prepare(plan)` → `ReviewPlan` of `ReviewItem`
  (`Outcome`: RecycleBin, Quarantine, DeleteForGood, RunCommand, Manual, Blocked; `Summary`,
  `Commands`, `Blockers`, `Notes`, `CanBeUndone`; `ReviewPlan.HasPermanentChanges`, `Runnable`). It
  makes the cleaner's checks in the cleaner's order without changing anything: named blockers
  running, `SafetyGuard.Check` per path, Restart Manager (`FileLocks.Check`), `RecycleBin.Assess`
  and, when the bin would not keep it, the quarantine root for that volume (no root: blocked, never
  "permanent"). `IReviewProbe` / `SystemReviewProbe` make it testable.
- `CommandLineText.Join/Quote`: argv shown the way `CommandLineToArgvW` parses it back.

I first added a scan-snapshot store; M3b merged its own (`Health/Alerts/ScanSnapshotStore`), so mine
was removed and Home uses M3b's.

## Accessibility

Every button, toggle, combo box, check box, chip, expander, tab and badge has an
`AutomationProperties.Name` (row check boxes "Select npm cache", ⓘ "Explain npm cache: what happens if
I clean this?", sizes read with their confidence). Keyboard: NavigationView and every control in tab
order, Ctrl+R scans again, Esc closes the inspector or Review, the Review/Welcome sheet sits inside the
window and disables the navigation behind it so focus cannot leave it, focus moves into a sheet when it
opens. Colours are Fluent theme resources only (badges use `SystemFillColor*` brushes with a 1 px
border in high contrast); screenshot mode turns Windows high contrast on (`SPI_SETHIGHCONTRAST`) for
Home, Clean up with the inspector, and Review, and off again.

## Screenshot mode and smoke test

- `DEVSWEEP_SHOTS=<dir>`: sample data (`ViewModels/Sample`, deterministic: fixed date, made-up
  `C:\Users\alex` machine; Review is prepared by the real `ReviewPlanner` against a fake machine
  where Chrome is running and the Android image is too big for the Recycle Bin), in light and dark:
  welcome, home, cleanup, inspector, inspector-admin, selection, review, review-done, scanning,
  projects ×4, health ×3, history, settings; with `DEVSWEEP_SHOTS_HIGH_CONTRAST=1` also
  home/cleanup/review in high contrast. PNGs come from `RenderTargetBitmap` of the window's own
  content (Mica is replaced by the solid base colour, which a bitmap can capture). `shots.log` lists them.
- `--smoke`: real engine with the user's flags, `LoadRules` (fails on any rule error), main window,
  every place built once, exit 0; log on stdout and `%TEMP%\devsweep-app-smoke.txt`.

## CI (`app` job in `.github/workflows/windows.yml`)

Build (Release, warnings as errors), view-model tests, Native AOT publish of the self-contained x64
folder (zipped as artifact `devsweep-app-win-x64`), `--smoke` (120 s limit) with default flags and
again with `+module:health`, screenshot mode (300 s limit, at least 30 PNGs, none under 10 KB)
uploaded as `app-screenshots`.

## Numbers from CI

CI run [42060982908](https://github.com/karuneshpalekar/DevSweep/actions/runs/42060982908)
(`windows-latest`, Windows Server 2025, merged with M3a, M3b and M3c; both jobs green, the main
job being the administrator and standard-user runs of everything else), app job:

| | |
|---|---|
| Build | Release, warnings as errors (CsWinRT AOT analyzers, trim/AOT analyzers, code style), no warnings |
| View-model tests | 40 passed (Review planner against a fake machine, size badges for every confidence, kinds, places from modules, Review flow re-measure, groups, clean, reconciliation; Home; History restore/empty; settings round trip; the real engine's modules: Health and Projects off by default, on with flags, tabs off with their sub-flags; Projects "Unknown" and "not checked" never safe, the last-fetch note) |
| Native AOT publish | `DevSweep.exe` 18,682,368 bytes; folder 294 files, 217,798,696 bytes (Windows App SDK runtime included, self-contained); zip 72,415,131 bytes; no managed `DevSweep.dll`; `DevSweep.pri` built |
| `--smoke` | exit 0 in 1.5 s: 4 places with default flags (Health and Projects off), 6 places with `+module:health +module:projects` (rendered in 1.6 s); 83 rules from 16 packs, 0 errors |
| Screenshots | 39 PNGs of the app window (1028x779 on the runner's desktop): 18 screens in light and dark, plus home, cleanup and review in high contrast (`AccessibilitySettings.HighContrast` was true while they were taken), 38-112 KB each |

The first runs found two things only the real runtime shows: an unpackaged app with no XAML pages
gets no `resources.pri` unless `EnableMsixTooling` is set (WinUI then cannot find its own
`themeresources.xaml`), and under Native AOT a `Style` read back out of a `ResourceDictionary` does not
come back typed, so each style is parsed on its own (`Theme.Style`) and accent buttons take
`{ThemeResource AccentButtonStyle}` from XAML.

## Deviations and gaps

- Views are C#, not XAML (above).
- Security "Move to Recycle Bin" and Ports "Stop…" are not in the app yet (M3b has
  `ProcessStopper`; Security removal needs a `Finding` built for the cleaner): the rows offer Show in
  File Explorer and Copy port.
- The Mac part pickers (per-profile ticks in Review) have no Windows equivalent: Windows findings
  have no parts yet.
- Menu bar / tray is M5; the update check is a stub interface (`IUpdateChecker`).
- Projects: download (strategy sheet), remove, publish and add-by-URL are in Core (M3c) and the CLI
  but not yet in the app's page; the page lists, checks, measures idle projects, pushes and signs in.
