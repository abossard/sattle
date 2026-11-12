import { describe, expect, it } from "vitest";
import { readPresentation, readTree } from "../src/source/presentation";

const id = (value: string) => ({ value });
const rel = (name: string, from: string[], to: string[], extra: object = {}) => ({ id: id(name), sourceTypes: from.map(id), targetTypes: to.map(id), properties: [], ...extra });
const ontology = (extra: object = {}) => ({
  schemaVersion: "1.0",
  entityTypes: [
    { id: id("Element"), properties: [id("state")], treeState: { own: "ownState", rolledUp: "state", severity: "severity", kind: "kind", counts: { Healthy: "healthy", Unhealthy: "unhealthy" } } },
    { id: id("Evidence"), properties: [id("name")] }, { id: id("Signal"), properties: [id("rule")] },
  ],
  relationshipTypes: [
    rel("Contains", ["Element"], ["Element"], { hierarchy: true }), rel("DependsOn", ["Element"], ["Element"], { hierarchy: false }),
    rel("EvidenceFor", ["Evidence"], ["Element"], { hierarchy: false }), rel("Measures", ["Signal"], ["Element"], { hierarchy: false }), rel("Loose", ["Evidence"], ["Signal"], { hierarchy: false }),
  ],
  properties: [],
  ...extra,
});

describe("the hierarchy marks of an ontology (engine 3.14)", () => {
  it("the relationship type marked hierarchy is the tree, parent is the source; the others are read from the structure of the ontology", () => {
    const { tree } = readTree(ontology());
    expect(tree).toEqual({
      hierarchy: { relationship: "Contains", childIsSource: false },
      edgeRoles: { DependsOn: "cross", EvidenceFor: "support", Measures: "support" },
      state: { Element: { own: "ownState", rolledUp: "state", severity: "severity", kind: "kind", counts: { Healthy: "healthy", Unhealthy: "unhealthy" } } },
      types: ["Element"],
    });
  });
  it.each([
    ["no type is marked", () => ontology({ relationshipTypes: [rel("Contains", ["Element"], ["Element"], { hierarchy: false })] })],
    ["the key is absent from every type", () => ontology({ relationshipTypes: [rel("Contains", ["Element"], ["Element"])] })],
    ["there is no relationship type", () => ontology({ relationshipTypes: [] })],
  ])("%s: not a tree", (_, make) => expect(readTree(make()).tree).toBeNull());
  it("removes the marks, so the contract schema (no extra fields) still accepts the ontology", () => {
    const { ontology: o } = readTree(ontology());
    expect(JSON.stringify(o)).not.toMatch(/treeState|hierarchy/);
    expect([(o as { entityTypes: unknown[] }).entityTypes.length, (o as { relationshipTypes: unknown[] }).relationshipTypes.length]).toEqual([3, 5]);
  });
  it("is read together with the value roles", () => {
    const r = readPresentation(ontology({ properties: [{ id: id("state"), presentation: [{ value: { kind: "text", value: "Healthy" }, role: "ok" }] }] }));
    expect([r.tree?.hierarchy.relationship, r.hints.map((h) => h.property)]).toEqual(["Contains", ["state"]]);
  });
  it("a graph that is not a hierarchy is read as before", () => expect(readPresentation({ properties: [] }).tree).toBeNull());
});

describe("what an ontology declares for viewers (engine 3.17): the kind of a relationship type and the label of an entity type", () => {
  const id = (v: string) => ({ value: v });
  const raw = (rel: Record<string, unknown>[], ent: Record<string, unknown>[] = []) => ({ schemaVersion: "1.0", properties: [], entityTypes: [{ id: id("NoType"), properties: [], label: null }, ...ent], relationshipTypes: rel });
  it.each([
    ["communication", "communication"], ["composition", "composition"], ["dependency", "dependency"], ["support", "support"],
  ])("reads the kind %s of a type", (kind, want) => expect(readPresentation(raw([{ id: id("R"), sourceTypes: [], targetTypes: [], properties: [], kind }])).kinds).toEqual({ R: want }));
  it.each([[null], [undefined], ["something else"], [3]])("a kind that is %j is no kind", (kind) => expect(readPresentation(raw([{ id: id("R"), sourceTypes: [], targetTypes: [], properties: [], kind }])).kinds).toEqual({}));
  it("reads the label of an entity type and none for a type that declares none", () => {
    const r = readPresentation(raw([], [{ id: id("Pod"), properties: [id("name")], label: id("name") }, { id: id("Blob"), properties: [], label: null }]));
    expect(r.labels).toEqual({ Pod: "name" });
  });
  it("removes both from the ontology: the contract schema forbids extra fields", () => {
    const r = readPresentation(raw([{ id: id("R"), sourceTypes: [], targetTypes: [], properties: [], kind: "composition", hierarchy: false }], [{ id: id("Pod"), properties: [], label: id("name") }]));
    expect(JSON.stringify(r.ontology)).not.toMatch(/"kind"|"label"|"hierarchy"/);
  });
});
