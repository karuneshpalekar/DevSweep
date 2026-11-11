# M3c: Projects

The Mac Projects place, ported to Windows as the `projects` module
(`DevSweep.Core/Projects`, `devsweep projects`). On by default since 0.1.1
(`module:projects`, `EnabledByDefault = true`; it was off in 0.1.0); turn it off with
`DEVSWEEP_FEATURES=-module:projects`, `"disabled": ["module:projects"]` in
`%LOCALAPPDATA%\DevSweep\features.json`, or Settings, Modules.

## What it does

| Mac feature | Windows |
|---|---|
| Every project in one list | Clones found under the workspace root (`%USERPROFILE%\Code` by default), Settings' folders, the common roots of M2's `staleProjectArtifacts` rule (`source`, `repos`, `Projects`, `code`, `src`, `dev`, `Documents\GitHub`, JetBrains/Android Studio folders...) and every folder a clone was seen in before; matched to GitHub by `origin` (https, `git@github.com:`, `ssh://`, and SSH host aliases from `~\.ssh\config` whose `HostName` is github.com), wherever the folder is; folders with no GitHub origin; removed repositories stay listed with Download |
| Safe removal | `SafetyChecker` (below). Only `Safe` may be removed: through the M1 `Cleaner` (Trash action: Recycle Bin when it will keep the folder, else DevSweep's quarantine; safety guard; Restart Manager; History; freed-space reconciliation). A removed project downloads back into the folder it lived in |
| Download with a strategy | blobless (`--filter=blob:none`), shallow (`--depth=1`), full; size estimate = GitHub size x the Mac factors (0.35 / 0.12 / 1.0) |
| Per-account identity | `git config --local user.name/user.email` in the new clone (never `--global`) |
| Accounts without switching | Each account's own token (`gh auth token --hostname github.com --user <login>`) for every call; `gh auth switch` is never run by DevSweep |
| Publish a folder | Secret check on exactly the files git would add (`.gitignore` respected; a scratch repository elsewhere lists them, so nothing changes until confirmed), then `git init`, identity, first commit, `POST /user/repos` or `/orgs/{org}/repos`, push as that account |
| Add any repo by URL | `GET /repos/{owner}/{name}` with each signed-in account until one can see it |
| Cleanup | Untouched 1 week to 3 months (7..90 days, 21 by default, saved), biggest first, safe vs reasons; sizes re-measured exactly with M2's `FindingRemeasurer` (streams counted, hard links to outside not freeable) |
| Accounts | Every account gh knows, which is active, repositories per account, what it uses on this PC, identity (missing ones flagged), clones from owners nobody here is signed in as |
| Activity | Downloads, removals, publishes, pushes, added repos, identity changes, sign-ins, user-triggered fetches; newest first, last 500 |
| Push / sign-in in Terminal | A PowerShell script that lists every command, waits for Enter, runs and shows each, stops at the first failure; opened in Windows Terminal (`wt.exe` on PATH) else a Windows PowerShell window; never elevated |

## Safety check (`Projects/Git/SafetyChecker.cs`)

git is found as a full path (PATH entries that are not absolute are skipped, then Git for
Windows' install folders) and run with no shell, a time limit and only `-c` settings:
`core.fsmonitor=false`, `core.longpaths=true`, `core.quotepath=false`, no colour;
`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `GIT_OPTIONAL_LOCKS=0`,
`GIT_NO_LAZY_FETCH=1` (a partial clone never downloads during a check); `GIT_DIR` and
friends inherited from a shell are removed. No config file is ever written by a check.

| Condition | How | Result |
|---|---|---|
| No remote | `config --get-regexp ^remote\..*\.url$` | NotSafe ("the only copy") |
| Commits not on a remote | `rev-list --count --branches HEAD --not --remotes` (detached HEAD included), then per branch | NotSafe, naming the branches |
| Remote-tracking refs stale | Fetch-free by default (the check says "as of the last fetch" with FETCH_HEAD's time); `--fetch` runs `git fetch --all --prune` as the project's account | |
| Commits only on tags | `rev-list --tags --not --branches --remotes HEAD` | Unknown (can't tell offline whether the tag was pushed) |
| Changed / conflicted / untracked | `status --porcelain=v2 -z --untracked-files=all --ignore-submodules=none` (.gitignore respected) | NotSafe, with samples |
| CRLF noise | git's own status decides: autocrlf churn git calls clean is clean. Files git calls changed whose only difference is CR at end of line (`diff --numstat --ignore-cr-at-eol`) are still blocking (the bytes differ) but named "differ only in line endings" | |
| Ignored files | `--ignored=matching`; not blocking (git was told to ignore them) but non-rebuildable ones (`.env`, local settings) are listed | |
| Stashes | `stash list` | NotSafe |
| Worktrees | `worktree list --porcelain`: another worktree, or being a linked worktree (submodule repositories under `.git\modules` excluded) | NotSafe |
| Submodules | each checked-out `160000` entry checked recursively (3 levels) | NotSafe, naming the submodule |
| Unfinished operation | `rebase-merge`, `rebase-apply`, `MERGE_HEAD`, `CHERRY_PICK_HEAD`, `REVERT_HEAD` | NotSafe |
| Git LFS | `filter=lfs` in `.gitattributes` or `.git\lfs\objects` | Unknown: whether every LFS object is on the server can only be answered by the server |
| Case-only renames | with `core.ignorecase=true`: every tracked path compared component by component with the names on disk (metadata-only listings) | NotSafe ("Readme.md is now README.md") |
| Case collisions | index paths equal ignoring case | NotSafe |
| Long paths | tracked files whose full path is 260+ characters, and `core.longpaths` from the repository's own config (`--show-scope`, not our `-c`) | reported; downloads always set `core.longpaths=true` in the clone |
| Files held open | Restart Manager (M1 `FileLocks`) before a removal | NotSafe ("Close X first") |
| OneDrive | the M1 walker measures the clone; any cloud-only placeholder means git is not run at all (it would hydrate files); `origin` read from `.git\config` only if that file is local | NotChecked ("In OneDrive, not checked") |
| git fails | any error, time-out or truncated output | Unknown, never Safe |

Discovery never enters junctions, symlinks or mount points (`ReparseGuard`, reparse attribute);
OneDrive folders are entered only when their contents are on disk (cloud reparse tag, not a
placeholder); dot-folders and dependency/build folders are skipped; 4 levels deep.

## GitHub (`Projects/GitHub`)

- Accounts: `gh auth status --hostname github.com` merged with `hosts.yml` in
  `%GH_CONFIG_DIR%` or `%APPDATA%\GitHub CLI` (gh 2.40+ `users:` map and the older single
  `user:`; token values in the file are never read out). gh runs with `GH_TOKEN`,
  `GITHUB_TOKEN` and the enterprise variables removed (with one set, gh reports that token
  instead of the account asked for), no prompts, 30 s limit. Logins are validated
  (`^[A-Za-z0-9](-?[A-Za-z0-9])*$`, 39 max) so one can never be read as an option.
- Tokens: `GitHubToken` (redacted `ToString`), only in an Authorization header or a child's
  environment. The API client refuses to send a token to any host but its own (a `Link`
  header pointing elsewhere fails).
- API: `HttpClient`, source-generated snake_case JSON (`GitHubJsonContext`).
  `user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=pushed`,
  every page via `Link: rel="next"` (URLs contain commas: parsed between angle brackets),
  archived left out, newest push first; a repository two accounts can see is listed once,
  under its owner's account when signed in, else the first (`MergeAccounts`). Rate-limit
  and error bodies become messages.
- Credentials for clone, fetch and push: `-c credential.helper= -c "credential.helper=!'<gh.exe>' auth git-credential"`
  with that account's token in `GH_TOKEN` in git's environment only. No config file, no
  token on a command line, active account unchanged. Clones get `--config core.longpaths=true`.
- Sign-in: `gh auth login --web`; gh makes a newly added account active, so when another was
  active the script runs `gh auth switch --user <previous>` right after (shown in the window).

## Publish secret check

`ISecretCheck` (`Projects/Actions/SecretCheck.cs`), implemented by `HealthSecretCheck`, which
applies M3b's `SecurityScanner` (merged before this branch finished) to the files a publish
would upload: private keys, recovery codes, service-account and cloud keys, password exports,
certificates, PuTTY keys, `.rdp`/`.publishsettings`, `.env` files with secret-looking values.
A folder with files that look like secrets is refused unless the user confirms
(`--publish-secrets`); confirmed publishes are recorded in Activity.

## Storage

`%LOCALAPPDATA%\DevSweep\projects.json` (`ProjectStore`): workspace root, extra roots, idle
days, identities, strategies, last opened, known repositories (account, size, last folder,
removed date), activity. Source-generated JSON, M1 `AtomicFile` writes, an unreadable file set
aside (never overwritten), missing fields filled on load (`Normalised`: source-generated JSON
sets missing init-only properties to null, found by the unit tests).

## CLI

```powershell
$env:DEVSWEEP_FEATURES = '+module:projects'
devsweep projects [--json] [--root D:\src] [--offline]
devsweep projects check <folder> [--fetch [--account a]] [--json]   # exit 0 safe, 1 not safe or unknown
devsweep projects cleanup [--days 21] [--root ...] [--fast] [--json]
devsweep projects remove <folder> [--yes] [--json]                    # only when safe
devsweep projects download owner/name [--strategy blobless|shallow|full] [--to folder] [--account a] [--dry-run]
devsweep projects add <url or owner/name>
devsweep projects identity <account> [--name n --email e]
devsweep projects publish <folder> --account a [--owner org] [--name n] [--public] [--check] [--publish-secrets]
devsweep projects push <folder>                                       # visible window
devsweep projects sign-in [--user u --name n --email e]               # visible window
devsweep projects accounts [--json]
devsweep projects activity [--json]
```

Exit code 3: Projects or that tab is turned off (same as Health).

## Flags and the app (M4)

`module:projects`, plus `module:projects.accounts` (off: purely local, gh and the network
never used), `module:projects.cleanup`, `module:projects.activity` (Health's naming). The
module registers `ProjectsFeatures`; the app creates `ProjectsService.Create(engine, http)` and
binds to immutable results: `ProjectsSnapshot` (projects, accounts, account summaries, outside
owners, search report, messages), `Project` (`StatusText`, `StatusLevel`, `LastUsed`),
`RemovalSafety` (`State`, `Headline`, `Blockers`, `Checks` for "What was checked"),
`IdleCleanupResult`, `DownloadPlan` (the exact git arguments and estimate, for the sheet),
`PublishPreview`, `RemoveResult`, `TerminalLaunch`, `ActivityEntry`. Test doubles:
`FixedGitHubCli`, `IGit`, `IItemMeasurer`.

## Tests

- Unit (`DevSweep.Core.Tests/Projects`): slugs and SSH aliases, `gh auth status` and
  hosts.yml, porcelain v2, strategies and the download plan, per-command credentials,
  terminal scripts, the GitHub API against recorded responses (pagination, archived,
  headers, 404, errors, rate limit, token never sent elsewhere, create for user and org,
  account merge), merge and destinations, the idle list, the store, module flags, add by URL.
- Accuracy (`DevSweep.Accuracy/Projects`, real git repositories with a local bare repository
  as origin): every unsafe condition above, a fully pushed repository safe, CRLF-only churn,
  case-only rename, a 300+ character path, worktrees both ways, a submodule, LFS, a file held
  open, an unfinished merge, tag-only commits, fetch with prune, discovery (junction not
  followed, node_modules skipped), OneDrive placeholders never hydrated, removal through the
  cleaner and download back into the same folder with the identity in the clone and the global
  config unchanged, publish behind the secret check.
- Live: one read-only API call with the workflow's `GITHUB_TOKEN`
  (`GitHubLiveSmokeTests`, its own CI step); skipped everywhere else.
- CI smoke (`Smoke test devsweep projects`): module off gives 3; list, check, exact cleanup,
  refusal to remove a project with unpushed work, removal of a safe one into History.

## Numbers from CI

Run [42057762178](https://github.com/karuneshpalekar/DevSweep/actions/runs/42057762178)
(`windows-latest`, git 2.56.0.windows.4, gh installed but not signed in): unit tests 447 passed,
1 skipped (the live test, which runs in its own step); accuracy suite 194 tests, all passed as
administrator; as a fresh standard user 189 passed, 0 failed, the same 5 M1 skips. All 21
Projects accuracy tests ran in both (no Projects skips: the Cloud Files filter, Restart Manager
and `core.ignorecase=true` were available to both accounts).

| Case | Result |
|---|---|
| Fully pushed (local bare origin) | Safe; "the remote is a folder on this PC" noted |
| Unpushed on main, then also on `feature` | NotSafe, 1 then 2 commits, branches named |
| Changed + untracked, then stash | NotSafe (3 lines), then 1 stash; safe after drop and push |
| No remote / detached HEAD commit | NotSafe ("only copy") / 1 commit on "detached HEAD" |
| `.env`, `node_modules`, `*.log` ignored | Safe; `.env` listed as ignored, artifacts not |
| Main worktree + linked worktree | both NotSafe, each naming the other; Safe after `worktree remove` |
| Submodule with a commit on a new branch | NotSafe "Submodule vendor/lib: 1 commit..." (a bug found: git lists a submodule's `.git\modules` folder as its main worktree; fixed) |
| `.gitattributes` with `filter=lfs` | Unknown |
| autocrlf=true checkout, stat-dirty | Safe (git says clean) |
| LF file rewritten with CRLF, autocrlf off | NotSafe, "1 differ only in line endings"; a real edit: 0 |
| `Readme.md` renamed to `README.md` | git status empty; NotSafe "Readme.md is now README.md" |
| 300+ character path | Safe, 1 long path; an edit to that file is seen |
| File held open | "Close Windows PowerShell (powershell.exe, PID 4480) first" (admin); "Close the program using ...\a.txt first (Windows did not say which program it is)" (standard user, Restart Manager denied) |
| Merge conflict / not a repository | NotSafe ("merge", 1 conflict) / Unknown |
| Commit only on a tag | Unknown |
| Branch deleted on the server | Safe without fetch (stale ref, said so); NotSafe after `--fetch` (pruned) |
| OneDrive sync root with one placeholder | NotChecked "In OneDrive, not checked: 1 file is only in the cloud"; provider got 0 FETCH_DATA requests, placeholder still 0 bytes on disk |
| Remove + download | the shell refused to recycle the clone in %TEMP% (git's read-only objects, `0x78`); the cleaner now quarantines an intact item instead; History entry; listed as removed; downloaded back into the same folder with `user.name`/`user.email` and `core.longpaths` in the clone's config, no credential settings, global config byte-identical; Activity: Download, Remove, Identity |
| Publish | files git would add: `.gitignore`, `app.py`, `deploy_key.pem` (an ignored `.pem` not listed); refused until confirmed; pushed commit authored "Octo Cat <octo@example.com>" |
| Live API (`GITHUB_TOKEN`) | `karuneshpalekar/DevSweep`, public, 86,335 KB |

CLI smoke (administrator, `D:\a\_temp`): with Projects off, exit 3; two clones of 3,210,704 and
3,210,480 bytes, Safe and NotSafe ("1 commit not pushed · 1 untracked file"); `check` exit 0 / 1;
`cleanup --days 7` both listed with exact sizes, only the pushed one safe; `remove` refused the
dirty one (folder kept) and moved the safe one to `D:\$Recycle.Bin\<SID>\$R5G6FG4` with a History
entry; with `-module:projects.cleanup`, `cleanup` exits 3.

## Decisions and deviations

- Shared code touched: `Cleaning/CommandRunner.cs` (an additive overload with environment
  variables, an output cap and a working folder) and `Cleaning/Cleaner.cs` (when the shell
  refuses to recycle an item that is still whole, it goes to the quarantine instead of failing;
  a partly recycled item is reported and left alone).
- Downloads into folders that do not exist yet are checked for links only on the existing part
  of the path (M1's `ReparseGuard` treats an unreadable ancestor as a possible link).

- Git LFS repositories are `Unknown` (never removable through DevSweep): confirming LFS objects
  on the server needs the server, and `git lfs push --dry-run` does not answer that reliably.
- Files git reports as changed only in line endings still block removal (the bytes on disk
  differ from what a download gives); they are named as such so the user can renormalise.
- Commits reachable only from the reflog (a branch deleted locally) are not counted, as on Mac.
- Ignored files never block (git was told to ignore them); non-rebuildable ones are listed.
- A project root that is itself a junction is not searched (links are never traversed).
- The GitHub API is used directly (`HttpClient`) instead of `gh api`, so JSON is source-generated
  and errors are typed; gh is still the only source of tokens.
- RepoShelf import (Mac only) is not ported.
