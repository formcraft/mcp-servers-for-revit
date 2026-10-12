import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  DEFAULT_CONFIG, GuardConfig, archivePlan, checkTemplatePath, checkWritePath, csvLine, isInside,
  liveLockVerdict, loadConfig, nameProblems, projectFamily, WRITE_COMMANDS,
} from "./policy.js";

const cfg: GuardConfig = {
  ...DEFAULT_CONFIG,
  write_roots: ["Z:\\Projects\\*\\01_WIP", "Z:\\00_Standards\\04_Families\\_Staging", "Z:\\00_Standards\\06_Templates\\_Staging"],
  template_roots: ["C:\\ProgramData\\Autodesk\\RVT 2025\\Family Templates", "Z:\\00_Standards\\06_Templates"],
  library_roots: ["Z:\\00_Standards\\04_Families\\_Staging", "Z:\\00_Standards\\04_Families\\_ChainTests", "Z:\\00_Standards\\06_Templates\\_Staging"],
};

test("isInside matches folders, one-level wildcards and is case-insensitive", () => {
  assert.ok(isInside("Z:\\Projects\\F26001_Pilot\\01_WIP\\A\\x.rvt", "Z:\\Projects\\*\\01_WIP"));
  assert.ok(isInside("z:/projects/F26001_Pilot/01_wip", "Z:\\Projects\\*\\01_WIP"));
  assert.ok(!isInside("Z:\\Projects\\01_WIP\\x.rvt", "Z:\\Projects\\*\\01_WIP"));
  assert.ok(!isInside("Z:\\Projects\\F26001\\01_WIP\\..\\02_Shared\\x.rvt", "Z:\\Projects\\*\\01_WIP"), "'..' cannot escape the root");
  assert.ok(!isInside("Z:\\Other", "Z:\\Projects"));
});

test("defaults refuse every write", () => {
  const v = checkWritePath("Z:\\Projects\\F26001\\01_WIP\\A\\F26001-FMC-ZZ-ZZ-M3-A-0001.rvt", DEFAULT_CONFIG);
  assert.equal(v.ok, false);
  assert.match(v.reasons.join(" "), /No write folders are configured/);
});

test("a missing or broken config falls back to deny-all", () => {
  const missing = loadConfig("nope.json", () => { throw new Error("ENOENT"); });
  assert.deepEqual(missing.config.write_roots, []);
  assert.ok(missing.problem);
  const broken = loadConfig("bad.json", () => "{ write_roots: [");
  assert.deepEqual(broken.config.write_roots, []);
  const good = loadConfig("good.json", () => "\uFEFF" + JSON.stringify({ write_roots: ["Z:\\X"] }));
  assert.deepEqual(good.config.write_roots, ["Z:\\X"]);
  assert.equal(good.config.live_model_lock, true, "unspecified keys keep the safe default");
});

test("writes go to WIP and staging only, never to CDE gate folders", () => {
  assert.ok(checkWritePath("Z:\\Projects\\F26001_Pilot\\01_WIP\\A\\F26001-FMC-ZZ-ZZ-M3-A-0001.rvt", cfg).ok);
  assert.ok(checkWritePath("Z:\\00_Standards\\04_Families\\_Staging\\FMC_P_MechEq_WaterHeater_Instant.rfa", cfg).ok);
  const released = checkWritePath("Z:\\00_Standards\\04_Families\\P\\FMC_P_MechEq_WaterHeater_Instant.rfa", cfg);
  assert.equal(released.ok, false, "release to 04_Families\\<Role> is the Family Librarian's job");
  const shared = checkWritePath("Z:\\Projects\\F26001_Pilot\\02_Shared\\A\\F26001-FMC-ZZ-ZZ-M3-A-0001.rvt", cfg);
  assert.equal(shared.ok, false);
  assert.match(shared.reasons.join(" "), /CDE gate/);
  assert.equal(checkWritePath("relative\\FMC_A_Door_X.rfa", cfg).ok, false);
});

test("names follow Manual 4.1, 4.6 and 9.10", () => {
  assert.deepEqual(nameProblems("Z:\\a\\F26001-FMC-ZZ-ZZ-M3-A-0001.rvt"), []);
  assert.deepEqual(nameProblems("Z:\\a\\FMC_ChainTest_E.rvt"), []);
  assert.deepEqual(nameProblems("Z:\\a\\FMC_E_EQ_DB_TPN.rfa"), []);
  assert.deepEqual(nameProblems("Z:\\a\\FMC_TAG_E_Circuit.rfa"), []);
  assert.deepEqual(nameProblems("Z:\\a\\FMC_E_Template_v2.2.rte"), []);
  assert.ok(nameProblems("Z:\\a\\PRJ26-001-FMC-ZZ-ZZ-M3-A-0001.rvt").length, "v1 project code with a hyphen is rejected");
  assert.ok(nameProblems("Z:\\a\\F26001-FMC-ZZ-ZZ-XX-A-0001.rvt").some((p) => /Type code/.test(p)));
  assert.ok(nameProblems("Z:\\a\\F26001-FMC-ZZ-ZZ-M3-F-0001.rvt").some((p) => /Role code/.test(p)), "F is reserved");
  assert.ok(nameProblems("Z:\\a\\Water Heater.rfa").length);
  assert.ok(nameProblems("Z:\\a\\notes.txt").length);
  assert.ok(nameProblems("Z:\\" + "a\\".repeat(100) + "FMC_A_Door_X.rfa").some((p) => /200/.test(p)));
});

