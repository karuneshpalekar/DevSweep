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
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/cleanup-dark.png">
  <img alt="DevSweep Clean up: items grouped by risk, with the explanation panel open for Chrome cache" src="docs/screenshots/cleanup-light.png">
</picture>

## Four places, nothing more

DevSweep keeps the window simple: **Home**, **Clean up**, **Projects** and
**Health**, plus History and Settings. Light and dark mode follow your Mac, or pick one
in Settings.

| | Light | Dark |
|---|---|---|
| **Home.** Free space, what can be cleaned, what grew or appeared since last week, and the few things that need attention. | ![Home, light](docs/screenshots/home-light.png) | ![Home, dark](docs/screenshots/home-dark.png) |
| **Clean up.** Everything that can go, filtered by kind (caches, old versions, leftovers, project dependencies) and grouped by risk. The ⓘ panel explains each item: what it is, what was checked on your Mac, what you won't lose, and a better option when there is one. | ![Clean up, light](docs/screenshots/cleanup-light.png) | ![Clean up, dark](docs/screenshots/cleanup-dark.png) |
| **Projects.** Your repos on this Mac and on GitHub, across every account you're signed in to. Each one says whether it's safe to remove (commits not pushed, uncommitted changes, stashes) before it can leave your Mac, and anything on GitHub downloads again on demand. | ![Projects, light](docs/screenshots/projects-light.png) | ![Projects, dark](docs/screenshots/projects-dark.png) |
| **Projects: Cleanup.** Local copies you haven't opened in a while (1 week to 3 months, you choose), with what removing them would free. Fully backed-up ones are marked safe to remove, one by one or all at once. | ![Cleanup, light](docs/screenshots/idle-light.png) | ![Cleanup, dark](docs/screenshots/idle-dark.png) |
| **Projects: Accounts.** Every GitHub account you're signed in to, what it has on this Mac, and the name and email its commits use. Clones from accounts you're not signed in to are listed too. | ![Accounts, light](docs/screenshots/accounts-light.png) | ![Accounts, dark](docs/screenshots/accounts-dark.png) |
| **Projects: Activity.** A running trail of downloads, removals, publishes, pushes and account changes, with timestamps. | ![Activity, light](docs/screenshots/activity-light.png) | ![Activity, dark](docs/screenshots/activity-dark.png) |
| **Review before anything changes.** Items are grouped by what will actually happen, and apps that must quit first are flagged. | ![Review, light](docs/screenshots/review-light.png) | ![Review, dark](docs/screenshots/review-dark.png) |
| **Health: Security.** Recovery codes, private keys, cloud keys, password exports and `.env` files that git would commit. Recognised by name and shape; the secrets themselves are never read into DevSweep. | ![Security, light](docs/screenshots/security-light.png) | ![Security, dark](docs/screenshots/security-dark.png) |
| **Health: Tools and versions.** Every install of 12 languages and databases, which one your shell runs, whether it's still supported, and what your projects ask for, with fixes you can run in Terminal. | ![Tools and versions, light](docs/screenshots/health-light.png) | ![Tools and versions, dark](docs/screenshots/health-dark.png) |
| **Health: Ports.** What's listening, which project it belongs to, and whether other devices on your network can reach it. | ![Ports, light](docs/screenshots/ports-light.png) | ![Ports, dark](docs/screenshots/ports-dark.png) |
| **History and undo.** Everything DevSweep changed, with Restore for anything still in the Trash. | ![History, light](docs/screenshots/history-light.png) | ![History, dark](docs/screenshots/history-dark.png) |
| **First launch.** What DevSweep looks at, what it never does, and the permissions macOS may ask for. | ![Welcome, light](docs/screenshots/welcome-light.png) | ![Welcome, dark](docs/screenshots/welcome-dark.png) |
| **Scans and alerts.** Open at login, scan on a schedule, and get a notification when the disk is nearly full, something grows fast, a tool you use reaches end of life, or a new file looks like a secret. Nothing is ever cleaned automatically. | <img alt="Scans and alerts, light" src="docs/screenshots/scans-light.png" width="420"> | <img alt="Scans and alerts, dark" src="docs/screenshots/scans-dark.png" width="420"> |
| **Settings and menu bar.** Project folders, GitHub accounts, ignored items and permissions in one window; space, alerts and recent projects at a glance from the menu bar. | <img alt="Settings" src="docs/screenshots/settings-light.png" width="420"> <img alt="Menu bar" src="docs/screenshots/menubar-light.png" width="280"> | <img alt="Settings, dark" src="docs/screenshots/settings-dark.png" width="420"> <img alt="Menu bar, dark" src="docs/screenshots/menubar-dark.png" width="280"> |

