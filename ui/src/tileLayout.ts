import { useCallback, useState } from "react";

export type TileSize = "s" | "m" | "l";

/** Dashboard arrangement: tile order and size per tile id. Saved per PC in the browser storage of the UI window. */
export interface TileLayout { order: string[]; sizes: Record<string, TileSize> }

const SIZES: TileSize[] = ["s", "m", "l"];
const KEY = "dashboard-layout";

/** Saved order first (tiles that no longer exist dropped), tiles the layout has not seen yet after them in their natural order. */
export function orderTiles(ids: string[], saved: string[]): string[] {
  const present = new Set(ids);
  const known = saved.filter((id, i) => present.has(id) && saved.indexOf(id) === i);
  return [...known, ...ids.filter(id => !known.includes(id))];
}

/** Moves the tile at `from` so that it ends up at index `to` (both in the current order). */
export function moveTile(order: string[], from: number, to: number): string[] {
  if (from === to || from < 0 || to < 0 || from >= order.length || to >= order.length) return order;
  const next = [...order];
  const [tile] = next.splice(from, 1);
  next.splice(to, 0, tile);
  return next;
}

export function nextSize(size: TileSize): TileSize {
  return SIZES[(SIZES.indexOf(size) + 1) % SIZES.length];
}

/** Reads a saved layout defensively: anything unexpected becomes an empty layout. */
export function parseLayout(text: string | null): TileLayout {
  try {
    const data = JSON.parse(text ?? "null") as Partial<TileLayout> | null;
    const order = Array.isArray(data?.order) ? data.order.filter((x): x is string => typeof x === "string") : [];
    const sizes: Record<string, TileSize> = {};
    for (const [id, size] of Object.entries(data?.sizes ?? {})) if (SIZES.includes(size as TileSize)) sizes[id] = size as TileSize;
    return { order, sizes };
  } catch {
    return { order: [], sizes: {} };
  }
}

function load(): TileLayout {
  try {
    return parseLayout(localStorage.getItem(KEY));
  } catch {
    return { order: [], sizes: {} };
  }
}

export function useTileLayout(): [TileLayout, (change: (l: TileLayout) => TileLayout) => void, () => void] {
  const [layout, setLayout] = useState<TileLayout>(load);
  const update = useCallback((change: (l: TileLayout) => TileLayout) => {
    setLayout(current => {
      const next = change(current);
      try {
        localStorage.setItem(KEY, JSON.stringify(next));
      } catch {
        // Storage can be blocked; the arrangement then lasts until the window closes.
      }

      return next;
    });
  }, []);
  const reset = useCallback(() => {
    try {
      localStorage.removeItem(KEY);
    } catch {
      // Ignored, as above.
    }

    setLayout({ order: [], sizes: {} });
  }, []);
  return [layout, update, reset];
}
