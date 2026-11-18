// @vitest-environment jsdom
// The default source: the engine's output on the estate. Each tile answers what its graph is, in words a person reads (no internal ids), and a document is shown as a document.
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { beforeAll, describe, expect, it, vi } from "vitest";
import { describeGraph, typesHeld } from "../../src/app/tile";
import { isInternalId } from "../../src/core/naming";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import { openApp } from "./helpers";

// the default source is static files of the engine's output: serve them to fetch from the public directory
const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
beforeAll(() => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const text = files[`/public${new URL(String(input), "http://localhost/").pathname}`];
    return text === undefined ? new Response("", { status: 404 }) : new Response(text);
  }) as typeof fetch;
});

const HAS_EDGES = ["master", "serviceTree", "capabilities", "deploymentHealth", "treeStructure", "treeRollup", "healthTree", "workloadStructure", "workload"];
const GRAPHS = ["master", "deploymentHealth", "deploymentReport", "serviceTree", "capabilities", "treeSignals", "treeStructure", "treeRollup", "healthTree", "healthReport", "workloadStructure", "workload"];
const tile = (id: string) => screen.findByRole("group", { name: `Graph ${id}` });

describe("the default source shows the master and every projection of the estate, how their elements stand for each other, and its documents as documents", () => {
  it("shows the master and every projection, each with its edges where it has any", async () => {
    await openApp("demo", "space");
    for (const id of ["master", "deploymentReport", "healthReport", "treeSignals", ...HAS_EDGES]) await screen.findByRole("group", { name: `Graph ${id}` });
    for (const id of HAS_EDGES) {
      const header = within(screen.getByRole("group", { name: `Graph ${id}` })).getByText(/ nodes · .* edges/).textContent ?? "";
      expect(Number(/· ([\d,]+) edges/.exec(header)?.[1].replace(",", "")), `${id}: ${header}`).toBeGreaterThan(0);
    }
  }, 30000);

  it("a projection with nodes and no edges says why and lists what it holds instead of a ring of dots; a document and the master do not say so", async () => {
    await openApp("demo", "space");
    const t = await tile("treeSignals");
    expect(within(t).getByText(/has 12 nodes and no edges/)).toBeTruthy();
    expect(within(t).getByText("It holds:")).toBeTruthy();
    expect(t.querySelector("canvas")).toBeNull();
    expect(within(screen.getByRole("group", { name: "Graph master" })).queryByText(/and no edges/)).toBeNull();
    expect(within(screen.getByRole("group", { name: "Graph workload" })).queryByText(/and no edges/)).toBeNull();
  }, 30000);

  it("a lifted edge stands for the lower edges and nodes down to the master, and a master node is part of what is above it", async () => {
    await openApp("demo", "space");
    const ws = runtime.ws!;
    const lifted = [...ws.history("capabilities")!.graph.relationships()].find((r) => r.type.endsWith("DependsOn"))!;
    const edge = { graph: "capabilities", kind: "relationship", id: lifted.id } as const;
    ws.select(edge);
    await vi.waitFor(() => expect(ws.closure(edge).lower.get("master")?.size ?? 0).toBeGreaterThan(0));
    const { lower } = ws.closure(edge);
    expect([...(lower.get("serviceTree") ?? [])].some((k) => k.startsWith("e:"))).toBe(true);
    expect([...(lower.get("master") ?? [])].some((k) => k.startsWith("e:")), "the master edges the lifted edge aggregates").toBe(true);
    expect([...(lower.get("master") ?? [])].some((k) => k.startsWith("n:"))).toBe(true);

    const service = [...ws.history("master")!.graph.entities()].find((e) => e.type === "Service")!;
    const node = { graph: "master", kind: "entity", id: service.id } as const;
    ws.select(node);
    await vi.waitFor(() => expect(["serviceTree", "workloadStructure", "workload"].filter((g) => !(ws.closure(node).upper.get(g)?.size))).toEqual([]));
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("says where the highlight is partial and not where it is complete", async () => {
    await openApp("demo", "space");
    const ws = runtime.ws!;
    const incomplete = (files["/public/demo/estate/treeStructure.lineage.ndjson"].trim().split("\n").map((l) => JSON.parse(l)) as { element: string; complete: boolean }[]).find((r) => !r.complete)!;
    const state = { graph: "treeStructure", kind: incomplete.element.startsWith("e:") ? "entity" : "relationship", id: incomplete.element } as const;
    ws.select(state);
    await vi.waitFor(() => expect(ws.partialOf(state).below.map((b) => b.graph)).toContain("treeStructure"));
    expect(ws.partialOf(state).cutOff).toBe(false);

    const lifted = [...ws.history("capabilities")!.graph.relationships()].find((r) => r.type.endsWith("DependsOn"))!;
    const edge = { graph: "capabilities", kind: "relationship", id: lifted.id } as const;
    ws.select(edge);
    await vi.waitFor(() => expect(ws.closure(edge).lower.get("master")?.size ?? 0).toBeGreaterThan(0));
    expect(ws.partialOf(edge)).toEqual({ below: [], aboveIn: [], cutOff: false });
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("the workload opens as a tree (its hierarchy is marked, its states are read through the stand-in), with the graph as the other tab", async () => {
    await openApp("demo", "data");
    dispatch({ type: "ui/activeGraph", graph: "workload" });
    const tree = await screen.findByRole("tree", { name: "Tree of workload" }, { timeout: 10000 });
    const items = within(tree).getAllByRole("treeitem");
    expect(items.length).toBeGreaterThan(0);
    expect(items.every((r) => r.getAttribute("aria-level") && r.textContent)).toBe(true);
    // the states are the engine's: at the head the workload has no data; in earlier revisions parts of it failed, and their rows say so
    expect(items[0].textContent, "the workload at the head").toMatch(/^shop workload.*state No data, unknown$/);
    const seen = new Set<string>();
    const { treeOf } = await import("../../src/app/tree-model");
    const ws = runtime.ws!, info = ws.graphInfo("workload")!;
    for (const t of ws.axisPoints()) {
      await act(async () => { dispatch({ type: "time/seek", t }); });
      // the tree has rendered the revision the timeline shows: its count of nodes is that revision's
      const model = treeOf(ws.history("workload")!.graph, info)?.model;
      if (model) await waitFor(() => { const bar = screen.queryByRole("group", { name: "Tree controls" }); if (bar) expect(bar.textContent).toContain(` ${model.nodes.size.toLocaleString()} nodes`); });
      const shown = screen.queryByRole("tree", { name: "Tree of workload" });
      for (const r of shown ? within(shown).getAllByRole("treeitem") : []) for (const m of (r.textContent ?? "").matchAll(/state (Healthy|No data|Degraded|Unhealthy|Unknown),/g)) seen.add(m[1]);
    }
    await act(async () => { dispatch({ type: "time/seek", t: useStore.getState().time.last }); });
    expect([...seen].filter((x) => x === "Degraded" || x === "Unhealthy").sort(), "no revision shows a failing part").toEqual(["Degraded", "Unhealthy"]);
    expect(within(screen.getByRole("tablist", { name: "How to show this graph" })).getAllByRole("tab")[0].textContent).toBe("Tree");
  }, 30000);

  it("opening a document graph from its tile shows the document view with the report, the graph is the other tab", async () => {
    await openApp("demo", "space");
    fireEvent.click(within(await tile("deploymentReport")).getByRole("button", { name: "Open" }));
    expect(useStore.getState().view).toBe("data");
    expect(useStore.getState().activeGraph).toBe("deploymentReport");
    expect((await screen.findByRole("tab", { name: "Document" })).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "Graph" }).getAttribute("aria-selected")).toBe("false");
    expect((await screen.findByLabelText("Document deploymentReport", {}, { timeout: 10000 })).textContent).toMatch(/Deployment health/);
  }, 30000);
});

describe("a tile of the default source says what its graph is in words a person reads, never an internal id", () => {
  it.each(GRAPHS)("%s: the tile says in one line what the graph is, and neither that line nor any element's label is an internal id", async (id) => {
    await openApp("demo", "space");
    const t = await tile(id);
    const ws = runtime.ws!;
    const line = describeGraph(ws.graphInfo(id)!, typesHeld(ws.history(id)!.graph));
    expect(line.length).toBeGreaterThan(10);
    expect(isInternalId(line) || isInternalId(t.textContent ?? ""), `${id}: ${t.textContent?.slice(0, 200)}`).toBe(false);
    // the line may be followed by what the tile leaves out
    expect(within(t).getByText(line, { exact: false }), `${id} shows no line that says what it is`).toBeTruthy();
    const g = ws.history(id)!.graph;
    const labels = [...g.entities()].map((e) => g.label(e));
    expect(labels.length).toBeGreaterThan(0);
    for (const l of labels) { expect(l.trim(), id).not.toBe(""); expect(isInternalId(l), `${id}: ${l}`).toBe(false); }
  }, 30000);

  it("a document graph shows the document: its title and figures, not its blocks as nodes", async () => {
    await openApp("demo", "space");
    const t = await tile("deploymentReport");
    expect(await within(t).findByRole("heading", { name: "Deployment health" })).toBeTruthy();
    expect(within(t).getByText("Deployments: 2")).toBeTruthy();
    expect(t.querySelector("canvas"), "a document tile draws no graph").toBeNull();
    expect(isInternalId(t.textContent ?? "")).toBe(false);
  }, 30000);
});
