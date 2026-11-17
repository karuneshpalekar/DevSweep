# P3: Settings, Home, sidebar and tray parity

Branch `windows-p3-settings-home` (cut from `windows`). Look: Windows-native Fluent with the Mac's structure (decision D1), built from the P0 toolkit.

## Settings (one page, like the Mac)

The five tabs are gone. Settings is one scrolling page of sections, with a row of **group chips** (General, Background, Alerts, GitHub, Folders, Privacy, Modules, About) that jump to a group in one click, and a **Search settings** box that shows only the sections whose title or words match (`SettingsSections`). Every control has an automation name.

| Section | What it has | Mac equivalent |
|---|---|---|
| Appearance | segmented Use Windows setting / Light / Dark (same value as the title bar's button) | Appearance |
| Updates | installed version, automatic check, Check now, See what's new | Updates |
| Open at login | switch, state text, Open Startup apps when Windows turned it off | Open at login |
| Scheduled scans | switch, Repeat, Day, Time, Next scan | Scheduled scans |
| Alert me when | disk, growth, end of life, secrets, reminder, Send a test notification, Open Notification settings (when Windows has them off) | Alert me when |
| GitHub accounts | each account with name and email, Save, "no identity set" warning, **email validation**, Add account (the P1 sheet), Refresh | GitHub accounts |
| Downloads | where downloads go (shared with the Clone sheet), removable drives, Find all repos | Downloads |
| Project folders | the folders you added (Remove) and the usual ones (marked "Usual folder"), Add folder, Restore defaults, "not on this PC, so skipped" list, a Windows Security button when a folder is blocked | Project folders |
| Default editor | the P1 choice | (Windows addition) |
| Ignored items | list with Stop ignoring | Ignored items |
| Scanning | exact sizes, check time limit | (Windows addition) |
| Permissions and privacy | blocked-folder status (Controlled folder access), buttons to Windows Security and Privacy settings, what DevSweep reads, what leaves the PC, what to do if Windows blocks a folder | Permissions and privacy |
| Troubleshooting | **Copy report** (Mac-style scan report), Run a scan first, **Copy diagnostics**, Open log folder | Troubleshooting |
| Modules, About | unchanged content | (Windows additions) |

The scan report (`ReportText.Scan`) lists the version, Windows build, free space, last scan, summary, blocked folders, each check with finding and measuring seconds and its result (found, nothing, skipped over N s, failed, partly measured), measuring and total seconds, and rule errors. It has no file or project names: blocked folders are cut to two levels under the profile (`~\AppData\Roaming\…`), check titles are rule titles, error texts are left out. Diagnostics list version, Windows, elevation, modules and `DEVSWEEP_FEATURES`, with the data folder's user name left out.

The account editor is a copy of the Projects one (the Projects view files are P4's); the email check (`IdentityValidation`) is also applied in `ProjectsViewModel.SaveIdentity`, so a bad email is refused from both places.

## Home

Subtitle "Scanned just now · next scan Monday, 9:00 AM" (from the schedule, `ScheduleText`), refresh icon button (Ctrl+R) and Stop scan; a warning banner when Windows blocked folders with **Open Settings** going to Permissions; a banner for rule packs that could not be read; states (`HomeState`): not scanned (Scan now), scanning (live status), scanned; first-scan and nothing-changed sentences; Health items capped at 4 (security, one exposed port, then tools) with "Showing the 4 most important. Health has the rest."; two columns above 860 px, stacked below.

## Sidebar

Free-space card at the top (free GB, bar with the cleanable part, "X GB can be cleaned"), hidden when the pane is compact; numbers on the places (Clean up size, Projects needing a push, Health items); the footer has the update card, up to two "Needs your attention" cards and the "Last scan … · next …" line inside a scroll box whose height is what is left after the places (`PaneReserved`), so History and Settings stay in view (the navigation bounds checks at 1000 x 680 are unchanged).

## Title bar

The gear and the theme button are the last two columns before the caption-button inset, so they are right-aligned. The shots had no caption buttons (a capture of the window's content does not include them), which made the buttons look as if they floated; screenshot mode now draws stand-ins for minimise, maximise and close in the inset.

## Tray

Left click opens a **flyout** (`TrayFlyoutWindow`): a small borderless always-on-top WinUI window placed above the click point inside the work area, closing when it loses focus. It has the Mac popover's content: free space with its bar, what can be cleaned (Review, or Scan now before the first scan), up to two alerts, three recent projects with Open (the chosen editor), Scan now, Open DevSweep, Settings, Quit, last scan. If the window cannot be created the click opens DevSweep. The native right-click menu stays and now also lists what can be cleaned, Settings and the last scan. Under Native AOT and unpackaged the window builds and is captured in the tour (`tray-flyout-*.png`).

## Welcome

Same three columns and wording as the Mac, in Windows terms; the third column also says what Controlled folder access can do (the Mac's Full Disk Access choice has no Windows equivalent: there is nothing to grant).

## Tests

`SettingsHomeTests.cs`: sections and search, identity validation, folder rows, add/remove/restore folders and persistence, ignored items, report and diagnostics text without names, permissions, schedule text, Home states, sidebar counts and cards, tray flyout and menu. The screenshot tour asserts every section and control by automation name, that search narrows the sections, that the identity editor refuses a bad email, the Home states and banners, and the flyout, and the workflow checks the shots exist.
