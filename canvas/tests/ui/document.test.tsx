// @vitest-environment jsdom
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { runtime } from "../../src/state/runtime";
import { dispatch } from "../../src/state/store";
import type { GraphSummary } from "../../src/state/types";
import { DocumentView } from "../../src/ui/DocumentView";
import { fakeWorkspace } from "../helpers";

afterEach(() => { cleanup(); runtime.ws = null; });

const MD: Record<string, string> = {
  "1": "# Report\n\n- Items: 1\n\n| Name | Count |\n| --- | --- |\n| a | 1 |\n",
  "2": "# Report\n\n- Items: 2\n\n| Name | Count |\n| --- | --- |\n| a | 1 |\n| b \\| c | 1 |\n",
};
function fake(cursor: { at: number }, asked: string[], fail = false) {
  runtime.ws = {
    ...fakeWorkspace({ r: { get cursor() { return cursor.at; }, revisionAt: (i: number) => String(i) } }),
    source: { document: async (_g: string, rev?: string) => { asked.push(String(rev)); if (fail) throw new Error("engine down"); return { revision: String(rev), digest: `sha256:${rev}`.padEnd(30, "0"), markdown: MD[String(rev)] ?? "\n" }; } },
  } as never;
}
const summary = (version: number): GraphSummary => ({ id: "r", role: "projection", head: "2", patches: 2, nodes: 0, edges: 0, version });
const show = () => render(<FluentProvider theme={webLightTheme}><DocumentView graph="r" /></FluentProvider>);

describe("the document view shows the document at the revision of the timeline, or says why there is none", () => {
  it("shows the document at the revision of the timeline and follows it when a patch arrives", async () => {
    const cursor = { at: 1 }, asked: string[] = [];
    fake(cursor, asked);
    dispatch({ type: "data/summary", graphs: [summary(0)], telemetry: {} });
    show();
    await screen.findByText("Items: 1");
    expect(screen.getByRole("heading", { level: 1, name: "Report" })).toBeTruthy();
    cursor.at = 2;
    act(() => dispatch({ type: "data/summary", graphs: [summary(1)], telemetry: {} }));
    await screen.findByText("Items: 2");
    expect(screen.getByRole("cell", { name: "b | c" })).toBeTruthy();
    expect(asked).toEqual(["1", "2"]);
  });
  it("an empty revision says so", async () => {
    fake({ at: 0 }, []);
    dispatch({ type: "data/summary", graphs: [summary(0)], telemetry: {} });
    show();
    await screen.findByText(/The document is empty at revision 0/);
  });
  it("an engine that fails gives a message, not a blank page", async () => {
    fake({ at: 1 }, [], true);
    dispatch({ type: "data/summary", graphs: [summary(0)], telemetry: {} });
    show();
    await waitFor(() => expect(screen.getByText("engine down")).toBeTruthy());
  });
  it("switching to another document graph at the same revision does not show the first graph's document while the second loads", async () => {
    const hist = { cursor: 1, revisionAt: (i: number) => String(i) };
    runtime.ws = {
      ...fakeWorkspace({ r: hist, s: hist }),
      source: { document: (g: string, rev?: string) => g === "s" ? new Promise(() => {}) : Promise.resolve({ revision: String(rev), digest: "sha256:".padEnd(30, "0"), markdown: "# Report of r\n" }) },
    } as never;
    const ui = (g: string) => <FluentProvider theme={webLightTheme}><DocumentView graph={g} /></FluentProvider>;
    const view = render(ui("r"));
    await screen.findByText("Report of r");
    view.rerender(ui("s"));
    expect(screen.queryByText("Report of r"), "the document of r stays on screen under s: only the revision number is compared").toBeNull();
  });
});
