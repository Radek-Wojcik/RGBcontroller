import { describe, expect, it } from "vitest";
import { decodeColors, nearestSlot, placeStrip } from "./strip";
import { paletteCss, setTurnOff, updateGroupEffect } from "./config";
import type { AppConfig, StripLayoutConfig } from "./types";
import fixture from "./mock-config.json";

const layout = (change: Partial<StripLayoutConfig>): StripLayoutConfig => ({
  startCorner: "TopLeft", clockwise: true, top: 1, right: 1, bottom: 1, left: 1, offset: 0, gaps: [], ...change,
});

describe("placeStrip", () => {
  it("runs clockwise from the top-left corner", () => {
    expect(placeStrip(layout({}), 4).map(l => l?.edge)).toEqual(["Top", "Right", "Bottom", "Left"]);
  });

  it("runs counterclockwise from the top-right corner along the top first", () => {
    const leds = placeStrip(layout({ startCorner: "TopRight", clockwise: false, top: 2 }), 5);
    expect(leds.map(l => l?.edge)).toEqual(["Top", "Top", "Left", "Bottom", "Right"]);
    expect(leds[0]!.x).toBeCloseTo(0.75);
  });

  it("skips gaps and wraps LEDs past the path around the loop", () => {
    const leds = placeStrip(layout({ gaps: [1] }), 6);
    expect(leds[1]).toBeNull();
    expect(leds.map(l => l?.edge ?? null)).toEqual(["Top", null, "Right", "Bottom", "Left", "Top"]);
  });

  it("matches the owner's layout: 133 LEDs starting 9 up the left edge", () => {
    const leds = placeStrip(layout({ startCorner: "BottomLeft", top: 41, right: 23, bottom: 43, left: 23, offset: 9 }), 133);
    expect(leds[0]!.edge).toBe("Left");
    expect(leds[0]!.y).toBeCloseTo(1 - 9.5 / 23);
    expect(leds[132]!.edge).toBe("Left");
  });
});

describe("first LED picking", () => {
  it("returns the offset that puts LED 0 where the user clicked", () => {
    const config = layout({ startCorner: "BottomLeft", top: 41, right: 23, bottom: 43, left: 23 });
    const offset = nearestSlot(config, 0, 1 - 9.5 / 23);
    expect(offset).toBe(9);
    const leds = placeStrip({ ...config, offset }, 133);
    expect(leds[0]!.edge).toBe("Left");
    expect(leds[0]!.y).toBeCloseTo(1 - 9.5 / 23);
  });

  it("decodes the Service's base64 colors", () => {
    expect([...decodeColors(btoa(String.fromCharCode(255, 0, 128)))]).toEqual([255, 0, 128]);
  });
});

describe("config edits", () => {
  const config = fixture as unknown as AppConfig;

  it("changes one group's effect without touching the others", () => {
    const next = updateGroupEffect(config, "Rainbow", "Keyboard", { effect: "Aurora" });
    const before = config.lighting.profiles.find(p => p.name === "Rainbow")!;
    const after = next.lighting.profiles.find(p => p.name === "Rainbow")!;
    expect(after.groups.find(g => g.group === "Keyboard")!.effect).toBe("Aurora");
    expect(after.groups.filter(g => g.group !== "Keyboard")).toEqual(before.groups.filter(g => g.group !== "Keyboard"));
    expect(config.lighting.profiles.find(p => p.name === "Rainbow")!.groups.find(g => g.group === "Keyboard")!.effect).not.toBe("Aurora");
  });

  it("toggles devices and the strip in a turn-off set", () => {
    const on = setTurnOff(config, "unlocked", "G502 HERO Gaming Mouse", true);
    expect(on.lighting.displayOff.unlocked.devices).toContain("G502 HERO Gaming Mouse");
    expect(setTurnOff(on, "unlocked", "g502 hero gaming mouse", false).lighting.displayOff.unlocked.devices).not.toContain("G502 HERO Gaming Mouse");
    expect(setTurnOff(config, "locked", null, false).lighting.displayOff.locked.strip).toBe(false);
  });

  it("draws palettes as cyclic gradients", () => {
    expect(paletteCss(config.lighting.palettes, "#FF0000")).toBe("#FF0000");
    expect(paletteCss(config.lighting.palettes, "Rainbow")).toMatch(/^linear-gradient\(90deg, .* 100%\)$/);
  });
});

describe("bridge", () => {
  it("replays the latest connection and state to a late subscriber", async () => {
    const { Bridge } = await import("./bridge");
    let deliver: (m: string) => void = () => {};
    const bridge = new Bridge({ post: () => {}, onMessage: l => (deliver = l) });
    deliver(JSON.stringify({ event: "connection", data: { connected: true, version: "1", error: null } }));
    const seen: string[] = [];
    bridge.subscribe(e => seen.push(e.event));
    expect(seen).toEqual(["connection"]);
  });
});
