import { describe, expect, it } from "vitest";
import {
  addPalette, deletePalette, deleteProfile, duplicateProfile, exportProfile, importProfile, paletteUses, renamePalette,
  renameProfile, uniqueName,
} from "./config";
import type { AppConfig } from "./types";
import fixture from "./mock-config.json";

const base = fixture as unknown as AppConfig;
const withHotkey: AppConfig = {
  ...base,
  lighting: { ...base.lighting, lockProfile: "Night" },
  hotkeys: { ...base.hotkeys, bindings: [{ keys: "Ctrl+Alt+N", action: "Profile", profile: "Night" }, { keys: "Ctrl+Alt+Right", action: "NextProfile", profile: null }] },
};

describe("profiles", () => {
  it("numbers names that are taken, ignoring case", () => {
    expect(uniqueName(["Night"], "night")).toBe("night 2");
    expect(uniqueName(["Night", "Night 2"], "Night")).toBe("Night 3");
    expect(uniqueName(["Night"], "Day")).toBe("Day");
  });

  it("duplicates a profile without sharing its objects", () => {
    const { config, name } = duplicateProfile(base, "Night");
    expect(name).toBe("Night copy");
    const copy = config.lighting.profiles.find(p => p.name === name)!;
    const original = config.lighting.profiles.find(p => p.name === "Night")!;
    expect(copy.groups).toEqual(original.groups);
    expect(copy.groups[0]).not.toBe(original.groups[0]);
  });

  it("renames a profile and everything that points to it", () => {
    const next = renameProfile(withHotkey, "Night", "Evening");
    expect(next.lighting.profiles.some(p => p.name === "Evening")).toBe(true);
    expect(next.lighting.profiles.some(p => p.name === "Night")).toBe(false);
    expect(next.lighting.lockProfile).toBe("Evening");
    expect(next.hotkeys.bindings[0].profile).toBe("Evening");
    expect(next.lighting.activeProfile).toBe(base.lighting.activeProfile);
  });

  it("deletes a profile, moves references and drops its hotkeys", () => {
    const next = deleteProfile({ ...withHotkey, lighting: { ...withHotkey.lighting, activeProfile: "Night" } }, "Night");
    expect(next.lighting.profiles.some(p => p.name === "Night")).toBe(false);
    expect(next.lighting.activeProfile).toBe(next.lighting.profiles[0].name);
    expect(next.lighting.lockProfile).toBeNull();
    expect(next.hotkeys.bindings.map(b => b.keys)).toEqual(["Ctrl+Alt+Right"]);
  });

  it("keeps the last profile", () => {
    const one = { ...base, lighting: { ...base.lighting, profiles: [base.lighting.profiles[0]], activeProfile: base.lighting.profiles[0].name } };
    expect(deleteProfile(one, one.lighting.profiles[0].name)).toBe(one);
  });

  it("round-trips a profile through export and import", () => {
    const file = JSON.parse(JSON.stringify(exportProfile(base, "Cyberpunk")));
    expect(file.palettes.map((p: { name: string }) => p.name)).toContain("Cyberpunk");
    const result = importProfile(base, file);
    expect(typeof result).not.toBe("string");
    if (typeof result === "string") return;
    expect(result.name).toBe("Cyberpunk 2");
    expect(result.config.lighting.palettes.length).toBe(base.lighting.palettes.length);
  });

  it("adds a palette that the importing setup lacks and drops unknown groups", () => {
    const file = JSON.parse(JSON.stringify(exportProfile(base, "Aurora")));
    file.profile.name = "Elsewhere";
    file.palettes = [{ name: "Brand new", stops: [{ position: 0, color: "#123456" }] }];
    file.profile.groups.push({ ...file.profile.groups[0], group: "Not here" });
    const result = importProfile(base, file);
    if (typeof result === "string") throw new Error(result);
    expect(result.config.lighting.palettes.map(p => p.name)).toContain("Brand new");
    expect(result.config.lighting.profiles.at(-1)!.groups.some(g => g.group === "Not here")).toBe(false);
  });

  it("refuses files that are not profiles", () => {
    expect(importProfile(base, { hello: 1 })).toMatch(/not an RGBcontroller profile/);
    expect(importProfile(base, null)).toMatch(/not an RGBcontroller profile/);
  });
});

describe("palettes", () => {
  it("renames a palette in every profile that uses it", () => {
    const before = paletteUses(base, "Cyberpunk");
    expect(before.length).toBeGreaterThan(0);
    const next = renamePalette(base, "Cyberpunk", "Neon");
    expect(paletteUses(next, "Cyberpunk")).toEqual([]);
    expect(paletteUses(next, "Neon")).toEqual(before);
    expect(next.lighting.palettes.map(p => p.name)).toContain("Neon");
  });

  it("copies a palette under a free name", () => {
    const { config, name } = addPalette(base, "Aurora");
    expect(name).toBe("Aurora copy");
    expect(config.lighting.palettes.find(p => p.name === name)!.stops).toEqual(base.lighting.palettes.find(p => p.name === "Aurora")!.stops);
  });

  it("deletes only a palette nothing uses", () => {
    expect(deletePalette(base, "Cyberpunk")).toBeNull();
    const { config, name } = addPalette(base);
    expect(deletePalette(config, name)!.lighting.palettes.length).toBe(base.lighting.palettes.length);
  });
});
