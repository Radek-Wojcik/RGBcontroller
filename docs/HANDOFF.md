# RGBcontroller: project state and handoff

Last updated: 2026-10-31, after the stage 1 gate. **Read this file right after `SPEC.md` and `CLAUDE.md` when you
start a new session.** It records everything that was built, decided and learned in stages 0-1, the exact state of
the owner's PC, and what to do next. `SPEC.md` stays the source of truth for requirements. This file is the source
of truth for **what exists now**.

---

## 1. Status at a glance

| Stage | State | Tag | Evidence |
|---|---|---|---|
| 0. Foundation | **Passed** (2026-10-30) | `v0.0.1-stage0` | `docs/spikes/R2-R3-openrgb-sdk.md` |
| 1. Service + OpenRGB supervisor + Adalight | **Passed** (2026-10-31) | `v0.1.0-stage1` | `docs/spikes/R1-R5-R6-stage1.md` |
| 2. Effect engine | **Not started.** The owner said explicitly: do not start until told. | | |
| 3-6 | Not started | | |

What works today on the owner's PC:
- The Windows Service `RGBcontroller` (LocalSystem, automatic start) lights the Adalight strip 13.2 s after boot.
- It lights all 5 OpenRGB devices 17.2 s after boot, before the logon screen, with one static color (`#00E5FF`).
- There is exactly one OpenRGB instance, and CPU use is 0.000%.

Repository: <https://github.com/Radek-Wojcik/RGBcontroller> (**private**, no LICENSE file: "All rights reserved").
- `main` is up to date and contains the merged `stage-1-service` branch and PR #1 (the README refresh).
- The branch `stage-1-service` (local and on origin) is merged and can be deleted.

---

## 2. Owner preferences and working rules (binding)

These come from the owner during stages 0-1 and add to `CLAUDE.md`:

- **Talk to the owner in Polish.** Code, comments, commits and docs are in English.
- **Commit author is the owner only.** Use `Radek Wójcik <124835720+Radek-Wojcik@users.noreply.github.com>`; it is already set as the repo-local git identity.
  - **Never add `Co-Authored-By: Claude` trailers or "Generated with Claude Code" lines**, in commits or PRs.
  - The whole history was rewritten once to enforce this.
- **Stage gates are hard stops.**
  - Give the owner short, exact test steps and wait for an explicit OK.
  - The owner prefers that you run checks yourself where possible (logs, measurements, `--probe`) and asks only for what needs eyes on the hardware.
  - The owner does not want to paste command output. Read logs and run `tools\Measure-TimeToFirstLight.ps1` yourself after their reboot.
- **Say what you will do before changing the system** (service install, task disable, UAC prompts), then do it.
  - Elevated actions are run via `Start-Process powershell -Verb RunAs` with a transcript into the scratchpad. The owner clicks UAC.
- **GitHub workflow:**
  - One branch per stage (`stage-N-...`), merged to `main` with `--no-ff` after the gate.
  - Each passed gate gets an annotated tag (`v0.N.0-stageN`), and the result is pushed.
  - Refresh the README through the `anthropic-skills:publikowanie-projektow-github` skill: show the README, wait for "zatwierdzam", then push a `docs/readme-refresh` branch, open a PR and merge it.
- **Do not add features that are not in `SPEC.md` without asking.**
- **Never touch fan or pump control.**
  - The owner mentioned fans "spinning up hard then going quiet/dark" at power-on.
  - It is still open whether they meant RPM (BIOS behaviour before FanControl starts) or the LEDs (ASUS power-on effect before the Service lights them). Either way it is not caused by the app, which never controls fans.

---

## 3. The owner's PC: current state (changed by us)

