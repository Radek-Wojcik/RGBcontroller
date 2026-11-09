import type { AppConfig, GroupEffectConfig, PaletteConfig, ProfileConfig, TurnOffSet } from "./types";

// Pure, immutable edits of the configuration. The page sends the whole edited config with updateConfig; the
// Service validates it and refuses it when someone else changed the config meanwhile.

export function findProfile(config: AppConfig, name: string): ProfileConfig | undefined {
  return config.lighting.profiles.find(p => p.name.toLowerCase() === name.toLowerCase());
}

export function updateProfile(config: AppConfig, name: string, change: (p: ProfileConfig) => ProfileConfig): AppConfig {
  return {
    ...config,
    lighting: {
      ...config.lighting,
      profiles: config.lighting.profiles.map(p => (p.name.toLowerCase() === name.toLowerCase() ? change(p) : p)),
    },
  };
}

/** Changes one group's effect in a profile; a group the profile does not list yet is added (it was off). */
export function updateGroupEffect(config: AppConfig, profile: string, group: string, change: Partial<GroupEffectConfig>): AppConfig {
  return updateProfile(config, profile, p => {
    const existing = p.groups.find(g => g.group === group);
    const base: GroupEffectConfig = existing ?? {
      group, effect: "Static", palette: "Rainbow", speed: 0.2, angle: 0, reverse: false, scale: 1,
      radialMode: "Rotate", brightnessPercent: 100, hardwareMode: null,
    };
    const next = { ...base, ...change };
    return { ...p, groups: existing ? p.groups.map(g => (g === existing ? next : g)) : [...p.groups, next] };
  });
}

export type TurnOffSituation = "unlocked" | "locked";

/** Adds or removes a device (or the strip, with device null) from a display-off turn-off set (SPEC 4). */
export function setTurnOff(config: AppConfig, situation: TurnOffSituation, device: string | null, off: boolean): AppConfig {
  const set = config.lighting.displayOff[situation];
  const next: TurnOffSet = device === null
    ? { ...set, strip: off }
    : {
        ...set,
        devices: off
          ? [...set.devices.filter(d => d.toLowerCase() !== device.toLowerCase()), device]
          : set.devices.filter(d => d.toLowerCase() !== device.toLowerCase()),
      };
  return { ...config, lighting: { ...config.lighting, displayOff: { ...config.lighting.displayOff, [situation]: next } } };
}

export function isTurnedOff(config: AppConfig, situation: TurnOffSituation, device: string): boolean {
  return config.lighting.displayOff[situation].devices.some(d => d.toLowerCase() === device.toLowerCase());
}

/** A name not used yet (case-insensitive): "Base", then "Base 2", "Base 3", ... */
export function uniqueName(existing: string[], base: string): string {
  const taken = new Set(existing.map(n => n.toLowerCase()));
  if (!taken.has(base.toLowerCase())) return base;
  for (let i = 2; ; i++) if (!taken.has(`${base} ${i}`.toLowerCase())) return `${base} ${i}`;
}

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/** Copies a profile under a new name (the original name plus a number); returns the config and the new name. */
export function duplicateProfile(config: AppConfig, name: string): { config: AppConfig; name: string } {
  const source = findProfile(config, name);
  if (!source) return { config, name };
  const copyName = uniqueName(config.lighting.profiles.map(p => p.name), `${source.name} copy`);
  const copy: ProfileConfig = structuredClone({ ...source, name: copyName });
  return { config: { ...config, lighting: { ...config.lighting, profiles: [...config.lighting.profiles, copy] } }, name: copyName };
}

/** Renames a profile and every reference to it (active, logon and lock profile, profile hotkeys). */
export function renameProfile(config: AppConfig, from: string, to: string): AppConfig {
  const l = config.lighting;
  const ref = (n: string) => (same(n, from) ? to : n);
  return {
    ...config,
    lighting: {
      ...l,
      activeProfile: ref(l.activeProfile),
      logonProfile: ref(l.logonProfile),
      lockProfile: l.lockProfile === null ? null : ref(l.lockProfile),
      profiles: l.profiles.map(p => (same(p.name, from) ? { ...p, name: to } : p)),
    },
    hotkeys: { ...config.hotkeys, bindings: config.hotkeys.bindings.map(b => (b.profile !== null && same(b.profile, from) ? { ...b, profile: to } : b)) },
  };
}

/**
 * Removes a profile. The last profile stays. References to it move to the first remaining profile (the lock profile
 * goes back to "same as active"); hotkeys that switched to it are removed.
 */
export function deleteProfile(config: AppConfig, name: string): AppConfig {
  const l = config.lighting;
  const rest = l.profiles.filter(p => !same(p.name, name));
  if (rest.length === 0 || rest.length === l.profiles.length) return config;
  const fallback = rest[0].name;
  const ref = (n: string) => (same(n, name) ? fallback : n);
  return {
    ...config,
    lighting: {
      ...l,
      profiles: rest,
      activeProfile: ref(l.activeProfile),
      logonProfile: ref(l.logonProfile),
      lockProfile: l.lockProfile !== null && same(l.lockProfile, name) ? null : l.lockProfile,
    },
    hotkeys: { ...config.hotkeys, bindings: config.hotkeys.bindings.filter(b => b.profile === null || !same(b.profile, name)) },
  };
}

