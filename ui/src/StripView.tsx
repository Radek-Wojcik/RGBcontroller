import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { bridge } from "./bridge";
import { angleHue, decodeColors, placeStrip } from "./strip";
import type { AmbilightMode, StripLayoutConfig } from "./types";

const W = 480;
const H = 290;
const PAD = 26;
const POLL_MS = 100;

/**
 * Polls the colors the strip shows right now (10 times a second, only while a preview is on screen). Null until the
 * first answer or when the Service has no strip.
 */
export function useStripColors(enabled = true): Uint8Array | null {
  const [colors, setColors] = useState<Uint8Array | null>(null);
  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        const result = await bridge.call("getStripColors");
        if (!stopped) setColors(result.rgb ? decodeColors(result.rgb) : null);
      } catch {
        if (!stopped) setColors(null);
      }

      if (!stopped) timer = setTimeout(tick, POLL_MS);
    };
    void tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [enabled]);
  return colors;
}

/**
 * The monitor outline with every strip LED where the layout puts it, in the colors the strip shows right now
 * (<paramref name="live"/>); without live colors the mode is simulated. LED 0 (the data input) is ringed. With
 * <paramref name="onPick"/> a click on the outline reports the clicked point (0..1 over the screen).
 */
export function StripView({ layout, ledCount, mode, solidColor, brightness, speed, live, onPick }: {
  layout: StripLayoutConfig; ledCount: number; mode: AmbilightMode; solidColor: string; brightness: number; speed: number;
  live?: Uint8Array | null; onPick?(x: number, y: number): void;
}) {
  const leds = useMemo(() => placeStrip(layout, ledCount), [layout, ledCount]);
  const hasLive = !!live && live.length >= ledCount * 3;
  const turn = useTurn(!hasLive && mode === "Radial" ? speed : 0);
  const svg = useRef<SVGSVGElement>(null);
  const level = Math.max(0.08, brightness / 100);
  const sw = W - PAD * 2;
  const sh = H - PAD * 2;

  const color = (i: number, x: number, y: number): string | null => {
    if (hasLive) {
      const r = live![i * 3], g = live![i * 3 + 1], b = live![i * 3 + 2];
      return r + g + b === 0 ? null : `rgb(${r} ${g} ${b})`;
    }

    switch (mode) {
      case "Radial": return `hsl(${angleHue(x, y, turn).toFixed(0)} 100% ${(50 * level + 6).toFixed(0)}%)`;
      case "Solid": return solidColor;
      case "Screen": return `rgba(200, 220, 255, ${0.25 + 0.5 * level})`;
      default: return null;
    }
  };

  const pick = (e: MouseEvent<SVGSVGElement>) => {
    if (!onPick || !svg.current) return;
    const box = svg.current.getBoundingClientRect();
    const x = ((e.clientX - box.left) / box.width) * W;
    const y = ((e.clientY - box.top) / box.height) * H;
    onPick(Math.min(1, Math.max(0, (x - PAD) / sw)), Math.min(1, Math.max(0, (y - PAD) / sh)));
  };

  return (
    <svg ref={svg} className={`stripview ${onPick ? "pickable" : ""}`} viewBox={`0 0 ${W} ${H}`} role="img"
      aria-label={`Strip of ${ledCount} LEDs, ${mode}`} onClick={pick}>
      <defs>
        <pattern id="scan" width="4" height="4" patternUnits="userSpaceOnUse">
          <rect width="4" height="1" fill="currentColor" opacity="0.05" />
        </pattern>
      </defs>
      <rect x={PAD} y={PAD} width={sw} height={sh} className="screen" />
      <rect x={PAD} y={PAD} width={sw} height={sh} fill="url(#scan)" />
      <foreignObject x={PAD} y={PAD} width={sw} height={sh}>
        <div className="screen-brand">
          <span className="mark" aria-hidden />
          <span className="name">RGB<b>CONTROLLER</b></span>
          <span className="mode">{mode === "Off" ? "strip off" : `${mode.toLowerCase()}${hasLive ? " · live" : ""}`}</span>
        </div>
      </foreignObject>
      {leds.map((led, i) => {
        if (!led) return null;
        const cx = PAD + led.x * sw + (led.edge === "Left" ? -11 : led.edge === "Right" ? 11 : 0);
        const cy = PAD + led.y * sh + (led.edge === "Top" ? -11 : led.edge === "Bottom" ? 11 : 0);
        const fill = color(i, led.x, led.y);
        return (
          <g key={i}>
            {fill && <circle cx={cx} cy={cy} r={4.5} fill={fill} opacity={0.35} className="glow" />}
            <circle cx={cx} cy={cy} r={2.1} fill={fill ?? "none"} className="led"><title>LED {i}</title></circle>
            {i === 0 && <circle cx={cx} cy={cy} r={6} className="led-start"><title>LED 0: the first LED (data input)</title></circle>}
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
