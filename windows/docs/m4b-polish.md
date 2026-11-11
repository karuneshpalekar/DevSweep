# M4b: 0.1.1 polish

Three problems reported from a real install of 0.1.0 (built from `windows` at 7f6e562), and what changed.
Everything stays Native AOT safe; the C# views and view models are the same ones M4 and M5 built.

1. The icon was not the DevSweep logo.
2. Only Home, History and Clean up were visible in the navigation; Settings, Projects and Health were not.
3. There was no obvious light/dark switch (Settings, General, Appearance, which could not be reached).

## 1. The real logo everywhere

`docs/icon.png` (1024 x 1024, the same artwork as the Mac app) is the source. `windows/eng/make-icons.py`
(Pillow) writes two committed files in `src/DevSweep.App/Assets/`, so building needs no Python:

| File | What | Used by |
|---|---|---|
| `DevSweep.ico` | 16, 24, 32, 48, 64, 128, 256 px | embedded in `DevSweep.exe` (`<ApplicationIcon>`), the window and taskbar icon, the tray icon, installer, shortcuts |
| `DevSweep.png` | 256 px | the title-bar logo and the toast logo |

Both are `Content` items, published to `Assets\` next to the exe (published folder, portable zip and
installer all carry them).

| Place | How |
|---|---|
| Explorer, taskbar pin, Alt+Tab, shortcuts | `<ApplicationIcon>` embeds the .ico in the AOT exe; the Start Menu and desktop shortcuts use `IconFilename={app}\DevSweep.exe, IconIndex 0` |
| Window and taskbar while running | `AppWindow.SetIcon(Assets\DevSweep.ico)`; if the window still has no big and small icon afterwards (`WM_GETICON`), `WM_SETICON` with `LoadImage` handles |
| Title bar | an `Image` (`BitmapImage` from `Assets\DevSweep.png`), not a glyph |
| Tray | `LoadImage(Assets\DevSweep.ico)` at the tray's small-icon size; the drawn disc and broom (`AppIconImage`) is only a fallback when the file is missing, and the smoke test fails if it is used |
| Toasts | the same PNG, copied to the data folder as before |
| Installer | `SetupIconFile`, `UninstallDisplayIcon={app}\DevSweep.exe,0` |

## 2. Every place in the navigation

Root cause of the missing Settings item (not provable without the user's machine; this is the cause the
code and the report point to): `MainWindow` opened at a fixed 1240 x 820 **pixels** (`AppWindow.Resize`), and
the navigation put History and Settings in the pane's footer, at the bottom of that window. On a work area
shorter than the window (a 1366 x 768 laptop has 728 px of work area; a scaled 1080p screen has fewer
device-independent pixels still) the bottom of the window is off the screen, so Home, Clean up and History
showed and Settings, the last footer item, was below the screen edge. Health and Projects were missing for
a second, simpler reason: both modules were off by default.

Fixes:

- **The window fits the screen.** `WindowSizing.Fit` (view-model project, unit tested) sizes the window in
  device-independent pixels, never bigger than the monitor's work area, centred, with a minimum of
  1000 x 680 (the minimum also shrinks to a tiny work area rather than forcing the window off screen).
- **Settings is the built-in item** (`IsSettingsVisible = true`, `args.IsSettingsSelected` goes to
  "settings"), in the standard bottom-left spot, plus a **gear button in the title bar**.
- **The update card cannot push items out**: it sits in a `ScrollViewer` with a maximum height.
- **Health and Projects are on by default** (`EnabledByDefault = true`); a module switched off in Settings,
  Modules, `features.json` or `DEVSWEEP_FEATURES=-module:health` keeps its place hidden as before.

Places now: Home, Clean up, Projects, Health in the main list; History and Settings at the bottom.

## 3. The theme button

A button at the right of the title bar (before the window buttons) shows a sun, a moon or a monitor and the
word System, Light or Dark. Each click goes System, Light, Dark, System through
`SettingsViewModel.CycleAppearance`; Settings, General, Appearance is bound to the same
`SettingsViewModel.Appearance` (both ways), which saves to `app-settings.json`. Tooltip and automation
name: `Theme: <mode>, click to change`. A change re-themes the content, the title bar (part of the content),
the Mica backdrop (follows the content's theme) and the system caption buttons
(`AppWindow.TitleBar.Button*Color`, set per theme because they would otherwise follow Windows, not DevSweep).

## 4. First run

The Welcome sheet has a "Where things are" card: the list of places (built from the places that exist) and
where the theme button and Settings are.

## What proves it (nothing here needs a person looking at a screenshot)

| Check | Where | What it proves |
|---|---|---|
| `VerifyNavigation`: every expected place has a `NavigationViewItem` with `ActualHeight` and `ActualWidth` above 0, whose bounds (`TransformToVisual`) lie inside the window and inside every `ScrollViewer` above it; no item exists for a place that is not offered | `--smoke` (default flags and `-module:health -module:projects`), at the opening size and at the minimum size; the screenshot tour on every page, with the update card showing and at the minimum size | nothing is off screen, clipped or scrolled away; Health and Projects are there by default and absent when off |
| Settings reached from its navigation item and from the gear, by invoking the real button | `--smoke` | both ways in work |
| The theme button invoked three times (UI Automation `Invoke`): the view model, the saved setting, `ActualTheme` and the caption button colour after each | `--smoke` | cycle, persistence and live re-theme |
| Light and dark screenshots of every screen: mean brightness of the title bar strip and of the content differ by at least 40 (typically 150 or more) | screenshot tour | the title bar and content both really switch |
| Embedded icon resource in the published exe, the portable exe, the installed exe and the setup exe: same sizes and per-size byte lengths as the .ico (`scripts/Test-DevSweepIcon.ps1`, reads the PE resources) | CI app and installer jobs | the exe has the logo, not the generic icon |
| Shortcut `IconLocation` is `<installed DevSweep.exe>,0`; the Apps & features entry's `DisplayIcon`; Assets installed and identical to the committed .ico | installer test, admin and standard user | the shortcuts and the installed files use the logo |
| The window has a small and a big icon (`WM_GETICON`), the title-bar logo decoded (`BitmapImage.PixelWidth`), the tray icon came from the file | `--smoke` | the running app shows the logo |
| View-model tests: the cycle, the saved value, the label text, the window-fit arithmetic, the places line, flags on by default and off | `DevSweep.App.Tests`, `DevSweep.Core.Tests` | the logic |

What still needs a human: how the logo and the title-bar buttons actually look (spacing, contrast of the
sun and moon, the caption buttons' hover colours) and whether the window sits comfortably on a real small
display.

## Files

New: `eng/make-icons.py`, `src/DevSweep.App/Assets/*`, `src/DevSweep.App/Toolkit/AppAssets.cs`,
`Toolkit/Win32Window.cs`, `src/DevSweep.App.ViewModels/WindowSizing.cs`, `scripts/Test-DevSweepIcon.ps1`,
`docs/release-notes/0.1.1.md`. Version 0.1.1 in `version.props`.
