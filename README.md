# DevSweep

A Mac app that finds what's safe to clean on a developer's machine, and
explains every item before you touch it.

Developer Macs fill up with things no general cleaner understands: Android
system images no emulator uses, Kotlin/Native compilers from projects you
finished a year ago, simulator runtimes, settings from three Android Studio
versions back, npm and Gradle caches, and files from apps you removed long
ago. DevSweep finds them, tells you what each one is, what was checked, and
exactly what happens if you remove it.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/items-dark.png">
  <img alt="DevSweep: items grouped by risk, with the explanation panel open for Chrome cache" src="docs/screenshots/items-light.png">
</picture>

## Screenshots

Light and dark mode follow your Mac, or pick one in the sidebar.

| | Light | Dark |
|---|---|---|
| **Every item explained.** The ⓘ panel says what it is, why it was flagged, what was checked on your Mac, what you won't lose, and a better option when there is one. | ![Items and explanation panel, light](docs/screenshots/items-light.png) | ![Items and explanation panel, dark](docs/screenshots/items-dark.png) |
| **Review before anything changes.** Items are grouped by what will actually happen: deleted right away (rebuilds itself) or moved to the Trash (restorable). Apps that must quit first are flagged, with a button to quit them. | ![Review sheet, light](docs/screenshots/review-light.png) | ![Review sheet, dark](docs/screenshots/review-dark.png) |
| **History and undo.** Everything DevSweep changed, with Restore for anything still in the Trash. | ![History, light](docs/screenshots/history-light.png) | ![History, dark](docs/screenshots/history-dark.png) |
| **Overview.** Free space, what can be cleaned, and where it is. | ![Overview, light](docs/screenshots/overview-light.png) | ![Overview, dark](docs/screenshots/overview-dark.png) |
| **Menu bar.** Space at a glance and a quick scan. | <img alt="Menu bar, light" src="docs/screenshots/menubar-light.png" width="320"> | <img alt="Menu bar, dark" src="docs/screenshots/menubar-dark.png" width="320"> |

## How it behaves

- **Scanning never changes anything.** You pick items, review them, and
  confirm. The review sheet shows the exact commands that will run.
- **Everything is explained.** Each item has an ⓘ panel: what it is, why it
  was flagged, what was checked on your Mac, what happens if you clean it,
  what you won't lose, and how to undo it.
- **Reversible by default.** Anything that could matter goes to the Trash
  and can be restored from History. Only things that rebuild on their own
  (caches) are deleted outright, using the tool's own command where there is
  one (`npm cache clean`, `pip cache purge`, `xcrun simctl`...).
- **Risk decides the options.** Items are labelled *Rebuilds itself*, *Old
  version*, *Leftover from a removed app*, *Holds your data* or *Needs
  admin*. Things that hold data never get a plain delete; things outside your
  home folder are shown as commands for you to run.
- **A hard safety net.** Whatever a rule says, DevSweep refuses to remove
  anything outside your home folder, top-level visible folders, or
  protected folders like `~/Library` and `~/.ssh`.
- **No AI and no network needed** for scanning and cleaning.

## What it finds (v0.1)

| Area | Examples |
|---|---|
| Leftovers | Files from uninstalled apps, matched by bundle ID across 11 Library folders |
| Background services | Launch agents and daemons whose program no longer exists |
| Old IDE versions | Android Studio and JetBrains settings from older versions, stale VS Code/Cursor extensions |
| Android | System images no emulator uses, old build tools, platforms, NDKs, Gradle versions |
| Xcode | DerivedData, device support files, superseded simulator runtimes, unavailable simulators |
| Toolchains | Old Kotlin/Native compilers, Intel builds on Apple silicon, Java versions older than 11 |
| Package caches | npm, npx, pip, Homebrew, Yarn, CocoaPods, SwiftPM, Gradle, Go, Cargo |
| Projects | `node_modules`, virtualenvs and Pods in projects untouched for 30 days |
| Browsers and AI models | Chrome/Edge/Brave caches, Chrome's on-device model, Whisper, Hugging Face, PyTorch |

## Install

