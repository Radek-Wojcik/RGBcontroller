import { bridge } from "../bridge";
import { isTurnedOff, setTurnOff, type TurnOffSituation } from "../config";
import { useStore } from "../store";
import { Empty, NumberField, Panel, Segmented, StatusDot, Toggle } from "../ui";
import { LogViewer } from "./LogViewer";

export type Accent = "cyan" | "magenta" | "acid";

/** Power policy (SPEC 4 turn-off sets), strip hardware, diagnostics and personalization. */
export function Settings({ accent, onAccent }: { accent: Accent; onAccent(accent: Accent): void }) {
  const { state, snapshot, devices, connection, editConfig, run } = useStore();
  if (!state || !snapshot) return <Empty>Waiting for the Service...</Empty>;
  const config = snapshot.config;
  const names = devices?.devices.map(d => d.name) ?? [];
  const situations: { key: TurnOffSituation; label: string }[] = [
    { key: "unlocked", label: "Display off, unlocked" },
    { key: "locked", label: "Display off, locked" },
  ];

  return (
    <div className="tiles">
      <Panel wide title="When the display turns off" meta="SPEC 4">
        <table className="grid matrix">
          <thead><tr><th>Output</th>{situations.map(s => <th key={s.key}>{s.label}</th>)}</tr></thead>
          <tbody>
            <tr>
              <td>Ambilight strip</td>
              {situations.map(s => (
                <td key={s.key}><Toggle label={<span className="sr">turn off</span>} checked={config.lighting.displayOff[s.key].strip}
                  onChange={off => editConfig(c => setTurnOff(c, s.key, null, off))} /></td>
              ))}
            </tr>
            {names.map(name => (
              <tr key={name}>
                <td>{name}</td>
                {situations.map(s => (
                  <td key={s.key}><Toggle label={<span className="sr">turn off</span>} checked={isTurnedOff(config, s.key, name)}
                    onChange={off => editConfig(c => setTurnOff(c, s.key, name, off))} /></td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
        <p className="dim">Ticked outputs go dark while the display is off and come back when it turns on. Sleep always turns everything off.</p>
      </Panel>

      <Panel title="Strip hardware" meta="applies after a Service restart">
        <div className="row">
          <div className="field">
            <label htmlFor="port">Port (empty: find by USB ID)</label>
            <input id="port" defaultValue={config.strip.portName ?? ""} key={config.strip.portName ?? ""} placeholder="auto"
              onBlur={e => {
                const port = e.target.value.trim().toUpperCase() || null;
                if (port !== config.strip.portName) void editConfig(c => ({ ...c, strip: { ...c.strip, portName: port } }));
              }} />
          </div>
          <NumberField label="LED count" value={config.strip.ledCount} min={1} max={1024} onCommit={ledCount => editConfig(c => ({ ...c, strip: { ...c.strip, ledCount } }))} />
        </div>
        <div className="row">
          <NumberField label="Current limit" value={config.strip.currentLimitMilliamps} min={100} max={20000} unit="mA"
            onCommit={v => editConfig(c => ({ ...c, strip: { ...c.strip, currentLimitMilliamps: v } }))} />
          <NumberField label="Frame rate cap" value={config.strip.maxFps} min={1} max={60} unit="fps"
            onCommit={maxFps => editConfig(c => ({ ...c, strip: { ...c.strip, maxFps } }))} />
        </div>
      </Panel>

      <Panel title="Diagnostics">
        <div className="statuses">
          <StatusDot label="Service" state={connection?.connected ? "Ok" : "Offline"} />
          <StatusDot label="OpenRGB" state={state.status.openRgb} />
          <StatusDot label="Strip" state={state.status.strip} />
        </div>
        <dl className="facts">
          <dt>Service</dt><dd className="num">{connection?.version ?? "-"}</dd>
          <dt>Detail</dt><dd>{state.status.detail ?? "-"}</dd>
          <dt>Shown profile</dt><dd>{state.shownProfile}</dd>
          <dt>Devices</dt><dd className="num">{devices?.devices.length ?? 0}</dd>
          <dt>Config revision</dt><dd className="num">{snapshot.revision}</dd>
        </dl>
        <div className="row buttons">
          <button disabled={!connection?.connected} onClick={() => run(() => bridge.call("rescanDevices"))}>Rescan devices</button>
          <button disabled={!connection?.connected} onClick={() => run(() => bridge.call("reopenStrip"))}>Reconnect strip</button>
          <button disabled={!connection?.connected} onClick={() => run(() => bridge.call("restartOpenRgb"))}>Restart OpenRGB</button>
        </div>
        <p className="dim">
          Rescan when a device is missing after it was plugged in. Restart OpenRGB when devices stop reacting; the lights
          go dark for a few seconds. Reconnect the strip when the LED box stops taking frames.
        </p>
      </Panel>

      <LogViewer />

      <Panel title="Look">
        <div className="field">
          <label>Accent</label>
          <Segmented options={["cyan", "magenta", "acid"] as const} value={accent} onChange={onAccent}
            labels={{ cyan: "Cyan", magenta: "Magenta", acid: "Acid" }} />
        </div>
      </Panel>
    </div>
  );
}
