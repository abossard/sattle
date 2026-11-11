// @vitest-environment jsdom
import { screen, within } from "@testing-library/react";
import { beforeAll, describe, expect, it, vi } from "vitest";
import { runtime } from "../../src/state/runtime";
import { dispatch } from "../../src/state/store";
import { openApp, violations } from "./helpers";

// the default source is static files of the engine's output: serve them to fetch from the public directory
const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
beforeAll(() => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const text = files[`/public${new URL(String(input), "http://localhost/").pathname}`];
    return text === undefined ? new Response("", { status: 404 }) : new Response(text);
  }) as typeof fetch;
});

const HAS_EDGES = ["master", "serviceTree", "capabilities", "deploymentHealth", "healthTopology", "healthState", "health"];

describe("the default source: the engine's output on the estate", () => {
  it("shows the master and every projection, each with its edges where it has any, and has no axe violations", async () => {
    await openApp("demo", "space");
    for (const id of ["master", "deploymentReport", "healthSignals", ...HAS_EDGES]) await screen.findByRole("group", { name: `Graph ${id}` });
    for (const id of HAS_EDGES) {
      const header = within(screen.getByRole("group", { name: `Graph ${id}` })).getByText(/ nodes · .* edges/).textContent ?? "";
      expect(Number(/· ([\d,]+) edges/.exec(header)?.[1].replace(",", "")), `${id}: ${header}`).toBeGreaterThan(0);
    }
    expect(await violations()).toEqual([]);
  }, 30000);

  it("a projection with nodes and no edges says so, a document and the master do not", async () => {
    await openApp("demo", "space");
    expect(within(await screen.findByRole("group", { name: "Graph healthSignals" })).getByText(/has 6 nodes and no edges/)).toBeTruthy();
    expect(within(screen.getByRole("group", { name: "Graph master" })).queryByText(/and no edges/)).toBeNull();
    expect(within(screen.getByRole("group", { name: "Graph health" })).queryByText(/and no edges/)).toBeNull();
  }, 30000);

  it("a lifted edge stands for the lower edges and nodes down to the master, and a master node is part of what is above it", async () => {
    await openApp("demo", "space");
    const ws = runtime.ws!;
    const lifted = [...ws.histories.get("capabilities")!.graph.relationships()].find((r) => r.type.endsWith("DependsOn"))!;
    const edge = { graph: "capabilities", kind: "relationship", id: lifted.id } as const;
    ws.select(edge);
    await vi.waitFor(() => expect(ws.closure(edge).lower.get("master")?.size ?? 0).toBeGreaterThan(0));
    const { lower } = ws.closure(edge);
    expect([...(lower.get("serviceTree") ?? [])].some((k) => k.startsWith("e:"))).toBe(true);
    expect([...(lower.get("master") ?? [])].some((k) => k.startsWith("e:")), "the master edges the lifted edge aggregates").toBe(true);
    expect([...(lower.get("master") ?? [])].some((k) => k.startsWith("n:"))).toBe(true);

    const service = [...ws.histories.get("master")!.graph.entities()].find((e) => e.type === "Service")!;
    const node = { graph: "master", kind: "entity", id: service.id } as const;
    ws.select(node);
    await vi.waitFor(() => expect(["serviceTree", "healthTopology", "health"].filter((g) => !(ws.closure(node).upper.get(g)?.size))).toEqual([]));
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("says where the highlight is partial and not where it is complete", async () => {
    await openApp("demo", "space");
    const ws = runtime.ws!;
    const incomplete = (files["/public/demo/estate/healthState.lineage.ndjson"].trim().split("\n").map((l) => JSON.parse(l)) as { element: string; complete: boolean }[]).find((r) => !r.complete)!;
    const state = { graph: "healthState", kind: incomplete.element.startsWith("e:") ? "entity" : "relationship", id: incomplete.element } as const;
    ws.select(state);
    await vi.waitFor(() => expect(ws.partialOf(state).below.map((b) => b.graph)).toContain("healthState"));
    expect(ws.partialOf(state).cutOff).toBe(false);

    const lifted = [...ws.histories.get("capabilities")!.graph.relationships()].find((r) => r.type.endsWith("DependsOn"))!;
    const edge = { graph: "capabilities", kind: "relationship", id: lifted.id } as const;
    ws.select(edge);
    await vi.waitFor(() => expect(ws.closure(edge).lower.get("master")?.size ?? 0).toBeGreaterThan(0));
    expect(ws.partialOf(edge)).toEqual({ below: [], aboveIn: [], cutOff: false });
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("the health roll-up opens as a tree through the stand-in hierarchy, with the graph as the other tab", async () => {
    await openApp("demo", "data");
    dispatch({ type: "ui/activeGraph", graph: "health" });
    const tree = await screen.findByRole("tree", { name: "Tree of health" }, { timeout: 10000 });
    const items = within(tree).getAllByRole("treeitem");
    expect(items.length).toBeGreaterThan(0);
    expect(items.every((r) => r.getAttribute("aria-level") && r.textContent)).toBe(true);
    expect(items.some((r) => /state Degraded, warning/.test(r.textContent ?? ""))).toBe(true);
    expect(within(screen.getByRole("tablist", { name: "How to show this graph" })).getAllByRole("tab")[0].textContent).toBe("Tree");
    expect(await violations()).toEqual([]);
  }, 30000);

  it("the report of the deployment health is a document", async () => {
    await openApp("demo", "data");
    dispatch({ type: "ui/activeGraph", graph: "deploymentReport" });
    expect((await screen.findByLabelText("Document deploymentReport", {}, { timeout: 10000 })).textContent).toMatch(/Deployment health/);
  }, 30000);
});
