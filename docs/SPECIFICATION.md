# RGBcontroller: Product and Engineering Specification

Version: 1.0 spec, 2026-10-30. Author: Radek Wójcik (single user, one machine, hardware configurable via settings).
Language of UI, code, comments, commits and docs: **English**. Code comments cite sections of this document as `SPEC x.y`.

This document is the requirements and design reference of the app. Hardware facts were measured on the author's PC in earlier tests; the results that matter are summarized in section 2 and in [spikes/](spikes/). Items marked **[VERIFY]** are technical assumptions that were confirmed by a short spike before building on them. Items marked **[DECISION]** are design choices that hold unless a spike proves them impossible.

---

## 1. Goal

One small, reliable Windows app that:

1. Lights all RGB hardware (keyboard, mouse, GPU, RAM, AIO pump, fans, motherboard headers) with synchronized, customizable effects.
2. Drives an Adalight ambilight strip behind the monitor (screen mirroring, radial rainbow, solid color).
3. **Starts as early as possible at boot, so the keyboard already glows on the Windows logon screen.**
4. Survives sleep, hibernate, display-off, lock and shutdown without flicker, duplicate processes or dark LEDs.
5. Uses almost no CPU in the background (the reason this project exists: OpenRGB with the Effects plugin at 60 FPS burns ~2.6% of the whole CPU, about 83% of one thread, doing nothing useful).

Working name and product name: **RGBcontroller**.

### Non-goals (v1)

- Fan/pump speed control (FanControl stays; never write fan registers; the risks are why this project never writes fan registers).
- Audio control, taskbar tweaks, cloud, telemetry, auto-update, multi-monitor ambilight, per-application auto profile switching (process watching adds background load, author said avoid).
- Writing our own hardware drivers or any direct SMBus/I2C code. All hardware except the Adalight strip goes through OpenRGB.
- Firmware writes ("save to device") are not used by default.

---

## 2. The machine (facts, already verified)

| Item | Value |
|---|---|
| OS | Windows 10 Pro 22H2 (19045), Polish locale. Must also work on Windows 11. No security patches after 2026-10-13. |
| CPU | Intel Core i9-14900K, 32 logical threads |
| Board | ASUS TUF GAMING H670-PRO WIFI D4 |
| GPU | Gigabyte RTX 3080 12G Gaming OC |
| Monitor | AOC AGON PRO AG326UD, 31.5", 3840x2160, 165 Hz, QD-OLED, HDR. Mostly SDR; HDR only sometimes in games. |
| OpenRGB | 1.0rc2 (v0.9+, commit 0fca93e, 2025-09-14). Uses the **PawnIO** driver for SMBus (already installed and working). |
| Polish-locale pitfall | `typeperf`/`Get-Counter` with English counter names do not work. Use `tools/Measure-RGBcontroller.ps1` (the `NtQuerySystemInformation` approach) for every CPU measurement. Task Manager shows ~1.8x higher numbers for the same process (different normalization); always compare with the same tool. |

### RGB device inventory (from OpenRGB profile and logs; detection takes ~6 s)

| OpenRGB device | Notes |
|---|---|
| HyperX Predator RGB (DRAM) | I2C `i801` addr 0x27, 4 slots (DDR4), **5 LEDs per slot (20 total)**, modes: Direct, Static, Rainbow, Heartbeat, Spectrum Cycle, Breathing, Bounce. Sticks stand **vertically** relative to the desk. |
| Gigabyte RTX 3080 12G Gaming OC | RGB Fusion GPU device via NvAPI I2C, addr 0x62, one zone, one LED, modes Direct, Breathing, Flashing, Dual Flashing, Color Cycle, Spectrum Cycle. **[VERIFY]** NvAPI I2C works from Session 0 (see risk R1). |
| ASUS TUF GAMING H670-PRO WIFI D4 (Aura USB, VID 0B05 PID 19AF) | Zones: **Aura Addressable 1/2/3, 20 LEDs each**; Aura Mainboard RGB Header 1/2/3 (12 V). The AIO pump and fans are on the addressable headers, **exact assignment unknown** (author does not know; the mapping wizard resolves it, see 7.4). |
| Razer Ornata Chroma V2 (VID 1532 PID 025D) | Keyboard, OpenRGB full 104-key ANSI matrix, 6 rows x 21 columns. |
| Logitech G502 HERO | Primary + Logo zones (1 LED each). Backlight currently off by author choice, but the device is controllable. |

