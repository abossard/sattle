// C3 of #144: the canvas colors, counts, sorts and explains state values from presentation metadata only.
// Renaming the workshop's state words in the fixture changes the displayed text and nothing else.
import workshopText from "../../engine/standard/health/workshop.module.gkql.json?raw";
import { describe, expect, it } from "vitest";
import { legendOf, tally } from "../src/app/color-by";
import { roleColors } from "../src/app/roles";
import { buildTree, explainNode, visibleRows, type TreeSpec } from "../src/app/tree-model";
import { Graph } from "../src/core/graph";
import type { PresentationHint, Role } from "../src/source/presentation";
import { ent, rel, ver } from "./helpers";

type Module = { entities: { name: string; properties: { name: string; roles?: Record<string, Role> }[] }[] };
const workshop: Module = JSON.parse(workshopText);
const roles = workshop.entities.find((e) => e.name === "Instrument")!.properties.find((p) => p.name === "state")!.roles!;
const WORDS = Object.keys(roles);

const spec: TreeSpec = {
  hierarchy: { relationship: "Uses", childIsSource: false },
  edgeRoles: {},
  state: { Consumer: { rolledUp: "state" }, Instrument: { rolledUp: "state" } },
  types: ["Consumer", "Instrument"],
};
const STATES: [string, string][] = [["kiln", "Hot"], ["probe", "Ready"], ["fan", "No reading"], ["door", "Ready"], ["vent", "Smouldering"]];

/** What the canvas shows for the workshop fixture with its state words renamed by `rename`. */
function shown(rename: Record<string, string>) {
  const word = (v: string) => rename[v] ?? v;
  const hint: PresentationHint = { property: "state", roles: Object.fromEntries(Object.entries(roles).map(([v, r]) => [word(v), r])) };
  const g = Graph.fromSnapshot({ version: ver("1", "workshop"), entities: [ent("line", "Consumer", { state: word("Hot") }), ...STATES.map(([id, s]) => ent(id, "Instrument", { state: word(s) }))],
    relationships: STATES.map(([id]) => rel(`uses-${id}`, "line", id, "Uses")) });
  const t = tally({ property: "state", hint }, g.entities());
  const legend = legendOf(t.counts, t.missing, t.hint, roleColors("light"));
  const tree = buildTree(g, spec, [hint]);
  const rows = visibleRows(tree, { expanded: new Set(["line"]), onlyWrong: false, query: "", sort: "severity" }).rows;
  return {
    labels: legend.map((l) => l.value),
    legend: legend.map((l) => [l.role, l.color, l.count]),
    rows: rows.map((r) => [r.node.id, r.node.rolled?.role, r.node.severity]),
    drivers: explainNode(g, tree, "line")!.drivers.map((d) => d.id),
  };
}

describe("state words come from presentation metadata (C3)", () => {
  const asWritten = shown({});
  it("the fixture's roles order and color the legend", () => expect(asWritten.legend.map(([r]) => r)).toEqual(["ok", "warning", "unknown", "neutral"]));
  it.each<[string, Record<string, string>]>([
    ["renamed", { Hot: "Overheated", Ready: "Steady", "No reading": "Silent" }],
    ["swapped", { Hot: "Ready", Ready: "Hot", "No reading": "Unknown" }],
  ])("%s: only the displayed text changes", (_, rename) => {
    const now = shown(rename);
    expect(now.labels).toEqual(asWritten.labels.map((v) => rename[v] ?? v));
    expect({ ...now, labels: [] }).toEqual({ ...asWritten, labels: [] });
  });
  it("a label without a role renders neutral", () => {
    const colors = roleColors("light");
    expect(asWritten.legend[asWritten.labels.indexOf("Smouldering")]).toEqual(["neutral", colors.neutral, 1]);
    expect(asWritten.rows.find(([id]) => id === "vent")?.[1]).toBe("neutral");
  });
});

const code = (src: string) => src.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:"'`])\/\/.*$/gm, "$1");
const SRC = Object.entries(import.meta.glob("/src/**/*.{ts,tsx}", { query: "?raw", import: "default", eager: true }) as Record<string, string>).map(([f, text]) => [f, code(text)] as const);

it.each([...WORDS, "Healthy", "Degraded", "Unhealthy"])("no model-state literal %j in the canvas source", (w) => {
  const literal = new RegExp(`["'\`][^"'\`\\n]*\\b${w}\\b[^"'\`\\n]*["'\`]`);
  expect(SRC.filter(([, s]) => literal.test(s)).map(([f]) => f)).toEqual([]);
});
