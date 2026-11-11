import { describe, expect, it } from "vitest";
import { isReferenceType, stubEntity } from "../src/core/reference";
import type { Reference } from "../src/source/types";

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
    ["without an identity the id is the name", ref({ identity: null }), "◌ e:1"],
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
    expect([s.type, prop(s, "name"), isReferenceType(s.type)]).toEqual(["reference", "◌ e:abcdefghijklmnop", true]);
  });
});
