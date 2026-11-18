# 0.2.0 walk-through (about 20 minutes, on a real PC)

Install `DevSweep-0.2.0-win-x64-setup.exe` (or unzip the portable build). Tick each line; note anything odd.

## 1. First look
- [ ] The window fits your screen; Settings and History are visible in the sidebar.
- [ ] The sidebar shows the free-space card, and counts next to Clean up, Projects and Health.
- [ ] Light, Dark and Windows-setting all look right (button at the top right). Turn on a high-contrast theme and check the sidebar and text are readable.

## 2. Your repos (the main complaint)
- [ ] Open Projects. Your clones appear with no GitHub account connected.
- [ ] Run "Find all repos on this PC". Repos in Downloads and under C:\ are found; the report says what was searched. Time it on your SSD.
- [ ] Search for part of a repo name; filters and counts change.
- [ ] Click a repo: the panel shows the safety checklist; Push / Remove buttons are visible without scrolling.
- [ ] Open in editor opens your editor; Show in File Explorer opens the folder.
- [ ] (Optional) Sign in to GitHub, Download a repo, and check commits use the identity set in Settings.

## 3. Clean up
- [ ] Run a scan. Open an item; read the panel. Select a few, Review, clean. They go to the Recycle Bin.
- [ ] Restore one from History.
- [ ] With Chrome or VS Code open, pick an item that needs it closed; Quit closes it politely.

## 4. Health, Settings, tray
- [ ] Health: tools and versions, packages (winget/Scoop/Chocolatey), a Security finding, a Ports entry.
- [ ] Settings: add a folder, set the default editor, copy the troubleshooting report (no file names in it).
- [ ] Tray: left click opens the flyout near the tray icon and closes when you click elsewhere; right click shows the menu.
- [ ] Send a scheduled-scan or alert toast if you can (Settings, Alerts).

## 5. Accuracy kit
- [ ] Run `Verify-DevSweep.ps1` (see `m7-accuracy-signoff.md`): 0 FAIL. Then with `-RunCleanTest` on a machine you do not mind.

Send back what looked wrong (screenshots welcome). 0.2.0 is released only after this passes.
