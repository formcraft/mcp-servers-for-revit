/**
 * Formcraft guard rules for Revit file operations and model-changing tools.
 *
 * Pure functions (Node built-ins only) so they can be unit tested without Revit.
 * Rules come from the Formcraft Master Manual FMC-BIM-MAN-001 v2.2:
 *  - Part 4    container and family names
 *  - Part 7    containers move through CDE states by a gate, never by a tool (no writes to 02_Shared,
 *              03_Published, 04_Archive, 05_Incoming); live centrals never sit in a synced folder
 *  - Part 9.12 only the Family Librarian releases families: tools write to staging, never to 04_Families\<Role>
 *  - Part 17.2 dry run first, loud failure, logged, never touch approved work
 *  - Part 18   Claude does not model live projects: model-changing tools only run on library files
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const win = path.win32;

export interface GuardConfig {
  /** Folders a tool may write into. "*" matches one folder level, e.g. Z:\Projects\*\01_WIP */
  write_roots: string[];
  /** Folders templates (.rft / .rte) may be read from */
  template_roots: string[];
  /** Library and test folders: model-changing tools are allowed on documents saved here */
  library_roots: string[];
  /** Folder names no tool may write into, wherever they appear in a path */
  blocked_segments: string[];
  /** Refuse model-changing tools when the active document is a live project model */
  live_model_lock: boolean;
  /** Where the CSV log goes (Manual 17.2 rule 3) */
  reports_dir: string;
  /** Number of .000N backups Revit keeps on save */
  max_backups: number;
  /** Family builder: structured specs written by the kit (_build/build_family_specs.py) */
  specs_path: string;
  /** Family builder: the dictionary. Shared parameters are only ever taken from this file */
  shared_parameter_file: string;
  /** Family builder: folder holding the Revit family templates (.rft) named in the specs */
  family_template_dir: string;
  /** Family builder: new families are saved to <family_staging_root>\<Role>\ (must be inside write_roots) */
  family_staging_root: string;
}

export const DEFAULT_CONFIG: GuardConfig = {
  write_roots: [], // nothing is writable until the BIM Manager names the folders
  template_roots: [],
  library_roots: [],
  blocked_segments: ["02_Shared", "03_Published", "04_Archive", "05_Incoming"],
  live_model_lock: true,
  reports_dir: "%USERPROFILE%\\FMC_Reports",
  max_backups: 3,
  specs_path: "Z:\\00_Standards\\03_Master_Data\\FMC_Family_Specs.json",
  shared_parameter_file: "Z:\\00_Standards\\01_Shared_Parameters\\FMC_SharedParameters.txt",
  family_template_dir: "C:\\ProgramData\\Autodesk\\RVT 2025\\Family Templates\\English",
  family_staging_root: "Z:\\00_Standards\\04_Families\\_Staging",
};

/** Tools that change the model. They are refused on live project models. */
export const WRITE_COMMANDS = new Set<string>([
  "create_point_based_element", "create_line_based_element", "create_surface_based_element",
  "create_level", "create_grid", "create_room", "create_dimensions", "create_structural_framing_system",
  "operate_element", "delete_element", "color_splash", "tag_walls", "tag_rooms", "send_code_to_revit",
  "fmc_load_test_families",
]);

export const ROLE_CODES = ["A", "S", "C", "E", "M", "P", "Q", "Z", "I", "L", "LP", "FP", "G", "W", "K", "Y"];
export const TYPE_CODES = ["M3", "M2", "DR", "SH", "SP", "RP", "IE", "PP", "RI", "CM", "CR", "MR", "BQ", "CA", "MI", "VS", "CO", "SU"];

export function configPath(env: NodeJS.ProcessEnv = process.env): string {
  if (env.FMC_GUARD_CONFIG) return env.FMC_GUARD_CONFIG;
  const appData = env.APPDATA || win.join(os.homedir(), "AppData", "Roaming");
  return win.join(appData, "Formcraft", "fmc_guard.json");
}

