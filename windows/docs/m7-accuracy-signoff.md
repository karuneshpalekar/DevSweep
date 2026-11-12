# M7: Accuracy sign-off

DevSweep's overriding requirement on Windows is accuracy (PLAN section 1). A cloud session cannot sit in
front of real Windows machines, so M7 ships two things a person can use to sign it off:

1. This checklist: what to prepare, what to click, what counts as a pass.
2. An automated kit, `scripts/Verify-DevSweep.ps1`, that does as much of the sign-off as a script can
   **on the real machine** and writes a report. It also runs in CI on `windows-latest`
   (job `m7-verify`, artifact `m7-verify-report`).

The kit proves the numbers and the claims. It cannot see a toast, a tray menu, a high-contrast theme or a
SmartScreen prompt; those are the manual checks in section 5.

## 1. How to run the kit

Open PowerShell (Start menu, type `powershell`) in the folder that holds `Verify-DevSweep.ps1`
(the `scripts` folder of the installed or portable DevSweep; or the repository's `windows\scripts`), then paste:

```
powershell -ExecutionPolicy Bypass -File .\Verify-DevSweep.ps1
powershell -ExecutionPolicy Bypass -File .\Verify-DevSweep.ps1 -RunCleanTest
```

Run the first line first. Right-click the file and choose "Run with PowerShell" does the same as the first
line. The second line adds the removal tests (section 3 of this page explains them); do it on a machine you
are comfortable creating a few hundred MB of throwaway files on.

- It needs `devsweep.exe` (the command-line program). It looks next to the script
  (the installer and portable zip put a copy in `scripts\`), then in `%LOCALAPPDATA%\Programs\DevSweep`, on
  PATH and in the current folder; or pass `-DevSweepPath <folder or devsweep.exe>`.
- **What it touches.** Without `-RunCleanTest` nothing is changed, moved or deleted: it runs
  `devsweep scan --json --exact`, `inventory`, `runtimes --offline` and `ports`, reads the registry, the Start
  menu, the Scoop and Chocolatey folders, the PATH and file sizes, and writes the report. No administrator
  rights, no network, nothing uploaded. With `-RunCleanTest` it also creates a folder
  `%TEMP%\devsweep-verify-<id>` (with a marker file), lets DevSweep's cleaner move and restore items in it,
  deletes a few throwaway items for good, and removes the folder. It refuses to touch anything it did not
  create. Your own History is not written (the test uses its own History file).
- **Where the report lands.** `Verify-DevSweep-<date-time>\` on your Desktop (or `-OutputPath`):
  `report.md` (read this), `report.json`, and `raw\` (the exact DevSweep outputs that were compared).
  Your user name and OneDrive organisation name are replaced by `<user>` and `<organisation>` unless you pass
  `-NoRedact`. Read it before you share it; sharing is optional and manual.
- **Reading it.** The first line says PASS, WARN or FAIL in plain words. Every FAIL and WARN has "what it likely
  means" and "where to look". Exit code: 0 no FAIL, 1 a FAIL, 2 the kit could not run.
- `-SelfTest` checks the kit's own comparison logic on made-up data (and its native helpers on a temporary
  folder); CI runs it on PowerShell 7 and Windows PowerShell 5.1.

## 2. The four accuracy promises

| # | Promise (PLAN 1) | How to verify | Pass threshold | Kit check |
|---|---|---|---|---|
| 1 | **Sizes are true** | The kit takes the 15 largest findings of `scan --exact` and re-measures them with its own walker (FindFirstFileEx, allocation size, `GetCompressedFileSizeW` for compressed and sparse files, hard links once by file ID, reparse points never entered, cloud-only files never opened). With `-RunCleanTest` it also compares the shown size of a fixture with the free space that comes back after a permanent delete (median of 3 runs). By hand: Explorer's "Size on disk" for a few folders. | Within **1% or 10 MB**, whichever is larger | `Sizes`, `clean.freed` |
| 2 | **Detection is true** | Every "leftover" is checked against the Uninstall registry (four views), Store packages (`Get-AppxPackage` and the package repository), Scoop, Chocolatey, Start menu shortcuts and Program Files; every program Windows lists is looked up in DevSweep's inventory; runtimes are compared with `where.exe` (registry PATH, as a new terminal gets), the `py` launcher, JAVA_HOME and registry install keys; Store app-execution aliases must not be installs; listening TCP ports are compared with `Get-NetTCPConnection`. | **Zero false leftovers** (a release blocker); no Store alias reported as an install; no install missing; PATH and ports equal | `Leftovers`, `Runtimes`, `Ports` |
| 3 | **Removal is true** | With `-RunCleanTest`: a fixture with a junction pointing at a decoy folder, a hard-link pair, a compressed file, a read-only file, a file larger than the Recycle Bin limit (a sparse file, so it costs no disk), and a path over 259 characters is moved by DevSweep's real cleaner. The kit reads the Recycle Bin's own `$I`/`$R` files and the quarantine folder to see where it went, checks the decoy byte for byte, restores through History and compares every file (SHA-256 up to 64 MB). | "Recycle Bin" claims backed by a `$R`/`$I` pair; "quarantine" claims backed by files; the decoy untouched; restore byte-identical; nothing outside the item changed | `clean.dest.*`, `clean.restore.*`, `clean.delete.outside`, `clean.decoy` |
| 4 | **We say when we don't know** | The kit flags a size that is wrong while DevSweep showed it as exact; leftovers offered while an inventory source was incomplete; a recycle claim when Windows' limit is unknown. By hand: open a folder you cannot read, a OneDrive Files-On-Demand folder, a drive without a Recycle Bin, and look at the badges. | Uncertainty is always visible: partial, access denied, cloud-only, "may be shared", "unknown", "cannot be undone" | `size-incomplete`, `inventory-incomplete`, manual 5.x |

Notes on the free-space check: free space on a busy drive is noisy (indexer, Defender, the page file). The kit
repeats the delete (default 3, `-CleanRepeats`), judges the median, and reports a WARN (not a FAIL) when the
runs disagree with each other more than half the allowed difference. A move to the Recycle Bin or quarantine
frees nothing until the bin is emptied, so the kit checks "free space did not change" for moves and the real
freeing with the permanent-delete fixture; it never empties your Recycle Bin or quarantine.

## 3. Reference machines (PLAN 6)

One developer PC can cover several profiles. Run the kit on each machine that exists, and note in the results
table which profiles it covers (the kit's report says which profiles the PC resembles).

| Profile | Install or prepare | Why it matters for accuracy |
|---|---|---|
| **a**: winget + Visual Studio + .NET + WSL | Windows 11 22H2 or later; `winget`; Visual Studio 2022 (workloads: .NET desktop, C++); several .NET SDKs (some old); WSL 2 with at least one distro (use it a little so `ext4.vhdx` grows); a project pinned with `global.json` | VS caches and instance folders, .NET SDK detector, WSL vhdx size 0 / "at most" text, winget installs in the inventory |
| **b**: Scoop + Node/Python + Docker Desktop | Scoop (user install) with `nodejs`, `python`, `git`; nvm/fnm/Volta/pyenv for at least one of Node or Python; Docker Desktop with a few images and build cache; pnpm with a store (hard links); a project with `node_modules` | Scoop `current` junctions, hard-link dedupe (pnpm store), version managers, Docker vhdx and `docker system df`, Store `python.exe` alias if Python from the Store was ever installed |
| **c**: Chocolatey + Android Studio + JetBrains + OneDrive Documents | Chocolatey (needs an admin once to install); Android Studio with an SDK, an emulator image and an AVD; one or two JetBrains IDEs (try two versions side by side); OneDrive signed in with Known Folder Move so Desktop and Documents are redirected, Files On-Demand on, some files online-only | Known folder redirection (protected folders), cloud placeholders counted as 0 and never opened, JetBrains old-version folders, Android old images/NDKs, Chocolatey inventory |

Also useful: a second fixed drive or a Dev Drive (ReFS), a USB stick (no Recycle Bin), a standard (non-admin)
account, a 4 GB / low-free-space machine (quota behaviour), and a PC with last-access updates disabled.

## 4. Per machine: what to do

1. Install or update DevSweep from the release candidate (installer or portable zip).
2. Run the kit (section 1) with and without `-RunCleanTest`. Attach or note `report.md`. Any FAIL must be
   explained (a bug to fix, or a kit limitation to document) before sign-off.
3. In the app, open Clean up, Projects and Health once; compare three sizes with Explorer's "Size on disk".
4. Save the golden scan: `devsweep scan --json > golden-<machine>.json`. When findings change later, review the diff.
5. Do the manual checks in section 5 that apply, and fill in the table in section 6.

## 5. Manual checks CI cannot do

| # | Check | How | Expected |
|---|---|---|---|
| 5.1 | Real toast and click | Settings > Alerts: turn on, trigger a test alert (or wait for a scheduled scan that finds something) | A Windows toast appears; clicking it opens DevSweep on the right place; it fires once, then reminders (not every scan) |
| 5.2 | Start at sign-in | Turn on "Open at login", sign out and in | A `DevSweep` value exists in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; the tray icon appears; turning the setting off removes the value |
| 5.3 | Wake from sleep catch-up | Schedule a scan for a time during sleep; sleep the PC past it; wake | The missed scan runs shortly after wake (about 30 s), once |
| 5.4 | Tray right-click | Right-click the tray icon (also in the overflow flyout) | The menu opens, items work (Open, Scan now, Settings, Quit); Quit really exits |
| 5.5 | High contrast | Settings > Accessibility > Contrast themes (Aquatic, Desert...) | Every screen readable; focus visible; nothing invisible; sizes and badges still legible |
| 5.6 | DPI 125%, 150%, 200% | Settings > Display > Scale; sign out and in each time | No clipped text, no overlapping controls, logo crisp, review sheet fits |
| 5.7 | Small display 1366x768 | Set resolution 1366x768 (or a VM), at 100% and 125% | Window fits (or scrolls), navigation and buttons reachable, nothing cut off |
| 5.8 | Dark and light, theme button | Change Windows theme both ways; use the title-bar sun/moon button | App follows Windows; the button cycles and persists after restart; title bar and content both switch |
| 5.9 | Logo everywhere | Look at taskbar, title bar, tray, Start menu entry, Apps & features list, installer window, desktop shortcut | The DevSweep logo each time, never the generic icon (check after an upgrade too: the icon cache can be stale until sign-out) |
| 5.10 | Installer upgrade 0.1.0 to 0.1.1 | Install 0.1.0 (from its release), use it (History, settings), then run the 0.1.1 setup | In-place upgrade (same folder), app stops and restarts as needed, History/settings/quarantine kept, shortcuts and icon updated, no admin prompt |
| 5.11 | Uninstall keeps data | Apps & features > Uninstall DevSweep (answer the data question "keep") | Program folder, shortcuts and entry gone; `%LOCALAPPDATA%\DevSweep` (History, settings, quarantine) still there; "remove my data" removes it |
| 5.12 | SmartScreen text | Run the downloaded (not unblocked) setup | "Windows protected your PC": More info > Run anyway works, and the README's wording matches what appears on this Windows build |
| 5.13 | Standard user | Do 5.10 and 5.11 and a clean-up as a non-admin account | No UAC prompt anywhere; admin items appear as commands to run, not as actions |
| 5.14 | Review before removal | Pick a leftover and an old version in Clean up; open Review | Exact action and what will happen are shown; "can't be undone" appears where the Recycle Bin would not keep it; History shows the item and Restore works |
| 5.15 | Cloud-only files | Mark a OneDrive folder online-only; scan; clean something next to it | The folder's size shows as cloud-only/0; no file is downloaded (OneDrive icon stays a cloud); cleaning a folder holding cloud files asks first |
| 5.16 | A running tool | Open a project in an IDE, then try to clean its build folder | "Close X first" names the program (administrator and, where Windows allows, standard user) |
| 5.17 | Drive without a Recycle Bin | Try to clean something on a USB stick or network drive | DevSweep says it cannot be undone (or uses a quarantine only where it can) before the user confirms |

## 6. Results table (copy this; one per machine)

Machine: ______________ Windows build: ________ Profile(s): a / b / c  Date: ________ Tester: ________
DevSweep version: ________ Kit report attached: yes / no

| Item | Result (pass / fail / n/a) | Notes |
|---|---|---|
| Kit read-only run: overall | | |
| Kit: sizes within 1% / 10 MB | | |
| Kit: zero false leftovers | | |
| Kit: inventory misses explained | | |
| Kit: runtimes and PATH | | |
| Kit: Store alias not an install | | |
| Kit: ports | | |
| Kit `-RunCleanTest`: destination truth | | |
| Kit `-RunCleanTest`: junction decoy untouched | | |
| Kit `-RunCleanTest`: restore byte-identical | | |
| Kit `-RunCleanTest`: freed space | | |
| App sizes vs Explorer (3 folders) | | |
| 5.1 Toast and click | | |
| 5.2 Start at sign-in | | |
| 5.3 Wake catch-up | | |
| 5.4 Tray right-click | | |
| 5.5 High contrast | | |
| 5.6 DPI 125 / 150 / 200% | | |
| 5.7 1366x768 | | |
| 5.8 Dark / light / theme button | | |
| 5.9 Logo (taskbar, title, tray, Start, Apps list) | | |
| 5.10 Upgrade 0.1.0 to 0.1.1 | | |
| 5.11 Uninstall keeps data | | |
| 5.12 SmartScreen text | | |
| 5.13 Standard user | | |
| 5.14 Review and History restore | | |
| 5.15 Cloud-only files | | |
| 5.16 Running tool blocker | | |
| 5.17 No-Recycle-Bin drive | | |

Sign-off rule: every threshold in section 2 met on at least the machines available (any FAIL explained), zero false
leftovers anywhere, and every row above either pass or listed in section 7 with an owner.

## 7. Known gaps accepted for v0.1

These are what earlier milestone documents say was never seen on a real machine. They are accepted for v0.1
as long as DevSweep offers nothing when unsure; each one is a thing to look for during sign-off.

- **Rule packs on fixtures only** (m3a): WSL, Docker Desktop, Android Studio, JetBrains IDEs and browsers were
  never present on the CI runner; their rules are proved on fixtures only.
- **Edge on-device model folders** (m3a): names are the best known ones, not seen on a machine that has them.
- **Visual Studio workload layout** (m3a): install records are read conservatively; a layout change makes
  DevSweep offer nothing, never too much.
- **WSL / Docker vhdx** (m3a): the size shown is 0 ("unknown until it is compacted"); a running distro gives
  "at most"; compaction itself is a guided command, never run by DevSweep.
- **Version managers on fixtures only** (m3a, m3b): nvm-windows, fnm, Volta, pyenv-win, Scoop and Microsoft Store
  Python are proved with fixture machines; the CI runner covers tool caches, conda, MSI installs, Chocolatey,
  rustup, PEP 514 and `py`.
- **Windows Firewall verdicts** (m3b): judged from the firewall configuration in the registry, not by asking
  `INetFwPolicy2`; unusual rule sets (third-party firewalls, complex Group Policy) may read differently.
- **Shell start-up files** (m3b): read for known patterns only, never executed; what a profile really does to PATH
  can differ. PATH from the registry expands `REG_EXPAND_SZ` with DevSweep's own environment.
- **Standard-user limits**: Restart Manager is denied, so a file in use is found but the program is not named
  ("Windows did not say which program it is"); the Windows Task Scheduler folder cannot be listed, so no
  scheduled task is judged as orphaned; the installer test cannot create the window in the scheduled-task session.
- **Projects** (m3c): Git LFS repositories are "Unknown" (never removable); files changed only in line endings
  still block removal; commits only reachable from the reflog are not counted; a project root that is itself a
  junction is not searched; projects inside OneDrive cloud folders are not looked into.
- **Not tested on CI** (m5): a real toast and its click, a sign-in start from the Run entry, wake from sleep,
  a human right-click on the tray, DST gaps in the schedule. These are section 5.1 to 5.4.
- **Not covered** (m6): code signing (SmartScreen will warn), winget, MSIX, ARM64, Windows 10; interactive installer
  wizard pages and the uninstall question are only read, CI runs silent.
- **Not in the app yet** (m4): Security "Move to Recycle Bin" and Ports "Stop" buttons, per-profile part pickers,
  and Projects download/remove/publish pages (the CLI has them).
- **Known size accounting limits**: a hard-linked file inside a finding that is also inside another finding's folder
  is subtracted twice from the scan total (the per-finding sizes are right); alternate data streams are counted by
  DevSweep with `--exact` but not by the kit (normally 0 bytes on disk).
- **Kit limits**: the kit cannot judge a size DevSweep took from a tool (for example `docker system df`); it counts a
  program name as installed when a Program Files folder with its name exists (reported as WARN); free-space
  deltas on a busy drive are noisy (median of several runs, WARN when they disagree); on a machine where Windows
  has no Recycle Bin limit configured, DevSweep (correctly) quarantines instead of recycling, so the real
  Recycle Bin path is only exercised where a limit is set (CI sets one for its disposable runner).

## 8. What CI proves (job `m7-verify`)

CI_NUMBERS_PLACEHOLDER

## 9. Files

New: `scripts/Verify-DevSweep.ps1`, `scripts/run-verify-as-standard-user.ps1`,
`src/DevSweep.Cli/Commands/Verify/VerifyCleanCommand.cs` (hidden `devsweep __verify-clean`, test-only: refuses
anything not inside a root that holds the kit's marker file under `%TEMP%`), this page.
Changed (additive): `src/DevSweep.Cli/Program.cs` (one line), `scripts/release.ps1` (copies the kit and the CLI into
`scripts\` of the app folder, so the portable zip and the installed folder carry them), `installer/README-FIRST.txt`,
`.github/workflows/windows.yml` (block `M7`), README (one docs link).
