# DevSweep for Windows: plan

Status: **approved** (decisions in section 7). M0 to M4 merged into `windows` (M3a/b/c/M4 CI green; Health and Projects off by default pending review); M0 to M6 and the 0.1.1 polish (logo, navigation, theme button; Health and Projects now on by default) all merged; M7 kit and checklist merged (CI: 0 FAIL); the human sign-off on real machines (docs/m7-accuracy-signoff.md) remains before a first release. This branch (`windows`)
holds the plan; code lands here milestone by milestone.

## 1. Goal

The same product as DevSweep for Mac: a disk cleaner for developer machines
that explains every item before it goes, with the same four places (Home,
Clean up, Projects, Health), History/undo and Settings.

The overriding requirement is **accuracy**. On macOS there is one way apps
are installed, one Library folder, one Trash and one shell. Windows has none of
that: apps install a dozen ways, data lives in at least five roots, the
filesystem has features (hard links, junctions, compression, cloud
placeholders) that make naive sizes wrong by gigabytes, and the Recycle Bin
can silently delete instead of recycling. Every number DevSweep shows and every
"safe to remove" it claims must survive those cases.

Accuracy here means four concrete things, each with a test (section 6):

1. **Sizes are true**: the space freed after cleaning matches what was shown,
   within 1% or 10 MB.
2. **Detection is true**: no false "leftover" (app is actually installed), no
   missed install, no wrong "which one you use".
3. **Removal is true**: what we say goes to the Recycle Bin really does, and
   nothing outside the item is touched (no following junctions).
4. **We say when we don't know**: partial sizes, locked files, cloud-only
   files and missing permissions are shown as such, never guessed.

## 2. Why Windows is hard (the accuracy traps)