export function loadConfig(file: string = configPath(), reader: (p: string) => string = (p) => fs.readFileSync(p, "utf8")): {
  config: GuardConfig; source: string; problem?: string;
} {
  let raw: string;
  try {
    raw = reader(file);
  } catch {
    return { config: { ...DEFAULT_CONFIG }, source: "defaults", problem: `No guard config at ${file}: every write is refused until it exists` };
  }
  try {
    const parsed = JSON.parse(raw.replace(/^\uFEFF/, ""));
    return { config: { ...DEFAULT_CONFIG, ...parsed }, source: file };
  } catch (e) {
    // A broken config must never widen access: fall back to the deny-all defaults.
    return { config: { ...DEFAULT_CONFIG }, source: "defaults", problem: `Guard config ${file} is not valid JSON: every write is refused` };
  }
}

export function expandEnv(p: string, env: NodeJS.ProcessEnv = process.env): string {
  return p.replace(/%([^%]+)%/g, (m, name) => env[name] ?? env[name.toUpperCase()] ?? m);
}

function norm(p: string): string {
  return win.normalize(expandEnv(p)).replace(/[\\/]+$/, "");
}

function segments(p: string): string[] {
  return norm(p).toLowerCase().split("\\").filter(Boolean);
}

/** True when target is root itself or inside root. "*" in root matches exactly one folder. */
export function isInside(target: string, root: string): boolean {
  const t = segments(target);
  const r = segments(root);
  if (r.length === 0 || t.length < r.length) return false;
  return r.every((seg, i) => seg === "*" || seg === t[i]);
}

export interface Verdict { ok: boolean; reasons: string[] }

/**
 * Project family (decision D4, 12 Oct 2026): [Project]_[Role]_[CategoryToken]_[Description].rfa, e.g.
 * F26001_E_ElecFixture_SocketDouble.rfa. The project code follows Manual 4.1 (2-6 characters A-Z/0-9) and is never
 * "FMC", which marks library families. Returns null for any other name.
 */
export function projectFamily(base: string): { project: string; role: string } | null {
  const m = /^([A-Z0-9]{2,6})_([A-Z]{1,2})_[A-Za-z0-9]+(_[A-Za-z0-9]+)+\.rfa$/.exec(base);
  if (!m || m[1] === "FMC") return null;
  return { project: m[1], role: m[2] };
}

export function nameProblems(file: string): string[] {
  const base = win.basename(file);
  const ext = win.extname(base).toLowerCase();
  const out: string[] = [];
  if (norm(file).length >= 200) out.push(`Full path is ${norm(file).length} characters; Manual 4.1 keeps it under 200`);
  if (ext === ".rfa") {
    const project = projectFamily(base);
    if (project) {
      if (!ROLE_CODES.includes(project.role)) out.push(`Role code ${project.role} in ${base} is not in Manual 4.3`);
    } else if (!/^FMC_[A-Za-z0-9]+(_[A-Za-z0-9]+)+\.rfa$/.test(base)) {
      out.push(`Family file ${base} follows neither FMC_[Role]_[CategoryToken]_[Description].rfa (library, Manual 4.6) ` +
        "nor [Project]_[Role]_[CategoryToken]_[Description].rfa (project family, decision D4)");
    }
  } else if (ext === ".rte") {
    if (!/^FMC_(Base|[A-Z]{1,2})_Template_v\d+\.\d+\.rte$/.test(base))
      out.push(`Template ${base} does not follow FMC_<Role>_Template_v<version>.rte (Manual 9.10)`);
  } else if (ext === ".rvt") {
    const chainTest = /^FMC_ChainTest_[A-Z]{1,2}\.rvt$/.test(base);
    const m = /^([A-Z0-9]{2,6})-([A-Z0-9]{3,6})-([A-Z0-9]{2})-([A-Z0-9]{2})-([A-Z0-9]{2})-([A-Z]{1,2})-(\d{4})\.rvt$/.exec(base);
    if (!chainTest) {
      if (!m) out.push(`Model ${base} is not a container name [Project]-[Originator]-[Volume]-[Level]-[Type]-[Role]-[Number].rvt (Manual 4.1)`);
      else {
        if (!TYPE_CODES.includes(m[5])) out.push(`Type code ${m[5]} is not in Manual 4.2`);
        if (!ROLE_CODES.includes(m[6])) out.push(`Role code ${m[6]} is not in Manual 4.3`);
      }
    }
  } else {
    out.push(`Extension ${ext || "(none)"} is not .rvt, .rte or .rfa`);
  }
  return out;
}

