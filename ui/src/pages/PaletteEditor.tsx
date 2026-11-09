import { useState } from "react";
import { addPalette, deletePalette, paletteCss, paletteUses, renamePalette, updatePalette } from "../config";
import { useStore, useThrottled } from "../store";
import type { PaletteStop } from "../types";
import { NumberField, Panel, Select } from "../ui";

const MAX_STOPS = 32;

/** The position that splits the widest gap of a cyclic palette (the gap from the last stop back to the first counts). */
function widestGapMiddle(stops: PaletteStop[]): number {
  const sorted = [...stops].map(s => s.position).sort((a, b) => a - b);
  if (sorted.length === 0) return 0;
  let best = { size: -1, at: 0 };
  sorted.forEach((p, i) => {
    const next = i + 1 < sorted.length ? sorted[i + 1] : sorted[0] + 1;
    if (next - p > best.size) best = { size: next - p, at: (p + next) / 2 };
  });
  return Math.round((best.at % 1) * 100) / 100;
}

/** Palettes of the effects and Ambilight (SPEC 7.3): edit stops, rename (references follow), copy, delete unused ones. */
export function PaletteEditor() {
  const { snapshot, editConfig } = useStore();
  const [selected, setSelected] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const saveColor = useThrottled((a: { palette: string; index: number; color: string }) =>
    editConfig(c => updatePalette(c, a.palette, p => ({ ...p, stops: p.stops.map((s, i) => (i === a.index ? { ...s, color: a.color.toUpperCase() } : s)) }))), 250);
  if (!snapshot) return null;

  const config = snapshot.config;
  const palettes = config.lighting.palettes;
  const palette = palettes.find(p => p.name.toLowerCase() === (selected ?? "").toLowerCase()) ?? palettes[0];
  if (!palette) return null;
  const uses = paletteUses(config, palette.name);
  const setStops = (change: (stops: PaletteStop[]) => PaletteStop[]) =>
    editConfig(c => updatePalette(c, palette.name, p => ({ ...p, stops: change(p.stops) })));
  const taken = (n: string) => palettes.some(p => p.name.toLowerCase() === n.toLowerCase() && p.name.toLowerCase() !== palette.name.toLowerCase());

  const commitRename = () => {
    const to = (renaming ?? "").trim();
    setRenaming(null);
    if (!to || to.startsWith("#") || to === palette.name || taken(to)) return;
    setSelected(to);
    void editConfig(c => renamePalette(c, palette.name, to));
  };

  return (
    <Panel wide title="Palettes" meta={<span className="num">{palettes.length}</span>}>
      <div className="palette-bar" style={{ background: paletteCss(palettes, palette.name) }} aria-label={`Preview of ${palette.name}`} />
      <div className="row">
        <Select label="Palette" value={palette.name} options={palettes.map(p => ({ value: p.name }))} onChange={n => { setSelected(n); setRenaming(null); }} />
        <div className="field">
          <label htmlFor="palette-name">Name</label>
          <input id="palette-name" maxLength={100} value={renaming ?? palette.name} onFocus={() => setRenaming(palette.name)}
            onChange={e => setRenaming(e.target.value)} onBlur={commitRename}
            onKeyDown={e => { if (e.key === "Enter") e.currentTarget.blur(); if (e.key === "Escape") { setRenaming(null); e.currentTarget.blur(); } }} />
        </div>
      </div>
      {renaming !== null && (taken(renaming.trim()) || renaming.trim().startsWith("#")) &&
        <p className="dim">A palette name must be unused and cannot start with "#".</p>}

      <table className="grid palette-grid">
        <thead><tr><th>Color</th><th>Position (0 to 1)</th><th /></tr></thead>
        <tbody>
          {palette.stops.map((s, i) => (
            <tr key={i}>
              <td>
                <input type="color" aria-label={`Color of stop ${i + 1}`} value={s.color.toLowerCase()}
                  onChange={e => saveColor({ palette: palette.name, index: i, color: e.target.value })} />
              </td>
              <td>
                <NumberField label={`Position of stop ${i + 1}`} value={s.position} min={0} max={1} step={0.01}
                  onCommit={position => setStops(stops => stops.map((x, j) => (j === i ? { ...x, position } : x)))} />
              </td>
              <td>
                <button className="ghost" aria-label={`Remove stop ${i + 1}`} disabled={palette.stops.length <= 1}
                  onClick={() => setStops(stops => stops.filter((_, j) => j !== i))}>×</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="dim">
        Palettes are cyclic: the last color blends back into the first, so effects that run around (Rainbow, Radial) have no seam.
        {uses.length > 0 ? ` Used by ${uses.join(", ")}.` : " Not used by any profile."}
      </p>
      <div className="row buttons">
        <button disabled={palette.stops.length >= MAX_STOPS}
          onClick={() => setStops(stops => [...stops, { position: widestGapMiddle(stops), color: "#FFFFFF" }])}>Add a stop</button>
        <button onClick={() => { const r = addPalette(config); setSelected(r.name); void editConfig(c => addPalette(c).config); }}>New palette</button>
        <button onClick={() => { const r = addPalette(config, palette.name); setSelected(r.name); void editConfig(c => addPalette(c, palette.name).config); }}>Duplicate</button>
        <button disabled={uses.length > 0 || palettes.length <= 1} title={uses.length > 0 ? "A profile uses this palette" : undefined}
          onClick={() => { setSelected(null); void editConfig(c => deletePalette(c, palette.name) ?? c); }}>Delete</button>
      </div>
    </Panel>
  );
}
