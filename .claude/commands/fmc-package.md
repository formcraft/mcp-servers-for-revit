---
description: Package changed kit files as the next FMC_Update rN zip with its README
---
Package the kit changes since the last `FMC_Update_*_r<N>.zip` in `D:\Fomcraft\FMC_BIM_27\FMC_MCP`.

1. Compare the kit with the last package and its backup folder, and list the changed kit files.
2. Build `FMC_Update_<today>_r<N+1>.zip` in the format of "Kit update packages" in `docs/fmc/EXECUTION.md`.
3. Run `py build_all.py` and `py test_kit.py`, and put the counts into the README.
4. Tell me the zip path and the claude.ai Project paths that need the same files.
