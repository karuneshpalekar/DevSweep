# Plan: AI editor caches, Claude app caches, Ollama and LM Studio models (Windows)

Source: Mac commit `2cd3cdd` ("Clean caches of AI code editors and the Claude app; list local Ollama and
LM Studio models"). Status: **decisions D1-D3 taken as recommended (2026-11-27); built on `windows-p6-ai-editors`, CI green on all 4 jobs (run 45974391958) and merged into `windows`**.

## 1. What the Mac commit does, and where Windows stands

| Mac change | Windows today | Gap |
|---|---|---|
| Caches of **Cursor, Windsurf**: web cache, compiled code, extension installers; delete; quit the app first | `61-ide.yml`: `cursor-caches`, `windsurf-caches` (and `vscode-caches`) under `%APPDATA%\<App>` | Same idea. Not gated on the app being installed |
| Same caches for **Antigravity, Kiro, Trae, Void, VS Code Insiders** | none | **missing** (5 rules) |
| **Claude desktop app** caches (Cache, Code Cache, GPUCache, Dawn* caches); `vm_bundles` never offered | none (`.claude` in the profile is protected) | **missing**; `vm_bundles` needs an explicit "never" |
| **Only while the app is installed** (`requireApps`); once removed, the leftovers check covers the folders | rules always look | **missing**: a Windows `requireApps` |
| **Ollama**: one item per model and tag, manifest + blobs no other model uses, **to the Trash**, quit Ollama first | `ollamaModels` detector lists per model with unshared size, but the action is **manual** (`ollama rm`) | Action differs (D2) |
| **LM Studio**: one item per model folder, Trash, quit LM Studio first | `lm-studio-models`: per model folder, Recycle Bin, blocker | Same. Titles and min size differ slightly (Mac: `LM Studio · <model>`, 100 MB; Windows: 200 MB) |
| IDE caches that rebuild sit under **Caches**, not **Old and unused** | `Labels.cs`: every `ideVersions` item goes to Old versions | **missing** (one line) |

## 2. Work

### 2.1 Rules (`rules/packs/61-ide.yml`, new `62-ai-editors.yml`)
- Five new editor rules, the same shape as `cursor-caches`: `{RoamingAppData}\<Folder>\{Cache, Code Cache,
  CachedData, CachedExtensionVSIXs, GPUCache}`, `risk: rebuilds`, `kind: delete`, blocker = the app's process.
  Folder and process names to confirm on a real install (section 4):

  | App | Folder under `%APPDATA%` | Process |
  |---|---|---|
  | VS Code Insiders | `Code - Insiders` | `Code - Insiders` |
  | Antigravity | `Antigravity` | `Antigravity` |
  | Kiro | `Kiro` | `Kiro` |
  | Trae | `Trae` | `Trae` |
  | Void | `Void` | `Void` |

- `claude-desktop-caches`: `Cache`, `Code Cache`, `GPUCache`, `DawnGraphiteCache`, `GraphiteDawnCache`,
  `DawnWebGPUCache` under `%APPDATA%\Claude`, and the same under the Store build's
  `%LOCALAPPDATA%\Packages\<Claude package>\LocalCache\Roaming\Claude` if that build exists. Blocker `claude`.
  Wording as on the Mac: conversations, projects, settings and connectors are not touched.
- **`vm_bundles` never offered**: a hard rule in the safety net (not only "not listed"), so no rule or user
  override can reach it. Test it.
- `minSizeMB`: Mac uses 50 for these; Windows uses 200 for VS Code, Cursor and Windsurf. Proposal: 50 for all
  (D3).

### 2.2 `requireApps` on Windows
- New optional detector field `requireApps: [...]`, same name as the Mac, matched against the existing
  installed-apps inventory (`Inventory/AppInventory`: Uninstall registry keys, Start Menu, MSIX packages,
  winget/Scoop IDs) by app name, package ID or executable. No match: the rule finds nothing.
- Applied to the six new rules; D1 decides whether VS Code, Cursor and Windsurf get it too.
- Leftovers: add Antigravity, Kiro, Trae, Void, VS Code Insiders and Claude to `knownApps` in
  `90-leftovers.yml`, so their folders are covered as leftovers once the app is gone (Cursor and VS Code are
  already there). Claude's leftover entry must still never offer `vm_bundles` and says chats are on the account.

### 2.3 Ollama (D2)
- If matched to the Mac: action becomes **Move to Recycle Bin** of the manifest plus the blobs only this model
  uses (the detector already computes them), blocker `ollama` and `ollama app`. Keep `ollama rm <model>` as the
  "better option" text. Blobs are large single files, so the Recycle Bin is fast; one over the bin's size limit
  goes to the quarantine as today.
- Tests: two models sharing a layer (only the unshared blob is in the item), a missing blob, a non-default
  `OLLAMA_MODELS` folder, a manifest that isn't JSON.

### 2.4 LM Studio
- Title `LM Studio · <model>`, subtitle `<publisher> · downloaded <ago>`, min size 100 MB, as on the Mac.

### 2.5 App
- `Labels.cs`: `ideVersions` items with risk `rebuilds` go to **Caches**; old versions stay in **Old versions**.
- Sample data and the screenshot tour: one AI-editor cache item and one Ollama item, sample names only.

### 2.6 Tests (fake folder trees only; nothing reads the real profile)
- Each new rule: found with the app "installed" in a fake inventory, nothing found without it.
- `vm_bundles` refused by the safety net even when a rule names it.
- The Caches / Old versions grouping.
- Rule-pack parse test for `requireApps`.

## 3. Decisions

- **D1.** Gate the existing VS Code, Cursor and Windsurf rules on the app being installed too? Recommended:
  **yes**, for one rule for all editors; their leftovers are already covered.
- **D2.** Ollama: match the Mac (Recycle Bin of the model's own files) or keep showing `ollama rm`?
  Recommended: **match the Mac**, with `ollama rm` as the better option.
- **D3.** Lower the editor-cache minimum from 200 MB to 50 MB, as on the Mac? Recommended: **yes**.

## 4. Can't be checked in CI

Real folder and process names of Antigravity, Kiro, Trae, Void and the Claude app (Squirrel vs Store
install), and Ollama with real models. These go on the walk-through checklist.

## 5. Delivery

One branch `windows-ai-editors`, one subagent or done directly (about a day of changes), CI green, screenshots
checked, merged into `windows`, then a new 0.2.0 candidate.
