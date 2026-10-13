/**
 * Ask Claude from the Formcraft Browser pane (test build, read-only).
 *
 * The pane's command box sends "ask <question>" to the CLI, which calls the Claude API with the firm's own API key and
 * lets Claude use a short list of READ-ONLY Revit tools (document, view, selection, statistics, quantities). Nothing in
 * this file can change the model, write a file or touch the guard: every tool here is a read, and the test in
 * ask.test.ts fails if one of them is ever a model-changing command.
 *
 * Billing: API keys are billed per token on the Claude Console account that owns the key (Max and Team plans include
 * monthly API credits once a Console organization is linked; otherwise prepaid Console credits). This is separate from
 * the Claude app's plan limits and usage credits.
 *
 * Key:     ANTHROPIC_API_KEY, else the first line of %APPDATA%\Formcraft\anthropic_api_key.txt (never logged).
 * Model:   FMC_ASK_MODEL (default claude-sonnet-5-5).
 * Budget:  FMC_ASK_DAILY_TOKENS (default 200000 input+output tokens per day on this PC); refused once reached.
 * Log:     <reports_dir>\FMC_Ask_Log.csv — time, model, tools used, tokens, result. Never the question or answer text.
 */
import fs from "node:fs";
import path from "node:path";
import { csvLine, expandEnv, loadConfig, WRITE_COMMANDS } from "./policy.js";

const win = path.win32;

export const ASK_DEFAULTS = {
  model: "claude-sonnet-5-5",
  maxTokens: 1200,
  maxToolTurns: 6,
  dailyTokens: 200_000,
  toolResultChars: 12_000,
  apiUrl: "https://api.anthropic.com/v1/messages",
  apiVersion: "2023-06-01",
};

/** The read-only tools Claude may call from the pane. `command` is the Revit command each one sends. */
export const ASK_TOOLS: Array<{ name: string; command: string; description: string; input_schema: object; params: (a: any) => object }> = [
  {
    name: "document_info", command: "fmc_document_info",
    description: "The open Revit documents: title, path, whether workshared, family or project. Call first when the question is about 'this model'.",
    input_schema: { type: "object", properties: {} },
    params: () => ({}),
  },
  {
    name: "current_view_info", command: "get_current_view_info",
    description: "The active view: type, name, scale, detail level.",
    input_schema: { type: "object", properties: {} },
    params: () => ({}),
  },
  {
    name: "selected_elements", command: "get_selected_elements",
    description: "Elements the user has selected in Revit (ids, categories, types).",
    input_schema: { type: "object", properties: { limit: { type: "number", description: "Max elements, default 50" } } },
    params: (a) => ({ limit: Math.min(Number(a?.limit) || 50, 200) }),
  },
  {
    name: "view_elements", command: "get_current_view_elements",
    description: "Elements visible in the active view, filtered by category names such as OST_Walls or OST_ElectricalFixtures.",
    input_schema: {
      type: "object",
      properties: {
        modelCategoryList: { type: "array", items: { type: "string" } },
        annotationCategoryList: { type: "array", items: { type: "string" } },
        limit: { type: "number", description: "Max elements, default 100" },
      },
    },
    params: (a) => ({
      modelCategoryList: a?.modelCategoryList ?? [], annotationCategoryList: a?.annotationCategoryList ?? [],
      includeHidden: false, limit: Math.min(Number(a?.limit) || 100, 200),
    }),
  },
  {
    name: "model_statistics", command: "analyze_model_statistics",
    description: "Counts for the whole model: elements by category and level, types, families, views, sheets.",
    input_schema: { type: "object", properties: { includeDetailedTypes: { type: "boolean", description: "Default false (smaller)" } } },
    params: (a) => ({ includeDetailedTypes: a?.includeDetailedTypes === true }),
  },
  {
    name: "material_quantities", command: "get_material_quantities",
    description: "Material areas, volumes and element counts, optionally by category (e.g. ['OST_Walls']) or only the selection.",
    input_schema: {
      type: "object",
      properties: { categoryFilters: { type: "array", items: { type: "string" } }, selectedElementsOnly: { type: "boolean" } },
    },
    params: (a) => ({ categoryFilters: a?.categoryFilters ?? null, selectedElementsOnly: a?.selectedElementsOnly === true }),
  },
];

