# Ask Claude in the pane (test build)

Type `ask <question>` (or `? <question>`) in the Formcraft Browser command box. Claude answers in the pane and can
**read** the open model: documents, the active view, the selection, model statistics and material quantities. It
cannot change anything. Changes still go through the pane's buttons (with their dry run) or Claude Desktop.

## Set up (once per PC)

1. **API key.** In the Claude Console (platform.claude.com) create an API key for the firm's organization.
   Save it as the first line of `%APPDATA%\Formcraft\anthropic_api_key.txt` (or set `ANTHROPIC_API_KEY`).
   Never put the key in the repo, the kit or a shared folder.
2. **Credits.** The key is billed per token on its Console organization, not the Claude app plan:
   - Max and Team plans include monthly API credits once a Console organization is linked to the plan
     (Claude settings › Billing). They don't roll over.
   - Otherwise buy prepaid credits in the Console. Usage credits in the Claude app do **not** cover API keys.
3. **Build.** Pull this branch, `cd server && npm ci && npm run build`, rebuild the add-in, restart Revit.
4. **Try it.** Start the Claude service (Formcraft tab), open a model, type `ask how many sheets and views are in this model?`

## Limits in this build

| Setting | Default | Change with |
|---|---|---|
| Model | `claude-sonnet-5-5` | `FMC_ASK_MODEL` |
| Tokens per day on this PC | 200,000 | `FMC_ASK_DAILY_TOKENS` |
| Reply length | 1,200 tokens | code (`ASK_DEFAULTS`) |
| Tool calls per question | 6 | code |
| Each tool result | 12,000 characters | code |

`ask reset` clears the follow-up memory (the pane keeps the last 6 questions and answers for context).

## What is logged

`<reports_dir>\FMC_Ask_Log.csv`: time, model, tools read, input and output tokens, result. The question and answer
text are never logged, and the log is never used to rank people (Manual 14.5).

## Safety

- The tools are listed in `server/src/fmc/ask.ts` (`ASK_TOOLS`). `ask.test.ts` fails if any of them is a
  model-changing command, and a tool name Claude invents is refused before it reaches Revit.
- The system prompt carries the Formcraft rules: read-only, never decide for a professional, never claim a change.
- Next step (Phase 2): let Claude *propose* a change that the pane turns into the normal dry run → approve → run →
  verify → log path. Not in this build.
