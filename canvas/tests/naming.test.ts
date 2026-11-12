import { describe, expect, it } from "vitest";
import { INTERNAL_ID, isInternalId, makeNamer } from "../src/core/naming";
import type { Entity } from "../src/core/model";
import { ont } from "./fixtures/engine-fake";
import { ent } from "./helpers";

const HASH = "e:j4c6vhyph6kc6nuzsq7oicnyhn";
const ontology = {
  ...ont("o"),
  properties: [
    { id: { value: "acme.svc.cluster" }, valueKind: "text", nullable: false, itemKind: null, unit: null, role: "graphData" },
    { id: { value: "acme.svc.namespace" }, valueKind: "text", nullable: false, itemKind: null, unit: null, role: "graphData" },
    { id: { value: "note" }, valueKind: "text", nullable: true, itemKind: null, unit: null, role: "graphData" },
    { id: { value: "replicas" }, valueKind: "integer", nullable: false, itemKind: null, unit: null, role: "graphData" },
  ],
  entityTypes: [{ id: { value: "NoType" }, properties: [] }, { id: { value: "Group" }, properties: [{ value: "acme.svc.cluster" }, { value: "replicas" }, { value: "acme.svc.namespace" }, { value: "note" }] }],
};
const group = (id: string, props: Record<string, string>): Entity => ent(id, "Group", props);
const namer = (all: Entity[] = [], labels: Record<string, string> = {}) => makeNamer(ontology as never, () => all, labels);

describe("internal ids are never labels", () => {
  it.each([[HASH, true], ["n:abcdefghijklmnopqrstuvwxyz", true], ["aks-synth", false], ["d:m/projection:p", false], ["e:short", false]])("%s is an internal id: %s", (s, yes) => expect(isInternalId(s)).toBe(yes));
  it("the pattern finds an id inside a text", () => expect(INTERNAL_ID.test(`label ${HASH} more`)).toBe(true));
});

describe("the name of an element", () => {
  it.each<[string, Entity, string]>([
    ["the name property", group(HASH, { name: "web", "acme.svc.cluster": "synth" }), "web"],
    ["a qualified name property", group(HASH, { "acme.svc.name": "api" }), "api"],
    ["a property that ends in name", group(HASH, { nodeName: "node-a" }), "node-a"],
    ["a name that is an id is no name: the required text properties instead", group(HASH, { name: "e:j4c6vhyph6kc6nuzsq7oicnyhn", "acme.svc.cluster": "synth", "acme.svc.namespace": "shop" }), "synth / shop"],
    ["the required text properties of the type, in declared order, not the optional or the integer ones", group(HASH, { "acme.svc.cluster": "synth", "acme.svc.namespace": "shop", note: "x", replicas: "2" }), "synth / shop"],
    ["a readable id when there is nothing else (it is no hash)", group("web", {}), "web"],
  ])("%s", (_, e, label) => expect(namer()(e)).toBe(label));

  it("an element with nothing to say is its type and a short index, the same one each time", () => {
    const all = [0, 1, 2].map((i) => group(`e:${"abcdefghij".repeat(3).slice(i, i + 26)}`, {}));
    const name = namer(all);
    expect(all.map(name)).toEqual(["Group 1", "Group 2", "Group 3"]);
    expect(all.map(name)).toEqual(["Group 1", "Group 2", "Group 3"]);
    expect(name(group("e:zzzzzzzzzzzzzzzzzzzzzzzzzz", {}))).toBe("Group 4");
  });
  it("no label is ever an internal id", () => {
    const all = [group(HASH, {}), group(HASH.replace("j", "k"), { name: HASH }), group(HASH.replace("j", "m"), { "acme.svc.cluster": HASH })];
    for (const e of all) expect(isInternalId(namer(all)(e)), namer(all)(e)).toBe(false);
  });
});

describe("the property the ontology names as the label of a type (engine 3.17)", () => {
  it.each<[string, Entity, Record<string, string>, string]>([
    ["is used before any guess", group(HASH, { name: "web", note: "the note", "acme.svc.cluster": "synth" }), { Group: "note" }, "the note"],
    ["is used when the element has no name", group(HASH, { note: "from the label" }), { Group: "note" }, "from the label"],
    ["falls back to the usual name when the element has no value for it", group(HASH, { name: "web" }), { Group: "note" }, "web"],
    ["a label that holds an internal id falls back", group(HASH, { note: HASH, name: "web" }), { Group: "note" }, "web"],
    ["is the type's own: another type is not affected", ent(HASH, "Other", { name: "other", note: "n" }), { Group: "note" }, "other"],
  ])("%s", (_, e, labels, want) => expect(namer([], labels)(e)).toBe(want));
});