| Item | State | How to undo |
|---|---|---|
| Service `RGBcontroller` | Installed in `C:\Program Files\RGBcontroller` (self-contained ReadyToRun build plus `openrgb\` pinned copy). Automatic start, recovery: restart after 1 s / 5 s / 30 s. | `uninstall-dev.ps1` (elevated) |
| Data folder | `C:\ProgramData\RGBcontroller\` holds `config\config.json`, `logs\service-YYYYMMDD.log`, `openrgb\` (private OpenRGB config, `OpenRGB.json`, `sizes.ors`) and `dev-install\state.json`. ACL: SYSTEM and Administrators full control, Users read and execute. | `uninstall-dev.ps1 -RemoveData` |
| Old OpenRGB autostart | Task Scheduler `\OpenRGB_Admin` is **disabled, not deleted**. It used to run `"C:\Program Files\OpenRGB\OpenRGB.exe" --startminimized --noautoconnect...` at system startup as Trolli04 with highest privileges and loaded the Effects plugin. `state.json` records it as previously `Running`. | `uninstall-dev.ps1` re-enables and starts it |
| Owner's own OpenRGB | `C:\Program Files\OpenRGB` (1.0rc2 plus `plugins\OpenRGBEffectsPlugin.dll`) and `%APPDATA%\OpenRGB` (RAINdbow profile, `sizes.ors`) are **untouched**. | n/a |
| SignalRGB | Was already uninstalled (Squirrel `.dead` marker from 2026-05-06). Leftover folders (`%LOCALAPPDATA%\VortxEngine`, `WhirlwindFX`, `%APPDATA%\WhirlwindFX`, two `*.firebaseio.com` caches, about 500 MB) went to the **Recycle Bin**. Its HKCU Run entry `SignalRgb` was removed with the owner's permission. | Restore from the Recycle Bin |
| Razer Synapse 3, Logitech G HUB, AmbiBox | Installed; their autostart is disabled (StartupApproved). **The owner wants them kept.** The `Razer Chroma SDK Server` service runs (it only talks to the Razer keyboard). AmbiBox must never run (it would take COM3). | n/a |
| .NET 10 SDK 10.0.403 | User-local in `%LOCALAPPDATA%\Microsoft\dotnet`. The machine-wide install has runtimes only. | Delete the folder |
| GitHub CLI 2.103.3 | `C:\Program Files\GitHub CLI\gh.exe` (winget). Logged in as `Radek-Wojcik` (keyring); git uses it for HTTPS. **It is not on PATH in an old shell:** prepend `C:\Program Files\GitHub CLI`. | `winget uninstall GitHub.cli` |
| Node.js | **Not installed** (needed in stage 5 for the React front end). | n/a |
| Fast Startup | Off (`HiberbootEnabled = 0`), so "Shut down" is a cold boot. | n/a |
| PawnIO driver | Present (`PawnIO`, manual start, running). | n/a |

Hardware facts confirmed live (in addition to SPEC section 2):
- The keyboard has **132 LEDs in a 6x22 matrix**, not the 104-key 6x21 layout that SPEC assumed.
- The motherboard has 3 onboard LEDs plus Aura Addressable 1-3, which are resizable from 0 to 120.
- The G502 reports 2 LEDs, and its mode index 0 is "Off", so Direct is index 1.
- **The G502 now lights too** (previously off by the owner's choice). Stage 2 per-device effects must allow "off".
- The LED BOX keeps its last frame while USB is unplugged, because it has its own 5 V supply.
- The CH340 comes back as COM3 after a replug.

---

## 4. What was built (code map)

Solution `RGBcontroller.sln`. Central package versions are in `Directory.Packages.props`; `Directory.Build.props`
turns warnings into errors and enables nullable. `global.json` pins SDK 10.0.100 with `latestFeature` and selects
the Microsoft.Testing.Platform runner.

### `src/RGBcontroller.Core` (net10.0)
- `AppPaths` has the data root (`%ProgramData%\RGBcontroller`), its config, logs and openrgb subfolders, and the user log folder (`%LOCALAPPDATA%\RGBcontroller\logs` for Agent and UI).
- `Config/`:
  - `AppConfig`: records with `set` accessors (see the gotchas) and sections `strip`, `openRgb`, `lighting` and `logging`.
  - `ConfigStore`: load, save, migrate, quarantine of corrupt files, atomic write.
  - `ConfigMigrator`: `IConfigMigration` chain. Schema v1, no migrations yet.
  - `ConfigValidator`: ranges, COM name, loopback-only OpenRGB host, `#RRGGBB` color.
  - `ConfigJsonContext`: System.Text.Json source generator, camelCase, string enums, comments allowed.
  - `AtomicFile`.
- `Logging/AppLogging`: Serilog rolling files `<component>-YYYYMMDD.log`, kept 14 days and 10 MB, UTF-8 **with BOM** (for PowerShell 5.1).
- `RgbColor`: `TryParseHex` and `Scale(percent)`.
- `Strip/AdalightFrame` builds the frame into a pre-allocated buffer without allocating:
  - Header `Ada` + hi/lo + checksum.
  - Processing order: brightness, then the current limiter, then color order. The limiter uses 6.5 mA per fully lit channel.
  - `MaxFramesPerSecond(baud, leds)` gives the serial link limit.
- `Strip/StripWriter` uses `IStripPortProvider` and `IStripTransport`:
  - Sequence: discover the port, open it, wait 0.8 s, send 3 startup frames.
  - Writes on change plus a 1 s keep-alive, at most min(config FPS, 95% of the link).
  - Reconnect back-off 0.5 / 1 / 2 / 5 s; black frame and flush on stop.
- `OpenRgb/`: our own SDK client (spike R2 decided against OpenRGB.NET).
  - `OpenRgbProtocol`: packet IDs; supported protocol versions are 4-5.
  - `ControllerData` and `ControllerDataReader`: exact-length parser for protocol 4 and 5.
  - `OpenRgbSdkClient` protocol and connection:
    - `ConnectAsync` sends the client name and negotiates the protocol.
    - One request is in flight at a time, matched by packet id and device index.
    - Request timeout is 5 s; the send timeout of 2 s ends the connection.
    - Events: `DeviceListUpdated` and `Disconnected`.
  - `OpenRgbSdkClient` device calls:
    - `UpdateLeds` does not allocate.
    - `UpdateMode` (1101) **pushes the mode to the hardware**. `SetCustomMode` (1100) only selects it inside OpenRGB.
    - `ResizeZone` (1000) and `RequestRescan` (140).

### `src/RGBcontroller.Ipc` (net10.0)
Contracts only; no server exists yet:
- `IpcProtocol`: version 1.0 and fixed pipe names `RGBcontroller.Control` and `RGBcontroller.Frames`.
- `ProtocolVersion.CheckAgainstServer`.
- `IServiceControl` with `HelloAsync` and `GetStatusAsync`, plus the DTOs.
- `IpcRpc`: StreamJsonRpc with SystemTextJsonFormatter and string enums.
- `FrameCodec`: 16-byte `RGBF` header for strip and preview frames.

### `src/RGBcontroller.Service` (net10.0-windows, Exe)
- `Program` modes:
  - (no arguments) runs as the Windows Service;
  - `--console` runs the same host in a console (elevated, Service stopped);
  - `--probe [--rescan]` is read-only SDK diagnostics;
  - `--data-dir <folder>` overrides the data folder.
- `RgbControllerService : ServiceBase` with `CanShutdown`. It stops with an 8 s budget, or 4 s on shutdown.
- `LightingHost` is the composition root:
  - It holds the global mutex `Global\RGBcontroller.LightingHost`.
  - **The strip starts first, then the OpenRGB supervisor.**
  - On stop it sends black everywhere.
- `StartupClock` measures R5 timings.
- `Hardware/UsbSerialPortProvider`:
  - Finds the strip's port through the registry `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_yyyy\*\Device Parameters\PortName`, keeping only ports present right now, or uses the configured override.
  - Its `SerialStripTransport` uses 8N1.
- `Hardware/JobObject`: kill-on-close job object (LibraryImport).
- `OpenRgb/OpenRgbLauncher` starts the pinned OpenRGB inside the job:
  - Arguments: `--server --server-host 127.0.0.1 --server-port 6742 --config %ProgramData%\RGBcontroller\openrgb --noautoconnect`.
  - It seeds `OpenRGB.json` with `log_file: true`, although OpenRGB has written no log so far; see the open items.
  - `KillStrays` terminates every OpenRGB.exe that is not in the job.
- `OpenRgb/OpenRgbSupervisor` main loop:
  - Kill strays, then the optional uptime wait (`minimumUptimeSeconds`, default 0), then launch.
  - Connect with retries until `startupTimeoutSeconds`, then light all devices.
- `OpenRgb/OpenRgbSupervisor` monitoring and restart:
  - On a DEVICE_LIST_UPDATED burst it waits for quiet (`deviceSettleMilliseconds`) and re-applies.
  - Health check every 10 s.
  - Restart back-off 1 / 2 / 5 / 15 / 30 s; the failure count resets after 60 s of stable uptime.
  - On stop: black, then one round trip and a 300 ms grace period, then kill.
- `OpenRgb/DeviceLighting` handles every enumeration:
  - Re-read all controllers and resize the configured zones (they are lost on every rescan).
  - Send `UPDATEMODE` with the Direct mode, chosen like OpenRGB's SetCustomMode: Direct, then Custom, then Static with per-LED or mode-specific colors.
  - Send `UPDATELEDS` with the static color.
  - Log "First light" once per device per Service start.
- `Probe`: lists devices as our parser reads them and, with `--rescan`, times a rescan.

### `src/RGBcontroller.Agent`, `src/RGBcontroller.UI`
Skeletons only: the Agent logs and exits; the UI is an empty WPF window. They are built in stages 4 and 5.

### `tests/RGBcontroller.Tests` (xUnit v3, 95 tests, all green)
- `FakeOpenRgbServer` is a loopback TCP stand-in for OpenRGB 1.0rc2:
  - It enforces the size checks for UPDATELEDS and UPDATEMODE, records LED colors, mode and resize requests, and pushes DEVICE_LIST_UPDATED.
  - It has switches for an unresponsive server and for dropping clients.
- `ControllerDataWriter` is an independent serializer with three fixtures: a keyboard (126 LEDs in the fixture, not the real 132), a motherboard with a resizable ARGB zone, and a mouse whose Direct mode is index 1.
- Coverage areas:
  - config;
  - IPC;
  - Adalight frames and the strip writer, with fake serial ports;
  - the SDK client;
  - the supervisor: crash restart, hung SDK, startup timeout, rescan re-apply, zone resize, black on stop, uptime wait.

### Scripts
| Script | Purpose |
|---|---|
| `build.ps1 [-Test] [-Configuration Release]` | Build (warnings as errors) and run tests through `dotnet test --solution` (MTP). Finds the user-local SDK. |
| `tools/Get-PinnedOpenRgb.ps1` | Copies `C:\Program Files\OpenRGB` to `third_party/openrgb` (git-ignored) **without `plugins`**, after checking the SHA-256 in `third_party/openrgb-pin.json`. |
| `install-dev.ps1` | Elevated. Steps: publish (self-contained, ReadyToRun, `--disable-build-servers`), take ownership of the data folder and set its ACL (folder only, children reset to inherit), record the `OpenRGB_Admin` state and disable it, stop old processes, copy to Program Files with robocopy `/MIR`, register the service with recovery actions, start it, show the log tail. Idempotent; re-run it to update the installed build. |
| `uninstall-dev.ps1 [-RemoveData]` | Elevated. Stop the service (black), delete it, remove Program Files, restore and start `OpenRGB_Admin`. |
| `tools/Measure-RGBcontroller.ps1` | The CPU tool required by CLAUDE.md: a copy of `reference/Measure-TrayTools.ps1` with process names `RGBcontroller.Service`, `RGBcontroller.Agent`, `RGBcontroller.UI` and `OpenRGB`. `RAZEM` is the total. |
| `tools/Measure-TimeToFirstLight.ps1 [-Last N]` | R5: pairs each Service start with its boot (System log Kernel-General 12 and Kernel-Boot 27) and lists the "First light" times. Works without admin. |

---

## 5. Decisions and lessons (do not re-learn these)

1. **Own SDK client, not OpenRGB.NET** (R2).
   - OpenRGB.NET 3.1.1 is capped at protocol 4, has no rescan, is not thread-safe and hangs on a dead server.
   - Live check: OpenRGB 1.0rc2 negotiates protocol 5, and all 5 devices parse exactly.
2. **Rescan via packet 140 inside the single instance** (R3).
   - A rescan sends about 11 DEVICE_LIST_UPDATED notices and settles about 1.1 s after the request.
   - **Zone sizes reset to 0 on rescan**, so we re-apply them every time.
   - A debounce during a rescan can apply to "0 devices" first (harmless; a later notice re-applies). Could be refined in stage 3.
3. **`SETCUSTOMMODE` does not change the hardware mode; always send `UPDATEMODE`.**
   - This bug left the RAM in its rainbow and the GPU dark after a cold boot.
   - Source: `RGBController::SetCustomMode` only sets `active_mode`.
4. **OpenRGB in Session 0 as LocalSystem controls all devices, NvAPI GPU included** (R1, confirmed visually). No second OpenRGB.
5. **The OpenRGB server binds to 0.0.0.0 by default.** Always pass `--server-host 127.0.0.1`.
   - 1.0rc2 has no CLI switch to disable plugins; isolation comes from a plugin-free pinned copy plus `--config`.
   - Verified: no `Plugins` section in the private config, and the server listens only on `127.0.0.1:6742`.
6. **Black screen at boot is NOT caused by RGBcontroller.**
   - A/B test: the Service set to Manual, still a black screen. No driver or Windows update in that period.
   - The owner says it is new, but its cause lies outside this project.
   - A 30 s OpenRGB start delay added during the investigation was reverted to 0 (`openRgb.minimumUptimeSeconds`).
7. **R5 timings:** Service +12.2 s, strip +13.2 s, all devices +17.2 s after boot. Boot detection takes about 4-5 s; detection on a running system takes about 1.6 s.
8. **CPU with a static color: 0.000% total** (Service plus OpenRGB, 20 s sample). The old setup used 2.58%.
9. **R6:** USB replug is detected and reconnected (port back after about 9 s, reconnected about 1 s later). The keep-alive restores the color after the LED BOX is power-cycled.
10. **Tooling gotchas:**
    - The System.Text.Json source generator resets **missing `init` properties to default**, so config records use `set`.
    - Records with a list need a custom `Equals` (see `OpenRgbConfig`).
    - The .NET 10 SDK refuses VSTest for xUnit v3 / MTP. `global.json` has `"test": {"runner": "Microsoft.Testing.Platform"}`, and `dotnet test --solution`.
    - `icacls /T` with `(OI)(CI)` grants on existing files **removes all access**: set the folder ACL, then `icacls folder\* /reset /T`.
    - `Start-Process -Verb RunAs -Wait` hangs on an elevated MSBuild node; use `--disable-build-servers` or `WaitForExit` on the process.
    - Windows PowerShell 5.1: `ConvertFrom-Json` fails on the owner's `OpenRGB.json` (duplicate keys); UTF-8 logs need a BOM; `typeperf` does not work (Polish locale).
    - The tool shell is not elevated. OpenRGB runs as SYSTEM, so killing it needs admin.
    - `C:\ProgramData\RGBcontroller` must never be created from a user prompt; `--probe` is read-only on purpose.
11. **Logon:** Windows authenticates the user about 11 s after boot, and profile load ends at +24-28 s on every boot (the same before and after the install).

---

## 6. Open items and known gaps (carry into the next stages)

| # | Item | Where it belongs |
|---|---|---|
| O1 | OpenRGB writes no log file despite the seeded `"LogManager": {"log_file": true}`; logs would help diagnostics. Check the exact setting name or the `--loglevel` / `-v` flags. | Stage 3 or 6 diagnostics |
| O2 | No IPC server yet; the contracts are tested in-memory only. A named-pipe server with the ACL from SPEC section 3 is needed. | Stage 4 (Agent), possibly stage 2 for the profile switch |
| O3 | Power, session and display events (suspend, resume, lock, display off) are not handled; only stop and shutdown send black. | Stage 3 (spike R4) |
| O4 | Device-list debounce may apply to 0 devices mid-rescan; a better settle (expected device set plus quiet period) would help. | Stage 3 |
| O5 | The G502 is lit, but the owner had kept it off. Per-device on/off and effects are needed. | Stage 2 |
| O6 | Owner's remarks, still open: fans "spin up hard / go dark" at power-on (not clarified whether RPM or LEDs); the logon-screen shutdown button needed 3 clicks once (not reproduced, nothing in the logs). | Ask if they come up again |
| O7 | `third_party/openrgb` needs `tools/Get-PinnedOpenRgb.ps1` on a fresh checkout; a release build should not depend on `C:\Program Files\OpenRGB`. | Stage 6 installer |
| O8 | The ProgramData `config.json` was written before some keys existed (`minimumUptimeSeconds`); missing keys take the code defaults. Note this when keys are renamed (migration). | Ongoing |
| O9 | Effects, profiles, Logon and Lock profiles, ambilight, Agent, UI and the installer: everything from SPEC stages 2-6. | Stages 2-6 |

---

## 7. Next: stage 2 (effect engine). Do not start without the owner's go

SPEC section 11, stage 2, covers:
- the device model with positions;
- wizard-less config;
- all effects: Static, Breathing, Wave, Radial, Mirror, Color cycle, Aurora;
- palettes, FPS caps, change-only writes, per-group brightness and the profile model.

The default **Rainbow** profile (SPEC 5.3) assigns:
- radial rainbow to the pump and fans;
- a right-to-left wave to the keyboard;
- color cycle to the mouse and GPU;
- a mirror from the middle of each RAM stick;
- strip mode to Screen (the ambilight comes in stage 4, so use a fallback until then).

**Gate:**
- CPU with the full Rainbow profile at 30 FPS (RAM 10-15, GPU 10), measured with `tools\Measure-RGBcontroller.ps1` over 60 s idle.
- Clearly below 2.6%, target below 1.0%; a static profile about 0.0x%.
- A visual check of every device.

Starting points in the existing code:
- Replace `DeviceLighting`'s static color with a render loop that writes through `OpenRgbSdkClient.UpdateLeds`. It already does not allocate; keep `UPDATEMODE` Direct per device.
- `StripWriter.SetColors` is ready for per-LED frames (rate-limited and change-only).
- Positions are unknown for the ARGB headers (mapping wizard in stage 5). Use configurable defaults: ring, 20 LEDs, CW. The keyboard matrix 6x22 comes from the controller data.
- Keep every device's FPS cap. RAM and GPU are I2C/SMBus, so use low FPS and change-only writes (SPEC 9.3).
- Re-measure CPU the same way as in stage 1.

Branch: `stage-2-effects`. Tag after the gate: `v0.2.0-stage2`.

---

## 8. How to resume in a new session

1. Open Claude Code in `C:\Users\Trolli04\Desktop\STUDYY\Projekty moje\RGBcontroller`.
2. Paste the prompt below.

```text
You are continuing RGBcontroller (Windows RGB + Adalight controller) for the owner Radek. Talk to him in Polish;
code, comments, commits and docs are in English.

Read, in this order: SPEC.md, CLAUDE.md, docs/HANDOFF.md, then docs/spikes/*.md. HANDOFF.md describes exactly what
exists, the owner's PC state, binding preferences (commits authored only by the owner with the noreply address, no
Co-Authored-By/Claude attribution anywhere), decisions and open items. Stages 0 and 1 have passed (tags
v0.0.1-stage0, v0.1.0-stage1); the Service is installed and running on this PC.

First: run `.\build.ps1 -Test` (expect 95 green tests), check `git status` and that main equals origin/main, and
check `Get-Service RGBcontroller`. Then report in Polish in a few lines and wait. Do NOT start stage 2 until the
owner says so. When he does: create branch stage-2-effects, plan stage 2 from SPEC section 11 and HANDOFF.md
section 7, and stop at the gate with exact test steps.
```

Useful commands:

```powershell
.\build.ps1 -Test
& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --probe
.\tools\Measure-TimeToFirstLight.ps1 -Last 2
.\tools\Measure-RGBcontroller.ps1 -Label check -DurationSeconds 60 -OutDir $env:TEMP
Get-Content (Get-ChildItem C:\ProgramData\RGBcontroller\logs\service-*.log | Sort-Object LastWriteTime)[-1].FullName -Tail 30
```
