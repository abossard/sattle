// The Outline and Wires notations' calculations (note 11, stage c) on the engine's outlines of the shipped `.kql` files: the outline's rows nest
// inside each other and name their list items exactly; the wires draw every input and statement once, without overlap, each wire left to
// right; the steps of one type stand together; a statement's read targets name a `let` that comes before it.
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import type { Analysis } from "../../src/editor/kql";
import { apply, set } from "../../src/editor/kql-edit";
import { outlineRows } from "../../src/editor/kql-outline";
import { CARD_H, targetsOf, wiresOf } from "../../src/editor/kql-wires";

const rec = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as { files: { projection: string; text: string; analyse: Analysis }[] };
const files = rec.files.map((f) => [f.projection, f.text, f.analyse] as const);
type Stmt = { kind: string; name?: string | null; operators?: unknown[]; reads?: { lets?: string[]; graphs?: string[]; rows?: string[] } };

describe("the outline's rows", () => {
  it.each(files)("%s: one row per id, each inside its parent, every operator of every statement shown", (_p, _text, a) => {
    const rows = outlineRows(a), by = new Map(rows.map((r) => [r.id, r]));
    expect(by.size).toBe(rows.length);
    for (const r of rows) {
      const parent = r.parent ? by.get(r.parent) : null;
      if (r.parent) expect(parent, r.id).toBeDefined();
      if (parent) { expect(r.place[0], r.id).toBeGreaterThanOrEqual(parent.place[0]); expect(r.place[1], r.id).toBeLessThanOrEqual(parent.place[1]); }
    }
    (a.statements as Stmt[]).forEach((s, i) => (s.operators ?? []).forEach((_, j) => expect(by.has(`statements.${i}.operators.${j}`), `${i}.${j}`).toBe(true)));
  });

  it.each(files)("%s: a row that is a list item is that item", (_p, _text, a) => {
    for (const r of outlineRows(a)) if (r.item) expect(r.item.list.items[r.item.i], r.id).toEqual(r.place);
  });
});

describe("the wires", () => {
  it.each(files)("%s: every input and statement is one card, no two overlap, every wire runs left to right", (_p, _text, a) => {
    const l = wiresOf(a);
    const stmts = a.statements as Stmt[], inputs = (a.inputs as unknown[] | undefined) ?? [];
    expect(l.cards.filter((c) => c.kind !== "input").map((c) => c.id).sort()).toEqual(stmts.map((_, i) => `statements.${i}`).sort());
    expect(l.cards.filter((c) => c.kind === "input")).toHaveLength(inputs.length);
    for (const c of l.cards) for (const d of l.cards) if (c !== d && c.x === d.x) expect(Math.abs(c.y - d.y), `${c.id} ${d.id}`).toBeGreaterThanOrEqual(CARD_H);
    const at = new Map(l.cards.map((c) => [c.id, c]));
    for (const w of l.wires) expect(at.get(w.from)!.x, `${w.from} > ${w.to}`).toBeLessThan(at.get(w.to)!.x);
  });

  it.each(files)("%s: a statement is wired from every input and earlier let it reads", (_p, _text, a) => {
    const l = wiresOf(a), stmts = a.statements as Stmt[];
    stmts.forEach((s, i) => {
      const from = new Set(l.wires.filter((w) => w.to === `statements.${i}`).map((w) => w.from));
      for (const g of [...(s.reads?.graphs ?? []), ...(s.reads?.rows ?? [])]) expect(from.has(`input:${g}`), `${i} reads ${g}`).toBe(true);
      for (const n of s.reads?.lets ?? []) {
        const k = stmts.slice(0, i).map((x, j) => (x.kind === "let" && x.name === n ? j : -1)).filter((j) => j >= 0).pop();
        if (k !== undefined) expect(from.has(`statements.${k}`), `${i} reads ${n}`).toBe(true);
      }
    });
  });

  it.each(files)("%s: the steps of one type stand together", (_p, _text, a) => {
    const steps = wiresOf(a).cards.filter((c) => c.kind !== "input" && c.kind !== "let");
    const types = steps.map((c) => c.title.slice(c.title.lastIndexOf(" ") + 1));
    for (const t of new Set(types)) { const at = types.flatMap((x, k) => (x === t ? [k] : [])); expect(at.at(-1)! - at[0], t).toBe(at.length - 1); }
  });

  it.each(files)("%s: a read target names an earlier let, and pointing it at another changes only that name", (_p, text, a) => {
    (a.statements as Stmt[]).forEach((_, i) => {
      for (const t of targetsOf(text, a, i)) {
        expect(text.slice(t.place[0], t.place[1])).toBe(t.name);
        expect(t.options).toContain(t.name);
        const other = t.options.find((o) => o !== t.name);
        if (other) expect(apply(text, set([t.place], other).changes)).toBe(text.slice(0, t.place[0]) + other + text.slice(t.place[1]));
      }
    });
  });
});
