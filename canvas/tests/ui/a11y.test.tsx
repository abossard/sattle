// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import { openApp, openDialog, violations } from "./helpers";

describe("axe: no violations", () => {
  it.each([["data"], ["space"], ["ontology"], ["editor"]] as const)("view %s", async (view) => {
    await openApp("synth-small", view);
    expect(await violations()).toEqual([]);
  });

  it.each([["light"], ["dark"], ["contrast"]] as const)("data view in the %s theme", async (theme) => {
    await openApp("synth-small", "data", theme);
    expect(await violations()).toEqual([]);
  });

  it.each([["shortcuts"], ["rejections"]] as const)("dialog %s", async (dialog) => {
    await openApp("synth-small", "data");
    openDialog(dialog);
    await waitFor(() => expect(screen.getByRole("dialog")).toBeTruthy());
    expect(await violations()).toEqual([]);
  });

  it("the element list and the inspector with a selection", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    await user.click(await screen.findByRole("tab", { name: "Elements" }));
    await screen.findByRole("grid", { name: /Nodes of the graph/ });
    const ws = runtime.ws!;
    const graph = useStore.getState().activeGraph;
    const id = ws.histories.get(graph)!.graph.entities().next().value!.id;
    dispatch({ type: "select/element", selection: { graph, kind: "entity", id } });
    await screen.findByRole("heading", { level: 2, name: /./ });
    expect(await violations()).toEqual([]);
  }, 60000);   // a ceiling for a busy machine: axe over the whole page with a grid and the inspector is the slowest check here
});
