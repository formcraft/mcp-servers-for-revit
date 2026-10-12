# Formcraft Revit MCP: roadmap to an all-round BIM plugin

Status: **planned, not built**. Agreed direction 12 Oct 2026. Design: canvas "Formcraft Revit MCP — Design Canvas",
row "All-round plugin: what we build next" (boards Roadmap, NewTools, PaneNext, SetupFlow; copies in `docs/fmc/design/`).

Goal: Claude and the Formcraft Browser pane set up and check Formcraft BIM workflows from the kit, covering naming
conventions, shared parameter binding, keynotes, model linking, templates, sheets, QA and families. They never model live
projects and never decide for a professional (CLAUDE.md rules 1 to 6 still hold).

## 1. Where things stand

| Capability | Through Claude today | In the kit, not yet in Claude | Planned | Phase |
|---|---|---|---|---|
| Naming conventions | File names checked by `fmc_file` (`policy.ts nameProblems`) | `05_Scripts/tools/fmc_name_check.py` (files on disk) | `fmc_qa naming`: views, sheets, levels, worksets, types | 1 |
| Shared parameter binding | Into families, in `fmc_family build` | `05_Scripts/Dynamo/FMC_BindSharedParameters.py` | `fmc_project bind_parameters` | 2 |
| Keynote syncing | none | `02_Keynotes/FMC_Keynotes.txt`, `05_Scripts/Dynamo/FMC_SyncMasterData.py` | `fmc_sync keynotes`, `master_data` | 3 |
| Model linking | none | none (`5_LinkedLoads` only reads loads from links) | `fmc_link link, pin, audit` | 4 |
| Templates and setup | `fmc_file create_from_template` with the name check | `fmc_new_project.py`, pyRevit `1_SetupElectrical` | `fmc_project`, `fmc_template` | 2, 5 |
| Sheets | none | pyRevit `2_CreateSheets`, `3_SheetQA` | `fmc_project create_sheets`, `fmc_qa sheets` | 1, 2 |
| QA and model health | `analyze_model_statistics` (read-only) | `FMC_ModelHealth.py`, `4_ProjectMetrics`, `07_IFC_IDS/*.ids` | `fmc_qa`: one report against the Manual | 1 |
| Families | `fmc_family plan, build, load_test` | register, specs, chain tests | unchanged | - |
| Safety | lock, dry run, nothing deleted, CSV log | - | lock gains a Setup class | all |

## 2. The pattern every new tool follows

