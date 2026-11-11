// What an operator still needs, by the editor's own knowledge of the palette: required slots without a child, required parameters without
// a value, references (the emitted type) that are not set, input ports without a wire. The engine reports an unfinished projection against
// the projection only; this leads to the card.
import type { Graph } from "../core/graph";
import { Catalog } from "../core/catalog";
import { valueText } from "../core/model";
import type { Palette } from "./palette";
import { refsOf } from "./refs";

export interface Incomplete { op: string; kind: string; name: string; missing: string[] }

export function incompleteOperators(g: Graph, palette: Palette, projection: string): Incomplete[] {
  const c = new Catalog(g);
  const wired = new Set([...g.relationships()].filter((r) => r.type === "wire").map((r) => r.target));
  const out: Incomplete[] = [];
  for (const op of c.children(projection, "op")) {
    const e = g.entity(op);
    const spec = palette.operators.find((o) => o.kind === e?.type);
    if (!e || !spec) continue;
    const missing: string[] = [];
    for (const s of spec.slots) {
      if (s.name === "port" || (s.multiplicity !== "1" && s.multiplicity !== "+")) continue;
      if (c.children(op, s.name).length === 0) missing.push(`${s.name}${s.multiplicity === "+" ? " (at least one)" : ""}`);
    }
    for (const p of spec.params) if (p.required && (!e.props[p.name] || valueText(e.props[p.name]) === "")) missing.push(`parameter ${p.name}`);
    for (const r of refsOf(spec.kind)) if (r.required && c.targets(op, r.rel).length === 0) missing.push(r.label.replace(/ \(.*/, "").toLowerCase());
    for (const port of c.children(op, "port")) {
      const pe = g.entity(port);
      if (pe && valueText(pe.props.direction) === "in" && !wired.has(port)) missing.push(`input ${valueText(pe.props.name)} is not connected`);
    }
    if (missing.length) out.push({ op, kind: spec.kind, name: c.name(op), missing });
  }
  return out;
}
