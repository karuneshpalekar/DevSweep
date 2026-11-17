# P1: Projects parity

Branch `windows-p1-projects`, merged into `windows` after run 44041352003 passed all four jobs (build and test, WinUI app, installer, M7 kit). Written by the coordinator from the CI logs and the screenshots on branch `windows-screenshots` (`windows-p1-projects/`).

## What it adds (Mac parity, plan 2.1)

- **List tab:** Show filter All / On this PC / On GitHub with counts, account filter, search (name, owner, description; Ctrl+F), a "Get a project" menu (Add by URL, Publish a folder, Find all repos on this PC, Add folder, Refresh), a virtualised list (5,014 projects: 12 rows built in about 200 ms, search about 1 ms), rows with lock, path, status tag, size and last used, and a Download button for GitHub-only repos.
- **Detail side panel:** description, Open in editor (VS Code, Insiders, Visual Studio, Rider, Cursor, Windsurf, or File Explorer), Show in File Explorer, Open on GitHub, the "Is it safe to remove from this PC?" checklist (commits not pushed, uncommitted files with names, files outside git, stashes, other worktrees, submodules) with "compared as of the last fetch" and Fetch now, the plain-language verdict, and Push / Publish / Remove. WSL repos are read-only (Show in Explorer and Open on GitHub only).
- **Sheets:** Clone (blobless, shallow or full with size estimates, destination, account, commit identity), Publish (secret check first), Add by URL, Add account.
- **Cards:** GitHub CLI missing, not signed in, first-run consent for the deep search, deep-search progress with Cancel, "Searched every folder on C: and D: (482,113 looked at) - 11 repos" with What was searched / Add folder / Find all repos.
- **Other tabs** restyled: Cleanup, Accounts, Activity.
- **Settings:** default editor, downloads folder, removable drives for the deep search. Folders added in Settings now add to the default search folders instead of replacing them.

## Deep search for any developer (decision D2)

Metadata-only, parallel walk of every fixed drive (removable drives only if ticked). It prunes dependency and system folders by name, never enters `.git`, links or cloud-only folders, lists worktrees, counts bare repos, lists non-GitHub origins as "other host" with credentials redacted, and caches results in `projects.json`. WSL distros are searched only if already running, read-only.

Numbers on the GitHub Windows runner (hosted VM, run 44041352003):

| Run | Drive C: | Time | Rate | Repos |
|---|---|---|---|---|
| Accuracy test, administrator, first (cold) walk | 148,843 folders | 383.9 s (6 min 23 s) | 388 folders/s | 27 |
| CLI step, administrator, warm | 148,996 folders (C: and D:) | 2.1 s | 70,745 folders/s | 32 |
| Accuracy test, standard user, warm | 147,555 folders | 2.2 s | 68,438 folders/s | 27 |

The cold first walk is slow on the runner's virtual disk; warm walks are fast. A real PC's SSD will differ, which is why the search runs in the background with progress and Cancel, keeps partial results, and later refreshes re-check known repos first.

## What the screenshots showed (coordinator review)

Viewed (light and dark): list, filters with counts, search, rows (lock, path, status tags, size, age, WSL and GitLab rows, a repo in Downloads), detail panel for an unpushed and a safe repo, the Clone sheet, the deep-search progress card. Matching the Mac structure; Windows-native look.

Not matching yet / open:
- A large empty gap under the tab bar on the list tab.
- At about 1030 px wide the panel overlays the list; the verdict and the Push / Publish / Remove buttons sit below the fold and need scrolling.
- Not yet looked at: the Publish, Add by URL and Add account sheets, the GitHub setup cards, the Cleanup, Accounts and Activity tabs, the other repo kinds' panels (no remote, LFS, OneDrive, other host, WSL), and high contrast.

## Deviations from the Mac

- Removing a project that is not fully backed up is **blocked** (the dialog lists what would be lost); the Mac offers "remove anyway".
- `SafetyGuard` would allow removing a fully safe repo under Program Files; the guard was not changed.

## Not verified without a human

Real GitHub accounts, `gh` install and sign-in through the visible PowerShell window, editor detection on a real machine, deep search on a real developer PC and on WSL, OneDrive cloud-only folders.
