// @vitest-environment jsdom
// Tab order, arrow keys in the tab list, focus return and visible focus depend on layout and Fluent's focus manager, which jsdom does not provide:
// scripts/a11y-cdp.mjs checks them in Chrome.
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { runtime } from "../../src/state/runtime";
import { useStore } from "../../src/state/store";
import { openApp } from "./helpers";

describe("keyboard only", () => {
  it("? opens the shortcut list and Escape closes it", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    screen.getByRole("button", { name: "Keyboard shortcuts" }).focus();
    await user.keyboard("?");
    const dialog = await screen.findByRole("dialog", { name: "Keyboard shortcuts" });
    expect(within(dialog).getByRole("table")).toBeTruthy();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(useStore.getState().dialog).toBeNull();
  }, 20000);

  it("the element list selects with the keyboard and the inspector follows (the canvas alternative)", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    await user.click(await screen.findByRole("tab", { name: "Elements" }));
    const grid = await screen.findByRole("grid", { name: /Nodes of the graph/ });
    const first = within(grid).getAllByRole("row")[1];
    first.focus();
    await user.keyboard(" ");
    await waitFor(() => expect(useStore.getState().selection).not.toBeNull());
    const sel = useStore.getState().selection!;
    expect(runtime.ws!.selection).toEqual(sel);
    expect(sel.kind).toBe("entity");
    await screen.findByRole("heading", { level: 2, name: /./ });
    expect(screen.getByText(sel.id)).toBeTruthy();
    // the row stays selected when the selection comes from elsewhere
    expect(within(grid).getAllByRole("row").some((r) => r.getAttribute("aria-selected") === "true")).toBe(true);
  }, 30000);

  it("connections in the inspector are a tree that navigates with Enter", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    await user.click(await screen.findByRole("tab", { name: "Elements" }));
    const grid = await screen.findByRole("grid", { name: /Nodes of the graph/ });
    within(grid).getAllByRole("row")[1].focus();
    await user.keyboard(" ");
    const tree = await screen.findByRole("tree", { name: "Connections by relationship type" });
    const branch = within(tree).getAllByRole("treeitem")[0];
    branch.focus();
    await user.keyboard("{ArrowRight}"); // open
    const items = within(tree).getAllByRole("treeitem");
    expect(items.length).toBeGreaterThan(1);
    const before = useStore.getState().selection!.id;
    items[1].focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(useStore.getState().selection!.id).not.toBe(before));
  }, 30000);
});
