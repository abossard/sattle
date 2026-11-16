// @vitest-environment jsdom
// A view reads a graph through one frozen object per revision: memos over it run again when the revision moves and at no other time.
import { describe, expect, it } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { Graph } from "../../src/core/graph";
import { runtime } from "../../src/state/runtime";
import { useStore } from "../../src/state/store";
import { useRevision } from "../../src/ui/common";
import { fakeWorkspace } from "../helpers";

const summary = (version: number) => ({ id: "g", role: "master" as const, head: "1", patches: 0, nodes: 0, edges: 0, version });
const workspace = (graph: Graph) => ({ ...fakeWorkspace({ g: { graph } }), graphInfo: () => ({ id: "g" }) }) as never;

describe("useRevision", () => {
  it.each<[string, (graph: Graph) => void, boolean]>([
    ["the same version rendered again keeps the object", () => {}, false],
    ["a store change that is not the graph's keeps the object", () => useStore.setState({ announcement: "something else" }), false],
    ["a new version of the graph gives a new object", () => useStore.setState({ graphs: [summary(2)] }), true],
    ["another workspace (a source opened again) gives a new object", (graph) => { runtime.ws = workspace(graph); }, true],
  ])("%s", (_, change, isNew) => {
    const graph = new Graph();
    runtime.ws = workspace(graph);
    try {
      act(() => useStore.setState({ graphs: [summary(1)] }));
      const { result, rerender } = renderHook(() => useRevision("g"));
      const first = result.current;
      act(() => change(graph));
      rerender();
      expect([result.current !== first, Object.isFrozen(result.current), result.current?.graph === graph]).toEqual([isNew, true, true]);
    } finally { runtime.ws = null; act(() => useStore.setState({ graphs: [], announcement: "" })); }
  });
});
