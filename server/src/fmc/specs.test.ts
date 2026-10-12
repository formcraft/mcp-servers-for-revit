import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { DEFAULT_CONFIG, GuardConfig } from "./policy.js";
import { SpecFile, expectedConnectorCounts, parseSharedParameters, planFamily, selectFamilies } from "./specs.js";

// Fixtures are cut from the real kit output (FMC_Family_Specs.json v2.2): the pilot families plus edge cases.
const FIX = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../src/fmc/fixtures");
const specs: SpecFile = JSON.parse(fs.readFileSync(path.join(FIX, "pilot_specs.json"), "utf8"));
const dictionary = parseSharedParameters(fs.readFileSync(path.join(FIX, "pilot_shared_parameters.txt"), "utf8"));

const cfg: GuardConfig = {
  ...DEFAULT_CONFIG,
  write_roots: ["Z:\\00_Standards\\04_Families\\_Staging"],
  library_roots: ["Z:\\00_Standards\\04_Families\\_Staging", "Z:\\00_Standards\\04_Families\\_ChainTests"],
};
// The Revit 2025 template files the pilot families resolve to (fmc_family_specs.TEMPLATE_MAP).
const templates = new Set([
  "Metric Generic Model face based.rft", "Metric Mechanical Equipment.rft", "Metric Casework.rft",
  "Titleblocks\\New Size metric.rft", "Metric Electrical Equipment.rft",
].map((t) => path.win32.join(cfg.family_template_dir, t).toLowerCase()));
const exists = (p: string) => templates.has(p.toLowerCase());
const plan = (name: string, extra: Partial<Parameters<typeof planFamily>[1]> = {}) =>
  planFamily(specs.families.find((f) => f.family === name)!, { cfg, dictionary, exists, ...extra });

test("shared parameter file parses names, GUIDs and group names", () => {
  const p = dictionary.get("FMC_Asset_Tag")!;
  assert.match(p.guid, /^[0-9a-f-]{36}$/);
  assert.equal(p.group, "FMC_16_Asset_7D");
});

test("selection by name reports unknown names; by role and priority; capped by limit", () => {
  const a = selectFamilies(specs, { families: ["FMC_P_MechEq_BoosterPumpSet", "FMC_Nope"] });
  assert.deepEqual(a.selected.map((s) => s.family), ["FMC_P_MechEq_BoosterPumpSet"]);
  assert.deepEqual(a.unknown, ["FMC_Nope"]);
  assert.ok(selectFamilies(specs, { role: "FP" }).selected.every((s) => s.role === "FP"));
  const capped = selectFamilies(specs, { role: "", priority: "P1", limit: 2 });
  assert.equal(capped.selected.length, 2);
  assert.ok(capped.truncated > 0);
});

test("instant water heater: P-owned product with pipe and electrical connectors, asset and power packs", () => {
  const p = plan("FMC_P_MechEq_WaterHeater_Instant");
  assert.deepEqual(p.problems, []);
  assert.equal(p.save_path, "Z:\\00_Standards\\04_Families\\_Staging\\P\\FMC_P_MechEq_WaterHeater_Instant.rfa");
  assert.deepEqual(expectedConnectorCounts(p), { Piping: 2, Electrical: 1 });
  const names = p.shared_parameters.map((s) => s.name);
  assert.ok(names.includes("FMC_Power_W") && names.includes("FMC_Asset_Tag") && names.includes("FMC_Serial_Number"));
  assert.equal(p.shared_parameters.find((s) => s.name === "FMC_Asset_Tag")!.instance, true, "asset tag is an instance parameter");
  assert.equal(p.shared_parameters.find((s) => s.name === "FMC_Power_W")!.instance, false, "rated power is a type parameter");
  assert.ok(p.family_parameters.some((f) => f.name === "Apparent Load"));
  assert.equal(p.geometry, "box");
});

test("fire pump: two motor circuits; the missing FMC_Power_W_2 is skipped and reported, never invented", () => {
  const p = plan("FMC_FP_MechEq_FirePumpSet");
  assert.deepEqual(p.problems, []);
  assert.equal(p.connectors.filter((c) => c.domain === "Electrical").length, 2);
  assert.ok(!p.shared_parameters.some((s) => s.name === "FMC_Power_W_2"));
  assert.ok(p.notes.some((n) => /FMC_Power_W_2.*change request/.test(n)));
});

test("vanity: nested basin is a note, not a connector the builder fakes", () => {
  const p = plan("FMC_I_Casework_Vanity");
  assert.equal(p.connectors.length, 0);
  assert.equal(p.geometry, "none");
  assert.ok(p.notes.some((n) => /Nested family FMC_P_PlumbingFixture_Basin/.test(n)));
});

test("face-based MEP devices use the Generic Model face based template and get their category set", () => {
  const heater = plan("FMC_P_MechEq_WaterHeater_Instant");
  assert.match(heater.template_path, /Metric Generic Model face based\.rft$/);
  assert.equal(heater.set_category, "OST_MechanicalEquipment");
  assert.equal(plan("FMC_E_ElecFixture_PowerConnection").set_category, "OST_ElectricalFixtures");
  assert.equal(plan("FMC_FP_SpecEq_Extinguisher").set_category, "OST_SpecialityEquipment");
  assert.equal(plan("FMC_P_MechEq_BoosterPumpSet").set_category, null, "a matching template keeps its own category");
  assert.match(plan("FMC_TB_Panel_SiteData").template_path, /Titleblocks\\New Size metric\.rft$/);
});

test("every pilot family is ready once the Revit 2025 templates exist", () => {
  for (const f of specs.families) assert.deepEqual(plan(f.family).problems, [], f.family);
});

test("single-phase board carries its Part Type from the register", () => {
  assert.equal(plan("FMC_E_EQ_DB_SPN").part_type, "Panelboard");
});

test("missing template, existing file and a closed staging folder are problems", () => {
  const noTemplate = planFamily(specs.families.find((f) => f.family === "FMC_P_MechEq_BoosterPumpSet")!,
    { cfg, dictionary, exists: () => false });
  assert.match(noTemplate.problems.join(" "), /Template not found/);
  const existing = plan("FMC_P_MechEq_BoosterPumpSet", { exists: () => true });
  assert.match(existing.problems.join(" "), /exists; set overwrite=true/);
  assert.equal(plan("FMC_P_MechEq_BoosterPumpSet", { exists: () => true, overwrite: true }).problems.length, 0);
  const closed = plan("FMC_P_MechEq_BoosterPumpSet", { cfg: { ...cfg, write_roots: [] } });
  assert.match(closed.problems.join(" "), /No write folders/);
});

test("staging outside write_roots or inside a gated folder is refused", () => {
  const p = plan("FMC_FP_SpecEq_Extinguisher", { cfg: { ...cfg, family_staging_root: "Z:\\00_Standards\\04_Families" } });
  assert.match(p.problems.join(" "), /Not inside an allowed write folder/);
});

test("annotation panel builds from the title block template with no connectors", () => {
  const p = plan("FMC_TB_Panel_SiteData");
  assert.deepEqual(p.problems, []);
  assert.equal(p.geometry, "none");
});
