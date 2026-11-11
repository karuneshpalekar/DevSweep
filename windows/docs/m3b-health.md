# M3b: Health (tools and versions, security, ports, alerts)

The Mac app's Health place, ported to Windows as the `health` module in
`DevSweep.Core/Health`, with three CLI commands:

```powershell
$env:DEVSWEEP_FEATURES = '-module:health'      # Health is on by default since 0.1.1; this turns it off
devsweep runtimes [--json] [--offline] [--project-root DIR]... [--script-step ID | --open-step ID]
devsweep security [--json] [--project-root DIR]...
devsweep ports    [--json] [--all]
devsweep ports --stop PID [--yes]               # asked first; ended only if still running and you confirm
```

Everything is read-only except `ports --stop` (your own processes only) and the fix
window, which runs nothing until you press Enter.

## Layout

```
src/DevSweep.Core/Health/
  HealthModule.cs              module:health, HealthFeatures (tab flags), HealthChecks (entry point)
  Common/                      VersionText, HealthFiles (metadata + first KB, no recall), PowerShellText, ProcessPaths
  Runtimes/
    RuntimeModels.cs           Installation, InstallSource, RuntimeReport, RuntimeStep, VersionsResult (view-model friendly)
    PathSearch/                SearchPath (Explorer PATH), ShimResolver, ShellProfiles
    Platform/                  AppExecLink (Store aliases), FileVersionReader (PE), VersionCommand (trusted runs)
    Discovery/                 ProbeContext, ProbeKit, one probe per tool
    EndOfLife.cs               endoflife.date client and cache
    ProjectRequirements.cs     what projects ask for
    RuntimeReporter.cs         support status, issues, fixes per tool
    FixCommands.cs             commands per owning tool
    TerminalScript.cs          the visible PowerShell fix window
    RuntimeHealth.cs           orchestration + the Windows report
  Security/                    SecurityScanner, SecurityModels, GitRunner
  Ports/                       SocketTables, ProcessDetails, Firewall, PortScanner, ProcessStopper (+ ConsoleInterrupt)
  Alerts/                      AlertEvaluator, AlertLedger, AlertStore, ScanSnapshotStore, HealthJsonContext
src/DevSweep.Cli/Commands/Health/   runtimes, security, ports, hidden __ctrl-c helper
tests/DevSweep.Core.Tests/Health/   unit tests (fake machines, recorded endoflife.date fixtures)
tests/DevSweep.Accuracy/Health/     real-machine accuracy tests
```

## Modules and flags

| Flag | Default | Effect |
|---|---|---|
| `module:health` | on (since 0.1.1; it was off while it had not passed its gate) | registers `HealthFeatures` and `HealthChecks` services; the CLI commands exit 3 with instructions while it is off |
| `module:health.runtimes` | on (when Health is on) | Tools and versions tab |
| `module:health.security` | on | Security tab |
| `module:health.ports` | on | Ports tab |

`engine.Services<HealthChecks>()` is empty when Health is off; `HealthChecks.Features` says which
tabs are on (`Features.Disabled` lists the tab flags that are off), and a turned-off tab's method throws
`InvalidOperationException`. M4 can bind its Health tabs directly to `VersionsResult`, `SecurityResult`,
`PortsResult` and `AlertEvent`: every type is a record with display-ready strings (`Label`, `SourceTitle`,
`Title`, `Summary`, `Describe()`), computed flags (`IsSystem`, `PatchAvailable`, `NeedsAttention`,
`Worst`, `ReachableFromNetwork`) and stable ids for selection (`RuntimeStep.Id`, `SecurityFinding.Id`,
`ListeningPort.Id`).

## Tools and versions

### Discovery (whoever installed it)

