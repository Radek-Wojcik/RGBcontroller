import { useEffect, useState } from "react";
import { bridge, insideApp } from "./bridge";
import { Ambilight } from "./pages/Ambilight";
import { Dashboard } from "./pages/Dashboard";
import { Devices } from "./pages/Devices";
import { Hotkeys } from "./pages/Hotkeys";
import { Profiles } from "./pages/Profiles";
import { Settings, type Accent } from "./pages/Settings";
import { StoreProvider, useStore } from "./store";
import { StatusDot } from "./ui";

const PAGES = [
  { id: "dashboard", label: "Dashboard", code: "01" },
  { id: "devices", label: "Devices", code: "02" },
  { id: "ambilight", label: "Ambilight", code: "03" },
  { id: "profiles", label: "Profiles", code: "04" },
  { id: "hotkeys", label: "Hotkeys", code: "05" },
  { id: "settings", label: "Settings", code: "06" },
] as const;
type PageId = (typeof PAGES)[number]["id"];

function readSetting<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const value = localStorage.getItem(key) as T | null;
    return value && allowed.includes(value) ? value : fallback;
  } catch {
    return fallback;
  }
}

function writeSetting(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage blocked: the choice lasts until the window closes.
  }
}

export function App() {
  return (
    <StoreProvider>
      <Shell />
    </StoreProvider>
  );
}

function Shell() {
  const pageIds = PAGES.map(p => p.id);
  // ?page=ambilight opens a page directly (documentation screenshots); otherwise the last page is restored.
  const linked = new URLSearchParams(location.search).get("page") as PageId | null;
  const [page, setPage] = useState<PageId>(() => (linked && pageIds.includes(linked) ? linked : readSetting("page", pageIds, "dashboard")));
  const [accent, setAccent] = useState<Accent>(() => readSetting("accent", ["cyan", "magenta", "acid"] as const, "cyan"));
  useEffect(() => writeSetting("page", page), [page]);
  useEffect(() => {
    document.documentElement.dataset.accent = accent;
    writeSetting("accent", accent);
  }, [accent]);

  return (
    <div className="shell">
      <nav className="rail" aria-label="Pages">
        <div className="brand">
          <span className="mark" aria-hidden />
          <span>RGB<b>CONTROLLER</b></span>
        </div>
        {PAGES.map(p => (
          <button key={p.id} className={p.id === page ? "on" : ""} aria-current={p.id === page ? "page" : undefined} onClick={() => setPage(p.id)}>
            <span className="code">{p.code}</span>{p.label}
          </button>
        ))}
        <div className="rail-foot">{import.meta.env.DEV && !insideApp ? "stand-in data · npm run dev" : "v0.5.0"}</div>
      </nav>
      <div className="main">
        <TopBar title={PAGES.find(p => p.id === page)!.label} />
        <main>
          {page === "dashboard" && <Dashboard />}
          {page === "devices" && <Devices />}
          {page === "ambilight" && <Ambilight />}
          {page === "profiles" && <Profiles />}
          {page === "hotkeys" && <Hotkeys />}
          {page === "settings" && <Settings accent={accent} onAccent={setAccent} />}
        </main>
      </div>
      <Notices />
    </div>
  );
}

function TopBar({ title }: { title: string }) {
  const { connection, state, run } = useStore();
  return (
    <header className="topbar">
      <h1>{title}</h1>
      <div className="topbar-state">
        {connection && !connection.connected && <span className="offline">Service not connected: {connection.error}</span>}
        {state && (
          <>
            <span className="profile-chip"><span className="dim">profile</span> {state.activeProfile}
              {state.shownProfile !== state.activeProfile && <span className="dim"> (showing {state.shownProfile})</span>}</span>
            <StatusDot label="OpenRGB" state={state.status.openRgb} />
            <StatusDot label="Strip" state={state.status.strip} />
            <button className={`power ${state.allOff ? "is-off" : ""}`} onClick={() => run(() => bridge.call("setAllOff", { off: !state.allOff }))}>
              {state.allOff ? "Turn everything on" : "Everything off"}
            </button>
          </>
        )}
      </div>
    </header>
  );
}

function Notices() {
  const { notices, dismiss } = useStore();
  return (
    <div className="notices" role="status" aria-live="polite">
      {notices.map(n => (
        <div key={n.id} className={`notice ${n.kind}`}>
          <span>{n.text}</span>
          <button className="ghost" aria-label="Dismiss" onClick={() => dismiss(n.id)}>×</button>
        </div>
      ))}
    </div>
  );
}
