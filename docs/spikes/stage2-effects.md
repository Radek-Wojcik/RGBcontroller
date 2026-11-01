# Stage 2: effect engine, CPU and resume

Dates: 2026-10-31 to 2026-11-01. Build installed with `install-dev.ps1` on the owner's PC.
CPU measured with `tools\Measure-RGBcontroller.ps1` (idle PC; percent of the whole 32-thread CPU).

## CPU

| Profile | OpenRGB | Service | Total |
|---|---|---|---|
| Rainbow, RAM 12 FPS (SPEC default) | 1.866% | 0.017% | 1.88% |
| Rainbow, RAM 5 FPS | 0.944% | 0.024% | **0.97%** |
| Rainbow, RAM 8 FPS (30 s) | 1.317% | 0.009% | 1.33% |
| Rainbow, RAM 10 FPS (30 s) | 1.654% | 0.018% | 1.67% |
| Solid White (static) | 0% | 0.012% | **0.012%** |
| Old setup: OpenRGB + Effects plugin, 60 FPS | | | 2.58% |

Where OpenRGB's time goes (per-thread times from `NtQuerySystemInformation`, Rainbow with RAM at 12 FPS, 20 s):
- One OpenRGB thread used **1.74%, almost all kernel time** (about 56% of one core), at 216 context switches/s.
- The next busiest used 0.10%; all others were near 0.
- That thread is the HyperX RAM device thread. Every RAM frame is dozens of SMBus writes through PawnIO, and
  OpenRGB waits for each one in the kernel: about 48 ms of CPU per frame, so roughly 0.16% total CPU per RAM FPS.
- Everything else together (keyboard and ARGB at 30 FPS, mouse 15, GPU 10, strip 30) costs about 0.13%; our
  Service about 0.02%.

**The RAM frame rate is the whole CPU budget.** It is a property of the SMBus path, not of our code (we must not
write SMBus ourselves, SPEC 9.2).

## RAM smoothness (owner feedback)

- 5 FPS with speed 0.3: "RAM lags a lot" (about 22 degrees of hue per frame).
- 10 FPS with speed 0.3: "still lags slightly" (about 11 degrees per frame). In addition, OpenRGB needs about
  48 ms to write one RAM frame, so the LEDs change one after another, not at once.
- Current tuning on the owner's PC: **10 FPS, speed 0.08, scale 0.3** (slower and a longer wave, about
  3 degrees per frame). Total CPU about 1.7%: above the 1% target, below the old 2.58%. The owner chose the
  mirror effect over hardware mode; the tuning stays adjustable (`--group RAM ...`, later the UI tiles).
- Code default for new configs: RAM group 5 FPS.

## Sleep and resume (pulled forward from stage 3)

Owner's bug: after sleep the RAM blinked (its power-on rainbow fought our frames) and the GPU stayed dark; with
the stage 1 build the keyboard and GPU were dark and the board and mouse showed their factory rainbow. Cause:
nothing reacted to resume, and the hardware comes back in its power-on state.

Fix: on `PBT_APMSUSPEND` the engine sends one black frame everywhere and idles. On resume:
1. The strip comes back at once.
2. OpenRGB devices are detached.
3. After 3 s (`openRgb.resumeRescanDelayMilliseconds`), the single OpenRGB instance rescans (packet 140).
4. Devices are re-read and modes and colors are pushed again.

Live result, sleep at 15:39:01:
- black was sent;
- resume came at 15:39:16 (ResumeAutomatic);
- all 5 devices were back at 15:39:23, 6.6 s after resume;
- one OpenRGB the whole time;
- the owner confirmed visually.

During the rescan the settle logic applied 0 and then 2 devices before all 5 (known item O4). It is harmless, but
the USB devices light about 2.5 s after the RAM and GPU.

Display off and lock are still stage 3.

## Other findings

- **PowerShell eats decimal commas**: `--speed 0,08` is the array (0, 8) and reached the Service as 0.8.
  - Always use a dot (`0.08`) in PowerShell.
  - The CLI accepts a comma only when the argument is quoted.
- **The Adalight CH340 drops off USB intermittently.**
  - It was absent from boot (21:15) until a resume the next afternoon, then reappeared.
  - It vanished again at 15:33.
  - When this happens, Windows lists no VID_1A86 device at all. The writer keeps searching and reconnects by
    itself, so this is a hardware or cable issue to check, not a code issue.
