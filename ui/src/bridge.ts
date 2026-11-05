import type { AgentState, BridgeMethods, Connection } from "./types";
import { createMockHost } from "./mock";

/** What the page talks to: the WebView2 host in the app, or an in-memory stand-in in a plain browser (npm run dev). */
export interface Host {
  post(message: string): void;
  onMessage(listener: (message: string) => void): void;
}

export type BridgeEvent = { event: "connection"; data: Connection } | { event: "state"; data: AgentState };

interface WebView {
  postMessage(message: string): void;
  addEventListener(type: "message", listener: (e: { data: string }) => void): void;
}

function webViewHost(): Host | null {
  const webview = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;
  if (!webview) return null;
  return {
    post: m => webview.postMessage(m),
    onMessage: listener => webview.addEventListener("message", e => listener(e.data)),
  };
}

/**
 * Request/reply over the host's message channel (src/RGBcontroller.UI/Bridge.cs): every request carries an id and
 * the reply echoes it. Events from the Service ("connection", "state") go to the subscribers.
 */
export class Bridge {
  private nextId = 1;
  private readonly pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  private readonly listeners = new Set<(e: BridgeEvent) => void>();

  constructor(private readonly host: Host) {
    host.onMessage(raw => this.receive(raw));
  }

  call<M extends keyof BridgeMethods>(method: M, ...params: BridgeMethods[M][0] extends undefined ? [] : [BridgeMethods[M][0]]): Promise<BridgeMethods[M][1]> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve: v => resolve(v as BridgeMethods[M][1]), reject });
      this.host.post(JSON.stringify({ id, method, params: params[0] }));
    });
  }

  subscribe(listener: (e: BridgeEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private receive(raw: string) {
    let message: { id?: number; result?: unknown; error?: string; event?: string; data?: unknown };
    try {
      message = JSON.parse(raw);
    } catch {
      return;
    }

    if (message.id !== undefined && message.id !== null) {
      const waiting = this.pending.get(message.id);
      this.pending.delete(message.id);
      if (!waiting) return;
      if (message.error !== undefined) waiting.reject(new Error(message.error));
      else waiting.resolve(message.result);
    } else if (message.event === "connection" || message.event === "state") {
      for (const listener of this.listeners) listener(message as BridgeEvent);
    }
  }
}

/** True when the page runs inside RGBcontroller.UI; false in a plain browser with the stand-in data. */
export const insideApp = webViewHost() !== null;

/** Outside the app in a production build there is nothing to talk to; the page shows the Service as not connected. */
function detachedHost(): Host {
  return {
    post: () => {},
    onMessage: listener => setTimeout(() => listener(JSON.stringify({
      event: "connection", data: { connected: false, version: null, error: "Open this page from RGBcontroller." },
    })), 0),
  };
}

// The stand-in data exists only in dev builds (import.meta.env.DEV is false in production, so the bundler drops it).
export const bridge = new Bridge(webViewHost() ?? (import.meta.env.DEV ? createMockHost() : detachedHost()));
