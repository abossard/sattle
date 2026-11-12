// @vitest-environment jsdom
// The tree of the demo workload at the database fault, reached the way a person scrubs: it opens on the paths to where the problems start.
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { dispatch } from "../../src/state/store";
import { openApp } from "./helpers";

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
beforeAll(() => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const text = files[`/public${new URL(String(input), "http://localhost/").pathname}`];
    return text === undefined ? new Response("", { status: 404 }) : new Response(text);
  }) as typeof fetch;
});

const labelOf = (r: Element) => r.querySelector("[data-label]")?.textContent ?? "";
const row = (tree: HTMLElement, name: string) => within(tree).getAllByRole("treeitem").find((r) => labelOf(r) === name)!;

describe("the tree of the demo workload, reached by scrubbing", () => {
  it("at the database fault, opens on the paths to the server and to the crash-looping pod, each marked as an origin", async () => {
    const user = userEvent.setup();
    await openApp("demo", "data");
    await act(async () => { dispatch({ type: "ui/activeGraph", graph: "workload" }); });
    // in steps from the start to ten minutes (revision 10)
    for (let t = 0; t <= 600; t += 60) await act(async () => { dispatch({ type: "time/seek", t }); });
    await user.click(await screen.findByRole("tab", { name: /^Tree/ }));
    const tree = await screen.findByRole("tree", { name: "Tree of workload" });
    await waitFor(() => expect(within(tree).getAllByRole("treeitem").filter((r) => /origin/.test(r.textContent ?? "")).map(labelOf)).toEqual(expect.arrayContaining(["pg-shop", "web-7d4-3"])));
    expect(row(tree, "database").textContent).toMatch(/through pg-shop.*same element, under 2/);
  }, 60000);
});
