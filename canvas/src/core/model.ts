// Flat in-memory model for data graphs. Ids are unwrapped; property lists are records.
import type { GraphValue, OntologyRef } from "./wire-types";

export type Props = Record<string, GraphValue>;
export interface Entity { id: string; type: string; props: Props }
export interface Rel { id: string; source: string; target: string; type: string; props: Props }
export interface Version { tenant: string; graph: string; revision: string }

export type Edit =
  | { kind: "entity"; id: string; before: Entity | null; after: Entity | null }
  | { kind: "relationship"; id: string; before: Rel | null; after: Rel | null }
  | { kind: "virtualRegion"; id: string; before: unknown; after: unknown }
  | { kind: "ontology"; before: OntologyRef; after: OntologyRef };

export interface Patch {
  from: Version;
  to: Version;
  projection: string;
  profile: "extensionalFinite" | "symbolic";
  edits: Edit[];
  cause: { inputs: string[]; configurationVersion: string };
  /** commit logical time in seconds, when the source provides it (not part of the GraphPatch wire shape) */
  time?: number;
}

export interface Snapshot {
  version: Version;
  entities: Entity[];
  relationships: Rel[];
}

export type ElementKey = string; // "n:<id>" for entities, "e:<id>" for relationships
export const nodeKey = (id: string): ElementKey => `n:${id}`;
export const edgeKey = (id: string): ElementKey => `e:${id}`;

/** One element change in a patch or between two graphs. */
export type ChangeKind = "added" | "removed" | "changed";
export interface ElementChange<T> { kind: ChangeKind; before: T | null; after: T | null }
export interface ChangeSet {
  entities: Map<string, ElementChange<Entity>>;
  relationships: Map<string, ElementChange<Rel>>;
}
export const emptyChangeSet = (): ChangeSet => ({ entities: new Map(), relationships: new Map() });
export const changeCount = (c: ChangeSet): number => c.entities.size + c.relationships.size;

/** Display text of a property value. */
export function valueText(v: GraphValue): string {
  switch (v.kind) {
    case "text": case "integer": case "decimal": case "instant": return String(v.value);
    case "boolean": return v.value ? "true" : "false";
    case "null": return "null";
    case "entity": return v.value.value;
    case "list": return `[${v.items.map(valueText).join(", ")}]`;
  }
}

export function valueEquals(a: GraphValue, b: GraphValue): boolean {
  if (a.kind !== b.kind) return false;
  switch (a.kind) {
    case "null": return true;
    case "entity": return a.value.value === (b as typeof a).value.value;
    case "list": {
      const bi = (b as typeof a).items;
      return a.items.length === bi.length && a.items.every((x, i) => valueEquals(x, bi[i]));
    }
    default: return a.value === (b as typeof a).value;
  }
}

export function propsEqual(a: Props, b: Props): boolean {
  const ka = Object.keys(a);
  if (ka.length !== Object.keys(b).length) return false;
  for (const k of ka) {
    const o = b[k];
    if (!o || !valueEquals(a[k], o)) return false;
  }
  return true;
}

export const entityEquals = (a: Entity, b: Entity): boolean => a.id === b.id && a.type === b.type && propsEqual(a.props, b.props);
export const relEquals = (a: Rel, b: Rel): boolean =>
  a.id === b.id && a.type === b.type && a.source === b.source && a.target === b.target && propsEqual(a.props, b.props);

/** Short display label: name property if present, else id. */
export function labelOf(e: { id: string; props: Props }): string {
  let n = e.props.name ?? e.props.display;
  // a property whose id is qualified by its module (`acme.health.name`) when two modules use the same name
  if (!n) for (const k in e.props) if (k.endsWith(".name")) { n = e.props[k]; break; }
  return n && n.kind === "text" && n.value ? n.value : e.id;
}
