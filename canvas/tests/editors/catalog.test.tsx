// @vitest-environment jsdom
// Every notation on every projection of a current catalog: it renders, shows every step that is in view, and has no accessibility violation.
import { screen } from "@testing-library/react";
import type { ComponentType } from "react";
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { allSteps } from "../../src/editing/doc";
import { docOf } from "../../src/editing/draft";
import { BlockEditor } from "../../src/editors/BlockEditor";
import { GraphEditor } from "../../src/editors/GraphEditor";
import type { EditorProps } from "../../src/editors/host";
import { PipelineEditor } from "../../src/editors/PipelineEditor";
import { StructureEditor } from "../../src/editors/StructureEditor";
import { estateCatalog, estatePalette } from "../editing/estate";
import { memoryDraft } from "../editing/memory-draft";
import { mount, violations } from "./harness";

const draft = memoryDraft(estateCatalog(), estatePalette);
const projections = new Catalog(draft.graph).projections().map((p) => p.id).sort();
const notations: [string, ComponentType<EditorProps>, (name: string) => HTMLElement][] = [
  ["structure", StructureEditor, (name) => screen.getByRole("tree", { name: `Structure of ${name}` })],
  ["pipeline", PipelineEditor, () => screen.getByRole("tablist", { name: "Row sets" })],
  ["blocks", BlockEditor, () => screen.getByRole("group", { name: "Blocks" })],
  ["graph", GraphEditor, (name) => screen.getByRole("group", { name: `Graph of ${name}` })],
];

describe.each(projections)("%s", (id) => {
  const doc = docOf(draft, id)!;
  it.each(notations)("in the %s notation", async (_n, Editor, root) => {
    const { container } = mount(Editor, id, draft);
    expect(root(doc.name)).toBeTruthy();
    // every step of the document has its row, block or card in the page (the pipeline shows the steps of one row set)
    const shown = new Set([...container.querySelectorAll<HTMLElement>("[data-row-id], [data-step], [data-card]")].flatMap((e) => [e.dataset.rowId, e.dataset.step, e.dataset.card]));
    const expected = Editor === PipelineEditor ? doc.statements[0].steps : allSteps(doc);
    for (const s of expected) expect(shown.has(s.id), s.name).toBe(true);
    expect(await violations()).toEqual([]);
  }, 60_000);
});