| Tool | Where | Version from |
|---|---|---|
| Node.js | nvm-windows (`NVM_HOME`), fnm (`FNM_DIR`, `%APPDATA%\fnm`), Volta, mise, asdf, Scoop `nodejs*`, CI tool cache, `Program Files\nodejs` (nodejs.org MSI, also what winget and Chocolatey install), Visual Studio's bundled node | folder names; `node.exe` version resource |
| Python | pyenv-win, uv (`%APPDATA%\uv\python`), conda base + every env (incl. `~\.conda\environments.txt`), the Python install manager (`%LOCALAPPDATA%\Python\pythoncore-*`), Scoop, CI tool cache, mise/asdf, python.org per-user (`%LOCALAPPDATA%\Programs\Python`) and per-machine, every PEP 514 registration (HKCU, HKLM 64/32), `py -0p`, Microsoft Store packages, Visual Studio's shared Python | `python.exe` version resource (product version; the file version encodes the release level, so it is only used when it is a plain patch), `include\patchlevel.h`, `conda-meta\python-*.json`, the registry, folder names |
| Java | Program Files vendor folders (Eclipse Adoptium, Microsoft, Oracle `Java`, Zulu, Amazon Corretto, BellSoft, Semeru, ...), JavaSoft and vendor registry keys, `~\.jdks`, Scoop, CI tool cache, mise, `JAVA_HOME`, JetBrains Runtime and Android Studio `jbr` | the JDK's `release` file (`JAVA_VERSION`, `IMPLEMENTOR`); support dates per vendor product |
| PostgreSQL | EDB installer registry (`SOFTWARE\PostgreSQL\Installations`: base and data folders), Program Files, `PGROOT`, Scoop | `postgres.exe` version resource, registry |
| Go | `Program Files\Go`, `GOROOT`, `~\sdk\go1.*`, Scoop, CI tool cache, mise/asdf | `VERSION` file |
| Ruby | RubyInstaller (`C:\Ruby*`, `Software\RubyInstaller\MRI`), Scoop, CI tool cache, mise/asdf | `ruby.exe` version resource, registry |
| PHP | Scoop, Chocolatey tools folder, `C:\php*`, XAMPP, winget portable packages, Program Files, mise/asdf | `php.exe` version resource |
| Rust | rustup toolchains (`RUSTUP_HOME`), Scoop | `lib\rustlib\multirust-channel-manifest.toml`; default from rustup's `settings.toml` (the `rustc` on PATH is rustup's proxy) |
| .NET SDK | `DOTNET_ROOT`, Program Files (x64 and x86), `%LOCALAPPDATA%\Microsoft\dotnet`, `~\.dotnet`, Scoop | SDK folder names (with `dotnet.dll`); default = newest SDK of the `dotnet` on PATH |
| Flutter | `~\flutter`, `~\dev\flutter`, `C:\src\flutter`, ..., Scoop, FVM | `bin\cache\flutter.version.json`, `version` |
| Deno / Bun | official installers (`DENO_INSTALL`/`~\.deno`, `BUN_INSTALL`/`~\.bun`), Scoop, winget portable | version resource, else `--version` |

