# Spikes R1, R5, R6 (stage 1)

Date: 2026-10-30. Setup: `install-dev.ps1` on the owner's PC: the Service runs as LocalSystem and starts the
pinned OpenRGB 1.0rc2 in Session 0. The old `OpenRGB_Admin` task is disabled. Fast Startup is **off** on this PC
(`HiberbootEnabled = 0`), so "Shut down" is a cold boot.

## R1: OpenRGB in Session 0 as LocalSystem

**Detection: all 5 devices**, the same set as the owner's own OpenRGB:

| Device | Bus | LEDs |
|---|---|---|
| HyperX Predator RGB | SMBus i801 (PawnIO) | 20 |
| Gigabyte RTX 3080 12G Gaming OC | **NvAPI I2C** | 1 |
| ASUS TUF GAMING H670-PRO WIFI D4 | USB HID | 63 (3 onboard + 3 x 20 ARGB) |
| Razer Ornata Chroma V2 | USB HID | 132 |
| Logitech G502 HERO | USB HID (receiver) | 2 |

Applying the static color with Direct mode was accepted for all 5 devices (`Applied #00E5FF to 5 OpenRGB devices`).
**Confirmed by the owner: every device, the GPU included, shows the color.** Decision R1 stands; no second
OpenRGB is needed for the GPU.

Kill test (`taskkill /F /IM OpenRGB.exe` at 21:12:09):
- The Service saw the SDK connection drop and restarted OpenRGB after 1 s.
- It reconnected 1.5 s later and re-applied all 5 devices.
- Exactly one OpenRGB was running afterwards (new pid 32660, Session 0).

## R5: time to first light

Measured during the dev install (Service started on a running system):

| Event | After Service start |
|---|---|
| Adalight strip lit (COM3 found, 0.8 s settle, startup burst) | 1.08 s |
| OpenRGB SDK connected | 1.59 s |
| All 5 OpenRGB devices lit, keyboard included | 1.60 s |

**Pending:** cold boot and restart, measured with `tools\Measure-TimeToFirstLight.ps1`, which reports seconds after
boot from the System event log.

## R6: CH340 port loss

The strip writer reconnects with back-off (0.5, 1, 2, 5 s), finds the port again by VID/PID, waits 0.8 s and
re-sends. This is covered by `StripWriterTests.PortLoss_ReconnectsAndKeepsColors`.

**Real PC (LED BOX USB unplugged and plugged back):**
- 21:12:38: the write failed and the writer started searching.
- 21:12:47: COM3 was present again.
- 21:12:48: connected and re-sent.

**Observed hardware behaviour:**
- The board has its own 5 V supply and **keeps showing its last frame while USB is unplugged**, so the
  re-connect is not visible.
- After a **power cycle** of the LED BOX the color came back by itself, thanks to the 1 s keep-alive frame.

Sleep and resume follow in stage 3.

## CPU (static color, 20 s sample, `tools\Measure-RGBcontroller.ps1`)

Average CPU of the whole processor: OpenRGB **0.000%** and RGBcontroller.Service **0.000%**, for a total of
**0.000%**, with 1 instance each. The previous setup used 2.58% (OpenRGB with the Effects plugin, measured the same
day with the same tool).