| Area | Trap | What we do |
|---|---|---|
| Sizes | Hard links (pnpm store, Scoop, WinSxS, Git for Windows) counted twice | Dedupe by file ID (volume serial + 128-bit file ID); report "shared with N other places" |
| Sizes | NTFS compression, sparse files | Use allocation size (`GetCompressedFileSizeW` / `FILE_STANDARD_INFO.AllocationSize`), not logical length |
| Sizes | OneDrive / cloud placeholders (Files On-Demand) | Detect `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `OFFLINE`; count as 0 on disk; **never open them** (opening downloads the file) |
| Sizes | Dev Drive / ReFS block cloning | Mark size as "may be shared"; measure freed space at volume level after cleaning |
| Walking | Junctions and symlinks (nvm-windows, Scoop `current`, `node_modules/.bin`, AppData redirects) | Never traverse reparse points; never delete through them; delete the link, not the target |
| Walking | Paths over 260 chars (deep `node_modules`) | Long-path APIs everywhere (`\\?\` prefix, manifest `longPathAware`) |
| Walking | Defender scanning every file we open | Metadata-only enumeration (`FindFirstFileEx` with `FIND_FIRST_EX_LARGE_FETCH`, or MFT/USN read when elevated); never read file contents for sizing |
| Locations | Data split across `%LOCALAPPDATA%`, `%APPDATA%`, `%ProgramData%`, `%USERPROFILE%\.tool`, `%TEMP%` | Rules use named roots, resolved through the Known Folders API, never hard-coded `C:\Users\...` |
| Locations | Desktop/Documents redirected to OneDrive or another drive | Resolve via `SHGetKnownFolderPath`; treat redirected folders as protected too |
| Locations | Tools moved by env vars or config (`NPM_CONFIG_CACHE`, `.npmrc`, `pip.ini`, `GRADLE_USER_HOME`, `CARGO_HOME`, `NUGET_PACKAGES`, `ANDROID_SDK_ROOT`...) | Each rule's location resolver asks the tool's own config first, then env, then default |
| Installs | Apps install via MSI, EXE installers, MSIX/Store, winget, Chocolatey, Scoop, portable zips, per-user and per-machine | One "installed apps" inventory merged from all sources (section 4.3); leftovers only flagged when **every** source says gone |
| Installs | 32- vs 64-bit registry views (`WOW6432Node`), HKCU vs HKLM | Read all four Uninstall views |
| Runtimes | Store "python.exe" app-execution alias that just opens the Store | Detect aliases in `%LOCALAPPDATA%\Microsoft\WindowsApps`; never report them as a Python install |
| Runtimes | PATH is not a shell's PATH: it is HKLM + HKCU registry, expanded, plus PATHEXT | Build PATH from the registry the way Explorer does, and separately from PowerShell/cmd/Git Bash profiles where they change it |
| Removal | Recycle Bin **permanently deletes** items bigger than its quota, and doesn't exist on some drives | Check quota and drive type first; if an item can't be recycled, use DevSweep's own quarantine folder on the same volume, or say "can't be undone" before the user confirms |
| Removal | Files in use (IDE running, `node.exe` holding a file) | Restart Manager API names the process holding a file; that becomes the blocker message |
| Removal | Read-only / system attributes, ACLs | Never take ownership or change ACLs; report "access denied" and offer the command |
| Admin | UAC: we must never run elevated by default | App runs as standard user; admin items become commands shown in an elevated PowerShell window the user confirms (same model as Terminal on Mac) |
| Big wins | WSL2 and Docker Desktop `.vhdx` disks grow and never shrink | Detect `ext4.vhdx` / `docker_data.vhdx`, show reclaimable estimate, offer guided compaction (`wsl --shutdown` + `Optimize-VHD` / `diskpart compact`), never auto-run |

## 3. Tech stack

### Recommendation: C# / .NET 10 (LTS) + WinUI 3 (Windows App SDK)

| Option | For | Against |
|---|---|---|
| **C# / .NET 10 + WinUI 3** (recommended) | First-class Win32, COM and WinRT access (Known Folders, IFileOperation, Restart Manager, MSIX package APIs, registry, WMI, toast notifications, tray). Mature async + parallel IO. Fluent UI that matches Windows 11. Native AOT for the CLI. Strong test tooling. | Second codebase; WinUI 3 has rough edges (we keep the UI thin). |
| Swift on Windows (reuse `Core/`) | Shares the existing engine | Foundation on Windows lacks or mis-implements exactly what we need (allocated size, trash, file IDs, reparse points); no SwiftUI, so UI is a separate stack anyway; small ecosystem. Accuracy would suffer. |
| Rust + Tauri (`windows-rs`) | Fast, safe, good Win32 bindings | Web UI isn't native; COM/WinRT interop more work; slower to build the UI breadth DevSweep has. |
| Electron | Fast UI iteration | Large, slow, and Win32 accuracy work still needs native modules. |

Accuracy decides it: almost every trap in section 2 is a Win32 API call, and
.NET reaches them directly (P/Invoke via `CsWin32` source generator) with no
glue layer to get wrong.

### What we share with the Mac app

- **Rule format.** Same YAML schema (`id`, `title`, `category`, `risk`,
  `detector`, `actions`, `explain`) so rules read the same on both. Windows
  rule packs are separate files; detector kinds are per platform.
- **Product language.** Risk labels, explanation fields, review-before-change,
  History/undo, "nothing is cleaned automatically".
- **endoflife.date** data and the update check (GitHub releases).

### Stack details

| Piece | Choice |
|---|---|
| Engine | `DevSweep.Core` class library (.NET 10), no UI dependencies |
| Win32 interop | `Microsoft.Windows.CsWin32` (generated, typed P/Invoke) |
| CLI | `devsweep.exe` (`System.CommandLine`, Native AOT): `scan`, `scan --json --explain`, `runtimes`, `security`, `ports`, `projects`, `history` |
| UI | WinUI 3, MVVM (`CommunityToolkit.Mvvm`), light/dark following Windows |
| Tray + notifications | `H.NotifyIcon.WinUI`, App SDK `AppNotificationManager` |
| Scheduled scans | Runs while the tray app is open (same as Mac); optional Task Scheduler entry for "scan at login" |
| YAML | `YamlDotNet` |
| Storage | JSON files in `%LOCALAPPDATA%\DevSweep` (history, snapshots, settings); SQLite only if history needs querying |
| GitHub | `gh` CLI tokens per account (same as Mac), `Octokit` or plain `HttpClient` |
| Tests | xUnit + FluentAssertions; fixture builder for NTFS edge cases; Windows CI runners (x64) |
| Packaging | **Unsigned.** Unpackaged, self-contained WinUI 3 app (MSIX needs a signature, so no MSIX). Per-user installer (no admin, installs to `%LOCALAPPDATA%\Programs\DevSweep`) + portable `.zip`; winget manifest later. README explains the SmartScreen "More info, Run anyway" step |
| Optimisation | Native AOT for `devsweep.exe`; trimmed, ReadyToRun app; no reflection-based serialisation (System.Text.Json source generators) so AOT stays possible |
| Targets | **Windows 11 only** (build 22621 / 22H2 and later). **x64 only**; ARM64 is a later add, kept possible by never shipping x64-only native code |

## 4. Architecture

```
windows/
  DevSweep.sln
  src/
    DevSweep.Core/          engine: no UI
      Platform/             Win32: file walking, sizes, file IDs, reparse points,
                            known folders, recycle bin, restart manager, registry
      Inventory/            installed apps from every source (4.3)
      Rules/                YAML loader + bundled Windows packs
      Detectors/            one class per detector kind
      Safety/               last-line guard (4.4)
      Cleaner/              executes a reviewed plan, writes History
      Health/               runtimes, EOL, security, ports
      Projects/             git, gh accounts, safe removal
    DevSweep.Cli/           devsweep.exe
    DevSweep.App/           WinUI 3 app (thin: view models over Core)
  tests/
    DevSweep.Core.Tests/    unit tests
    DevSweep.Accuracy/      fixture-based accuracy suite (section 6)
  rules/                    Windows rule packs (YAML)
