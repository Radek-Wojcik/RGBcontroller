# Contributing

RGBcontroller is a personal project built around one PC, but fixes and support for other hardware are welcome.

## Build and test

Requirements are listed in the [README](README.md#getting-started). Then:

```powershell
.\build.ps1 -Test
```

This builds the React front end and the .NET solution (warnings are errors) and runs all unit tests. The front end runs on stand-in data with `npm run dev` in `ui/`, no Service or hardware needed.

## Ground rules

- Only the Service touches hardware. Never write fan or pump control registers and never access SMBus/I2C directly; OpenRGB is the only path.
- Measure CPU only with `tools\Measure-RGBcontroller.ps1`; Task Manager and `typeperf` give different numbers (see [docs/spikes/stage2-effects.md](docs/spikes/stage2-effects.md)).
- Commits are small and use [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `build:`, `chore:`). Everything (UI, code, comments, docs) is in English.
- Open pull requests against `main`. Add or update tests for behavior changes; run `.\build.ps1 -Test` first.

## Reporting problems

Open an issue with your Windows version, the OpenRGB device list (`RGBcontroller.Service.exe --probe`) and the relevant lines of the Service log (`%ProgramData%\RGBcontroller\logs`).
