import { useEffect, useMemo, useRef, useState } from "react";
import { bridge } from "../bridge";
import { useDeviceColors } from "../DevicePreview";
import {
  applyLayout, applyZoneSize, changeSegment, describeSegment, findLayout, removeSegment, resizeSegment,
  resolveSegments, ringTop, segmentRange, splitLast, toLayout, type Segment, type SegmentShape,
} from "../mapping";
import { useStore } from "../store";
import type { DeviceInfo, ZoneInfo } from "../types";
import { NumberField, Panel, Select, Toggle } from "../ui";

const SHAPES: readonly SegmentShape[] = ["Ring", "Line", "Point"];
const IDENTIFY_SECONDS = 120;
const MAX_HEADER_LEDS = 300;

interface Pick { device: DeviceInfo; zone: ZoneInfo }

/** What the hardware shows: the whole zone, one part, or only the LED a ring has on top. */
type View = { kind: "zone" } | { kind: "segment" | "top" | "probe"; index: number };

/** Zones the owner can describe: single LEDs and lines. Matrix zones (the keyboard) have their shape from the device. */
function editableZones(devices: DeviceInfo[]): Pick[] {
  return devices.flatMap(device => device.zones.filter(z => z.type !== 2 && z.ledCount > 0).map(zone => ({ device, zone })));
}

const zoneKey = (p: Pick) => `${p.device.index}/${p.zone.name}`;

/**
 * SPEC 7.4: lights one zone or one part of it at a time, and the owner says what it is (ring or line, how many
 * LEDs, which way round, which LED is on top). Saves the same layout structures the defaults use.
 */
export function MappingWizard({ onClose }: { onClose(): void }) {
  const { devices, snapshot } = useStore();
  const picks = useMemo(() => editableZones(devices?.devices ?? []), [devices]);
  const [key, setKey] = useState<string | null>(null);
  const pick = picks.find(p => zoneKey(p) === key) ?? picks.find(p => p.zone.resizable) ?? picks[0];

  // The Service keeps an identification for a while; leaving the wizard ends it at once.
  useEffect(() => () => void bridge.call("identify", { deviceIndex: 0, zone: null, start: 0, count: 1, seconds: 0 }).catch(() => {}), []);

  const close = <button onClick={onClose}>Close</button>;
  if (!devices || !snapshot) return null;
  if (!pick) return <Panel title="Mapping wizard" wide meta={close}><p className="dim">No zones to map right now.</p></Panel>;

  return (
    <Panel title="Mapping wizard" wide meta={close}>
      <p className="dim">
        This tells the app what is plugged into an RGB header of the motherboard (for example a pump ring and fans), so effects
        such as Radial look right on them. Pick a header, then follow steps 1 to 3 while you look at your PC: the header lights
        up white and everything else goes dark. Nothing is saved until you press Save mapping.
      </p>
      <Select label="Zone" value={zoneKey(pick)}
        options={picks.map(p => ({ value: zoneKey(p), label: `${p.device.name} · ${p.zone.name} (${p.zone.ledCount} LEDs)` }))}
        onChange={setKey} />
      {/* Remounted per zone and per LED count, so the draft always starts from what is saved. */}
      <ZoneEditor key={`${zoneKey(pick)}/${pick.zone.ledCount}`} pick={pick} />
    </Panel>
  );
}