/** What an exported profile file holds: the profile and the palettes it uses (by value, so another PC can import it). */
export interface ProfileExport { kind: "rgbcontroller-profile"; version: 1; profile: ProfileConfig; palettes: PaletteConfig[] }

function usedPalettes(profile: ProfileConfig): Set<string> {
  const used = new Set<string>();
  for (const g of profile.groups) if (!g.palette.startsWith("#")) used.add(g.palette.toLowerCase());
  if (!profile.ambilight.palette.startsWith("#")) used.add(profile.ambilight.palette.toLowerCase());
  return used;
}

export function exportProfile(config: AppConfig, name: string): ProfileExport | null {
  const profile = findProfile(config, name);
  if (!profile) return null;
  const used = usedPalettes(profile);
  return {
    kind: "rgbcontroller-profile", version: 1, profile: structuredClone(profile),
    palettes: structuredClone(config.lighting.palettes.filter(p => used.has(p.name.toLowerCase()))),
  };
}

/**
 * Adds an exported profile. A profile name in use gets a number; a palette that is missing is added, one with the
 * same name is kept as it is here. Groups the profile mentions that this setup does not have are dropped.
 */
export function importProfile(config: AppConfig, data: unknown): { config: AppConfig; name: string } | string {
  const file = data as Partial<ProfileExport> | null;
  if (!file || file.kind !== "rgbcontroller-profile" || file.version !== 1 || !file.profile || typeof file.profile.name !== "string"
    || !Array.isArray(file.profile.groups) || !file.profile.ambilight || !Array.isArray(file.palettes)) {
    return "This is not an RGBcontroller profile file.";
  }

  const l = config.lighting;
  const name = uniqueName(l.profiles.map(p => p.name), file.profile.name.trim().slice(0, 100) || "Imported");
  const groupNames = new Set(l.groups.map(g => g.name.toLowerCase()));
  const palettes = [...l.palettes];
  for (const p of file.palettes) {
    if (typeof p?.name === "string" && Array.isArray(p.stops) && !palettes.some(x => same(x.name, p.name))) palettes.push(p);
  }

  const profile: ProfileConfig = {
    ...file.profile, name, groups: file.profile.groups.filter(g => groupNames.has(String(g.group).toLowerCase())),
  };
  return { config: { ...config, lighting: { ...l, palettes, profiles: [...l.profiles, profile] } }, name };
}

/** Where a palette is used: "profile: group" strings, ambilight included. */
export function paletteUses(config: AppConfig, palette: string): string[] {
  return config.lighting.profiles.flatMap(p => [
    ...p.groups.filter(g => same(g.palette, palette)).map(g => `${p.name}: ${g.group}`),
    ...(same(p.ambilight.palette, palette) ? [`${p.name}: Ambilight`] : []),
  ]);
}

export function updatePalette(config: AppConfig, name: string, change: (p: PaletteConfig) => PaletteConfig): AppConfig {
  return { ...config, lighting: { ...config.lighting, palettes: config.lighting.palettes.map(p => (same(p.name, name) ? change(p) : p)) } };
}

/** New palette: a copy of the given one (or a two-color default) under a free name. */
export function addPalette(config: AppConfig, copyOf?: string): { config: AppConfig; name: string } {
  const source = copyOf ? config.lighting.palettes.find(p => same(p.name, copyOf)) : undefined;
  const name = uniqueName(config.lighting.palettes.map(p => p.name), source ? `${source.name} copy` : "New palette");
  const stops = source ? structuredClone(source.stops) : [{ position: 0, color: "#FF0000" }, { position: 0.5, color: "#0000FF" }];
  return { config: { ...config, lighting: { ...config.lighting, palettes: [...config.lighting.palettes, { name, stops }] } }, name };
}

/** Renames a palette and every reference to it in the profiles. */
export function renamePalette(config: AppConfig, from: string, to: string): AppConfig {
  const ref = (n: string) => (same(n, from) ? to : n);
  const next = updatePalette(config, from, p => ({ ...p, name: to }));
  return {
    ...next,
    lighting: {
      ...next.lighting,
      profiles: next.lighting.profiles.map(p => ({
        ...p, groups: p.groups.map(g => ({ ...g, palette: ref(g.palette) })), ambilight: { ...p.ambilight, palette: ref(p.ambilight.palette) },
      })),
    },
  };
}

/** Deletes a palette that nothing uses; returns null when it is in use (or the last one). */
export function deletePalette(config: AppConfig, name: string): AppConfig | null {
  if (config.lighting.palettes.length <= 1 || paletteUses(config, name).length > 0) return null;
  return { ...config, lighting: { ...config.lighting, palettes: config.lighting.palettes.filter(p => !same(p.name, name)) } };
}

/** CSS gradient for a palette name or a single "#RRGGBB" color. */
export function paletteCss(palettes: PaletteConfig[], palette: string): string {
  if (/^#[0-9a-f]{6}$/i.test(palette)) return palette;
  const found = palettes.find(p => p.name.toLowerCase() === palette.toLowerCase());
  if (!found || found.stops.length === 0) return "#333";
  const stops = [...found.stops].sort((a, b) => a.position - b.position);
  // Palettes are cyclic: close the gradient with the first color.
  const parts = [...stops.map(s => `${s.color} ${(s.position * 100).toFixed(1)}%`), `${stops[0].color} 100%`];
  return `linear-gradient(90deg, ${parts.join(", ")})`;
}
