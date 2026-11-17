// @vitest-environment jsdom
// A projection opens in the editor of its file's language (PROTOCOL 3.50): `kql` in the KQL editor, any other language as its text, read-only.
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
    ["gkql", false],
    ["KQL", false],
    ["", false],
  ])("%j opens editable: %s", async (language, editable) => {
    const Editor = editorFor(language);
    render(<Suspense fallback={null}><Editor port={port} file={{ projection: "m.p", file: `m.p.${language}`, language }} /></Suspense>);
    expect((await screen.findAllByText((_, e) => e?.textContent === text)).length).toBeGreaterThan(0);
    expect(screen.queryByRole("textbox", { name: "KQL text of m.p" }) !== null).toBe(editable);
    expect(screen.queryByText(/^The canvas has no editor for .*projections, so this text is read-only\.$/) === null).toBe(editable);
  });
});
