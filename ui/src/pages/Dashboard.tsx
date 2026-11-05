import { bridge } from "../bridge";
import { findProfile, paletteCss, updateGroupEffect, updateProfile } from "../config";
import { useStore, useThrottled } from "../store";
import { StripView, useStripColors } from "../StripView";
import type { AmbilightMode, AppConfig, DeviceList, EffectKind, GroupConfig, GroupEffectConfig } from "../types";
import { Empty, Panel, Segmented, Select, Slider } from "../ui";

const MODES = ["Screen", "Radial", "Solid", "Off"] as const;
const EFFECTS: EffectKind[] = ["Off", "Static", "Breathing", "Wave", "Radial", "Mirror", "ColorCycle", "Aurora"];
const EFFECT_LABELS: Partial<Record<EffectKind, string>> = { ColorCycle: "Color cycle" };

/** Effects whose speed, scale or angle mean something (Static and Off ignore them). */
const ANIMATED = new Set<EffectKind>(["Breathing", "Wave", "Radial", "Mirror", "ColorCycle", "Aurora"]);

export function Dashboard() {
  const { state, snapshot, devices } = useStore();
  if (!state || !snapshot) return <Empty>Waiting for the Service...</Empty>;
  const config = snapshot.config;
  const profile = findProfile(config, state.activeProfile);
  if (!profile) return <Empty>The active profile {state.activeProfile} is missing from the config.</Empty>;

  return (
    <div className="tiles">
      <AmbilightTile config={config} />
      {config.lighting.groups.map(group => (
        <GroupTile key={group.name} config={config} group={group} effect={profile.groups.find(g => g.group === group.name)}
          profile={profile.name} devices={devices} />
      ))}
    </div>
  );
}

function AmbilightTile({ config }: { config: AppConfig }) {
  const { state, run, editConfig } = useStore();
  const profile = findProfile(config, state!.activeProfile)!;
  const a = profile.ambilight;
  const sendBrightness = useThrottled((v: number) => run(() => bridge.call("changeStrip", { brightnessPercent: v })));
  const live = useStripColors();
  const saveColor = useThrottled((color: string) =>
    editConfig(c => updateProfile(c, profile.name, p => ({ ...p, ambilight: { ...p.ambilight, solidColor: color.toUpperCase() } }))), 300);

  return (
    <Panel wide tall title="Ambilight" meta={<span className="num">{config.strip.ledCount} LED · {state!.captureWanted ? "capturing" : "idle"}</span>}>
      <div className="ambilight">
        <StripView layout={config.strip.layout} ledCount={config.strip.ledCount} mode={state!.allOff ? "Off" : a.mode}
          solidColor={a.solidColor} brightness={state!.stripBrightnessPercent} speed={a.speed} live={live} />
        <div className="controls">
          <Segmented options={MODES} value={a.mode} onChange={(mode: AmbilightMode) => run(() => bridge.call("changeStrip", { mode }))} />
          <Slider label={a.mode === "Solid" ? "Solid power" : "Brightness"} value={state!.stripBrightnessPercent} min={0} max={100} unit="%"
            onChange={sendBrightness} disabled={a.mode === "Off"} />
          <div className="row">
            <div className="field color">
              <label htmlFor="solid-color">Solid color</label>
              <input id="solid-color" type="color" value={a.solidColor.toLowerCase()} onChange={e => saveColor(e.target.value)} />
              <span className="num dim">{a.solidColor}</span>
            </div>
            <Select label="When capture is impossible" value={a.fallback}
              options={(["Radial", "Solid", "Off"] as const).map(m => ({ value: m }))}
              onChange={fallback => editConfig(c => updateProfile(c, profile.name, p => ({ ...p, ambilight: { ...p.ambilight, fallback } })))} />
          </div>
        </div>
      </div>
    </Panel>
  );
}

function GroupTile({ config, group, effect, profile, devices }: {
  config: AppConfig; group: GroupConfig; effect: GroupEffectConfig | undefined; profile: string; devices: DeviceList | null;
}) {
  const { run, editConfig } = useStore();
  const tune = useThrottled((change: { speed?: number; scale?: number; brightnessPercent?: number; maxFps?: number }) =>
    run(() => bridge.call("tuneGroup", { group: group.name, profile, ...change })), 150);
  const setEffect = (change: Partial<GroupEffectConfig>) => editConfig(c => updateGroupEffect(c, profile, group.name, change));

  const kind = effect?.effect ?? "Off";
  const deviceNames = [...new Set(group.members.map(m => m.device))];
  const slowBus = devices?.devices.some(d => deviceNames.includes(d.name) && (d.type === 1 || d.type === 2)) ?? false;
  const leds = group.members.reduce((sum, m) => {
    const device = devices?.devices.find(d => d.name === m.device);
    const zones = device?.zones.filter(z => m.zone === null || z.name === m.zone) ?? [];
    return sum + zones.reduce((s, z) => s + z.ledCount, 0);
  }, 0);
  const custom = effect && /^#[0-9a-f]{6}$/i.test(effect.palette);
  const swatch = effect ? paletteCss(config.lighting.palettes, effect.palette) : "transparent";
  const flow = effect && ANIMATED.has(kind) && effect.speed > 0 ? `${Math.min(30, Math.max(1.2, 1 / effect.speed)).toFixed(2)}s` : undefined;

  return (
    <Panel title={group.name} meta={<span className="num">{leds || "?"} LED · {group.maxFps} fps</span>} className={kind === "Off" ? "is-off" : ""}>
      <div className="swatch" style={{ background: kind === "Off" ? undefined : swatch, animationDuration: flow }} data-flow={flow ? "on" : "off"}
        aria-hidden />
      <p className="members">{deviceNames.join(" · ")}</p>
      <div className="row">
        <Select label="Effect" value={kind} options={EFFECTS.map(e => ({ value: e, label: EFFECT_LABELS[e] }))}
          onChange={e => setEffect({ effect: e })} />
        <Select label="Palette" value={custom ? "custom" : effect?.palette ?? "Rainbow"} disabled={kind === "Off"}
          options={[...config.lighting.palettes.map(p => ({ value: p.name })), { value: "custom", label: "Single color" }]}
          onChange={p => setEffect({ palette: p === "custom" ? "#00E5FF" : p })} />
        {custom && (
          <div className="field color">
            <label htmlFor={`c-${group.name}`}>Color</label>
            <input id={`c-${group.name}`} type="color" value={effect!.palette.toLowerCase()} onChange={e => setEffect({ palette: e.target.value.toUpperCase() })} />
          </div>
        )}
      </div>
      {effect && kind !== "Off" && (
        <>
          <Slider label="Brightness" value={effect.brightnessPercent} min={0} max={100} unit="%" onChange={v => tune({ brightnessPercent: v })} />
          {ANIMATED.has(kind) && (
            <>
              <Slider label="Speed" value={effect.speed} min={0} max={2} step={0.01} digits={2} unit="/s" onChange={v => tune({ speed: v })} />
              <Slider label="Wave length" value={effect.scale} min={0.1} max={5} step={0.05} digits={2} unit="×" onChange={v => tune({ scale: v })} />
            </>
          )}
        </>
      )}
      <Slider label="Frame rate cap" value={group.maxFps} min={1} max={60} unit=" fps" onChange={v => tune({ maxFps: v })} />
      {slowBus && group.maxFps > 15 && (
        <p className="warning">RAM and GPU are on slow I2C/SMBus links: above 15 fps the CPU cost rises (about 0.16% per RAM fps).</p>
      )}
    </Panel>
  );
}
