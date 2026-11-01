# RGBcontroller: project state and handoff

Last updated: 2026-11-01 late evening, stage 4 in progress (code done and installed, gate tests not run yet). **Read this file right after
`SPEC.md` and `CLAUDE.md` when you start a new session.** It records everything that was built, decided and learned
in stages 0-4, the exact state of
the owner's PC, and what to do next. `SPEC.md` stays the source of truth for requirements. This file is the source
of truth for **what exists now**.

---

## 1. Status at a glance

| Stage | State | Tag | Evidence |
|---|---|---|---|
| 0. Foundation | **Passed** (2026-10-30) | `v0.0.1-stage0` | `docs/spikes/R2-R3-openrgb-sdk.md` |
| 1. Service + OpenRGB supervisor + Adalight | **Passed** (2026-10-31) | `v0.1.0-stage1` | `docs/spikes/R1-R5-R6-stage1.md` |
| 2. Effect engine (+ sleep/resume pulled forward from stage 3) | **Passed** (2026-11-01) | `v0.2.0-stage2` | `docs/spikes/stage2-effects.md` |
| 3. Power, session, display | **Passed** (2026-11-01; owner waived the remaining 7 sleep and 3 hibernate cycles) | `v0.3.0-stage3` | `docs/spikes/stage3-power.md` |
| 4. Agent, tray, hotkeys, ambilight | **In progress**: built and installed on branch `stage-4-agent` (pushed, not merged); gate tests open | | `docs/spikes/stage4-agent.md` |
| 5-6 | Not started | | |

What works today on the owner's PC:
- The installed build is branch `stage-4-agent` at commit 26f4bbe (`--status` reports 0.3.0, IPC 1.3; the version is
  bumped to 0.4.0 when the gate passes).
- **Stage 4:** the Agent runs in the tray (started at logon from HKCU Run), mirrors the screen to the strip (Screen
  mode) while the session is unlocked and the display is on, and has hotkeys Ctrl+F9 (solid toggle), Ctrl+F10 (cycle
  strip mode), Ctrl+F11/F12 (strip brightness -/+). The strip is now **133 LEDs on COM7** in the owner's layout
  (confirmed with `--strip-test`).
- The Windows Service `RGBcontroller` (LocalSystem, automatic start) lights the strip and all 5 OpenRGB devices
  before the logon screen.
- Session and display policy (SPEC 4): Logon profile on the logon screen, active profile when unlocked, Lock
  profile (null = active) when locked. Display off while unlocked turns the strip off; display off while locked or
  on the logon screen turns the strip, keyboard and mouse off. Everything returns when the display turns on.
- Effects run from profiles. The active profile is **Rainbow**: radial pump/fans, keyboard wave right to left,
  mouse/GPU/board color cycle, RAM mirror, strip radial rainbow.
- CPU (whole processor):
  - static profile 0.012%;
  - Rainbow 0.97% with RAM at 5 FPS;
  - about 1.7% with the owner's RAM tuning (10 FPS);
  - the old setup used 2.58%.
- Sleep: black before suspend; after resume a rescan in the single OpenRGB brings everything back within about
  7 s, all 5 devices in one apply (O4 fixed).
- `RGBcontroller.Service.exe --status | --profile <name> | --group <name> ...` control the running Service over
  the named pipe, without elevation.

Repository: <https://github.com/Radek-Wojcik/RGBcontroller> (**private**, no LICENSE file: "All rights reserved").
- `main` contains the merged `stage-1-service`, `stage-2-effects` and `stage-3-power` branches (`--no-ff`) and is
  pushed, with tag `v0.3.0-stage3`.
