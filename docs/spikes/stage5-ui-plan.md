# Stage 5 (UI): plan and progress

Branch `stage-5-ui`, started 2026-11-04 from `stage-4-agent` at the owner's request while the stage 4 gate still has
open items. SPEC sections 3, 7.3-7.5, 8 and 11 (stage 5) are the requirements.

## Architecture

```
RGBcontroller.UI.exe (WPF, on demand, one per session)
  MainWindow: WebView2, user data in %LOCALAPPDATA%\RGBcontroller\webview2
    https://ui.rgbcontroller.example/  ->  {app}\wwwroot (ui/dist), or {app}\fallback (bridge test page)
    navigation outside that origin and new windows are blocked; dev tools only in Debug builds
  Bridge: page JSON  {"id", "method", "params"}  ->  fixed method list  ->  IServiceControl (control pipe)
          replies    {"id", "result" | "error"};  events {"event": "connection" | "state", "data"}
  ServiceClient: hello as ClientKind.UI, subscribe, reconnect with back-off
```

The page can do nothing the IPC surface does not already allow; the Service validates every argument again.

## IPC 1.4 (done)

| Method | Purpose |
|---|---|
| `GetConfigAsync` | Whole `AppConfig` plus a revision. |
| `UpdateConfigAsync(expectedRevision, config)` | Optimistic concurrency (stale revision is refused), `ConfigValidator`, the OpenRGB executable path and schema version are kept from the running config (SPEC 9), applied and saved. Lighting, strip layout, capture and hotkeys apply live; other strip hardware, OpenRGB and logging report `RestartRequired`. |
| `GetDevicesAsync` | OpenRGB devices and zones (type, LED count, resizable, matrix size) and the strip LED count. |
| `AgentState.SettingsRevision`, `ConfigRevision` | The Agent re-reads its settings when the first changes; the UI reloads the config when the second changes. |

## Front end (done, first version)

Portable Node 24.22.2 in `%USERPROFILE%\.tools\nodejs`. `ui/` is React 19 + TypeScript 7 + Vite 8 with exact
versions; `build.ps1` and `install-dev.ps1` build it through `tools\Build-Frontend.ps1`. Pages:

| Page | Now | Missing |
|---|---|---|
| Dashboard | Ambilight tile (strip drawn in its layout, mode, brightness, solid color, fallback); one tile per group (effect, palette, speed, wave length, brightness, FPS cap with the RAM/GPU warning) | live LED preview, drag and resize of tiles |
| Devices | OpenRGB devices, zones, LED counts, owning group | mapping wizard |
| Ambilight | layout by numbers with the test pattern; response and picture settings | layout wizard (O14), screen thumbnail |
| Profiles | activate, master brightness, logon and lock profile | palette editor, duplicate, rename, delete, import/export |
| Hotkeys | record a combination, action, profile, duplicates flagged, brightness step | conflict report from the Agent |
| Settings | display-off turn-off sets (O12), strip port, LED count, current limit, FPS cap, diagnostics, accent | log tail, restart OpenRGB, rescan |

## Next steps

1. **Live preview** (SPEC 8): the Service streams the colors the engine sends (all devices plus the strip) at about
   20 Hz, only while a UI subscribed, over the frame pipe (SPEC 3: separate stream pipe). The frame pipe server
   becomes InOut; the UI sends a subscribe frame, the Service writes `Preview` frames; the UI host forwards them to
   the page. LED positions come with the device list so tiles can draw them.
2. **Identify API for the wizards** (SPEC 7.4, 7.5): light one zone or LED range of a device, or a travelling dot on
   the strip, for a limited time; the engine shows it on top of the profile like the existing strip test.
3. **The missing column above**, wizards first (they need 1 and 2).
4. **Gate**: the owner completes the mapping and strip wizards, edits a palette, switches profiles; opening and
   closing the UI repeatedly leaves no process (UI or msedgewebview2 children) and no CPU residue.
