# Spike R2 + R3: OpenRGB 1.0rc2 SDK protocol, OpenRGB.NET, device rescan

Date: 2026-10-30. Stage 0. Method: source review of OpenRGB at the pinned commit
`0fca93e4544f943d4d7ec8073dba4e47c18ef54b` (1.0rc2, 2025-09-14; files `NetworkProtocol.h`,
`NetworkServer.cpp`, `NetworkClient.cpp`, `RGBController/RGBController.cpp`, `ResourceManager.cpp`, `cli.cpp`,
`Documentation/OpenRGBSDK.md`) and of OpenRGB.NET 3.1.1 (GitHub `diogotr7/OpenRGB.NET`, NuGet).
Then an own client was written and tested against an in-process fake server that follows that source.

**Not yet verified on the live server.** The owner's running OpenRGB (Session 0, with the Effects plugin) has no
SDK server open (port 6742 closed), and a second OpenRGB instance must never be started. The live check is the
first step of stage 1, against the instance the Service supervises (see "Open for stage 1").

## Results

| # | Question | Answer |
|---|---|---|
| R2 | Protocol of 1.0rc2 | `OPENRGB_SDK_PROTOCOL_VERSION 5` (NetworkProtocol.h:25). 4 = segments + plugins (0.9), 5 = zone flags, controller flags, LED alt names, effects-only resizable zones (1.0). |
| R2 | OpenRGB.NET compatible? | **Partly.** 3.1.1 (2024-07-28, dormant since, net8.0, MIT) caps at protocol 4 (`MaxProtocolNumber = 4`). The server caps per client, so a v4 client gets v4 data. **Gaps:** no rescan request, `DeviceListUpdated` event effectively broken (open PR #34), not thread-safe (replies matched only by packet type), no reconnect, `Receive` waits without timeout (hangs if OpenRGB dies), effects-only zones reported as 1/1/1 at v4. |
| R2 | **Decision** | **Own minimal SDK client** (the fallback SPEC section 3 allows). Implemented in `src/RGBcontroller.Core/OpenRgb/`: protocol 5 with min-4 negotiation, controller data parser (exact-length check), `UpdateLeds` without allocations, `SetCustomMode`, rescan, request timeouts, disconnect detection. OpenRGB.NET is not referenced. |
| R3 | Rescan request exists? | **Yes.** `NET_PACKET_ID_REQUEST_RESCAN_DEVICES = 140`, no payload, no reply, **not gated** on the client version server-side (NetworkServer.cpp:671). |
| R3 | What it does | `ResourceManager::DetectDevices()`: `Cleanup()` deletes every hardware controller and I2C bus object, then detection runs **asynchronously** in a background thread. A second rescan while one runs is ignored with a log warning. |
| R3 | How the client learns the result | Header-only `DEVICE_LIST_UPDATED` (100) pushed to all clients: once after cleanup (empty list), then once per registered controller. **Device indices are invalid until the list settles.** |
| R3 | **Decision** | Rescan inside the single instance via packet 140 (no process restart needed). Fallback (restart the supervised process) stays in the supervisor for the case where the rescan does not bring back the expected devices. |

## Protocol facts the implementation depends on

- Header: 16 bytes, `"ORGB"`, `dev_idx u32`, `pkt_id u32`, `pkt_size u32` (payload only), little-endian.
- Negotiation: the server replies with **its own** version (5) and stores min(client, 5) per client; the client uses min(server, client). `REQUEST_CONTROLLER_DATA` uses the 4-byte version in each request, not the negotiated one, so the client always sends it.
- Strings: `u16 length` (counts the NUL) + bytes + NUL.
- Colors: `u32` `0x00BBGGRR`, bytes R, G, B, 0.
- `UPDATELEDS` (1050): `u32 data_size` (= whole payload) + `u16 count` + colors. **If `data_size` != header size the server closes the connection.** If count > device LEDs, the write is silently ignored.
- `UPDATEZONELEDS` (1051): count not checked against the zone; we must send exactly `leds_count`.
- `SETCUSTOMMODE` (1100): server picks the first mode named Direct, then Custom, then Static with per-LED colors.
- Idle clients are never dropped (no keepalive); a dead client is detected only on a socket error.

## Consequences for stage 1 (OpenRGB supervisor)

1. **Bind loopback explicitly**: the default `--server-host` is `0.0.0.0`. Start with
   `--server-host 127.0.0.1 --server-port 6742` (both imply `--server`).
2. **No plugin-disable switch exists in 1.0rc2's CLI.** The "no Effects plugin" guarantee therefore relies on
   the pinned copy in `{app}\openrgb\` having no `plugins\` folder plus the isolated `--config` folder. Stage 1
   must confirm in the OpenRGB log that no plugin is loaded (the owner's plugin lives in
   `C:\Program Files\OpenRGB\plugins\`, which our copy never uses).
3. Other flags confirmed in `cli.cpp`: `--noautoconnect`, `--nodetect`, `--config <path>`, `--localconfig`,
   `--gui`, `--startminimized`, `--loglevel`. Whether a CLI start without `--gui` stays headless is verified in stage 1.
4. Rescan policy (supervisor, not the client): one rescan at a time; after packet 140 ignore device indices until
   the list settles (no `DEVICE_LIST_UPDATED` for a quiet period and the expected devices are present, with a
   timeout), then re-read controllers and reapply the profile.
5. The client never sends a malformed size, so a client bug cannot make OpenRGB drop us silently; malformed
   controller data fails loudly (exact-length parse).

## Evidence (automated, stage 0)

`tests/RGBcontroller.Tests/OpenRgb/` runs the client against `FakeOpenRgbServer` (loopback TCP, same packet
handling and size check as the 1.0rc2 source). The tests cover: v5 negotiation and client name, a newer server
(v6) capped at 5, an older one (v3) rejected, the full controller description at v4 and v5 (126-key matrix keyboard,
segments, flags, alt names), truncated or padded or wrong-version data rejected, `UpdateLeds` byte layout and size
field, **zero allocations per `UpdateLeds` after warm-up**, rescan with three `DEVICE_LIST_UPDATED` notices, a
hung server timing out instead of hanging, and a dropped connection raising `Disconnected`.

## Live verification (stage 1, 2026-10-30, owner's PC)

Done against the supervised OpenRGB 1.0rc2 (running as LocalSystem) with `RGBcontroller.Service.exe --probe [--rescan]`:

- **R2 confirmed.** Negotiated protocol 5 (server 5). All 5 devices were parsed by the exact-length parser:
  - HyperX Predator RGB: 4 zones x 5 LEDs.
  - RTX 3080: 1 LED.
  - ASUS TUF H670: 3 onboard LEDs plus Aura Addressable 1-3, resizable 0-120.
  - Razer Ornata Chroma V2: 132 LEDs, 6x22 matrix.
  - G502: 2 LEDs.
- **R3 confirmed.** Packet 140 produced 11 `DEVICE_LIST_UPDATED` notices, the last one 1.1 s after the request. The same 5 devices came back. The Service re-applied lighting by itself in the single OpenRGB instance (same pid).
- **Zone sizes do not survive a rescan.** The ARGB zones come back at 0 LEDs, so the Service re-applies the configured sizes after every device list change (already implemented).
- **Loopback only.** The SDK server listens on `127.0.0.1:6742` only.
- **No plugins.** The private config has no `Plugins` section, and the pinned copy has no `plugins` folder.
