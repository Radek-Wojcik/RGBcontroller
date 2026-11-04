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
