# M5: tray, scheduled scans, alerts, updates

Everything here lives in `src/DevSweep.App.ViewModels/Background` and `/Updates` (no WinUI, unit tested)
and `src/DevSweep.App/Background` (Win32 and Windows App SDK). The tray is part of `DevSweep.exe`
itself (no helper exe), so the M6 installer stops it by process name like any other copy.

## Tray icon

A hidden top-level window, an icon drawn in code and `Shell_NotifyIcon` (version 4 callbacks), as
source-generated `LibraryImport`s (`TrayNative`). H.NotifyIcon.WinUI was not used: it is a XAML control
that relies on reflection-based XAML loading, and the views here are C# with Native AOT; the wrapper is
~300 lines and needs no XAML. Click opens DevSweep; right-click or the menu key opens a native menu built
each time from `TrayViewModel.Items()`:

1. free space on the system volume (`412 GB free on Windows (C:)`)
2. active alerts (every problem present now, announced or not; click opens the right screen)
3. recent projects (only when the Projects module is on and loaded; click shows the folder in Explorer)
4. Open DevSweep, Scan now (a normal read-only scan; disabled while scanning or while Review is open), Quit

The tooltip shows free space and how many things need attention. The icon is re-added when Explorer
restarts (`TaskbarCreated`). Windows 11 may keep a new icon behind the `^`; Settings says so.

**Closing the window** keeps DevSweep in the tray only while "Open at login" is on (`AppWindow.Closing`
is cancelled and the window hidden); otherwise it exits. Quit in the tray always exits. A second start
(Start menu while it sits in the tray) signals the first through a named event and exits
(`SingleInstance`), so there is one tray icon, one scheduler, one update check.

## Open at login

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `DevSweep` =
`"<path>\DevSweep.exe" --background` (starts hidden in the tray; the first show does the first scan).
Off by default; written only when the user turns the switch on, removed when they turn it off; HKCU
only, never an elevation. If Windows (Settings, Apps, Startup, or Task Manager) turned the entry off
(`StartupApproved\Run`, first byte odd) Settings says so and offers Open Startup apps. A moved install
rewrites an existing entry at start (`RepairIfMoved`, never turns it on). Switching it on again clears a
Windows "off" marker the user set earlier.

## Scheduled scans

Daily, or weekly on a chosen day, at a chosen time (`ScanSchedule`; Mac rules). Checked every 30 s while
DevSweep runs (`ScanScheduler`, `PeriodicTimer`), the first check at start. Due once the first run time
after the last scheduled scan (`alerts.json` `LastScheduledRun`) has passed, so a PC that slept or was off
catches up (within 30 s of waking, or at start) with one scan, however many runs were missed; a schedule
just switched on or changed counts from that moment (`Since`). Default off.

**Read-only, enforced in code:** `ScheduledScans` is `ScanViewModel.ScanAsync` (the Scan button's path,
which also records the snapshot with `ScanSnapshotStore.Record`), then Health's read-only loads when an
alert needs them, then the alert rules. The class has no reference to Review, the cleaner, History
restore, the quarantine or terminal scripts, and does nothing while Review is open. Tests run it against a
backend that records every call and throws on `Clean`, `Prepare`, `Remeasure`, `Restore`, `EmptyQuarantine`,
`OpenInTerminal`, `SetModuleEnabled` and `OpenFix`: a full scheduled run (scan, snapshot, Health, alerts,
toast) makes none of them.

## Alerts