Copy `fmc_family` and the Formcraft Browser branch (PR #2). Do not invent a new shape.

1. **Kit data is the only source.** Each tool reads kit files through paths in `fmc_guard.json`. It never hard-codes
   standards. A value missing from the kit is reported as a kit change request, never filled in.
2. **Server check first** (`server/src/fmc/<tool>.ts`, pure functions, unit tested with fixtures cut from the real
   kit, like `specs.test.ts`). Names, folders, lock class and dictionary are all checked before Revit is called.
3. **Dry run by default.** `dry_run: true` returns exactly what would change. A real run needs `dry_run: false` after a
   clear yes from the user.
4. **One Revit handler per tool** (`commandset/Commands/Fmc/Fmc<Tool>Commands.cs` plus
   `commandset/Services/Fmc/Fmc<Tool>EventHandler.cs`): one transaction (or transaction group) per action, on the
   Revit main thread via ExternalEvent. Each action returns `{done, changed[], skipped[], warnings[]}`.
5. **Log and report.** Every action appends a row to `FMC_MCP_FileLog.csv`. QA writes its report to `reports_dir`.
6. **Pane parity.** Export `FMC_<TOOL>_SHAPE` and `runFmc<Tool>` from `server/src/tools/fmc_<tool>.ts` and add the
   command to `server/src/fmc/cli.ts`. The pane calls the CLI (option A, already chosen in PR #2), so the pane and
   Claude share one copy of the rules.
7. **Register** in `register.ts` (FMC profile), `command.json`, and CLAUDE.md (a short usage section per tool).
8. **Tests.** Unit tests for every refusal and for each kit edge case. CI (`.github/workflows/fmc-build.yml`) must
   stay green on Windows.

## 3. Lock classes (change to `policy.ts`)

Today there are two kinds of command: read-only, and model-changing (`WRITE_COMMANDS` plus `liveLockVerdict`). Add a
third, **Setup**:

| Class | Where it may run | Commands |
|---|---|---|
| Read | Every model, live projects included | `fmc_qa *`, `fmc_link audit`, `fmc_sync preview`, upstream read-only tools |
| Setup | (a) new unsaved documents; (b) files in `library_roots` that are not workshared, e.g. templates in `06_Templates\_Staging`; (c) project models in `write_roots` (WIP) that are **not workshared**, i.e. new projects before worksharing | `fmc_project *`, `fmc_sync master_data, keynotes`, `fmc_link link, pin`, `fmc_template *` |
| Model | Unchanged: families, new unsaved documents, `library_roots` | `WRITE_COMMANDS` (`fmc_load_test_families`, `create_*`, `operate_element`, `delete_element`, `send_code_to_revit`, ...) |

Implementation:

- `SETUP_COMMANDS` set and `setupLockVerdict(active, cfg)` next to `liveLockVerdict`.
- `SocketClient.sendCommand` checks the class of the command. It fails closed, exactly like the live lock.
- Tests in `policy.test.ts` and the C# parity cases in `server/src/fmc/fixtures/lock_cases.json` (PR #2).
- **Decision D1 (BIM Manager): setup on live projects.** The default is closed: a workshared or live model is Read only.
  If Formcraft decides the BIM Coordinator may run setup on a live project from the pane, add
  `"setup_on_live": false` to `fmc_guard.json` (default false) and require a confirmation in the pane. Never turn it on
  for Claude without that decision.

## 4. Kit side: one JSON for the setup data (phase 0)

The setup tools need `FMC_Revit_Kit.xlsx` sheets in a form Node can read without Excel. Add
`_build/build_mcp_data.py` (the same pattern as `build_family_specs.py`; run last by `build_all.py`; checked by
`test_kit.py`). It writes `03_Master_Data/FMC_MCP_Data.json` with:

| Key | From | Used by |
|---|---|---|
| `parameters` (name, guid, binding, categories_bic, status) | `FMC_Master_Data.xlsx` Parameters, or `FMC_Master_Data.json` `params` | `fmc_project bind_parameters`, `fmc_qa bindings` |
| `sheets` (role, number, name, series, scale, issue_set) | `FMC_Sheet_Register.csv` (194 sheets) | `fmc_project create_sheets`, `fmc_qa sheets` |
| `view_templates` (81), `filters` (42), `schedules` (54) | `FMC_Revit_Kit.xlsx` sheets of the same names | `fmc_template`, `fmc_qa naming` |
| `electrical_settings`, `discipline_settings` | `Electrical_Settings`, `Discipline_Settings` | `fmc_project electrical` |
| `naming` (container pattern, role and type codes, view and level patterns) | Manual 4.1 to 4.6, `policy.ts` codes, `fmc_name_check.py` | `fmc_qa naming` |
| `keynotes_path` | `02_Keynotes/FMC_Keynotes.txt` | `fmc_project keynote_file`, `fmc_sync`, `fmc_qa keynotes` |

Add `mcp_data_path` and `master_data_path` to `fmc_guard.json` and `fmc_guard.example.json`. A row the script cannot
parse is a `test_kit.py` error, never a silent skip.

## 5. Phases

Sizes: S small, M medium, L large. Build in this order. Each phase ends with a run on the user's PC (Revit 2025) and a
short README section in the kit update.

### Phase 1: `fmc_qa` (M). Read class, safe on live models

Actions (each may run alone; `all` runs them in order and writes one report):

| Action | Checks | Kit source |
|---|---|---|
| `naming` | View, sheet, level, workset and type names against the Manual patterns; the model's own container name | `naming` in `FMC_MCP_Data.json`; `nameProblems` |
| `bindings` | Dictionary parameters bound to the right categories as Type or Instance; extras not in the dictionary | `parameters` |
| `keynotes` | Keynote table path is the kit file; type keynote values exist in it and match `^[A-Z]\.[A-Z0-9]{3}_\d{2}$` | `keynotes_path` |
| `links` | Each link: container name, shared coordinates or origin, pinned, workset | - |
| `health` | Warnings count by type, imports, in-place families, unused families, unplaced views and sheets | `FMC_ModelHealth.py` logic |
| `sheets` | Sheets against the register for the model's role: missing, extra, wrong name, no title block | `sheets` |
| `ids` | Pre-check of the IDS base: required properties present and non-empty on the IDS applicability categories | `07_IFC_IDS/FMC_Design_Base.ids` |

Output: a summary of one line per action, plus `FMC_QA_<model>_<yyyymmdd-hhmm>.csv` and `.md` in `reports_dir`, one
row per issue (element id, rule, found, expected). Never returns element dumps to Claude; counts and the report path
only. Pane: QA section with Run QA and Open report.

### Phase 2: `fmc_project` (M). Setup class

| Action | Does | Ports |
|---|---|---|
| `bind_parameters` | Binds dictionary parameters to categories as Type or Instance under Data. Refreshes existing bindings. A binding conflict (Type vs Instance) is reported, never changed. A missing category is skipped and reported. | `FMC_BindSharedParameters.py` (same report lines: BIND, REFRESH, CONFLICT, SKIP) |
| `keynote_file` | Sets the keynote table to the kit file (`KeynoteTable` external resource) | - |
| `electrical` | Voltages, distribution systems and load classifications from `electrical_settings`. Never sets demand factors (rule 5): they are listed for the electrical engineer. | `1_SetupElectrical` |
| `create_sheets` | The role's sheets from the register on the FMC title block; skips numbers that exist | `2_CreateSheets` |
| `project_info` | Project number, name, client and status fields, from arguments the user gives | `fmc_new_project.py` |
| `setup` | Runs the above in order, each with its own dry run | - |

### Phase 3: `fmc_sync` (S)

`preview` (Read class) and `master_data`, `keynotes` (Setup class). Ports `FMC_SyncMasterData.py` exactly, including:

- `FIELD_MAP` and the code pattern;
- "master data wins";
- "unpriced" handling;
- the CSV change log.

A keynote code not in the kit is a kit change request, never added to the file.

### Phase 4: `fmc_link` (M)

| Action | Class | Does |
|---|---|---|
| `link` | Setup | Links a model from `02_Shared` (reading is allowed; never writes there) with `ImportPlacement.Shared`. If the files share no coordinates, it refuses and says so. Container name checked. |
| `pin` | Setup | Pins link instances |
| `audit` | Read | Same as `fmc_qa links` |

Acquire and publish coordinates stay with the BIM Coordinator (Manual 8.5). No tool writes to another team's file.
Workset placement needs worksharing, so it waits for D1.

### Phase 5: `fmc_template` (L). Setup class, templates in `06_Templates\_Staging` only

`filters`, `view_templates`, `schedules` and `browser` (browser organisation), built from the kit sheets into a role
template opened from `FMC_Base_Template_v2.2.rte`. Each element is created or updated by name. Elements the kit doesn't
list are reported, never deleted. Saving goes through `fmc_file save_as` with the template name rule
`FMC_<Role>_Template_v2.2.rte`. Publishing to `06_Templates` is a gate with a named approver: no tool does it.

### Phase 6: setup workflow in the pane (S)

The pane's Setup section runs this chain, with a dry run before each step:

`fmc_file create_from_template` → `fmc_project bind_parameters` → `keynote_file` + `fmc_sync keynotes` →
`create_sheets` → `fmc_link link` → `fmc_qa all`

The user can stop after any step. The chain ends at the people gate: the coordinator reviews the report, saves,
enables worksharing and sets coordinates.

## 6. Pane changes (Formcraft Browser, PR #2)

See `docs/fmc/design/HANDOFF_NEXT.md`.

- Sections: Setup, QA, Links, Sync, Families, Activity. Guard moves behind the ribbon's Guard button.
- The status strip shows the lock class of the active document: Setup open (green) or Read only (red).
- Buttons of a closed class show disabled, with the reason in the footer note.
- Compact footer buttons (24 px high, 11.5 px text) so lists get the height. This is already wanted for today's
  Families section.

## 7. What stays with people

Geometry, releasing families (Family Librarian), every CDE gate (Manual 7.5, 9.12), demand factors and structural
sections, acquiring or publishing coordinates, enabling worksharing, synchronising with central, and every signature.

## 8. Open decisions

| # | Decision | Owner | Default until decided |
|---|---|---|---|
| D1 | May the BIM Coordinator run setup tools on live projects from the pane? | BIM Manager | No: live projects are Read only |
| D2 | Retire the Dynamo and pyRevit scripts as each tool is proven, or keep them as a fallback? | BIM Manager | Keep until a phase passes on a real project |
| D3 | Which naming patterns apply to views and levels (Manual 4.x lists files; views and levels may need a rule added) | BIM Manager | `fmc_qa naming` reports only file, sheet and type names until the patterns are in the kit |
