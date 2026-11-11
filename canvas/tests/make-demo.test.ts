import { describe, expect, it } from "vitest";
// @ts-expect-error the generator is a plain script
import { EXPECT_EDGES, binariesProblem, problems, sizesOf } from "../scripts/make-demo.mjs";

const g = (id: string, entities: number, relationships: number) => ({ id, entities, relationships });

describe("the demo generator refuses output without edges", () => {
  it.each([
    ["every expected graph has edges", ["master", "health"].map((id) => g(id, 3, 2)), ["master", "health"], []],
    ["a graph with nodes and no edges", [g("master", 5, 4), g("health", 42, 0)], ["master", "health"], ["health: 42 nodes and no edges"]],
    ["a graph the engine did not write", [g("master", 5, 4)], ["master", "health"], ["health: the engine wrote no such graph"]],
    ["a graph that is not expected to have edges is not checked", [g("master", 5, 4), g("deploymentReport", 7, 0)], ["master"], []],
    ["the expected list covers the master, the lifted graphs and the health graphs", [], EXPECT_EDGES, EXPECT_EDGES.map((id: string) => `${id}: the engine wrote no such graph`)],
  ])("%s", (_, found, expect_, want) => expect(problems(found, expect_)).toEqual(want));

  it("reads the size of every graph of the manifest from its final snapshot", () => {
    const finals: Record<string, unknown> = { a: { extent: { entities: [1, 2, 3], relationships: [1] } }, b: { extent: { entities: [], relationships: [] } } };
    expect(sizesOf({ graphs: { a: {}, b: {} } }, (id: string) => finals[id])).toEqual([g("a", 3, 1), g("b", 0, 0)]);
  });
});

describe("the demo generator runs the engine's built binaries and never builds them", () => {
  it.each([
    ["missing binaries", null, 0, /not built/],
    ["binaries older than the sources", 1_000_000, 1_000_000 + 7 * 60000, /older than the engine's sources by 7 minutes/],
    ["binaries newer than the sources", 5_000_000, 1_000_000, null],
    ["same time", 1_000_000, 1_000_000, null],
  ])("%s", (_, builtAt, sourcesAt, want) => {
    const p = binariesProblem("sattle.dll", builtAt, sourcesAt);
    if (want) expect(p).toMatch(want); else expect(p).toBeNull();
  });
});
