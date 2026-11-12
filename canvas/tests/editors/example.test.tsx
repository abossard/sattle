// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { docOf, perform } from "../../src/editing/draft";
import { around, conditions, edgeText, generalise, matchGesture, nodeText } from "../../src/editing/example";
import { parseExpr } from "../../src/editor/expr";
import { ExampleEditor } from "../../src/editors/ExampleEditor";
import type { EditorProps } from "../../src/editors/host";
import { dangling, demoData } from "../editing/memory-draft";
import { freshDraft, mount, SVC, violations } from "./harness";

const MASTER = `${SVC}/port:master`;
const data = demoData();
const rels = [...data.relationships()];
const steps = (draft: Parameters<typeof docOf>[0], name: string) => docOf(draft, SVC)!.statements.find((s) => s.name === name)?.steps ?? [];
const entries = (draft: Parameters<typeof docOf>[0], id: string) => docOf(draft, SVC)!.statements.flatMap((s) => s.steps).find((s) => s.id === id)!.slots.flatMap((s) => s.entries);
/** A call between two pods and the services behind them: the example `deps` of the demo was written from. */
const call = rels.find((r) => r.type === "Calls" && data.entity(r.source)?.type === "Pod" && data.entity(r.target)?.type === "Pod")!;
const backs = (pod: string) => rels.find((r) => r.type === "Backs" && r.source === pod)!;

describe("a pattern from an example", () => {
  const cat = new Catalog(freshDraft().graph);

  it("two pods that call each other and the services they back give the pattern the demo's `deps` matches", async () => {
    const draft = freshDraft();
    const p = generalise(cat, MASTER, data, [call.id, backs(call.source).id, backs(call.target).id]);
    expect(p.unknown).toEqual([]);
    expect(p.nodes.map(nodeText).slice(0, 2)).toEqual(["(p: Pod)", "(p2: Pod)"]);
    expect(p.edges.map(edgeText)).toContain("(p)-[:Calls]->(p2)");
    const done = await perform(draft, SVC, matchGesture(p, MASTER, "byExample", [])!);
    expect(done).toMatchObject({ ok: true });
    expect(dangling(draft.graph)).toEqual([]);
    const types = (id: string) => entries(draft, id).map((e) => `${e.type} ${e.refs.find((r) => r.rel === "ofType")?.target}`).sort();
    const written = steps(draft, "deps")[0];
    // the same pattern as the hand-written one when the two pods back different services
    if (p.nodes.length === 4) expect(types(steps(draft, "byExample")[0].id)).toEqual(types(written.id));
    expect(steps(draft, "byExample")[0].inputs.map((i) => i.fromName)).toEqual(["master"]);
    expect(draft.log.length).toBe(1);
  });

  it.each([...new Set(rels.map((r) => r.type))].map((t) => [t, rels.find((r) => r.type === t)!.id]))("one %s relationship is a pattern of its two ends, or names what the ontology lacks", async (_type, id) => {
    const p = generalise(cat, MASTER, data, [id]);
    const r = data.rel(id)!;
    expect(p.nodes.map((n) => n.example.id)).toEqual([...new Set([r.source, r.target])]);
    expect(p.edges.length).toBe(1);
    const g = matchGesture(p, MASTER, "one", []);
    if (p.unknown.length) return expect(g).toBeNull();
    const draft = freshDraft();
    expect(await perform(draft, SVC, g!)).toMatchObject({ ok: true });
    expect(dangling(draft.graph)).toEqual([]);
    expect(entries(draft, steps(draft, "one")[0].id).length).toBe(p.nodes.length + 1);
  });

  it("offers a condition per value of the example, each an expression the engine's grammar reads", () => {
    const cs = conditions(generalise(cat, MASTER, data, [call.id]));
    expect(cs.length).toBeGreaterThan(0);
    for (const c of cs) expect(parseExpr(c.text)).toMatchObject({ ok: true });
  });

  it("a condition becomes a step after the match, in the same change", async () => {
    const draft = freshDraft();
    const p = generalise(cat, MASTER, data, [call.id]);
    const where = conditions(p)[0].text;
    expect(await perform(draft, SVC, matchGesture(p, MASTER, "narrow", [where])!)).toMatchObject({ ok: true });
    const st = steps(draft, "narrow");
    expect(st.length).toBe(2);
    expect(st[1].slots.flatMap((s) => s.entries).map((e) => e.expr)).toEqual([where]);
    expect(draft.log.length).toBe(1);
  });

  it("the example grows along the relationships of what is picked, and not by what is picked already", () => {
    const near = around(data, [call.source]);
    expect(near.some((n) => n.rel.id === call.id && n.outward)).toBe(true);
    expect(around(data, [call.id]).some((n) => n.rel.id === call.id)).toBe(false);
    expect(around(data, [call.source], 2).length).toBeLessThanOrEqual(2);
  });
});

describe("the example editor", () => {
  const WithData = (p: EditorProps) => <ExampleEditor {...p} examples={{ [MASTER]: data }} />;
  const Without = (p: EditorProps) => <ExampleEditor {...p} examples={{}} />;

  it("says so when no data is open", () => {
    mount(Without);
    expect(screen.getByText(/No data is open/)).toBeTruthy();
  });

  it("builds a row set from picks: an entity, a relationship next to it, a condition", async () => {
    const user = userEvent.setup();
    const { draft } = mount(WithData);
    expect(await violations()).toEqual([]);
    await user.type(screen.getByRole("textbox", { name: "Find in master" }), "pod");
    const label = `Pod ${data.label(data.entity(call.source)!)}`;
    await user.click(within(screen.getByRole("list", { name: "Entities" })).getByRole("checkbox", { name: label }));
    const near = screen.getByRole("list", { name: "Next to the example" });
    await user.click(within(near).getByRole("checkbox", { name: `-[:Calls]-> Pod ${data.label(data.entity(call.target)!)}` }));
    const pattern = screen.getByRole("list", { name: "Pattern elements" });
    expect(within(pattern).getAllByRole("listitem").map((li) => li.textContent)).toEqual(expect.arrayContaining([expect.stringContaining("(p: Pod)"), expect.stringContaining("(p2: Pod)"), expect.stringContaining("(p)-[:Calls]->(p2)")]));
    const offered = within(screen.getByRole("combobox", { name: "Keep only rows where" })).getAllByRole("option").map((o) => (o as HTMLOptionElement).value).filter(Boolean);
    await user.selectOptions(screen.getByRole("combobox", { name: "Keep only rows where" }), offered[0]);
    expect(await violations()).toEqual([]);
    await user.type(screen.getByRole("textbox", { name: "Name of the row set" }), "calls");
    await user.click(screen.getByRole("button", { name: "Add as row set" }));
    await waitFor(() => expect(steps(draft, "calls").length).toBe(2));
    expect(draft.log.length).toBe(1);
    expect(within(screen.getByRole("list", { name: "Pattern elements" })).queryAllByRole("listitem")).toEqual([]);
  });

  it("taking a line out of the pattern takes the pick back", async () => {
    const user = userEvent.setup();
    const Picked = (p: EditorProps) => <ExampleEditor {...p} examples={{ [MASTER]: data }} picked={[call.id]} />;
    mount(Picked);
    await user.click(screen.getByRole("button", { name: "remove (p)-[:Calls]->(p2)" }));
    expect(within(screen.getByRole("list", { name: "Pattern elements" })).queryAllByRole("listitem")).toEqual([]);
  });
});
