import { describe, expect, it } from "vitest";
import { STAND_IN, withStandIn } from "../src/source/tree-standin";
import type { GraphInfo } from "../src/source/types";
import { treeSpec } from "./fixtures/tree-graph";

const info = (ontologyId: string, tree?: typeof treeSpec) => ({ id: "g", ontology: { reference: { id: ontologyId } }, ...(tree ? { tree } : {}) }) as unknown as GraphInfo;

describe("the stand-in for the engine's hierarchy marker", () => {
  it.each([
    ["an ontology it knows gets its hierarchy", info("acme.health"), STAND_IN["acme.health"]],
    ["an ontology it does not know gets none", info("acme.ops"), undefined],
    ["an ontology that marks its own hierarchy keeps it", info("acme.health", treeSpec), treeSpec],
  ])("%s", (_, i, want) => expect(withStandIn(i).tree).toBe(want));
  it("is keyed by the ontology, never by a graph name", () => expect(withStandIn({ ...info("acme.health"), id: "anything" } as GraphInfo).tree).toBe(STAND_IN["acme.health"]));
});
