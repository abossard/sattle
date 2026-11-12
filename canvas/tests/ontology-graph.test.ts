import { describe, expect, it } from "vitest";
import { ontologyGraph } from "../src/core/ontology-graph";
import { ont } from "./fixtures/engine-fake";

describe("an ontology as a picture", () => {
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
