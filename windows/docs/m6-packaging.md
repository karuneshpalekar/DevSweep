# M6: Packaging and release

Unsigned, unpackaged (no MSIX), per user, no administrator, Windows 11 x64.

| Piece | Where |
|---|---|
| Version (single source) | `windows/version.props` (`DevSweepVersion`, imported by `Directory.Build.props`) |
| Installer script (Inno Setup 6) | `windows/installer/DevSweep.iss`, `after-install.txt` (SmartScreen help on the finish page) |
| Zip texts | `windows/installer/README-FIRST.txt` (portable), `CLI-README.txt` |
| Release script | `windows/scripts/release.ps1` |
| Installer tests | `windows/scripts/test-installer.ps1`, `test-installer-as-standard-user.ps1` |
| CI | `installer` job in `.github/workflows/windows.yml`; manual `.github/workflows/windows-release.yml` |
| Notes | `windows/docs/release-notes/<version>.md` |

## Why Inno Setup

Inno Setup 6 is preinstalled on GitHub's windows-latest image (and `choco install innosetup` /
`winget install JRSoftware.InnoSetup` otherwise), free, and does exactly this job from a single
declarative script: per-user install with no elevation (`PrivilegesRequired=lowest`, no override), HKCU
Apps & features entry, shortcuts, upgrade-in-place keyed on a fixed `AppId`, a real uninstaller and
`/VERYSILENT`. A pure-script installer (PowerShell) would have to re-implement the uninstaller, the
uninstall registry entry and upgrade handling; WiX needs a heavier toolchain and MSI per-user quirks;
MSIX is out of scope. The small Pascal `[Code]` block (stop DevSweep, clean old files, opt-in data
removal) is the only logic.

## Installer behaviour

- Installs to `%LOCALAPPDATA%\Programs\DevSweep`, Start Menu shortcut, optional desktop shortcut
  (task `desktopicon`, unticked), HKCU uninstall entry, `.pdb` files excluded. Requires 10.0.22621 and x64.
- Silent install: `DevSweep-<v>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/TASKS=desktopicon] [/LOG=file]`.
- Upgrade: run a newer setup (same `AppId`). Before copying, Setup closes DevSweep (`taskkill` asking
  it to close, ten seconds, then forced) and, if `DevSweep.exe` is already in the folder, empties the
  folder except the uninstaller files so files dropped in a newer version do not linger. It does not
  touch `%LOCALAPPDATA%\DevSweep`. It stops any process named `DevSweep.exe` of the current user
  (a portable copy too). It works the same with or without a tray: the tray, being the same exe, is
  stopped by name.
- Uninstall (Settings, Apps, or `"%LOCALAPPDATA%\Programs\DevSweep\unins000.exe" /VERYSILENT`): stops
  DevSweep, removes program files, shortcuts and the entry. **User data is kept**:
  `%LOCALAPPDATA%\DevSweep` (History, settings, quarantine). Interactively, a final question (default
  No) offers to delete it and says the quarantine may hold restorable items. Silent uninstalls never
  ask; add `/REMOVEUSERDATA` to delete the folder. Deletion does not follow junctions or symbolic links
  inside the folder (tested). Quarantine folders on other drives (`<drive>:\DevSweep Quarantine\<SID>`)
  are never touched by the uninstaller; the dialog says so.
- Finish page (`after-install.txt`): the SmartScreen "More info, Run anyway" steps, no-admin note, where
  data lives.

## Versioning

`version.props` holds `DevSweepVersion` (0.1.0). `Directory.Build.props` imports it and sets `Version`,
so file, product and informational version of `DevSweep.exe` and `devsweep.exe` carry it; Settings, About
already shows `AssemblyInformationalVersion` ("DevSweep for Windows 0.1.0"), so no app change was needed
(the hard-coded `<Version>` in `DevSweep.App.csproj` was removed). `release.ps1 -Version x.y.z` overrides
for one run (MSBuild `-p:DevSweepVersion`) and the script fails if the built exes are not stamped with it.
The same value names the files: `DevSweep-<v>-win-x64-setup.exe`, `DevSweep-<v>-win-x64-portable.zip`
(top folder `DevSweep-<v>-portable` with `README-FIRST.txt`), `devsweep-cli-<v>-win-x64.zip`
(`devsweep.exe` and `README.txt`), plus `SHA256SUMS.txt` (`<hash> *<file>`, check with `sha256sum -c`).

## `scripts/release.ps1`

```powershell
cd windows
./scripts/release.ps1                      # build, package, checksums into artifacts/release/<v>; nothing leaves the machine
./scripts/release.ps1 -RunTests            # also unit, accuracy and app tests
./scripts/release.ps1 -SkipBuild           # repackage the last build
./scripts/release.ps1 -SkipBuild -Publish  # tag windows-v<v>, push the tag, create a DRAFT release with gh
./scripts/release.ps1 -SkipBuild -Publish -Draft:$false   # explicit: publish immediately
```

`-Publish` requires `gh` signed in, a clean tree, HEAD on a pushed branch, no existing tag, and
`docs/release-notes/<v>.md` (used as the release notes; versions with a `-suffix` are marked pre-release).

## CI

`installer` job (every push, parallel to the others): `release.ps1` (publishes both AOT binaries, builds the
installer), then `test-installer.ps1` as the runner and `test-installer-as-standard-user.ps1` as a fresh
`Users`-only account (scheduled task, RunLevel Limited, so there is no elevation to prompt for). Each cycle:
install silently, assert location under the profile, HKCU entry (and nothing in HKLM), Start Menu shortcut,
version stamp; `DevSweep.exe --smoke` from the installed folder (exit 0); create data including a junction
out of it; start DevSweep, drop a stale file, install again over it with the desktop task (stale files
gone, DevSweep stopped, shortcut added, data kept); uninstall (program files, shortcuts and entry gone,
data kept); reinstall and uninstall with `/REMOVEUSERDATA` (data gone, the junction target survives). It
also unpacks the portable zip and runs `--smoke`, and runs `devsweep.exe rules validate` from the CLI zip.
Uploaded as `release-candidate` and `installer-test-logs`.

`windows-release.yml` is `workflow_dispatch` only (inputs `version`, `publish` default false, `draft`
default true, `run_tests` default false). Job `build` packages, runs the admin installer test and uploads
`devsweep-windows-<v>`; job `release` runs only when `publish` is ticked, has `contents: write`, and
runs `gh release create windows-v<v> --target <sha>` (a draft has no tag until it is published).

## Not covered

- Code signing (SmartScreen will warn), winget, MSIX, auto-update (M5), ARM64, Windows 10.
- Interactive wizard pages and the uninstall question are only checked by reading the script; CI runs silent.
