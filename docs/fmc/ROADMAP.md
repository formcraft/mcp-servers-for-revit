# Formcraft Revit MCP: roadmap to an all-round BIM plugin

Status: **planned**, revised 12 Oct 2026 (v2) after the BIM Manager's direction. Design: canvas "Formcraft Revit MCP —
Design Canvas", row "All-round plugin: what we build next" (copies in `docs/fmc/design/`).

## The goal, in the BIM Manager's words

A Claude-backed system to **start every new project BIM-compliant** with the Formcraft philosophy:

- a library of compliant templates: discipline models, families, symbols, view templates and sheets;
- shared parameters with their bindings;
- families made from a description, an image or a discipline need, through **JSON recipes**;
- parameters injected into existing families;
- project-specific family batches, named for the project;
- project details (names, client and so on) entered once.

Claude takes over and edits these in Revit itself. It never decides for a professional, and CLAUDE.md rules 1 to 6
still hold.

## 1. Decisions (12 Oct 2026, BIM Manager)

| # | Decision | Effect in code |
|---|---|---|
| **D1** | **A toggle the user controls, "Claude in shared models".** Off by default. When it is on, Setup tools may run on workshared and live models. | Guard key `claude_in_shared_models` (default `false`). Only the pane changes it (a human click, logged); no tool can. The status strip always shows its state. Model-class tools (`create_*`, `operate_element`, `delete_element`, `send_code_to_revit`) stay locked on live models whatever the toggle says (Manual Part 18); widen that only on a new decision. Rule 6 is unchanged: tools never save or synchronise a workshared model. |
| D2 | Open: retire the Dynamo and pyRevit scripts as each tool is proven, or keep them as a fallback. | Keep them until decided. |
| D3 | Open: naming rules for views and levels. | QA reports file, sheet and type names only until the rules are in the kit. |
| **D4** | **Project families are named `[Project]_[Role]_[CategoryToken]_[Description].rfa`**, e.g. `F26001_E_ElecFixture_SocketDouble.rfa`. They are saved in the project's `01_WIP\<Role>\Families\` and stay project-scoped. The Family Librarian may promote one to the library, renamed `FMC_...`. | **Done** in `policy.ts` (`projectFamily`, `nameProblems`, `checkWritePath`): a project family is refused in the kit library and outside `01_WIP`. Manual 4.6 needs the matching line (a Manual change request). |
| **D5** | **The user creates the project folder and picks it in the project-start options.** The tree is built inside it. | Project start takes `project_root` from a folder picker in the pane (or from the user in chat). It builds the `fmc_new_project.py` tree there: empty folders and `_README.txt` files only, never other files in the gate folders. Then it adds `<project_root>\01_WIP` to the write roots for that project (guard key `project_roots`, written by the pane). |

## 2. Where things stand

Code and data that exist today:

- **Kit data and rules:** registers, the dictionary, keynotes, `FMC_Revit_Kit.xlsx` (81 view templates, 42 filters,
  54 schedules, 194 sheets, 107 symbols, electrical and discipline settings) and the family specs.
