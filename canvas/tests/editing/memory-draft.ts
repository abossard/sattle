// A draft that lives in memory, for tests: it applies edits to a graph with the guards the engine applies, and stands in for the
// engine's skeleton of a new operator with what the palette says the operator has (its node and its ports). It checks nothing else:
// canonical text, diagnostics and previews come from a real engine.
import { Graph } from "../../src/core/graph";
import { snapshotFromWire } from "../../src/core/wire";
import { checkDescriptor } from "../../src/core/validate";
import type { GraphExtentWire } from "../../src/core/wire-types";
import type { Edit, Snapshot } from "../../src/core/model";
import { paramValue } from "../../src/editor/commands";
import { parsePalette, type Palette } from "../../src/editor/palette";
import { exprEdits } from "../../src/editor/expr";
import { contains, ent, text } from "../../src/editor/patches";
import type { DraftPort } from "../../src/editing/draft";
import type { Skeleton } from "../../src/editing/gestures";
import enginePalette from "../fixtures/palette.json";

export const palette: Palette = parsePalette(enginePalette);

const raw = import.meta.glob("/public/demo/engine/catalog.snapshot.json", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
/** The catalog the demo ships (acme.ops with `ingest`, acme.svc with `serviceDeps`), as the engine served it. */
export function demoCatalog(): Graph {
  const file = JSON.parse(Object.values(raw)[0]) as { descriptor: unknown; extent: GraphExtentWire };
  const d = checkDescriptor(file.descriptor);
  if (!d.ok) throw new Error(d.errors.join());
  return Graph.fromSnapshot(snapshotFromWire(d.value, file.extent));
}

const estate = import.meta.glob("/public/demo/estate/master.final.snapshot.json", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
/** The data the demo's `serviceDeps` reads: the synthetic estate's master graph (pods, services, calls). */
export function demoData(): Graph {
  const file = JSON.parse(Object.values(estate)[0]) as { descriptor: unknown; extent: GraphExtentWire };
  const d = checkDescriptor(file.descriptor);
  if (!d.ok) throw new Error(d.errors.join());
  return Graph.fromSnapshot(snapshotFromWire(d.value, file.extent));
}

/**
 * What the engine's skeleton does, from the palette: the node, its ports, and in every required slot a child so that the operator
 * prints (an empty literal, the assignment `x`, the aggregate `n = count()`, the path `x.y`), named as placeholders (engine 3.19).
 */
export const skeletonOf = (p: Palette): Skeleton => async (kind, container, name, params) => {
  const spec = p.operators.find((o) => o.kind === kind);
  if (!spec) throw new Error(`unknown operator ${kind}`);
  const id = `${container}/op:${name}`;
  const props = { name: text(name), ...Object.fromEntries(Object.entries(params).flatMap(([k, v]) => { const pp = spec.params.find((x) => x.name === k); const val = pp && paramValue(pp, v); return val && val.ok ? [[k, val.value]] : []; })) };
  const empty = { k: "lit" as const, kind: "text" as const, value: "" };
  const edits = [
    ent(id, kind, props), contains(container, id, "op"),
    ...spec.ports.flatMap((port) => [ent(`${id}/port:${port.name}`, "Port", { name: text(port.name), direction: text(port.direction), kind: text(port.kind) }), contains(id, `${id}/port:${port.name}`, "port")]),
  ];
  const placeholders: string[] = [];
  for (const slot of spec.slots.filter((s) => s.multiplicity === "1" || s.multiplicity === "+")) {
    const child = slot.children[0], at = `${id}/${slot.name}`;
    if (child === "Expr") { edits.push(...exprEdits(id, slot.name, 0, empty)); placeholders.push(`${at}:#0`); }
    else if (child === "Assign") { edits.push(ent(`${at}:x`, "Assign", { target: text("x") }), contains(id, `${at}:x`, slot.name), ...exprEdits(`${at}:x`, "value", 0, empty)); placeholders.push(`${at}:x`); }
    else if (child === "Aggregate") { edits.push(ent(`${at}:n`, "Aggregate", { target: text("n"), fn: text("count") }), contains(id, `${at}:n`, slot.name)); placeholders.push(`${at}:n`); }
    else if (child === "FieldRef") { edits.push(ent(`${at}:#0`, "FieldRef", { path: text("x.y") }), contains(id, `${at}:#0`, slot.name, 0)); placeholders.push(`${at}:#0`); }
  }
  return { edits, placeholders };
};

export interface MemoryDraft extends DraftPort { log: { label: string; edits: Edit[] }[] }
export function memoryDraft(graph: Graph, p: Palette = palette): MemoryDraft {
  const log: MemoryDraft["log"] = [];
  const placeholders = new Set<string>();
  return {
    graph, palette: p, diagnostics: [], placeholders, log, skeleton: skeletonOf(p),
    async apply(edits, label) {
      const v = graph.version ?? { tenant: "", graph: "catalog", revision: "0" };
      const r = graph.apply({ from: v, to: { ...v, revision: String(Number(v.revision) + 1) }, projection: "catalog", profile: "extensionalFinite", edits, cause: { inputs: [], configurationVersion: "0" } });
      if (!r.ok) return { ok: false, error: r.conflicts[0]?.message ?? "conflict" };
      log.push({ label, edits });
      // as the engine session does: what an edit removed or changed is no placeholder any more
      for (const e of edits) if (e.kind === "entity" && e.before !== null) placeholders.delete(e.id);
      return { ok: true };
    },
  };
}

/** The elements of a graph in a form two graphs can be compared by. */
export function shape(g: Graph): { entities: string[]; relationships: string[] } {
  const s: Snapshot = g.toSnapshot();
  const props = (p: Record<string, unknown>) => JSON.stringify(Object.keys(p).sort().map((k) => [k, p[k]]));
  return {
    entities: s.entities.map((e) => `${e.id} ${e.type} ${props(e.props)}`).sort(),
    relationships: s.relationships.map((r) => `${r.id} ${r.type} ${props(r.props)}`).sort(),
  };
}

/** Relationships of a graph that point at nothing: none in a draft that is whole. */
export const dangling = (g: Graph): string[] => [...g.relationships()].filter((r) => !g.entity(r.source) || !g.entity(r.target)).map((r) => r.id);
