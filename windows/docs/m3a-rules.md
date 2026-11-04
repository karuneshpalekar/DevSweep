# M3a: Windows rule packs

The first full set of Windows rules (PLAN 4.5): 75 new rules in ten packs, 16 new detector
kinds, 13 new named locations, all in one module that can be switched off. Every rule has a
fixture test on real NTFS that checks where it looks, the exact size it reports, and that its
explanation is fully filled in. Nothing here runs elevated or removes anything outside the
user's own folders: admin-only items are shown as commands.

## The module (`module:packs`)

`src/DevSweep.Core/Packs/` (namespace `DevSweep.Core.Packs`), registered in `BuiltInModules.All`
as `PacksModule`:

- `Detectors/`: the new detector kinds (below) and their parsers;
- `Locations/PackLocations.cs`: the new named roots;
- `Tools/IMachineQueries.cs`: the only way a pack runs a program or asks for the Recycle Bin.
  Programs are `.exe` files only (found on `PATH` or in the tool's own install folder by
  `ToolLocator`), run with no shell and a time limit (10 to 30 s) through M1's `CommandRunner`.
  `FixedMachineQueries` answers in tests; `MachineFixture.Machine` gives every fixture machine one,
  so no test ever runs a real tool;
- packs: `windows/rules/packs/*.yml`, embedded as `DevSweep.Packs.<file>.yml`.

`DEVSWEEP_FEATURES=-module:packs` (or `features.json`) turns all of it off: Clean up keeps its
eight M2 rules, and nothing reports a pack error (tested in both suites and in CI). The module
also turns itself off when `module:cleanup` is off, because its packs use Clean up's `paths` and
`versionedSiblings` detectors. Single rules, categories and detector kinds can be turned off as in M2.

