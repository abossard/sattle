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

describe("useRevision", () => {
  it.each<[string, number, boolean]>([
    ["the same revision rendered again", 1, false],
    ["a new revision of the graph", 2, true],
  ])("%s gives a new object: %s", (_, next, isNew) => {
    const graph = new Graph();
    runtime.ws = { ...fakeWorkspace({ g: { graph } }), graphInfo: () => ({ id: "g" }) } as never;
    try {
      act(() => useStore.setState({ graphs: [summary(1)] }));
      const { result, rerender } = renderHook(() => useRevision("g"));
      const first = result.current;
      act(() => useStore.setState({ graphs: [summary(next)] }));
      rerender();
      expect([result.current !== first, Object.isFrozen(result.current), result.current?.graph === graph]).toEqual([isNew, true, true]);
    } finally { runtime.ws = null; act(() => useStore.setState({ graphs: [] })); }
  });
});
