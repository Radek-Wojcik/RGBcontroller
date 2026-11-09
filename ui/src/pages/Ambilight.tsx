import { useState } from "react";
import { bridge } from "../bridge";
import { findProfile } from "../config";
import { useStore } from "../store";
import { nearestSlot } from "../strip";
import { StripView, useStripColors } from "../StripView";
import type { ScreenCaptureConfig, StripCorner, StripLayoutConfig } from "../types";
import { Empty, NumberField, Panel, Segmented, Select, Toggle } from "../ui";
import { StripWizard } from "./StripWizard";

const CORNERS: StripCorner[] = ["TopLeft", "TopRight", "BottomRight", "BottomLeft"];
const CORNER_LABELS: Record<StripCorner, string> = { TopLeft: "Top left", TopRight: "Top right", BottomRight: "Bottom right", BottomLeft: "Bottom left" };

/** Strip layout (by numbers until the wizard of SPEC 7.5 lands) and the capture pipeline of SPEC 6.2. */
export function Ambilight() {
  const { state, snapshot, editConfig, run } = useStore();
  const live = useStripColors();
  const [wizard, setWizard] = useState(() => new URLSearchParams(location.search).has("wizard")); // ?wizard opens it directly (documentation screenshots)
  if (!state || !snapshot) return <Empty>Waiting for the Service...</Empty>;
  const config = snapshot.config;
  const layout = config.strip.layout;
  const capture = config.capture;
  const a = findProfile(config, state.activeProfile)?.ambilight;
  const placed = layout.top + layout.right + layout.bottom + layout.left;
  const setLayout = (change: Partial<StripLayoutConfig>) =>
    editConfig(c => ({ ...c, strip: { ...c.strip, layout: { ...c.strip.layout, ...change } } }));
  const setCapture = (change: Partial<ScreenCaptureConfig>) => editConfig(c => ({ ...c, capture: { ...c.capture, ...change } }));

  return (
    <div className="tiles">
      {wizard ? <StripWizard onClose={() => setWizard(false)} /> : <Panel wide tall title="Strip layout" meta={<span className="num">{config.strip.ledCount} LED · {placed} on the edges</span>}>
        <div className="ambilight">
          <StripView layout={layout} ledCount={config.strip.ledCount} mode={state.allOff ? "Off" : a?.mode ?? "Off"}
            solidColor={a?.solidColor ?? "#FFFFFF"} brightness={state.stripBrightnessPercent} speed={a?.speed ?? 0.1} live={live}
            onPick={(x, y) => {
              const offset = nearestSlot(layout, x, y);
              if (offset !== layout.offset) void setLayout({ offset });
            }} />
          <div className="controls">
            <p className="dim">
              <b>Click the outline where your strip's first LED sits</b> (the end with the cable from the LED box); the
              ringed LED moves there. Then pick the direction the strip runs. The test pattern helps: the first 3 LEDs
              light green, the last 3 red, every 10th white.
            </p>
            <div className="row">
              <Select label="First LED near" value={layout.startCorner} options={CORNERS.map(c => ({ value: c, label: CORNER_LABELS[c] }))}
                onChange={startCorner => setLayout({ startCorner })} />
              <div className="field">
                <label>Direction (from the front)</label>
                <Segmented options={["cw", "ccw"] as const} value={layout.clockwise ? "cw" : "ccw"} labels={{ cw: "Clockwise", ccw: "Counter" }}
                  onChange={d => setLayout({ clockwise: d === "cw" })} />
              </div>
            </div>
            <div className="row four">
              <NumberField label="Top" value={layout.top} min={0} max={1024} onCommit={top => setLayout({ top })} />
              <NumberField label="Right" value={layout.right} min={0} max={1024} onCommit={right => setLayout({ right })} />
              <NumberField label="Bottom" value={layout.bottom} min={0} max={1024} onCommit={bottom => setLayout({ bottom })} />
              <NumberField label="Left" value={layout.left} min={0} max={1024} onCommit={left => setLayout({ left })} />
            </div>
            <div className="row">
              <NumberField label="First LED: slots from the corner" value={layout.offset} min={-1024} max={1024} unit="LED"
                onCommit={offset => setLayout({ offset })} />
              <div className="field">
                <label htmlFor="gaps">LEDs kept dark (numbers)</label>
                <input id="gaps" defaultValue={layout.gaps.join(", ")} key={layout.gaps.join(",")}
                  onBlur={e => {
                    const gaps = e.target.value.split(/[\s,;]+/).filter(Boolean).map(Number).filter(n => Number.isInteger(n) && n >= 0);
                    if (gaps.join(",") !== layout.gaps.join(",")) void setLayout({ gaps });
                  }} />
              </div>
            </div>
            <p className="dim">
              <b>First LED</b>: how many LED places from the start corner, in the strip's direction, the first LED
              sits; clicking the outline sets it for you. <b>LEDs kept dark</b>: LED numbers (0 is the first LED,
              comma separated) that never light and take no place around the screen, e.g. LEDs hidden behind the
              monitor stand.
            </p>
            <div className="row buttons">
              <button className="primary" onClick={() => setWizard(true)}>Start the layout wizard</button>
              <button onClick={() => run(() => bridge.call("showStripTest", { seconds: 60 }))}>Show test pattern (60 s)</button>
              <button onClick={() => run(() => bridge.call("showStripTest", { seconds: 0 }))}>End test</button>
            </div>
          </div>
        </div>
      </Panel>}

      <Panel title="Response" meta="SPEC 6.2">
        <div className="row">
          <NumberField label="Capture rate" value={capture.captureFps} min={1} max={60} unit="fps" onCommit={captureFps => setCapture({ captureFps })} />
          <NumberField label="Output rate" value={capture.outputFps} min={1} max={60} unit="fps" onCommit={outputFps => setCapture({ outputFps })} />
        </div>
        <div className="row">
          <NumberField label="Brighter in" value={capture.attackMilliseconds} min={0} max={5000} unit="ms" onCommit={v => setCapture({ attackMilliseconds: v })} />
          <NumberField label="Darker in" value={capture.releaseMilliseconds} min={0} max={5000} unit="ms" onCommit={v => setCapture({ releaseMilliseconds: v })} />
        </div>
        <div className="row">
          <NumberField label="To black in" value={capture.blackReleaseMilliseconds} min={0} max={5000} unit="ms" onCommit={v => setCapture({ blackReleaseMilliseconds: v })} />
          <NumberField label="Fade-in at start" value={capture.fadeInMilliseconds} min={0} max={10000} unit="ms" onCommit={v => setCapture({ fadeInMilliseconds: v })} />
        </div>
        <p className="dim">Time constants: after one, the strip has covered about two thirds of a change.</p>
      </Panel>

      <Panel title="Picture" meta="SPEC 6.2">
        <div className="row">
          <NumberField label="Edge band depth" value={capture.edgeDepthCm} min={0.5} max={20} step={0.1} unit="cm" onCommit={edgeDepthCm => setCapture({ edgeDepthCm })} />
          <NumberField label="Neighbour overlap" value={capture.segmentOverlapPercent} min={0} max={200} unit="%" onCommit={v => setCapture({ segmentOverlapPercent: v })} />
        </div>
        <div className="row">
          <NumberField label="Black threshold" value={capture.blackThreshold} min={0} max={255} unit="/255" onCommit={blackThreshold => setCapture({ blackThreshold })} />
          <NumberField label="Soft knee" value={capture.blackKnee} min={0} max={255} unit="/255" onCommit={blackKnee => setCapture({ blackKnee })} />
        </div>
        <div className="row">
          <NumberField label="Gamma" value={capture.gamma} min={0.5} max={4} step={0.1} onCommit={gamma => setCapture({ gamma })} />
          <NumberField label="Saturation" value={capture.saturation} min={0} max={3} step={0.05} onCommit={saturation => setCapture({ saturation })} />
        </div>
        <Toggle label="Follow the picture inside letterbox and pillarbox bars" checked={capture.letterboxDetection}
          onChange={letterboxDetection => setCapture({ letterboxDetection })} />
      </Panel>
    </div>
  );
}
