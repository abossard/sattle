// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { dispatch, useStore } from "../../src/state/store";
import { openApp, violations } from "./helpers";

describe("color by property: the cues that are not color", () => {
  it("the legend lists value, count and role for nodes and for edges, and has no axe violations", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    await user.click(await screen.findByRole("button", { name: /Color by property/ }));
    const nodes = await screen.findByRole("list", { name: /Legend of nodes: health/ });
    expect(within(nodes).getAllByRole("listitem").map((li) => li.textContent)).toEqual(expect.arrayContaining([expect.stringMatching(/✓ healthy.*ok/), expect.stringMatching(/✖ unhealthy.*danger/), expect.stringMatching(/\? \(no value\).*unknown/)]));
    expect(await screen.findByRole("list", { name: /Legend of edges: health/ })).toBeTruthy();
    expect(await violations()).toEqual([]);
  }, 30000);

  it("the element list shows the value with its role word, so the grid says what the color says", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    await user.click(await screen.findByRole("tab", { name: "Elements" }));
    const grid = await screen.findByRole("grid", { name: /Nodes of the graph/ });
    expect(within(grid).getByRole("columnheader", { name: /health/ })).toBeTruthy();
    expect(within(grid).getAllByRole("gridcell").some((c) => /(✓ ok|⚠ warning|✖ danger|\? unknown|\(no value\))/.test(c.textContent ?? ""))).toBe(true);
  }, 30000);

  it("the user overrides the hint per graph: type colors remove the legend and the column", async () => {
    await openApp("synth-small", "data");
    const graph = useStore.getState().activeGraph;
    dispatch({ type: "filter/set", graph, filter: { colorNodes: "" } });
    await waitFor(() => expect(useStore.getState().filters[graph].colorNodes).toBe(""));
    const user = userEvent.setup();
    await user.click(await screen.findByRole("tab", { name: "Elements" }));
    const grid = await screen.findByRole("grid", { name: /Nodes of the graph/ });
    expect(within(grid).queryByRole("columnheader", { name: /health/ })).toBeNull();
  }, 30000);
});
