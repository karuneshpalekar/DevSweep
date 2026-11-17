# P4: polish and Mac parity of Clean up, Review, History, Projects and Health

Branch `windows-p4-polish` (cut from `windows` at 0e92ca9). Written from the CI screenshots on branch `windows-screenshots` (`windows-p4-polish/`, light, dark and high contrast) and the Mac screenshots in `docs/screenshots/`. "Screenshot" in the tables names the shot in `windows-p4-polish/<light|dark|hc>/`.

P3 (Settings, Home, sidebar, title bar, tray) runs in parallel and is not touched here.

## Clean up: item-by-item audit against the Mac

| Mac behaviour | Windows before | Fix | Screenshot |
|---|---|---|---|
| Group headers with "Select all" that turns into "Deselect all" | Link always said "Select all" | Label follows the group's selection state (`GroupLinkText`, refreshed on every selection change); needs-admin groups have none, as on the Mac | `light/cleanup` |
| Per-item action picker only when there is more than one action, plain label otherwise | Present | Verified, unchanged | `light/cleanup` (npm cache has the picker, Gradle caches a label) |
| Labels "Delete / Move to Recycle Bin / Run command / Manual" | Present | Verified | `light/cleanup` |
| Size and confidence badge | Present | Verified ("Overview", "Shared links") | `light/cleanup` |
| Footer "Select items to clean. Nothing changes until you review and confirm." with "Review 0 items", disabled at 0 | Button said "Review" at 0 | Always "Review N items", disabled at 0 | `light/cleanup` |
| Filter chips with counts and sizes, wrap | Chips scrolled sideways and were cut off at the right edge | Chips wrap onto further lines (`FlowButtons`); selected chip text takes the button's own colour (white on the accent) | `light/cleanup` |
| Search | Present | Verified | `light/cleanup` |
| Inspector: what it is, why flagged, what was checked, if you clean it, you won't lose, before you do, better option, how to undo, what to do, locations, always ignore | Present, but "What to do" was plain radio buttons | Action cards (selected card has an accent border, 2 px, in high contrast the system highlight); whole card is clickable | `light/inspector`, `light/inspector-blocked` |
| Before you do: **Quit <app>** for each blocking app | Only a banner "close it first" | New `Quit <app>` button (`QuitButtons`, `IAppQuitter`): asks first, then closes the app's windows with `Process.CloseMainWindow` (a close request, like clicking the X) and polls for it to exit, up to six rounds so windows behind the first also get the request; never kills; an app that keeps running is reported as such | `light/inspector-blocked` |
| Manual (admin) item: command box, Copy, run | Buttons inside the scrolling body | Copy and Open in PowerShell pinned in the footer | `dark/inspector-admin` |
| (Windows only) pinned actions | Primary action only reachable by scrolling | Footer with "Select for cleaning" / "Deselect" and "Always ignore this" | `light/inspector` |
| Parts list ("13 Chrome profiles, untick any in Review") | not present | **Not applicable yet**: Core's `Finding` has no parts on Windows (the Mac's browser-profile parts come from the Mac detectors). Not added; needs a Core model change, left for a later milestone | none |
| Quit buttons in Review rows | not present | Same `Quit <app>` button on every blocked row; after an app closed, Review measures and plans again so the blocker disappears | `light/review` |
| Wide layout (1400 px) | n/a | The CI display is about 1044 x 788, so the window cannot reach 1400; the tour tries it and logs "wide: the window could only reach ..." Reasoned from code: at 1000 px and wider the panel sits beside the list (380 px) and rows have their full width | log only |

## Side panel (all panels)

- **Footer that never scrolls out of view** (`PanelFooter`, `PanelFrame`): Clean up inspector (select / deselect, ignore, or Copy and Open in PowerShell for manual items), Projects (the verdict, Push / Publish / Remove, or Download), Health security finding (Recycle Bin, Show in File Explorer, Ignore) and port (Stop). The body scrolls above it; the footer scrolls on its own only if it is taller than 280 px. Health's tool panel has no separate footer: its step cards are the content and start right after "What DevSweep noticed".
- The tour asserts, from the window geometry, that these buttons are inside the window with no scrolling (`pinned: ... is on screen at 735-767 of 779`), for the Clean up inspector (three kinds), the Projects panels (inventory-api, web-shop, scratch-notes, OneDrive) and the Health port and security panels. Screenshot: `light/projects-detail-unpushed` (verdict, Push and Remove at the bottom of a 779 px window; P1 had them below the fold).
- **Narrow windows**: below 1000 px of content the panel used to cover the right half of the list, hiding Health's version pills and Clean up's sizes. It now becomes the whole body (a detail view; Esc or the close button goes back). Beside the list at 1000 px and wider, unchanged. Keyboard order, Esc and focus return are unchanged and still asserted (the width assertion now compares with `OpenWidth`).

