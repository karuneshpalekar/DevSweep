# Windows parity and polish plan

Status: **approved 2026-11-16** (decisions in section 6). Building P0 first.

## 1. What was reported

After running 0.1.1 on a real PC, the user found four problems:

1. **Not all Mac features are in the Windows app.**
2. **The list of repos already on the PC is missing** ("I don't have what repos I have installed already").
3. **There is no search in the GitHub (Projects) area.**
4. **The UI is completely different from the Mac app and feels less polished.**

These came from a person using the app, not from CI. CI could not have caught them: every check passed, because the checks asked "does it work?", not "is it as complete and as pleasant as the Mac app?".

## 2. What the audit found

I compared the Mac app (`App/Views/*.swift`, README) with the Windows app (`windows/src/DevSweep.App`, `.ViewModels`) line by line. Windows has the engine; most of the gaps are in what the screens let you do.

### 2.1 Projects (the biggest gap)

| Mac has | Windows 0.1.1 has | Gap |
|---|---|---|
| **Show** filter: All / On this Mac / On GitHub, each with a count | A single list, local clones first | No filter, no counts |
| **Account** filter when 2+ accounts | none | missing |
| **Search** box (name, owner, description) | none | **missing (reported)** |
| **Get a project** menu: Add by URL, Publish a folder, Refresh | Refresh only | Add by URL and Publish exist in Core and the CLI, not in the app |
| Row: name, private lock, path or owner/description, status tag, size, "used 3d ago", **Download** button for GitHub-only repos | Name, path, size, safety badge, Push | No Download button, no lock, no description |
| **Detail side panel** (slides in): description, Open in editor, Show in Finder, Open on GitHub, a **"Is it safe to remove?" checklist** (pushed, uncommitted, untracked, stashes), a plain-language verdict, Push / Publish / Remove | none; the safety text is one badge | No panel, no Remove, no Publish |
| **Clone sheet** (blobless / shallow / full, size estimate), **Publish sheet** (secret check), **Add by URL sheet**, **Add account sheet** (identity first) | none | all four missing in the app (Core has them) |
| GitHub set-up card when not connected | A caption in Accounts only | Nothing guides the user |
| Local clones found "wherever they are" | Searches a fixed list of folders, 4 levels deep | **Likely cause of the missing repos** (see 2.2) |

### 2.2 Why "my installed repos" are missing

I can't see the user's screen, so these are the likely causes, in order of probability:

