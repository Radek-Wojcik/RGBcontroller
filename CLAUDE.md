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