export const ASK_SYSTEM = [
  "You are Claude inside the Formcraft Browser pane in Autodesk Revit 2025, used by a Formcraft BIM team (Uganda).",
  "This is a READ-ONLY test build: you can look at the open model through the tools given, and you cannot change",
  "anything. If the user asks for a change, say what you would do and that changes go through the pane's buttons",
  "(with their dry run) or Claude Desktop, not this box.",
  "Formcraft rules: never decide for a professional (no approving gates, releasing families, setting demand factors or",
  "signing anything); never claim something was changed; workshared models are synced by people, not tools.",
  "Names follow FMC-BIM-STD-002: models [Project]-[Originator]-[Volume]-[Level]-[Type]-[Role]-[Number].rvt,",
  "library families FMC_[Role]_[CategoryToken]_[Description].rfa.",
  "Answer briefly in plain English for a busy modeller: lead with the answer, then at most a few lines of detail.",
  "Quote element ids when you refer to elements. Call as few tools as you need; ask for small results.",
].join(" ");

export interface AskArgs { question: string; history?: Array<{ role: "user" | "assistant"; text: string }> }
export interface AskDeps {
  fetch: typeof fetch;
  revit: (command: string, params: object) => Promise<unknown>;
  apiKey: () => string | null;
  now: () => Date;
  env: NodeJS.ProcessEnv;
  logFile: () => string | null;
}

export function readApiKey(env: NodeJS.ProcessEnv = process.env): string | null {
  if (env.ANTHROPIC_API_KEY?.trim()) return env.ANTHROPIC_API_KEY.trim();
  const appData = env.APPDATA;
  if (!appData) return null;
  try {
    const line = fs.readFileSync(win.join(appData, "Formcraft", "anthropic_api_key.txt"), "utf8").replace(/^﻿/, "").split(/\r?\n/)[0].trim();
    return line || null;
  } catch {
    return null;
  }
}

export function askLogFile(): string | null {
  try {
    const { config } = loadConfig();
    return win.join(expandEnv(config.reports_dir), "FMC_Ask_Log.csv");
  } catch {
    return null;
  }
}

