import { useRef, useState } from "react";
import { deleteProfile, duplicateProfile, exportProfile, importProfile, renameProfile } from "../config";
import { useStore } from "../store";

/** Duplicate, rename, export and delete for one profile (SPEC 5.4). Names are checked by the Service as well. */
export function ProfileTools({ name }: { name: string }) {
  const { snapshot, editConfig } = useStore();
  const [renaming, setRenaming] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  if (!snapshot) return null;
  const profiles = snapshot.config.lighting.profiles;
  const taken = (n: string) => profiles.some(p => p.name.toLowerCase() === n.toLowerCase() && p.name.toLowerCase() !== name.toLowerCase());

  const commitRename = () => {
    const to = (renaming ?? "").trim();
    setRenaming(null);
    if (to && to !== name && !taken(to)) void editConfig(c => renameProfile(c, name, to));
  };

  const download = () => {
    const file = exportProfile(snapshot.config, name);
    if (!file) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(file, null, 2)], { type: "application/json" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = `${name.replace(/[^\w.-]+/g, "_")}.rgbprofile.json`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };

  if (renaming !== null) {
    return (
      <div className="row buttons">
        <input aria-label="New profile name" autoFocus maxLength={100} value={renaming} onChange={e => setRenaming(e.target.value)}
          onKeyDown={e => { if (e.key === "Enter") commitRename(); if (e.key === "Escape") setRenaming(null); }} />
        <button className="primary" disabled={!renaming.trim() || taken(renaming.trim())} onClick={commitRename}>Rename</button>
        <button onClick={() => setRenaming(null)}>Cancel</button>
        {taken((renaming ?? "").trim()) && <span className="dim">That name is taken.</span>}
      </div>
    );
  }

  return (
    <div className="row buttons">
      <button onClick={() => void editConfig(c => duplicateProfile(c, name).config)}>Duplicate</button>
      <button onClick={() => setRenaming(name)}>Rename</button>
      <button onClick={download}>Export</button>
      {confirmDelete
        ? (
          <>
            <button className="danger" onClick={() => { setConfirmDelete(false); void editConfig(c => deleteProfile(c, name)); }}>Really delete</button>
            <button onClick={() => setConfirmDelete(false)}>Keep</button>
          </>
        )
        : <button disabled={profiles.length <= 1} onClick={() => setConfirmDelete(true)}>Delete</button>}
    </div>
  );
}

/** A file picker that adds an exported profile (with the palettes it needs) to this setup. */
export function ImportProfile() {
  const { editConfig, notify } = useStore();
  const input = useRef<HTMLInputElement>(null);

  const read = async (file: File) => {
    let data: unknown;
    try {
      data = JSON.parse(await file.text());
    } catch {
      notify("error", "That file is not valid JSON.");
      return;
    }

    let problem: string | null = null;
    const ok = await editConfig(c => {
      const result = importProfile(c, data);
      if (typeof result === "string") {
        problem = result;
        return c;
      }

      return result.config;
    });
    if (problem) notify("error", problem);
    else if (ok) notify("info", `Imported ${file.name}.`);
  };

  return (
    <div className="row buttons">
      <input ref={input} type="file" accept=".json,application/json" hidden
        onChange={e => { const f = e.target.files?.[0]; e.target.value = ""; if (f) void read(f); }} />
      <button onClick={() => input.current?.click()}>Import a profile file</button>
    </div>
  );
}
