import { useState } from "react";
import { useStore } from "../store";
import type { HotkeyAction, HotkeyBinding } from "../types";
import { Empty, NumberField, Panel } from "../ui";

const ACTIONS: { value: HotkeyAction; label: string }[] = [
  { value: "StripSolidToggle", label: "Strip solid on/off" },
  { value: "StripBrightnessUp", label: "Strip brightness up" },
  { value: "StripBrightnessDown", label: "Strip brightness down" },
  { value: "CycleStripMode", label: "Next strip mode" },
  { value: "NextProfile", label: "Next profile" },
  { value: "PreviousProfile", label: "Previous profile" },
  { value: "Profile", label: "Switch to profile" },
  { value: "AllOffToggle", label: "Everything off/on" },
];

const MODIFIERS: Record<string, string> = { Control: "Ctrl", Alt: "Alt", Shift: "Shift", Meta: "Win" };

/** Turns a key press into "Ctrl+F9": exactly one modifier and one key (SPEC 7.2). */
export function gestureOf(e: { key: string; code: string; ctrlKey: boolean; altKey: boolean; shiftKey: boolean; metaKey: boolean }): string | null {
  if (e.key in MODIFIERS) return null;
  const modifiers = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win"].filter(Boolean);
  if (modifiers.length !== 1) return null;
  // Names as src/RGBcontroller.Core/Hotkeys/HotkeyGesture.cs spells them.
  const key = /^F\d{1,2}$/.test(e.key) ? e.key
    : e.key.startsWith("Arrow") ? e.key.slice(5)
    : e.code === "Space" ? "Space"
    : e.code.startsWith("Key") ? e.code.slice(3)
      : e.code.startsWith("Digit") ? e.code.slice(5)
        : e.code.startsWith("Numpad") ? `Num${e.code.slice(6)}`
          : e.key.length === 1 ? e.key.toUpperCase() : e.key;
  return `${modifiers[0]}+${key}`;
}

export function Hotkeys() {
  const { snapshot, editConfig } = useStore();
  const [recording, setRecording] = useState<number | null>(null);
  if (!snapshot) return <Empty>Waiting for the Service...</Empty>;
  const hotkeys = snapshot.config.hotkeys;
  const profiles = snapshot.config.lighting.profiles.map(p => p.name);
  const duplicates = new Set(hotkeys.bindings.map(b => b.keys.toLowerCase()).filter((k, i, all) => all.indexOf(k) !== i));

  const save = (bindings: HotkeyBinding[]) => editConfig(c => ({ ...c, hotkeys: { ...c.hotkeys, bindings } }));
  const change = (index: number, binding: Partial<HotkeyBinding>) =>
    save(hotkeys.bindings.map((b, i) => (i === index ? { ...b, ...binding } : b)));

  return (
    <div className="tiles">
      <Panel wide title="Global hotkeys" meta="one modifier + one key · RegisterHotKey, no keyboard hook">
        <table className="grid">
          <thead><tr><th>Keys</th><th>Action</th><th>Profile</th><th /></tr></thead>
          <tbody>
            {hotkeys.bindings.map((b, i) => (
              <tr key={i} className={duplicates.has(b.keys.toLowerCase()) ? "conflict" : ""}>
                <td>
                  <button className={`keycap ${recording === i ? "recording" : ""}`} onClick={() => setRecording(i)}
                    onBlur={() => setRecording(null)}
                    onKeyDown={e => {
                      if (recording !== i) return;
                      e.preventDefault();
                      if (e.key === "Escape") return setRecording(null);
                      const g = gestureOf(e);
                      if (g) {
                        setRecording(null);
                        void change(i, { keys: g });
                      }
                    }}>
                    {recording === i ? "press modifier + key" : b.keys}
                  </button>
                </td>
                <td>
                  <select value={b.action} aria-label="Action" onChange={e => change(i, { action: e.target.value as HotkeyAction, profile: e.target.value === "Profile" ? b.profile ?? profiles[0] : null })}>
                    {ACTIONS.map(a => <option key={a.value} value={a.value}>{a.label}</option>)}
                  </select>
                </td>
                <td>
                  {b.action === "Profile" && (
                    <select value={b.profile ?? ""} aria-label="Profile" onChange={e => change(i, { profile: e.target.value })}>
                      {profiles.map(p => <option key={p}>{p}</option>)}
                    </select>
                  )}
                </td>
                <td><button className="ghost" aria-label={`Remove ${b.keys}`} onClick={() => save(hotkeys.bindings.filter((_, j) => j !== i))}>Remove</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        {duplicates.size > 0 && <p className="warning">The same combination is used twice; only the first one works.</p>}
        <div className="row buttons">
          <button onClick={() => save([...hotkeys.bindings, { keys: "Ctrl+F8", action: "NextProfile", profile: null }])}>Add hotkey</button>
        </div>
        <p className="dim">Click a combination, then press the new one. A combination another program owns is reported by the tray icon.</p>
      </Panel>
      <Panel title="Brightness step">
        <NumberField label="Per press" value={hotkeys.brightnessStepPercent} min={1} max={50} unit="%"
          onCommit={v => editConfig(c => ({ ...c, hotkeys: { ...c.hotkeys, brightnessStepPercent: v } }))} />
      </Panel>
    </div>
  );
}
