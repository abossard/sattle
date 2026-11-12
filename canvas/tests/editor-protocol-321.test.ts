// Engine 3.21 in the editor's seam: references per operator in the palette, the columns after each operator from a check, the rows after each
// operator in a preview. Additions only: an older engine's answers read as before.
import { describe, expect, it } from "vitest";
import { parsePalette, parseReferences } from "../src/editor/palette";
import { parseColumns, parseOperatorRows, parseSchemas } from "../src/editor/service";
import { EditorSession } from "../src/editor/session";
import { fakeEngine } from "./fixtures/editor-fake";

const ofType = { name: "ofType", targets: ["EntityType"], required: true, multiplicity: "1", scope: "outputOntology", meaning: "the type of the node" };
const column = { name: "s", kind: "Entity", type: "d:sattle.infra/type:Service", unit: null, item: null, required: true, role: "data", temporality: null, period: null };

describe("references in the palette", () => {
  it.each<[string, unknown, number]>([
    ["an entry as the engine sends it", [ofType], 1],
    ["none", [], 0],
    ["not a list", "x", 0],
    ["an entry without a name or targets is left out", [{ targets: ["X"] }, { name: "n" }], 0],
  ])("%s", (_, raw, n) => expect(parseReferences(raw).length).toBe(n));
  it.each<[string, string]>([["1", "1"], ["+", "+"], ["?", "?"], ["many", "?"]])("multiplicity %s reads as %s", (m, want) => expect(parseReferences([{ ...ofType, multiplicity: m }])[0].multiplicity).toBe(want));
  it("the top level keyed by node type is read, an older palette has none, and operators keep theirs", () => {
    const p = parsePalette({ operators: [{ kind: "Match", references: [ofType] }], references: { PatternNode: [ofType], Rule: [] } });
    expect([p.references?.PatternNode[0].name, p.references?.Rule.length, p.operators[0].references?.[0].scope]).toEqual(["ofType", 0, "outputOntology"]);
    expect(parsePalette({ operators: [] }).references).toBeUndefined();
  });
});

describe("the columns after each operator", () => {
  it("a check answer carries them by projection id, in the order of the operators", () => {
    const s = parseSchemas({ "d:m/projection:p": [{ op: "d:m/projection:p/op:services", name: "services", kind: "Match", window: null, columns: [column] }, { op: "x", name: "w", kind: "Window", window: "1m", columns: [] }] })!;
    expect(s["d:m/projection:p"].map((o) => [o.name, o.window, o.columns.length])).toEqual([["services", null, 1], ["w", "1m", 0]]);
  });
  it("an older engine sends none", () => expect(parseSchemas(undefined)).toBeUndefined());
  it("a column keeps what it says and nulls what it does not", () => expect(parseColumns([{ name: "v", kind: "Decimal", unit: "ms", role: "telemetry", temporality: "gauge", period: "1m" }])[0]).toEqual({ name: "v", kind: "Decimal", type: null, unit: "ms", item: null, required: false, role: "telemetry", temporality: "gauge", period: "1m" }));
  it("the session keeps the last ones beside its diagnostics", async () => {
    const eng = fakeEngine();
    const schemas = { "d:m/projection:p": [{ op: "o", name: "services", kind: "Match", window: null, columns: [] }] };
    (eng.svc as unknown as { check: () => Promise<unknown> }).check = async () => ({ revision: "0", ok: true, diagnostics: [], modules: {}, schemas });
    const s = await EditorSession.open(eng.svc);
    expect(s.schemas).toEqual(schemas);
  });
});

describe("the rows after each operator in a preview", () => {
  it("are read with their sample, counts as text and the cells as text", () => {
    const [o] = parseOperatorRows([{ op: "o", name: "services", kind: "Match", columns: [column], rows: 12, weight: "12", sample: [{ weight: "1", cells: { s: "e:1", "s.name": "web" } }], truncated: false }]);
    expect([o.rows, o.weight, o.sample[0].cells["s.name"], o.truncated, o.columns[0].type]).toEqual(["12", "12", "web", false, "d:sattle.infra/type:Service"]);
  });
  it("no list is no rows", () => expect(parseOperatorRows(undefined)).toEqual([]));
  it("columns named by their names alone, as the live engine sends them in a preview, keep their names", () => {
    const [o] = parseOperatorRows([{ op: "d:acme.svc/projection:serviceDeps/op:dependsOn__edges", columns: ["source", "target", "edge"], rows: "3", sample: [] }]);
    expect(o.columns.map((c) => [c.name, c.kind, c.required])).toEqual([["source", "", true], ["target", "", true], ["edge", "", true]]);
  });
});
