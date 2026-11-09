import type { Host } from "./bridge";
import type { AgentState, AppConfig, DeviceList } from "./types";
import fixture from "./mock-config.json";

/**
 * Stand-in for the WebView2 host when the page runs in a plain browser (npm run dev): answers the bridge methods
 * from an in-memory copy of a real config so the UI can be designed without the Service. Never used in the app.
 */
export function createMockHost(): Host {
  let config = structuredClone(fixture) as unknown as AppConfig;
  let revision = 1;
  let allOff = false;
  let sequence = 0;
  const listeners: ((m: string) => void)[] = [];
  const emit = (m: object) => setTimeout(() => listeners.forEach(l => l(JSON.stringify(m))), 0);

  const devices: DeviceList = {
    stripLedCount: config.strip.ledCount,
    devices: [
      { index: 0, name: "HyperX Predator RGB", type: 1, zones: [1, 2, 3, 4].map(i => ({ name: `HyperX Slot ${i}`, type: 1, ledCount: 5, resizable: false, matrixWidth: 0, matrixHeight: 0 })) },
      { index: 1, name: "Gigabyte GeForce RTX 3080 12G Gaming OC", type: 2, zones: [{ name: "GPU Zone", type: 0, ledCount: 1, resizable: false, matrixWidth: 0, matrixHeight: 0 }] },
      {
        index: 2, name: "ASUS TUF GAMING H670-PRO WIFI D4", type: 0, zones: [
          { name: "Aura Mainboard", type: 1, ledCount: 3, resizable: false, matrixWidth: 0, matrixHeight: 0 },
          ...[1, 2, 3].map(i => ({ name: `Aura Addressable ${i}`, type: 1, ledCount: 20, resizable: true, matrixWidth: 0, matrixHeight: 0 })),
        ],
      },
      { index: 3, name: "Razer Ornata Chroma V2", type: 5, zones: [{ name: "Keyboard", type: 2, ledCount: 132, resizable: false, matrixWidth: 22, matrixHeight: 6, matrix: Array.from({ length: 132 }, (_, i) => i) }] },
      { index: 4, name: "G502 HERO Gaming Mouse", type: 6, zones: [{ name: "Primary Zone", type: 0, ledCount: 1, resizable: false, matrixWidth: 0, matrixHeight: 0 }, { name: "Logo Zone", type: 0, ledCount: 1, resizable: false, matrixWidth: 0, matrixHeight: 0 }] },
    ],
  };

  const state = (): AgentState => {
    const profile = config.lighting.profiles.find(p => p.name === config.lighting.activeProfile)!;
    const a = profile.ambilight;
    return {
      sequence: ++sequence,
      status: { openRgb: "Ok", strip: "Ok", detail: "strip COM7 (stand-in data)", activeProfile: profile.name },
      profiles: config.lighting.profiles.map(p => p.name),
      activeProfile: profile.name,
      shownProfile: profile.name,
      stripMode: a.mode,
      stripBrightnessPercent: a.mode === "Solid" ? a.solidBrightnessPercent : a.brightnessPercent,
      captureWanted: a.mode === "Screen" && !allOff,
      allOff,
      settingsRevision: 1,
      configRevision: revision,
    };
  };

  const commit = (next: AppConfig) => {
    config = next;
    revision++;
    emit({ event: "state", data: state() });
  };

  const ok = { success: true, message: null };
  const methods: Record<string, (p: any) => unknown> = {
    getStatus: () => state().status,
    getState: () => state(),
    getProfiles: () => ({ profiles: state().profiles, activeProfile: config.lighting.activeProfile }),
    setActiveProfile: p => (commit({ ...config, lighting: { ...config.lighting, activeProfile: p.name } }), ok),
    stepProfile: () => ok,
    tuneGroup: () => ok,
    changeStrip: p => {
      const name = config.lighting.activeProfile;
      commit({
        ...config,
        lighting: {
          ...config.lighting,
          profiles: config.lighting.profiles.map(pr => {
            if (pr.name !== name) return pr;
            const mode = p.mode ?? pr.ambilight.mode;
            const solid = mode === "Solid";
            const brightness = p.brightnessPercent;
            return {
              ...pr,
              ambilight: {
                ...pr.ambilight,
                mode,
                brightnessPercent: !solid && brightness !== undefined ? brightness : pr.ambilight.brightnessPercent,
                solidBrightnessPercent: solid && brightness !== undefined ? brightness : pr.ambilight.solidBrightnessPercent,
              },
            };
          }),
        },
      });
      return ok;
    },
    setAllOff: p => ((allOff = p.off), emit({ event: "state", data: state() }), ok),
    showStripTest: () => ok,
    identify: () => ok,
    getAgentSettings: () => ({}),
    getConfig: () => ({ revision, config }),
    updateConfig: p => {
      if (p.expectedRevision !== revision) return { success: false, message: "The configuration changed meanwhile; reload it and try again.", revision, errors: [], restartRequired: false };
      const restart = JSON.stringify(p.config.strip.ledCount) !== JSON.stringify(config.strip.ledCount);
      commit(p.config);
      return { success: true, message: null, revision, errors: [], restartRequired: restart };
    },
    getDevices: () => devices,
    getDeviceColors: () => devices.devices.map(d => {
      const n = d.zones.reduce((sum, z) => sum + z.ledCount, 0);
      const bytes = new Uint8Array(n * 3);
      const t = Date.now() / 4000 + d.index / 5;
      for (let i = 0; i < n; i++) {
        const h = ((i / Math.max(1, n) + t) % 1) * Math.PI * 2;
        bytes.set([127 + 127 * Math.sin(h), 127 + 127 * Math.sin(h + 2.1), 127 + 127 * Math.sin(h + 4.2)], i * 3);
      }
      return { index: d.index, rgb: btoa(String.fromCharCode(...bytes)) };
    }),
    getStripColors: () => {
      // A slowly turning rainbow, so the live preview can be designed without the Service.
      const n = config.strip.ledCount;
      const bytes = new Uint8Array(n * 3);
      const t = Date.now() / 10000;
      for (let i = 0; i < n; i++) {
        const h = ((i / n + t) % 1) * 6;
        const f = h - Math.floor(h);
        const [r, g, b] = [[1, f, 0], [1 - f, 1, 0], [0, 1, f], [0, 1 - f, 1], [f, 0, 1], [1, 0, 1 - f]][Math.floor(h)];
        bytes.set([r * 255, g * 255, b * 255], i * 3);
      }
      return { rgb: btoa(String.fromCharCode(...bytes)) };
    },
  };

  setTimeout(() => {
    emit({ event: "connection", data: { connected: true, version: "dev", error: null } });
    emit({ event: "state", data: state() });
  }, 150);

  return {
    post(raw) {
      const { id, method, params } = JSON.parse(raw);
      const handler = methods[method];
      setTimeout(() => {
        listeners.forEach(l => l(JSON.stringify(handler ? { id, result: handler(params) } : { id, error: `Unknown method '${method}'.` })));
      }, 30);
    },
    onMessage: listener => listeners.push(listener),
  };
}
