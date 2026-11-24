// @vitest-environment jsdom
// A projection opens in the editor of its file's language (PROTOCOL 3.50). Unsupported languages use the read-only fallback.
import { cleanup, render, screen } from "@testing-library/react";
import { Suspense } from "react";
import { afterEach, describe, expect, it } from "vitest";
import type { EditorService } from "../../src/editor/service";
import { editorFor } from "../../src/ui/editor/languages";

const text = "graph p : m;";
const port = { definitionText: async () => text } as unknown as EditorService;

afterEach(cleanup);

describe("the editor of a projection's language", () => {
  it.each([
    ["unknown"],
    ["kql"],
    [""],
  ])("%j opens as read-only text", async (language) => {
    const Editor = editorFor(language);
    render(<Suspense fallback={null}><Editor port={port} file={{ projection: "m.p", file: `m.p.${language}`, language }} /></Suspense>);
    expect((await screen.findAllByText((_, e) => e?.textContent === text)).length).toBeGreaterThan(0);
    expect(screen.queryByText(/^The canvas has no editor for .*projections, so this text is read-only\.$/)).not.toBeNull();
  });
  it("opens gkql as an authored graph", async () => {
    const Editor = editorFor("gkql");
    const source = JSON.stringify({ graph: "p", ontology: "m", inputs: [], parameters: [], verbs: [], wires: [] });
    render(<Suspense fallback={null}><Editor port={port} file={{ projection: "m.p", file: "m.p.gkql.json", language: "gkql" }} source={source} /></Suspense>);
    expect(await screen.findByRole("region", { name: "GKQL operations and wires" })).toBeTruthy();
  });
});
