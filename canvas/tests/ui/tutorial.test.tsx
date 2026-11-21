// @vitest-environment jsdom
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { initialState } from "../../src/state/reducer";
import { useStore } from "../../src/state/store";
import { App } from "../../src/ui/App";
import { violations } from "./helpers";

const openWithoutEngine = (guide: string) => {
  useStore.setState({ ...initialState(), sourceId: "engine", guide, load: { status: "error", message: "Engine unavailable", ms: 0 } });
  render(<App />);
  return screen.findByRole("complementary", { name: "Developer guide" });
};

describe("developer guide", () => {
  it("shows the startup commands without a running engine", async () => {
    const rail = await openWithoutEngine("start");
    const guide = within(rail);
    expect(guide.getByRole("heading", { name: "Start the engine and master graph" })).toBe(document.activeElement);
    expect(guide.getByText("Replay captured Azure and Kubernetes data into one graph.")).toBeTruthy();
    expect(guide.getByRole("link", { name: "Identity finds the same object" }).getAttribute("href")).toContain("guide=explore");
    expect(guide.getByLabelText("Prepare and serve").textContent).toContain("./scripts/tutorial.sh serve");
    expect(guide.getByRole("combobox", { name: "Developer guide" }).querySelectorAll('optgroup[label="Azure and Kubernetes"] option')).toHaveLength(11);
    expect(guide.getByRole("link", { name: "Next" }).getAttribute("href")).toContain("guide=adapter");
    expect(await violations(rail)).toEqual([]);
  });

  it("opens the local CORE case and keeps its chapters on the mirror engine", async () => {
    const guide = within(await openWithoutEngine("core-import"));
    expect(guide.getByLabelText("Open the local replay").textContent).toContain("health-mirror.sh serve");
    expect(guide.getByRole("link", { name: "Next" }).getAttribute("href")).toContain("source=health-mirror");
    expect(guide.getByRole("link", { name: "Open projection editor" }).getAttribute("href")).toContain("view=editor");
  });

  it("shows the repository's Azure adapter code and a copyable AI prompt", async () => {
    const user = userEvent.setup();
    const guide = within(await openWithoutEngine("adapter"));
    await user.click(guide.getByText("Adapters.cs"));
    expect((await guide.findByLabelText("Adapters.cs")).textContent).toContain("public sealed class AzureResourceGraphAdapter");
    await user.click(guide.getByText("Ask your AI assistant"));
    await user.click(guide.getByRole("button", { name: "Copy AI prompt" }));
    expect(await navigator.clipboard.readText()).toContain("AzureResourceGraphAdapter");
  });

  it("shows threshold rules read from the starter projection", async () => {
    const guide = within(await openWithoutEngine("azure-health"));
    const gate = await guide.findByLabelText("CPU hysteresis in the starter projection");
    expect(gate.textContent).toContain("Degraded≥ 8% × 2");
    expect(gate.textContent).toContain("Healthy≤ 7% × 3");
  });

  it.each([
    ["first-projection", "workloads", "firstWorkloads", ["master"], "ServiceCalls"],
    ["second-projection", "placement", "firstPlacement", ["workloads", "master"], "RunsOnVmss"],
    ["branch", "colocation", "firstColocation", ["placement"], "SharesVmssWith"],
  ])("copies the ready-to-paste %s projection recipe without the engine", async (lesson, graph, exercise, inputs, relationship) => {
    const user = userEvent.setup();
    const guide = within(await openWithoutEngine(lesson));
    const file = `tutorial.${graph}.gkql.json`;
    await user.click(guide.getByText(file, { selector: "summary" }));
    const code = JSON.parse((await guide.findByLabelText(file)).textContent!);
    expect(code.graph).toBe(graph);
    expect(code.inputs.map((input: { name: string }) => input.name)).toEqual(inputs);
    expect(code.verbs.flatMap((verb: { target?: { edges?: { label: string }[] } }) => verb.target?.edges?.map((edge) => edge.label) ?? [])).toContain(relationship);
    await user.click(guide.getByRole("button", { name: "Copy GKQL" }));
    expect(JSON.parse(await navigator.clipboard.readText())).toEqual({ ...code, graph: exercise });
  });
});
