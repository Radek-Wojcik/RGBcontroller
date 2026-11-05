import type { StripLayoutConfig } from "./types";

export type Edge = "Top" | "Right" | "Bottom" | "Left";

/** Where one strip LED sits: x, y in 0..1 over the screen outline (0,0 top-left), or null for a gap. */
export interface StripLed { edge: Edge; x: number; y: number }

/**
 * Port of src/RGBcontroller.Core/Ambilight/StripLayout.cs: the path starts at the start corner, runs clockwise or
 * counterclockwise over the four edges, gaps take no place on it, and LEDs past the end wrap around (overlap).
 * Keep the two in step; strip.test.ts pins the same cases as the C# tests.
 */
export function placeStrip(config: StripLayoutConfig, ledCount: number): (StripLed | null)[] {
  const path = buildPath(config);
  const gaps = new Set(config.gaps);
  const leds: (StripLed | null)[] = [];
  let k = 0;
  for (let led = 0; led < ledCount; led++) {
    if (gaps.has(led) || path.length === 0) {
      leds.push(null);
      if (!gaps.has(led)) k++;
      continue;
    }

    leds.push(path[(((k + config.offset) % path.length) + path.length) % path.length]);
    k++;
  }

  return leds;
}

/** The slots around the screen from the start corner in the strip's direction (top, right, bottom, left counts). */
export function buildPath(config: StripLayoutConfig): StripLed[] {
  const clockwiseOrder: Edge[] = ["Top", "Right", "Bottom", "Left"];
  const first = config.clockwise
    ? { TopLeft: 0, TopRight: 1, BottomRight: 2, BottomLeft: 3 }[config.startCorner]
    : { TopLeft: 3, BottomLeft: 2, BottomRight: 1, TopRight: 0 }[config.startCorner];
  const counts: Record<Edge, number> = { Top: config.top, Right: config.right, Bottom: config.bottom, Left: config.left };

  const path: StripLed[] = [];
  for (let step = 0; step < 4; step++) {
    const edge = config.clockwise ? clockwiseOrder[(first + step) % 4] : clockwiseOrder[(first - step + 4) % 4];
    const count = counts[edge];
    const forward = (edge === "Top" || edge === "Right") === config.clockwise;
    for (let j = 0; j < count; j++) {
      const t = (j + 0.5) / count;
      const along = forward ? t : 1 - t;
      path.push(
        edge === "Top" ? { edge, x: along, y: 0 }
          : edge === "Right" ? { edge, x: 1, y: along }
            : edge === "Bottom" ? { edge, x: along, y: 1 }
              : { edge, x: 0, y: along },
      );
    }
  }

  return path;
}

/** Slot on the path nearest to a point (0..1 over the screen); the offset that puts the first LED there. */
export function nearestSlot(config: StripLayoutConfig, x: number, y: number): number {
  const path = buildPath(config);
  let best = 0;
  let distance = Infinity;
  path.forEach((slot, i) => {
    const d = (slot.x - x) ** 2 + (slot.y - y) ** 2;
    if (d < distance) {
      distance = d;
      best = i;
    }
  });
  return best;
}

/** Strip colors from the Service: base64 R,G,B per LED. */
export function decodeColors(base64: string): Uint8Array {
  const text = atob(base64);
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i);
  return bytes;
}

/** Hue in degrees of a point around the screen center (the Radial mode look; 0 at the right, clockwise). */
export function angleHue(x: number, y: number, turn = 0): number {
  const degrees = (Math.atan2(y - 0.5, x - 0.5) * 180) / Math.PI;
  return (((degrees + turn * 360) % 360) + 360) % 360;
}
