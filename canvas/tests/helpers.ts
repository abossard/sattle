import type { Edit, Entity, Patch, Rel, Version } from "../src/core/model";

export const text = (value: string) => ({ kind: "text" as const, value });
export const ent = (id: string, type = "Service", props: Record<string, string> = {}): Entity => ({
  id, type, props: Object.fromEntries(Object.entries(props).map(([k, v]) => [k, text(v)])),
});
export const rel = (id: string, source: string, target: string, type = "Calls", props: Record<string, string> = {}): Rel => ({
  id, source, target, type, props: Object.fromEntries(Object.entries(props).map(([k, v]) => [k, text(v)])),
});
export const ver = (revision: string, graph = "g"): Version => ({ tenant: "demo", graph, revision });
export const patch = (from: string, to: string, edits: Edit[]): Patch => ({
  from: ver(from), to: ver(to), projection: "p", profile: "extensionalFinite", edits, cause: { inputs: [], configurationVersion: "1" },
});
export const addN = (e: Entity): Edit => ({ kind: "entity", id: e.id, before: null, after: e });
export const delN = (e: Entity): Edit => ({ kind: "entity", id: e.id, before: e, after: null });
export const setN = (a: Entity, b: Entity): Edit => ({ kind: "entity", id: a.id, before: a, after: b });
export const addE = (r: Rel): Edit => ({ kind: "relationship", id: r.id, before: null, after: r });
export const delE = (r: Rel): Edit => ({ kind: "relationship", id: r.id, before: r, after: null });
