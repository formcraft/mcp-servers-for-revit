import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { ASK_TOOLS, runAsk, tokensToday, trimResult, AskDeps } from "./ask.js";
import { WRITE_COMMANDS } from "./policy.js";

// No test reaches the Claude API or Revit: fetch and the Revit call are stand-ins.
function deps(replies: any[], over: Partial<AskDeps> = {}): AskDeps & { calls: any[]; revitCalls: string[] } {
  const calls: any[] = [], revitCalls: string[] = [];
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "fmc-ask-"));
  return {
    calls, revitCalls,
    fetch: (async (_u: any, init: any) => {
      calls.push(JSON.parse(init.body));
      const r = replies.shift();
      return { ok: r.status ? r.status < 400 : true, status: r.status ?? 200, json: async () => r.body ?? r } as any;
    }) as any,
    revit: async (c) => { revitCalls.push(c); return { ok: true, command: c }; },
    apiKey: () => "sk-test",
    now: () => new Date("2026-10-13T10:00:00Z"),
    env: {},
    logFile: () => path.join(dir, "FMC_Ask_Log.csv"),
    ...over,
  };
}

test("every Ask tool is read-only", () => {
  for (const t of ASK_TOOLS) assert.ok(!WRITE_COMMANDS.has(t.command), `${t.name} sends ${t.command}, a model-changing command`);
});

test("no API key: refused with the setup step, no API call", async () => {
  const d = deps([], { apiKey: () => null });
  const r = await runAsk({ question: "how many sheets?" }, d);
  assert.equal(r.ok, false);
  assert.match(String(r.body.error), /anthropic_api_key\.txt/);
  assert.equal(d.calls.length, 0);
});

test("tool call then answer: Revit is read, tokens are summed and logged without the question", async () => {
  const d = deps([
    { content: [{ type: "tool_use", id: "t1", name: "model_statistics", input: {} }], stop_reason: "tool_use", usage: { input_tokens: 100, output_tokens: 20 } },
    { content: [{ type: "text", text: "The model has 21 sheets." }], stop_reason: "end_turn", usage: { input_tokens: 150, output_tokens: 10 } },
  ]);
  const r = await runAsk({ question: "secret question text" }, d);
  assert.equal(r.ok, true);
  assert.equal(r.body.answer, "The model has 21 sheets.");
  assert.deepEqual(d.revitCalls, ["analyze_model_statistics"]);
  assert.deepEqual(r.body.usage, { input_tokens: 250, output_tokens: 30 });
  assert.equal(d.calls[1].messages.at(-1).content[0].type, "tool_result");
  const log = fs.readFileSync(d.logFile()!, "utf8");
  assert.match(log, /model_statistics/);
  assert.doesNotMatch(log, /secret question/);
});

test("an unknown tool name is refused, never sent to Revit", async () => {
  const d = deps([
    { content: [{ type: "tool_use", id: "t1", name: "delete_element", input: {} }], stop_reason: "tool_use", usage: {} },
    { content: [{ type: "text", text: "I can't change the model here." }], stop_reason: "end_turn", usage: {} },
  ]);
  const r = await runAsk({ question: "delete it" }, d);
  assert.equal(r.ok, true);
  assert.equal(d.revitCalls.length, 0);
  assert.equal(d.calls[1].messages.at(-1).content[0].is_error, true);
});

test("daily budget: refused once today's tokens reach the cap", async () => {
  const d = deps([], { env: { FMC_ASK_DAILY_TOKENS: "100" } });
  fs.writeFileSync(d.logFile()!, "timestamp,model,tools,input_tokens,output_tokens,result\n2026-10-13T08:00:00Z,m,,80,30,answered\n");
  const r = await runAsk({ question: "hi" }, d);
  assert.equal(r.ok, false);
  assert.match(String(r.body.error), /budget/);
  assert.equal(d.calls.length, 0);
});

test("helpers", () => {
  assert.equal(tokensToday("h\n\"2026-10-13T01:00:00Z\",m,,5,6,a\n2026-10-12T01:00:00Z,m,,9,9,a\n", new Date("2026-10-13T12:00:00Z")), 11);
  assert.match(trimResult("x".repeat(20), 10), /cut at 10/);
});
