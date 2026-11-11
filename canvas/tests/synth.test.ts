import { describe, expect, it } from "vitest";
import { ESTATE_SIZES, estateOntology, estatePatches, generateEstate, ownershipTree, workloadDependencies } from "../src/core/synth";
import { Graph } from "../src/core/graph";
import { checkOntology } from "../src/core/validate";

describe("synthetic estate", () => {
  it.each(["small", "medium"])("is deterministic for the same seed (%s)", (size) => {
    const a = generateEstate(ESTATE_SIZES[size]).snapshot;
    const b = generateEstate(ESTATE_SIZES[size]).snapshot;
    expect(JSON.stringify(a)).toBe(JSON.stringify(b));
  });
  it("changes with the seed", () => {
    const a = generateEstate(ESTATE_SIZES.small).snapshot;
    const b = generateEstate({ ...ESTATE_SIZES.small, seed: 2 }).snapshot;
    expect(JSON.stringify(a)).not.toBe(JSON.stringify(b));
  });
  it("has no dangling edges and only declared types", () => {
    const { snapshot } = generateEstate(ESTATE_SIZES.medium);
    const g = Graph.fromSnapshot(snapshot);
    expect(g.unresolvedEdgeCount).toBe(0);
    const types = new Set(estateOntology.entityTypes.map((t) => t.id.value));
    expect(snapshot.entities.every((e) => types.has(e.type))).toBe(true);
    const rtypes = new Set(estateOntology.relationshipTypes.map((t) => t.id.value));
    expect(snapshot.relationships.every((r) => rtypes.has(r.type))).toBe(true);
  });
  it("ontology validates against the schema", () => expect(checkOntology(estateOntology).ok).toBe(true));
  it("100k preset is about 100k nodes and 300k edges", () => {
    const { snapshot } = generateEstate(ESTATE_SIZES["100k"]);
    expect(snapshot.entities.length).toBeGreaterThan(98_000);
    expect(snapshot.entities.length).toBeLessThan(112_000);
    expect(snapshot.relationships.length).toBeGreaterThan(270_000);
    expect(snapshot.relationships.length).toBeLessThan(330_000);
  });
  it("derives an ownership tree and workload dependencies sharing entity ids", () => {
    const est = generateEstate(ESTATE_SIZES.small);
    const tree = ownershipTree(est.snapshot);
    expect(tree.entities.every((e) => ["Subscription", "Cluster", "Namespace", "Workload"].includes(e.type))).toBe(true);
    expect(tree.relationships.length).toBe(tree.entities.length - tree.entities.filter((e) => e.type === "Subscription").length);
    const deps = workloadDependencies(est.snapshot, est.serviceOf);
    const ids = new Set(est.snapshot.entities.map((e) => e.id));
    expect(deps.snapshot.entities.every((e) => ids.has(e.id))).toBe(true);
    expect([...deps.sources.values()].every((s) => s.length > 0)).toBe(true);
  });
  it("patch stream is valid and deterministic", () => {
    const { snapshot } = generateEstate(ESTATE_SIZES.small);
    const run = () => [...estatePatches(snapshot, 40, 7)];
    const a = run();
    expect(JSON.stringify(a)).toBe(JSON.stringify(run()));
    const g = Graph.fromSnapshot(snapshot);
    for (const p of a) expect(g.apply(p).ok).toBe(true);
    expect(a.length).toBeGreaterThan(20);
  });
});