Software left running next to us, untouched: FanControl, EarTrumpet, TranslucentTB. AmbiBox is installed but unused (`C:\Program Files (x86)\AmbiBox`); it must not run (it would fight for COM3).

### Adalight ambilight driver (verified on the hardware)

| Parameter | Value |
|---|---|
| Board | "LED BOX", PCB AMB L32+48, CH340G (USB-serial) + 48-pin MCU |
| Port | COM3 (`USB-SERIAL CH340`, VID_1A86 PID_7523). Must be discovered by VID/PID, not hardcoded; COM number is a setting/override. |
| Protocol | Adalight. Frame: `'A' 'd' 'a' hi lo chk R0 G0 B0 ... ` with `hi/lo = (ledCount-1)` big-endian and `chk = hi XOR lo XOR 0x55`. Colors are **RGB** order (verified). |
| Baud | 115200 8N1 (DTR/RTS irrelevant). |
| Handshake | Firmware sends `Ada\n` about every second while idle and stops when frames arrive. |
| Startup | The board needs ~0.8 s after the port opens; the first frames can be lost. Send several frames after open. |
| Throughput | 113 LEDs = 345-byte frame = **max 33.4 FPS**. Send at most ~30 Hz. Never exceed the link limit. |
| Strip | WS2812B, 60 LED/m, **113 LEDs**, power supply 5 V 5 A. |
| Current | Full white measured: 25% 0.5 A, 50% 1.0 A, 75% 1.5 A, 100% 2.2 A (linear, ~19.5 mA/LED). PSU headroom is >2x. A current limiter exists only as a safety fuse (default 4.5 A estimated). |

All of the above values are **settings** with these defaults (port, LED count, baud, color order, current limit), because hardware must be configurable.

---

## 3. Architecture [DECISION]

Three executables plus a pinned OpenRGB. One hardware author, small always-on parts, rich UI only on demand.

```
 boot ──► RGBcontroller.Service  (LocalSystem, auto start, no UI, ALWAYS running)
            ├─ supervises OpenRGB.exe (headless SDK server, localhost:6742, single instance)
            ├─ owns COM3 (Adalight writer)
            ├─ effect engine (all non-screen effects), device model, profiles
            ├─ power / session / display policy
            └─ IPC server (named pipes)
 logon ─► RGBcontroller.Agent    (user session, no window, tray icon, tiny)
            ├─ tray icon + menu + brightness flyout
            ├─ global hotkeys
            ├─ screen capture + ambilight pipeline (only while Screen mode is active)
            └─ IPC client (sends strip colors and hotkey actions to the Service)
 on demand ► RGBcontroller.UI    (WebView2 host + web front end, started by the user, exits when closed)
            └─ IPC client (config, live preview, wizards)
```

Rules:

- **Only the Service touches hardware.** The Agent never opens COM3 or the OpenRGB socket. It sends captured strip colors (113 x 3 bytes, <=30 Hz) over IPC; the Service writes them to COM3. This guarantees one author and lets the strip stay lit on the logon screen.
- The Service must be fully functional with no user logged in and no Agent running: effects, strip fallback mode, power handling.
- The Agent exists only because screen capture, hotkeys and the tray need an interactive session.
- The UI is launched from the tray ("Open RGBcontroller") or a Start menu shortcut. It is never resident. Closing the window ends the process. Live preview is streamed by the Service only while a UI is subscribed.
- Failure isolation: a crash of the UI or Agent must not affect lighting; the Service restarts crashed children (OpenRGB) with back-off and is itself restarted by the SCM (failure actions: restart after 1 s, 5 s, 30 s).

### Tech stack [DECISION]

