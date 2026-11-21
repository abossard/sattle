import { describe, expect, it } from "vitest";
import { isReferenceType, stubEntity } from "../src/core/reference";
import { Workspace } from "../src/app/workspace";
import { darkTheme } from "../src/render/theme";
import type { GraphInfo, GraphSource, Reference, StreamEvent } from "../src/source/types";
import { descriptor, ont } from "./fixtures/engine-fake";
import { ent, rel, ver } from "./helpers";

const ref = (over: Partial<Reference>): Reference => ({ id: "e:1", state: "absent", graph: null, entityType: "AzureResource", identity: { domain: "azure", key: { resourceId: "/subscriptions/s/resourceGroups/g/providers/x/aks-prod" } }, uses: [{ relationshipType: "Concerns", end: "target", count: 3 }], ...over });
const prop = (r: ReturnType<typeof stubEntity>, k: string) => (r.props[k] as { value: string } | undefined)?.value;

describe("the stub of a reference", () => {
  it.each([
    ["is typed as a reference to the entity type", ref({}), "AzureResource (reference)"],
    ["without an entity type it is a plain entity reference", ref({ entityType: "" }), "entity (reference)"],
  ])("%s", (_, r, type) => { const s = stubEntity("e:1", r); expect([s.type, isReferenceType(s.type)]).toEqual([type, true]); });

  it.each([
    ["the last segment of a path-like key names it", ref({}), "◌ aks-prod"],
    ["a long key part is shortened", ref({ identity: { domain: "d", key: { k: "x".repeat(60) } } }), `◌ ${"x".repeat(27)}…`],
    ["without an identity it says which kind of entity is missing", ref({ identity: null }), "◌ unresolved AzureResource"],
    ["several key components are joined", ref({ identity: { domain: "d", key: { cluster: "c1", name: "web" } } }), "◌ c1 / web"],
  ])("%s", (_, r, name) => expect(prop(stubEntity("e:1", r), "name")).toBe(name));

  it.each([
    ["absent", ref({ state: "absent" }), /No graph holds this entity/],
    ["in another graph", ref({ state: "inOtherGraph", graph: "master" }), /in graph master/],
  ])("says why it is missing: %s", (_, r, reason) => expect(prop(stubEntity("e:1", r), "reason")).toMatch(reason));

  it("lists the identity key components and what uses the endpoint", () => {
    const s = stubEntity("e:1", ref({}));
    expect([prop(s, "identityDomain"), prop(s, "key.resourceId")?.endsWith("aks-prod"), prop(s, "usedBy")]).toEqual(["azure", true, "Concerns (target) × 3"]);
  });

  it("without information from the engine it still says what is known", () => {
    const s = stubEntity("e:abcdefghijklmnopqrstuvwxyz");
    expect([s.type, prop(s, "name"), isReferenceType(s.type)]).toEqual(["reference", "◌ unresolved reference", true]);
  });
});

it("follows the declared signal endpoint when its identity is also a master meter", async () => {
  const meter = "e:1", signalUse = { relationshipType: "CpuFromSignal", end: "target", count: 1 } as const;
  const entry = ref({ state: "inOtherGraph", graph: "master", entityType: "Meter", uses: [signalUse] });
  const ontology = {
    ...ont("o"), relationshipTypes: [
      { id: { value: "CpuFromSignal" }, sourceTypes: [{ value: "Check" }], targetTypes: [{ value: "Signal" }], properties: [] },
      { id: { value: "MeterOf" }, sourceTypes: [{ value: "Meter" }], targetTypes: [], properties: [] },
    ],
  };
  const graphs = ["master", "signals", "health"].map((id) => ({ id, role: id === "master" ? "master" : "projection",
    inputs: id === "health" ? ["master", "signals"] : [], descriptor: descriptor(id, "1"), ontology })) as GraphInfo[];
  const source: GraphSource = {
    name: "test",
    open: async () => ({ id: "test", graphs }), onRejected: () => () => {},
    subscribe: (_id: string, next: (e: StreamEvent) => void) => { next({ type: "end" }); return () => {}; },
    snapshot: async (id: string) => ({ version: ver("1", id), entities: id === "health" ? [ent("check", "Check")] : [ent(meter, id === "master" ? "Meter" : "Signal")],
      relationships: id === "health" ? [rel("edge", "check", meter, "CpuFromSignal")] : [] }),
    references: async () => [entry],
  };
  const workspace = new Workspace(source, darkTheme, () => {});
  await workspace.load();
  workspace.select({ graph: "health", id: "check", kind: "entity" });
  await Promise.resolve();
  expect(workspace.reference("health", meter)).toMatchObject({ graph: "signals", entityType: "Signal", id: meter });
  expect([...workspace.history("health")!.graph.stubs()][0].type).toBe("Signal (reference)");
  expect(workspace.reference("health", meter, { relationshipType: "MeterOf", end: "source" })).toEqual(entry);
  expect(workspace.reference("health", meter, { relationshipType: "Unknown", end: "target" })).toEqual(entry);
});
