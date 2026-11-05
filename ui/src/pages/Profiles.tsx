import { bridge } from "../bridge";
import { paletteCss, updateProfile } from "../config";
import { useStore, useThrottled } from "../store";
import { Empty, Panel, Slider } from "../ui";

/** SPEC 5.4: profiles switch from here, the tray or a hotkey; the logon and lock profiles are chosen here too. */
export function Profiles() {
  const { state, snapshot, run, editConfig } = useStore();
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
              <li><span>Ambilight</span><b>{p.ambilight.mode}</b></li>
            </ul>
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
          </Panel>
        );
      })}
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