test("project families (D4): [Project]_[Role]_[CategoryToken]_[Description].rfa, saved in the project's WIP only", () => {
  assert.deepEqual(nameProblems("Z:\\a\\F26001_E_ElecFixture_SocketDouble.rfa"), []);
  assert.deepEqual(projectFamily("F26001_E_ElecFixture_SocketDouble.rfa"), { project: "F26001", role: "E" });
  assert.equal(projectFamily("FMC_E_ElecFixture_SocketDouble.rfa"), null, "FMC_ marks a library family");
  assert.ok(nameProblems("Z:\\a\\F26001_X_ElecFixture_Socket.rfa").some((p) => /Role code X/.test(p)));
  assert.ok(nameProblems("Z:\\a\\F26-001_E_ElecFixture_Socket.rfa").length, "project code with a hyphen is rejected");
  assert.ok(nameProblems("Z:\\a\\F26001_E_Socket.rfa").length, "needs a category token and a description");
  const wip = "Z:\\Projects\\F26001_Pilot\\01_WIP\\E\\Families\\F26001_E_ElecFixture_SocketDouble.rfa";
  assert.ok(checkWritePath(wip, cfg).ok);
  const lib = checkWritePath("Z:\\00_Standards\\04_Families\\_Staging\\E\\F26001_E_ElecFixture_SocketDouble.rfa", cfg);
  assert.match(lib.reasons.join(" "), /does not go into the kit library/);
  const shared = checkWritePath("Z:\\Projects\\F26001_Pilot\\02_Shared\\E\\F26001_E_ElecFixture_SocketDouble.rfa", cfg);
  assert.equal(shared.ok, false);
});

test("templates come from template folders only", () => {
  assert.ok(checkTemplatePath("C:\\ProgramData\\Autodesk\\RVT 2025\\Family Templates\\English\\Metric Mechanical Equipment.rft", cfg).ok);
  assert.equal(checkTemplatePath("C:\\Users\\x\\Downloads\\Metric Door.rft", cfg).ok, false);
  assert.equal(checkTemplatePath("C:\\ProgramData\\Autodesk\\RVT 2025\\Family Templates\\x.rvt", cfg).ok, false);
});

test("live-model lock lets tools work on families, new documents and library files only", () => {
  const live = { title: "F26001-FMC-ZZ-ZZ-M3-A-0001", path: "Z:\\Projects\\F26001\\01_WIP\\A\\F26001-FMC-ZZ-ZZ-M3-A-0001.rvt", isFamily: false, isWorkshared: true };
  assert.equal(liveLockVerdict(live, cfg).ok, false);
  assert.equal(liveLockVerdict({ ...live, isWorkshared: false }, cfg).ok, false, "a non-workshared project model is still live");
  assert.ok(liveLockVerdict({ title: "Family1", path: "", isFamily: true, isWorkshared: false }, cfg).ok);
  assert.ok(liveLockVerdict({ title: "Project1", path: "", isFamily: false, isWorkshared: false }, cfg).ok);
  assert.ok(liveLockVerdict({ title: "FMC_ChainTest_E", path: "Z:\\00_Standards\\04_Families\\_ChainTests\\FMC_ChainTest_E.rvt", isFamily: false, isWorkshared: false }, cfg).ok);
  assert.equal(liveLockVerdict(null, cfg).ok, false);
  assert.ok(liveLockVerdict(live, { ...cfg, live_model_lock: false }).ok);
  assert.ok(WRITE_COMMANDS.has("send_code_to_revit") && WRITE_COMMANDS.has("delete_element") && !WRITE_COMMANDS.has("get_selected_elements"));
});

test("archive moves the file and its backups to _Archive with a timestamp", () => {
  const plan = archivePlan("Z:\\00_Standards\\04_Families\\_Staging\\FMC_A_Door_X.rfa",
    ["FMC_A_Door_X.rfa", "FMC_A_Door_X.0001.rfa", "FMC_A_Door_X.0002.rfa", "FMC_A_Door_XY.0001.rfa", "Other.rfa"],
    new Date(2026, 9, 12, 9, 5, 7));
  assert.deepEqual(plan.moves.map((m) => m[1]), [
    "Z:\\00_Standards\\04_Families\\_Staging\\_Archive\\20261012-090507_FMC_A_Door_X.rfa",
    "Z:\\00_Standards\\04_Families\\_Staging\\_Archive\\20261012-090507_FMC_A_Door_X.0001.rfa",
    "Z:\\00_Standards\\04_Families\\_Staging\\_Archive\\20261012-090507_FMC_A_Door_X.0002.rfa",
  ]);
});

test("csv lines quote commas and quotes", () => {
  assert.equal(csvLine(["a", "b,c", 'd"e', true]), 'a,"b,c","d""e",true\r\n');
});

test("live-model lock agrees with the shared cases (the Formcraft Browser's C# copy runs the same file)", () => {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const fixture = JSON.parse(fs.readFileSync(path.resolve(here, "../../src/fmc/fixtures/lock_cases.json"), "utf8"));
  for (const c of fixture.cases) {
    const cfgCase: GuardConfig = { ...DEFAULT_CONFIG, ...fixture.config, live_model_lock: c.lock ?? fixture.config.live_model_lock };
    assert.equal(liveLockVerdict(c.doc, cfgCase).ok, c.ok, c.name);
  }
});