- The stage branches are merged and can be deleted.
- The README describes stages 1-3 (refreshed through PR #2).
- `stage-4-agent` is pushed to origin so the owner can continue elsewhere; merge it after the stage 4 gate.

---

## 2. Owner preferences and working rules (binding)

These come from the owner during stages 0-3 and add to `CLAUDE.md`:

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
- **Owner's stage 2 wishes:**
  - RAM must run the mirror effect, not hardware mode, with a tunable FPS.
  - The UI (stage 5) must expose speed, wave length (scale) and FPS per group. SPEC 7.3 covers this; the backend is
    `TuneGroupAsync`.
  - Sleep/resume bugs are the owner's top priority.
- **In PowerShell, decimal numbers need a dot** (`--speed 0.08`); `0,08` becomes an array.
- **Tooling note for the AI:** in the Bash tool (Git Bash), long `python - <<'EOF'` heredocs containing C# code failed
  twice with "unexpected EOF". Write such edit scripts to the scratchpad with the Write tool and run
  `python <file>` instead.
- **Elevated install, the way the owner accepts it** (they click UAC):
  `Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',"Start-Transcript <scratchpad>\install.log; Set-Location '<repo>'; .\install-dev.ps1; Stop-Transcript" -PassThru`,
  then `WaitForExit()` and read the transcript. If the UAC prompt is cancelled, ask before retrying.
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
| `config.json` | Schema 2 (migrated; `config.v1.bak.json` next to it). Active profile Rainbow; the RAM group is tuned to 10 FPS, and Rainbow's RAM effect to speed 0.08, scale 0.3. Stage 4: `strip.ledCount` 133 and `strip.layout` = BottomLeft, clockwise, left 23, top 41, right 23, bottom 43, offset 9 (the strip starts mid-height on the left edge going up and ends with a 3 LED overlap). The previous file is `config.before-stage4.json`. | Edit it, or use `--group` / `--profile` |
| Agent autostart | HKCU `Software\Microsoft\Windows\CurrentVersion\Run` value `RGBcontroller.Agent` = `"C:\Program Files\RGBcontroller\RGBcontroller.Agent.exe"`. Agent logs: `%LOCALAPPDATA%\RGBcontroller\logs\agent-YYYYMMDD.log`. | `uninstall-dev.ps1` removes it |
| Adalight strip | **New strip mounted 2026-11-01: 133 LEDs**, the CH340 now enumerates as **COM7** (found by VID/PID, no config change needed). |  n/a |
| Adalight CH340 (old note) | **Drops off USB intermittently** (no VID_1A86 device in Windows at all); the owner was asked to check the cable and port. | n/a |

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

### Stage 2 additions
- `Core/Config/LightingConfig.cs` and `DefaultLighting.cs` (schema 2):
  - `lighting` holds layouts (zone shapes and segments), groups (zone sets with `maxFps`), palettes and profiles
    (effect per group plus ambilight plus master brightness).
  - Defaults are pre-mapped to the owner's device names.
  - `LightingV2Migration` drops the stage 1 section.
  - `ConfigValidator.Lighting.cs` checks the references.
  - `LightingEdits` applies `GroupTuning`.
- `Core/Effects`:
  - `Palette`: cyclic, 1024-entry lookup table.
  - `LedPoint`: x, y, angle and radius.
  - `Effect` subclasses: Off, Static, Breathing, Wave, Radial, Mirror, ColorCycle, Aurora.
- `Core/Lighting`:
  - `LedLayoutBuilder`: matrix, line, ring, point.
  - `DeviceModes`: Direct and hardware mode selection.
  - `LightingEngine`:
    - per-target FPS, change-only writes, brightness group x master;
    - hardware mode for whole devices, strip fallback (Screen shows Radial until stage 4);
    - `SetBlackout` for suspend;
    - `Render` does not allocate (tested).
- `Service/Lighting`:
  - `RenderLoop`: dedicated thread with a `HighResolutionTimer`, sleeps when nothing is animated; `RenderNow`
    for suspend.
  - `LightingController` is the `IDeviceLightingSink` the supervisor attaches devices to.
- `OpenRgbSupervisor`:
  - It detaches the engine on DEVICE_LIST_UPDATED, on session end and before blackout.
  - `NotifyResumed` triggers delay, rescan and re-apply.
- `Service/Ipc`:
  - `ControlServer`: pipe ACL with SYSTEM/Admins full, INTERACTIVE read/write, and the creating account.
  - `ServiceControlEndpoint` validates input.
  - `ControlClient` is the CLI.
  - IPC protocol 1.2 adds GetProfiles, SetActiveProfile and TuneGroup.
- `RgbControllerService` handles power events: Suspend leads to `LightingHost.OnSuspend`, and the Resume* events
  to `OnResume` (debounced).

### Stage 3 additions (branch `stage-3-power`)
- `Core/Config/LightingConfig.cs`: `lighting.displayOff` with `unlocked` (default: strip) and `locked` (default:
  strip, keyboard, mouse) `TurnOffSet`s (`strip` flag plus OpenRGB device names). New keys with defaults only, so
  the schema stays 2; the owner's `config.json` does not contain them yet and runs on the defaults.
- `Core/Lighting/SessionPolicy.cs`: `SessionKind` (Unlocked, Locked, LogonScreen), `LightingSituation`
  (session + display on), `ProfileFor` and `TurnedOff` (the logon screen uses the locked set).
- `LightingEngine`:
  - `SetSituation` selects the profile and forces the turn-off set to black (devices keep their per-LED mode);
    the log line `Rendering profile X (Locked, display off)` shows the situation.
  - Static device frames are sent 3 times (0 / 250 / 500 ms): the Razer keyboard is written in several USB
    packets and one got lost.
  - `ResendAll` re-sends modes and frames; `LightingController` calls it 5 s and 30 s after every attach
    (a lost SMBus mode write left one RAM stick dark after a cold boot).
- `Service/Power/`:
  - `ConsoleSession`: WTS query of the console session (user name, lock flag).
  - `DisplayStateMonitor`: `PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE)` with an
    `UnmanagedCallersOnly` callback (ServiceBase drops the power setting payload).
  - `SituationTracker`: logs every raw event, re-reads WTS on each trigger and 2 s later, calls the engine under
    its lock so changes arrive in order.
- `RgbControllerService`: `CanHandleSessionChangeEvent`, `OnSessionChange` to `LightingHost.OnSessionChange`.
  `LightingHost` reads the session before the first frame and refreshes it on resume.
- `OpenRgbSupervisor`: after a resume rescan it waits up to `RescanCompleteTimeout` (10 s) for the device count from
  before the sleep (O4).
- `StripWriter`: open failures now name the port (was `port null`).

### Stage 4 additions (branch `stage-4-agent`)
- `Core/Config/AmbilightSettings.cs`: `strip.layout` (`StripLayoutConfig`: start corner, clockwise, LEDs per edge,
  offset, gaps; LEDs past the path wrap around the loop = overlap), `capture` (`ScreenCaptureConfig`, every SPEC 6.2
  parameter) and `hotkeys` (`HotkeysConfig`). New sections with defaults, schema stays 2. `ConfigValidator.Agent.cs`.
- `Core/Hotkeys/HotkeyGesture`: "Ctrl+F9" parser to RegisterHotKey modifier and virtual key.
- `Core/Ambilight`: `StripLayout` (placement per LED), `EdgeSampler` (band per LED with overlap, stride-limited),
  `LetterboxDetector` (outer-quarter rows, symmetric bars, 1.5 s hysteresis, dark frames keep the result),
  `AmbilightPipeline` (black threshold + soft knee, gamma 2.0, saturation, attack 80 / release 300 / black release
  60 ms, snap to 0), `ScreenImage`/`ColorMath` (BGRA8 and scRGB FP16, HDR tone map with the SDR white level).
- `Core/Lighting/LightingEngine`: strip points from the layout; `SetScreenFeed`, `PushScreenFrame` (no allocation),
  `SetAllOff`, `SetStripTest`, `IsScreenWanted` (Screen mode, unlocked, display on, nothing off).
- IPC 1.3 (`Ipc` now references `Core`): `SubscribeAsync` + `IServiceEvents.StateChanged` notifications
  (`AgentState` with a sequence number), `GetAgentSettingsAsync`, `ChangeStripAsync` (mode, brightness, delta,
  toggle solid, cycle), `SetAllOffAsync` (runtime only), `StepProfileAsync`, `ShowStripTestAsync`; frame type
  `CaptureUnavailable`.
- Service: `Ipc/FrameServer` (frame pipe, only the console session's Agent drives the strip, newest wins, malformed
  frames close the connection), `Ipc/ServicePipes` (shared pipe ACL, client session id), per-connection
  endpoints and `Broadcast` in `ControlServer`, `LightingHost` agent operations (strip changes saved 2 s after the
  last one, mode before Solid remembered in memory), `--strip-test [seconds]` CLI.
- Agent: `Capture/DesktopDuplicator` (DXGI duplication, GPU mip downscale to about 480 px, non-blocking map),
  `Capture/DisplayInfo` (EDID width, SDR white level), `Capture/ScreenCaptureLoop` (capture <= 20 FPS, output <= 30
  FPS, idle blocking in AcquireNextFrame, 1.5 s grace then fallback), `Capture/CaptureProbe` (`--capture-probe`),
  `ServiceConnection` (control + frame pipes, back-off reconnect), `HotkeyWindow`, `Tray/*` (dark menu, owner-drawn
  brightness slider, drawn ring icons), `AgentApplication`.
- `install-dev.ps1` publishes Service and Agent into one folder, registers the HKCU Run value and starts the Agent
  through `explorer.exe` (unelevated); `uninstall-dev.ps1` stops it and removes the value.

### `tests/RGBcontroller.Tests` (xUnit v3, 198 tests after stage 4, all green)
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
11. **Stage 2 CPU:** the RAM is the whole budget (about 0.16% total CPU per RAM FPS, kernel time in OpenRGB's
    SMBus writes). Everything else costs about 0.13%; our Service costs about 0.02%. Details are in
    `docs/spikes/stage2-effects.md`.
12. **Resume:** devices come back in their power-on state (RAM rainbow, GPU dark, board and mouse factory
    rainbow), so after resume the modes must be pushed again. A rescan in the same OpenRGB plus re-apply does
    this; no restart is needed.
13. **Logon:** Windows authenticates the user about 11 s after boot, and profile load ends at +24-28 s on every boot (the same before and after the install).
14. **Spike R4 (stage 3):** session events via `ServiceBase` work in Session 0; display state via
    `PowerSettingRegisterNotification` with `DEVICE_NOTIFY_CALLBACK` works in Session 0 and console mode. Do not
    try the service status handle route: `ServiceBase` drops the `POWERBROADCAST_SETTING` payload. Session events
    arrive on thread-pool threads, so always re-read the state from WTS instead of trusting event order.
15. **Windows lock screen turns the display off after about 60-80 s.** After every logon Windows reports a
    `SessionLock` 0.3 s later and an unlock 13-28 s later (cause not investigated; harmless while the Lock profile
    equals the active one).
16. **Never send a static frame or a mode only once.** USB (Razer, row by row) and SMBus (HyperX RAM) writes can
    be lost without any error reaching the SDK client. Static frames are repeated twice, and modes plus frames are
    re-sent 5 s and 30 s after every attach.
17. **AmbiBox grabs COM3 if someone starts it** (`Access to the path 'COM3' is denied` in the log). The Service
    keeps retrying and takes the port back when AmbiBox lets go; close AmbiBox.
18. **Stage 4 / spike R7:** Vortice 3.8.3 `DuplicateOutput1` crashes (access violation), so it is called through
    the vtable; a blocking `Map` spins about 2 ms CPU per frame in the NVIDIA driver, so it is mapped with
    DO_NOT_WAIT plus 0.5 ms high-resolution sleeps (Agent 0.18% -> 0.05% with a constantly changing desktop). UAC
    secure desktop: duplication lost, recreated after 2.3 s.
19. **The Claude desktop app is MSIX:** processes started from Claude's shell write `%LOCALAPPDATA%` into
    `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\...`, and the shell reads that copy first. Start the Agent
    through `explorer.exe` and delete that LocalCache `RGBcontroller` folder if Agent logs look stale.
20. **Stage 4 CPU (60 s, mostly still desktop):** Agent 0.004%, Service 0.001%; OpenRGB 1.6% is the RAM at 10 FPS.


---

## 6. Open items and known gaps (carry into the next stages)

| # | Item | Where it belongs |
|---|---|---|
| O1 | OpenRGB writes no log file despite the seeded `"LogManager": {"log_file": true}`; logs would help diagnostics. Check the exact setting name or the `--loglevel` / `-v` flags. | Stage 3 or 6 diagnostics |
| O2 | Control pipe exists (profiles, tuning, status). Missing: the frame pipe (strip colors from the Agent, preview to the UI), config edits beyond tuning, and Hello enforcement. | Stages 4-5 |
| O3 | **Done in stage 3** (lock, display, logon/logoff, Logon/Lock profiles). Remaining gate evidence: 7 more sleep cycles and 3 hibernate cycles. | Stage 3 gate |
| O4 | **Done in stage 3** (wait for the expected device count after a resume rescan). The hot-plug path (DEVICE_LIST_UPDATED without resume) still applies after the quiet period only. | Done |
| O5 | The G502 can be set to `Off` per profile (effect Off); it is still lit in Rainbow. Ask the owner whether the mouse should be off. | Stage 2 gate / 5 |
| O10 | The Adalight CH340 drops off USB intermittently. 2026-11-01: gone 15:33-16:52, several dropouts around unplugging the LED BOX, none in a 10-minute watch without touching it. Once the port accepted writes but the strip stayed dark until the LED BOX was power-cycled (Arduino stuck). Suspect power/ground of the LED BOX or the USB cable/port. The Service always reconnected by itself. | Owner (hardware) |
| O12 | The turn-off sets and the Lock profile are only editable in `config.json`; the UI (stage 5) needs the two checkbox lists from SPEC 4 and a Lock profile picker. | Stage 5 |
| O13 | USB selective suspend is on in the power plan and the root hub of the CH340 may be powered down by Windows; not proven to matter. Owner's choice to change (system setting). | Owner |
| O11 | RAM smoothness versus CPU: owner tuning 10 FPS (about 1.7% total). A smoother RAM would need about 20 FPS (about 3.3%). | Owner decision |
| O6 | Owner's remarks, still open: fans "spin up hard / go dark" at power-on (not clarified whether RPM or LEDs); the logon-screen shutdown button needed 3 clicks once (not reproduced, nothing in the logs). | Ask if they come up again |
| O7 | `third_party/openrgb` needs `tools/Get-PinnedOpenRgb.ps1` on a fresh checkout; a release build should not depend on `C:\Program Files\OpenRGB`. | Stage 6 installer |
| O8 | The ProgramData `config.json` was written before some keys existed (`minimumUptimeSeconds`); missing keys take the code defaults. Note this when keys are renamed (migration). | Ongoing |
| O9 | UI and the installer: SPEC stages 5-6. | Stages 5-6 |
| O14 | **Owner wish for the stage 5 strip wizard:** a screen where the owner marks on a drawn monitor outline where the strip starts and ends, where it turns the corners, where there is an overlap, and how many LEDs each run has; universal for any layout. The config model (`StripLayoutConfig`) already covers start corner, direction, counts, offset, overlap (wrap) and gaps. | Stage 5 |
| O15 | Stage 4 gate tests not run yet (see section 7). The owner said bugs will be fixed afterwards. | Stage 4 gate |

---

## 7. Next steps

1. **Run the stage 4 gate** with the owner (give the steps in Polish, read logs and measure yourself):
   1. black screen = strip off (YouTube "black screen" fullscreen);
   2. dark scenes dark, no tail on fades;
   3. smooth at 20/30, letterbox film: bands move into the picture after about 1.5 s, subtitles do not disturb;
   4. CPU with a colorful fullscreen video, 60-70 s, `tools\Measure-RGBcontroller.ps1`: Agent + Service (+ OpenRGB
      minus its RAM baseline) below 1%;
   5. borderless game mirrors; exclusive fullscreen mirrors or falls back within about 2 s and returns;
   6. HDR on (Win+Alt+B): colors not washed out or blown;
   7. hotkeys Ctrl+F9/F10/F11/F12 and the tray menu (profiles, strip mode, slider, all off);
   8. sleep and wake: Screen mode back, hotkeys still work;
   9. Win+L: strip falls back to the rainbow, back to Screen after unlock.
2. Fix what the gate finds on `stage-4-agent`, re-install with `install-dev.ps1` (elevated, the owner clicks UAC).
3. After the gate: bump `Directory.Build.props` to 0.4.0, update this file, merge `stage-4-agent` into `main`
   with `--no-ff`, tag `v0.4.0-stage4`, push, refresh the README through the skill.
4. Still open from before: should the G502 mouse be off (O5)?
5. Then stage 5 (UI, including the strip wizard of O14).

## 8. How to resume in a new session

1. Open Claude Code in `C:\Users\Trolli04\Desktop\STUDYY\Projekty moje\RGBcontroller`.
2. Paste the prompt below.

```text
You are continuing RGBcontroller (Windows RGB + Adalight controller) for the owner Radek. Talk to the owner in
Polish; code, comments, commits and docs are in English.

Read, in this order: SPEC.md, CLAUDE.md, docs/HANDOFF.md, then docs/spikes/*.md. HANDOFF.md describes exactly what
exists, the owner's PC state, binding preferences (commits authored only by the owner with the noreply address, no
Co-Authored-By/Claude attribution anywhere), decisions and open items. Stages 0-3 have passed (tags v0.0.1-stage0,
v0.1.0-stage1, v0.2.0-stage2, v0.3.0-stage3). Stage 4 (Agent, tray, hotkeys, ambilight) is built on branch
stage-4-agent and installed on this PC; its gate tests are still open.

First: `git checkout stage-4-agent && git pull`, run `.\build.ps1 -Test` (expect 198 green tests), and run
`& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --status`. Then report in Polish in a few lines and
continue with HANDOFF.md section 7 (stage 4 gate).
```

Useful commands:

```powershell
.\build.ps1 -Test
& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --probe
& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --status
& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --profile "Solid White"
& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --group RAM --fps 10 --speed 0.08 --scale 0.3
& 'C:\Program Files\RGBcontroller\RGBcontroller.Service.exe' --strip-test 120
& 'C:\Program Files\RGBcontroller\RGBcontroller.Agent.exe' --capture-probe 20 $env:TEMP\probe
.\tools\Measure-TimeToFirstLight.ps1 -Last 2
.\tools\Measure-RGBcontroller.ps1 -Label check -DurationSeconds 60 -OutDir $env:TEMP
Get-Content (Get-ChildItem C:\ProgramData\RGBcontroller\logs\service-*.log | Sort-Object LastWriteTime)[-1].FullName -Tail 30
```
