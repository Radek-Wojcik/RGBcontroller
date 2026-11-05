# RGBcontroller: instructions for the AI builder

Read `SPEC.md` completely before doing anything. It is the source of truth. `reference/` (local only, not in the public repo) holds verified hardware evidence; the facts are summarized in SPEC.md section 2 and docs/spikes (do not re-test what is already proven).

Then read `docs/HANDOFF.md`: it is the current project state (stages done, what is installed on the owner's PC, decisions, lessons, open items, next steps). Update it at the end of every stage or whenever the owner asks for a handoff.

Working rules:

- Build in the stages defined in SPEC.md section 11. Stop at every gate, give the owner exact hardware test steps and pass criteria, and wait for an explicit OK.
- Spikes marked **[VERIFY]** come first; if one invalidates a **[DECISION]**, stop and ask.
- Everything (UI, code, comments, commits, docs) is in English. Talk to the owner in Polish.
- Never touch fan/pump control, never write raw SMBus/I2C, never run a second OpenRGB instance.
- Measure CPU only with `tools/Measure-RGBcontroller.ps1` (Polish Windows: `typeperf` does not work).
- Git: use the `anthropic-skills:publikowanie-projektow-github` skill to create the private GitHub repo and README, commit small and often (Conventional Commits), push only after the owner accepts the skill's report. See SPEC.md section 13.
- Do not run the Service or install drivers/services on the owner's machine without saying so first; dev install scripts must be reversible.
- Commits and PRs are authored by the owner only (repo-local identity `Radek Wójcik <124835720+Radek-Wojcik@users.noreply.github.com>`); never add `Co-Authored-By: Claude` or "Generated with Claude Code" lines.

Build and test:

- `.\build.ps1 -Test` builds the front end and the solution and runs all unit tests (warnings are errors). It uses a .NET 10 SDK from PATH or the user-local one in `%LOCALAPPDATA%\Microsoft\dotnet`, and Node.js from PATH or `%USERPROFILE%\.tools\nodejs`.
- Front end: `ui/` (React + TypeScript + Vite, exact versions at least two weeks old). `npm run dev` in `ui/` runs on stand-in data for design work; the app serves `ui/dist` inside WebView2.
- CPU measurements: `tools\Measure-RGBcontroller.ps1` (copy of the reference tool with the new process names).
- OpenRGB SDK facts and the decision to use our own client (not OpenRGB.NET): `docs/spikes/R2-R3-openrgb-sdk.md`.
- The System.Text.Json source generator resets missing `init` properties to default, so config records use `set`.
- Dev install on the owner's PC: `install-dev.ps1` / `uninstall-dev.ps1` (elevated, reversible). Live SDK check: `RGBcontroller.Service.exe --probe [--rescan]`. Boot timing: `tools\Measure-TimeToFirstLight.ps1`.
- OpenRGB `SETCUSTOMMODE` does not touch the hardware; always send `UPDATEMODE` with the Direct mode (see `docs/spikes/R1-R5-R6-stage1.md`).
- The black screen at boot exists without RGBcontroller (A/B tested); do not chase it in this code.
- Control the running Service without elevation: `RGBcontroller.Service.exe --status | --profile <name> | --group <name> [--fps N] [--speed X] [--scale X] [--brightness N]`. In PowerShell write decimals with a dot (`0,08` becomes an array).
- RAM (HyperX over SMBus) is the CPU budget: about 0.16% total CPU per RAM FPS (`docs/spikes/stage2-effects.md`).
