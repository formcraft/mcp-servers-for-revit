/**
 * Formcraft family builder: reads the kit's structured family specs and the shared parameter file, and turns
 * each family into a resolved build plan with every problem found before Revit is touched.
 *
 * Inputs come from the kit (FMC_Kit_v2.2):
 *  - 03_Master_Data/FMC_Family_Specs.json   written by _build/build_family_specs.py
 *  - 01_Shared_Parameters/FMC_SharedParameters.txt   the dictionary (Manual Part 5)
 * Pure functions (Node built-ins only) so they can be unit tested without Revit.
 */
import path from "node:path";
import { GuardConfig, checkWritePath } from "./policy.js";

const win = path.win32;

export interface SpParam { name: string; guid: string; datatype: string; group: string }

/** Parses a Revit shared parameter file (tab separated, META 2 1). */
export function parseSharedParameters(text: string): Map<string, SpParam> {
  const groups = new Map<string, string>();
  const params = new Map<string, SpParam>();
  for (const line of text.replace(/^﻿/, "").split(/\r?\n/)) {
    const f = line.split("\t");
    if (f[0] === "GROUP" && f.length >= 3) groups.set(f[1], f[2]);
    else if (f[0] === "PARAM" && f.length >= 6) params.set(f[2], { name: f[2], guid: f[1], datatype: f[3], group: f[5] });
  }
  for (const p of params.values()) p.group = groups.get(p.group) ?? p.group;
  return params;
}

export interface SpecConnector { domain: string; [key: string]: unknown }
export interface FamilySpec {
  family: string; role: string; category: string; base_category: string; template: string;
  template_file: string | null; set_category?: string | null; host: string; priority: string; types: string[];
  connectors: SpecConnector[] | null; packs: string[];
  shared_parameters: Array<{ name: string; binding: string | null; guid: string | null }>;
  family_parameters: Array<{ name: string; level: string; rule: string }>;
  open_item: string;
}
/** pilot_set: the kit's pilot families (fmc_family_specs.PILOT_SET), the pane's default selection. */
export interface SpecFile { version: string; pilot_set?: string[]; families: FamilySpec[] }

export interface Selection { families?: string[]; role?: string; priority?: string; limit?: number }

/** Picks families by name, or by role and priority. Names not in the specs are returned as unknown. */
export function selectFamilies(specs: SpecFile, sel: Selection): { selected: FamilySpec[]; unknown: string[]; truncated: number } {
  const byName = new Map(specs.families.map((s) => [s.family, s]));
  let selected: FamilySpec[];
  const unknown: string[] = [];
  if (sel.families && sel.families.length) {
    selected = [];
    for (const n of sel.families) {
      const s = byName.get(n);
      if (s) selected.push(s);
      else unknown.push(n);
    }
  } else {
    selected = specs.families.filter((s) =>
      (!sel.role || s.role === sel.role) && (!sel.priority || s.priority === sel.priority));
  }
  const limit = sel.limit ?? 25;
  const truncated = Math.max(0, selected.length - limit);
  return { selected: selected.slice(0, limit), unknown, truncated };
}

const MEP_DOMAINS = new Set(["Electrical", "Piping", "Duct", "ELV"]);

export interface FamilyPlan {
  family: string; role: string; category: string;
  template_path: string; save_path: string;
  /** BuiltInCategory the builder sets when the template is a stand-in (Generic Model, Generic Tag) */
  set_category: string | null;
  shared_parameters: Array<{ name: string; guid: string; group: string; datatype: string; instance: boolean }>;
  family_parameters: Array<{ name: string; level: string; rule: string }>;
  connectors: SpecConnector[];
  part_type: string | null;
  types: string[];
  geometry: "box" | "none";
  problems: string[];
  notes: string[];
}

export interface PlanContext {
  cfg: GuardConfig;
  dictionary: Map<string, SpParam>;
  exists: (p: string) => boolean;
  overwrite?: boolean;
}

/** Revit rejects these characters in type names. */
const BAD_TYPE_CHARS = /[\\:{}[\]|;<>?`~]/;

export function planFamily(spec: FamilySpec, ctx: PlanContext): FamilyPlan {
  const problems: string[] = [];
  const notes: string[] = [];
  const templatePath = spec.template_file ? win.join(ctx.cfg.family_template_dir, spec.template_file) : "";
  if (!spec.template_file) problems.push(`No Revit family template named ("${spec.template}"): build this family by hand`);
  else if (!ctx.exists(templatePath)) problems.push(`Template not found: ${templatePath}`);

  const savePath = win.join(ctx.cfg.family_staging_root, spec.role, `${spec.family}.rfa`);
  const v = checkWritePath(savePath, ctx.cfg);
  problems.push(...v.reasons);
  if (ctx.exists(savePath) && !ctx.overwrite) problems.push(`${savePath} exists; set overwrite=true and confirm=true to rebuild it`);

  if (spec.connectors === null) problems.push("Connectors not specified in the kit (fmc_family_specs.py): fix the kit first");

  const shared: FamilyPlan["shared_parameters"] = [];
  for (const p of spec.shared_parameters) {
    const d = ctx.dictionary.get(p.name);
    if (!d) {
      notes.push(`Skipped ${p.name}: not in the shared parameter file (dictionary change request needed)`);
      continue;
    }
    if (p.guid && p.guid.toLowerCase() !== d.guid.toLowerCase())
      problems.push(`${p.name}: GUID in the specs differs from the shared parameter file; rebuild the kit`);
    shared.push({ name: d.name, guid: d.guid, group: d.group, datatype: d.datatype, instance: /instance/i.test(p.binding ?? "") });
  }

  const all = spec.connectors ?? [];
  const connectors = all.filter((c) => MEP_DOMAINS.has(c.domain));
  for (const c of all) {
    if (c.domain === "Nested") notes.push(`Nested family ${String(c.family)}${c.optional ? " (optional)" : ""}: load and nest it by hand after both families exist`);
    if (c.domain === "Electrical" && c.role === "switch") notes.push("Switch connector: set by hand (no API system type for switches)");
  }
  const supply = all.find((c) => c.domain === "Electrical" && typeof c.role === "string" && (c.role.startsWith("supply") || c.role === "source"));
  const partType = (supply?.part_type as string | undefined) ?? null;

  const types = spec.types.filter((t) => !BAD_TYPE_CHARS.test(t));
  for (const t of spec.types) if (BAD_TYPE_CHARS.test(t)) notes.push(`Type "${t}" has characters Revit rejects; create it by hand`);
  if (spec.open_item) notes.push(`Open item: ${spec.open_item}`);

  return {
    family: spec.family, role: spec.role, category: spec.category,
    template_path: templatePath, save_path: savePath, set_category: spec.set_category ?? null,
    shared_parameters: shared, family_parameters: spec.family_parameters ?? [],
    connectors: connectors.filter((c) => !(c.domain === "Electrical" && c.role === "switch")),
    part_type: partType, types,
    geometry: connectors.length ? "box" : "none",
    problems, notes,
  };
}

/** What the load test expects to find on a placed instance. */
export function expectedConnectorCounts(plan: FamilyPlan): Record<string, number> {
  const out: Record<string, number> = {};
  for (const c of plan.connectors) {
    const key = c.domain === "ELV" ? "Electrical" : c.domain;
    out[key] = (out[key] ?? 0) + 1;
  }
  return out;
}
