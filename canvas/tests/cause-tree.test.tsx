// @vitest-environment jsdom
// The tree of a graph with a roll-up answers where a problem starts: by default it nests along the relation the state was computed from, marks
// the origins (own state as bad as the rolled-up one) and opens the paths to them; an element reached by two paths is under both. Read on the
// demo workload, first as a model, then through the application. The faults are found in the data, not named: the first revision at which the
// workload is degraded, and the first at which it is unhealthy; the names come from the tree by cause at that revision.
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { blamePath } from "../src/app/edge-kinds";
import { History } from "../src/app/history";
import { defaultOpen, isWrong, treeOf, visibleRows, type TreeModel } from "../src/app/tree-model";
import type { Patch } from "../src/core/model";
import { StaticSource } from "../src/source/static";
import type { Role } from "../src/source/presentation";
import { runtime } from "../src/state/runtime";
import { dispatch } from "../src/state/store";
import { openApp } from "./ui/helpers";

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = async (url: string) => { const t = files[`/public${new URL(url).pathname}`]; if (t === undefined) throw new Error(url); return t; };
beforeAll(() => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const text = files[`/public${new URL(String(input), "http://localhost/").pathname}`];
    return text === undefined ? new Response("", { status: 404 }) : new Response(text);
  }) as typeof fetch;
});

async function load(graph: string) {
  const src = new StaticSource("t", "http://localhost/demo/estate/manifest.json", fetcher);
  const info = (await src.open()).graphs.find((g) => g.id === graph)!;
  const h = new History(await src.snapshot(graph));
  const patches: Patch[] = await new Promise((resolve) => { const out: Patch[] = []; src.subscribe(graph, (e) => { if (e.type === "patch") out.push(e.patch); if (e.type === "end") resolve(out); }); });
  for (const p of patches) h.append(p);
  return { info, h };
}
const demo = load("workload");

/** The first revision at which the workload (the element of the hierarchy's top type) has a rolled-up state of this role, with the tree there. */
async function fault(role: Role) {
  const { info, h } = await demo;
  for (let r = 0; r <= h.length; r++) {
    h.seek(r);
    const workload = h.graph.nodes.find((e) => e?.type === info.tree?.types?.[0]);
    const m = treeOf(h.graph, info)!.model;
    const w = workload && m.nodes.get(workload.id);
    if (w && (w.rolled ?? w.own)?.role === role) return { r, m, w, blamed: [...blamePath(h.graph, info).blamed].sort() };
  }
  return undefined;
}

const rowsOf = (m: TreeModel) => visibleRows(m, { expanded: defaultOpen(m, 40), onlyWrong: false, query: "", sort: "severity" }).rows;
const path = (m: TreeModel, key: string) => key.split("\u001f").map((id) => m.nodes.get(id)!.label).join(" > ");
/** the rows the tree opens on, each as the labels of its path from the root, with its state, and a mark for an origin */
const opened = (m: TreeModel): string[] => rowsOf(m).map((r) => `${path(m, r.key)} ${r.node.rolled?.value}${r.node.origin ? " origin" : ""}`);
const origins = (m: TreeModel) => [...m.nodes.values()].filter((n) => n.origin).map((n) => n.id).sort();
/** a way down from an element to another, each step to a child it is wrong through */
const wayTo = (m: TreeModel, from: string, to: string): string[] | null =>
  from === to ? [from] : m.nodes.get(from)!.through.reduce<string[] | null>((found, c) => found ?? ((w) => w && [from, ...w])(wayTo(m, c, to)), null);

