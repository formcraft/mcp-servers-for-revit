import fs from "node:fs";
import path from "node:path";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { GuardConfig, csvLine, expandEnv, loadConfig } from "../fmc/policy.js";
import { FamilyPlan, SpecFile, expectedConnectorCounts, parseSharedParameters, planFamily, selectFamilies } from "../fmc/specs.js";

const win = path.win32;

function reply(body: object, isError = false) {
  return { content: [{ type: "text" as const, text: JSON.stringify(body, null, 2) }], isError };
}

function log(cfg: GuardConfig, action: string, dryRun: boolean, target: string, result: string, detail: string) {
  try {
    const dir = expandEnv(cfg.reports_dir);
    fs.mkdirSync(dir, { recursive: true });
    const file = win.join(dir, "FMC_MCP_FileLog.csv");
    if (!fs.existsSync(file)) fs.writeFileSync(file, csvLine(["timestamp", "action", "dry_run", "target", "result", "detail"]));
    fs.appendFileSync(file, csvLine([new Date().toISOString(), action, dryRun, target, result, detail]));
  } catch {
    // logging must never block the work
  }
}

/** One line per family, so a 25-family plan stays short. */
function brief(p: FamilyPlan) {
  return {
    family: p.family,
    ok: p.problems.length === 0,
    save_path: p.save_path,
    shared_parameters: p.shared_parameters.length,
    connectors: p.connectors.map((c) => [c.domain, c.system, c.direction ?? c.role].filter(Boolean).join(" ")),
    types: p.types.length,
    part_type: p.part_type ?? undefined,
    category_set_to: p.set_category ?? undefined,
    problems: p.problems.length ? p.problems : undefined,
    notes: p.notes.length ? p.notes : undefined,
  };
}

export const FMC_FAMILY_DESCRIPTION =
  "Formcraft family builder (FMC-BIM-STD-002 4-5, Family Development Process stages 4-5). Builds data-shell families " +
    "in one batch from the kit's FMC_Family_Specs.json: right template and category, dictionary shared parameters " +
    "(asset and electrical packs included), electrical family parameters linked to connectors, a placeholder box carrying " +
    "every MEP connector, Part Type and types. Saves to <family_staging_root>\\<Role>\\ only; the Family Librarian " +
    "releases. Actions: plan (check only), build (dry run unless dry_run=false), load_test (loads staged families into " +
    "the open FMC_ChainTest project, places and flexes them, checks connectors and parameters). Select families by name " +
    "or by role and priority; at most 25 per call.";

/** Arguments of the fmc_family tool. The Formcraft Browser's CLI (fmc/cli.ts) parses with the same schema, so defaults match. */
export const FMC_FAMILY_SHAPE = {
  action: z.enum(["plan", "build", "load_test"]),
  families: z.array(z.string()).optional().describe("Family names from the register"),
  role: z.string().optional().describe("Role code, e.g. P, E, Z (used when families is empty)"),
  priority: z.enum(["P1", "P2", "P3"]).optional(),
  limit: z.number().int().min(1).max(25).optional().default(25),
  only_ready: z.boolean().optional().default(true).describe("build: skip families with problems instead of refusing the batch"),
  overwrite: z.boolean().optional().default(false).describe("build: replace families already in staging (needs confirm=true)"),
  dry_run: z.boolean().optional().default(true),
  confirm: z.boolean().optional().default(false),
};

export type FmcFamilyArgs = z.infer<z.ZodObject<typeof FMC_FAMILY_SHAPE>>;

