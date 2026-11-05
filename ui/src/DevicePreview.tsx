import { useEffect, useState } from "react";
import { bridge } from "./bridge";
import { decodeColors } from "./strip";
import type { DeviceList, GroupConfig } from "./types";

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

interface Segment { key: string; label: string; width: number; rgb: Uint8Array }

/** The group's zones with their live LED colors; a keyboard matrix wraps at its width. */
export function GroupLeds({ group, devices, colors }: { group: GroupConfig; devices: DeviceList | null; colors: Map<number, Uint8Array> }) {
  const segments: Segment[] = [];
  for (const member of group.members) {
    const device = devices?.devices.find(d => d.name === member.device);
    const rgb = device && colors.get(device.index);
    if (!device || !rgb) continue;
    let offset = 0;
    for (const zone of device.zones) {
      if (member.zone === null || member.zone === zone.name) {
        segments.push({
          key: `${device.index}/${zone.name}`,
          label: zone.name,
          width: zone.type === 2 && zone.matrixWidth > 0 ? zone.matrixWidth : Math.min(zone.ledCount, 20),
          rgb: rgb.subarray(offset * 3, (offset + zone.ledCount) * 3),
        });
      }
      offset += zone.ledCount;
    }
  }

  if (segments.length === 0) return null;
  return (
    <div className="group-leds" aria-label={`Live colors of ${group.name}`}>
      {segments.map(s => (
        <div key={s.key} className="led-zone" title={s.label} style={{ gridTemplateColumns: `repeat(${s.width}, 1fr)` }}>
          {Array.from({ length: s.rgb.length / 3 }, (_, i) => {
            const [r, g, b] = [s.rgb[i * 3], s.rgb[i * 3 + 1], s.rgb[i * 3 + 2]];
            const on = r + g + b > 0;
            return <i key={i} style={on ? { background: `rgb(${r} ${g} ${b})`, boxShadow: `0 0 6px rgb(${r} ${g} ${b} / 0.6)` } : undefined} />;
          })}
        </div>
      ))}
    </div>
  );
}
