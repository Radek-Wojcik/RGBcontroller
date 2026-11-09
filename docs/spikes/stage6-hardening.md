# Stage 6: installer and hardening results

Measured on the author's PC (Windows 10 22H2, i9 with 32 threads, the installed build 0.5.0) on 2026-11-09.

## Clean install, uninstall and reinstall

Silent setup with `/DisableConflicts=1` from `installer/RGBcontroller.iss`:

- Service `RGBcontroller` registered (LocalSystem, automatic start), failure actions restart after 1 s, 5 s and 30 s, reset
  period one day. The data folder `%ProgramData%\RGBcontroller` allows SYSTEM and Administrators full control and Users read.
- The tray Agent is in the user's logon autostart. The `OpenRGB_Admin` scheduled task and the `AmbiBox` Run value were disabled
  (not deleted) and recorded in `%ProgramData%\RGBcontroller\autostart-state.json`.
- Uninstall (silent): service, files, shortcuts and the Agent Run value removed; `OpenRGB_Admin` back to Ready, the `AmbiBox`
  Run value back with its original data; the configuration and logs stay (a question is asked in the interactive uninstaller).
- Reinstall and an in-place update from 0.4.0 to 0.5.0 worked; after a cold boot the keyboard and strip lit on the logon screen.

One observation: right after the first install RAM (HyperX, SMBus) was not detected, in that session and in the next two
OpenRGB starts; a PC restart fixed it, and later hard kills of OpenRGB and the Service did not reproduce it (below). The
old OpenRGB that `uninstall-dev.ps1` had just started was removed by the Service's stray cleanup a moment before OpenRGB's own
start, which is the suspected trigger. Not reproduced since; watch for it.

## Crash recovery

| Test | Result |
|---|---|
| Kill OpenRGB (`Stop-Process -Force`) | The Service logs the lost SDK connection, restarts OpenRGB after 1 s, exactly one OpenRGB runs again, all 5 devices are handed back and set to Direct. |
| Kill the Service | The Service Control Manager starts it again after 1 s. The old OpenRGB is gone with it, one new OpenRGB runs, the strip is first lit 1.1 s and the other devices 1.6 s after the Service start, 5 devices. |
| Requested OpenRGB restart (IPC 1.11) | Logged as "Restarting OpenRGB as requested", no failure backoff, one OpenRGB, 5 devices. |
| Unit tests | `OpenRgbExit_RestartsOnceAndRelights`, `HungSdk_IsRestarted`, `OpenRgbNeverAccepting_IsRestartedAfterStartupTimeout`, `Stop_SendsBlackThenKillsOpenRgb`, and for the new diagnostics `RequestRestart_*` and `RequestRescan_*`. |

## CPU

`tools\Measure-RGBcontroller.ps1`, 60 s, percent of the whole CPU (100% = 32 threads). The desktop was mostly still.

| Case | OpenRGB | Agent | Service | Total |
|---|---|---|---|---|
| Rainbow profile (RAM at 10 fps), strip in Screen mode | 1.709 | 0 | 0.004 | **1.714** |
| Solid White (static devices), strip in Screen mode with capture on | 0 | 0.055 | 0.006 | **0.060** |

The ambilight itself (Agent, Service and OpenRGB together) is far below the 1% target. In the Rainbow profile almost the whole
cost is OpenRGB writing the RAM over SMBus, as in `stage2-effects.md`. Not measured: a full-screen video or a game, where the
capture does more work.

## Preflight and installer checks that are tested

`tools\Test-InstallPreflight.ps1` (scratch locations, no real autostart entry touched): finding, disabling and restoring
OpenRGB and AmbiBox autostart entries; exit code 2 when PawnIO is missing, 3 when only WebView2 is missing, 2 when both.

Not tested: the installer's own dialogs (missing PawnIO or WebView2 message boxes, the interactive consent question, the
optional desktop shortcut task); they are Inno Setup code and need a manual run.