- C# on **.NET 10 LTS** (.NET 8 LTS ends support in Nov 2026, so do not use it). Solution `RGBcontroller.sln`, projects: `RGBcontroller.Core` (device model, effects, color, config), `RGBcontroller.Service`, `RGBcontroller.Agent`, `RGBcontroller.UI` (WPF shell hosting WebView2) plus `ui/` (front end), `RGBcontroller.Ipc` (contracts), `RGBcontroller.Tests`, `installer/`.
- OpenRGB client: **OpenRGB.NET** NuGet. **[VERIFY]** protocol version of OpenRGB 1.0rc2 against the library's supported versions in a spike; fall back to a small own SDK client if needed (the protocol is a simple binary TCP protocol).
- Screen capture: **DXGI Desktop Duplication** via **Vortice.Windows** (D3D11/DXGI). Downscale on the GPU.
- Serial: `System.IO.Ports` or direct `CreateFile` overlapped I/O (choose what gives reliable reconnect).
- Front end: **React + TypeScript + Vite**, hosted in WebView2 (WebView2 Evergreen runtime; the installer must ensure it is present on Windows 10). Canvas/WebGL for live previews. No heavy UI framework look-alikes; design direction in section 8.
- Logging: Serilog (or Microsoft.Extensions.Logging with file sink), rolling files in `%ProgramData%\RGBcontroller\logs`.
- IPC: named pipes, JSON-RPC (StreamJsonRpc) for control; a separate stream pipe for preview frames and ambilight frames. **Pipe ACL**: SYSTEM and Administrators full control, interactive users may connect but the command surface exposes only app operations (no arbitrary paths, no process launch, validated inputs). Protocol is versioned; mismatched Service/UI/Agent versions must be detected and reported, not crash.
- Config: JSON, schema-versioned with migrations, atomic write (temp file + replace), stored in `%ProgramData%\RGBcontroller\config\`. Only the Service writes it; UI asks the Service to change it.
- Installer: Inno Setup or WiX. Ships a **pinned OpenRGB 1.0rc2** inside the install folder (`{app}\openrgb\`), registers the Service, the Agent autostart (logon, per user, non-elevated), Start menu shortcut, and cleans up on uninstall. Detects the PawnIO driver and fails with a clear message if missing. Detects the author's existing OpenRGB autostart entry (Task Scheduler task / Run key / Startup folder) and **offers to disable it (never deletes it silently)**. Also detects AmbiBox autostart and offers the same.

### OpenRGB supervision (Service) [DECISION]

- Start the pinned binary headless with its own config folder, e.g. `OpenRGB.exe --server --server-port 6742 --noautoconnect --config "%ProgramData%\RGBcontroller\openrgb"` (**[VERIFY]** exact flags with `--help` on the pinned build; passing CLI options normally suppresses the GUI, confirm). The separate config folder has **no plugins**, so the Effects plugin can never load again.
- Bind to loopback only.
- **Singleton guarantee**: the Service owns the OpenRGB process via a Job Object (kill-on-close), keeps a named mutex, and on start terminates stray `OpenRGB.exe` processes that it did not launch (log it). After resume from sleep it **never starts a second instance**; it re-scans the existing one.
- Watchdog: detect exit or unresponsive SDK, restart with exponential back-off (1, 2, 5, 15, 30 s), then reapply the active profile.
- **After resume / device change, re-scan devices inside OpenRGB** (SDK rescan request; **[VERIFY]** it exists in 1.0rc2 protocol and that OpenRGB.NET exposes it; fallback = restart the supervised OpenRGB process). Reconnecting the socket alone is not enough because USB devices re-enumerate.
- Put every controllable device in **Direct** mode when running our effects; restore the device's own mode only if the user chooses a hardware-mode option for it (7.3).
- **[VERIFY] Boot speed**: OpenRGB detection takes ~6 s. Measure the time from Service start to first keyboard frame. The Adalight strip must light independently and immediately (do not wait for OpenRGB).

---

## 4. Power, session and display behavior (author requirements)

Everything must work with **zero duplicate OpenRGB instances and no visible flicker** after sleep (the author's current problem is blinking LEDs after a second instance appears).

| State | Behavior |
|---|---|
| Boot, logon screen (no user) | Service already running. Apply the **Logon profile** (separate profile, default: calm rainbow on everything, strip in radial rainbow because screen capture is impossible). Keyboard must be lit here. |
| User logged in | Agent starts, applies the **Active profile**. Strip in Screen mode if selected. |
| Locked, display on | Everything stays lit using the **Lock profile** (default: same as active profile, but Screen-mode strip is replaced by its configured fallback because capture is unavailable). |
| Locked, display off | Turn off the strip, keyboard and mouse by default (these are the author's choices; the set is configurable). |
| Display off (unlocked, idle timeout) | Turn off the strip by default; the remaining devices optionally (per-device checkboxes in settings: "turn off when display is off"). Restore when the display turns on (mouse move etc.). |
| Sleep / hibernate | Before suspend: stop effects and send black to everything (strip and OpenRGB devices). On resume: wait for USB devices to settle (configurable delay, start with 3 s), **rescan devices in the single OpenRGB instance**, reapply the right profile for the current session state. |
| Shutdown | Send black to everything in the shutdown handler. |
| Device hot-plug | Debounced rescan (WM_DEVICECHANGE / device notification), reapply profile. |
| OpenRGB or COM3 lost | Reconnect loop with back-off, indicate state in UI/tray, never crash. |

Per-device **turn-off policy** is two sets of checkboxes (display off while unlocked; display off while locked) with the defaults above.

Implementation notes (all **[VERIFY]** by a spike in stage 3, because services are special):

- Use a custom `ServiceBase` (or direct `RegisterServiceCtrlHandlerEx`) so the Service receives: power events (suspend/resume), session change events (lock/unlock/logon/logoff), shutdown, and **console display state** (`GUID_CONSOLE_DISPLAY_STATE` via `RegisterPowerSettingNotification` with `DEVICE_NOTIFY_SERVICE_HANDLE`). The generic .NET host `WindowsServiceLifetime` does not surface these; do not rely on it for them.
- Windows **Fast Startup** (hybrid shutdown) must not break the logon-screen lighting; test cold boot and restart.
- Service start type: automatic, not delayed. Keep service dependencies minimal.
- Time budget for black-on-suspend is small; send black frames first, do cleanup after.

---

## 5. Effect engine (Service) [DECISION]

### 5.1 Principles

- One render loop for all devices, one clock. Fixed timestep, high-resolution timer, **no allocations in the hot loop** (pre-allocated buffers, float math, palette lookup tables).
- **Send only when colors changed.** A static effect sends once (0% CPU afterwards). Rainbow sends at its FPS.
- Per-device **FPS cap** (defaults: strip 30, keyboard 30, addressable headers 30, mouse 15, GPU 10, **RAM 10-15**). RAM and GPU are I2C/SMBus: low rate by default; the UI shows a warning on raising it. Detection of I2C devices happens once at start and after resume, never in a loop.
- Brightness is applied last in the pipeline per device group and as a master factor; 100% means value 255 (no hidden ceiling).
- Spatial awareness: every LED has a position (2D, normalized 0..1) so linear, radial and mirrored effects are synchronized across devices and drawn consistently.

### 5.2 Effects (all take palette, speed, direction, scale/width, brightness)

| Effect | Description | Default use |
|---|---|---|
| Static | One color, sent once. | |
| Breathing | Smooth fade of color(s). | |
| Wave | Linear gradient moving along an angle/direction. | **Keyboard: rainbow chasing right to left** |
| Radial | Gradient by angle around a center (rotating) or by distance from center (expanding). | **AIO pump and fans: radial rainbow** |
| Mirror | Gradient by distance from a center line, moving outward symmetrically. | **RAM: from the middle of each stick, up and down, symmetric** |
| Color cycle | Whole device changes color uniformly over time. | **Mouse and GPU** |
| Aurora (the "extra nice theme") | Slow flowing gradient (teal, green, violet) with soft noise modulation; designed to match the rainbow/cyberpunk themes and each device's layout. | Theme |

Palettes are gradients with editable stops (rainbow wheel is just a palette preset). Effects are parameterized; no scripting, no layers (explicitly out of scope: unbounded CPU risk).

### 5.3 Defaults for the author's current setup ("Rainbow" profile)

- Pump + fans: Radial rainbow. Keyboard (Ornata V2): Wave rainbow, direction right to left. Mouse (G502) and GPU: Color cycle. RAM (4 sticks): Mirror rainbow from the center LED outward, symmetric up and down on every stick. Motherboard onboard zones and headers: follow the nearest group or off (user-choosable).
- Strip: Screen mode (see section 6). Hardware modes are never used for the synchronized rainbow, because hardware modes cannot be synchronized between devices. Per-device option "use hardware mode" exists only for devices where the author prefers 0 CPU over sync (RAM especially).

### 5.4 Themes and profiles

- A **profile** = complete state: per-device-group effect + palette + params + brightness, ambilight mode + params, master brightness, power policy overrides.
- Shipped profiles/themes (all pre-mapped to the author's devices): **Rainbow** (current look), **Cyberpunk** (magenta/cyan/violet neon), **Aurora** (the extra theme), **Night** (dim warm, minimal), **Solid White** (utility). User can create, duplicate, rename, delete, import/export (JSON).
- A profile switches by: UI, tray menu, hotkey. No schedules, no app triggers in v1.
- Special profiles: **Logon profile** and **Lock profile** (see section 4), editable in the UI like any profile.

---

## 6. Ambilight

### 6.1 Modes

1. **Screen** (default): mirrors the monitor edge colors.
2. **Radial rainbow**: hue by angle around the screen center, rotating (also the fallback where capture is impossible).
3. **Solid**: one color (default white) at a configurable power %. A global hotkey toggles Solid on/off, returning to the previous mode.
4. **Off**.

Brightness slider 0-100% in the UI and the tray flyout. **100% = full diode power (255)**, nothing hidden. Brightness changes apply instantly with no reconnect.

### 6.2 Screen pipeline (Agent) [DECISION, parameters are settings]

Requirements already agreed:

- Capture with DXGI Desktop Duplication at **~20 FPS** (setting). The frame is **downscaled on the GPU** (e.g. to 480x270); only the small image is read back to the CPU, never a 4K frame.
- Sample region: a strip **2-3 cm** from each screen edge (setting in cm; convert with the monitor's physical size from EDID or manual input; at 4K and 31.5" that is roughly 110-165 px full-res, 15-20 px after downscale). Each LED averages its own segment; **neighbouring segments overlap slightly** so colors blend.
- Smoothness: capture 20 FPS, output to the strip at ~30 Hz with interpolation/smoothing (separate attack and release time constants, default fast attack and moderate release).
- **Black = off**: a pure black screen gives exactly (0,0,0), no minimum brightness. A black threshold zeroes near-black values (noise, dithering, banding in films) with a soft knee. Gamma correction makes dark scenes dark. **No trailing glow when fading to black** (when the target is black, decay to zero quickly and snap to 0 below a small epsilon).
- **Letterbox/pillarbox detection**: cheap detection of black bars on the downscaled frame (a few rows/columns), shifting the sampling region into the picture so the strip shows scene colors instead of black at top/bottom or sides. Toggle in settings, enabled by default; must have hysteresis to avoid flapping.
- **HDR**: the author mostly uses SDR, with HDR only sometimes in games. SDR path is primary. When the duplicated frame is FP16/scRGB, tone-map to the SDR range (use the SDR white level, simple robust tone map, clamp) so colors are not blown out. Detect format changes at runtime and rebuild the pipeline.
- DRM-protected content (browsers) produces black frames: documented limitation, no workaround. Exclusive-fullscreen games cannot be duplicated: recommend borderless; on `DXGI_ERROR_ACCESS_LOST` or persistent failure re-create the duplication with back-off and meanwhile signal the Service to show the fallback mode.
- Process only changed frames; skip work when nothing changed (smoothing still runs if converging). **Capture stops completely (resources released) when mode is not Screen, when the display is off, or when the session is locked.**
- Color processing order: average -> black threshold -> gamma -> saturation (optional, default 1.0) -> master/strip brightness -> current limiter (estimate = sum of all R, G and B values / 255 x 6.5 mA, from the measured 19.5 mA per LED at full white, i.e. 6.5 mA per channel) -> Adalight frame.
- **Performance target: ambilight Screen mode, Agent + Service + OpenRGB together under 1% of total CPU** measured with `tools/Measure-RGBcontroller.ps1`.

### 6.3 Strip layout

113 LEDs around the monitor. Layout is **not known** (author will use the wizard). Starting estimate: top ~40, left ~21, right ~21, bottom ~30, possible gap at the stand. The layout model: start corner, direction (clockwise/counterclockwise viewed from the front), LEDs per edge, optional gaps (unused LED indices), per-edge offset. See wizard in 7.5.

---

## 7. Features and settings

### 7.1 Tray and Agent

- Tray icon with state (ok / degraded / off). Menu: Open RGBcontroller, Profiles (submenu), Ambilight mode (submenu), strip brightness (a slider in the menu using a lightweight author-drawn WinForms `ToolStripControlHost`/TrackBar in dark style, created lazily; no WebView2 here), Turn everything off/on, Exit Agent.
- One Agent instance per user session (mutex). The Agent is allowed to be absent; nothing lighting-critical depends on it.
- Memory/CPU idle goal for the Agent: negligible (message loop only; capture pipeline disposed when idle).

### 7.2 Global hotkeys

- Fully mappable; each hotkey is a **combination of exactly two keys** (one modifier + one key, e.g. Ctrl+F9) registered with `RegisterHotKey` (no hooks, so no CPU cost). Conflicts are detected and reported.
- Mappable actions: Strip solid on/off (color and % configured in settings), strip brightness up/down (step configurable), cycle strip mode, next/previous profile, switch to a specific profile, everything off/on.
- Principle for all settings: **no setting may add background load**; anything that needs polling/watching is excluded.

### 7.3 Device and effect configuration

- Auto-discovered OpenRGB devices appear as **tiles**; the user may **group** zones/devices (e.g. "Pump + Fans", "RAM", "Keyboard", "Mouse + GPU") and assign effects per group.
- Per tile: on/off, effect, palette (gradient editor with stops), speed, direction, scale, brightness, FPS cap (advanced), "use hardware mode" (where available), layout/orientation (flip).
- Zones not claimed by any group stay off or follow a default group.
- Settings page: power policy (4), hotkeys, autostart state (Service/Agent), COM port and strip hardware, FPS caps, diagnostics (OpenRGB status, device list with detection time, COM status, log viewer, "restart OpenRGB", "rescan devices"), about/version.

### 7.4 Device mapping wizard (needed, author does not know the wiring)

For the motherboard addressable headers (3 x 20 LEDs): the wizard lights chains/LED ranges one at a time, the user marks which physical component lights up (pump ring, fan N, strip), defines LED counts, ring direction (CW/CCW), start offset, and component type (Ring or Strip). Radial effects use these definitions (hue by angle around each ring). Saved in config and editable later. Same wizard handles RAM orientation (which end is top) and keyboard orientation checks.

### 7.5 Strip layout wizard

Lights the first 3 LEDs green and the last 3 red (as in `Chase` mode of the test script), runs a travelling dot, lets the user mark the corners by clicking on a drawn monitor outline, sets direction, per-edge LED counts, gaps, edge sampling depth (cm), and shows a live preview of the sampled screen colors on a drawn strip around a screen thumbnail.

---

## 8. UI (on demand, WebView2) [DECISION on structure; visual taste is the author's: "very modern, dark, cyberpunk, tiles like FanControl, lots of personalization, no generic template look"]

- **Look**: dark cyberpunk. Hard-edged panels with clipped/angled corners, neon accents (cyan, magenta, acid yellow as selectable accent), mono-spaced numerics, subtle scanline/grid/noise textures, restrained glow only where it carries meaning (active LEDs, state). Avoid generic purple-gradient hero blobs, glassmorphism clichés, emoji icons, stock chat-bot aesthetics. Real visual hierarchy, dense but readable, keyboard accessible, responsive to window size. Custom window chrome is fine but must support Windows 10 and 11 (no Windows-11-only APIs required; Mica is optional progressive enhancement).
- **Dashboard of tiles** like FanControl: each tile shows a **live preview of what the device is currently displaying** (real LED colors streamed from the Service at ~20 Hz, only while the UI is open), plus its controls inline. Tiles are draggable, resizable/collapsible, and the layout is saved. Large Ambilight tile: screen thumbnail with the strip drawn around it, mode switcher, brightness slider, solid color/percent.
- Pages: Dashboard, Devices and Mapping, Ambilight (layout wizard, capture settings, preview), Profiles and Themes, Hotkeys, Settings and Diagnostics.
- Personalization: accent color, tile arrangement, density; effect parameters per tile (see 7.3).
- The preview subscription is the only extra load while the UI is open; the author accepts this.

---

## 9. Safety rules (non-negotiable)

1. Never write fan/pump control registers or anything unrelated to lighting.
2. No custom SMBus/I2C access; OpenRGB is the only path (it already uses PawnIO).
3. RAM (SMBus) gets a conservative default FPS and change-only writes; warn in the UI on raising it. Detection scans run once at start and after resume.
4. Strip: estimated current limiter (default 4.5 A), frame never larger than the link can carry, black frames on every stop path (crash handler where feasible, suspend, shutdown, service stop).
5. Service never runs arbitrary commands received over IPC; inputs validated; pipe ACL as in section 3.
6. All hardware-state-changing actions are logged. No telemetry, no network access except the loopback connection to the OpenRGB SDK.
7. Installer/uninstaller never deletes or modifies the author's existing OpenRGB config or autostart entries without explicit consent.

---

## 10. Risks to resolve by early spikes (stage 1-3)

| # | Risk | Spike and decision |
|---|---|---|
| R1 | OpenRGB running in Session 0 (service) might fail for some devices (NvAPI I2C for the GPU needs a display adapter context; HID usually works; SMBus via PawnIO should work as SYSTEM). | Run the pinned OpenRGB as LocalSystem from the Service, check which devices are detected and controllable. If the GPU (or anything else) fails: **stop and ask the author** between (a) GPU lit only after logon via a second OpenRGB in the user session handling only that device (with explicit non-duplication rules), (b) accept GPU static/hardware mode until logon. |
| R2 | OpenRGB 1.0rc2 SDK protocol vs OpenRGB.NET. | Compatibility spike; fallback own minimal client. |
| R3 | Rescan request availability. | Check protocol; fallback restart the supervised process. |
| R4 | Service receiving display on/off and lock events reliably, including on the logon screen. | Prototype with `RegisterServiceCtrlHandlerEx` + `GUID_CONSOLE_DISPLAY_STATE`; log events to a file during 10 cycles. |
| R5 | Time to first keyboard light after cold boot / restart / Fast Startup. | Measure and record; optimize start order (strip first, then OpenRGB). |
| R6 | CH340 behavior on resume (port disappears/reappears). | Reconnect with VID/PID discovery and the 0.8 s settle time. |
| R7 | Desktop Duplication across lock screen, UAC, fullscreen games, HDR. | Handle `ACCESS_LOST`; test each. |

---

## 11. Build stages with test gates

Every stage ends with a gate: unit tests and fakes (fake OpenRGB server, fake COM port) run first, then the stage is tested by hand on the real hardware against the pass/fail criteria below before the next stage starts.

**Stage 0: Foundation.** `git init`, solution layout, build scripts, logging, config with migrations, IPC contracts, CPU measurement script updated for new process names. Spikes R2, R3. Gate: builds, unit tests green, author reads the spike report.

**Stage 1: Service + OpenRGB supervisor + Adalight.** Windows Service, OpenRGB headless singleton with isolated config and no plugins, Adalight driver with discovery/reconnect/black-on-stop, simple static color for everything, installer-free dev scripts (`install-dev.ps1`). Spikes R1, R5, R6. Gate: after a **cold boot and restart** the keyboard and strip glow on the logon screen; kill OpenRGB and it comes back once; no second instance ever; measure time-to-first-light.

**Stage 2: Effect engine.** Device model with positions, wizard-less config, all effects, palettes, FPS caps, change-only writes, per-group brightness, profile model. Gate: CPU with the full Rainbow profile at 30 FPS (RAM 10-15, GPU 10) measured via `Measure-TrayTools.ps1`, 60 s idle. **Pass: total of Service + OpenRGB clearly below the old 2.6%, target below 1.0%; static profile about 0.0x%.** Visual check of each device's effect vs section 5.3.

**Stage 3: Power, session, display.** Spike R4. All of section 4. Gate: **10 sleep/resume cycles, 10 display-off/on cycles, 3 hibernate cycles, lock/unlock, shutdown** with: no blinking, no second OpenRGB, all LEDs back in correct state within a few seconds, black on suspend/shutdown.

**Stage 4: Agent, tray, hotkeys, ambilight.** Capture pipeline, strip layout (config-file first), Screen/Radial/Solid/Off, letterbox detection, SDR+HDR, hotkeys, tray flyout. Spike R7. Gate: black screen = strip fully off; dark scenes dark; no tail; smooth at 20/30; CPU of ambilight (Agent+Service+OpenRGB) **below 1%**; a game in borderless mode and an HDR case behave; hotkeys work after sleep.

**Stage 5: UI.** WebView2 shell, front end, tiles with live preview, mapping wizard, strip layout wizard, profiles/themes, hotkey editor, settings, diagnostics. Gate: author completes the mapping and strip wizards on real hardware, edits a palette, switches profiles; UI opened and closed repeatedly leaves no process behind and no CPU residue.

**Stage 6: Installer and hardening.** Pinned OpenRGB bundled, PawnIO check, autostart conflict detection with consent, uninstall cleanup, crash recovery tests, docs (README: install, usage, troubleshooting, hardware settings; ARCHITECTURE.md). Gate: clean install and uninstall on the author's PC; reboot; full checklist from stages 1-4 re-run once.

---

## 12. Definition of done (v1.0)

- Keyboard and strip light on the logon screen after every boot.
- No duplicate OpenRGB instances and no flicker across 10 sleep cycles.
- All six power/session rows in section 4 behave as specified, with configurable turn-off sets.
- Rainbow profile reproduces the author's current look (5.3) with synchronized motion; CPU below 1% total at 30 FPS; static profile approaches 0%.
- Ambilight: black means off, smooth, letterbox aware, under 1% CPU, solid-color hotkey works.
- UI: cyberpunk tiles with live previews, wizards, profiles, hotkeys, diagnostics; not resident.
- Hardware is configurable through settings (COM port, LED count, layout, device mapping, FPS caps), not code.
- Everything in English; clean installer and uninstaller; README and ARCHITECTURE docs.

---

## 13. Git workflow

- Commits are small and logical, with English messages in Conventional Commits style (`feat:`, `fix:`, `docs:`, `test:`, `build:`, `chore:`), one concern per commit.
- One short-lived branch per stage (`stage-1-service`, ...), merged into `main` through a pull request after the stage gate passes, then tagged (`v0.1.0-stage1`, ...).
- Never committed: secrets, personal files, build artifacts, installer binaries, local hardware evidence.

---

## 14. Open items (author will fill in later or via wizards)

- Strip layout (wizard).
- Which addressable header holds the pump and which hold fans, LED counts per component (wizard).
- RAM orientation of the LED order (wizard).
- Not part of this app, but recommended: update the BIOS to a microcode of **0x12B or newer** and use the Intel Default Settings profile (i9-14900K degradation issue).