Owners: a folder inside a Scoop root, Chocolatey, a winget package root or a CI tool cache
(`RUNNER_TOOL_CACHE`) belongs to that tool. An install in a vendor's default place is refined with the
M2 inventory: a Chocolatey package of the same version makes it Chocolatey's; an Uninstall entry for the
folder gives the MSI product code and `WinGetPackageIdentifier` (so it becomes winget's). IDE-bundled
and CI tool-cache installs are reported but never offered for removal.

Programs are run only as a last resort, only by full path, only plain local `.exe` files (no link, no
cloud placeholder, no `.cmd` that would need a shell) outside Downloads/Desktop/Documents/Temp, with no
shell and a 10-second limit (`TrustedPrograms`, `TrustedVersionCommand`). `py -0p` is run from the
Windows folder or the launcher's own folder only.

### Store app-execution aliases

`%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe` is an `IO_REPARSE_TAG_APPEXECLINK` reparse point.
`AppExecLink` reads its reparse data (`FSCTL_GET_REPARSE_POINT`: package family, app user model id,
target): the App Installer `PythonRedirector` stub is **not an install** (listed under `NotInstalls`,
and "typing python opens the Microsoft Store instead of running ..." when it is first on PATH); an alias
of `PythonSoftwareFoundation.Python.3.x` is reported as that Store package (version from the package's
program when readable, else the release line with "patch release not known"). Proved with real
reparse points on NTFS (`StoreAliasTests`) and with fakes (`PathAndAliasTests`).

### Which one you actually use

PATH is built as Explorer builds it for a new terminal: system `Path`
(`HKLM\...\Session Manager\Environment`) then user `Path` (`HKCU\Environment`), `%VARIABLES%` expanded
(system values with system variables, user values with user then system ones, then the process
environment), `PATHEXT` from the system environment. Every match is listed in order (`Resolution.Hits`);
the first is followed through Scoop shims (`*.shim`), Volta's `platform.json`, pyenv-win's `version`
file and links (nvm-windows' `NVM_SYMLINK`, Scoop `current`) to the program that really runs. Reported:
an older default although a newer install exists, a Store stub ahead of a real Python, a version
manager whose folder comes after another install (so switching has no effect). Shell start-up files
(PowerShell 7 and Windows PowerShell profiles, Git Bash `.bashrc`/`.bash_profile`/`.profile`, the
Command Prompt `AutoRun`) are read, never run, and give "May differ in PowerShell 7: ... line 3 runs fnm
env" (line contents are never shown; they may hold secrets).

Note: M2's registry reader returns `REG_EXPAND_SZ` values expanded against DevSweep's own environment;
the result differs from Explorer's only if a referenced variable changed after DevSweep started.

### Support dates (endoflife.date)

`EndOfLifeClient`: `https://endoflife.date/api/<product>.json`, 10-second limit, cached for a day in
`%LOCALAPPDATA%\DevSweep\endoflife\<product>.json` (atomic writes). A failed download uses the cache
however old (`EolOffline`), else "Not checked"; `--offline` never downloads. Products: `nodejs`,
`python`, `postgresql`, `go`, `ruby`, `php`, `rust`, `dotnet` (lines "8.0"), `deno`, `bun`; Java per vendor
(`eclipse-temurin`, `microsoft-build-of-openjdk`, `amazon-corretto`, `azul-zulu`, `oracle-jdk`);
Windows itself (`windows` lines like `11-24h2-w`/`-e`, `windows-server`). Flags: end of life, ending
within 120 days, newer patch in the same line, installed by several tools, the same version twice.
Tests use recorded responses only (`EolFixtures`), never the network.

### Project requirements

`.nvmrc`, `.node-version`, `package.json` engines, `.python-version`, `runtime.txt`, `pyproject.toml`
(`requires-python`, Poetry), `.ruby-version`, `.java-version`, Gradle toolchains, `go.mod`,
`composer.json`, `rust-toolchain(.toml)`, `.tool-versions` and `global.json` (with its `rollForward`
policy: `latestPatch` means the same feature band, `latestFeature` the same minor...). Projects are
looked for (three levels) in the same folders as the stale-project rule (`rules/70-projects.yml`) or the
given `--project-root`s; links and cloud-only folders are never entered.

### Fixes

Commands per owning tool: winget, Scoop (`scoop update`/`scoop cleanup`), Chocolatey (needs an
administrator), nvm-windows, fnm, Volta, pyenv-win, uv, the Python install manager (`py install`),
conda (`conda env remove --prefix`), rustup, mise, asdf, MSI (`msiexec /x {code}`), Store packages
(`Remove-AppxPackage`), folder removal to the Recycle Bin; otherwise Settings > Apps. Values are always
single-quoted PowerShell literals. `devsweep runtimes --open-step <id>` writes
`%TEMP%\DevSweep\Scripts\<time>-<id>.ps1` (UTF-8 with BOM) and opens it in a new PowerShell window
(Windows Terminal when it is the default terminal) with `-NoProfile`: it refreshes PATH from the
registry, lists the commands, waits for Enter, stops at the first failing command and keeps the window
open. For admin steps a non-elevated window explains why and offers "A" to reopen itself with
`-Verb RunAs` (Windows asks); nothing is ever elevated silently.

## Security

Looks in Downloads, Desktop and Documents (Known Folders, wherever redirected; two levels) and project
folders (`.env` only, four levels): recovery codes, private keys outside `.ssh` (passphrase-protected
ones are a warning), Google service-account keys (also base64), AWS key CSVs, password-manager exports,
`.env` files (git: committed / ignored / "one `git add .` away" / not a repository), and on Windows
`.rdp` files with a saved password, `.publishsettings`, `.pfx`/`.p12` outside the certificate store and
PuTTY `.ppk` (unencrypted is critical). Names and at most the first 64 KB are read, through a handle
opened with `FILE_FLAG_OPEN_NO_RECALL`; cloud-only files are counted and never opened; links are never
followed; values are never kept, logged or shown (tests assert the fake secrets appear in no field).
git is the one on the Explorer PATH (Scoop shims followed), trusted like any program, run without a
shell with `-c core.fsmonitor=false -c core.hooksPath=NUL` so a downloaded repository cannot run its own
programs.

## Ports

`GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)` and `GetExtendedUdpTable(UDP_TABLE_OWNER_PID)` for
IPv4 and IPv6, parsed from the documented row layouts (unit-tested with built buffers, and compared with
`netstat -ano` on the runner). For each owning process: program path (`QueryFullProcessImageNameW`),
command line (`NtQueryInformationProcess(ProcessCommandLineInformation)`, secret-looking values masked),
owner (token user SID vs ours) and working directory. The working directory has no documented API: it is
read from the process's `RTL_USER_PROCESS_PARAMETERS` through its PEB with x64 offsets (as Process
Explorer and psutil do), only for 64-bit processes that can be opened for reading; the folder shown says
where it came from (`WorkingDirectory`, `CommandLine` path inside a project, `ProgramPath` inside a
project, or `Unknown`). Exposure: loopback only, all interfaces, or a specific address.

Firewall reachability (per Domain/Private/Public profile, block rules first) is judged from the
firewall's own configuration in the registry (local rules, Group Policy rules, each profile's on/off and
default inbound action); rules for services or restricted remote addresses are reported as such.
`INetFwPolicy2` is IDispatch-based and not usable from the trimmed Native AOT engine, and `netsh` output
is localised, so neither is used; when the rules cannot be read the verdict is "unknown".