```

### 4.1 Scan pipeline

Same shape as the Mac engine: load rules, resolve locations, run detectors in
parallel with per-check time limits, produce `Finding`s with facts for
explanation templates, and a scan report with per-check timings. New on
Windows: every `Finding` carries a **size confidence** (`exact`, `partial`,
`sharedLinks`, `cloudPlaceholders`, `accessDenied`) and the UI shows it.

### 4.2 Filesystem layer (the accuracy core)

One walker used by everything:

- Enumerates each folder from its own handle with
  `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` under `\\?\` paths
  (attributes, reparse tag, sizes and 128-bit file ID without opening files),
  falling back to `FindFirstFileExW` where the file system lacks it (FAT, exFAT).
  NTFS has no link count in directory entries, so each file still needs one
  metadata query: by name (`NtQueryInformationByName`, no handle) or, to count
  alternate data streams (the default), one attribute-only handle (M1).
- Stops at every reparse point (junction, symlink, mount point, WSL, AppX alias)
  and records it; cloud-provider folders (OneDrive) are entered reading
  attributes only, never content (M1).
- Dedupes hard links by file ID within a scan, so a pnpm store or Scoop app
  isn't counted twice across findings.
- Time-limited, cancellable, returns partial counts with `complete=false`.
- Optional fast path when elevated: read the MFT/USN journal for whole-volume
  sizing (like WizTree). Off by default; v2.

### 4.3 Installed-apps inventory

Merged from: Uninstall registry keys (HKLM/HKCU, 64/32-bit), MSIX packages
(`PackageManager.FindPackagesForUser`), winget (`winget list` export), Scoop
(`~/scoop/apps`), Chocolatey (`%ProgramData%\chocolatey\lib`), App Paths,
Start Menu shortcuts, and known portable locations. Each app gets IDs
(publisher, product code, package family name, exe path). Leftover detection
(`%APPDATA%`/`%LOCALAPPDATA%`/`%ProgramData%` folders whose app is gone) only
fires when no source matches, and the explanation lists every source checked.

### 4.4 Safety (hard rules, whatever a rule says)

- Only inside the user profile, or the user's own dev folders, or DevSweep's
  quarantine. Anything else becomes a shown command.
- Protected: profile root, every Known Folder (Desktop, Documents, Downloads,
  Pictures... wherever they're redirected), `AppData`, `AppData\Local`,
  `AppData\Roaming`, `AppData\LocalLow`, `.ssh`, `.gnupg`, `.aws`, `.kube`,
  `.docker`, `.gradle`, `.android`, `.nuget`, `OneDrive*`, `scoop` root,
  `%LOCALAPPDATA%\Microsoft`, `%LOCALAPPDATA%\Packages` (root).
- Never through a reparse point; never anything with a cloud placeholder
  inside unless the user confirms that cloud copies are affected.
- Case-insensitive, normalized, 8.3-short-name-resolved path comparison.
- Before Recycle: confirm the volume has a Recycle Bin and the item fits its
  quota; otherwise quarantine or warn "permanent".

### 4.5 Windows rule packs (first set)

| Area | Examples |
|---|---|
| Package caches | npm, pnpm store (hard-link aware), Yarn, pip, uv, NuGet (`.nuget\packages`, http-cache), Gradle, Maven, Cargo, Go, Composer, Chocolatey/Scoop caches, winget downloads |
| .NET / Visual Studio | Old .NET SDKs and workloads (`dotnet --list-sdks`), VS Installer package cache (command only), `obj`/`bin` in idle projects, ReSharper/Rider caches, VS `ComponentModelCache` |
| IDEs | JetBrains old-version folders, Android Studio old configs, VS Code / Cursor stale extensions |
| Android | System images no AVD uses, old build-tools/platforms/NDKs, emulator snapshots |
| Toolchains | Old JDKs, old Node via nvm-windows/fnm/Volta, Windows SDK versions no project targets (command only) |
| WSL & Docker | Bloated `ext4.vhdx` / `docker_data.vhdx` (guided compact), Docker build cache/images via `docker system df` and prune |
| Browsers & AI | Chrome/Edge/Brave caches per profile, Edge/Chrome on-device models, Hugging Face, Ollama, PyTorch, Whisper |
| Projects | `node_modules`, `.venv`, `target`, `bin/obj`, `.gradle` in idle projects |
| Large files | Old installers (`.msi`, `.exe`, `.iso`, `.zip`) in Downloads; big files unopened for months (last-access is often disabled on NTFS, so we say so and fall back to modified date) |
| Windows | `%TEMP%` older than 7 days, crash dumps (`CrashDumps`, `.dmp`), Windows Error Reporting; Windows Update cleanup / `DISM` / Delivery Optimization as admin commands only |
| Leftovers | AppData folders and scheduled tasks/services of removed apps |

## 5. Milestones

Each milestone is run by its own subagent (in its own worktree), against a
written brief and an acceptance checklist. Milestones in the same row can run in
parallel; each ends with a PR into `windows`.

| # | Milestone | Depends on | Delivers | Accepted when |
|---|---|---|---|---|
| **M0** (done, CI green; see README) | Foundation & stack spike | none | Solution skeleton, CI on `windows-latest` (x64), CsWin32 set up, a spike proving: allocation size, file ID dedupe, reparse-point stop, OneDrive placeholder detection, recycle with quota check | Spike numbers match `fsutil`/Explorer on fixture folders; CI green |
| **M1** (done, CI green; see README) | Filesystem layer + Safety | M0 | Walker (4.2), Safety (4.4), Recycle/quarantine, Restart Manager blockers, History store | Accuracy suite fixtures (6) all pass; fuzzed path tests for Safety |
| **M2** (done, CI green; see README) | Rules engine + inventory | M1 | YAML loader (shared schema), location resolvers (Known Folders, env, tool config), installed-apps inventory (4.3), `paths`/`versionedSiblings`/`staleProjectArtifacts`/`orphanedAppData` detectors, CLI `scan` | Inventory matches on 3 reference machines; no false leftovers |
| **M3a** (done, CI green; see docs/m3a-rules.md; real-machine checks pending reference VMs) | Windows rule packs | M2 | Section 4.5 packs, WSL/Docker vhdx detector, .NET SDK detector, Android detector, explanations | Each rule has a fixture test + a real-machine check |
| **M3b** (done, CI green; see docs/m3b-health.md; off by default until reviewed) | Health | M2 | Runtimes (nvm-windows, fnm, Volta, pyenv-win, `py` launcher, conda, uv, SDKMAN-alt, rustup, Scoop/winget/choco installs, Store aliases), registry PATH, EOL, project requirements, Security scan, Ports (`GetExtendedTcpTable` + firewall reachability) | Runtime inventory correct on reference machines; Store alias never reported as install |
| **M3c** (done, CI green; see docs/m3c-projects.md; off by default until reviewed) | Projects | M2 | git/gh multi-account, safe removal checks, download strategies, per-account identity, CRLF/case-insensitivity handling | Same checks as Mac test suite, ported |
| **M4** (done, CI green; Native AOT; see docs/m4-app.md) | WinUI 3 app | M2 (stubs), then M3* | Home, Clean up (+ⓘ inspector), Review sheet, Projects tabs, Health tabs, History, Settings, Welcome, light/dark | Every Mac screen has a Windows equivalent; review shows exact commands |
| **M5** (done, CI green; see docs/m5-tray.md; real toast/login/sleep untested on CI) | Tray, schedule, alerts, updates | M4 | Tray icon + menu, open at login, scheduled read-only scans, toast alerts, update check | Alerts fire once with reminders, like Mac |
| **M6** (done, CI green; see docs/m6-packaging.md; release workflow untested until merged, drafts only) | Packaging & release | M4 | Unsigned per-user installer + portable zip, `release.ps1`, GitHub release, README section | Clean install and uninstall on fresh Windows 11 22H2 and 24H2/25H2 x64 VMs, no admin prompt |
| **M7** (kit and checklist done, CI green; real-machine sign-off pending; see docs/m7-accuracy-signoff.md) | Accuracy sign-off | all | Full accuracy matrix run (section 6), freed-space reconciliation report | All thresholds met; known gaps documented in-app |

Order: M0 → M1 → M2 → (M3a ∥ M3b ∥ M3c ∥ M4) → M5 → M6 → M7.

## 6. Accuracy validation

- **Fixture builder** (`tests/DevSweep.Accuracy`): creates on a real NTFS
  volume (and a Dev Drive/ReFS one in CI where available): hard-link farms,
  junction loops, symlinks to outside the profile, compressed and sparse files,
  paths > 260 chars, read-only and locked files, fake OneDrive placeholders
  (via the Cloud Filter API), a Recycle-Bin-quota-exceeding file. Every walker
  and Safety test runs against it.
- **Reference comparison**: sizes compared to `fsutil`, Sysinternals `du -l`,
  and the volume free-space delta after cleaning.
- **Freed-space reconciliation**: after every clean (in tests and in the app's
  scan report) compare "shown" vs "actually freed" from the volume; differences
  over threshold are logged as accuracy bugs.
- **Machine matrix**: CI (x64) + three reference VMs set up the way
  developers really use Windows: (a) winget + VS + .NET + WSL; (b) Scoop +
  Node/Python + Docker Desktop; (c) Chocolatey + Android Studio + JetBrains +
  OneDrive-redirected Documents.
- **Golden scans**: `devsweep scan --json` on each VM is snapshotted; any change
  in findings needs a reviewed diff.
- **No-false-positive gate**: a rule that ever flags an installed app's data as
  leftover is a release blocker.

## 7. Decisions (agreed)

1. **Stack**: the most optimised and maintainable option: C# / .NET 10 (LTS)
   + WinUI 3, CsWin32 for Win32, Native AOT CLI.
2. **OS**: Windows 11 and later only.
3. **Architecture**: x64 first. ARM64 later, not blocked by design.
4. **Signing**: unsigned. So: unpackaged app, per-user installer + zip, no MSIX.
5. **Repo**: Windows code lives in `windows/` in this repository, on the
   `windows` branch.
6. **Scope**: as flexible as possible. Each place (Clean up, Projects, Health
   and its tabs) is a separate module in Core and in the app, registered at
   startup and switchable by a feature flag. Any module that passes its accuracy
   gate can ship on its own; one that doesn't stays hidden, so v1's scope is
   decided by quality, not fixed in advance. Clean up + History is the minimum.

## 8. How milestones are run

- One subagent per milestone, on its own branch `windows-mN-<name>` cut from
  `windows`, with a brief and the acceptance checklist from section 5.
- No .NET SDK in the cloud session, so CI (`.github/workflows/windows.yml` on
  `windows-latest`) is the build and test authority: a milestone is done only
  when CI is green on its branch.
- Finished milestones are merged into `windows` after review.
