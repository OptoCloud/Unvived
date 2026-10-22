# AGENTS.md

Unvived: reverse engineering HTC's SRanipal runtime (VIVE Pro Eye + Facial
Tracker) to replace it with an open implementation. See `README.md` for the plan.

## Rules

- Never commit HTC or Tobii files (binaries, firmware, models, Unity assets)
  or raw decompiler output. They live in `vendor/`, `dumps/`, `ghidra_project*/`
  and `archive/`, all ignored.
- Findings go in `docs/architecture.md` (or a new `docs/*.md`), each citing
  the binary and export/address it came from.
- Replacement code is written from those notes, not pasted from decompiles.
- Python that reads vendor files runs with `py -I`.

## Tools on this machine

- Ghidra 12.0.4: `C:\Tools\ghidra_12.0.4_PUBLIC` (headless: `support\analyzeHeadless.bat`).
- `ilspycmd` for the .NET service and the Unity tools' `Assembly-CSharp.dll`.
- `pwsh scripts/inventory.ps1` refreshes `vendor/` and `docs/inventory.md`.
  The VRCFT module's client SDK DLLs go in `vendor/client/` by hand (from
  `%APPDATA%\VRCFaceTracking\CustomLibs\<module>\ModuleLibs\New`).
- `scripts/decompile.ps1 [-Project name] [-Files ...]` (needs `JAVA_HOME`,
  e.g. `C:\Program Files\Java\jdk-25.0.2`): Ghidra headless import +
  `BulkDecompile.java` -> `dumps/<binary>/all_functions.c` and
  `decompiled/<function>.c`. Call it with `&` from PowerShell when passing
  several files; `pwsh -File` passes the list as one string. Use a different
  `-Project` for each parallel run.
- `py -I scripts/disasm.py <pe> <va> [len]` (capstone + pefile; resolves
  import calls), `py -I scripts/strings.py <file>`, `py -I scripts/pe_info.py`.
- .NET: `ilspycmd -p -o dumps/<name> <file>`.
