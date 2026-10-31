# RGBcontroller: instructions for the AI builder

Read `SPEC.md` completely before doing anything. It is the source of truth. `reference/` holds verified hardware evidence (do not re-test what is already proven).

Working rules:

- Build in the stages defined in SPEC.md section 11. Stop at every gate, give the owner exact hardware test steps and pass criteria, and wait for an explicit OK.
- Spikes marked **[VERIFY]** come first; if one invalidates a **[DECISION]**, stop and ask.
- Everything (UI, code, comments, commits, docs) is in English. Talk to the owner in Polish.
- Never touch fan/pump control, never write raw SMBus/I2C, never run a second OpenRGB instance.
- Measure CPU only with `reference/Measure-TrayTools.ps1` (Polish Windows: `typeperf` does not work).
- Git: use the `anthropic-skills:publikowanie-projektow-github` skill to create the private GitHub repo and README, commit small and often (Conventional Commits), push only after the owner accepts the skill's report. See SPEC.md section 13.
- Do not run the Service or install drivers/services on the owner's machine without saying so first; dev install scripts must be reversible.

Build and test:

- `.\build.ps1 -Test` builds the solution and runs all unit tests (warnings are errors). It uses a .NET 10 SDK from PATH or the user-local one in `%LOCALAPPDATA%\Microsoft\dotnet`.
- CPU measurements: `tools\Measure-RGBcontroller.ps1` (copy of the reference tool with the new process names).
- OpenRGB SDK facts and the decision to use our own client (not OpenRGB.NET): `docs/spikes/R2-R3-openrgb-sdk.md`.
- The System.Text.Json source generator resets missing `init` properties to default, so config records use `set`.
- Dev install on the owner's PC: `install-dev.ps1` / `uninstall-dev.ps1` (elevated, reversible). Live SDK check: `RGBcontroller.Service.exe --probe [--rescan]`. Boot timing: `tools\Measure-TimeToFirstLight.ps1`.
- OpenRGB `SETCUSTOMMODE` does not touch the hardware; always send `UPDATEMODE` with the Direct mode (see `docs/spikes/R1-R5-R6-stage1.md`).
- The black screen at boot exists without RGBcontroller (A/B tested); do not chase it in this code.