Download the latest `.dmg` from [Releases](https://github.com/karuneshpalekar/DevSweep/releases),
open it, and drag DevSweep to Applications.

Releases aren't notarized by Apple (that needs a paid developer account), so
macOS asks you to allow DevSweep the first time:

- **macOS 14:** right-click DevSweep in Applications, choose **Open**, then **Open** again.
- **macOS 15 and later:** try to open it, then click **Open Anyway** in System
  Settings, Privacy & Security.
- **Or:** `xattr -dr com.apple.quarantine /Applications/DevSweep.app`

## Build and run

Requires macOS 14+, Xcode 15.3+ and [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
brew install xcodegen
./install.sh          # builds Release and installs to /Applications
```

Or open the project in Xcode: `./generate.sh && open DevSweep.xcodeproj`.

For complete results, give DevSweep **Full Disk Access** (System Settings,
Privacy & Security). Without it macOS hides some app folders.

To sign with your own team (so the Full Disk Access grant survives
rebuilds), copy `Config/Local.xcconfig.example` to `Config/Local.xcconfig`.

### Command line

The engine is a Swift package with a CLI, handy for testing rules:

```bash
cd Core
swift run devsweep scan              # read-only scan
swift run devsweep scan --explain    # include the explanation and checks
swift run devsweep scan --json
swift run devsweep history
swift test
```

## Writing rules

Rules are YAML files in `Core/Sources/DevSweepCore/Rules/`. You can also
drop your own packs into `~/Library/Application Support/DevSweep/Rules/`
(a rule with the same `id` overrides the bundled one).

```yaml
rules:
  - id: pip-cache
    title: pip cache
    category: packageCaches      # leftovers, ideVersions, android, xcode, toolchains,
                                 # packageCaches, browsers, projects, aiModels, backgroundServices
    risk: rebuilds               # rebuilds, oldVersion, leftover, holdsData, needsAdmin
    minSizeMB: 50
    blockers: ["SomeApp"]        # optional: apps that must be closed first
    detector:
      kind: paths
      paths: ["~/Library/Caches/pip"]
    actions:                     # first one is the default
      - kind: command
        label: Delete
        command: ["pip3", "cache", "purge"]
      - kind: delete
    explain:
      what: "Python packages pip has downloaded or built."
      why: "{{size}} of downloaded and built packages."
      ifDeleted: "The next pip install downloads packages again."
      wontLose: "Packages already installed in your environments."
```

Explanations can use `{{placeholders}}` filled from what the scan found:
`size`, `count`, `path`, plus detector-specific facts such as `version`,
`newest`, `kept`, `versions`, `app`, `level` or `project`.

Detector kinds: `paths`, `versionedSiblings` (keep the newest N of versioned
folders), `orphanedAppData`, `orphanedLaunchServices`, `androidSystemImages`,
`simulatorRuntimes`, `unavailableSimulators`, `editorExtensions`, `oldJDKs`,
`staleProjectArtifacts`. See the bundled packs for examples of each.

Good rules are conservative. If you're not sure something is safe, label it
`holdsData` and say why in `explain`.

## Releasing

Write `docs/release-notes/<version>.md`, commit, then run
`scripts/release.sh <version>`. It builds a Release, packages a DMG with a
checksum, tags the commit and publishes a GitHub release.

Builds are ad-hoc signed. If a **Developer ID Application** certificate and
notary credentials (`xcrun notarytool store-credentials devsweep ...`) are
in the keychain, the script signs with Developer ID and notarizes instead.

## Updating the screenshots

Debug builds can walk through every screen in light and dark mode and save
the README images. The app captures only its own windows, so no screen
recording permission is needed:

```bash
DEVSWEEP_SHOTS="$PWD/docs/screenshots" \
  build/Build/Products/Debug/DevSweep.app/Contents/MacOS/DevSweep
```

## Roadmap

- Runtime and version checks: end-of-life dates from endoflife.date,
  Homebrew deprecations, duplicate installs (Homebrew vs nvm vs python.org)
- Guided upgrades for things that hold data, starting with PostgreSQL
- Scheduled scans and a disk-space alert from the menu bar
- Running admin commands behind a single password prompt

## License

MIT
