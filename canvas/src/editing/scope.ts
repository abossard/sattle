// What a hole can be filled with: the definitions a projection can name and the outputs a step can read. Read from the catalog.
import { Catalog, moduleOfId } from "../core/catalog";
import type { ProjectionDoc, StepDoc } from "./doc";

export interface Option { value: string; label: string }

/** The modules a reference may point into: `scope` as the engine's palette says it (engine 3.21), for the step it is on. */
function scopeModules(cat: Catalog, doc: ProjectionDoc, scope: string | undefined, step: StepDoc | undefined): string[] | null {
  const withImports = (m: string) => modules(cat, moduleOfId(m));
  switch (scope) {
    case "outputOntology": { const out = cat.targets(doc.id, "outputOntology")[0]; return out ? withImports(out) : null; }
    case "inputGraph": {
      // the ontology the graph a step reads is typed by: the port it reads from expects it
      const from = step?.inputs.find((i) => i.kind === "graph" && i.from)?.from;
      const expects = from ? cat.targets(from, "expects")[0] : undefined;
      return expects ? withImports(expects) : null;
    }
    default: return null;
  }
}

/** What a reference of a step can point at: the definitions of its kind in the scope the palette gives it, all in scope otherwise. */
export function referenceCandidates(cat: Catalog, doc: ProjectionDoc, ref: { to: string; scope?: string }, step?: StepDoc): Option[] {
  const mods = scopeModules(cat, doc, ref.scope, step);
  const slot = ({ EntityType: "entity", RelationshipType: "rel", EquivalencePolicy: "equivalence" } as Record<string, string>)[ref.to];
  if (!mods || !slot) return candidates(cat, doc, ref.to, step);
  return mods.flatMap((m) => cat.children(m, slot).map((id) => ({ value: id, label: cat.typeLabel(id, doc.module) }))).sort((a, b) => a.label.localeCompare(b.label));
}

/** The module of the projection and the modules it imports. */
function modules(cat: Catalog, module: string): string[] {
  const own = `d:${module}`;
  const imported = cat.targets(own, "imports").map((release) => cat.targets(release, "releaseOf")[0] ?? release.replace(/@[^@/]*$/, ""));
  return [own, ...imported.filter((m) => cat.entity(m))];
}

const declared = (cat: Catalog, module: string, slot: string): Option[] =>
  modules(cat, module).flatMap((m) => cat.children(m, slot).map((id) => ({ value: id, label: cat.typeLabel(id, module) }))).sort((a, b) => a.label.localeCompare(b.label));

/** Definitions of one kernel type that the projection can refer to (`EntityType`, `RelationshipType`, `PropertyType`, `EquivalencePolicy`), or the outputs of its steps (`OutPort`). */
export function candidates(cat: Catalog, doc: ProjectionDoc, to: string, step?: StepDoc): Option[] {
  switch (to) {
    case "EntityType": return declared(cat, doc.module, "entity");
    case "RelationshipType": return declared(cat, doc.module, "rel");
    case "EquivalencePolicy": return declared(cat, doc.module, "equivalence");
    case "PropertyType": {
      // the properties of the type the step emits, when it says which, with those it inherits; all properties in scope otherwise
      const type = step?.refs.find((r) => r.target && (r.to === "EntityType" || r.to === "RelationshipType"))?.target;
      // its own properties and those of the types it extends (a WorkloadService has the key of every Component)
      const lineage: string[] = [];
      for (let t = type ?? undefined; t && !lineage.includes(t); t = cat.targets(t, "extends")[0]) lineage.push(t);
      const own = lineage.flatMap((t) => cat.targets(t, "hasProperty")).map((id) => ({ value: id, label: cat.name(id) }));
      return own.length ? own.sort((a, b) => a.label.localeCompare(b.label)) : declared(cat, doc.module, "prop");
    }
    case "OutPort": return doc.statements.flatMap((s) => s.steps).filter((s) => s.out).map((s) => ({ value: s.out!, label: s.name }));
    default: return [];
  }
}

/** The definition of the data type `name` in the ontology a graph input expects: declared there or in a module it imports. */
export function typeOfData(cat: Catalog, input: string, slot: "entity" | "rel", name: string): string | null {
  const ontology = cat.targets(input, "expects")[0];
  if (!ontology) return null;
  for (const m of modules(cat, moduleOfId(ontology))) { const hit = cat.children(m, slot).find((id) => cat.name(id) === name); if (hit) return hit; }
  return null;
}

/** The statements `name` reads, directly or through others. */
export function upstream(doc: ProjectionDoc, name: string): Set<string> {
  const reads = new Map(doc.statements.map((s) => [s.name, s.steps.flatMap((x) => x.inputs).filter((i) => i.from && !i.inChain).map((i) => i.fromName)]));
  const seen = new Set<string>(), todo = [...(reads.get(name) ?? [])];
  while (todo.length) { const n = todo.pop()!; if (!seen.has(n) && reads.has(n)) { seen.add(n); todo.push(...reads.get(n)!); } }
  return seen;
}

/** What an input of `kind` can read: the projection's inputs of that kind, and for rows the result of every statement that does not read `statement` itself. */
export function sources(doc: ProjectionDoc, kind: string, statement: string): Option[] {
  const inputs = doc.inputs.filter((i) => i.kind === kind).map((i) => ({ value: i.id, label: i.name }));
  const results = kind === "rows" ? doc.statements.filter((s) => !s.sink && s.name !== statement && !upstream(doc, s.name).has(statement)).map((s) => ({ value: s.steps[s.steps.length - 1].out!, label: s.name })) : [];
  return [...inputs, ...results];
}

/** A name no statement has yet: `rows1`, `rows2`, ... for a row set; for a sink the next number after the sinks there are (`emit.3`). */
export function freeName(doc: ProjectionDoc, sink: boolean): string {
  const taken = new Set(doc.statements.map((s) => s.name));
  const last = doc.statements.filter((s) => s.sink).at(-1)?.name ?? "";
  const base = sink ? (last.includes(".") ? last.slice(0, last.lastIndexOf(".") + 1) : "emit.") : "rows";
  for (let n = 1; ; n++) if (!taken.has(`${base}${n}`)) return `${base}${n}`;
}

export { moduleOfId };
