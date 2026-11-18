# Parity checklist (living)

One row per Mac feature. The sign-off in P5 is "no row below *verified*". Source of the rows: `docs/` (Mac README and views) and section 2 of `parity-and-polish-plan.md`.

Status: **not started** / **built** (in the code, CI green) / **verified-by-screenshot** (looked at in the `windows-screenshots` branch) / **verified-by-user** (the user used it on a real PC).
Milestone: P0 design system, P1 Projects, P2 Health, P3 Settings/Home/tray, P4 Clean up/Review/History final pass, P5 sign-off.

P4 (2026-11-17) opened the `windows-p4-polish` shots (details in `p4-polish.md`): rows below say which were looked at. Updated by P0 (design system): rows marked *built (P0)* are in; they move to *verified-by-screenshot* once reviewed against the Mac screenshots.

## Shell

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Sidebar with Home, Clean up, Projects, Health, History, Settings | NavigationView (Fluent), History and Settings in the footer | built (P0 polish: spacing, accent indicator, glyph map) | P0 |
| Selected item highlight slides between rows | NavigationView's own indicator slide | built | P0 |
| Sidebar volume card (free space, cleanable) | none (Home carries it) | not started | P3 |
| Sidebar "Needs your attention" cards | none (Home carries it) | not started | P3 |
| Clean up kind filters in the sidebar | Filter chips on the Clean up page | built (different place, same filters) | P4 |
| Update card | Sidebar update card (restyled) | built | P0 |
| Motion (panel 0.38 s, swap 0.2 s, screen 0.22 s) | `Design/Motion.cs`, off when Windows animation effects are off | built (P0) | P0 |
| Light, dark, auto; high contrast | Theme resources, title-bar theme button | built | P0 |

## Home

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Big free-space number, cleanable number, Review button | Home headline card | built (P0) | P0 |
| Volume bars with the cleanable segment | Home, one bar per volume | built (P0) | P0 |
| Since last week (Grew, New, Cleaned) | Home, list rows | built (P0) | P0 |
| Needs attention (4 most important, links) | Home, list rows | built (P0) | P0 |
| First-scan message, rule-pack errors | Home | built | P0 |
| Full Disk Access banner | n/a on Windows (no equivalent) | not started | P3 |

## Clean up

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Filter by kind with counts | Clean up, filter chips (wrap) | verified-by-screenshot (light) | P4 |
| Search | Clean up, search box | verified-by-screenshot (light) | P4 |
| Group by risk with Select all / Deselect all per group | Clean up, group headers | verified-by-screenshot (light) | P4 |
| Row: checkbox, title, what, size, confidence badge, ⓘ | Clean up, `ListRow` | verified-by-screenshot (light) | P4 |
| Per-item action picker (only with 2+ actions) | Row combo box or label | verified-by-screenshot (light) | P4 |
| ⓘ inspector as a sliding side panel, primary actions pinned in a footer | `SidePanel` (slide, cross-fade, Esc, focus return; the whole body when narrow) | verified-by-screenshot (light); pinned footer asserted by window geometry | P4 |
| Inspector sections (what it is ... where it is), action cards | Inspector | verified-by-screenshot (light, dark, high contrast). The Mac's "parts" list is not available (Core has no parts on Windows) | P4 |
| "Better option" callout | Inspector | built (the sample data has no item with one, so no screenshot shows it) | P4 |
| Quit-first blocking app with a Quit button | Inspector, Review rows | built (P4); the button is on screen in `inspector-blocked` and `review`, the real quit is not verified against a real app | P4 |
| Selection bar, Review N items (Review 0 items, disabled) | Clean up bottom bar | verified-by-screenshot (light) | P4 |
| Scan progress, per-check list, scan report | Clean up | built | P4 |

## Review and History

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Review sheet: groups by what happens, quit-first apps, exact commands, warnings | Review `SheetFrame` | verified-by-screenshot (light, high contrast) | P4 |
| Cleaning progress per item, outcomes with freed-space reconciliation | Review sheet | built (per-item progress is in the tour only as a state change; not shot); outcomes verified-by-screenshot (light) | P4 |
| History by day with Restore | History | verified-by-screenshot (light) | P4 |
| Quarantine size and Empty (with confirm) | History banner | verified-by-screenshot (light) | P4 |

