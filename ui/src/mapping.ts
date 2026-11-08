import type { AppConfig, LedShape, SegmentLayoutConfig, ZoneInfo, ZoneLayoutConfig } from "./types";

// Pure logic of the mapping wizard (SPEC 7.4). A zone's LEDs are split into segments, one per physical component
// (pump ring, fan ring, RAM stick). The wizard works on explicit LED counts and writes the last segment as "the
// rest" (leds: null), the way the Service's LedLayoutBuilder reads it.

export type SegmentShape = "Line" | "Ring" | "Point";

export interface Segment {
  leds: number;
  shape: SegmentShape;
  /** Line: runs top to bottom instead of left to right. */
  vertical: boolean;
  /** Line: reversed; ring: counterclockwise. */
  reverse: boolean;
  /** Ring: index (inside the segment) of the LED at 12 o'clock. */
  offset: number;
}

/** What a zone is taken for before the owner says otherwise: headers are rings, one LED a point, the rest lines. */
export function defaultShape(zone: Pick<ZoneInfo, "resizable" | "ledCount">): SegmentShape {
  return zone.ledCount === 1 ? "Point" : zone.resizable ? "Ring" : "Line";
}

function plain(leds: number, shape: SegmentShape): Segment {
  return { leds, shape, vertical: false, reverse: false, offset: 0 };
}

function shapeOf(shape: LedShape, leds: number, fallback: SegmentShape): SegmentShape {
  if (shape === "Ring" || shape === "Line" || shape === "Point") return shape;
  return leds === 1 ? "Point" : fallback;
}

/** The zone's segments with every LED count spelled out; LEDs no segment claims form one more segment at the end. */
export function resolveSegments(layout: ZoneLayoutConfig | undefined, zone: Pick<ZoneInfo, "resizable" | "ledCount">): Segment[] {
  const fallback = defaultShape(zone);
  const result: Segment[] = [];
  let remaining = zone.ledCount;
  for (const s of layout?.segments ?? []) {
    const leds = Math.min(s.leds ?? remaining, remaining);
    if (leds <= 0) continue;
    result.push({
      leds,
      shape: shapeOf(s.shape, leds, fallback),
      vertical: s.vertical,
      reverse: s.reverse,
      offset: Math.max(0, s.offset),
    });
    remaining -= leds;
  }

  if (remaining > 0 || result.length === 0) result.push(plain(Math.max(remaining, 0), fallback));
  return result;
}

/** First LED and LED count of the segment at `index` inside the zone. */
export function segmentRange(segments: Segment[], index: number): { start: number; count: number } {
  let start = 0;
  for (let i = 0; i < index; i++) start += segments[i].leds;
  return { start, count: segments[index]?.leds ?? 0 };
}

/** The ring's top LED inside the zone: the segment's start plus its offset. */
export function ringTop(segments: Segment[], index: number): number {
  const s = segments[index];
  return segmentRange(segments, index).start + (s.leds > 0 ? Math.min(s.offset, s.leds - 1) : 0);
}

/** Gives the segment at `index` `leds` LEDs, taking them from (or handing them to) the next segment. */
export function resizeSegment(segments: Segment[], index: number, leds: number): Segment[] {
  const next = segments[index + 1];
  if (!next) return segments; // the last segment is "the rest"
  const total = segments[index].leds + next.leds;
  const wanted = Math.min(Math.max(Math.round(leds), 1), total - 1);
  return segments.map((s, i) => {
    if (i === index) return clampOffset({ ...s, leds: wanted });
    if (i === index + 1) return clampOffset({ ...s, leds: total - wanted });
    return s;
  });
}

/** Splits the last segment in two halves (the second keeps its settings), for a header that carries two components. */
export function splitLast(segments: Segment[]): Segment[] {
  const last = segments[segments.length - 1];
  if (!last || last.leds < 2) return segments;
  const second = Math.floor(last.leds / 2);
  return [
    ...segments.slice(0, -1),
    clampOffset({ ...last, leds: last.leds - second }),
    clampOffset({ ...last, leds: second, offset: 0 }),
  ];
}

/** Removes a segment; its LEDs go to the previous one, or to the next when it is the first. */
export function removeSegment(segments: Segment[], index: number): Segment[] {
  if (segments.length < 2) return segments;
  const target = index === 0 ? 1 : index - 1;
  return segments
    .map((s, i) => (i === target ? clampOffset({ ...s, leds: s.leds + segments[index].leds }) : s))
    .filter((_, i) => i !== index);
}

export function changeSegment(segments: Segment[], index: number, change: Partial<Segment>): Segment[] {
  return segments.map((s, i) => (i === index ? clampOffset({ ...s, ...change }) : s));
}

function clampOffset(s: Segment): Segment {
  return { ...s, offset: s.leds > 0 ? Math.min(Math.max(0, s.offset), s.leds - 1) : 0 };
}

/** The config form: the last segment takes "the rest", so a later resize of the header changes only that component. */
export function toLayout(device: string, zone: string, segments: Segment[]): ZoneLayoutConfig {
  return {
    device,
    zone,
    segments: segments.map((s, i): SegmentLayoutConfig => ({
      leds: i === segments.length - 1 ? null : s.leds,
      shape: s.shape,
      vertical: s.shape === "Line" && s.vertical,
      reverse: s.reverse,
      offset: s.shape === "Ring" ? s.offset : 0,
    })),
  };
}

const sameName = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

export function findLayout(config: AppConfig, device: string, zone: string): ZoneLayoutConfig | undefined {
  return config.lighting.layouts.find(l => sameName(l.device, device) && sameName(l.zone, zone));
}

/** Replaces the layout of one zone (or adds it). */
export function applyLayout(config: AppConfig, layout: ZoneLayoutConfig): AppConfig {
  const rest = config.lighting.layouts.filter(l => !(sameName(l.device, layout.device) && sameName(l.zone, layout.zone)));
  return { ...config, lighting: { ...config.lighting, layouts: [...rest, layout] } };
}

/** LED count of a resizable header (applied by the Service after the next OpenRGB detection). */
export function applyZoneSize(config: AppConfig, device: string, zone: string, leds: number): AppConfig {
  const rest = config.openRgb.zoneSizes.filter(z => !(sameName(z.device, device) && sameName(z.zone, zone)));
  return { ...config, openRgb: { ...config.openRgb, zoneSizes: [...rest, { device, zone, leds }] } };
}

/** The strings that describe what a segment lights up, for the wizard's summary. */
export function describeSegment(s: Segment): string {
  if (s.shape === "Ring") return `${s.leds} LED ring, ${s.reverse ? "counterclockwise" : "clockwise"}, top = LED ${s.offset + 1}`;
  if (s.shape === "Point") return `${s.leds} LED point`;
  return `${s.leds} LED line, ${s.vertical ? (s.reverse ? "bottom to top" : "top to bottom") : s.reverse ? "right to left" : "left to right"}`;
}
