import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

// The Formcraft Browser pane runs build/fmc/cli.js as a separate process, so these tests do too. Each run gets its own
// guard file (FMC_GUARD_CONFIG) pointing at the fixtures, and a temporary reports_dir: the user's log is never touched.
// None of these calls reaches Revit: plan, dry runs and refusals are all decided by the server before port 8080.
const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.join(HERE, "cli.js");
const FIX = path.resolve(HERE, "../../src/fmc/fixtures");
const PILOT = JSON.parse(fs.readFileSync(path.join(FIX, "pilot_specs.json"), "utf8")).pilot_set as string[];

function run(command: string, args: unknown, guard: Record<string, unknown> = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "fmc-cli-"));
  const config = path.join(dir, "fmc_guard.json");
  fs.writeFileSync(config, JSON.stringify({
    specs_path: path.join(FIX, "pilot_specs.json"),
    shared_parameter_file: path.join(FIX, "pilot_shared_parameters.txt"),
    reports_dir: dir,
    ...guard,
  }));
  const r = spawnSync(process.execPath, [CLI, command], {
    input: args === undefined ? "" : JSON.stringify(args),
    env: { ...process.env, FMC_GUARD_CONFIG: config },
    encoding: "utf8",
  });
  const log = path.join(dir, "FMC_MCP_FileLog.csv");
  return { code: r.status, out: r.stdout, body: r.stdout.trim() ? JSON.parse(r.stdout) : null, log: fs.existsSync(log) ? fs.readFileSync(log, "utf8") : "" };
}

test("specs lists the families and the kit's pilot set, as pure JSON on stdout", () => {
  const r = run("specs", undefined);
  assert.equal(r.code, 0);
  assert.equal(r.body.families.length, 8);
  assert.deepEqual(r.body.pilot_set, PILOT);
  assert.equal(PILOT.length, 6);
  const heater = r.body.families.find((f: { family: string }) => f.family === "FMC_P_MechEq_WaterHeater_Instant");
  assert.equal(heater.role, "P");
  assert.ok(heater.connectors > 0 && heater.parameters > 0);
});

test("specs refuses with the kit build hint when the specs file is missing", () => {
  const r = run("specs", undefined, { specs_path: path.join(os.tmpdir(), "no-such-specs.json") });
  assert.equal(r.code, 1);
  assert.match(r.body.error, /py build_all\.py/);
});

test("fmc_family plan runs the tool's own checks and reports unknown names", () => {
  const r = run("fmc_family", { action: "plan", families: [...PILOT, "FMC_Nope"] });
  assert.equal(r.code, 0);
  assert.equal(r.body.selected, 6);
  assert.deepEqual(r.body.unknown_families, ["FMC_Nope"]);
  assert.deepEqual(r.body.families.map((f: { family: string }) => f.family).sort(), [...PILOT].sort());
  assert.equal(r.log, "", "a plan is read-only and writes no log row");
});

test("fmc_family build with no write folders is refused and the refusal is logged", () => {
  const r = run("fmc_family", { action: "build", families: PILOT, dry_run: true });
  assert.equal(r.code, 1);
  assert.equal(r.body.refused, true);
  assert.match(r.log, /family_build,true,6 families,refused/);
});

test("fmc_family load_test before any build says to build first", () => {
  const r = run("fmc_family", { action: "load_test", families: PILOT, dry_run: true });
  assert.equal(r.code, 1);
  assert.match(r.body.reasons.join(" "), /build first/);
});

test("fmc_file save_as into a CDE gate folder is refused before Revit is called", () => {
  const root = path.join(os.tmpdir(), "FMC_Test_Projects");
  const r = run("fmc_file", {
    action: "save_as", document: "Project1",
    save_path: path.join(root, "F26001", "03_Published", "F26001-FMC-ZZ-ZZ-M3-A-0001.rvt"),
  }, { write_roots: [path.join(root, "*", "01_WIP")] });
  assert.equal(r.code, 1);
  assert.equal(r.body.refused, true);
  assert.match(r.body.reasons.join(" "), /03_published/i);
  assert.match(r.log, /save_as,true,.*refused/);
});

test("fmc_file save_as dry run in WIP passes without touching Revit", () => {
  const root = path.join(os.tmpdir(), "FMC_Test_Projects");
  const target = path.join(root, "F26001", "01_WIP", "F26001-FMC-ZZ-ZZ-M3-A-0001.rvt");
  const r = run("fmc_file", { action: "save_as", document: "Project1", save_path: target }, { write_roots: [path.join(root, "*", "01_WIP")] });
  assert.equal(r.code, 0, r.out);
  assert.equal(r.body.dry_run, true);
  assert.equal(r.body.would_do.save_path, target);
});

test("bad arguments use the tool's own schema; an unknown command is a usage error", () => {
  const bad = run("fmc_family", { action: "bogus" });
  assert.equal(bad.code, 1);
  assert.match(bad.body.error, /^Bad arguments: action/);
  const limit = run("fmc_family", { action: "plan", families: PILOT, limit: 26 });
  assert.equal(limit.code, 1, "the 25-family batch limit applies to the pane too");
  const usage = run("nope", {});
  assert.equal(usage.code, 2);
  assert.equal(usage.body, null);
});