Stopping (`ProcessStopper`): only processes of the current user, never Windows' own processes,
Explorer, terminals or DevSweep. First politely: `WM_CLOSE` to its main window, or Ctrl+C to its console
(attach, `GenerateConsoleCtrlEvent`, detach; the CLI does this from a throwaway copy of itself so its
own console stays attached). Only when it is still running and the user confirms (`--yes`, or "y" at the
prompt) is it ended with `TerminateProcess`.

## Alerts (logic for M5)

`AlertEvaluator` ports the Mac rules: disk at least `DiskPercent` full (default 85; cool-down 3 days,
forgotten when it drops below so the next crossing is said at once), something grew by `GrowthGB`
(default 2 GB) since last week, tools past end of life, new secret files (only against a baseline from an
earlier run); each announced once, then again only after its cool-down (7 days). `AlertLedger`,
`AlertStore` (`%LOCALAPPDATA%\DevSweep\alerts.json`) and `ScanSnapshotStore`
(`%LOCALAPPDATA%\DevSweep\scans.json`, last 40 scans; M2 kept none) persist with atomic writes; an
unreadable file is renamed aside. `AlertInputs.EndOfLifeFrom`, `SecretsFrom` and `DiskUsedPercentOf`
build inputs from Health results.

## Numbers from CI

CI run [41993174247](https://github.com/karuneshpalekar/DevSweep/actions/runs/41993174247) (`windows-latest`, Windows
Server 2025 Datacenter 24H2, build 26100), on the branch merged with M3a: unit tests 408 passed (Health
adds 54); accuracy suite 175 passed as administrator (Health adds 11), and as a fresh standard user 175
with the 5 earlier M1 skips and no Health skips. Native AOT `devsweep.exe`: 10,988,544 bytes, no trim or
AOT warnings. No live network in any test (recorded endoflife.date responses only).

