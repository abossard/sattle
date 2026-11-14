// The editing module of note 11 (stage b) on the engine's outlines of the shipped `.kql` files (tests/fixtures/kql.json.gz): for every item
// of every list, removing it and adding it back, and moving it away and back, give the text byte for byte; items lie in text order without
// overlap, and between two items stand only the separator, white space and comments.
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import type { Analysis, Place } from "../../src/editor/kql";
import { add, apply, cardsOf, carried, itemAt, listsOf, mapPos, move, remove, set, type List } from "../../src/editor/kql-edit";

const rec = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as { files: { projection: string; text: string; analyse: Analysis }[] };
const files = rec.files.map((f) => [f.projection, f.text, listsOf(f.analyse)] as const);

/** The list after an edit: the other items where the changes put them, the edited one where the edit says it stands. */
const after = (list: List, gone: number, changes: Parameters<typeof mapPos>[1], moved?: { at: number; place: Place }): List => {
  const items = list.items.filter((_, j) => j !== gone).map(([s, e]) => [mapPos(s, changes, 1), mapPos(e, changes, -1)] as Place);
  if (moved) items.splice(moved.at, 0, moved.place);
  return { ...list, items };
};

describe("the lists of the outline", () => {
  it.each(files)("%s: items in text order, no overlap, only separators, white space and comments between them", (_p, text, lists) => {
    expect(lists.length).toBeGreaterThan(3);
    for (const l of lists) for (let i = 1; i < l.items.length; i++) {
      expect(l.items[i][0], `${l.id} ${i}`).toBeGreaterThanOrEqual(l.items[i - 1][1]);
      const gap = text.slice(l.items[i - 1][1], l.items[i][0]).replace(/\/\/[^\n]*/g, "");
      expect(gap, `${l.id} ${i}`).toMatch(new RegExp(`^\\s*\\${l.sep}?\\s*$`));
    }
  });
});

describe("edits as text", () => {
  it.each(files)("%s: removing each item and adding it back gives the text byte for byte", (_p, text, lists) => {
    let n = 0;
    for (const l of lists.filter((x) => x.items.length > 1)) l.items.forEach((_, i) => {
      const { body, joint } = carried(text, l, i);
      const r = remove(text, l, i);
      const without = apply(text, r.changes);
      expect(without.length, `${l.id} ${i}`).toBeLessThan(text.length);
      expect(apply(without, add(without, after(l, i, r.changes), i, body, joint).changes), `${l.id} ${i}`).toBe(text);
      n++;
    });
    expect(n).toBeGreaterThan(10);
  });

  it.each(files)("%s: moving each item away and back gives the text byte for byte", (_p, text, lists) => {
    for (const l of lists.filter((x) => x.items.length > 1)) l.items.forEach((_, i) => {
      const k = i === 0 ? l.items.length - 1 : 0;
      const m = move(text, l, i, k);
      const moved = apply(text, m.changes);
      expect(moved, `${l.id} ${i}`).not.toBe(text);
      expect(moved.slice(m.select![0], m.select![1]), `${l.id} ${i} stands where the edit says`).toBe(text.slice(l.items[i][0], l.items[i][1]));
      const there = after(l, i, m.changes, { at: k, place: m.select! });
      expect(apply(moved, move(moved, there, k, i).changes), `${l.id} ${i}`).toBe(text);
    });
  });

  it.each(files)("%s: setting every item to its own text changes nothing, and the innermost item at a place is found", (_p, text, lists) => {
    for (const l of lists) for (const p of l.items) {
      expect(apply(text, set([p], text.slice(p[0], p[1])).changes)).toBe(text);
      const hit = itemAt(lists, p[0])!;
      expect(hit.list.items[hit.index][0]).toBeLessThanOrEqual(p[0]);
      expect(hit.list.items[hit.index][1] - hit.list.items[hit.index][0]).toBeLessThanOrEqual(p[1] - p[0]);
    }
  });
});

describe("rows under the right card", () => {
  const preview = (JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as { preview: { op: string }[] }).preview;
  it.each(rec.files.map((f) => [f.projection, f.analyse] as const))("%s: every operator of the preview has its card", (projection, analysis) => {
    const dot = projection.lastIndexOf(".");
    const own = preview.filter((o) => o.op.startsWith(`d:${projection.slice(0, dot)}/projection:${projection.slice(dot + 1)}/`));
    const cards = cardsOf(analysis);
    expect(own.length).toBeGreaterThan(0);
    expect(own.filter((o) => !cards.has(o.op)).map((o) => o.op)).toEqual([]);
  });
});
