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

  it("starts the submission replay and links to its coverage graph", async () => {
    const guide = within(await openWithoutEngine("trace-submission"));
    expect(guide.getByLabelText("Start the local submission replay").textContent).toContain("scripts/review-flow.py serve");
    expect(guide.getByRole("link", { name: "Next" }).getAttribute("href")).toContain("guide=inspect-coverage");
    expect(guide.getByRole("combobox", { name: "Developer guide" }).querySelectorAll('optgroup[label="Submission review"] option')).toHaveLength(3);
  });

  it.each([
    ["inspect-coverage", "sattle.coverage.coverage.gkql.json", "coverage", "scripts/review-flow.py serve"],
    ["kiln-policy", "workshop.rating.gkql.json", "rating", "scripts/kiln-guide.py serve"],
    ["kiln-impact", "workshop.impact.gkql.json", "impact", "scripts/kiln-guide.py serve"],
    ["kiln-completion", "workshop.progress.gkql.json", "progress", "scripts/kiln-guide.py serve"],
  ])("opens %s from a short link with copyable GKQL and its server command", async (id, file, graph, command) => {
    const user = userEvent.setup();
    const rail = await openWithoutEngine(id);
    const guide = within(rail);
    expect([...rail.querySelectorAll("details[open] pre")].some(pre => pre.textContent?.includes(command))).toBe(true);
    expect(guide.getByRole("option", { name: "Follow one submission" })).toBeTruthy();
    await user.click(guide.getByText(file, { selector: "summary" }));
    await user.click(guide.getByRole("button", { name: "Copy " + file }));
    expect(JSON.parse(await navigator.clipboard.readText()).graph).toBe(graph);
  });

  it("opens the local CORE case and keeps its chapters on the mirror engine", async () => {
    const guide = within(await openWithoutEngine("core-import"));
    expect(guide.getByLabelText("Open the local replay").textContent).toContain("health-mirror.sh serve");
    expect(guide.getByRole("link", { name: "Next" }).getAttribute("href")).toContain("source=health-mirror");
    expect(guide.getByRole("link", { name: "Open projection editor" }).getAttribute("href")).toContain("view=editor");
  });

  it("shows the repository's record adapter code and a copyable AI prompt", async () => {
    const user = userEvent.setup();
    const guide = within(await openWithoutEngine("adapter"));
    await user.click(guide.getByText("RecordAdapter.cs"));
    expect((await guide.findByLabelText("RecordAdapter.cs")).textContent).toContain("public sealed class RecordAdapter");
    await user.click(guide.getByText("Ask your AI assistant"));
    await user.click(guide.getByRole("button", { name: "Copy AI prompt" }));
    expect(await navigator.clipboard.readText()).toContain("RecordAdapter");
  });

  it("shows the fold of the starter projection: the state it starts in and where its policy comes from", async () => {
    const guide = within(await openWithoutEngine("azure-health"));
    expect(await guide.findByText("Starter fold cpuStateFold · one-minute CPU windows, state starts Healthy, policy from cpuStatePolicy")).toBeTruthy();
  });

  it.each([["above", "8.0"], ["below", "7.0"], ["enterAfter", "2"], ["exitAfter", "3"]])("shows the fold policy column %s = %s read from the starter projection", async (name, value) => {
    const guide = within(await openWithoutEngine("azure-health"));
    const policy = await guide.findByLabelText("CPU fold policy in the starter projection");
    expect(within(policy).getByText(name).nextElementSibling?.textContent).toBe(value);
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
