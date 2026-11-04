# Stage 5 (UI): plan and progress

Branch `stage-5-ui`, started 2026-11-04 from `stage-4-agent` at the owner's request while the stage 4 gate still has
open items (see `docs/HANDOFF.md`). SPEC sections 3, 7.3-7.5, 8 and 11 (stage 5) are the requirements.

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

## Next steps

1. **Node.js** (owner permission needed): portable Node LTS in `%LOCALAPPDATA%` (no admin, reversible, like the
   .NET SDK), then `ui/` with React + TypeScript + Vite, `npm run build` into `ui/dist`, and `build.ps1` building the
   front end when Node is present.
2. **Live preview** (SPEC 8): the Service streams the colors the engine sends (all devices plus the strip) at about
   20 Hz, only while a UI subscribed, over the frame pipe (SPEC 3: separate stream pipe). The frame pipe server
   becomes InOut; the UI sends a subscribe frame, the Service writes `Preview` frames; the UI host forwards them to
   the page. LED positions come with the device list so tiles can draw them.
3. **Identify API for the wizards** (SPEC 7.4, 7.5): light one zone or LED range of a device, or a travelling dot on
   the strip, for a limited time; the engine shows it on top of the profile like the existing strip test.
4. **Front end pages**: Dashboard (tiles with live preview and inline controls, drag and resize, layout saved),
   Devices and Mapping (wizard: chains, rings, direction, offset, RAM and keyboard orientation), Ambilight (strip
   layout wizard from O14, capture settings, preview), Profiles and Themes (palette editor with stops, duplicate,
   rename, import/export), Hotkeys (two-key editor with conflict report), Settings and Diagnostics (power turn-off
   sets and Lock profile from O12, COM port, FPS caps, status, log tail, restart OpenRGB, rescan).
5. **Gate**: the owner completes the mapping and strip wizards, edits a palette, switches profiles; opening and
   closing the UI repeatedly leaves no process (UI or msedgewebview2 children) and no CPU residue.
