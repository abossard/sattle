// @vitest-environment jsdom
// A projection opens in the editor of its file's language (PROTOCOL 3.50). Unsupported languages use the read-only fallback.
import { cleanup, render, screen } from "@testing-library/react";
import { Suspense } from "react";
import { afterEach, describe, expect, it } from "vitest";
import type { EditorService } from "../../src/editor/service";
import { editorFor } from "../../src/ui/editor/languages";

const text = "graph p : m;";
/** Both reads answer the same text; the KQL editor's checks never answer, so it shows the text alone. */
const port = { kqlText: async () => ({ digest: "", text }), definitionText: async () => text, kqlAnalyse: () => new Promise(() => {}), kqlComplete: () => new Promise(() => {}), kqlHover: () => new Promise(() => {}) } as unknown as EditorService;

afterEach(cleanup);

describe("the editor of a projection's language", () => {
  it.each([
    ["kql", true],
    ["unknown", false],
    ["KQL", false],
    ["", false],
  ])("%j opens editable: %s", async (language, editable) => {
    const Editor = editorFor(language);
    render(<Suspense fallback={null}><Editor port={port} file={{ projection: "m.p", file: `m.p.${language}`, language }} /></Suspense>);
    // shown, not only in the page: the KQL editor builds its text into a body that stays `hidden` until React renders it ready
    expect((await screen.findAllByText((_, e) => e?.textContent === text && !e.closest("[hidden]"))).length).toBeGreaterThan(0);
    expect(screen.queryByRole("textbox", { name: "KQL text of m.p" }) !== null).toBe(editable);
    expect(screen.queryByText(/^The canvas has no editor for .*projections, so this text is read-only\.$/) === null).toBe(editable);
  });
  it("opens gkql as an authored graph", async () => {
    const Editor = editorFor("gkql");
    const source = JSON.stringify({ graph: "p", ontology: "m", inputs: [], parameters: [], verbs: [], wires: [] });
    render(<Suspense fallback={null}><Editor port={port} file={{ projection: "m.p", file: "m.p.gkql.json", language: "gkql" }} source={source} /></Suspense>);
    expect(await screen.findByRole("region", { name: "GKQL operations and wires" })).toBeTruthy();
  });
});