/** Tokens already used today, read from the ask log (the log is the budget's only memory). */
export function tokensToday(logText: string, now: Date): number {
  const day = now.toISOString().slice(0, 10);
  let total = 0;
  for (const line of logText.split(/\r?\n/).slice(1)) {
    const cells = line.split(",");
    if (cells[0]?.replace(/"/g, "").startsWith(day)) total += (Number(cells[3]) || 0) + (Number(cells[4]) || 0);
  }
  return total;
}

export function trimResult(text: string, max: number = ASK_DEFAULTS.toolResultChars): string {
  return text.length <= max ? text : text.slice(0, max) + `\n…[cut at ${max} characters: ask for fewer elements or a narrower category]`;
}

function writeLog(file: string | null, row: Array<string | number | boolean>) {
  if (!file) return;
  try {
    fs.mkdirSync(win.dirname(file), { recursive: true });
    if (!fs.existsSync(file)) fs.writeFileSync(file, csvLine(["timestamp", "model", "tools", "input_tokens", "output_tokens", "result"]));
    fs.appendFileSync(file, csvLine(row));
  } catch {
    // logging never blocks the answer
  }
}

export async function runAsk(args: AskArgs, deps: AskDeps): Promise<{ ok: boolean; body: Record<string, unknown> }> {
  const question = (args.question ?? "").trim();
  if (!question) return { ok: false, body: { error: "Type a question after ask, e.g. ask how many sheets are in this model?" } };

  const key = deps.apiKey();
  if (!key) {
    return { ok: false, body: { error: "No Claude API key on this PC. Create one in the Claude Console (platform.claude.com) and save it as the first line of %APPDATA%\\Formcraft\\anthropic_api_key.txt, or set ANTHROPIC_API_KEY." } };
  }

  const model = deps.env.FMC_ASK_MODEL?.trim() || ASK_DEFAULTS.model;
  const daily = Number(deps.env.FMC_ASK_DAILY_TOKENS) || ASK_DEFAULTS.dailyTokens;
  const logFile = deps.logFile();
  let used = 0;
  try { used = logFile && fs.existsSync(logFile) ? tokensToday(fs.readFileSync(logFile, "utf8"), deps.now()) : 0; } catch { used = 0; }
  if (used >= daily) {
    return { ok: false, body: { error: `Today's Ask Claude budget on this PC is used up (${used} of ${daily} tokens). It resets at midnight UTC; the admin can change FMC_ASK_DAILY_TOKENS.` } };
  }

  const messages: any[] = [];
  for (const h of (args.history ?? []).slice(-6)) {
    if (h?.text) messages.push({ role: h.role === "assistant" ? "assistant" : "user", content: String(h.text).slice(0, 4000) });
  }
  messages.push({ role: "user", content: question });

  const tools = ASK_TOOLS.map(({ name, description, input_schema }) => ({ name, description, input_schema }));
  const toolsUsed: string[] = [];
  let inTok = 0, outTok = 0;

  for (let turn = 0; turn <= ASK_DEFAULTS.maxToolTurns; turn++) {
    let res: Response;
    try {
      res = await deps.fetch(ASK_DEFAULTS.apiUrl, {
        method: "POST",
        headers: { "content-type": "application/json", "x-api-key": key, "anthropic-version": ASK_DEFAULTS.apiVersion },
        body: JSON.stringify({ model, max_tokens: ASK_DEFAULTS.maxTokens, system: ASK_SYSTEM, tools, messages }),
      });
    } catch (e) {
      writeLog(logFile, [deps.now().toISOString(), model, toolsUsed.join(" "), inTok, outTok, "network error"]);
      return { ok: false, body: { error: "Could not reach the Claude API: " + (e instanceof Error ? e.message : String(e)) } };
    }
    const data: any = await res.json().catch(() => ({}));
    if (!res.ok) {
      const msg = data?.error?.message ?? `HTTP ${res.status}`;
      writeLog(logFile, [deps.now().toISOString(), model, toolsUsed.join(" "), inTok, outTok, `api error ${res.status}`]);
      const hint = res.status === 401 ? " Check the API key." : res.status === 400 && /credit/i.test(msg) ? " Add API credits in the Claude Console." : "";
      return { ok: false, body: { error: `Claude API: ${msg}.${hint}` } };
    }
    inTok += data?.usage?.input_tokens ?? 0;
    outTok += data?.usage?.output_tokens ?? 0;
    const content: any[] = Array.isArray(data?.content) ? data.content : [];
    messages.push({ role: "assistant", content });

    const calls = content.filter((b) => b.type === "tool_use");
    if (data?.stop_reason !== "tool_use" || calls.length === 0 || turn === ASK_DEFAULTS.maxToolTurns) {
      const answer = content.filter((b) => b.type === "text").map((b) => b.text).join("\n").trim()
        || "(No answer: Claude used its tool turns without finishing. Try a narrower question.)";
      writeLog(logFile, [deps.now().toISOString(), model, toolsUsed.join(" "), inTok, outTok, "answered"]);
      return { ok: true, body: { answer, model, tools_used: toolsUsed, usage: { input_tokens: inTok, output_tokens: outTok }, budget_left: Math.max(0, daily - used - inTok - outTok) } };
    }

    const results: any[] = [];
    for (const call of calls) {
      const tool = ASK_TOOLS.find((t) => t.name === call.name);
      let text: string, isError = false;
      if (!tool || WRITE_COMMANDS.has(tool.command)) {
        text = `Unknown or not allowed tool: ${call.name}. Only read-only tools are available here.`; isError = true;
      } else {
        toolsUsed.push(tool.name);
        try {
          text = trimResult(JSON.stringify(await deps.revit(tool.command, tool.params(call.input ?? {}))));
        } catch (e) {
          text = "Revit did not answer: " + (e instanceof Error ? e.message : String(e)) + ". Is the Claude service on (Formcraft tab)?";
          isError = true;
        }
      }
      results.push({ type: "tool_result", tool_use_id: call.id, content: text, ...(isError ? { is_error: true } : {}) });
    }
    messages.push({ role: "user", content: results });
  }
  return { ok: false, body: { error: "Unexpected end of the ask loop." } };
}