## Review sheet

| Mac behaviour | Windows | Status |
|---|---|---|
| Groups by what will happen | Blocked, Deleted right away, Cleaned by the tool's command, Recycle Bin, Quarantine, You run these yourself | Verified (`light/review`) |
| Apps that must quit flagged, Quit button | Row warning plus Quit button (above) | New |
| Exact commands | Per-row "Runs ...", and the "Exactly what will be done" expander (all commands, `(you run)` for manual) | Verified present; expander is below the fold in the 779 px shot |
| "Can't be undone" warning | Wrapping `InfoBar` at the top; each group also says it | Verified (`light/review`) |
| Exact re-measured sizes | Measured before the plan; the subheading says so | Verified |
| Progress with per-item status | A single bar plus a status line | New: every item listed as Waiting / Cleaning now (spinner) / Done (tick) while cleaning (`CleaningRows`, `RunTitles`, `CleanedCount`) |
| Outcomes with freed-space reconciliation, Done | Per-item outcome with "Shown X, expected X, C: actually freed X", total "Removed X" and the drive's figure | Verified (`light/review-done`, baseline run) |

## History

Compared `light/history` (and the dark shot) with `history-light.png` on the Mac. By day, time, title, action and item count, per-item original path and where it is now (Recycle Bin / Quarantine / Deleted), size, `Restore` or a status tag (Restored, Rebuilds itself, No longer in ...), quarantine banner with its size and `Empty quarantine...` (with a confirm), empty state "Nothing cleaned yet". Differences kept on purpose: Windows shows the item paths on every row instead of expanding on click, and says "removed in total" (the Mac says "freed" although the Trash still holds the bytes). No fix was needed.

## Projects: what the shots showed and what was fixed

| Finding | Fix | Screenshot |
|---|---|---|
| Large empty gap under the tab bar on the list tab | The list's stack added 8 px of spacing for every empty slot (notice, set-up, consent, deep-search cards); empty slots are now collapsed (`Ui.Fill`), and the three other tabs start 8 px under the bar instead of 24 | `light/projects-projects` |
| Panel's Push / Publish / Remove and the verdict below the fold | Pinned footer (above) | `light/projects-detail-unpushed` |
| Activity chips cut off ("Sig...") | Chips wrap | `light/projects-activity` |
| Add account sheet | Read: fields, helper text and buttons all fit, no clipping | `light/projects-sheet-account` |
| Publish sheet (secret warning) | Read: warning, "Publish anyway", disabled Publish; fits | `light/projects-sheet-publish` (baseline) |
| GitHub CLI missing card | Read: wording and buttons fine | `light/projects-gh-missing` (baseline) |
| OneDrive panel | Read: the verdict repeats the checklist line (the same sentence in the body and the footer). Left: it is the verdict the Mac shows too | `light/projects-detail-onedrive` |
| Search with no result | Read: "Nothing matches", Clear search | `light/projects-search-none` |
| Cleanup tab, Accounts, Activity | Read against the Mac's `idle`, `accounts`, `activity` shots: same structure (idle selector, safe-to-remove count, identity cards, activity by day) | `light/projects-cleanup`, `projects-accounts`, `projects-activity` |
| High contrast | New high contrast shots: list, panel, search with no result, Publish sheet, GitHub card, Cleanup, Accounts and Activity tabs | `hc/projects-*` |

Not opened (so not claimed): the dark Projects shots except through the contrast check, the LFS, no-remote, other-host and WSL panels, and the Clone, Add by URL sheets.

## Health

