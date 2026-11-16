# P0: the design system and the visual feedback loop

Look: Windows-native Fluent (Mica, Segoe UI Variable, rounded cards, accent colour, Segoe Fluent Icons), with the Mac app's information structure (same places, tabs, panels, wording and flows). Everything is built in C# on WinUI 3, Native AOT safe (no reflection, no XAML compiler), and every colour is a `ThemeResource` brush so light, dark and high contrast stay live.

Code: `windows/src/DevSweep.App/Design/` (tokens, motion, icons, components) and `Toolkit/Theme.cs` (the styles, as one XAML resource dictionary parsed at start-up).

## Tokens

| Token | Value | Where |
|---|---|---|
| Spacing | 4, 8, 12, 16, 24, 32 (`Tokens.Space1/2/3/4/6/8`, `DsSpace*` resources) | every margin, padding, gap |
| Page gutter | 32 px sides, 24 px top and bottom (`Ds.PageScroll`) | all pages |
| Radii | 4 (tags), 8 (cards, sheets), 12 (large), 6 for rows | `Tokens.Radius*`, `DsRadius*` |
| Type ramp | Display 44 / Title 28 / Subtitle 20 (Segoe UI Variable Display, semibold), Strong 14 semibold, Body 14, Caption 12 (Segoe UI Variable Text, the system default) | styles `DsDisplay`, `DsTitle`, `DsSubtitle`, `DsStrong`, `DsBody`, `DsSecondary`, `DsCaption`, `DsMono` |
| Strokes | 1 px card and control strokes from Fluent theme brushes; high contrast gets 1 px hover and 2 px selected borders | `DsSelectedBorderThickness`, `DsHoverBorderThickness` |
| Elevation | sheets and the overlay panel use a hairline stroke over the scrim (no shadows in high contrast) | `DsSheet`, `DsInspector` |
| Accent | selection bars, the Review/Clean buttons, the cleanable number and bar segment; never body text | `AccentFillColorDefaultBrush`, `AccentTextFillColorPrimaryBrush` |

## Components

All in `DevSweep.App.Design`. Every interactive control gets an automation name.

| Component | Use |
|---|---|
| `Ds.PageHeader(title, subtitle, actions...)` | `Ds.PageHeader("Clean up", subtitle, ScanButtons.Build(...))` |
| `Ds.Card(child)`, `Ds.RowsCard(rows)` | rounded surface; a card of rows with hairlines between them |
| `ListRow` | `new ListRow(grid, () => open(item)).Root` hover, pressed, selected (fill plus a thin accent bar), keyboard focus; `Selected = true` |
| `SidePanel` | `new SidePanel(listColumn, (key, subs) => detailView, "Details")`; `Select(key)` opens or switches, `Select(null)` closes; Esc closes; focus returns to the opener; overlays the list below 1000 px |
| `Ds.StatusTag(text, tone, tip)` | the small coloured tag (risk, Grew, Cleaned) |
| `Ds.CommandBar(filters, search, actions...)` | filter chips at the start, search and actions at the end, wraps when narrow |
| `Ds.Chip`, `Ds.Segmented`, `Ds.Tabs` | filter chips, a segmented control, tabs (the system `SelectorBar`) |
| `Ds.EmptyState(glyph, title, text, buttons...)` | nothing here yet |
| `Ds.Skeleton(rows)` | loading placeholder; pulses only when motion is on |
| `Ds.Banner(kind, text, title?, actionLabel?, action?)` | info, warning, critical (the system `InfoBar`) |
| `SheetFrame(width)` | dialog frame: heading, subheading, scrolling `Body`, footer with a message and `Buttons` |
| `FlowButtons.Build(buttons...)` | a row of buttons that wraps |
| `Ds.CheckRow(ok, text)` | green tick or amber warning line |
| `Ds.TwoColumns(left, right)` | two columns that stack below 860 px |
| `Ui.Size(...)` | the size with its confidence badges (unchanged) |

## Motion

One class, `Motion`, with the Mac's timings: panel 380 ms (a critically damped spring, no bounce), swap 200 ms, screen 220 ms, slide 350 ms. `Motion.Run(ms, ease, step, done)` plays a tween driven by the render loop, so layout properties (a width) animate with no storyboards.

- The navigation highlight slides by the NavigationView's own indicator.
- A new screen eases in (opacity and an 8 px lift); the first screen and sheets fade (200 ms).
- Motion is off when Windows "animation effects" are off (`SPI_GETCLIENTAREAANIMATION`), in screenshot mode (so shots are deterministic) and when `Motion.Force = false`. Off means every tween jumps to its end and calls its completion at once.
- Tests set `Motion.Force` to check both paths (see below).

## Icons

`Icons` is the one glyph map (Segoe Fluent Icons). Views never write a code point. Add a name there first.

## How to add a screen

1. Add a `Place` in the view model (id, title) and a `Build(model, subs, window)` in `Views/` returning a `UIElement`.
2. Compose it from the components: `Ds.PageScroll(Ui.V(Tokens.Space4, Ds.PageHeader(...), Ds.CommandBar(...), list))`. A list with details is `new SidePanel(...)`; return `panel.Root`, and call `subs.Add(panel.Dispose)`.
3. Subscribe with `subs.On(vm, nameof(...), update)`; subscriptions are disposed when the page is replaced.
4. Add the id to `MainWindow.ShowPage` and to the tour (`ScreenshotTour.WalkAsync`) so it appears on the screenshot branch in light, dark and high contrast.
5. Add a row to `parity-checklist.md`.

## The screenshot branch

The CI `app` job renders the tour (`DEVSWEEP_SHOTS`) and then runs `windows/scripts/Publish-Screenshots.ps1`, which pushes the PNGs to the branch `windows-screenshots` with the workflow's `GITHUB_TOKEN` (the job has `contents: write`; a push made with that token does not start workflows). It runs on pushes to `windows`, `windows-m*` and `windows-p*`.

Layout: `<source-branch>/<light|dark|hc|misc>/<screen>.png`, `<source-branch>/shots-log.txt` (the tour log) and `<source-branch>/README.md` (commit SHA, run URL). Only the source branch's folder is replaced; other branches' folders stay. The branch is a single commit (no history, so it does not grow), and a concurrent run is handled by re-reading the branch and pushing with `--force-with-lease`, up to six attempts.

Looking at it:

```
git fetch origin windows-screenshots
git show origin/windows-screenshots:windows-p0-design/light/home.png > /tmp/home.png
# or: git archive origin/windows-screenshots windows-p0-design | tar -x -C /tmp/shots
```

Shots are taken in screenshot mode: solid background instead of Mica (Mica is not part of a render-target capture), motion off, sample data. The window on the CI runner is limited by its small virtual screen (about 1030 x 780 px), so the shots show the layout at a narrow width, which is the hard case. A 150 % DPI run is not possible there: the window's DPI follows the monitor, and changing the display scale needs a sign-out.

## Checks

The tour log (`shots.log`) carries the assertions the workflow greps for: the side panel opens at 380 px, Esc closes it, the last item stays while it closes, focus returns to the ⓘ button, the motion-off path settles at once, the motion-on path starts a tween and settles, and the panel cross-fades to another item. The existing navigation bounds, theme, icon and smoke assertions are unchanged.
