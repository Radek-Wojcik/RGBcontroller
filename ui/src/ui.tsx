import { useEffect, useId, useState, type CSSProperties, type ReactNode } from "react";

// Small controls shared by the pages. Visuals live in styles.css.

export function Panel({ title, meta, tools, wide, tall, children, className = "" }: {
  title?: ReactNode; meta?: ReactNode; tools?: ReactNode; wide?: boolean; tall?: boolean; children: ReactNode; className?: string;
}) {
  return (
    <section className={`panel ${wide ? "wide" : ""} ${tall ? "tall" : ""} ${className}`}>
      <div className="panel-body">
        {(title || meta || tools) && (
          <header className="panel-head">
            <h2>{title}</h2>
            {meta && <span className="panel-meta">{meta}</span>}
            {tools}
          </header>
        )}
        {children}
      </div>
    </section>
  );
}

/** Slider with a live label; sends while dragging (callers throttle) and keeps its own value until the next prop change. */
export function Slider({ label, value, min, max, step = 1, unit = "", digits = 0, onChange, disabled }: {
  label: string; value: number; min: number; max: number; step?: number; unit?: string; digits?: number;
  onChange(value: number): void; disabled?: boolean;
}) {
  const id = useId();
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  const percent = ((local - min) / (max - min)) * 100;
  return (
    <div className="field slider">
      <label htmlFor={id}>{label}</label>
      <output htmlFor={id} className="num">{local.toFixed(digits)}{unit}</output>
      <input
        id={id} type="range" min={min} max={max} step={step} value={local} disabled={disabled}
        style={{ "--fill": `${percent}%` } as CSSProperties}
        onChange={e => {
          const v = Number(e.target.value);
          setLocal(v);
          onChange(v);
        }}
      />
    </div>
  );
}

export function Segmented<T extends string>({ options, value, onChange, labels, disabled }: {
  options: readonly T[]; value: T | undefined; onChange(value: T): void; labels?: Partial<Record<T, string>>; disabled?: boolean;
}) {
  return (
    <div className="segmented" role="radiogroup">
      {options.map(o => (
        <button key={o} role="radio" aria-checked={o === value} className={o === value ? "on" : ""} disabled={disabled} onClick={() => onChange(o)}>
          {labels?.[o] ?? o}
        </button>
      ))}
    </div>
  );
}

export function Select<T extends string>({ label, value, options, onChange, disabled }: {
  label: string; value: T; options: readonly { value: T; label?: string }[]; onChange(value: T): void; disabled?: boolean;
}) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <select id={id} value={value} disabled={disabled} onChange={e => onChange(e.target.value as T)}>
        {options.map(o => <option key={o.value} value={o.value}>{o.label ?? o.value}</option>)}
      </select>
    </div>
  );
}

export function Toggle({ label, checked, onChange, disabled, hint }: {
  label: ReactNode; checked: boolean; onChange(checked: boolean): void; disabled?: boolean; hint?: string;
}) {
  return (
    <label className={`toggle ${disabled ? "disabled" : ""}`} title={hint}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={e => onChange(e.target.checked)} />
      <span className="box" aria-hidden />
      <span>{label}</span>
    </label>
  );
}

/** Number input that commits on blur or Enter, so typing "1" on the way to "120" does not save "1". */
export function NumberField({ label, value, min, max, step = 1, unit, onCommit, disabled }: {
  label: string; value: number; min: number; max: number; step?: number; unit?: string; onCommit(value: number): void; disabled?: boolean;
}) {
  const id = useId();
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  const commit = () => {
    const v = Number(text.replace(",", "."));
    if (Number.isFinite(v) && v >= min && v <= max && v !== value) onCommit(v);
    else setText(String(value));
  };
  return (
    <div className="field number">
      <label htmlFor={id}>{label}</label>
      <span className="input-unit">
        <input id={id} inputMode="decimal" value={text} step={step} disabled={disabled}
          onChange={e => setText(e.target.value)} onBlur={commit} onKeyDown={e => e.key === "Enter" && (e.target as HTMLInputElement).blur()} />
        {unit && <span className="unit">{unit}</span>}
      </span>
    </div>
  );
}

export function StatusDot({ state, label }: { state: string; label: string }) {
  const kind = state === "Ok" ? "ok" : state === "Starting" || state === "Degraded" ? "warn" : "bad";
  return <span className={`status ${kind}`}><i aria-hidden />{label}<b>{state}</b></span>;
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>;
}
