import { describe, expect, it } from "vitest";
import type { Column } from "../../src/editor/service";
import { columnsByStep, rowsByStep, stepRowsOf, type OperatorRows } from "../../src/editing/results";

const P = "d:acme.svc/projection:serviceDeps";
const column = (name: string, kind: string, more: Partial<Column> = {}): Column => ({ name, kind, type: null, unit: null, item: null, required: true, role: "data", temporality: null, period: null, ...more });
const op = (name: string, columns: [string, string][], cells: Record<string, string>[], rows = cells.length, truncated = false): OperatorRows => ({
  op: `${P}/op:${name}`, name, kind: "Map", columns: columns.map(([n, kind]) => column(n, kind)), rows: String(rows), weight: String(rows),
  sample: cells.map((c) => ({ weight: "1", cells: c })), truncated,
});

describe("the rows after a step", () => {
  it("shows an element by the name the match read from it, never by its id", () => {
    const o = op("services", [["s", "Entity"], ["s.name", "Text"], ["n", "Integer"]], [{ s: "e:abc123", "s.name": "web", n: "2" }, { s: "e:def456", "s.name": "api", n: "1" }]);
    expect(stepRowsOf(o)).toEqual({ columns: ["s", "n"], rows: [["web", "2"], ["api", "1"]], total: 2, complete: true });
  });
  it.each([
    ["an element with no name read keeps its column", [["s", "Entity"], ["n", "Integer"]] as [string, string][], ["s", "n"]],
    ["a property read besides the name is a column of its own", [["s", "Entity"], ["s.name", "Text"], ["s.cluster", "Text"]] as [string, string][], ["s", "s.cluster"]],
  ])("%s", (_t, columns, expected) => {
    expect(stepRowsOf(op("x", columns, [])).columns).toEqual(expected);
  });
  it("says when the sample is not all of the rows", () => {
    const o = op("many", [["n", "Integer"]], [{ n: "1" }], 40, true);
    expect(stepRowsOf(o)).toMatchObject({ total: 40, complete: false });
  });
  it("gives each step the operator that stands for it, and a step planned as several operators its last one", () => {
    const ops = [op("a", [["n", "Integer"]], [{ n: "1" }]), { ...op("lifted", [["k", "Text"]], [{ k: "x" }]), op: `${P}/op:dependsOn__edges` }, { ...op("lifted", [["k", "Text"]], [{ k: "y" }, { k: "z" }]), op: `${P}/op:dependsOn__windows` }, { ...op("other", [], []), op: `${P}/op:gone` }];
    const m = rowsByStep(ops, [`${P}/op:a`, `${P}/op:dependsOn`, `${P}/op:b`]);
    expect([...m.keys()]).toEqual([`${P}/op:a`, `${P}/op:dependsOn`]);
    expect(m.get(`${P}/op:dependsOn`)!.total).toBe(2);
  });
  it("says the columns after a step: an element by its type's name, an optional value with a mark, telemetry as such", () => {
    const m = columnsByStep([{ op: `${P}/op:a`, name: "a", kind: "Match", window: null, columns: [column("s", "Entity", { type: "d:sattle.infra/entity:Service" }), column("drops", "Integer", { required: false, role: "telemetry" })] }], [`${P}/op:a`, `${P}/op:b`], (id) => id.slice(id.lastIndexOf(":") + 1));
    expect([...m]).toEqual([[`${P}/op:a`, ["s: Service", "drops: Integer? (telemetry)"]]]);
  });
  it("never shows an element by its id: by the name the data gives it, else as an element; and a time as a date", () => {
    // as the engine answers a preview: column names only, the kinds from the draft's check
    const o = { ...op("dependsOn", [["src", ""], ["dst", ""], ["windowStart", ""], ["flows", ""]], [{ src: "e:2dy5qu5oq7r4m5z6tvdepbvv63", dst: "e:gyz33iui5m6rbp2b2bg3folwaj", windowStart: "1767261600000000000", flows: "30" }]) };
    const kinds = new Map([["src", "Entity"], ["dst", "Entity"], ["windowStart", "Instant"], ["flows", "Integer"]]);
    const label = (id: string) => (id === "e:2dy5qu5oq7r4m5z6tvdepbvv63" ? "web" : undefined);
    expect(stepRowsOf(o, { kinds, label }).rows).toEqual([["web", "(element)", "2026-01-01 10:00", "30"]]);
    // without kinds an id still is not shown: a column whose every cell is an element id is an element column
    expect(stepRowsOf(o).rows[0].slice(0, 2)).toEqual(["(element)", "(element)"]);
  });
});
