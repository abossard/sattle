// How the declared kinds of edge are read on a workload: what restates others, which compositions stack the parts and which stand beside them,
// where the elements beside go, which part is to blame, and the tree by dependency. A small workload, with every kind the engine declares.
import { describe, expect, it } from "vitest";
import { besidePositions, blamePath, kindAxes, restatingTypes } from "../src/app/edge-kinds";
import { buildTree, specForKind } from "../src/app/tree-model";
import { Graph } from "../src/core/graph";
import type { EdgeKind, TreeSpec } from "../src/source/presentation";
import type { GraphInfo } from "../src/source/types";
import { ont } from "./fixtures/engine-fake";
import { ent, rel, ver } from "./helpers";

const kinds: Record<string, EdgeKind> = { Contains: "composition", HostedOn: "composition", MemberOf: "composition", BackedBy: "composition", ConnectedThrough: "composition", DependsOn: "dependency", Exposes: "dependency", Affects: "dependency", TalksTo: "communication", Measures: "support" };
const ends: Record<string, [string[], string[]]> = {
  Contains: [["Workload", "Service", "Deployment"], ["Service", "Deployment", "Pod"]], HostedOn: [["Pod"], ["Node"]], MemberOf: [["Node", "Pool"], ["Pool", "Cluster"]],
  BackedBy: [["Service"], ["Resource"]], ConnectedThrough: [["Resource"], ["Resource"]], DependsOn: [["Service"], ["Resource", "Service"]], Exposes: [["Resource"], ["Service"]],
  Affects: [["Service", "Pod", "Node"], ["Workload", "Service", "Pod"]], TalksTo: [["Service"], ["Service", "Resource"]], Measures: [["Signal"], ["Service"]],
};
const ontology = { ...ont("o"), relationshipTypes: Object.entries(ends).map(([id, [s, t]]) => ({ id: { value: id }, sourceTypes: s.map((value) => ({ value })), targetTypes: t.map((value) => ({ value })), properties: [] })) };
const rows = ["Workload", "Service", "Deployment", "Pod"];
const tree: TreeSpec = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: {}, state: Object.fromEntries(rows.concat("Resource", "Node").map((t) => [t, { rolledUp: "state" }])), types: rows };
const info = { id: "w", role: "projection", descriptor: {}, ontology, inputs: [], tree, edgeKinds: kinds, presentation: [{ property: "state", roles: { Healthy: "ok", Degraded: "warning", Unhealthy: "danger" } }] } as unknown as GraphInfo;

const types: Record<string, string> = { shop: "Workload", web: "Service", api: "Service", database: "Service", webd: "Deployment", pod: "Pod", node: "Node", pool: "Pool", cluster: "Cluster", cache: "Resource", pg: "Resource", pe: "Resource", vnet: "Resource", apim: "Resource", sig: "Signal" };
const edges: [string, string, string][] = [
  ["Contains", "shop", "web"], ["Contains", "shop", "api"], ["Contains", "shop", "database"], ["Contains", "web", "webd"], ["Contains", "webd", "pod"],
  ["HostedOn", "pod", "node"], ["MemberOf", "node", "pool"], ["MemberOf", "pool", "cluster"],
  ["DependsOn", "web", "cache"], ["DependsOn", "web", "api"], ["DependsOn", "api", "database"], ["BackedBy", "database", "pg"], ["ConnectedThrough", "pg", "pe"], ["ConnectedThrough", "pe", "vnet"], ["Exposes", "apim", "api"],
  ["TalksTo", "web", "api"], ["Measures", "sig", "web"],
  ["Affects", "web", "shop"], ["Affects", "api", "web"], ["Affects", "node", "pod"],
];
const workload = (states: Record<string, string> = {}) => Graph.fromSnapshot({
  version: ver("1"),
  entities: Object.entries(types).map(([id, t]) => ent(id, t, { name: id, ...(states[id] ? { state: states[id] } : {}) })),
  relationships: edges.map(([t, a, b], i) => rel(`${t}-${i}`, a, b, t)),
});

describe("edges that restate others", () => {
  it("a type every edge of which runs beside an edge of three or more other types is a restatement; one beside a single other type is not", () => {
    expect([...restatingTypes(workload())]).toEqual(["Affects"]);
  });
});