/** The whole tool: checks, dry run, Revit call and log row. Claude and the Formcraft Browser both run this. */
export async function runFmcFamily(args: FmcFamilyArgs) {
  const { config, problem } = loadConfig();
  const dry = args.dry_run !== false;
  try {
    if (problem) return reply({ refused: true, reasons: [problem] }, true);
    let specs: SpecFile;
    let dictionaryText: string;
    try {
      specs = JSON.parse(fs.readFileSync(config.specs_path, "utf8").replace(/^﻿/, ""));
    } catch (e) {
      return reply({ refused: true, reasons: [`Cannot read the family specs at ${config.specs_path}: run the kit build (py build_all.py)`] }, true);
    }
    try {
      // Revit writes shared parameter files as UTF-16 LE; the kit writes UTF-8. Read either.
      const buf = fs.readFileSync(config.shared_parameter_file);
      dictionaryText = buf[0] === 0xff && buf[1] === 0xfe ? buf.toString("utf16le") : buf.toString("utf8");
    } catch {
      return reply({ refused: true, reasons: [`Cannot read the shared parameter file at ${config.shared_parameter_file}`] }, true);
    }
    const dictionary = parseSharedParameters(dictionaryText);
    if (!args.families?.length && !args.role && !args.priority)
      return reply({ refused: true, reasons: ["Name families, or give a role and/or priority"] }, true);

    const sel = selectFamilies(specs, { families: args.families, role: args.role, priority: args.priority, limit: args.limit });
    const overwrite = args.overwrite && args.confirm;
    const plans = sel.selected.map((s) => planFamily(s, { cfg: config, dictionary, exists: fs.existsSync, overwrite }));
    const ready = plans.filter((p) => p.problems.length === 0);
    const header = {
      specs_version: specs.version, selected: plans.length, ready: ready.length,
      unknown_families: sel.unknown.length ? sel.unknown : undefined,
      not_selected_over_limit: sel.truncated || undefined,
    };

    if (args.action === "plan") return reply({ ...header, families: plans.map(brief) });

    if (args.action === "build") {
      const reasons: string[] = [];
      if (args.overwrite && !args.confirm) reasons.push("overwrite needs confirm=true");
      if (!ready.length) reasons.push("No family in the selection is ready to build");
      if (!args.only_ready && ready.length !== plans.length) reasons.push("Some families have problems and only_ready=false");
      if (reasons.length) {
        log(config, "family_build", dry, `${plans.length} families`, "refused", reasons.join(" | "));
        return reply({ refused: true, reasons, ...header, families: plans.map(brief) }, true);
      }
      if (dry) return reply({ dry_run: true, ...header, would_build: ready.map(brief),
        skipped: plans.filter((p) => p.problems.length).map(brief), next: "Run again with dry_run=false" });
      const payload = {
        shared_parameter_file: config.shared_parameter_file, max_backups: config.max_backups, overwrite,
        families: ready,
      };
      const result = await withRevitConnection((c) => c.sendRaw("fmc_build_families", payload, 60000 + 60000 * ready.length));
      log(config, "family_build", false, `${ready.length} families`, "done", JSON.stringify(result).slice(0, 2000));
      return reply({ done: true, result });
    }

    // load_test: the chain-test project must be the active document; the live-model lock enforces it
    const staged = ready.length ? ready : plans;
    const files = staged.filter((p) => fs.existsSync(p.save_path));
    const missing = staged.filter((p) => !fs.existsSync(p.save_path)).map((p) => p.family);
    if (!files.length) return reply({ refused: true, reasons: ["None of the selected families is in staging yet: build first"], missing }, true);
    const tests = files.map((p) => ({
      family: p.family, path: p.save_path, types: p.types,
      expected_connectors: expectedConnectorCounts(p),
      expected_parameters: p.shared_parameters.map((s) => s.name),
    }));
    if (dry) return reply({ dry_run: true, would_load_and_place: tests.map((t) => t.family), missing: missing.length ? missing : undefined,
      next: "Open FMC_ChainTest_<Role>.rvt from _ChainTests as the active document, then run with dry_run=false" });
    const result = await withRevitConnection((c) => c.sendCommand("fmc_load_test_families", { families: tests }, 60000 + 30000 * tests.length));
    log(config, "family_load_test", false, `${tests.length} families`, "done", JSON.stringify(result).slice(0, 2000));
    return reply({ done: true, missing: missing.length ? missing : undefined, result });
  } catch (error) {
    const msg = error instanceof Error ? error.message : String(error);
    log(config, `family_${args.action}`, dry, "", "error", msg);
    return reply({ error: msg }, true);
  }
}

export function registerFmcFamilyTool(server: McpServer) {
  server.tool("fmc_family", FMC_FAMILY_DESCRIPTION, FMC_FAMILY_SHAPE, (args) => runFmcFamily(args));
}
