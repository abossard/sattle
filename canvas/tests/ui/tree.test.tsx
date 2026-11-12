// @vitest-environment jsdom
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { SOURCES } from "../../src/state/sources";
import { dispatch, useStore } from "../../src/state/store";
import { TreeSource } from "../fixtures/tree-source";
import { openApp, violations } from "./helpers";

beforeAll(() => { SOURCES.push({ id: "tree-test", label: "Tree test", make: () => new TreeSource() }, { id: "tree-curated", label: "Tree curated", make: () => new TreeSource(true) }); });

const open = async () => { await openApp("tree-test", "data"); return screen.findByRole("tree", { name: "Tree of tree" }); };
const labelOf = (r: Element) => r.querySelector("[data-label]")?.textContent ?? "";
const names = (tree: HTMLElement) => within(tree).getAllByRole("treeitem").map(labelOf);
const row = (tree: HTMLElement, name: string) => within(tree).getAllByRole("treeitem").find((r) => labelOf(r) === name)!;

describe("a graph whose ontology marks a hierarchy", () => {
  it("opens as a tree, with the graph as the other tab, on the paths to what is wrong", async () => {
    const tree = await open();
    const tabs = screen.getByRole("tablist", { name: "How to show this graph" });
    const tabNames = within(tabs).getAllByRole("tab").map((t) => t.textContent ?? "");
    expect([tabNames.length, tabNames[0], tabNames[1].startsWith("Graph")]).toEqual([2, "Tree", true]);
    expect(names(tree)).toEqual(["Estate", "Region A", "api", "db", "Region B", "web", "cache", "Region C"]);
  }, 30000);

  it("a row is named by what it shows: its text and the words that stand for its glyphs, with no separate label that could differ (WCAG 2.5.3)", async () => {
    const tree = await open();
    for (const r of within(tree).getAllByRole("treeitem")) expect(r.hasAttribute("aria-label")).toBe(false);
    const text = row(tree, "api").textContent!;
    expect(text).toMatch(/^api Component.*state Unhealthy, danger/);
    // the glyphs are drawn and hidden from readers: the words stand for them
    expect([...row(tree, "api").querySelectorAll('[aria-hidden="true"]')].map((x) => x.textContent).join("")).toMatch(/✖/);
  }, 30000);

  it("says the level and position of a row, and its state in words as well as color", async () => {
    const tree = await open();
    const api = row(tree, "api");
    expect([api.getAttribute("aria-level"), api.getAttribute("aria-posinset"), api.getAttribute("aria-setsize")]).toEqual(["3", "1", "2"]);
    expect(api.textContent).toMatch(/2 supporting.*1 link.*state Unhealthy, danger/);
    expect(row(tree, "Estate").textContent).toMatch(/1 Unhealthy.*1 Degraded.*1 Unknown/);
    expect(api.textContent).toMatch(/✖ state Unhealthy, danger/);
  }, 30000);

  it("collapses and expands with the arrow keys, and moves with the arrows, Home and End over the whole list", async () => {
    const user = userEvent.setup();
    const tree = await open();
    act(() => row(tree, "Region A").focus());
    await user.keyboard("{ArrowLeft}");
    await waitFor(() => expect(names(tree)).not.toContain("api"));
    await user.keyboard("{ArrowRight}");
    await waitFor(() => expect(names(tree)).toContain("api"));
    await user.keyboard("{ArrowRight}");
    await waitFor(() => expect(document.activeElement).toBe(row(tree, "api")));
    await user.keyboard("{End}");
    await waitFor(() => expect(labelOf(document.activeElement as HTMLElement)).toBe("Region C"));
    await user.keyboard("{Home}");
    await waitFor(() => expect(labelOf(document.activeElement as HTMLElement)).toBe("Estate"));
  }, 30000);

  it("a click selects a row and the chevron opens and closes it", async () => {
    const user = userEvent.setup();
    const tree = await open();
    await user.click(row(tree, "Region A"));
    expect(names(tree)).toContain("api");
    expect(useStore.getState().selection?.id).toBe("ra");
    await user.click(row(tree, "Region A").querySelector(".fui-TreeItemLayout__expandIcon")!);
    await waitFor(() => expect(names(tree)).not.toContain("api"));
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("type-ahead moves to the next row that starts with the typed letters", async () => {
    const user = userEvent.setup();
    const tree = await open();
    act(() => row(tree, "Estate").focus());
    await user.keyboard("we");
    await waitFor(() => expect(labelOf(document.activeElement as HTMLElement)).toBe("web"));
  }, 30000);

  it("only where something is wrong keeps the paths to the wrong nodes", async () => {
    const user = userEvent.setup();
    const tree = await open();
    await user.click(screen.getByRole("switch", { name: "Only where something is wrong" }));
    await waitFor(() => expect(names(tree)).toEqual(["Estate", "Region A", "api", "db", "Region B", "web"]));
  }, 30000);

  it("a search shows the path to a match and the count of what it found", async () => {
    const user = userEvent.setup();
    const tree = await open();
    await user.type(screen.getByRole("searchbox", { name: "Find in the tree" }), "cache");
    await waitFor(() => expect(names(tree)).toEqual(["Estate", "Region B", "cache"]));
    expect(screen.getByText(/· 1 found/)).toBeTruthy();
  }, 30000);

  it("sorts children by name on request", async () => {
    const user = userEvent.setup();
    const tree = await open();
    await user.selectOptions(screen.getByRole("combobox", { name: "Sort children" }), "label");
    await waitFor(() => expect(names(tree).slice(1, 3)).toEqual(["Region A", "api"]));
    await user.selectOptions(screen.getByRole("combobox", { name: "Sort children" }), "severity");
  }, 30000);

  it("selecting a row is the shared selection: a click, and Enter from the keyboard", async () => {
    const user = userEvent.setup();
    const tree = await open();
    await user.click(row(tree, "api"));
    expect(useStore.getState().selection).toEqual({ graph: "tree", kind: "entity", id: "api" });
    act(() => row(tree, "db").focus());
    await user.keyboard("{Enter}");
    expect(useStore.getState().selection).toEqual({ graph: "tree", kind: "entity", id: "db" });
    expect(row(tree, "db").getAttribute("aria-selected")).toBe("true");
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("the Inspector says why a row has its state: what drives the roll-up, what supports it, what depends on it", async () => {
    const user = userEvent.setup();
    const tree = await open();
    await user.click(row(tree, "Region A"));
    const inspector = await screen.findByRole("complementary", { name: "Inspector" });
    await waitFor(() => expect(within(inspector).getByText("Why this state")).toBeTruthy());
    expect(within(within(inspector).getByRole("list", { name: "Children that drive the roll-up" })).getByText("api")).toBeTruthy();
    await user.click(row(tree, "api"));
    await waitFor(() => expect(within(inspector).getByRole("heading", { level: 2 }).textContent).toMatch(/api/));
    await waitFor(() => expect(within(inspector).getByRole("list", { name: "Supporting elements" })).toBeTruthy());
    expect(within(within(inspector).getByRole("list", { name: "Supporting elements" })).getAllByRole("listitem").map((li) => li.textContent)).toEqual([expect.stringContaining("restart"), expect.stringContaining("oom")]);
    expect(within(within(inspector).getByRole("list", { name: "Links to other nodes" })).getByText("web")).toBeTruthy();
    dispatch({ type: "select/element", selection: null });
  }, 30000);

  it("marks the rows that changed in the revision the timeline shows, and moves back with the timeline", async () => {
    const tree = await open();
    await waitFor(() => expect(row(tree, "db").textContent).toMatch(/changed in this revision/));
    expect(row(tree, "api").textContent).not.toMatch(/in this revision/);
    expect(row(tree, "db").textContent).toMatch(/state Degraded, warning/);
    // the timeline moves the tree: at the first revision db is healthy again and nothing is marked
    await act(async () => { dispatch({ type: "time/seek", t: useStore.getState().time.first }); });
    await waitFor(() => expect(row(tree, "db").textContent).toMatch(/state Healthy, ok/));
    expect(row(tree, "db").textContent).not.toMatch(/in this revision/);
  }, 30000);

  it.each([["no row was touched: the rows are those of the revision", false], ["the reader closed a row: it stays closed", true]])("after a scrub to the first revision and back, %s", async (_, touch) => {
    const user = userEvent.setup();
    const tree = await open();
    const before = names(tree);
    if (touch) { act(() => row(tree, "Region A").focus()); await user.keyboard("{ArrowLeft}"); await waitFor(() => expect(names(tree)).not.toContain("api")); }
    const shown = names(tree);
    await act(async () => { dispatch({ type: "time/seek", t: useStore.getState().time.first }); });
    await act(async () => { dispatch({ type: "time/seek", t: useStore.getState().time.last }); });
    await waitFor(() => expect(names(tree)).toEqual(touch ? shown : before));
  }, 30000);

  it("has no axe violations", async () => {
    await open();
    expect(await violations()).toEqual([]);
  }, 30000);

  it("curated rows say so: the record, critical, hidden behind a switch, and the policy in the Inspector", async () => {
    const user = userEvent.setup();
    await openApp("tree-curated", "data");
    const tree = await screen.findByRole("tree", { name: "Tree of tree" });
    expect(names(tree)).not.toContain("cache");
    expect(row(tree, "api").textContent).toMatch(/curated by cur-7/);
    expect(row(tree, "db").textContent).toMatch(/critical/);
    await user.click(screen.getByRole("switch", { name: "Show hidden (1)" }));
    await waitFor(() => expect(names(tree)).toContain("cache"));
    expect(row(tree, "cache").textContent).toMatch(/hidden/);
    await user.click(row(tree, "api"));
    const inspector = await screen.findByRole("complementary", { name: "Inspector" });
    await waitFor(() => expect(within(inspector).getByRole("list", { name: "Roll-up policy and curation" }).textContent).toMatch(/share \(share 0\.5\).*Curated by record cur-7/));
    dispatch({ type: "select/element", selection: null });
  }, 30000);
});
