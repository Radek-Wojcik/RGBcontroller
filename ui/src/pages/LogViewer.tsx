import { useCallback, useEffect, useRef, useState } from "react";
import { bridge } from "../bridge";
import { Panel, Toggle } from "../ui";

const LINES = 200;
const FOLLOW_MS = 3000;

/** The end of the Service's newest log. Read on demand; "Follow" refreshes every 3 s while the page is open. */
export function LogViewer() {
  const [follow, setFollow] = useState(false);
  const [tail, setTail] = useState<{ file: string; lines: string[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const box = useRef<HTMLPreElement>(null);

  const load = useCallback(async () => {
    try {
      setTail(await bridge.call("getLogTail", { component: "service", lines: LINES }));
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  useEffect(() => {
    void load();
    if (!follow) return;
    const timer = setInterval(() => void load(), FOLLOW_MS);
    return () => clearInterval(timer);
  }, [load, follow]);

  // Keep the newest lines in view.
  useEffect(() => {
    if (box.current) box.current.scrollTop = box.current.scrollHeight;
  }, [tail]);

  return (
    <Panel wide title="Service log" meta={<span className="num">{tail?.file || "no file yet"}</span>}>
      <div className="row buttons">
        <button onClick={() => void load()}>Refresh</button>
        <Toggle label="Follow" checked={follow} onChange={setFollow} />
      </div>
      {error && <p className="offline">{error}</p>}
      <pre ref={box} className="logbox" aria-label="Last lines of the Service log">
        {tail && tail.lines.length > 0 ? tail.lines.join("\n") : "Nothing logged yet."}
      </pre>
      <p className="dim">
        The last {LINES} lines. The full logs are in %ProgramData%\RGBcontroller\logs; the Agent and this window log under your user profile.
      </p>
    </Panel>
  );
}
