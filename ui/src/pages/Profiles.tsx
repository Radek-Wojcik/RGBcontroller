import { bridge } from "../bridge";
import { paletteCss, updateProfile } from "../config";
import { useStore, useThrottled } from "../store";
import type { AmbilightConfig, AmbilightMode } from "../types";
import { Empty, Panel, Segmented, Select, Slider } from "../ui";
import { PaletteEditor } from "./PaletteEditor";
import { ImportProfile, ProfileTools } from "./ProfileTools";

const MODES = ["Screen", "Radial", "Solid", "Off"] as const;

/** SPEC 5.4: profiles switch from here, the tray or a hotkey; the logon and lock profiles are chosen here too. */
export function Profiles() {
  const { state, snapshot, run, editConfig } = useStore();
  const saveAmbilight = (name: string, change: Partial<AmbilightConfig>) =>
    editConfig(c => updateProfile(c, name, p => ({ ...p, ambilight: { ...p.ambilight, ...change } })));
  const saveSolid = useThrottled((args: { name: string; color: string }) => saveAmbilight(args.name, { solidColor: args.color.toUpperCase() }), 300);
  const saveMaster = useThrottled((args: { name: string; value: number }) =>
    editConfig(c => updateProfile(c, args.name, p => ({ ...p, masterBrightnessPercent: args.value }))), 300);
  if (!state || !snapshot) return <Empty>Waiting for the Service...</Empty>;
  const lighting = snapshot.config.lighting;

  return (
    <div className="tiles">
      {lighting.profiles.map(p => {
        const active = p.name === state.activeProfile;
        const roles = [
          p.name === lighting.logonProfile && "logon screen",
          (lighting.lockProfile ?? lighting.activeProfile) === p.name && "locked",
        ].filter(Boolean);
        return (
          <Panel key={p.name} title={p.name} className={active ? "is-active" : ""}
            meta={<span className="num">{active ? "ACTIVE" : roles.join(" · ")}</span>}>
            <div className="profile-stripes" aria-hidden>
              {p.groups.map(g => (
                <span key={g.group} title={`${g.group}: ${g.effect}`}
                  style={{ background: g.effect === "Off" ? undefined : paletteCss(lighting.palettes, g.palette) }} />
              ))}
            </div>
            <ul className="profile-groups">
              {p.groups.map(g => <li key={g.group}><span>{g.group}</span><b>{g.effect}</b></li>)}
            </ul>
            <div className="ambilight-theme">
              <div className="ambilight-head">
                <span>Ambilight</span>
                <span className="theme-swatch" title={ambilightLook(p.ambilight)} style={{ background: ambilightCss(p.ambilight, lighting.palettes) }} />
              </div>
              <Segmented options={MODES} value={p.ambilight.mode} onChange={(mode: AmbilightMode) => saveAmbilight(p.name, { mode })} />
              <div className="row">
                <Select label="Radial palette" value={p.ambilight.palette}
                  options={lighting.palettes.map(x => ({ value: x.name }))} onChange={palette => saveAmbilight(p.name, { palette })} />
                <div className="field color">
                  <label htmlFor={`solid-${p.name}`}>Solid color</label>
                  <input id={`solid-${p.name}`} type="color" value={p.ambilight.solidColor.toLowerCase()}
                    onChange={e => saveSolid({ name: p.name, color: e.target.value })} />
                </div>
                <Select label="When locked" value={p.ambilight.fallback}
                  options={(["Radial", "Solid", "Off"] as const).map(m => ({ value: m }))} onChange={fallback => saveAmbilight(p.name, { fallback })} />
              </div>
            </div>
            <Slider label="Master brightness" value={p.masterBrightnessPercent} min={0} max={100} unit="%"
              onChange={value => saveMaster({ name: p.name, value })} />
            <div className="row buttons">
              <button className="primary" disabled={active} onClick={() => run(() => bridge.call("setActiveProfile", { name: p.name }))}>
                {active ? "Active" : "Activate"}
              </button>
              <button disabled={lighting.logonProfile === p.name}
                onClick={() => editConfig(c => ({ ...c, lighting: { ...c.lighting, logonProfile: p.name } }))}>Use on logon screen</button>
              <button disabled={lighting.lockProfile === p.name}
                onClick={() => editConfig(c => ({ ...c, lighting: { ...c.lighting, lockProfile: p.name } }))}>Use when locked</button>
            </div>
            <ProfileTools name={p.name} />
          </Panel>
        );
      })}
      <Panel title="Import" meta="profile file">
        <p className="dim">Add a profile exported from another RGBcontroller. A name already in use gets a number; palettes it needs are added.</p>
        <ImportProfile />
      </Panel>
      <PaletteEditor />
      <Panel title="Lock screen" meta="SPEC 4">
        <p className="dim">
          While the session is locked the strip cannot mirror the screen; it shows the profile's fallback mode.
          {lighting.lockProfile ? ` Locked: ${lighting.lockProfile}.` : " Locked: the active profile."}
        </p>
        <div className="row buttons">
          <button disabled={lighting.lockProfile === null}
            onClick={() => editConfig(c => ({ ...c, lighting: { ...c.lighting, lockProfile: null } }))}>Same as active profile</button>
        </div>
      </Panel>
    </div>
  );
}

/** What the strip shows in this profile: the palette for Radial (and as the lock-screen fallback), the color for Solid. */
function ambilightCss(a: AmbilightConfig, palettes: Parameters<typeof paletteCss>[0]): string {
  const shown = a.mode === "Screen" ? a.fallback : a.mode;
  if (shown === "Solid") return a.solidColor;
  if (shown === "Off") return "transparent";
  return paletteCss(palettes, a.palette);
}

function ambilightLook(a: AmbilightConfig): string {
  return a.mode === "Screen" ? `Mirrors the screen; when locked: ${a.fallback}` : a.mode;
}
