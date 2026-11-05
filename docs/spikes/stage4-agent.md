# Stage 4 spikes: Agent, screen capture (R7)

Date: 2026-11-01. Machine: the owner's PC (SPEC section 2), AOC AG326UD at 3840x2160, SDR desktop.

## R7: DXGI Desktop Duplication

Tool: `RGBcontroller.Agent.exe --capture-probe <seconds> <folder>` runs the whole Screen pipeline without the
Service and writes `capture-probe.txt` (counters, CPU, last strip colors) and `capture-probe.png` (the downscaled
frame with every LED's sampled band outlined).

| Check | Result |
|---|---|
| Duplication from the user session | Works on `\\.\DISPLAY1`, B8G8R8A8 (SDR). |
| GPU downscale | Mip chain on the GPU, 3840x2160 read back as 480x270 (mip 3). |
| Screen width from EDID | 69.9 cm (registry EDID, detailed timing descriptor in mm). |
| Capture rate | 18-19 frames/s processed at the 20 FPS cap while the desktop changes. |
| CPU, Release build, desktop changing continuously | 0.049% of the whole CPU (Agent, 20 s including start-up). |

### Findings that changed the code

1. **Vortice 3.8.3 `IDXGIOutput5.DuplicateOutput1` crashes the process** with an access violation inside the
   generated wrapper. `DesktopDuplicator` calls the method through the COM vtable (slot 26) instead.
2. **A blocking `ID3D11DeviceContext.Map` spins in the driver**: about 2.1 ms of CPU per frame, 0.16-0.19% of the
   whole CPU for the Agent alone (more than everything else in the pipeline together, 0.13 ms per frame). The
   staging texture is now mapped with `D3D11_MAP_FLAG_DO_NOT_WAIT` after a `Flush`, with 0.5 ms sleeps on a
   high-resolution waitable timer between attempts: CPU dropped about 4x (0.18% to 0.05%), the extra latency is
   about 3 ms.
3. `AcquireNextFrame` with a timeout is the idle wait: with a still desktop and a settled strip the capture thread
   blocks there (200 ms slices) and costs nothing.

### Still to be checked on the hardware (gate)

- HDR desktop (FP16 scRGB through `DuplicateOutput1`, tone-mapped with the Windows SDR white level).
- UAC prompt, lock screen, a borderless game and an exclusive-fullscreen game (`DXGI_ERROR_ACCESS_LOST`; the strip
  keeps its last colors for 1.5 s, then the Service shows the fallback until capture is back).