describe("the workload tree by cause opens on the paths from what is wrong to where it starts", () => {
  it("is the default, for the tile and the Tree tab alike", async () => {
    const { info, h } = await demo;
    h.seek(h.length);
    expect(treeOf(h.graph, info)?.choice).toBe("cause");
  });

  it("the workload degraded: the origins are those the graph flags, the workload is degraded through a path to each, a part off that path stays healthy", async () => {
    const f = await fault("warning");
    expect(f, "the demo workload is degraded at some revision").toBeDefined();
    const { m, w, blamed } = f!;
    expect(origins(m), "the tree by cause has an origin").not.toEqual([]);
    expect(origins(m)).toEqual(blamed);
    for (const o of origins(m)) {
      const way = wayTo(m, w.id, o);
      expect(way, `the workload is wrong through a path to ${m.nodes.get(o)!.label}`).not.toBeNull();
      expect(opened(m)).toContain(`${way!.map((id) => m.nodes.get(id)!.label).join(" > ")} ${w.rolled?.value} origin`);
    }
    const aside = w.children.filter((c) => !w.through.includes(c));
    expect(aside.length, "the workload has a part it is not degraded through").toBeGreaterThan(0);
    expect(aside.filter((c) => isWrong(m.nodes.get(c)!)).map((c) => m.nodes.get(c)!.label), "a part off the path is healthy").toEqual([]);
  });

  it("the workload unhealthy with two problems at once: each origin shows once, at the end of an open path from a root; an element under several parents shows its children at its first row", async () => {
    const f = await fault("danger");
    expect(f, "the demo workload is unhealthy at some revision").toBeDefined();
    const { m, blamed } = f!;
    expect(origins(m)).toEqual(blamed);
    expect(origins(m).length, "two problems start at once").toBeGreaterThan(1);
    const rows = rowsOf(m);
    const runsThrough = (o: string) => { const ids = rows.find((r) => r.node.id === o)!.key.split("\u001f"); return ids.slice(1).every((id, i) => m.nodes.get(ids[i])!.through.includes(id)); };
    expect(origins(m).filter(runsThrough).length, "the root is wrong through each element down to an origin").toBeGreaterThan(0);
    for (const o of origins(m)) {
      const at = rows.filter((r) => r.node.id === o);
      expect(at.map((r) => path(m, r.key)), "the origin is shown once, at the first row of what takes it in").toHaveLength(1);
      const ids = at[0].key.split("\u001f");
      expect([at[0].node.origin, m.roots.includes(ids[0])], path(m, at[0].key)).toEqual([true, true]);
      // each element on the way is under the one before it; the way need not run through: a deployment with one crash-looping pod of several is not wrong
      for (let i = 1; i < ids.length; i++) expect(m.nodes.get(ids[i])!.parents, `${path(m, at[0].key)}, step ${i}`).toContain(ids[i - 1]);
    }
    // reached by two paths and more, under each, the same element; its children show at its first row, the later rows say so
    const later = rows.filter((r) => r.shownAbove);
    expect(later.length, "an open element is reached by more than one path").toBeGreaterThan(0);
    for (const r of later) {
      const first = rows.findIndex((x) => x.key === r.shownAbove);
      expect([first >= 0 && first < rows.indexOf(r), rows[first]?.node.id, r.node.parents.length > 1], path(m, r.key)).toEqual([true, r.node.id, true]);
    }
  });

  it("the graph's flag marks the same elements as the tree's origins, at every revision", async () => {
    const { info, h } = await demo;
    for (let r = 0; r <= h.length; r++) {
      h.seek(r);
      const m = treeOf(h.graph, info)!.model;
      expect([...blamePath(h.graph, info).blamed].sort(), `revision ${r}`).toEqual(origins(m));
    }
  });
});

const labelOf = (r: Element) => r.querySelector("[data-label]")?.textContent ?? "";
const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

describe("the tree of the demo workload, reached by scrubbing, shows what the tree by cause says", () => {
  it("when the workload is unhealthy, opens on the paths to every origin, each marked, and a later row of a shared element leads to its first", async () => {
    const f = (await fault("danger"))!;
    const { m } = f;
    // the element the person checks: open under several parents, wrong through one child, with a label no other element has
    const unique = (id: string) => [...m.nodes.values()].filter((n) => n.label === m.nodes.get(id)!.label).length === 1;
    const shared = m.nodes.get(rowsOf(m).find((r) => r.shownAbove && r.node.through.length > 0 && unique(r.node.id))!.node.id)!;
    const user = userEvent.setup();
    await openApp("demo", "data");
    await act(async () => { dispatch({ type: "ui/activeGraph", graph: "workload" }); });
    // in steps of a minute from the start to the time of that revision
    const until = runtime.ws!.patchTimes("workload").times[f.r - 1];
    for (let t = 0; t < until; t += 60) await act(async () => { dispatch({ type: "time/seek", t }); });
    await act(async () => { dispatch({ type: "time/seek", t: until }); });
    expect(runtime.ws!.history("workload")!.cursor, "scrubbed to the revision of the fault").toBe(f.r);
    await user.click(await screen.findByRole("tab", { name: /^Tree/ }));
    const tree = await screen.findByRole("tree", { name: "Tree of workload" });
    await waitFor(() => expect(within(tree).getAllByRole("treeitem").filter((r) => /origin/.test(r.textContent ?? "")).map(labelOf)).toEqual(expect.arrayContaining(origins(m).map((o) => m.nodes.get(o)!.label))));
    const rowsNamed = () => within(tree).getAllByRole("treeitem").filter((r) => labelOf(r) === shared.label);
    const through = shared.through.map((c) => m.nodes.get(c)!.label).join(", ");
    expect(rowsNamed()[0].textContent).toMatch(new RegExp(`through ${escape(through)}.*same element, under ${shared.parents.length}`));
    // an open element shows its children at its first row; a later row of it says so, and Enter there goes to the first
    const later = rowsNamed();
    expect(later.map((r) => /shown above/.test(r.textContent ?? ""))).toEqual([false, ...later.slice(1).map(() => true)]);
    expect(later.length).toBeGreaterThan(1);
    const hint = document.getElementById(later[1].getAttribute("aria-describedby") ?? "");
    expect(hint?.textContent, "the later row says, for a reader, where its children are and what Enter does").toMatch(/first row.*Enter/);
    expect(later[0].hasAttribute("aria-describedby")).toBe(false);
    const first = () => rowsNamed()[0];
    later[1].focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(document.activeElement).toBe(first()));
    // a click on the mark goes there too
    first().blur();
    await user.click(within(rowsNamed()[1]).getByText("shown above"));
    await waitFor(() => expect(document.activeElement).toBe(first()));
  }, 60000);
});
