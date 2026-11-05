import { useStore } from "../store";
import { Empty, Panel } from "../ui";

const TYPES: Record<number, string> = { 0: "Motherboard", 1: "RAM", 2: "GPU", 3: "Cooler", 4: "LED strip", 5: "Keyboard", 6: "Mouse", 7: "Mousemat", 8: "Headset" };
const ZONE_TYPES: Record<number, string> = { 0: "single", 1: "linear", 2: "matrix" };

/** What OpenRGB detected and which group drives each zone. The mapping wizard (SPEC 7.4) builds on this page. */
export function Devices() {
  const { devices, snapshot } = useStore();
  if (!devices || !snapshot) return <Empty>Waiting for the Service...</Empty>;
  if (devices.devices.length === 0) return <Empty>No OpenRGB devices are attached right now (OpenRGB starting or rescanning).</Empty>;
  const groups = snapshot.config.lighting.groups;
  const groupOf = (device: string, zone: string) =>
    groups.find(g => g.members.some(m => m.device === device && (m.zone === null || m.zone === zone)))?.name;

  return (
    <div className="tiles">
      {devices.devices.map(d => (
        <Panel key={d.index} title={d.name} meta={<span className="num">#{d.index} · {TYPES[d.type] ?? `type ${d.type}`}</span>}>
          <table className="grid">
            <thead><tr><th>Zone</th><th>LEDs</th><th>Shape</th><th>Group</th></tr></thead>
            <tbody>
              {d.zones.map(z => (
                <tr key={z.name}>
                  <td>{z.name}</td>
                  <td className="num">{z.ledCount}{z.resizable && <span className="dim"> (resizable)</span>}</td>
                  <td>{ZONE_TYPES[z.type] ?? z.type}{z.type === 2 && <span className="num dim"> {z.matrixWidth}×{z.matrixHeight}</span>}</td>
                  <td>{groupOf(d.name, z.name) ?? <span className="dim">off</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>
      ))}
      <Panel title="Mapping wizard" meta="next">
        <p className="dim">
          The wizard lights each addressable header chain in turn so you can tell which component it is (pump ring,
          fan, strip), its LED count, direction and start, and checks the RAM and keyboard orientation.
        </p>
      </Panel>
    </div>
  );
}
