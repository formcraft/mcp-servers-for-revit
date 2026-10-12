# Formcraft Browser: design handoff, next pane (r2)

Source: canvas "Formcraft Revit MCP — Design Canvas", version 8, 12 Oct 2026. `HANDOFF.md` is the first handoff (r1:
Families, Files, Activity, Guard), which PR #2 (`fmc/formcraft-browser`) built. This file covers what changed since then.
The spec behind it is `docs/fmc/ROADMAP.md`.

## Boards in `project/`

| Board | Status | What |
|---|---|---|
| `McpPane.dc.html` | **changed (C1)** | Today's pane, with compact footer buttons |
| `PaneNext.dc.html` | **new** | The next pane, clickable: Setup, QA, Links, Sync, Families, Activity, with a template, new project or live project switch |
| `SetupFlow.dc.html` | **new** | The new-project setup chain and the three lock classes |
| `NewTools.dc.html` | **new** | The five new tools: actions, lock class, what each replaces, what stays human |
| `Roadmap.dc.html` | **new** | Capability matrix and phases |
| `Main`, `Ribbon`, `McpStates`, `Flow`, `Tools` | unchanged | As r1 |

`../tokens.json` is unchanged: same colours, type and radii.

## C1: compact footer (do now, in PR #2)

The family list needs the height. In the pane footer (`FormcraftBrowserPage.xaml`):

| | Before | After |
|---|---|---|
| Footer padding | 8 × 10 | 4 × 8 |
| Button gap | 6 | 4 |
| Button min height | 34 | 24 |
| Button padding | 4 × 10 | 1 × 8 |
| Button text | 12.5 | 11.5 |
| Button radius | 4 | 3 |
| Footer note text | 12 | 11.5 |
| Note box under the list (amber) | padding 8 × 10, text 12 | padding 5 × 8, text 11.5, radius 4 |

The same sizes apply to every section's footer.

## C2: next pane (build with each phase)

Add each section when its tool exists, not before. A section without a tool behind it must not ship.

| Section | Phase | Rows | Footer buttons → tool, class |
|---|---|---|---|
| Setup | 2, 6 | Setup steps with status chips: Done, Ready, Projects only, Report only | Run setup (dry run) → chain (Setup) · One step… (Setup) |
| QA | 1 | One row per check with an issue count or Pass | Run QA → `fmc_qa all` (Read) · Open report (opens the `.md`) |
| Links | 4 | Linked models: coordinates, pinned | Link from 02_Shared… (Setup) · Pin all (Setup) · Audit (Read) |
| Sync | 3 | Keynote file, type keys not in the file, master data differences, last sync | Preview (Read) · Apply (dry run) (Setup) |
| Families | done | As today | As today |
| Activity | done | Log rows | Open log |

- **Tabs:** with six sections, use a 3 × 2 segmented grid (`PaneNext`). Guard moves to the ribbon button.
- **Status strip:** in the real pane the active document decides the state; the canvas's three-way switch is only a
  demo. The strip shows green "setup tools open" for a template, a new document or a non-workshared WIP model. It shows
  red "Claude reads and reports" for a workshared or live model. Use the same `setupLockVerdict` from the server (CLI),
  never a C# copy that could drift.
- **Disabled buttons:** a button of a closed class stays visible: grey fill #F1F3F5, border #DDE1E6, text #8A919C.
  The footer note gives the reason ("Read only").
- **Notes box:** blue (#E8F0FE / #1556BD) for information, amber (#FFF4E0 / #6B4100) for a limit, grey (#EEF0F3 /
  #4A515C) for neutral.

## Not decided

- **D1 (BIM Manager):** setup on live projects. Until decided, Setup buttons are disabled on live projects.
- The values on the boards (files, counts, log rows) are sample data.