**Runtime discovery vs ground truth** (`RuntimeGroundTruthTests`): every program `where.exe` finds on
PATH for node, python, java, go, ruby, php, dotnet, psql, rustc, deno, bun and flutter belongs to a
discovered install: 8 programs checked as administrator and as the standard user, **0 missed** (the
runner's `WindowsApps\python.exe` is recognised as an alias and not counted). All 5 PEP 514
registrations (PythonCore 3.11 to 3.15), `JAVA_HOME` and the 5 `JAVA_HOME_*` JDKs (8, 11, 17, 21, 25),
and all 5 `py -0p` entries are discovered.

`devsweep runtimes --json` on the runner (administrator):

| Tool | Installs | Default (a new terminal runs) | Sources |
|---|---:|---|---|
| Node.js | 4 | 22.23.4 (nodejs.org MSI) | Visual Studio's node, nodejs.org, CI tool cache |
| Python | 6 | 3.12.10 (tool cache) | conda (`C:\Miniconda`), CI tool cache |
| Java | 5 | 17.0.20 (tool cache) | CI tool cache (Temurin 8, 11, 17, 21, 25) |
| PostgreSQL | 1 | not on PATH | EDB installer |
| Go | 3 | 1.24.13 | CI tool cache |
| Ruby | 4 | 3.3.13 | CI tool cache |
| PHP | 1 | 8.5.12 | Chocolatey (`C:\tools\php`) |
| Rust | 1 | 1.99.1 | rustup (default from `settings.toml`) |
| .NET SDK | 11 | 10.0.403 | `DOTNET_ROOT` (setup-dotnet) |
| Windows | 1 | Windows Server 2025 Datacenter 24H2 (build 26100) | supported (endoflife.date `windows-server`) |

72 PATH folders (system then user); support data downloaded live in CI (not offline). The first
suggested fix's script (`--script-step`) lists its commands and waits for Enter.

**Store aliases** (`StoreAliasTests`): real `IO_REPARSE_TAG_APPEXECLINK` reparse points created on NTFS
(as administrator and as the standard user): the App Installer stub is read as
`StoreInstallerStub` and reported only under "not an install" with the "opens the Microsoft Store"
warning; the Python 3.12 package alias is reported as a Store Python install (release line 3.12,
"patch release not known").

**Ports** (`PortsAccuracyTests`): IPv4 TCP listeners equal `netstat -ano` exactly (0 missing, 0 extra);
our own `127.0.0.1` and `0.0.0.0` listeners are found with our PID, owner = us, program path, command
line and **working directory read through the PEB equal to `Environment.CurrentDirectory`**. Windows
Firewall: 348 rules read, as administrator and as the standard user; our `0.0.0.0` listener is "Allowed
through Windows Firewall on every network type" (the runner's rules allow it). `ping -t` in its own
console stopped by Ctrl+C ("sent Ctrl+C to its console"); a Command Prompt (which survives Ctrl+C) is
reported "still running" and ended only by the explicit `End` (the CLI's confirmation). PID 4, our own
process and a service (`spoolsv`) are refused. CLI: `ports --json` lists our pwsh listener
(`AllInterfaces`, firewall verdict) with 33-35 listeners of other users hidden.

**Security** (`SecurityAccuracyTests`): with real git, `.env` files are "Committed to git", "Kept out of
git", "Not ignored" and "Not in git" exactly as set up; a junction to a folder with recovery codes is not
followed; three cloud-only placeholders (`github-recovery-codes.txt`, `id_rsa`, `.env`) in a real Cloud
Files sync root are counted as skipped and keep 0 bytes of local data (never hydrated). The runner's own
loose folders hold 0 findings.

## Decisions and deviations

- **Health was off by default** (`module:health`) until the coordinator accepted the gate; since 0.1.1
  it is on, and CI turns it off with `DEVSWEEP_FEATURES=-module:health` to check that it exits 3.
  The default-module unit test expects `engine.Disabled` to be empty.
- **Firewall from the registry**, not `INetFwPolicy2` (IDispatch, not usable from the AOT engine) or
  `netsh` (localised output).
- **Working directory through the PEB** (undocumented layout, x64 only, same-bitness processes that can
  be opened for reading), with the source of every folder shown; Ctrl+C via a helper process in the CLI.
- **winget** is never run: winget ownership comes from `WinGetPackageIdentifier` in the Uninstall keys
  and winget's portable package folders (M2 inventory).
- Programs are run for a version only when no file gives it, only trusted ones; in CI every version came
  from files or version resources except PHP and PostgreSQL where the resource was used too.
- The Mac app's Homebrew report and the guided PostgreSQL upgrade have no Windows counterpart yet
  (EDB upgrades need `pg_upgrade` with service changes as administrator; left for later).

## Known gaps

- PATH: M2's registry reader returns `REG_EXPAND_SZ` already expanded with DevSweep's environment.
- Shells: start-up files are read for known patterns only; what a profile really does is not executed.
- Chocolatey vs nodejs.org MSI: an MSI in Program Files is called Chocolatey's only when a Chocolatey
  package of the same version exists.
- The PLAN 6 reference machines (nvm/fnm/Volta/Scoop/pyenv/Store Python) are not available to a
  milestone run; those sources are proved with fixture machines, the CI runner covers tool caches,
  conda, MSI installs, Chocolatey, rustup, PEP 514 and `py`.
- Alerts are logic only; M5 wires the scheduler and toasts and records snapshots after each scan.