## How it behaves

- **Scanning never changes anything.** You pick items, review them, and
  confirm. The review sheet shows the exact commands that will run.
- **Everything is explained.** Each item has an ⓘ panel: what it is, why it
  was flagged, what was checked on your Mac, what happens if you clean it,
  what you won't lose, and how to undo it.
- **Explains before it asks.** The first launch says what DevSweep looks at
  and which permission prompts macOS may show, before the first scan.
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
| Browsers and AI models | Chrome/Edge/Brave caches, Chrome's on-device model, Chrome profiles unused for 6 months, Whisper, Hugging Face, PyTorch |
| Docker | Build cache, unused images, stopped containers and unused volumes, sized by `docker system df` and cleaned with Docker's own prune commands |
| Large files | Old installers (.dmg, .pkg, .iso) in Downloads and Desktop, and files over 200 MB nobody has opened in 4 months, judged by Spotlight's last-opened date |
| Backups | Old or replaced iPhone and iPad backups, and Time Machine local snapshots (run in Terminal) |

## Scheduled scans and alerts

DevSweep can check in on its own, and tell you only when something matters.

- **Open at login** puts DevSweep in the menu bar and keeps it there after you
  close its window. Scheduled scans and alerts only run while it's open.
- **Scheduled scans** run every day or week at a time you pick. They're
  read-only, like every scan. A Mac that was asleep or off at the scheduled
  time scans when it wakes.
- **Alerts** (each one can be switched off): the disk is more than 70 to 95%
  full; something grew by more than 1 to 10 GB in a week; a tool you use is
  past end of life; a new file that looks like a secret appeared. Several
  problems become one notification, each is announced once, and a reminder
  comes only after 3 to 7 days if it's still there. Clicking a notification
  opens the right screen.
- Nothing is ever cleaned, moved or deleted by a scheduled scan.

## Projects

Projects replaces RepoShelf, and carries over its tabs: **Projects**,
**Cleanup**, **Accounts** and **Activity**, plus a gear that opens the
related settings.

- **Every project in one list**: repos on GitHub for each account `gh` knows,
  clones already on your Mac (found by their `origin`, wherever the folder is),
  folders with no GitHub origin, and repos you removed earlier, which stay
  listed with a Download button.
- **Safe removal.** Before a project can leave your Mac, DevSweep checks for
  commits on any branch that aren't on GitHub, uncommitted or untracked files,
  and stashes. Only a fully backed-up project can be removed, to the Trash,
  and a removed project downloads back into the folder it came from. **Push**
  runs in Terminal so you see it.
- **Download with a strategy**: blobless (full history, contents on demand),
  shallow (latest commit only) or full, with a size estimate.
- **Per-account commit identity.** Set a name and email for each GitHub
  account in Settings, GitHub, and they're written into every project
  downloaded with it, so commits are attributed correctly whatever your global
  Git identity is. DevSweep warns when an account has none.
- **Accounts without switching.** It uses each account's own token, so the
  account active in your terminal never changes.
- **Publish a folder** to GitHub (private or public), after checking it for
  files that look like secrets.
