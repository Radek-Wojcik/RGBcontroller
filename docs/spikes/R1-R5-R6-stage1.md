# Spikes R1, R5, R6 (stage 1)

Dates: 2026-10-30/31.

Setup: `install-dev.ps1` on the owner's PC. The Service runs as LocalSystem and starts the pinned OpenRGB 1.0rc2
in Session 0. The old `OpenRGB_Admin` task is disabled. Fast Startup is **off** on this PC (`HiberbootEnabled = 0`),
so "Shut down" is a cold boot.

**Gate passed on 2026-10-31.** Keyboard and strip were lit on the logon screen; RAM and GPU showed the color after
boot; OpenRGB came back once after being killed; there was never a second instance.

## R1: OpenRGB in Session 0 as LocalSystem

**Detection: all 5 devices**, the same set as the owner's own OpenRGB:

| Device | Bus | LEDs |
|---|---|---|
| HyperX Predator RGB | SMBus i801 (PawnIO) | 20 |
| Gigabyte RTX 3080 12G Gaming OC | **NvAPI I2C** | 1 |
| ASUS TUF GAMING H670-PRO WIFI D4 | USB HID | 63 (3 onboard + 3 x 20 ARGB) |
| Razer Ornata Chroma V2 | USB HID | 132 |
| Logitech G502 HERO | USB HID (receiver) | 2 |

**Control: all 5 confirmed visually by the owner, GPU included.** Decision R1 stands; no second OpenRGB is needed
for the GPU.

**Lesson (bug found at the cold-boot gate):** `SETCUSTOMMODE` (1100) only sets `active_mode` inside OpenRGB.
`RGBController::SetCustomMode` never calls `DeviceUpdateMode`.
- Symptom: after a cold boot the RAM kept its power-on rainbow and the GPU stayed dark, although the colors were
  "applied".
- Why it worked at first: the very first install worked only because the old OpenRGB had left the devices in
  Direct mode.
- Fix: the Service sends `UPDATEMODE` (1101) with the Direct mode description. That runs `DeviceUpdateMode`, which
  writes the mode to the hardware.

**Kill test** (`taskkill /F /IM OpenRGB.exe`):
- The Service restarted OpenRGB after 1 s.
- It reconnected 1.5 s later and re-applied all 5 devices.
- Exactly one OpenRGB was running afterwards.

## R5: time to first light

| Event | Seconds after boot (final build, 2026-10-31 restart) | Seconds after Service start |
|---|---|---|
| Service start | 12.2 | 0 |
| Adalight strip lit | 13.2 | 1.0 |
| All 5 OpenRGB devices lit, keyboard included | 17.2 | 5.1 |

At boot, OpenRGB detection takes about 4 s (1.6 s on a running system). Everything is lit before the logon
screen appears.

**Black screen at boot: not caused by RGBcontroller.**
- The owner saw a few seconds of black screen before the logon screen.
- An A/B restart with the Service set to Manual (so neither the Service nor OpenRGB ran) showed **the same black
  screen**.
- No driver or Windows update was installed in that period.
- A 30 s "wait for uptime" before starting OpenRGB, added during the investigation, was therefore reverted to 0. It
  remains available as `openRgb.minimumUptimeSeconds`.
- Profile load timing (Winlogon) is unchanged compared with the boots before the install.

## R6: CH340 port loss

The strip writer reconnects with back-off (0.5, 1, 2, 5 s), finds the port again by VID/PID, waits 0.8 s and
re-sends (`StripWriterTests.PortLoss_ReconnectsAndKeepsColors`).

**Real PC (LED BOX USB unplugged and plugged back):**
- the write failed and the writer started searching;
- 9 s later COM3 was present again;
- 1 s after that it reconnected and re-sent.

**Observed hardware behaviour:**
- The board has its own 5 V supply and **keeps its last frame while USB is unplugged**.
- After a **power cycle** of the LED BOX the color came back thanks to the 1 s keep-alive frame.

Sleep and resume follow in stage 3.

## CPU (static color, `tools\Measure-RGBcontroller.ps1`, 20 s)

Average CPU of the whole processor: OpenRGB **0.000%** and RGBcontroller.Service **0.000%**, for a total of
**0.000%**, with 1 instance each. The previous setup used 2.58% (OpenRGB with the Effects plugin, measured the same
day with the same tool).

## Other findings

- **The ARGB zone sizes are lost on every rescan** (OpenRGB reports 0 LEDs). The Service re-applies the configured
  sizes after every device list change.
- **`install-dev.ps1` once stripped all ACEs from existing data files.** Cause: `icacls /T` with `(OI)(CI)` grants.
  Now the folder gets the ACL and its children are reset to inherit it.