**Claim order across modules.** `DevSweepEngine.BundledPacks()` now sorts every module's packs by
file name, so file-name order is claim order whichever module a pack comes from (the packs are
numbered between M2's: `11-` after `10-`, `91-` after `90-leftovers`).

## Rules

`P` = paths/versionedSiblings rule tested by `PackPathRuleTests.Rule_finds_exactly_its_folders`
(one case each); `D` = detector test in `PackDetectorTests`.

| Pack | Rule | Risk | Action | Test |
|---|---|---|---|---|
| 11-package-caches | npx-cache, uv-cache (`uv cache clean`), go-module-cache (`go clean -modcache`), go-build-cache (`go clean -cache`), cargo-cache, maven-repository, gradle-daemon-logs, composer-cache, chocolatey-cache, scoop-cache, winget-downloads, nuget-other-caches | rebuilds | delete (+ the tool's command; `.cmd`/`.ps1` tools shown as manual) | P |
| | pnpm-store (`pnpm store prune` shown; hard links: the size says how much is shared) | rebuilds | manual, delete | P |
| | yarn-cache (v1 cache and the Berry global cache) | rebuilds | delete, manual | P |
| | nuget-global-packages (`dotnet nuget locals global-packages --clear`; blocks on devenv, rider64) | rebuilds | command, delete | P |
| | gradle-wrappers (keeps the newest 2) | oldVersion | trash | P |
| 15-docker-wsl | docker-build-cache, docker-images, docker-containers, docker-volumes (sizes from `docker system df`, prune commands; port of the Mac pack) | as Mac | command | D |
| | wsl-disks, docker-disk (`vhdxDisks`) | holdsData | manual steps only | D |
| 21-windows | crash-dumps (older than 7 days) | oldVersion | trash | D |
| | error-reports-user | rebuilds | delete, trash | P |
| | error-reports-machine, windows-update-cleanup (DISM, Disk Cleanup, Delivery Optimization) | needsAdmin | manual | P |
| | thumbnail-cache (Explorer holds them open: Disk Cleanup) | rebuilds | manual | P |
| | shader-caches (D3D, NVIDIA, AMD, Intel) | rebuilds | delete | P |
| | recycle-bin (`SHQueryRecycleBin`, informational) | holdsData | manual | D |
| 25-browsers-and-ai | chrome-cache, edge-cache, brave-cache (one finding per profile) | rebuilds | delete, trash | D |
| | chrome-ai-model, edge-ai-model, torch-hub, whisper-models, huggingface-models (one per model) | rebuilds | delete/trash | P |
| | chrome-profiles, edge-profiles (unused 180 days, never the one the browser opens with) | holdsData | manual | D |
| | ollama-models (size = layers only that model uses; `ollama rm`) | holdsData | manual | D |
| | lm-studio-models | holdsData | trash | P |
| 31-dotnet-vs | dotnet-sdks, dotnet-runtimes, dotnet-workload-packs (Program Files) | needsAdmin | manual (Settings, `dotnet-core-uninstall`, `dotnet workload clean`) | D |
| | dotnet-sdks-user (`%LOCALAPPDATA%\Microsoft\dotnet`, `~\.dotnet`) | oldVersion | trash | D |
| | vs-component-model-cache, resharper-caches (block on devenv) | rebuilds | delete | P |
| | vs-old-instances (`visualStudioInstances`) | leftover | trash | D |
| | vs-package-cache | needsAdmin | manual (the installer's own setting) | P |
| 40-android | android-system-images (`androidSystemImages`) | oldVersion | trash | D |
| | android-build-tools, android-platforms (keep 3), android-ndk (keep 1) | oldVersion | trash | P |
| | android-emulator-snapshots | holdsData | trash | P |
| | android-studio-old (inventory guard), android-studio-caches | oldVersion / rebuilds | trash / delete | D / P |
| 50-toolchains | old-jdks-user, old-jdks-machine (`oldJdks`) | oldVersion / needsAdmin | trash / manual | D |
| | nvm-old-node, fnm-old-node, volta-old-node, pyenv-old-python (`toolVersions`) | oldVersion | trash | D |
| | rustup-toolchains (`rustup toolchain uninstall`) | oldVersion | command, trash | D |
| | windows-sdks (keep 2) | needsAdmin | manual | P |
| 61-ide | editor-extensions (`editorExtensions`; VS Code, Insiders, Cursor, Windsurf) | oldVersion | trash | D |
| | vscode-caches, cursor-caches, windsurf-caches, jetbrains-caches (each blocks on its IDE) | rebuilds | delete | P |
| 65-large-files | old-installers (Downloads: msi, exe, msix, appx, iso; 14 days), old-disk-images (Desktop iso; 30 days) | oldVersion | trash | D |
| | large-old-files (Downloads, Desktop, Documents; 200 MB, 120 days) | holdsData | trash | D |
| 91-leftovers | orphaned-program-data (ProgramData) | needsAdmin | manual | D |
| | orphaned-scheduled-tasks, orphaned-startup-entries | leftover | manual (`schtasks /delete`, `reg delete`) | D |

M2 packs changed: `60-ide.yml` jetbrains-old gets the side-by-side guard; `70-projects.yml` adds
`.next`, `.turbo`, `build`, `dist`; `90-leftovers.yml` lists ten more apps (Discord, Figma,
Notion, Cursor, VS Code, Docker Desktop, Obsidian, Spotify, Unity Hub, Oracle Java in LocalLow).

Explanations follow the Mac packs: plain words, what / why (with the real numbers and the
location the tool's own settings gave) / if deleted / what you won't lose / before / undo.

## Detectors

| Kind | What it decides, and what it never does |
|---|---|
| `docker` | Rows of `docker system df --format "{{json .}}"` (docker.exe on PATH or Docker Desktop's copy, 30 s); reclaimable bytes as Docker reports them (`Reported`); nothing when Docker is missing or not running (a note says which) |
| `vhdxDisks` | WSL 2 distros from `HKCU\...\Lxss\{guid}` (`DistributionName`, `BasePath` with `\\?\` stripped, `VhdFileName`, default `ext4.vhdx`; Docker's own distros skipped) or Docker Desktop's `docker_data.vhdx` / old `ext4.vhdx`. File size by the walker (allocation). **Size 0**: what compaction frees can't be known before it runs. Only if the distro is already running (`wsl --list --running --quiet`, UTF-16 handled) is `df -B1 --output=used /` run inside it, giving "at most file − used"; a stopped distro is never started. Actions: `wsl --shutdown`, then `Optimize-VHD` (Hyper-V module, admin) or the diskpart steps; `--set-sparse` as the better way |
| `dotnetInstalls` | `what: sdk`: per install root, keeps the newest SDK of each major and every SDK in the feature band (`8.0.4xx`) a `global.json` in the project folders pins; prereleases sort before their release. `runtime`: keeps the newest patch of each framework's major.minor. `workloadPacks`: a pack version with no install record under `ProgramData\dotnet\workloads\<arch>\<band>\InstalledPacks\v1`, only when that pack has records for other versions (packs DevSweep can't tie to records are left alone; unreadable records mean nothing is offered). `dotnet --list-sdks` / `--list-runtimes` (10 s) is run only to say whether it agrees with the folder |
| `visualStudioInstances` | `%LOCALAPPDATA%\Microsoft\VisualStudio\<major>.0_<id>` (and `Exp`, `_Config`) whose `<id>` has no `ProgramData\Microsoft\VisualStudio\Packages\_Instances\<id>` (state.json read only for names). No `_Instances` folder, or one that can't be read: nothing offered |
| `editorExtensions` | Port of the Mac detector: folders not in `extensions.json` (relativeLocation or location.path) or marked in `.obsolete`; only `publisher.name-1.2.3` folders; nothing when the list is missing, empty or unreadable |
| `androidSystemImages` | Port: images no AVD's `config.ini` (`image.sysdir.1`, either slash) names; AVDs from `*.avd` and the `path=` of each `<name>.ini`; any unreadable AVD config means nothing is offered |
| `oldJdks` | Port: major version from each JDK's `release` file; older than `minMajor` (11) when Java ≥ 11 is also installed; `JAVA_HOME` and `PATH` pointing into it give warnings |
| `toolVersions` | nvm for Windows (`settings.txt` root/path, the active link's target read, never followed or removed), fnm (aliases), Volta (`platform.json`), pyenv-win (global `version`, `PYENV_VERSION`, `.python-version` in project folders). Only older patches of a line (Node major, Python major.minor) are offered; a link that can't be read means nothing is offered |
| `rustToolchains` | Not the default (`settings.toml`), not an override, not `RUSTUP_TOOLCHAIN`, not pinned by `rust-toolchain(.toml)`; only numbered toolchains (channels like nightly may be used with `cargo +nightly`); unreadable settings: nothing |
| `largeOldFiles` | Port; four levels deep, no links, cloud files, hidden or system items. Reads `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisableLastAccessUpdate` (low bit set = off; 0x80000000 = system-managed): last-opened dates count only when NTFS records them; every finding says which date was used and why |
| `browserCaches` / `browserProfiles` | Ports of `browserProfileCaches` / `chromeProfiles` for Windows' layout (cache folders inside each profile in `User Data`; `Local State` `info_cache`, `last_used`, `active_time`). One finding per profile, since the Windows model has no per-profile parts |
| `ollamaModels` | Manifests to blobs; a model's size is the blobs no other model uses (what `ollama rm` frees), shared bytes in the text; `Reported`, no paths |
| `orphanedScheduledTasks` | Task XML files in `{Windows}\System32\Tasks` (skipping `\Microsoft\`), or, when that folder can't be listed (standard users), `schtasks /query /xml ONE` (30 s). A task counts only when every Exec program is a full local path (variables expanded, unknown variable = not judged) whose drive exists and file doesn't |
| `orphanedRunEntries` | `HKCU\...\CurrentVersion\Run` values with the same test |
| `recycleBin` | `SHQueryRecycleBin` per fixed drive |

`versionedSiblings` (M2) has a new option, `installedAs` (`{key, names}`): an old version stays
while the inventory lists an app of that name with that version (`IntelliJ IDEA 2024.1.4` keeps
`IntelliJIdea2024.1`, but not `2024.10`), and a guarded family is left alone entirely when any
inventory source could not be read completely (the M2 gap "JetBrains IDEs installed side by side").

`staleProjectArtifacts` (M2) knows `.next` (Next.js; confirmed by `BUILD_ID`, `cache`, `server` or
`static`), `.turbo` (turbo.json), `build` (Gradle output: `intermediates`, `classes`...) and `dist`
(bundler output next to a vite/webpack/rollup/tsup config, with a script or index.html at its top).
`build` and `dist` are common names, so they are offered only when confirmed and otherwise count as
project activity like any other folder (`ArtifactKind.AlwaysArtifact = false`).

## Locations added

`{Windows}` (`SystemRoot`, `windir`), `{androidAvd}` (`ANDROID_AVD_HOME`, `ANDROID_USER_HOME\avd`,
`ANDROID_SDK_HOME\.android\avd`, `~\.android\avd`), `{goBuildCache}` (`GOCACHE`, the go env file,
`%LOCALAPPDATA%\go-build`), `{composerCache}`, `{chocolateyCache}` (`cacheLocation` in
chocolatey.config, then `{Temp}\chocolatey`), `{rustupHome}`, `{nvmHome}`, `{nvmSymlink}`,
`{fnmDir}`, `{voltaHome}`, `{pyenvRoot}`, `{ollamaModels}`, `{torchHome}`.

## Decisions

- **NuGet global packages**: rebuilds (restores fetch them again), with `dotnet nuget locals
  global-packages --clear` first and blockers on Visual Studio and Rider.
- **Maven**: the whole repository as one rebuildable item; no per-artifact version judgement
  (any project may still need any version).
- **Thumbnail cache**: Explorer always holds it open and DevSweep never closes Explorer, so it is
  shown with Disk Cleanup (which can) rather than deleted.
- **WSL/Docker disks**: size 0 and the numbers in words, because "space freed" must match what
  was shown (PLAN 6) and compaction's result can't be known in advance.
- **.NET SDKs**: keep the newest per major plus pinned bands (older majors stay: a newer SDK builds
  older targets, but removing a whole major is the user's call through its uninstaller).
- **Scheduled tasks**: the task files when readable, otherwise schtasks (COM `ITaskService` would
  need COM interop that is not AOT-friendly).
- **Ollama**: listed with Ollama's own command, never removed by DevSweep.

## Shared files touched

- `src/DevSweep.Core/Modules/BuiltInModules.cs`: `new Packs.PacksModule()` added to `All`.
- `src/DevSweep.Core/Modules/DevSweepEngine.cs`: `BundledPacks()` sorted by file name; scan context gets `Registry`.
- `src/DevSweep.Core/Scanning/ScanContext.cs`: `IRegistryReader Registry` (for the WSL, Run-key and last-access reads).
- `src/DevSweep.Core/Inventory/Registry/IRegistryReader.cs`, `InMemoryRegistry.cs`: `ValueNames()` (default interface method, so other readers still compile).
- `src/DevSweep.Core/Rules/RuleLoader.cs`: `EmbeddedRulePacks.From(assembly, prefix)` overload.
- `src/DevSweep.Core/DevSweep.Core.csproj`: one `EmbeddedResource` line for `rules\packs`.
- `src/DevSweep.Core/Detectors/VersionedSiblingsDetector.cs` (`installedAs`), `Detectors/Projects/ArtifactKinds.cs` and `StaleProjectArtifactsDetector.cs` (new kinds).
- `tests/DevSweep.Accuracy/Rules/MachineFixture.cs`: `Machine` (fake tools) and `Engine()` built module by module.
- `tests/DevSweep.Core.Tests/Rules/RulePackParserTests.cs`, `Modules/FeatureFlagTests.cs`: rule and detector lists.
- `.github/workflows/windows.yml`: two self-contained steps marked `M3a`.

## Numbers from CI

See the end of this file (filled in from the green run).
