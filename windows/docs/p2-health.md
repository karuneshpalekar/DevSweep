# P2: Health parity

Health reaches the Mac app's structure and features in a Windows-native look (D1), built only from the P0 toolkit. The guided PostgreSQL upgrade stays deferred (D4); PostgreSQL is a normal runtime with its normal notes and steps.

## What is in it

**Shell** (`Views/HealthView.cs`, `HealthViewModel.cs`): tab bar with counts (`Tools and versions · 9`, `Security · 3`, `Ports · 4`), "Checked 12:38 · support dates from endoflife.date" (or the offline note), Check again, skeleton while loading, error banners, and a "Health is off" state with a one-click **Turn on Health** that writes `features.json` (the health flag and its three tab flags) and says a restart applies it. One side panel serves all three tabs. Home's "needs attention" rows carry an item (`tool:`, `security:`, `port:`) and open the right tab with that item's panel (`MainViewModel.OpenAttention`).

**Tools and versions**: header summary ("10 tools · 4 need attention"), chips All / Needs attention / Up to date with counts, a legend, and one `ListRow` per tool (name and summary, one pill per install coloured by support status, a terminal glyph on the install a new terminal runs, a dot on a running server, "N to look at" or "All good"). Extra rows: Windows, Package managers, Your projects. The panel (`HealthPanels.cs`) has "What DevSweep noticed" (issues in plain words plus what a new terminal runs), "What you can do" step cards (title, detail, commands with a "show the full script" toggle, Copy, Run in PowerShell in a visible window that waits for Enter; admin steps are marked and Windows asks via UAC only when the user clicks inside that window), "Installed" cards (version, owner, support dates, latest patch, data folder, path, Show in File Explorer, Copy path), "Not an install" notes (Store aliases), and "Projects that need it" (OK / Not installed / End of life, with the install step). Existing automation names (`Copy the commands for ...`, `Open ... in PowerShell`) are kept.

**Package-manager report** (`Core/Health/Packages`): the Windows counterpart of the Mac's Homebrew report.

| Manager | How it is read | Notes |
|---|---|---|
| winget | `winget list --upgrade-available --disable-interactivity --accept-source-agreements`, 90 s limit, no shell | the table has localised headings, so columns are found by position (the dashes line and the heading above it); "nothing to upgrade" is recognised by exit code 0x8A150014, not by words; any other unrecognised output becomes "couldn't read the result" |
| Scoop | its own files only (`apps\<name>\<version>\install.json`, the bucket's manifest); no program is run | outdated, held back, "no longer in its bucket" (deprecated); as fresh as the last `scoop update` (the panel says so); `current` junctions are never followed |
| Chocolatey | `choco outdated -r --no-color`, 90 s limit | pipe separated, language independent; upgrade step needs an administrator |

Each manager is reported only when installed. The panel shows Deprecated, Outdated (with Held back tags), Caches (size, Show in File Explorer), Leftover folders (empty Scoop app folders, empty Chocolatey package folders, with a Recycle Bin step), upgrade steps, and "Installed twice" (the same release line installed by two owners, from the tools list). Nothing is upgraded or removed without the user running a step in a visible window. The report loads after the rest of Health (it is the slow part) and shows as a "Checking winget, Scoop and Chocolatey" row meanwhile.

**Security**: Needs action / Looks fine lists (ignored items hidden), a panel with why, what to do, the git state for `.env` files (committed, not ignored, kept out of git) and the exact fix commands as a step card (`git rm --cached`, `.gitignore`), what was checked, and actions: Move to Recycle Bin (through the M1 cleaner so it is in History; blocked with a plain reason for online-only OneDrive files, links and missing files, and not offered for `.env` files), Show in File Explorer, Ignore (stored in the same ignore list as Settings, Ignored items). Secret values are never shown or stored.

**Ports**: Development vs Apps and Windows, project folder with the confidence note ("from the process's working folder"), "On your network" tag and a warning with the firewall verdict, command line, Open in browser (development web ports only; databases get none), Copy URL, Copy port, Show folder. Stop asks for confirmation, then asks the program to stop the way Ctrl+C does (`ProcessStopper.AskToStop`); if it is still running, a second confirmation offers End it now. System processes and other users' are refused with the reason shown in the panel; the process id is checked against the listed program before anything is stopped (ids are reused). The list re-reads every 4 seconds while the tab is on screen and the window is showing (a timer in the view; it is paused when the window is hidden in the tray or minimised, and off in screenshot mode).

## Core and plumbing changes (all additive)

- `Core/Health/Packages/*`, `HealthChecks.PackagesAsync`.
- `IHealthSource` gained default-implemented members (`PackagesAsync`, `PortsAsync`, `StopRefusal`, `StopAsync`, `TrashBlockedReason`, `TrashAsync`), so existing implementers still compile. `CoreHealthSource` implements them; `EngineBackend` passes it a cleaner.
- `HealthViewModel` moved out of `PlacesViewModels.cs` into its own file (the enums stay).
- `AttentionRow` has an optional `Item`; `MainWindow.IsShowing`; `Shell.OpenUrl`; Health glyphs in `Icons`.
- Sample data was extended (EOL runtime with fix, a shadowed default, a Store alias, several installs, running PostgreSQL, Windows, package managers, more secrets and ports); the existing sample findings and EOL installs are unchanged so earlier tests still hold.

## Tests

- Core: `PackageReportTests` (recorded winget output in English and German, spinner frames, truncated names, Chocolatey `-r`, Scoop files with held, removed and empty folders, read-only queries with a time limit and `--disable-interactivity`, unreadable and timed-out results, duplicates).
- App: `HealthViewModelTests` with a fake machine (filters and counts, selection, step run requests, ignore, Recycle Bin blocking, graceful-then-confirm stop, refresh, URL rules, one-click enable, Home deep links) and `RealStopTests` (a spawned `ping.exe` is ended through the real source; a stale pid is refused).
- Screenshot tour (`ScreenshotTour.Health.cs`): every tab, EOL panel, shadowed default and Store alias, OK runtime, package-manager panel, project requirements, Security (needs action, a finding, `.env` fix, OneDrive-blocked), Ports (selected, Stop confirmation, still running, refused), loading, not checked, failed, off, empty states, and high contrast. It asserts through UI Automation names that each panel action is reachable.

## Not verified without a human

winget, Scoop and Chocolatey are not installed on the CI runner, so the real output of each is checked only against recorded samples. The "Run in PowerShell" window is not driven in CI. Rows are not virtualised (the lists are short; the P0 `ListRow` is not an `ItemsRepeater` template).
