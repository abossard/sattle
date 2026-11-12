// @vitest-environment jsdom
// What each notation puts into the page for a projection of about 50 and about 500 operators, in a view of 1200 by 700.
// The element counts are exact and are the claim; the times are jsdom's on the machine that ran the test and are printed, not asserted.
import { act } from "@testing-library/react";
import type { ComponentType } from "react";
import { describe, expect, it } from "vitest";
import { allSteps } from "../../src/editing/doc";
import { docOf, perform } from "../../src/editing/draft";
import type { Gesture } from "../../src/editing/gestures";
import { BlockEditor } from "../../src/editors/BlockEditor";
import { GraphEditor } from "../../src/editors/GraphEditor";
import type { EditorProps } from "../../src/editors/host";
import { PipelineEditor } from "../../src/editors/PipelineEditor";
import { StructureEditor } from "../../src/editors/StructureEditor";
import type { MemoryDraft } from "../editing/memory-draft";
import { freshDraft, mount, SVC } from "./harness";

const VIEW = { width: 1200, height: 700 };
const notations: Record<string, ComponentType<EditorProps>> = {
  structure: (p) => <StructureEditor {...p} window={Math.ceil(VIEW.height / 28)} />,
  pipeline: PipelineEditor,
  blocks: (p) => <BlockEditor {...p} viewport={VIEW.height} />,
  graph: (p) => <GraphEditor {...p} viewport={{ width: VIEW.width - 340, height: VIEW.height }} />,
};

/** The demo's `serviceDeps` grown by row sets of five filters each, every one reading the one before it or `deps`. */
async function grown(sets: number): Promise<MemoryDraft> {
  const d = freshDraft();
  const deps = docOf(d, SVC)!.statements.find((s) => s.name === "deps")!.steps.at(-1)!.out!;
  const items: Gesture[] = [];
  for (let k = 0; k < sets; k++) for (let i = 0; i < 5; i++) {
    const from = i > 0 ? undefined : { in: k % 8 === 0 ? deps : `${SVC}/op:big${k - 1}/port:out` };
    items.push({ g: "addStep", kind: "Filter", statement: `big${k}`, from });
    items.push({ g: "addEntry", step: `${SVC}/op:big${k}`, slot: "predicate", childType: "Expr", values: { src: `s != t and n > ${i}` } });
  }
  expect(await perform(d, SVC, { g: "batch", label: "grow", items })).toMatchObject({ ok: true });
  return d;
}

const drafts = { 50: grown(6), 500: grown(96) };
const results: Record<string, Record<string, { operators: number; elements: number; mountMs: number; changeMs: number }>> = {};

describe.each([[50], [500]] as const)("a projection of about %i operators", (size) => {
  it.each(Object.keys(notations))("%s: what is in the page", async (name) => {
    const draft = await drafts[size];
    const operators = allSteps(docOf(draft, SVC)!).length;
    const t0 = performance.now();
    const { container } = mount(notations[name], SVC, draft);
    const mountMs = performance.now() - t0;
    const elements = container.querySelectorAll("*").length;
    // one change of the draft and the page that follows it: another condition in the first of the added filters
    const t1 = performance.now();
    await act(async () => { expect(await perform(draft, SVC, { g: "do", cmd: { t: "expr", id: `${SVC}/op:big0#0`, slot: "predicate", src: `s == t and n > ${Math.round(t0)}` } })).toMatchObject({ ok: true }); });
    const changeMs = performance.now() - t1;
    (results[name] ??= {})[size] = { operators, elements, mountMs: Math.round(mountMs), changeMs: Math.round(changeMs) };
    expect(elements).toBeGreaterThan(0);
  }, 60_000);
});

describe("the cost of a larger projection", () => {
  it("is stated", () => {
    console.log(JSON.stringify(results));
    for (const [name, r] of Object.entries(results)) {
      expect(r[50].operators).toBeGreaterThanOrEqual(50);
      expect(r[500].operators).toBeGreaterThanOrEqual(500);
      // ten times the operators is far from ten times the page: only what is in view is rendered
      expect(r[500].elements / r[50].elements, name).toBeLessThan(name === "pipeline" ? 4 : 2.5);
    }
  });
});
