import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import { snapshotFromWire } from "../src/core/wire";
import { Catalog, moduleOntologyGraph, DIRECTION, DIRECTION_CLASSES, durationText, moduleOfId } from "../src/core/catalog";
import { checkDescriptor } from "../src/core/validate";
import type { GraphExtentWire } from "../src/core/wire-types";

const raw = import.meta.glob("/public/demo/engine/catalog.snapshot.json", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const file = JSON.parse(Object.values(raw)[0]) as { descriptor: unknown; extent: GraphExtentWire };
const catalog = () => {
  const d = checkDescriptor(file.descriptor);
  if (!d.ok) throw new Error(d.errors.join());
  return new Catalog(Graph.fromSnapshot(snapshotFromWire(d.value, file.extent)));
};

describe("catalog graph of the engine definitions", () => {
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
    expect(ps.map((p) => `${p.module}/${p.name}`)).toEqual(["acme.ops/ingest", "acme.svc/serviceDeps"]);
    const ingest = ps[0];
    expect(ingest.outputModule).toBe("acme.ops");
    expect(ingest.rules.map((r) => r.direction).sort()).toEqual(["bijective", "bijective", "readonly", "readonly", "readonly", "readonly", "readonly", "readonly", "readonly"]);
    expect(ingest.direction).toBe("bijective");
    expect(DIRECTION_CLASSES).toContain(ps[1].direction);
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
    expect(by("deps#0").lines).toEqual(expect.arrayContaining(["a: infra.Pod", "s: infra.Service"]));
    expect(by("deps#0").lines.some((l) => l.includes("-[c:infra.Calls]->"))).toBe(true);
    expect(by("health").lines.join(" ")).toContain("above 0.05");
    const ops = c.projectionGraph("d:acme.ops/projection:ingest");
    expect(ops.nodes.find((n) => n.id.endsWith("/op:known"))!.lines[0]).toBe("And(IsPresent(srcNamespace), IsPresent(srcPod), IsPresent(dstNamespace), IsPresent(dstPod))");
  });
  it("rule cards carry their direction class and govern emit operators", () => {
    const g = c.projectionGraph("d:acme.ops/projection:ingest");
    const rules = g.nodes.filter((n) => n.kind === "rule");
    expect(rules.length).toBe(9);
    expect(new Set(rules.map((r) => r.direction))).toEqual(new Set(["readonly", "bijective"]));
    expect(g.edges.filter((e) => e.fromPort === "governs").length).toBe(12);
    for (const cls of DIRECTION_CLASSES) expect(DIRECTION[cls].glyph.length).toBeGreaterThan(0);
  });
  it("lists the ontology of a module", () => {
    const o = c.ontology("d:acme.svc");
    expect(o.entityTypes.map((x) => c.name(x)).sort()).toEqual(["ServiceGroup", "ServiceNode"]);
    expect(o.relationshipTypes.map((x) => c.name(x)).sort()).toEqual(["Contains", "DependsOn"]);
    expect(o.identityDomains.length).toBe(1);
    expect(o.constraints.length).toBe(2);
  });
  it("maps a diagnostic focus deep inside an operator to its card", () => {
    const g = c.projectionGraph("d:acme.ops/projection:ingest");
    const cards = new Set(g.nodes.map((n) => n.id));
    expect(c.nearest("d:acme.ops/projection:ingest/op:known/predicate:#0/arg:#1/arg:#0", (id) => cards.has(id))).toBe("d:acme.ops/projection:ingest/op:known");
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