/** May a tool write (save, save as, create, archive) at this path? */
export function checkWritePath(target: string, cfg: GuardConfig, opts: { checkName?: boolean } = {}): Verdict {
  const reasons: string[] = [];
  if (!win.isAbsolute(expandEnv(target))) reasons.push(`Path must be absolute: ${target}`);
  const segs = segments(target);
  const blocked = cfg.blocked_segments.map((s) => s.toLowerCase()).filter((s) => segs.includes(s));
  if (blocked.length) reasons.push(`Writing into ${blocked.join(", ")} is a CDE gate with a named approver, not a tool action (Manual 7.5)`);
  if (!cfg.write_roots.some((r) => isInside(target, r))) {
    reasons.push(cfg.write_roots.length
      ? `Not inside an allowed write folder: ${cfg.write_roots.join("; ")}`
      : "No write folders are configured (write_roots is empty in fmc_guard.json)");
  }
  if (opts.checkName !== false) {
    reasons.push(...nameProblems(target));
    // D4: project families live in the project's WIP (01_WIP\<Role>\Families), never in the kit library.
    if (projectFamily(win.basename(target))) {
      if (cfg.library_roots.some((r) => isInside(target, r)))
        reasons.push("A project family does not go into the kit library; save it in the project's 01_WIP\\<Role>\\Families (decision D4)");
      else if (!segs.includes("01_wip"))
        reasons.push("A project family is saved in the project's 01_WIP\\<Role>\\Families (decision D4)");
    }
  }
  return { ok: reasons.length === 0, reasons };
}

export function checkTemplatePath(template: string, cfg: GuardConfig): Verdict {
  const reasons: string[] = [];
  const ext = win.extname(template).toLowerCase();
  if (ext !== ".rft" && ext !== ".rte") reasons.push(`Template must be .rft or .rte, got ${ext || "(none)"}`);
  if (!cfg.template_roots.some((r) => isInside(template, r))) {
    reasons.push(cfg.template_roots.length
      ? `Template is not inside an allowed template folder: ${cfg.template_roots.join("; ")}`
      : "No template folders are configured (template_roots is empty in fmc_guard.json)");
  }
  return { ok: reasons.length === 0, reasons };
}

export interface DocInfo { title: string; path: string; isFamily: boolean; isWorkshared: boolean; isActive?: boolean }

/** Live-model lock: may a model-changing tool run on the active document? */
export function liveLockVerdict(active: DocInfo | null | undefined, cfg: GuardConfig): Verdict {
  if (!cfg.live_model_lock) return { ok: true, reasons: [] };
  if (!active) return { ok: false, reasons: ["No active document in Revit"] };
  if (active.isFamily) return { ok: true, reasons: [] };
  if (!active.path) return { ok: true, reasons: [] }; // new, unsaved document created by a tool
  if (cfg.library_roots.some((r) => isInside(active.path, r)) && !active.isWorkshared) return { ok: true, reasons: [] };
  return {
    ok: false,
    reasons: [`Live-model lock: ${active.title} is a project model outside the library folders. ` +
      "Model-changing tools run only on families, new unsaved documents and files in library_roots " +
      "(Manual Part 18: modelling on live projects is done by assessed modellers). Read-only tools still work."],
  };
}

/** Where archive() moves a file: <folder>\_Archive\<yyyyMMdd-HHmmss>_<name>, plus its .000N backups. */
export function archivePlan(file: string, siblings: string[], now: Date = new Date()): { moves: Array<[string, string]> } {
  const dir = win.dirname(norm(file));
  const base = win.basename(file);
  const ext = win.extname(base);
  const stem = base.slice(0, base.length - ext.length);
  const pad = (n: number) => String(n).padStart(2, "0");
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
  const dest = win.join(dir, "_Archive");
  const backup = new RegExp(`^${stem.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\.\\d{4}${ext.replace(".", "\\.")}$`, "i");
  const names = [base, ...siblings.filter((s) => backup.test(s))];
  return { moves: names.map((n) => [win.join(dir, n), win.join(dest, `${stamp}_${n}`)] as [string, string]) };
}

export function csvLine(fields: Array<string | number | boolean>): string {
  return fields.map((f) => {
    const s = String(f);
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  }).join(",") + "\r\n";
}