| Finding | Fix | Screenshot |
|---|---|---|
| Ports panel: Process / Started / Folder values were almost invisible on dark | Root cause, found by the contrast check's design: colours read from `Application.Current.Resources` in code are fixed to the app's theme at that moment (light), so "primary text" was near-black on the dark panel. Every such lookup (about 20: panel values, tick and warning icons, status dots, glyph tints, Remove buttons, activity icons) now goes through styles that use `ThemeResource` (`Ui.Tint`, `Ui.Dot`, `Ui.Button(danger: true)`), so they follow light, dark and high contrast | `dark/health-ports-selected` |
| Version pills hidden behind the panel at narrow widths | The panel becomes the whole body at narrow widths (above); filter chips and the legend wrap | `light/health-tools` |
| Boxy terminal glyph on the pill that a new terminal runs | Replaced with a small play triangle (`Icons.RunsHere`); the legend uses the same | `light/health-tools` |
| Pinned Stop / security actions | Footer (above) | `dark/health-ports-selected` |
| High contrast shots for all states | Added: Node panel, package managers, projects, `.env` finding, refused port, loading, not checked, failed, off and the three empty states | `hc/health-*` |

## Cross-cutting

- **Contrast check in the tour** (`Shots/ContrastCheck.cs`): for every shot, in light, dark and high contrast, every `TextBlock` and `FontIcon` is compared with what is painted behind it (solid fills of borders, panels, presenters, shapes and buttons, blended up the tree, with opacity). Body text must reach 4.5:1, large text (24 px, or 18.66 px bold) and icons 3:1. It is strict: any offender fails the tour (`DEVSWEEP_STRICT_CHECKS=0` makes it log only). Counted and skipped, not hidden: checked toggles (chips and segments; the fill comes from a visual-state animation that cannot be read back, white on the accent), and white glyphs inside system controls whose circle fill is not readable (InfoBar icons, check marks). Result of the last run before strict mode: 18,954 texts and icons checked, 144 skipped, 3 offenders, all of them the black glyph of an InfoBar icon in dark mode, which the rule for system glyphs now skips; no body text below 4.5:1 remained. Earlier runs found the white-on-white chip and button false alarms that led to the fill rules above; the real defects were the theme-fixed colours (Health values, icons), fixed in the toolkit, not per screen.
- **Keyboard order**: the tour dumps the Tab stops (tree order, as WinUI visits them) for 18 main screens in light and asserts none is unnamed for a screen reader: 0 unnamed controls. Example: Review is Quit Chrome, Exactly what will be done, Cancel, Clean N items; the Publish sheet is folder, name, account, description, Private, Public, Publish anyway, Cancel.
- **Loading and empty states**: Clean up (skeleton while scanning, "Not scanned yet", "Nothing to clean", "Nothing matches"), Projects (skeleton, off, failed, search with none), Health (skeleton, not checked, failed, off, three empty states), History (empty). All existed; verified in shots.
- **Motion**: all animation still goes through `Motion` (panel, swap, pulse); nothing new animates.
- **Colours**: no colour literal outside the token styles and Fluent theme resources remains in the app except the title bar's caption colours in `MainWindow.cs` (P3's area) and `Transparent`.

## Plumbing (additive)

`IAppQuitter` / `ProcessQuitter` and `MainViewModel.Quitter` (one line), `ReviewRow.QuitApps`, `ReviewViewModel.RecheckAsync`, `RunTitles` and `CleanedCount`, `Ds.PageScroll(content, top)`, `Ui.Fill`, `Ui.Tint`, `Ui.Dot`, `Ui.Button(danger)`, `Theme.DangerButton`, styles `DsChoice`, `DsChoiceSelected`, `DsIcon*`, `DsDot*`, `DsCaptionCaution`, `DsCaptionPrimary`. The workflow's side panel needle now accepts any width (`settles at \d+ px`). A flaky unit test (`DeepSearchTests.Progress_reports_folders_and_repositories` raced on a plain list and a fixed 200 ms wait) was made robust; nothing in Core changed.

## Not verified without a human

Quit buttons against a real Chrome, Edge or VS Code (the tour only checks the button is there; the confirm dialog and `CloseMainWindow` need a real app with windows), the wide layout, DPI 125-200 %, screen reader speech, real GitHub flows, and the dark and high contrast Projects shots that were not opened.