describe("the two axes of a workload", () => {
  it("the hierarchy and the compositions toward what nothing depends on stack; dependencies and the compositions toward what is depended on stand beside", () => {
    const { vertical, horizontal } = kindAxes(info);
    expect([[...vertical].sort(), [...horizontal].sort()]).toEqual([["Contains", "HostedOn", "MemberOf"], ["Affects", "BackedBy", "ConnectedThrough", "DependsOn", "Exposes"]]);
  });
});

describe("where the elements beside the parts go", () => {
  const g = workload();
  // the parts as the layered layout placed them: services in one row, then deployment, pod, node, pool, cluster
  const at: Record<string, [number, number]> = { shop: [100, 0], web: [0, 100], api: [100, 100], database: [200, 100], webd: [0, 200], pod: [0, 300], node: [0, 400], pool: [0, 500], cluster: [0, 600] };
  const placed = (slot: number) => at[g.nodes[slot]!.id] ?? null;
  const moves = besidePositions(g, info, placed, () => true, { column: 100, row: 30 });
  const pos = (id: string) => moves.get(g.nodeSlot.get(id)!);
  it.each<[string, string, [number, number]]>([
    ["what a service needs is right of everything, in the service's row", "cache", [300, 100]],
    ["a database server backing a service, one column right", "pg", [300, 130]],
    ["its private endpoint one column further", "pe", [400, 100]],
    ["its network further still", "vnet", [500, 100]],
    ["a gateway that exposes a service is to the left", "apim", [-100, 100]],
  ])("%s", (_, id, xy) => expect(pos(id)).toEqual(xy));
  it("the parts and what only communicates or supports are not moved", () => {
    for (const id of ["shop", "web", "api", "database", "webd", "pod", "node", "sig"]) expect(pos(id), id).toBeUndefined();
  });
});

describe("which part is to blame", () => {
  it.each<[string, Record<string, string>, string[], string[]]>([
    ["the database server is down: everything above it through what needs it is on the way", { pg: "Unhealthy", database: "Unhealthy", api: "Unhealthy", web: "Unhealthy", shop: "Unhealthy", apim: "Unhealthy" }, ["pg"], ["api", "apim", "database", "pg", "shop", "web"]],
    ["one pod crash-loops and nothing above it is wrong", { pod: "Unhealthy" }, ["pod"], ["pod"]],
    ["a degraded cache under a degraded service", { cache: "Degraded", web: "Degraded", shop: "Degraded" }, ["cache"], ["cache", "shop", "web"]],
    ["nothing is wrong", {}, [], []],
  ])("%s", (_, states, blamed, path) => {
    const b = blamePath(workload(states), info);
    expect([[...b.blamed].sort(), [...b.nodes].sort()]).toEqual([blamed, path]);
  });
  it("the edges of the way are those between wrong elements, cause to effect", () => {
    const g = workload({ pg: "Unhealthy", database: "Unhealthy", api: "Unhealthy" });
    const b = blamePath(g, info);
    expect([...b.edges].map((id) => id.split("-")[0]).sort()).toEqual(["BackedBy", "DependsOn"]);
  });
  it("a type that restates others names no cause: a shop that a roll-up edge says the web affects does not shift the blame onto the web", () => {
    const g = workload({ web: "Unhealthy", shop: "Unhealthy" });
    expect([...blamePath(g, info, new Set(["Affects"])).blamed]).toEqual(["web"]);
  });
  it("a graph whose hierarchy declares no states blames nothing", () => {
    expect(blamePath(workload({ pg: "Unhealthy" }), { ...info, tree: { ...tree, state: {} } }).blamed.size).toBe(0);
  });
});

describe("the tree by dependency", () => {
  it("nests what each part needs under it, what that is made of under that, and a gateway under what it exposes", () => {
    const m = buildTree(workload(), specForKind(info, "dependency")!, []);
    const parent = (id: string) => m.nodes.get(id)?.parent ?? null;
    expect(Object.fromEntries(["web", "api", "cache", "database", "apim", "pg", "pe", "vnet"].map((id) => [id, parent(id)]))).toEqual({ web: null, api: "web", cache: "web", database: "api", apim: "api", pg: "database", pe: "pg", vnet: "pe" });
  });
});
