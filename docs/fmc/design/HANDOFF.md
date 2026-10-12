# Formcraft Browser (Revit MCP) — design handoff

Source canvas: **Formcraft Revit MCP — Design Canvas** (claude.ai artifact, version 7, 12 Oct 2026).
Target code: `formcraft/mcp-servers-for-revit`, branch `fmc/file-tools-and-guard`, Revit 2025 (.NET 8, WPF).

## What is in this zip

| Path | What |
|---|---|
| `project/canvas.json` | Canvas index: board positions and titles |
| `project/Main.dc.html` | Cover: the three features |
| `project/Ribbon.dc.html` | Formcraft tab, **Claude** panel, and the docking sketch |
| `project/McpPane.dc.html` | **The pane to build** (clickable on the canvas): Families, Files, Activity, Guard |
| `project/McpStates.dc.html` | Pane states: library file, live project, not ready |
| `project/Flow.dc.html` | Family builder flow (plan → dry run → build → load test → release) |
| `project/Tools.dc.html` | Tool groups Claude sees |
| `design/tokens.json` | Colours, type, sizes and radii used by every board |

The `.dc.html` files are design references, not code. They open in a browser only roughly, because the canvas
runtime (`support.js`) is not included. To get images, open the canvas and use Share › Export.

## Build list

### 1. Dockable pane: `Formcraft Browser`

- Register it with `UIControlledApplication.RegisterDockablePane` at start-up (TouchKit 0.3's
  `TouchViewsPaneProvider` in `formcraft/fmc-plugins` is a working pattern).
- Default position: tabbed with Properties (`DockablePanes.BuiltInDockablePanes.PropertiesPalette`).
- Pane width 420 DIP. Layout from top to bottom:
  1. **Status strip.**
     - Connection dot and text, with "port 8080 · Revit 2025" on the right.
     - Active-document card, in one of three styles from `McpStates`:
       - **Library file:** green, open lock, "model tools open".
       - **Live project:** red, closed lock, "Claude reads, never models".
       - **Not ready:** a grey dot, with one amber card per problem and its fix button.
  2. **Section tabs:** a segmented control with Families, Files, Activity and Guard.
  3. **Section body:** scrolls.
  4. **Footer:** section actions on the left, a short status note on the right.
- Update the status strip on `ViewActivated` and `DocumentOpened`/`Closed`. The pane only reads state.

### 2. Sections and the code they call

| Section | Shows | Buttons → code |
|---|---|---|
| Families | Selection (pilot set / role / priority), **Dry run** checkbox (on by default), one row per family with a status chip | Plan / Build (dry run) / Build / Load test → the same checks as the `fmc_family` tool (`server/src/fmc/specs.ts`), then `fmc_build_families` / `fmc_load_test_families` |
| Files | Open documents with a badge (Library file · Family · Live project · workshared) | Save as… / Close / New from template… / Archive a closed file… → `fmc_file` actions, dry run first |
| Activity | Last rows of `%USERPROFILE%\FMC_Reports\FMC_MCP_FileLog.csv` | none (read-only) |
| Guard | Values from `%APPDATA%\Formcraft\fmc_guard.json` | Open fmc_guard.json / Reload |

The folder rules, the dry run, the live-model lock and name checks must come from the same code the MCP server uses
(`server/src/fmc/policy.ts`), so the pane and Claude never disagree. Either:

- **Option A:** run the checks in the server and have the pane ask the server, or
- **Option B:** port `policy.ts` to C# with the same unit tests.

Decide this before building; option A keeps one copy of the rules.

### 3. Ribbon: Formcraft tab, panel **Claude**

| Item | Size | Does |
|---|---|---|
| Formcraft Browser | Large, toggle | Shows / hides the pane; stays pressed while the pane is visible |
| Claude service | Large | Starts / stops listening on port 8080 (the existing plugin switch). Hiding the pane never stops it |
| Guard | Small, stacked | Opens fmc_guard.json |
| Log | Small, stacked | Opens the FMC_Reports folder |
| Probe | Small, stacked | Tests the connection |

The icon paths are in `Ribbon.dc.html` (`ic` in the script): 24 px line icons, round caps. The main action is filled blue.

### 4. Status chips

| Chip | Background | Text |
|---|---|---|
| Ready / Plan | #E8F0FE | #1556BD |
| Note | #FFF4E0 | #6B4100 |
| Built / Pass / Done | #E6F4EA | #1E6B34 |
| Refused / Problem | #FDECEA | #A50E0E |

## Rules that come from the Formcraft Manual

- Dry run first; Build without dry run and every overwrite or archive ask again (Manual 17.2).
- Writes go only to WIP and staging. Never to 02_Shared, 03_Published, 04_Archive, 05_Incoming or 04_Families\<Role>
  (Manual 7.5, 9.12).
- Live projects: model buttons stay visible but disabled, with the reason (Manual Part 18).
- Workshared models are never saved by a tool.
- Every warning a build returns is shown. Warnings are the hand steps left for the Family Librarian.

## Not decided yet

- **Look:** this canvas follows the MEP Suite canvas (IBM Plex, blue #1F6FEB). TouchKit 0.3 uses Satoshi on light
  Studio Green. Pick one for all Formcraft panes before building.
- **Rules location:** option A or B above.
- **Family selection picker:** the canvas shows a "Change…" button only. It needs a role / priority / name picker.