## Projects (P1)

Status after P1: rows marked *verified-by-screenshot* were looked at in the `windows-p1-projects` shots (light and dark) by the coordinator on 2026-11-17; rows still *built (P1)* are built and test-covered but their screenshots have not been looked at yet (see `p1-projects.md`).

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Show filter All / On this PC / On GitHub with counts | Segmented control in the command bar | verified-by-screenshot (light, dark) | P1 |
| Account filter (2+ accounts) | Combo box in the command bar | verified-by-screenshot (light, dark) | P1 |
| Search (name, owner, description), Ctrl+F | Command bar search box | verified-by-screenshot (light, dark) | P1 |
| Get a project menu (Add by URL, Publish, Find all repos, Add folder, Refresh) | Drop-down in the command bar | built (P1); the closed button is in every list shot, the open menu was not shot | P1 |
| Rows: lock, path or description, status tag, size, used 3d ago, Download; virtualised | `VirtualList` of `ListRow`s | verified-by-screenshot (light, dark) | P1 |
| Detail side panel with "Is it safe to remove?" checklist and verdict | `SidePanel` | verified-by-screenshot (light, dark) | P1 |
| Open in editor (VS Code, Insiders, Visual Studio, Rider, Cursor, Windsurf), Show in Explorer, Open on GitHub, Push, Publish, Remove | Panel buttons; editor choice in Settings | built (P1) | P1 |
| Clone, Publish, Add by URL, Add account sheets | `SheetFrame` sheets | verified-by-screenshot (light) for Publish and Add account (P4) and Clone (P1); Add by URL built | P4 |
| GitHub set-up card (gh missing, not signed in) | Card above the list | verified-by-screenshot (light, gh missing) | P4 |
| Deep search of every drive, what was searched, Add folder, WSL | Consent card, progress card, search report, Settings | verified-by-screenshot for the consent card, progress card, search report and the WSL row; rest built (P1) | P1 |
| Cleanup tab, Accounts tab (identity editor), Activity tab (kinds, days) | Tabs | verified-by-screenshot (light) | P4 |

## Health (P2)

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Tools and versions: list with version pills, filters (All, Needs attention, Up to date), summary | Health, Tools and versions | verified-by-screenshot (light) | P4 |
| Tool side panel: What DevSweep noticed, Installed cards, What you can do steps, Not an install notes | Health, side panel | verified-by-screenshot (light, high contrast) | P4 |
| Projects that need it (requirements with OK, Not installed, End of life) | Health, "Your projects" row and per-tool section | built (verify by screenshot) | P2 |
| Package-manager report (Homebrew on Mac; winget, Scoop, Chocolatey), duplicates, caches, leftover folders | Health, "Package managers" row and panel | built; real winget, Scoop and Chocolatey output not verified on a PC | P2 |
| Security findings: Needs action / Looks fine, panel, reveal, move to Recycle Bin, ignore, .env git state and fix commands | Health, Security | built (verify by screenshot) | P2 |
| Ports: Development vs others, project folder, network warning, Stop (graceful then confirm, pinned), Open in browser, Copy URL, auto-refresh | Health, Ports | verified-by-screenshot (dark; values were unreadable before P4) | P4 |
| Health tab counts, last checked, off state with enable, skeletons, error banners, Home rows open the item | Health shell | built (verify by screenshot) | P2 |
| Guided PostgreSQL upgrade | deferred (D4) | not started | later |

## Settings, tray (P3)

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Appearance, updates, open at login, schedule, alerts | Settings tabs | built | P3 |
| GitHub accounts (identity editor, Add account, Refresh) | none | not started | P3 |
| Downloads folder | none | not started | P3 |
| Project folders, ignored items | Settings | built | P3 |
| Permissions and privacy text | none | not started | P3 |
| Troubleshooting (copy scan report) | none | not started | P3 |
| Menu bar item | Tray menu (M5) | built, to match the Mac menu and check on a real PC | P3 |
