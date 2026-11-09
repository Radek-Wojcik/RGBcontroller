import { describe, expect, it } from "vitest";
import { moveTile, nextSize, orderTiles, parseLayout } from "./tileLayout";

describe("tile layout", () => {
  it("keeps the saved order, drops missing tiles and appends new ones", () => {
    expect(orderTiles(["a", "b", "c", "d"], ["c", "x", "a", "c"])).toEqual(["c", "a", "b", "d"]);
    expect(orderTiles(["a", "b"], [])).toEqual(["a", "b"]);
  });

  it("moves a tile to a new place", () => {
    expect(moveTile(["a", "b", "c", "d"], 0, 2)).toEqual(["b", "c", "a", "d"]);
    expect(moveTile(["a", "b", "c", "d"], 3, 1)).toEqual(["a", "d", "b", "c"]);
    expect(moveTile(["a", "b"], 0, 5)).toEqual(["a", "b"]);
  });

  it("cycles the size", () => {
    expect(nextSize("s")).toBe("m");
    expect(nextSize("l")).toBe("s");
  });

  it("ignores a broken saved layout", () => {
    expect(parseLayout("{nope")).toEqual({ order: [], sizes: {} });
    expect(parseLayout(null)).toEqual({ order: [], sizes: {} });
    expect(parseLayout(JSON.stringify({ order: ["a", 3], sizes: { a: "m", b: "huge" } }))).toEqual({ order: ["a"], sizes: { a: "m" } });
  });
});
