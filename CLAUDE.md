# Formcraft rules for Claude in Revit

This is Formcraft's fork of mcp-servers-for-revit. It serves the Formcraft BIM system: Master Manual
FMC-BIM-MAN-001 v2.2 and the Revit standard FMC-BIM-STD-002 v2.2. The kit (registers, dictionary, scripts)
lives in `Z:\00_Standards\` and in the claude.ai Project "REVIT MCP".

## What Claude does here
- Builds and checks the kit: families, templates, view templates, schedules, chain tests, QA reports.
- Reads live project models (statistics, selections, quantities, QA) and reports. It does not model them.

## Hard rules (enforced by the server; do not try to work around them)
1. **Live-model lock.** Model-changing tools (create_*, operate_element, delete_element, tag_*, color_splash,
   send_code_to_revit) run only on families, new unsaved documents and files in `library_roots`
   (`_Staging`, `_ChainTests`). On a live project the server refuses them. Say so and stop; never retry
   through `send_code_to_revit`. Setup tools (templates, bindings, sheets, project data) may run on shared or live
   models only while the user's **"Claude in shared models"** toggle is on (decision D1). Only the user sets it, in
   the pane; never ask to change it, never work around it.
2. **Files.** Use `fmc_file`. Writes go only to `write_roots` (WIP and staging). Never to `02_Shared`,
   `03_Published`, `04_Archive`, `05_Incoming` or `04_Families\<Role>`: those moves are gates with a named
   approver (Manual 7.5, 9.12). Nothing is deleted: `archive` moves a closed file to `_Archive`.
3. **Dry run first.** Every `fmc_file` write starts with `dry_run: true`. Show the user the result and get
   a clear yes before running it with `dry_run: false`. Overwrite and archive also need `confirm: true`.
4. **Names.** Models are container names `[Project]-[Originator]-[Volume]-[Level]-[Type]-[Role]-[Number].rvt`
   (e.g. `F26001-FMC-ZZ-ZZ-M3-A-0001.rvt`); library families `FMC_[Role]_[CategoryToken]_[Description].rfa`;
   project families `[Project]_[Role]_[CategoryToken]_[Description].rfa` (e.g. `F26001_E_ElecFixture_SocketDouble.rfa`,
   decision D4), saved only in the project's `01_WIP\<Role>\Families\`; templates `FMC_<Role>_Template_v2.2.rte`.
   The server rejects anything else.
5. **Never decide for a professional.** Claude does not release families, approve gates, change structural
   sections, set demand factors or sign anything. It prepares, checks and reports.
6. **Workshared models** are never saved by a tool. The user synchronises with central in Revit.

## Building families (`fmc_family`)
- Always `plan` first and show the user the one-line-per-family summary; then `build` with `dry_run: true`, then
  `dry_run: false` after a clear yes. Families land in `_Staging\<Role>`; only the Family Librarian releases.
- Batches of at most 25. Report every warning the build returns (load classification, formulas, shared faces):
  they are the hand steps left for the Family Librarian, not errors to hide.
- `load_test` runs only with `FMC_ChainTest_<Role>.rvt` from `_ChainTests` as the active document.
- A parameter missing from the dictionary is skipped and reported. Never add it some other way.

## Data sources
- Family specs: `Z:\00_Standards\03_Master_Data\FMC_Family_Specs.json` (connectors, parameter packs, types).
- Shared parameters: `Z:\00_Standards\01_Shared_Parameters\FMC_SharedParameters.txt`. Never invent a
  parameter: if one is missing, report it as a dictionary change request.
- Revit version for the pilot: 2025.

## The Formcraft Browser pane
- The pane in Revit runs the same `fmc_file` and `fmc_family` code through `build/fmc/cli.js`, so it follows these
  rules too. Rows in `FMC_MCP_FileLog.csv` may come from the user's clicks in the pane as well as from Claude.

## Developing this fork (Claude Code)
- **Start with `docs/fmc/ROADMAP.md`.** It is the agreed plan for the all-round plugin: phase order, the pattern every
  tool follows (server check → dry run → one Revit handler → log → pane CLI), the Setup lock class, and the
  decisions (D1 toggle, D4 project family names and D5 project folder decided; D2 and D3 open). Build phases in
  order; don't start a phase before the previous one ran on the user's PC.
- Pane design: `docs/fmc/design/HANDOFF_NEXT.md` (changes since r1) and `HANDOFF.md` (r1). The boards in
  `docs/fmc/design/project/` are design references, not code.
- Branches: `fmc/file-tools-and-guard` (PR #1, base) ← `fmc/formcraft-browser` (PR #2, the pane). Merge the base
  forward into the pane branch after each base change. `main` is upstream until PR #1 is merged.
- D1 is the user's toggle (`claude_in_shared_models`, default off): no tool may change it, and it opens Setup tools
  only. Model tools stay locked on live models until a new decision.
- Kit data comes from the kit, never from code: a missing value is a kit change request.

## Keep tokens low
- Prefer one batch call over many small ones; ask for summaries, not element dumps.
- The FMC tool profile hides upstream tools Formcraft does not use (`FMC_TOOL_PROFILE=full` restores them).
