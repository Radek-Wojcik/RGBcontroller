# Architecture

How RGBcontroller is put together and why. The requirements and the verified hardware facts are in
[SPECIFICATION.md](SPECIFICATION.md); measurements and spikes are in [spikes/](spikes/).

## Processes

```
 boot  --> RGBcontroller.Service  (LocalSystem, automatic start, no window, always running)
             |- supervises one private OpenRGB.exe (SDK server on localhost:6742)
             |- owns the Adalight strip's serial port
             |- effect engine, device model, profiles, power / session / display policy
             '- IPC server (named pipes)
 logon --> RGBcontroller.Agent    (user session, tray icon, no window)
             |- tray menu, global hotkeys
             |- screen capture and the ambilight pipeline (only while the strip mirrors the screen)
             '- IPC client
 on demand > RGBcontroller.UI     (WPF shell hosting WebView2 with the React front end, exits when closed)
             '- IPC client
```

- **Only the Service touches hardware.** The Agent never opens the strip's port or the OpenRGB socket; it sends the
  sampled strip colors to the Service. That keeps one writer per device and lets the lights work on the logon screen with
  nobody logged in.
- **Failure isolation.** A crash of the UI or the Agent does not affect lighting. The Service restarts its OpenRGB with a
  back-off and is itself restarted by the Service Control Manager (after 1 s, 5 s, 30 s).
- **One OpenRGB.** The Service launches the pinned build from `{app}\openrgb` with its own configuration folder and no
  plugins, and removes strays it did not start. It talks to it with its own small SDK client
  ([docs/spikes/R2-R3-openrgb-sdk.md](spikes/R2-R3-openrgb-sdk.md)).

## Projects

| Project | Role |
|---|---|
| `RGBcontroller.Core` | Everything without Windows service or UI dependencies: color and palette model, effects, the lighting engine, ambilight sampling and the screen pipeline, strip layout and the Adalight frame writer, session policy, hotkey parsing, config model, validation and migrations. |
| `RGBcontroller.Ipc` | The contract between the processes: `IServiceControl` (JSON-RPC over a named pipe), the frame codec, the versioned protocol. |
| `RGBcontroller.Service` | The Windows service: host and control endpoint, OpenRGB launcher and supervisor, device lighting, power / session / display monitors, USB arrival monitor, log tail reader. |
| `RGBcontroller.Agent` | Tray, hotkeys, DXGI desktop duplication, the capture loop, the connection to the Service. |
| `RGBcontroller.UI` | The WPF window, WebView2 hosting `ui/dist`, and `Bridge`, which maps front-end calls to `IServiceControl`. |
| `ui/` | React + TypeScript + Vite front end. `npm run dev` runs it on stand-in data (`mock.ts`) for design work. |
| `RGBcontroller.Tests`, `ui/src/*.test.ts` | xUnit and Vitest. |
| `installer/`, `tools/` | Installer script and build, publish, preflight and measurement scripts. |

## Lighting engine

`LightingEngine` turns the active profile into one render target per device group plus the strip. Each target has LED
points (position, ring angle, matrix cell), bindings from LED ranges to an effect, a frame rate cap and a brightness.
The engine is rebuilt when the profile, the config, the situation (locked, display off, sleep) or a temporary overlay
(strip test or marker, device identify) changes; between rebuilds it only renders. Effects are pure functions of
position and time, so devices stay in step. Static targets are written once and the loop idles.

Two rules shape it:

- **The bus is the budget.** RAM (SMBus) and GPU (I2C) writes cost far more CPU than the effect math, so every group has
  its own frame rate cap (about 0.16% of total CPU per RAM frame per second;
  [spikes/stage2-effects.md](spikes/stage2-effects.md)).
- **Direct mode only.** OpenRGB's "custom mode" call does not touch the hardware, so the Service always sends
  `UPDATEMODE` with the Direct mode ([spikes/R1-R5-R6-stage1.md](spikes/R1-R5-R6-stage1.md)).

## Ambilight

The Agent captures the desktop with DXGI duplication and downscales on the GPU, so a 4K frame never reaches the CPU. The
CPU side samples the edges (`EdgeSampler`), detects letterbox bars, and applies averaging, black threshold, gamma,
saturation, brightness and the current limiter (`AmbilightPipeline`). The Service receives the strip colors, applies
fades and writes the Adalight frame. When capture is impossible (lock screen, no Agent, a protected window) the
profile's fallback mode (Radial, Solid or Off) shows instead. The strip layout (`StripLayout`, mirrored in `ui/src/strip.ts`)
maps LED numbers to places around the screen.

## Power, session and display

`Service/Power` receives console session changes, display on/off and resume notifications, and USB arrivals.
`SituationTracker` and `SessionPolicy` decide what each output does for the situation (unlocked, locked, display off,
sleep) from the turn-off sets in the config. After resume or when a USB device appears, the supervisor rescans devices
inside the single OpenRGB and re-applies everything, modes included. On stop, suspend and shutdown everything goes black.

## IPC

- Control: JSON-RPC (StreamJsonRpc) on the pipe `RGBcontroller.Control`. SYSTEM and Administrators have full control,
  interactive users may connect; the command surface exposes only application operations with validated inputs (no paths,
  no process launch).
- Frames: a binary pipe `RGBcontroller.Frames` for the strip colors (Agent to Service) and the live preview (Service to UI).
- Versioning: `IpcProtocol.Current` (major.minor). Additive changes bump the minor version and say `since 1.x` on the
  method. A client or server of an older major is reported, not crashed on. The Service only streams preview data while a
  UI is subscribed.

## Configuration

One JSON file in `%ProgramData%\RGBcontroller\config`, schema-versioned with migrations (`ConfigMigrator`), written
atomically by the Service only. The UI edits through `GetConfig` / `UpdateConfig` with an optimistic revision; the
Service validates the whole config (`ConfigValidator`), applies what it can live (lighting, strip layout, capture,
hotkeys, header LED counts) and reports `RestartRequired` for the rest (strip hardware, OpenRGB, logging). Per-window
UI preferences (accent, dashboard tile arrangement) stay in the WebView's local storage and never reach the Service.

## UI

The WPF shell hosts WebView2 (`ui/dist`) and relays messages between the page and the Service pipe (`Bridge`). The page
keeps a store (`store.tsx`) with the latest config snapshot, agent state and device list; every edit goes through
`editConfig`, which retries once on a revision conflict. Pure helpers (`config.ts`, `strip.ts`, `mapping.ts`,
`tileLayout.ts`) hold the logic and are unit tested. Wizards (device mapping, strip layout) light real hardware through
`Identify` and `ShowStripMarker` and show the Service's real colors back, so a step is done when the drawing matches the desk.

## Installation

The installer (`installer/RGBcontroller.iss`, Inno Setup) copies the self-contained build and the pinned OpenRGB to
`Program Files\RGBcontroller`, secures `%ProgramData%\RGBcontroller`, registers the service (LocalSystem, automatic,
restart on failure), adds the Agent to the user's logon autostart and creates shortcuts. `tools/InstallPreflight.ps1`
checks PawnIO and WebView2 and finds OpenRGB and AmbiBox autostart entries; with the user's consent it disables them
(never deletes) and the uninstaller restores them. Development uses `install-dev.ps1` / `uninstall-dev.ps1`, which share
`tools/Publish-App.ps1` with the installer build.

## Safety rules

Fan and pump control are never touched, there is no raw SMBus/I2C access (OpenRGB is the only path), a second OpenRGB is
never started, and the installer never deletes or rewrites another program's configuration or autostart entries
(SPECIFICATION.md section 9).
