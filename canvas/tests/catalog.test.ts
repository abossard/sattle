import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import { snapshotFromWire } from "../src/core/wire";
import { Catalog, moduleOntologyGraph, DIRECTION, DIRECTION_CLASSES, durationText, moduleOfId } from "../src/core/catalog";
import { ontologyGraph } from "../src/core/ontology-graph";
import { checkDescriptor } from "../src/core/validate";
import type { GraphExtentWire } from "../src/core/wire-types";
import { coalesce, contains, ent as catalogEnt, text } from "../src/editor/patches";
import { ont } from "./fixtures/engine-fake";
import { ver } from "./helpers";

const raw = import.meta.glob("/public/demo/engine/catalog.snapshot.json", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const file = JSON.parse(Object.values(raw)[0]) as { descriptor: unknown; extent: GraphExtentWire };
const catalog = () => {
  const d = checkDescriptor(file.descriptor);
  if (!d.ok) throw new Error(d.errors.join());
  return new Catalog(Graph.fromSnapshot(snapshotFromWire(d.value, file.extent)));
};

describe("the catalog graph of the engine definitions lists modules, projections and ontologies and draws them as graphs", () => {
  const c = catalog();
  it("lists modules with versions and imports", () => {
    const m = c.modules();
    expect(m.map((x) => x.name)).toEqual(expect.arrayContaining(["acme.ops", "acme.svc", "sattle.infra", "sattle.kernel"]));
    const svc = m.find((x) => x.name === "acme.svc")!;
    expect(svc.version).toBe("1.0.0");
    expect(svc.digest.startsWith("sha256:")).toBe(true);
    expect(svc.imports.sort()).toEqual(["acme.ops", "sattle.infra"]);
  });
  it("finds projections with output module and the direction class of their rules", () => {
    const ps = c.projections();
    expect(ps.map((p) => `${p.module}/${p.name}`)).toEqual(["acme.biz/capabilityView", "acme.ops/authored", "acme.ops/ingest", "acme.readiness/readiness", "acme.report/readinessReport", "acme.svc/serviceDeps"]);
    const by = (name: string) => ps.find((p) => p.name === name)!;
    expect([by("ingest").outputModule, by("readinessReport").outputModule]).toEqual(["acme.ops", "sattle.doc"]);
    expect(by("authored").rules.map((r) => r.direction)).toEqual(["bijective", "bijective"]);
    expect(by("authored").direction).toBe("bijective");
    expect(new Set(by("ingest").rules.map((r) => r.direction))).toEqual(new Set(["readonly"]));
    expect(by("ingest").direction).toBe("readonly");
    for (const p of ps) expect(DIRECTION_CLASSES).toContain(p.direction);
  });
  it.each([["acme.ops/projection:ingest"], ["acme.svc/projection:serviceDeps"]])("projection graph %s is closed: every wire joins two cards through existing ports", (path) => {
    const g = c.projectionGraph(`d:${path}`);
    const ids = new Map(g.nodes.map((n) => [n.id, n]));
    expect(g.nodes.some((n) => n.kind === "operator")).toBe(true);
    for (const e of g.edges) {
      const a = ids.get(e.from), b = ids.get(e.to);
      expect(a, e.id).toBeDefined(); expect(b, e.id).toBeDefined();
      if (e.kind === "wire") { expect(a!.outputs, e.id).toContain(e.fromPort); expect(b!.inputs, e.id).toContain(e.toPort); }
    }
  });
  it("describes operators from their subtree", () => {
    const svc = c.projectionGraph("d:acme.svc/projection:serviceDeps");
    const by = (n: string) => svc.nodes.find((x) => x.id.endsWith(`/op:${n}`))!;
    expect(by("calls_1_1").lines).toEqual(["a: infra.Instance", "b: infra.Instance", "a -[c:infra.Calls]-> b"]);
    expect(by("dependsOn_1Fold_1").lines).toEqual(["key dst = dst", "key src = src", "n = count(dropRatio)", "v = max(dropRatio)", "policy above, below, enterAfter, exitAfter", 'state from "ok"', "streak from 0", "show state"]);
    const ops = c.projectionGraph("d:acme.ops/projection:ingest");
    expect(ops.nodes.find((n) => n.id.endsWith("/op:host_1_1"))!.lines[0]).toBe('Equal(Coalesce(metric, ""), "forward_rate")');
  });
  it.each([["ingest", 23, "readonly", 38], ["authored", 2, "bijective", 3]])("rule cards of %s carry their direction class and govern emit operators", (name, count, direction, governs) => {
    const g = c.projectionGraph(`d:acme.ops/projection:${name}`);
    const rules = g.nodes.filter((n) => n.kind === "rule");
    expect(rules.length).toBe(count);
    expect(new Set(rules.map((r) => r.direction))).toEqual(new Set([direction]));
    expect(g.edges.filter((e) => e.fromPort === "governs").length).toBe(governs);
    for (const cls of DIRECTION_CLASSES) expect(DIRECTION[cls].glyph.length).toBeGreaterThan(0);
  });
  it("lists the ontology of a module", () => {
    const o = c.ontology("d:acme.svc");
    expect(o.entityTypes.map((x) => c.name(x)).sort()).toEqual(["ServiceGroup", "ServiceNode"]);
    expect(o.relationshipTypes.map((x) => c.name(x)).sort()).toEqual(["Contains", "DependsOn"]);
    expect(o.identityDomains.length).toBe(1);
    expect(o.constraints.length).toBe(1);
  });
  it("maps a diagnostic focus deep inside an operator to its card", () => {
    const g = c.projectionGraph("d:acme.ops/projection:ingest");
    const cards = new Set(g.nodes.map((n) => n.id));
    expect(c.nearest("d:acme.ops/projection:ingest/op:calls_1_1/predicate:#0/arg:#1/arg:#0", (id) => cards.has(id))).toBe("d:acme.ops/projection:ingest/op:calls_1_1");
    expect(c.nearest("d:nowhere/x", (id) => cards.has(id))).toBeNull();
  });
  it("renders the ontology of a module as a graph, with imported types as nodes of their module", () => {
    const g = moduleOntologyGraph(c, "d:acme.ops");
    const names = new Map([...g.entities()].map((e) => [e.id, e]));
    expect([...g.entities()].filter((e) => e.type === "EntityType").map((e) => (e.props.name as { value: string }).value)).toEqual(expect.arrayContaining(["Capability", "infra.Service"]));
    const partOf = [...g.relationships()].filter((r) => r.type === "PartOf");
    expect(partOf.length).toBe(1);
    expect(names.get(partOf[0].source)!.props.name).toEqual({ kind: "text", value: "Capability" });
    expect([...g.relationships()].some((r) => r.type === "identifies")).toBe(true);
    expect(g.unresolvedEdgeCount).toBe(0);
  });
  it.each([["60000000000", "1m"], ["86400000000000", "1d"], ["3600000000000", "1h"], ["1500000000", "1.5s"], ["x", "x"]])("durationText(%s) = %s", (ns, t) => expect(durationText(ns)).toBe(t));
  it.each([["d:acme.svc/projection:serviceDeps/op:x", "acme.svc"], ["d:acme.ops@1.0.0", "acme.ops"], ["d:a|b|c", "a"]])("moduleOfId(%s) = %s", (id, m) => expect(moduleOfId(id)).toBe(m));
});

describe("an ontology is drawn as a graph of its types and how they relate", () => {
  const o = { ...ont("o"), relationshipTypes: [{ id: { value: "NoType" }, sourceTypes: [], targetTypes: [], properties: [] }, { id: { value: "Calls" }, sourceTypes: [{ value: "Pod" }], targetTypes: [{ value: "Pod" }], properties: [] }] } as never;
  it.each<[string, boolean, string[], string[]]>([
    ["the types and how they relate, without the placeholder NoType", false, ["Pod"], ["Calls"]],
    ["with the properties as nodes of their own when asked", true, ["Pod", "Property"], ["Calls", "has"]],
  ])("%s", (_, withProperties, types, relationships) => {
    const g = ontologyGraph(o, withProperties);
    expect([...new Set([...g.entities()].map((e) => e.type === "EntityType" ? String((e.props.name as { value: string }).value) : e.type))].sort()).toEqual(types.sort());
    expect([...new Set([...g.relationships()].map((r) => r.type))].sort()).toEqual(relationships.sort());
  });
});

describe("a relationship type with an empty source or target list is drawn from and to the node 'any type', as the engine reads an empty list as every type", () => {
  it("in the ontology picture of a graph", () => {
    const o = { ...ont("o"), relationshipTypes: [...ont("o").relationshipTypes, { id: { value: "Mentions" }, sourceTypes: [], targetTypes: [{ value: "Pod" }], properties: [] }, { id: { value: "Links" }, sourceTypes: [], targetTypes: [], properties: [] }] } as never;
    const g = ontologyGraph(o, false);
    const ends = (id: string) => String((g.entity(id)?.props.name as { value: string } | undefined)?.value);
    expect([...g.relationships()].map((r) => `${r.type}: ${ends(r.source)} -> ${ends(r.target)}`).sort(), "a type without a source or target list draws nothing").toEqual(["Links: any type -> any type", "Mentions: any type -> Pod"]);
  });
  it("in the ontology of a module in the editor, as the ontology picture draws it", () => {
    const m = "d:m", pod = "d:m/entity:Pod", links = "d:m/rel:Links", mentions = "d:m/rel:Mentions";
    const edits = coalesce([catalogEnt(m, "Module", { name: text("m") }), catalogEnt(pod, "EntityType", { name: text("Pod") }), catalogEnt(links, "RelationshipType", { name: text("Links") }), catalogEnt(mentions, "RelationshipType", { name: text("Mentions") }),
      contains(m, pod, "entity"), contains(m, links, "rel"), contains(m, mentions, "rel"), { kind: "relationship", id: `${mentions}|targetType|${pod}`, before: null, after: { id: `${mentions}|targetType|${pod}`, source: mentions, target: pod, type: "targetType", props: {} } } as never]);
    const g = Graph.fromSnapshot({ version: ver("1", "catalog"), entities: edits.filter((e) => e.kind === "entity").map((e) => e.after as never), relationships: edits.filter((e) => e.kind === "relationship").map((e) => e.after as never) });
    const o = moduleOntologyGraph(new Catalog(g), m);
    const name = (id: string) => String((o.entity(id)?.props.name as { value: string } | undefined)?.value);
    expect([...o.relationships()].map((r) => `${r.type}: ${name(r.source)} -> ${name(r.target)}`).sort(), "a type without a source or target list draws nothing").toEqual(["Links: any type -> any type", "Mentions: any type -> Pod"]);
  });
});
