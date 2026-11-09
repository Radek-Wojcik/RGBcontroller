import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { bridge } from "./bridge";
import type { AgentState, AppConfig, ConfigSnapshot, Connection, DeviceList } from "./types";

export interface Notice { id: number; kind: "error" | "info"; text: string }

interface Store {
  connection: Connection | null;
  state: AgentState | null;
  snapshot: ConfigSnapshot | null;
  devices: DeviceList | null;
  notices: Notice[];
  dismiss(id: number): void;
  notify(kind: Notice["kind"], text: string): void;
  /** Runs a command and reports a refusal or failure as a notice. */
  run(action: () => Promise<{ success: boolean; message: string | null }>): Promise<void>;
  /** Applies an edit to the latest config and sends it; on a revision conflict reloads and applies it once more. */
  editConfig(edit: (config: AppConfig) => AppConfig): Promise<boolean>;
}

const StoreContext = createContext<Store | null>(null);

export function useStore(): Store {
  const store = useContext(StoreContext);
  if (!store) throw new Error("useStore outside StoreProvider");
  return store;
}

export function StoreProvider({ children }: { children: ReactNode }) {
  const [connection, setConnection] = useState<Connection | null>(null);
  const [state, setState] = useState<AgentState | null>(null);
  const [snapshot, setSnapshot] = useState<ConfigSnapshot | null>(null);
  const [devices, setDevices] = useState<DeviceList | null>(null);
  const [notices, setNotices] = useState<Notice[]>([]);
  const snapshotRef = useRef<ConfigSnapshot | null>(null);
  const noticeId = useRef(0);

  const notify = useCallback((kind: Notice["kind"], text: string) => {
    const id = ++noticeId.current;
    setNotices(n => [...n.slice(-3), { id, kind, text }]);
    setTimeout(() => setNotices(n => n.filter(x => x.id !== id)), kind === "error" ? 9000 : 4000);
  }, []);

  const loadConfig = useCallback(async () => {
    try {
      const next = await bridge.call("getConfig");
      snapshotRef.current = next;
      setSnapshot(next);
      return next;
    } catch (e) {
      notify("error", (e as Error).message);
      return null;
    }
  }, [notify]);

  useEffect(() => bridge.subscribe(e => {
    if (e.event === "connection") {
      setConnection(e.data);
      if (e.data.connected) {
        void loadConfig();
        bridge.call("getDevices").then(setDevices, () => setDevices(null));
      }
    } else {
      setState(prev => (prev && prev.sequence > e.data.sequence ? prev : e.data));
      if (snapshotRef.current && e.data.configRevision !== snapshotRef.current.revision) void loadConfig();
    }
  }), [loadConfig]);

  const run = useCallback(async (action: () => Promise<{ success: boolean; message: string | null }>) => {
    try {
      const result = await action();
      if (!result.success) notify("error", result.message ?? "The Service refused the change.");
    } catch (e) {
      notify("error", (e as Error).message);
    }
  }, [notify]);

  const editConfig = useCallback(async (edit: (config: AppConfig) => AppConfig) => {
    for (let attempt = 0; attempt < 2; attempt++) {
      const current = snapshotRef.current ?? (await loadConfig());
      if (!current) return false;
      try {
        const result = await bridge.call("updateConfig", { expectedRevision: current.revision, config: edit(current.config) });
        if (result.success) {
          if (result.restartRequired) notify("info", "Saved. Some changes apply after the Service restarts.");
          await loadConfig();
          // A new header LED count is applied by OpenRGB a moment later; pick up the resized device list.
          for (const delay of [1500, 4000]) setTimeout(() => void bridge.call("getDevices").then(setDevices, () => {}), delay);
          return true;
        }

        if (result.errors.length > 0) {
          notify("error", result.errors.join(" "));
          return false;
        }

        await loadConfig(); // revision conflict: try once more on the fresh config
      } catch (e) {
        notify("error", (e as Error).message);
        return false;
      }
    }

    notify("error", "The configuration keeps changing; try again.");
    return false;
  }, [loadConfig, notify]);

  const value = useMemo<Store>(() => ({
    connection, state, snapshot, devices, notices, notify, run, editConfig,
    dismiss: id => setNotices(n => n.filter(x => x.id !== id)),
  }), [connection, state, snapshot, devices, notices, notify, run, editConfig]);

  return <StoreContext.Provider value={value}>{children}</StoreContext.Provider>;
}

/** Calls send at most every ms milliseconds while a slider moves, and once more with the last value. */
export function useThrottled<T>(send: (value: T) => void, ms = 120) {
  const last = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const sendRef = useRef(send);
  sendRef.current = send;
  return useCallback((value: T) => {
    clearTimeout(timer.current);
    const wait = last.current + ms - Date.now();
    if (wait <= 0) {
      last.current = Date.now();
      sendRef.current(value);
    } else {
      timer.current = setTimeout(() => {
        last.current = Date.now();
        sendRef.current(value);
      }, wait);
    }
  }, [ms]);
}