1. **Search folders.** Windows discovery (`ProjectFinder`) only looks in the workspace folder, folders added in Settings, and common names under the profile (`source`, `repos`, `code`, `src`, `dev`, `Documents\GitHub` and similar), 4 levels deep. Repos on `D:\`, in `C:\Work`, in a OneDrive folder, or deeper than 4 levels are not found. The Mac app has the same shape but Mac developers mostly use `~/Developer`, `~/Projects` or `~/Documents`; Windows developers scatter them across drives.
2. **GitHub CLI not installed or not signed in.** With no account, the page should still list every local clone; today the list may be empty or hidden behind errors with no explanation of what was searched.
3. **Module just switched on.** In 0.1.0 Projects was off; switching it on in Settings needs a restart.

Fix: show **what was searched and what was skipped**, a button to **add a folder**, and a **"Find all repos on this PC"** deep search across every fixed drive (skipping `node_modules`, `.git` internals, system folders and cloud-only files), with progress. Local clones appear even with no GitHub account.

### 2.3 Other feature gaps

| Area | Mac | Windows 0.1.1 | Plan |
|---|---|---|---|
| Home | Free space, cleanable, "since last week", needs attention, first-scan message, rule-pack errors | Equivalent content | Visual polish only |
| Clean up | Filter chips, search, group by risk, per-item action picker, ⓘ panel, "better option" | Present | Audit item by item, polish |
| Review | Groups by what happens, quit-first apps, exact commands | Present | Polish |
| History | Restore, quarantine | Present | Polish |
| Health: Tools and versions | Master list with version pills, side panel per tool ("What DevSweep noticed", Installed cards, "What you can do" steps), project requirements, **Homebrew report**, **guided PostgreSQL upgrade** | Cards with the same data, no side panel | Rebuild as list + side panel; **package-manager report for winget, Scoop and Chocolatey** (the Windows equivalent of Homebrew); guided PostgreSQL upgrade (see section 6) |
| Health: Security | Findings, reveal, move to Trash | List only | Add Show in Explorer, Move to Recycle Bin, Ignore |
| Health: Ports | Stop process, project name | Stop exists in Core, not in the app | Add Stop with the confirm step |
| Settings | Appearance, updates, open at login, schedule, alerts, **GitHub accounts (identity editor, Add account, Refresh)**, **Downloads folder**, project folders, ignored items, permissions and privacy text, **Troubleshooting (copy scan report)** | Appearance, folders, ignored, modules, login, schedule, alerts, updates, About | Add GitHub accounts, Downloads folder, Troubleshooting report, privacy text |
| Menu bar | Menu bar item | Tray (M5) | Match the Mac menu content, check on a real PC |
| CLI | `devsweep` | `devsweep.exe` | Already similar |

## 3. Why it feels less polished, and the approach

Causes, from the code:

- **Hand-built C# views with default controls.** There is no shared set of building blocks, so spacing, type sizes and states vary from page to page.
- **No list-plus-detail layout.** The Mac app's sliding side panel is the heart of how Projects, Clean up and Health feel; Windows uses stacked cards.
- **No motion.** The Mac app has one set of animation timings (`Motion`): panel slide, fade-in on screen change, sliding sidebar highlight. Windows has none.
- **No hover, pressed or selected states** on rows, no loading skeletons, thin empty states.
- **Nobody has looked at it.** The subagents could not view screenshots (the artifact download is blocked from the build environment), so polish was checked by assertions, not by eye.

**Approach: same information and features as the Mac, but Windows-native in look.** A Mac-styled app on Windows feels foreign (macOS traffic-light layout, SF-style spacing). Instead, keep the Mac's structure (the same places, tabs, panels, wording and flows) and express it in Windows 11 Fluent: Mica, Segoe UI Variable, rounded cards, accent colour, Segoe Fluent Icons, standard controls styled consistently. The user can overrule this (decision D1).

**Stay on WinUI 3 and Native AOT.** Switching stacks would throw away the working, tested engine and app shell. The polish comes from a design system layer, not a new framework.

### 3.1 The design system (built first, used by everything)

- **Tokens:** spacing scale (4/8/12/16/24/32), corner radii, a type ramp (Segoe UI Variable Display/Text), elevation and stroke, and the accent colour, all as theme resources for light, dark and high contrast.
- **Components:** page header, segmented control and tabs, card, list row with hover/pressed/selected states, **side panel with slide-in animation**, status tag and badge, command bar (search, filter, actions), empty state, loading skeleton, banner (info/warning), sheet/dialog frame, flow-wrap of buttons, `CheckRow` (the green tick / amber warning line).
- **Motion:** one set of timings (panel 380 ms spring-like ease, content swap 200 ms, screen change 220 ms, sidebar highlight slide), using WinUI implicit/composition animations; all of it switched off when Windows "animation effects" are off.
- **Icons:** one glyph map (Segoe Fluent Icons) so the same action has the same icon everywhere.

### 3.2 A visual feedback loop (the thing that was missing)

Polish needs eyes. Plan: the CI app job already renders every screen to PNG. Add a step that **commits the screenshots to a `windows-screenshots` branch**. I can then `git fetch` it and open the images myself, compare with the Mac screenshots in `docs/screenshots/`, and give the subagents concrete visual feedback. This works because plain git over the proxy works even though artifact downloads do not. The user can also drop their own screenshots into the chat at any time.

## 4. Milestones

Each runs as its own subagent on its own branch, CI-green before merge, as before. Two subagents at a time, because four at once hit the usage limit repeatedly.

| # | Milestone | Depends on | Delivers | Accepted when |
|---|---|---|---|---|
| **P0** | Design system + visual feedback loop | none | Section 3.1 tokens, components and motion; the screenshot-to-branch step; Home, Clean up, Review, History restyled on the new components as the proof; a parity checklist file (section 2 as a living table) | Screenshots on the `windows-screenshots` branch; I review them against the Mac screenshots; no regressions in existing tests |
| **P1** | Projects parity | P0 | Everything in 2.1 and 2.2 (deep search of all drives, WSL read-only listing, editor detection per section 6): Show filter with counts, account filter, **search**, Get a project menu, rows with Download, the detail side panel with the safety checklist and Open in editor / Show in Explorer / Open on GitHub / Push / Publish / Remove, all four sheets, GitHub set-up card, "what was searched" + Add folder + **Find all repos on this PC** | A test machine with several repos in different places and drives: every repo is listed with no GitHub account; with fake GitHub data, search/filters/counts are right; every action is reachable from the UI; removal is blocked unless safe |
| **P2** | Health parity | P0 | Tools and versions as list + side panel; package-manager report (winget, Scoop, Chocolatey); Security actions; Ports Stop (guided PostgreSQL upgrade is deferred, D4) | Each Mac Health capability has a Windows equivalent that is reachable from the UI |
| **P3** | Settings, Home and tray parity | P0 | GitHub accounts section (identity editor, Add account), Downloads folder, Troubleshooting report, privacy text; Home details; tray menu matching the Mac menu bar | Every Mac Settings section has a Windows equivalent |
| **P4** | Clean up, Review, History final pass | P0, P1 | Item-by-item parity audit of those screens, per-item action picker, "better option", animations everywhere, empty/loading states, keyboard and screen-reader pass, DPI 100-200%, high contrast | The parity checklist is all "done"; accessibility checks green |
| **P5** | Sign-off | P1-P4 | Re-run the M7 verification kit; new release candidate 0.2.0; the user's own walk-through using a short checklist | User approves; M7 kit has 0 FAIL |

Order: P0 → (P1 ∥ P2) → (P3 ∥ P4) → P5.

## 5. How parity will be tracked

`windows/docs/parity-checklist.md`, created in P0: one row per Mac feature (from the README, the Mac views and the table in section 2), with the Windows screen where it lives, status (not started / built / verified by screenshot / verified by the user) and the milestone. The sign-off in P5 is "no row below *verified*".

## 6. Decisions (agreed 2026-11-16)

- **D1. Look:** Windows-native Fluent look with the Mac's structure.
- **D2. Where repos live:** the user keeps repos in Downloads and under C:\ (including Program Files-style folders), and asked that DevSweep be built for **any developer, not for this one setup**. So discovery must not depend on a short list of friendly folder names: a **"Find all repos on this PC"** deep search over every fixed drive is part of P1 and runs on first use. It searches the whole drive, skipping only `C:\Windows`, `$Recycle.Bin`, `System Volume Information`, and the insides of `.git`, `node_modules` and other dependency/build folders; Downloads, Desktop, Documents, Program Files and other drives are included. Results are remembered so later refreshes are fast. It uses file metadata only and never opens cloud-only files. The result list always shows what was searched.
- **D3. Open in editor:** detect VS Code, Visual Studio, Rider and Cursor (and Windsurf); the user chooses a default in Settings; if none is found, open the folder in File Explorer.
- **D4. Guided PostgreSQL upgrade:** later, after 0.2.0.
- **D5. WSL repos:** included in P1 as **read-only listing** (found under `\\wsl.localhost\<distro>`, reached without starting a stopped distro); no removal or size checks are offered for them, only "Show in Explorer" and "Open on GitHub", and the row says why.
- **D6. Release:** 0.2.0 only once P5 passes; the first public release after that.

## 7. Risks

- **Usage limits.** Each milestone is large; two at a time and push-after-every-chunk, as now.
- **Visual quality can't be fully automated.** The screenshot branch plus the user's eyes are the safety net; budget one review round per milestone.
- **Scope creep.** The parity checklist is the scope. Anything not in it needs the user's approval.
- **Regressions in accuracy.** The M7 kit and all existing CI jobs stay on for every milestone.