`AlertCoordinator` feeds M3b's `AlertEvaluator` with the latest scan (system volume used %, `ScanChanges.Grew`
from the snapshot a week back) and Health (end-of-life installs, secret files; needs Health on) and the
persistent `AlertStore` ledger (`alerts.json`). Disk 70 to 95 % full (default 85), growth 1 to 10 GB in a
week (default 2), tool past end of life, new file that looks like a secret (only against the baseline of the
previous scheduled run); each switchable on its own. Each problem is announced once; if it is still there
it is announced again after the reminder days (3 to 7, or the default 3 for a full disk and 7 for the rest;
added to Core as `AlertSettings.ReminderDays`, 0 = old behaviour, so existing tests and the CLI are
unchanged). Several problems become **one** toast (`AlertToasts.Compose`: title "DevSweep found 3 things to
look at", up to three lines); one problem keeps its own text. Clicking opens the screen they share, else
Home. Alerts are evaluated after scheduled scans only (as on the Mac).

Toasts: Windows App SDK `AppNotificationManager.Register("DevSweep", icon)` (unpackaged, the icon is a PNG
written to `%LOCALAPPDATA%\DevSweep\app-icon.png`), `AppNotificationBuilder` with `target=<screen>` as the
launch argument. A click while running arrives through `NotificationInvoked`; a click when the app is
closed starts it and `AppInstance.GetActivatedEventArgs()` gives the same argument (`AppToasts.LaunchTarget`).
Settings, Alerts shows whether Windows will display them and has "Send a test notification".

## Update check

`GitHubUpdateChecker` (replaces the stub): one `GET /repos/karuneshpalekar/DevSweep/releases?per_page=30`,
the newest published release whose tag starts `windows-v` (the Mac releases in the same repository, drafts
and pre-releases are ignored), compared numerically with the running version (`windows/version.props` via
`AppInfo.Version`). Headers: Accept, API version, `User-Agent: DevSweep/<version>`; no body, no identifier,
no query beyond the page size. Offline, 403/429 (rate limit), other status, a timeout, malformed JSON: a
`Failed` result with a message, never an exception (only cancellation throws). The page address must be
https github.com, else the tag's page is used. `UpdateViewModel`: a card in the sidebar when newer ("See
what's new" opens the release page, "Later" skips that version), one check about every 20 hours (retry
no sooner than 2 hours after a failure), Settings switch (off: no checks, no card), Check now.

## Settings

Five pages (SelectorBar): General, Modules, Open at login and schedule, Alerts, Updates and about; each
has a view-model test and is in the screenshot tour in light and dark (`settings-<page>-<theme>.png`).

## Tests

`ScanScheduleTests` (due/catch-up/baseline/zone), `ScheduledScanTests` (read-only proof, Review busy, once then
next, off), `AlertFlowTests` (one toast, once, reminders, per-alert switch, secrets baseline, targets, core
reminder days), `TrayMenuTests`, `LoginItemTests` (fake store, and the real registry under a throwaway HKCU
subkey), `BackgroundSettingsTests` (ranges, defaults, round trip, old files), `GitHubUpdateCheckerTests`
with recorded GitHub responses (no network anywhere), `UpdateViewModelTests`. App smoke (`--smoke`) creates
the tray icon, builds its menu, updates the tooltip and disposes it, builds a toast payload, tries toast
registration and reads the login state; CI checks the log. The installer test now starts
`DevSweep.exe --background` and asserts it is still running after 8 s and that an in-place upgrade stops it
(administrator cycle; the standard-user cycle has no desktop, as M6 noted).

## Numbers from CI

Run [42429042905](https://github.com/karuneshpalekar/DevSweep/actions/runs/42429042905) (windows-latest,
merged with M6): all three jobs green. Core unit tests 447 passed (1 skipped), accuracy suites unchanged;
app tests 153 passed (113 new); Native AOT publish `DevSweep.exe` 19,488,768 bytes (294 files,
222,230,192 bytes, zip 73,454,786); `--smoke`: tray icon added to the notification area on the runner,
menu of 4 items, tray ok; screenshots: 10 Settings pages plus the existing tour, all non-blank; installer
job: the tray instance was running after 8 s and was stopped by the upgrade.

## Not tested on CI

- A real toast on screen and a click on it (the runner session reports toasts unsupported; payload building,
  the activation parser and the routing are tested).
- A real sign-in start from the Run entry (the entry's reading, writing and removal are tested against the
  real registry; Windows starting it at logon is not).
- Wake from sleep (the 30 s timer and the catch-up maths are tested with an injected clock).
- Right-click on the tray icon with a human (menu building and item dispatch are tested).
- DST gaps in the schedule's local time (clock times in a fixed-offset zone are tested).

## Known gaps

- Alerts for end of life and secrets need the Health module (off by default); Settings says so.
- Alerts run after scheduled scans only; a manual scan refreshes the tray's list but does not toast.
- The tray menu is a native menu (not Fluent styled).
