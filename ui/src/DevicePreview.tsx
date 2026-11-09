import { useEffect, useState } from "react";
import { bridge } from "./bridge";
import { decodeColors } from "./strip";
import { findLayout, resolveSegments } from "./mapping";
import type { AppConfig, DeviceInfo, DeviceList, GroupConfig, ZoneInfo } from "./types";

const POLL_MS = 200;

/** Polls what every OpenRGB device shows right now (5 times a second, only while the dashboard is open). */
export function useDeviceColors(): Map<number, Uint8Array> {
  const [colors, setColors] = useState<Map<number, Uint8Array>>(new Map());
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        const list = await bridge.call("getDeviceColors");
        if (!stopped) setColors(new Map(list.map(d => [d.index, decodeColors(d.rgb)])));
      } catch {
        // The Service may be older or restarting; the tiles keep their palette bar.
      }

      if (!stopped) timer = setTimeout(tick, POLL_MS);
    };
    void tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);
  return colors;
}

type Rgb = Uint8Array;

function dotStyle(rgb: Rgb, led: number): React.CSSProperties | undefined {
  const [r, g, b] = [rgb[led * 3], rgb[led * 3 + 1], rgb[led * 3 + 2]];
  if (r + g + b === 0) return undefined;
  return { background: `rgb(${r} ${g} ${b})`, boxShadow: `0 0 6px rgb(${r} ${g} ${b} / 0.6)` };
}

const RING_BOX = 76;
const RING_RADIUS = 31;

/** A ring of LEDs drawn as a circle: LED 1 of the ring at 12 o'clock plus the saved offset, clockwise unless reversed. */
function Ring({ rgb, from, leds, offset, reverse }: { rgb: Rgb; from: number; leds: number; offset: number; reverse: boolean }) {
  return (
    <div className="led-ring" style={{ width: RING_BOX, height: RING_BOX }} title={`${leds} LED ring`}>
      {Array.from({ length: leds }, (_, k) => {
        const turn = (((reverse ? -(k - offset) : k - offset) % leds) + leds) % leds / leds;
        const angle = turn * 2 * Math.PI;
        const x = RING_BOX / 2 + RING_RADIUS * Math.sin(angle);
        const y = RING_BOX / 2 - RING_RADIUS * Math.cos(angle);
        return <i key={k} style={{ left: x, top: y, ...dotStyle(rgb, from + k) }} />;
      })}
    </div>
  );
}

/** One zone as it looks on the desk: a keyboard as keys, a ring as a circle, a stick or strip as a line. */
function ZoneView({ config, device, zone, rgb, offset }: { config: AppConfig; device: DeviceInfo; zone: ZoneInfo; rgb: Rgb; offset: number }) {
  if (zone.type === 2 && zone.matrixWidth > 0 && zone.matrix?.length) {
    return (
      <div className="led-keys" title={zone.name} style={{ gridTemplateColumns: `repeat(${zone.matrixWidth}, 1fr)` }}>
        {zone.matrix.map((led, cell) => led < 0 || led >= zone.ledCount
          ? <b key={cell} className="gap" />
          : <i key={cell} style={dotStyle(rgb, offset + led)} />)}
      </div>
    );
  }

  const parts = resolveSegments(findLayout(config, device.name, zone.name), zone);
  let from = offset;
  return (
    <div className="led-parts" title={zone.name}>
      {parts.map((s, n) => {
        const start = from;
        from += s.leds;
        if (s.shape === "Ring" && s.leds > 2) return <Ring key={n} rgb={rgb} from={start} leds={s.leds} offset={s.offset} reverse={s.reverse} />;
        const indices = Array.from({ length: s.leds }, (_, k) => start + (s.reverse ? s.leds - 1 - k : k));
        return (
          <div key={n} className={s.vertical ? "led-line vertical" : "led-line"} style={s.vertical ? undefined : { gridTemplateColumns: `repeat(${Math.min(s.leds, 20)}, 1fr)` }}>
            {indices.map(led => <i key={led} style={dotStyle(rgb, led)} />)}
          </div>
        );
      })}
    </div>
  );
}

/** The group's zones with their live LED colors, laid out like the hardware (keys, rings, lines). */
export function GroupLeds({ config, group, devices, colors }: { config: AppConfig; group: GroupConfig; devices: DeviceList | null; colors: Map<number, Uint8Array> }) {
  const zones: { key: string; device: DeviceInfo; zone: ZoneInfo; rgb: Rgb; offset: number }[] = [];
  for (const member of group.members) {
    const device = devices?.devices.find(d => d.name === member.device);
    const rgb = device && colors.get(device.index);
    if (!device || !rgb) continue;
    let offset = 0;
    for (const zone of device.zones) {
      if (member.zone === null || member.zone === zone.name) zones.push({ key: `${device.index}/${zone.name}`, device, zone, rgb, offset });
      offset += zone.ledCount;
    }
  }

  if (zones.length === 0) return null;
  const labelled = zones.length > 1 && !zones.every(z => z.zone.type === 2);
  return (
    <div className="group-leds" aria-label={`Live colors of ${group.name}`}>
      {zones.map(z => (
        <div key={z.key} className="led-zone">
          {labelled && <span className="led-zone-name">{z.zone.name}</span>}
          <ZoneView config={config} {...z} />
        </div>
      ))}
    </div>
  );
}
