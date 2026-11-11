// What an element is called on screen. The engine's element ids are hashes; they are for the Inspector, never a label.
// Order: the element's name; else the required text properties of its type in the ontology (the ones that make up its identity are required);
// else the type name with a short index.
import type { Entity } from "./model";
import { valueText } from "./model";
import type { OntologyDefinition } from "./wire-types";

/** The engine's minted element ids (`e:` or `n:` and a long base32 hash) and the like. Never shown as a label. */
export const INTERNAL_ID = /\b[a-z]{1,3}:[a-z0-9]{20,}\b/;
export const isInternalId = (s: string): boolean => INTERNAL_ID.test(s);

export type Namer = (e: Entity) => string;

const readable = (e: Entity, key: string): string => { const v = e.props[key]; const t = v ? valueText(v) : ""; return t && !isInternalId(t) ? t : ""; };
const isNameKey = (k: string): boolean => /name$/i.test(k.slice(k.lastIndexOf(".") + 1)) || k === "display";

/** The first name a property gives: `name` or `display`, then a qualified `module.name`, then any property whose last part ends in "name". */
export function usualName(e: Entity): string {
  for (const k of ["name", "display"]) { const t = readable(e, k); if (t) return t; }
  for (const k in e.props) if (k.endsWith(".name")) { const t = readable(e, k); if (t) return t; }
  for (const k in e.props) if (isNameKey(k)) { const t = readable(e, k); if (t) return t; }
  return "";
}

/** `entities` lists the graph's elements; the short index of a type counts them in slot order, once, and keeps the number it gave. */
/** Gives the graph the naming its ontology implies, unless it has one already. */
export function nameGraph(g: { namer: Namer | null; entities(): Iterable<Entity> }, ontology: OntologyDefinition | undefined): void {
  if (!g.namer) g.namer = makeNamer(ontology, () => g.entities());
}

export function makeNamer(ontology: OntologyDefinition | undefined, entities: () => Iterable<Entity>): Namer {
  const defs = new Map((ontology?.properties ?? []).map((p) => [p.id.value, p]));
  const required = new Map<string, string[]>();
  const requiredOf = (type: string): string[] => {
    let r = required.get(type);
    if (!r) {
      const declared = ontology?.entityTypes.find((t) => t.id.value === type)?.properties.map((p) => p.value) ?? [];
      r = declared.filter((id) => { const d = defs.get(id); return d?.valueKind === "text" && d.nullable === false; }).slice(0, 2);
      required.set(type, r);
    }
    return r;
  };
  const index = new Map<string, Map<string, number>>();
  const indexOf = (e: Entity): number => {
    let m = index.get(e.type);
    if (!m) { m = new Map(); for (const x of entities()) if (x.type === e.type) m.set(x.id, m.size + 1); index.set(e.type, m); }
    let n = m.get(e.id);
    if (n === undefined) { n = m.size + 1; m.set(e.id, n); }
    return n;
  };
  return (e) => {
    const name = usualName(e);
    if (name) return name;
    const parts = requiredOf(e.type).map((k) => readable(e, k)).filter(Boolean);
    if (parts.length) return parts.join(" / ");
    return isInternalId(e.id) ? `${e.type} ${indexOf(e)}` : e.id;
  };
}