- **Add any repo by URL**, for forks and team repos outside your own lists.
- **Cleanup.** Projects untouched for 1 week to 3 months (you choose, 3 weeks
  by default), biggest first, with what removing them frees. Safe ones can be
  removed one by one or all at once; the rest say what's holding them back.
- **Accounts.** Each signed-in account with its repositories, what it uses on
  this Mac, and an editable commit identity with a Save button. An Add account
  sheet saves the identity before you sign in, then opens GitHub's sign-in in
  Terminal. Clones from organisations you're not signed in to are listed.
- **Activity.** A trail of downloads, removals, publishes, pushes, added repos,
  identity changes and sign-ins. DevSweep brings over RepoShelf's existing
  trail the first time it runs.
- The menu bar lists your recent projects.

DevSweep imports what RepoShelf remembered (clone strategies, when you last
opened projects, repos you'd removed, and its activity trail) the first time
Projects loads.

From the command line: `swift run devsweep projects`.

## Health: tools and versions

The Health section answers a different question: are the tools you rely on
still supported, and do you have too many copies of them?

- **Finds every install** of Node.js, Python (including conda environments),
  Java, PostgreSQL, Go, Ruby, PHP, Rust, the .NET SDK, Flutter, Deno and Bun,
  whoever installed it: Homebrew, nvm, fnm, Volta, asdf, mise, pyenv, uv,
  rbenv, RVM, SDKMAN, rustup, FVM, the official installers, Postgres.app,
  IDE-bundled JDKs and macOS itself.
- **Checks your projects:** reads `.nvmrc`, `package.json` engines,
  `.python-version`, `pyproject.toml`, `.ruby-version`, `go.mod`, Gradle
  toolchains, `composer.json`, `rust-toolchain` and `.tool-versions`, and
  says when a project asks for a version you don't have or one that's past
  end of life.
- **Knows which one you actually use** by asking your login shell for its
  PATH, so it can tell you when a python.org copy is shadowing Homebrew's.
- **Checks support dates** from [endoflife.date](https://endoflife.date),
  cached for a day and usable offline: release date, end of support, latest
  patch, LTS.
- **Flags** end-of-life versions, versions ending within 120 days, the same
  tool installed by several managers, missing patch releases, PostgreSQL
  servers nobody runs, deprecated or outdated Homebrew packages, and macOS
  updates.
- **Suggests fixes for the tool that owns each install** (`brew`, `nvm`,
  `pyenv`, `sdk`...). Copy them, or run them in Terminal: a window opens,
  lists the commands and waits for Return, so nothing runs unseen and `sudo`
  can ask for your password.
- **Guided PostgreSQL upgrade** for Homebrew installs: backs up every database
  with `pg_dumpall`, counts rows in every table, switches servers, restores,
  counts again and compares, and only then asks before removing the old
  version. The backup stays in `~/DevSweep Backups`.
- Apps that are part of macOS or an IDE (like macOS's own Python 3.9) are
  reported but never offered for removal.

From the command line: `swift run devsweep runtimes` (or `--json`).

## Health: Security and Ports

- **Security** looks in Downloads, Desktop, Documents and your project folders
  for recovery codes, private keys outside `~/.ssh`, cloud service-account
  keys (even base64-encoded), AWS access-key CSVs, password-manager exports,
  and `.env` files. For `.env` files it asks git whether the file is
  committed, ignored, or one `git add .` away from being committed. Files are
  recognised by name and their first few kilobytes; secret values are never
  kept, logged or shown.
- **Ports** lists what's listening, started by you: dev servers, databases,
  Docker. It names the project folder, flags anything reachable from your
  network, and can stop a process (like Ctrl-C in its terminal).

From the command line: `swift run devsweep security` and `swift run devsweep ports`.

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

- More runtimes (PHP, Rust toolchains, .NET, Flutter) and conda environments
- Guided upgrades for MySQL and Redis
- Scheduled scans and a disk-space alert from the menu bar
- Running admin commands behind a single password prompt

## License

MIT
