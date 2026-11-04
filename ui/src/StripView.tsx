import { useEffect, useMemo, useState } from "react";
import { angleHue, placeStrip } from "./strip";
import type { AmbilightMode, StripLayoutConfig } from "./types";

const W = 480;
const H = 290;
const PAD = 26;

/**
 * The monitor outline with every strip LED where the layout puts it. Colors show what the mode does (Radial rotates
 * like the Service renders it, Solid shows the color); live screen colors arrive with the preview stream.
 * LED 0 (the data input) is ringed so the layout can be checked against the real strip.
 */
export function StripView({ layout, ledCount, mode, solidColor, brightness, speed }: {
  layout: StripLayoutConfig; ledCount: number; mode: AmbilightMode; solidColor: string; brightness: number; speed: number;
}) {
  const leds = useMemo(() => placeStrip(layout, ledCount), [layout, ledCount]);
  const turn = useTurn(mode === "Radial" ? speed : 0);
  const level = Math.max(0.08, brightness / 100);
  const sw = W - PAD * 2;
  const sh = H - PAD * 2;

  const color = (x: number, y: number): string => {
    switch (mode) {
      case "Radial": return `hsl(${angleHue(x, y, turn).toFixed(0)} 100% ${(50 * level + 6).toFixed(0)}%)`;
      case "Solid": return solidColor;
      case "Screen": return `rgba(200, 220, 255, ${0.25 + 0.5 * level})`;
      default: return "transparent";
    }
  };

  return (
    <svg className="stripview" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`Strip of ${ledCount} LEDs, ${mode}`}>
      <defs>
        <pattern id="scan" width="4" height="4" patternUnits="userSpaceOnUse">
          <rect width="4" height="1" fill="currentColor" opacity="0.05" />
        </pattern>
      </defs>
      <rect x={PAD} y={PAD} width={sw} height={sh} className="screen" />
      <rect x={PAD} y={PAD} width={sw} height={sh} fill="url(#scan)" />
      <text x={W / 2} y={H / 2} className="screen-label">{mode === "Off" ? "STRIP OFF" : mode.toUpperCase()}</text>
      {leds.map((led, i) => {
        if (!led) return null;
        const cx = PAD + led.x * sw + (led.edge === "Left" ? -11 : led.edge === "Right" ? 11 : 0);
        const cy = PAD + led.y * sh + (led.edge === "Top" ? -11 : led.edge === "Bottom" ? 11 : 0);
        const fill = color(led.x, led.y);
        return (
          <g key={i}>
            {mode !== "Off" && <circle cx={cx} cy={cy} r={4.5} fill={fill} opacity={0.35} className="glow" />}
            <circle cx={cx} cy={cy} r={2.1} fill={mode === "Off" ? "none" : fill} className="led" />
            {i === 0 && <circle cx={cx} cy={cy} r={6} className="led-start" />}
          </g>
        );
      })}
    </svg>
  );
}

/** Rotations elapsed at <code>speed</code> turns per second, updated at about 30 FPS while the speed is not 0. */
function useTurn(speed: number): number {
  const [turn, setTurn] = useState(0);
  useEffect(() => {
    if (speed === 0) return;
    const start = performance.now();
    let frame = 0;
    let last = 0;
    const tick = (now: number) => {
      if (now - last > 33) {
        last = now;
        setTurn(((now - start) / 1000) * speed);
      }
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, [speed]);
  return turn;
}
