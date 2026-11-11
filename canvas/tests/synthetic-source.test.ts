import { describe, expect, it } from "vitest";
import { SyntheticSource, dependenciesDefinition, dependenciesOntology, ownershipDefinition, ownershipOntology } from "../src/source/synthetic";
import { estateOntology } from "../src/core/synth";
import { checkDescriptor, checkOntology, checkProjection } from "../src/core/validate";
import { checkDefinition } from "../src/core/projection/typecheck";
import { Graph } from "../src/core/graph";

describe("synthetic source definitions", () => {
  it.each([
    ["ownership", ownershipDefinition(), ownershipOntology],
    ["dependencies", dependenciesDefinition(), dependenciesOntology],
  ])("%s definition is schema valid and type checks against the estate ontology", (_n, def, out) => {
    expect(checkProjection(def).ok).toBe(true);
    expect(checkOntology(out).ok).toBe(true);
    const errors = checkDefinition(def, { output: out, inputs: { estate: estateOntology } }).diagnostics.filter((d) => d.severity === "error");
    expect(errors.map((e) => e.message)).toEqual([]);
  });
});

describe("SyntheticSource", () => {
  it("serves snapshots, descriptors and a valid patch stream", async () => {
    const s = new SyntheticSource("small", 30);
    const info = await s.open();
    expect(info.graphs.map((g) => g.id)).toEqual(["master", "ownership", "dependencies"]);
    for (const g of info.graphs) expect(checkDescriptor(g.descriptor).ok).toBe(true);
    const g = Graph.fromSnapshot(await s.snapshot("master"));
    const patches: number[] = [];
    s.subscribe("master", (e) => { if (e.type === "patch") { expect(g.apply(e.patch).ok).toBe(true); patches.push(1); } });
    await new Promise((r) => setTimeout(r, 10));
    expect(patches.length).toBeGreaterThan(10);
  });
  it("correspondences point at existing master elements", async () => {
    const s = new SyntheticSource("small", 0);
    const master = Graph.fromSnapshot(await s.snapshot("master"));
    for (const gname of ["ownership", "dependencies"]) {
      for (const c of await s.correspondences(gname)) for (const src of c.sources) expect(master.rel(src.key.slice(2))).toBeDefined();
    }
  });
  it("derived graphs carry their own telemetry, with the same incident as the master", async () => {
    const s = new SyntheticSource("small", 0);
    await s.open();
    const feed = s.telemetry!;
    for (const g of ["master", "ownership", "dependencies"]) {
      const { batches } = await feed.fetch(g, 25);
      expect(batches.map((b) => `${b.target}:${b.property}`).sort()).toEqual(["edge:errorRate", "edge:latencyMs", "edge:requests", "node:cpu", "node:memory"]);
      const snap = await s.snapshot(g);
      const cpu = batches.find((b) => b.property === "cpu")!.values;
      expect(cpu.length).toBe(snap.entities.length);
    }
    const dep = await s.snapshot("dependencies");
    const { batches } = await feed.fetch("dependencies", 25);
    const err = batches.find((b) => b.property === "errorRate")!.values;
    const hit = dep.relationships.map((r, i) => (/-0-0-/.test(r.source + r.target) ? err[i] : null)).filter((v): v is number => v !== null);
    const rest = dep.relationships.map((r, i) => (/-0-0-/.test(r.source + r.target) ? null : err[i])).filter((v): v is number => v !== null);
    expect(Math.max(...hit)).toBeGreaterThan(0.1);
    expect(Math.max(...rest)).toBeLessThan(0.02);
  });
});
