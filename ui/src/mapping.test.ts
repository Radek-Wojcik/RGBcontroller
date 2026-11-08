import { describe, expect, it } from "vitest";
import {
  applyLayout, applyZoneSize, changeSegment, defaultShape, describeSegment, removeSegment, resizeSegment, resolveSegments,
  ringTop, segmentRange, splitLast, toLayout,
} from "./mapping";
import type { AppConfig, ZoneLayoutConfig } from "./types";
import fixture from "./mock-config.json";

const header = { resizable: true, ledCount: 20 };
const config = () => structuredClone(fixture) as unknown as AppConfig;
const layout = (...segments: ZoneLayoutConfig["segments"]): ZoneLayoutConfig => ({ device: "Board", zone: "Header 1", segments });
const seg = (leds: number | null, shape: "Ring" | "Line" | "Auto" = "Ring") => ({ leds, shape, vertical: false, reverse: false, offset: 0 });

describe("resolveSegments", () => {
  it("takes a zone without a layout for one component of the default shape", () => {
    expect(resolveSegments(undefined, header)).toEqual([{ leds: 20, shape: "Ring", vertical: false, reverse: false, offset: 0 }]);
    expect(resolveSegments(undefined, { resizable: false, ledCount: 5 })[0].shape).toBe("Line");
    expect(defaultShape({ resizable: false, ledCount: 1 })).toBe("Point");
  });

  it("spells out 'the rest' and adds the LEDs no segment claims", () => {
    expect(resolveSegments(layout(seg(8), seg(null)), header).map(s => s.leds)).toEqual([8, 12]);
    expect(resolveSegments(layout(seg(6, "Line")), header).map(s => [s.leds, s.shape])).toEqual([[6, "Line"], [14, "Ring"]]);
  });

  it("cuts segments that do not fit and ignores empty ones", () => {
    expect(resolveSegments(layout(seg(0), seg(15), seg(15)), header).map(s => s.leds)).toEqual([15, 5]);
  });
});

describe("editing segments", () => {
  const two = resolveSegments(layout(seg(8), seg(null)), header);

  it("moves LEDs between neighbours and keeps the total", () => {
    const wider = resizeSegment(two, 0, 11);
    expect(wider.map(s => s.leds)).toEqual([11, 9]);
    expect(resizeSegment(two, 0, 99).map(s => s.leds)).toEqual([19, 1]);
    expect(resizeSegment(two, 0, 0).map(s => s.leds)).toEqual([1, 19]);
    expect(resizeSegment(two, 1, 5)).toBe(two); // the last one is the rest
  });

  it("keeps a ring's top inside its smaller ring", () => {
    const turned = changeSegment(two, 0, { offset: 7 });
    expect(resizeSegment(turned, 0, 4)[0].offset).toBe(3);
  });

  it("splits the last segment and merges a removed one into its neighbour", () => {
    const three = splitLast(two);
    expect(three.map(s => s.leds)).toEqual([8, 6, 6]);
    expect(removeSegment(three, 2).map(s => s.leds)).toEqual([8, 12]); // into the previous one
    expect(removeSegment(three, 1).map(s => s.leds)).toEqual([14, 6]);
    expect(removeSegment(three, 0).map(s => s.leds)).toEqual([14, 6]); // the first goes into the next one
    expect(removeSegment(splitLast([{ ...two[0], leds: 1 }]), 0).map(s => s.leds)).toEqual([1]);
  });

  it("finds a segment's range and a ring's top LED", () => {
    const rings = changeSegment(resolveSegments(layout(seg(8), seg(null)), header), 1, { offset: 3 });
    expect(segmentRange(rings, 1)).toEqual({ start: 8, count: 12 });
    expect(ringTop(rings, 1)).toBe(11);
  });
});

describe("saving", () => {
  it("writes the last segment as the rest and drops settings the shape does not use", () => {
    const segments = changeSegment(changeSegment(resolveSegments(layout(seg(8), seg(null)), header), 0, { reverse: true, offset: 2, vertical: true }), 1, { shape: "Line", vertical: true });
    const saved = toLayout("Board", "Header 1", segments);

    expect(saved.segments).toEqual([
      { leds: 8, shape: "Ring", vertical: false, reverse: true, offset: 2 },
      { leds: null, shape: "Line", vertical: true, reverse: false, offset: 0 },
    ]);
  });

  it("replaces the layout of a zone and its LED count without touching the others", () => {
    const base = config();
    const board = base.lighting.layouts[0];
    const next = applyLayout(base, { ...board, segments: [seg(null, "Line")] });

    expect(next.lighting.layouts).toHaveLength(base.lighting.layouts.length);
    expect(next.lighting.layouts.at(-1)!.segments[0].shape).toBe("Line");
    expect(next.lighting.layouts.filter(l => l.zone === board.zone)).toHaveLength(1);
    expect(applyLayout(base, layout(seg(null))).lighting.layouts).toHaveLength(base.lighting.layouts.length + 1);

    const sized = applyZoneSize(base, base.openRgb.zoneSizes[0].device, base.openRgb.zoneSizes[0].zone.toUpperCase(), 30);
    expect(sized.openRgb.zoneSizes).toHaveLength(base.openRgb.zoneSizes.length);
    expect(sized.openRgb.zoneSizes.at(-1)!.leds).toBe(30);
    expect(base.openRgb.zoneSizes[0].leds).toBe(20); // the input is not changed
  });

  it("describes what a segment lights", () => {
    expect(describeSegment({ leds: 12, shape: "Ring", vertical: false, reverse: true, offset: 3 })).toBe("12 LED ring, counterclockwise, top = LED 4");
    expect(describeSegment({ leds: 5, shape: "Line", vertical: true, reverse: false, offset: 0 })).toBe("5 LED line, top to bottom");
  });
});
