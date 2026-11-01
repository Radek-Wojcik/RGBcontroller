# Stage 3: power, session and display (spike R4)

Date: 2026-11-01. Branch `stage-3-power`, build installed with `install-dev.ps1` on the owner's PC. All results
below come from the Service log (`C:\ProgramData\RGBcontroller\logs\service-20261101.log`) and
`tools\Measure-TimeToFirstLight.ps1`.

## R4: how the Service learns about sessions and the display

| Signal | Mechanism | Result |
|---|---|---|
| Lock, unlock, logon, logoff | `ServiceBase.CanHandleSessionChangeEvent` + `OnSessionChange` | Works in Session 0, including on the logon screen. .NET delivers the events on thread-pool threads, so their order is not guaranteed. |
| Session state | `WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation(WTSSessionInfoEx)` | Re-read on every trigger and once more 2 s later; empty user name = logon screen, `SessionFlags` = lock state. |
| Display on/off/dimmed | `PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_CALLBACK)` | Works in Session 0 and in console mode; Windows calls back once at registration with the current state. |

Why not the service status handle (SPEC 4 suggestion): `ServiceBase` handles `SERVICE_CONTROL_POWEREVENT`, but
it queues `OnPowerEvent` on the thread pool and drops the `POWERBROADCAST_SETTING` payload, so the display state
would be unreadable. The callback route needs neither a window nor the status handle.

Observed: locking with Win+L turns the display off after about 60-80 s (Windows lock-screen timeout). Logon is
followed by a `SessionLock` about 0.3 s later and a `SessionUnlock` 13-28 s later on every boot (cause not
investigated); the lock profile is shown for that time, which is invisible while it equals the active profile.

## Behaviour (SPEC 4)

- `SessionPolicy` (Core) picks the profile: logon screen = `lighting.logonProfile`, locked =
  `lighting.lockProfile` (null = active), unlocked = active.
- While the display is off, `lighting.displayOff.unlocked` (default: strip) or `lighting.displayOff.locked`
  (default: strip, keyboard, mouse; also used on the logon screen) is forced black. Turned-off devices stay in
  their per-LED mode.
- Suspend blackout (stage 2) still wins over everything.

## Problems found on the hardware and fixed

1. **F-key row stayed lit after the keyboard turned off.** The engine sent the black frame once; OpenRGB pushes
   the Razer keyboard in several USB packets (row by row) and one was lost. Static device frames are now sent three
   times (0, 250, 500 ms). Owner confirmed: keyboard fully dark.
2. **One RAM stick (slot 2 of 4) dark after a cold boot.** OpenRGB detected all 4 slots and colors were sent, but
   the stick ignored them; a rescan (which re-sends `UPDATEMODE`) brought it back, so the Direct mode write over
   SMBus had been lost. Modes and frames are now re-sent 5 s and 30 s after every attach (start, resume, rescan).
   Owner confirmed after the next cold boot: all 4 sticks lit.
3. **O4 resume debounce.** After resume the Service now waits (up to 10 s) until all devices known before the
   sleep are back; resumes since then lit all 5 devices in one apply (`Only N of M` never logged).

## Measurements

- Boot (3 cold boots / restarts): Service at +12.1-12.5 s, strip +1.0-1.1 s later, all OpenRGB devices
  +5.1-5.6 s after Service start (17.2-18.0 s after boot). Same as stage 1.
- Since 16:00: 3 suspend/resume cycles, 6 display-off events, 20 session events, 6 Service starts (installs and
  boots), 0 OpenRGB restarts, 0 stray OpenRGB processes.

## Adalight strip outage during the tests (not a code issue)

- The CH340 disappeared from USB from 15:33 to 16:52 and dropped out several more times around unplugging the
  LED BOX (`Urządzenie dołączone do komputera nie działa`, `BaseStream is only available when the port is open`).
  The Service reconnected each time. A 10-minute watch without touching the box showed no dropout.
- AmbiBox had been started by hand and grabbed COM3 after a resume (`Access to the path 'COM3' is denied`). It was
  closed; its autostart remains disabled.
- Once, after power-cycling, COM3 accepted writes but the strip stayed dark; replugging or power-cycling the LED
  BOX resets its Arduino. Suspected cause: power/ground of the LED BOX or the USB cable (open item O10).

## Gate

Passed on 2026-11-01. The SPEC gate asks for 10 sleep/resume cycles and 3 hibernate cycles; the logs show 3 sleeps
and no hibernate. The owner reported "everything works" and closed the gate without the remaining cycles.
