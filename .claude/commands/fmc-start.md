---
description: Start a Formcraft MCP session - pull, check builds, show status and the next step
---
Start a Formcraft MCP work session. Change nothing in this command.

1. `git fetch origin`. Pull both `fmc/file-tools-and-guard` and `fmc/formcraft-browser`, and stay on
   `fmc/formcraft-browser`. If either branch has local changes or would not fast-forward, stop and tell me.
2. Check the latest GitHub Actions runs for both branches (`gh run list --limit 4`).
3. Read `CLAUDE.md`, `docs/fmc/ROADMAP.md` and `docs/fmc/EXECUTION.md`.
4. Tell me in a few lines:
   - the branch heads and whether the builds are green;
   - the installed build, if you can see it;
   - the Status table's next step;
   - anything blocking it.
