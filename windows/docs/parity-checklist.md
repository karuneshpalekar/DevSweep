# Parity checklist (living)

One row per Mac feature. The sign-off in P5 is "no row below *verified*". Source of the rows: `docs/` (Mac README and views) and section 2 of `parity-and-polish-plan.md`.

Status: **not started** / **built** (in the code, CI green) / **verified-by-screenshot** (looked at in the `windows-screenshots` branch) / **verified-by-user** (the user used it on a real PC).
Milestone: P0 design system, P1 Projects, P2 Health, P3 Settings/Home/tray, P4 Clean up/Review/History final pass, P5 sign-off.

Updated by P0 (design system): rows marked *built (P0)* are in; they move to *verified-by-screenshot* once reviewed against the Mac screenshots.

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
| Filter by kind with counts | Clean up, filter chips | built (P0) | P0 |
| Search | Clean up, search box | built | P0 |
| Group by risk with Select all per group | Clean up, group headers | built (P0) | P0 |
| Row: checkbox, title, what, size, confidence badge, ⓘ | Clean up, `ListRow` | built (P0; the risk tag is the group header, as on the Mac) | P0 |
| Per-item action picker | Row combo box | built | P4 |
| ⓘ inspector as a sliding side panel | `SidePanel` (slide, cross-fade, Esc, focus return) | built (P0) | P0 |
| Inspector sections (what it is ... where it is) | Inspector | built | P4 |
| "Better option" callout | Inspector | built | P4 |
| Quit-first blocking app | Inspector banner | built; the "Quit" button is not started | P4 |
| Selection bar, Review N items | Clean up bottom bar | built | P0 |
| Scan progress, per-check list, scan report | Clean up | built | P4 |

## Review and History

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Review sheet: groups by what happens, exact commands, warnings | Review `SheetFrame` | built (P0) | P0 |
| Cleaning progress, outcomes with freed-space reconciliation | Review sheet | built (P0) | P0 |
| History by day with Restore | History | built (P0) | P0 |
| Quarantine size and Empty | History banner | built (P0) | P0 |

## Projects (P1)

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Show filter All / On this PC / On GitHub with counts | none | not started | P1 |
| Account filter | none | not started | P1 |
| Search (name, owner, description) | none | not started | P1 |
| Get a project menu (Add by URL, Publish a folder, Refresh) | Refresh only | not started | P1 |
| Rows: lock, path or description, status tag, size, used 3d ago, Download | partial | not started | P1 |
| Detail side panel with "Is it safe to remove?" checklist and verdict | none | not started | P1 |
| Open in editor, Show in Explorer, Open on GitHub, Push, Publish, Remove | partial | not started | P1 |
| Clone, Publish, Add by URL, Add account sheets | none | not started | P1 |
| GitHub set-up card | none | not started | P1 |
| Deep search of every drive, what was searched, Add folder | none | not started | P1 |

## Health (P2)

| Mac feature | Windows screen | Status | Milestone |
|---|---|---|---|
| Tools and versions: list with version pills and side panel | cards | not started | P2 |
| Package-manager report (Homebrew on Mac; winget, Scoop, Chocolatey) | none | not started | P2 |
| Security findings: reveal, move to Trash | list only | not started | P2 |
| Ports: Stop process | none in the app | not started | P2 |
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
