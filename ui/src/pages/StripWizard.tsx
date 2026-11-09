import { useEffect } from "react";
import { bridge } from "../bridge";
import { edgeRun, nearestCorner, nearestSlot, type Edge } from "../strip";
import { StripView, useStripColors } from "../StripView";
import { useStore } from "../store";
import type { StripLayoutConfig } from "../types";
import { NumberField, Panel, Segmented } from "../ui";

const MARK_SECONDS = 300;
const EDGES = [["Top", "top"], ["Right", "right"], ["Bottom", "bottom"], ["Left", "left"]] as const;

/**
 * SPEC 7.5: lights parts of the strip and the owner tells the app where they are. The drawn strip always shows the
 * LEDs the Service really lights, so a step is done when the drawing matches the strip on the desk. Edits apply at once.
 */
export function StripWizard({ onClose }: { onClose(): void }) {
  const { state, snapshot, editConfig, run } = useStore();
  const live = useStripColors();
  // The Service keeps a marker for a while; leaving the wizard ends it at once.
  useEffect(() => () => void bridge.call("showStripMarker", { start: 0, count: 1, seconds: 0 }).catch(() => {}), []);
  if (!state || !snapshot) return null;

  const config = snapshot.config;
  const layout = config.strip.layout;
  const ledCount = config.strip.ledCount;
  const set = (change: Partial<StripLayoutConfig>) =>
    editConfig(c => ({ ...c, strip: { ...c.strip, layout: { ...c.strip.layout, ...change } } }));
  const mark = (start: number, count: number) =>
    run(() => bridge.call("showStripMarker", { start: Math.max(0, start), count: Math.max(1, count), seconds: MARK_SECONDS }));
  const markEdge = (edge: Edge, from: StripLayoutConfig) => {
    const r = edgeRun(from, ledCount, edge);
    if (r) void mark(r.start, r.count);
  };

  const pickStart = (x: number, y: number) => {
    const startCorner = nearestCorner(x, y);
    void set({ startCorner, offset: nearestSlot({ ...layout, startCorner, offset: 0 }, x, y) });
  };

  return (
    <Panel wide tall title="Strip layout wizard" meta={<button onClick={onClose}>Close</button>}>
      <div className="ambilight">
        <StripView layout={layout} ledCount={ledCount} mode="Off" solidColor="#FFFFFF" brightness={100} speed={0} live={live} onPick={pickStart} />
        <div className="controls">
          <p className="dim">
            Look at the monitor with the strip. The drawn strip is what the Service lights right now. Each step lights some LEDs
            in white; change the settings until the drawing matches the real strip. Changes apply and save at once.
          </p>

          <section className="wizard-step">
            <h3>Step 1 · Where does the strip start?</h3>
            <p className="dim">
              The first LED is the one next to the cable from the LED box (ringed in the drawing). Press "Light the first 3
              LEDs", then click the outline where the lit LEDs really are.
            </p>
            <div className="row buttons">
              <button className="primary" onClick={() => void mark(0, 3)}>Light the first 3 LEDs</button>
              <button onClick={() => void mark(ledCount - 3, 3)}>Light the last 3 LEDs</button>
            </div>
          </section>

          <section className="wizard-step">
            <h3>Step 2 · Which way does it run?</h3>
            <p className="dim">Press "Light LEDs 1 to 20": the lit stretch should start at the first LED and run the way it is drawn.</p>
            <div className="row buttons">
              <button className="primary" onClick={() => void mark(0, 20)}>Light LEDs 1 to 20</button>
              <div className="field">
                <label>Direction (from the front)</label>
                <Segmented options={["cw", "ccw"] as const} value={layout.clockwise ? "cw" : "ccw"} labels={{ cw: "Clockwise", ccw: "Counter" }}
                  onChange={d => set({ clockwise: d === "cw" })} />
              </div>
            </div>
          </section>

          <section className="wizard-step">
            <h3>Step 3 · How many LEDs on each edge?</h3>
            <p className="dim">
              Press an edge's button: exactly the LEDs the app assigns to that edge light up. Change the count until they cover
              the whole edge and nothing else.
            </p>
            <div className="row four">
              {EDGES.map(([edge, key]) => (
                <div key={edge}>
                  <NumberField label={edge} value={layout[key]} min={0} max={1024}
                    onCommit={v => { void set({ [key]: v }); markEdge(edge, { ...layout, [key]: v }); }} />
                  <button onClick={() => markEdge(edge, layout)}>Light {key}</button>
                </div>
              ))}
            </div>
            <p className="dim">
              Placed {layout.top + layout.right + layout.bottom + layout.left} of {ledCount} LEDs. LEDs hidden behind the
              monitor stand can be kept dark on the Ambilight page.
            </p>
          </section>

          <div className="row buttons">
            <button onClick={() => void run(() => bridge.call("showStripMarker", { start: 0, count: 1, seconds: 0 }))}>Back to normal light</button>
          </div>
        </div>
      </div>
    </Panel>
  );
}