function ZoneEditor({ pick }: { pick: Pick }) {
  const { snapshot, run, editConfig } = useStore();
  const { device, zone } = pick;
  const config = snapshot!.config;
  const saved = useMemo(() => resolveSegments(findLayout(config, device.name, zone.name), zone), [config, device.name, zone]);
  const [segments, setSegments] = useState<Segment[]>(saved);
  const [view, setView] = useState<View>({ kind: "zone" });
  const colors = useDeviceColors();
  const count = segments.length;

  const target = useMemo(() => {
    if (view.kind === "zone") return { start: 0, count: zone.ledCount };
    if (view.kind === "probe") return { start: Math.min(view.index, zone.ledCount - 1), count: 1 };
    const index = Math.min(view.index, segments.length - 1);
    return view.kind === "top" ? { start: ringTop(segments, index), count: 1 } : segmentRange(segments, index);
  }, [view, segments, zone.ledCount]);

  // Show the current target on the hardware; a later request replaces an earlier one.
  const lastTarget = useRef("");
  useEffect(() => {
    const wanted = `${device.index}/${zone.name}/${target.start}/${target.count}`;
    if (wanted === lastTarget.current) return;
    lastTarget.current = wanted;
    void run(() => bridge.call("identify", { deviceIndex: device.index, zone: zone.name, start: target.start, count: target.count, seconds: IDENTIFY_SECONDS }));
  }, [device.index, zone.name, target, run]);

  const asLayout = (s: Segment[]) => JSON.stringify(toLayout(device.name, zone.name, s));
  const dirty = asLayout(segments) !== asLayout(saved);
  const configuredSize = config.openRgb.zoneSizes.find(z => z.device === device.name && z.zone === zone.name)?.leds;
  const change = (i: number, c: Partial<Segment>) => setSegments(s => changeSegment(s, i, c));
  const probe = view.kind === "probe" ? view.index : 0;
  const showing = (i: number) => view.kind === "segment" && view.index === i;

  const live = colors.get(device.index);
  const zoneStart = device.zones.slice(0, device.zones.indexOf(zone)).reduce((sum, z) => sum + z.ledCount, 0);

  return (
    <>
      <div className="group-leds" aria-label="Live colors of the zone">
        <div className="led-line long">
          {Array.from({ length: zone.ledCount }, (_, i) => {
            const at = (zoneStart + i) * 3;
            const [r, g, b] = live ? [live[at], live[at + 1], live[at + 2]] : [0, 0, 0];
            const on = r + g + b > 0;
            return <i key={i} title={`LED ${i + 1}`} style={on ? { background: `rgb(${r} ${g} ${b})`, boxShadow: `0 0 6px rgb(${r} ${g} ${b} / 0.6)` } : undefined} />;
          })}
        </div>
      </div>

      {zone.resizable && (
        <section className="wizard-step">
          <h3>Step 1 · How many LEDs does this header have?</h3>
          <p className="dim">
            Press "Next LED" and watch the hardware: one LED lights up at a time. Keep pressing until nothing lights up any
            more; the number of the last LED that did light up is the LED count. If you reach the end of the list and it is
            still lit, the chain is longer: type a higher count below first.
          </p>
          <div className="row buttons">
            <button onClick={() => setView({ kind: "probe", index: Math.max(0, probe - 1) })} disabled={view.kind !== "probe" || probe <= 0}>Previous LED</button>
            <button className="primary" onClick={() => setView({ kind: "probe", index: view.kind === "probe" ? Math.min(zone.ledCount - 1, probe + 1) : 0 })}
              disabled={view.kind === "probe" && probe >= zone.ledCount - 1}>Next LED</button>
            <b className="num">{view.kind === "probe" ? `Lit now: LED ${probe + 1} of ${zone.ledCount}` : "Press Next LED to start"}</b>
          </div>
          <div className="row buttons">
            <button disabled={view.kind !== "probe" || probe < 1 || probe === (configuredSize ?? zone.ledCount)}
              onClick={() => void editConfig(c => applyZoneSize(c, device.name, zone.name, probe))}>
              Nothing lit up now? Then LED {probe} was the last one: use {probe} LEDs
            </button>
            <NumberField label="Or type the LED count" value={configuredSize ?? zone.ledCount} min={1} max={MAX_HEADER_LEDS}
              onCommit={leds => editConfig(c => applyZoneSize(c, device.name, zone.name, leds))} />
          </div>
          <p className="dim">A new count applies at once; the lights may blink for a moment while OpenRGB resizes the header.</p>
        </section>
      )}

      <h3>{zone.resizable ? "Step 2 · Split the LEDs into parts" : "Parts of this zone"}</h3>
      <p className="dim">
        Pump ring, fan rings, a strip: each is one part, in the order they are wired. Press "Light up" on a part to see which
        LEDs it covers, then change its LED count until only that part is lit. The last part takes the rest.
      </p>
      <table className="grid">
        <thead><tr><th>Part</th><th>Shape</th><th>LEDs</th><th>Direction</th><th /></tr></thead>
        <tbody>
          {segments.map((s, i) => (
            <tr key={i}>
              <td><button className={showing(i) ? "primary" : ""} onClick={() => setView({ kind: "segment", index: i })}>Light up {i + 1}</button></td>
              <td>
                <select aria-label={`Shape of part ${i + 1}`} value={s.shape} onChange={e => change(i, { shape: e.target.value as SegmentShape })}>
                  {SHAPES.map(x => <option key={x}>{x}</option>)}
                </select>
              </td>
              <td className="num">
                {i < count - 1
                  ? <NumberField label={`LEDs of part ${i + 1}`} value={s.leds} min={1} max={zone.ledCount - 1}
                      onCommit={leds => { setSegments(x => resizeSegment(x, i, leds)); setView({ kind: "segment", index: i }); }} />
                  : <span title="The last part takes the remaining LEDs">{s.leds} <span className="dim">(rest)</span></span>}
              </td>
              <td>
                {s.shape === "Ring" && (
                  <div className="row buttons">
                    <Toggle label="Counterclockwise" checked={s.reverse} onChange={reverse => change(i, { reverse })} />
                    <NumberField label="LED on top" value={s.offset + 1} min={1} max={Math.max(1, s.leds)}
                      onCommit={v => { change(i, { offset: v - 1 }); setView({ kind: "top", index: i }); }} />
                    <button onClick={() => setView({ kind: "top", index: i })}>Show top</button>
                  </div>
                )}
                {s.shape === "Line" && (
                  <div className="row buttons">
                    <Toggle label="Vertical" checked={s.vertical} onChange={vertical => change(i, { vertical })} />
                    <Toggle label={s.vertical ? "Bottom to top" : "Right to left"} checked={s.reverse} onChange={reverse => change(i, { reverse })} />
                  </div>
                )}
                {s.shape === "Point" && <span className="dim">single spot</span>}
              </td>
              <td>
                {count > 1 && (
                  <button className="ghost" aria-label={`Remove part ${i + 1}`}
                    onClick={() => { setSegments(x => removeSegment(x, i)); setView({ kind: "zone" }); }}>×</button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <ul className="profile-groups">
        {segments.map((s, i) => <li key={i}><span>Part {i + 1}</span><b>{describeSegment(s)}</b></li>)}
      </ul>
      {segments.some(s => s.shape === "Ring") && (
        <p className="dim">
          Step 3 · Ring direction: press "Show top" and set "LED on top" until the single lit LED sits at 12 o'clock when you
          look at the ring from the front. Then press "Light up" on the part: the lit LEDs should run clockwise; if they run the
          other way, tick Counterclockwise.
        </p>
      )}

      <div className="row buttons">
        <button onClick={() => setView({ kind: "zone" })}>Light whole header</button>
        <button disabled={segments[count - 1].leds < 2}
          onClick={() => { setSegments(splitLast); setView({ kind: "segment", index: count }); }}>Add a part</button>
        <button disabled={!dirty} onClick={() => { setSegments(saved); setView({ kind: "zone" }); }}>Undo changes</button>
        <button className="primary" disabled={!dirty}
          onClick={() => void editConfig(c => applyLayout(c, toLayout(device.name, zone.name, segments)))}>Save mapping</button>
      </div>
    </>
  );
}
