// @vitest-environment jsdom
// The graph space of the default source: each tile answers what its graph is, in words a person reads (no internal ids), and a document is shown as a document.
import { fireEvent, screen, within } from "@testing-library/react";
import { beforeAll, describe, expect, it } from "vitest";
import { describeGraph, typesHeld } from "../../src/app/tile";
import { isInternalId } from "../../src/core/naming";
import { runtime } from "../../src/state/runtime";
import { useStore } from "../../src/state/store";
import { openApp } from "./helpers";

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
beforeAll(() => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const text = files[`/public${new URL(String(input), "http://localhost/").pathname}`];
    return text === undefined ? new Response("", { status: 404 }) : new Response(text);
  }) as typeof fetch;
});

const GRAPHS = ["master", "deploymentHealth", "deploymentReport", "serviceTree", "capabilities", "healthSignals", "healthTopology", "healthState", "health"];
const tile = (id: string) => screen.findByRole("group", { name: `Graph ${id}` });

describe("a tile of the graph space of the default source", () => {
  it.each(GRAPHS)("%s says in one line what the graph is, and the line holds no internal id", async (id) => {
    await openApp("demo", "space");
    const t = await tile(id);
    const ws = runtime.ws!;
    const line = describeGraph(ws.graphInfo(id)!, typesHeld(ws.histories.get(id)!.graph));
    expect(line.length).toBeGreaterThan(10);
    expect(isInternalId(line) || isInternalId(t.textContent ?? ""), `${id}: ${t.textContent?.slice(0, 200)}`).toBe(false);
    expect(within(t).getByText(line), `${id} shows no line that says what it is`).toBeTruthy();
  }, 30000);

  it("a document graph shows the document: its title and figures, not its blocks as nodes", async () => {
    await openApp("demo", "space");
    const t = await tile("deploymentReport");
    expect(await within(t).findByRole("heading", { name: "Deployment health" })).toBeTruthy();
    expect(within(t).getByText("Deployments: 3")).toBeTruthy();
    expect(t.querySelector("canvas"), "a document tile draws no graph").toBeNull();
    expect(isInternalId(t.textContent ?? "")).toBe(false);
  }, 30000);

  it("opening a document graph shows the document view, the graph is the other tab", async () => {
    await openApp("demo", "space");
    fireEvent.click(within(await tile("deploymentReport")).getByRole("button", { name: "Open" }));
    expect(useStore.getState().view).toBe("data");
    expect(useStore.getState().activeGraph).toBe("deploymentReport");
    expect((await screen.findByRole("tab", { name: "Document" })).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "Graph" }).getAttribute("aria-selected")).toBe("false");
  }, 30000);

  it("a graph with nodes and no edges says why and lists what it holds, instead of a ring of dots", async () => {
    await openApp("demo", "space");
    const t = await tile("healthSignals");
    expect(within(t).getByText(/has 6 nodes and no edges/)).toBeTruthy();
    expect(within(t).getByText("It holds:")).toBeTruthy();
    expect(t.querySelector("canvas")).toBeNull();
  }, 30000);
});

describe("what elements are called in the default source", () => {
  it.each(GRAPHS)("%s: every element has a label and none is an internal id", async (id) => {
    await openApp("demo", "space");
    const g = runtime.ws!.histories.get(id)!.graph;
    const labels = [...g.entities()].map((e) => g.label(e));
    expect(labels.length).toBeGreaterThan(0);
    for (const l of labels) { expect(l.trim(), id).not.toBe(""); expect(isInternalId(l), `${id}: ${l}`).toBe(false); }
  }, 30000);
});
