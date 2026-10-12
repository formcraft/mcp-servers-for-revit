# Formcraft MCP: execution run book (for Claude Code)

`ROADMAP.md` says **what** to build and why. This file says **how** to carry out each step on the pilot PC, and what
state each step is in. Read `CLAUDE.md`, then `ROADMAP.md`, then this file. Update the status table when a step moves.

## Where things are (pilot PC "crafty27", Revit 2025)

| What | Where |
|---|---|
| This repo | `D:\Fomcraft\FMC_BIM_27\FMC_MCP\mcp-servers-for-revit`, branch `fmc/formcraft-browser` (PR #2) |
| Base branch | `fmc/file-tools-and-guard` (PR #1): server, guard, kit-facing tools, docs |
| Kit (master on this PC) | `D:\Fomcraft\FMC_BIM_27\FMC_Kit_v2.2\FMC_Kit_v2.2` |
| Kit update packages | `D:\Fomcraft\FMC_BIM_27\FMC_MCP\FMC_Update_<date>_r<N>.zip` |
| Guard | `%APPDATA%\Formcraft\fmc_guard.json` |
| Log and reports | `%USERPROFILE%\FMC_Reports` (`FMC_MCP_FileLog.csv`) |
| Revit family templates | `C:\ProgramData\Autodesk\RVT 2025\Family Templates\English` |
| claude.ai Project "REVIT MCP" | Copies of the kit (`FMC_Kit_v2.2/`) and of these docs (`FMC_MCP/`). Claude Code can't write there: tell the user which files changed so they (or Claude in the Project) copy them. |

## Status

| Step | State | Evidence or next action |
|---|---|---|
| Builds and kit r1 to r3 | Done | Add-in `85428a6`, server `3780569` (D4 name rule) installed 12 Oct; kit r3 in the master kit and the Project (704 checks, 0 failed) |
| Decisions D1, D4, D5 | Decided 12 Oct | D4 in `policy.ts` (`7b662b3`); D1 and D5 are built in phases 2 and 4 |
| Decisions D2, D3 | Open | Ask the BIM Manager when phase 5 starts |
| C# lock check in CI | Waiting | Needs the gh `workflow` scope on this PC (`gh auth refresh -h github.com -s workflow`) |
| C5 naming text (D4) in `FmcDialogs.cs` | To do | Small: see `design/HANDOFF_NEXT.md` C5 |
| **Now: pilot run** | **Next** | `/fmc-pilot`, the user present |
| Phase 1: kit JSON | Not started | Plan after the pilot |
| Phase 2: template library | Not started | |
| Phase 3: family recipes | Not started | |
| Phase 4: project start | Not started | |
| Phase 5: QA | Not started | |
| Phase 6: sync | Not started | |

## The loop for every step

1. **Plan.** Write the plan: files to add or change, tests, what runs in Revit, and what could go wrong. Show it to the
   user and wait for a clear yes. Don't write code before the yes.
2. **Branch.**
   - Server, guard, command set, kit-facing code and docs go on the base branch.
   - Pane (WPF) code goes on the pane branch.
   - After each base change, merge the base forward into the pane branch.
   - Never push to `main`.
3. **Test.** Run `npm run build` and `npm test` in `server/`, and `py build_all.py` then `py test_kit.py` in the kit
   `_build`. GitHub's Windows build must pass before anything is installed.
4. **Install** with a rollback.
   - Before replacing them, copy the add-in folder to `_rollback_2025_mcp_<old sha>` and the server folder to
     `build.bak-<old sha>`.
   - Before changing kit files, copy them to `FMC_Kit_v2.2\_backup_before_<what>`.
5. **Run in Revit.** Dry run first, show the result, then the real run after a yes. Report every warning.
6. **Package the kit change** (`/fmc-package`) when kit files changed. List the Project files to update.
7. **Update the status table** above and commit it on the base branch.
8. **Report** to the user: what changed, what you checked and how, and what they need to do. Short. No recap of the
   steps.

## Now: pilot run (`/fmc-pilot`)

Goal: prove today's family builder in Revit 2025 on the kit's `pilot_set` (six families).

1. `fmc_family plan` with `families` = `pilot_set` from `FMC_Family_Specs.json`. Expect 6 of 6 ready. Show the
   one-line-per-family summary.
2. `fmc_family build`, dry run. Show it. Then build for real after the user's yes. Families go to
   `_Staging\<Role>`.
3. Chain-test project.
   - If `_ChainTests\FMC_ChainTest_P.rvt` (and `_FP`, `_E`, `_I`) don't exist, create them with
     `fmc_file create_from_template`, dry run first.
   - The role templates don't exist yet, so use Revit 2025's Systems template from
     `C:\ProgramData\Autodesk\RVT 2025\Templates`.
   - Ask the user to make the chain test the active document.
4. `fmc_family load_test` for that role: dry run, then for real.
5. Report a table with one row per family: built (yes/no), connectors found against expected, parameters found
   against expected, warnings, and hand steps left for the Family Librarian.
6. Done when all six are built and load tested, or every failure has a cause and a proposed fix. Update the status
   table.

## Phase 1: kit JSON export

Spec: `ROADMAP.md` phase 1. Tasks:

1. `_build/build_mcp_data.py`, in the same pattern as `build_family_specs.py`: a `check()` the kit test calls, and
   `main()` writes `03_Master_Data/FMC_MCP_Data.json`.
2. Add it as the last step of `build_all.py`, and add a section to `test_kit.py`. A row that can't be parsed is an
   error. Notes go to the data owner.
3. Add `mcp_data_path`, `master_data_path` and `template_library` to `fmc_guard.example.json` and the server's
   `GuardConfig` (base branch).
4. Write a server reader `server/src/fmc/kitdata.ts` with unit tests on a fixture cut from the real JSON.

Acceptance:

- `test_kit.py` shows 0 failed.
- The JSON counts match the kit: 81 view templates, 42 filters, 54 schedules, 107 symbols, 194 sheets.
- The server tests pass.
- Ship it as kit update r4.

## Phase 2: template library (`fmc_template`)

Spec: `ROADMAP.md` phase 2. Work order:

1. Pure planner `server/src/fmc/template.ts`: kit data plus role, giving the planned actions, each a list of
   creates and updates by name. Unit test it.
2. Tool `server/src/tools/fmc_template.ts`: `plan`, `build` (dry run first), one action at a time or all. Export
   `FMC_TEMPLATE_SHAPE` and `runFmcTemplate`, add it to `cli.ts`, and register it.
3. Setup lock class in `policy.ts` and `SocketClient`: `SETUP_COMMANDS`, `setupLockVerdict`, and
   `claude_in_shared_models` (D1). Add cases to `fixtures/lock_cases.json`.
4. C# handlers in `commandset/.../Fmc/FmcTemplate*.cs`, one per action, each in one transaction. Port the logic of
   `FMC_BindSharedParameters.py`, `2_CreateSheets` and `1_SetupElectrical`, with the same report lines.
5. Pane: the Setup section in template mode (`design/project/PaneNext.dc.html`, template document), and the D1
   switch (HANDOFF_NEXT C3).

Acceptance:

- `FMC_Base_Template_v2.2.rte` and `FMC_E_Template_v2.2.rte` are built in `06_Templates\_Staging`.
- Each action reports created, updated and skipped counts that match the kit.
- A second build changes nothing.
- The user checks both templates in Revit.
- Then the other 11 role templates.

## Phases 3 to 6

Write the detailed plan when the step before is done, from `ROADMAP.md` phases 3 to 6. Add it here as a section like
the ones above before you start.

## Kit update packages (`/fmc-package`)

Use the same format each time: `FMC_Update_<yyyy-mm-dd>_r<N>.zip` holding `1_Kit_update\FMC_Kit_v2.2\...` (changed
files only) and a `README.md` with these sections:

- What changed since r<N-1> (a table: part, change, why)
- What is in it
- Install (`py build_all.py`, `py test_kit.py`, the expected counts)
- Will my MCP still work the way it did before?

List the Project paths the user must update.