- **MCP:** `fmc_file`, `fmc_family` (data-shell families: a placeholder box with connectors and parameters), the
  live-model lock, and the Formcraft Browser pane (PR #2).

What the kit does **not** have yet is the compliant library itself. `06_Templates` holds only a README (13 templates
planned: Base, A, I, S, M, E, P, FP, LP, C, L, Z, W), and `04_Families\<Role>` hold only READMEs. Building that library
is the first job.

## 3. The pattern every tool follows

Copy `fmc_family` and the pane branch (PR #2):

1. **Kit data is the only source.** A value missing from the kit is a kit change request, never filled in by code.
2. **Server check first.** Pure functions in `server/src/fmc/`, unit tested with fixtures cut from the real kit.
3. **Dry run by default.** A real run follows a clear yes.
4. **One Revit handler per tool** (`commandset/.../Fmc`). Each action runs as one transaction (or transaction group)
   through ExternalEvent and returns `{done, changed[], skipped[], warnings[]}`.
5. **Log and report.** Each run adds a row to `FMC_MCP_FileLog.csv`; reports go to `reports_dir`.
6. **Pane parity.** Each tool exports `FMC_<TOOL>_SHAPE` and `runFmc<Tool>`, has a command in `server/src/fmc/cli.ts`,
   and the pane calls that command.
7. **Register.** Add the tool to `register.ts`, `command.json` and CLAUDE.md, with a short usage section.
8. **Tests.** Every refusal and every kit edge case has a test. CI stays green on Windows.

## 4. Lock classes (`policy.ts`)

| Class | Where it may run | Commands |
|---|---|---|
| Read | Every model | `fmc_qa *`, `fmc_link audit`, `fmc_sync preview`, upstream read-only tools |
| Setup | New unsaved documents; non-workshared files in `library_roots` (templates, families); non-workshared models in `write_roots` / `project_roots`; **and workshared or live models while the D1 toggle is on** | `fmc_template *`, `fmc_project *`, `fmc_sync apply`, `fmc_link link, pin`, `fmc_family inject` (families loaded in a project) |
| Model | Unchanged: families, new unsaved documents, `library_roots` | `WRITE_COMMANDS` |

Add a `SETUP_COMMANDS` set and `setupLockVerdict(active, cfg)`. `SocketClient.sendCommand` checks the command's class
and fails closed. Add cases to `fixtures/lock_cases.json`, so the C# GuardStatus agrees (parity test).

## 5. Phases, in the agreed order

Sizes: S small, M medium, L large. Each phase ends with a run on the pilot PC (Revit 2025) and a kit update README.

### Now: pilot run of today's family builder

Six pilot families: plan, build, load test (`pilot_set` in the specs).

### Phase 1: kit JSON export (S)

Add `_build/build_mcp_data.py` (run last by `build_all.py`, checked by `test_kit.py`). It writes
`03_Master_Data/FMC_MCP_Data.json`:

| Key | From |
|---|---|
| `parameters` | `FMC_Master_Data.json` `params` (guid, binding, categories) |
| `sheets` | `FMC_Sheet_Register.csv` |
| `view_templates`, `filters`, `schedules` | `FMC_Revit_Kit.xlsx`, sheets of the same names |
| `symbols` | `FMC_Revit_Kit.xlsx` `Symbols` |
| `electrical_settings`, `discipline_settings` | `Electrical_Settings`, `Discipline_Settings` |
| `project_tree` | `fmc_new_project.py` (folders and READMEs) |
| `naming` | Manual 4.1 to 4.6, D4 |
| `keynotes_path` | `02_Keynotes/FMC_Keynotes.txt` |

New guard keys: `mcp_data_path`, `master_data_path`, `template_library` (where the built `.rte` files live).

### Phase 2: `fmc_template`, the compliant template library (L). Setup class

Builds `FMC_Base_Template_v2.2.rte`, then the 12 role templates from it, in `06_Templates\_Staging`. Each action is
create-or-update by name, and anything the kit doesn't list is reported, never deleted.

| Action | Puts into the template |
|---|---|
| `bind_parameters` | Dictionary bindings, Type or Instance, under Data (ports `FMC_BindSharedParameters.py`; a conflict is reported, never changed) |
| `keynote_file` | Keynote table pointing at the kit file |
| `filters`, `view_templates` | From the kit sheets, per role |
| `schedules` | From the kit sheet, per role |
| `sheets` | The role's sheets from the register, on the FMC title block (ports `2_CreateSheets`) |
| `electrical` | Voltages, distribution systems and load classifications (ports `1_SetupElectrical`). Demand factors are never set (rule 5). |
| `browser` | Project Browser organisation |
| `load_families` | The released library families for the role |
| `build` | All of the above in order, each with its dry run |

The server checks the template name (`FMC_<Role>_Template_v2.2.rte`). Publishing a template to `06_Templates` is a gate
with a named approver: no tool does it.

### Phase 3: family recipes (L). The JSON family maker

Today's builder makes data shells. A **recipe** describes the whole family, and the builder makes it in Revit:

```json
{ "family": "FMC_E_ElecFixture_SocketDouble", "template": "Metric Generic Model face based.rft",
  "category": "OST_ElectricalFixtures", "subcategories": ["Socket Faceplate"],
  "parameters": [{"name": "Width", "type": "Length", "type_param": true, "default": 146},
                 {"shared": "FMC_Mounting_Height_mm"}, {"shared": "FMC_Asset_Tag"}],
  "reference_planes": [{"name": "Left", "offset": "-Width/2"}, {"name": "Right", "offset": "Width/2"}],
  "geometry": [{"extrusion": "rectangle", "between": ["Left", "Right"], "height": 86, "depth": 35,
                "material": "Plastic White", "subcategory": "Socket Faceplate"}],
  "symbolic": [{"line": [[0, 0], [10, 0]], "view": "plan"}],
  "connectors": [{"domain": "Electrical", "system": "Power", "face": "back", "voltage": 230, "poles": 1}],
  "types": {"13A Double": {"Width": 146}, "13A Single": {"Width": 86}} }
```

- **Schema.** `docs/fmc/family_recipe.schema.json` (to write) covers parameters (shared from the dictionary only, family
  parameters with formulas), reference planes, labelled dimensions, solids (extrusion, blend, revolve, sweep, void),
  materials, subcategories, visibility, symbolic lines and filled regions (for the 107 symbols and annotation), labels,
  connectors with positions, and types.
- **Where recipes come from.**
  - In the kit: `03_Master_Data/FMC_Family_Recipes/<family>.json`, one per family. They are generated from the specs
    and refined by hand or by Claude.
  - From Claude: written from a description, a product photo or a datasheet, or a discipline need. Dimensions read from
    a photo are estimates, so a datasheet or the user confirms the real values before a build.
- **Server check.** The schema, dictionary parameters only, naming (FMC_ or D4), and formulas that reference only
  existing parameters.
- **Actions.** `fmc_family recipe_plan`, `recipe_build` (dry run first, batches of 25), `inject`, `batch`.
  - `inject` opens existing families (manufacturer content, older Formcraft families), adds dictionary parameters,
    sets values, renames them to the standard and saves them to staging (library) or the project WIP (D4).
  - `batch` takes one recipe and a table of sizes or variants (CSV or JSON) and makes project families named under D4.
- **Limits.** Recipe geometry is simple and parametric. Manufacturer-detail and freeform geometry stay hand work.

### Phase 4: `fmc_project start`, the project start form (M)

One form in the pane, also callable by Claude:

| Option | Notes |
|---|---|
| Project folder | **D5**: created by the user and picked here |
| Project code, short name | Manual 4.1 (2-6 characters A-Z/0-9; short name letters and digits) |
| Client, address, stage, other Project Information | Written to Project Information and the title block parameters |
| Roles | Which discipline models to create (e.g. A S M E P FP LP C L) |
| Originator, volume, level | For the container names |
| Project family set | Recipes and tables for D4 batches (optional) |
| "Claude in shared models" | The D1 toggle, shown here and in the status strip |

The chain runs these steps in order, each with its dry run, and the user can stop after any one:

1. Build the folder tree in the project folder.
2. Create each discipline model from its role template, named `[Project]-[Originator]-[Volume]-[Level]-M3-[Role]-0001.rvt`
   in `01_WIP\<Role>`.
3. Fill Project Information and the title blocks.
4. Link the discipline models by shared coordinates.
5. Build and load the project family set (D4).
6. Run QA.

It ends at the people gate: the coordinator reviews, enables worksharing, sets the central location (Manual 7.2) and
coordinates.

### Phase 5: `fmc_qa` (M). Read class, works on every model

`naming`, `bindings`, `keynotes`, `links`, `health`, `sheets`, `ids`, `all`. The report goes to
`FMC_QA_<model>_<yyyymmdd-hhmm>.csv` and `.md`, and Claude gets counts and the report path only. Ports `FMC_ModelHealth`,
`3_SheetQA`, `4_ProjectMetrics` and `fmc_name_check.py`. This phase may move earlier if QA is wanted on current jobs.

### Phase 6: `fmc_sync` (S)

`preview` (Read) and `apply` (Setup): master data and keynote values into types. It ports `FMC_SyncMasterData.py`
exactly: `FIELD_MAP`, master data wins, unpriced handling and the CSV change log. A keynote code missing from the kit
is a kit change request.

### `fmc_link` (inside phase 4, and audit in phase 5)

`link` (Setup) uses `ImportPlacement.Shared` and refuses when the files share no coordinates. `pin` is Setup;
`audit` is Read. Acquiring and publishing coordinates stays with the BIM Coordinator (Manual 8.5).

## 6. Pane

See `docs/fmc/design/HANDOFF_NEXT.md`: sections, the project start form, the D1 toggle and the compact footer.

## 7. What stays with people

- Releasing families (Family Librarian) and promoting project families to the library.
- Every CDE gate (Manual 7.5, 9.12) and publishing templates.
- Demand factors and structural sections.
- Coordinates.
- Enabling worksharing and synchronising with central.
- Real geometry beyond recipes.
- Every signature.
