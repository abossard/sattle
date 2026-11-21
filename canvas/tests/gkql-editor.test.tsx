// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, it, vi } from "vitest";
import { readGkql, type GkqlProgram } from "../src/editor/gkql";
import { EditorService } from "../src/editor/service";
import GkqlEditor from "../src/ui/editor/GkqlEditor";

const base: GkqlProgram = {
  graph: "health", ontology: "tutorial",
  inputs: [{ name: "master", ontology: "tutorial" }], parameters: [],
  verbs: [
    { id: "all", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n", label: "Resource" }] } },
    { id: "live", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n", label: "Resource", props: { ready: { lit: true } } }] } },
    { id: "choose", verb: "where", condition: { lit: true } },
    { id: "cpuState", verb: "hysteresis", hysteresis: { by: ["n"], value: "cpu", above: 8, enterAfter: 2, below: 7, exitAfter: 2, high: "Degraded", low: "Healthy", initial: "Healthy" } },
  ],
  wires: [{ from: "all", to: "choose" }, { from: "choose", to: "cpuState" }],
};
const metadata = { projection: "tutorial.health", language: "gkql", file: "tutorial.health.gkql.json" };
afterEach(cleanup);

it("opens a guided projection in the source tab with its recovered text", () => {
  const recovered = JSON.stringify({ ...base, graph: "firstHealth" });
  render(<GkqlEditor file={metadata} source={JSON.stringify(base)} recoveredText={recovered} initialTab="source" port={new EditorService("http://localhost/engine")} />);
  expect(screen.getByRole("tab", { name: "GKQL" }).getAttribute("aria-selected")).toBe("true");
  expect((screen.getByRole("textbox", { name: "GKQL source" }) as HTMLTextAreaElement).value).toBe(recovered);
});

it("loads the tutorial starter as an editable local change", async () => {
  const user = userEvent.setup();
  const starter = JSON.stringify(base);
  const onEdit = vi.fn();
  const store = vi.fn().mockResolvedValue(null);
  render(<GkqlEditor file={metadata} source={JSON.stringify({ ...base, verbs: [], wires: [] })} initialTab="source" starter={starter} port={new EditorService("http://localhost/engine")} onEdit={onEdit} store={store} />);
  await user.click(screen.getByRole("button", { name: "Load starter GKQL" }));
  expect((screen.getByRole("textbox", { name: "GKQL source" }) as HTMLTextAreaElement).value).toBe(starter);
  expect(onEdit).toHaveBeenCalledWith(starter);
  expect(store).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  expect(store).toHaveBeenCalledWith(starter);
});

function editor() {
  const save = vi.fn();
  function Draft() {
    const [source, setSource] = useState(JSON.stringify(base));
    return <GkqlEditor file={metadata} source={source} port={new EditorService("http://localhost/engine")} store={async (text) => {
      save(readGkql(text));
      setSource(text);
      return null;
    }} />;
  }
  render(<Draft />);
  return { save, user: userEvent.setup() };
}

it("draws authored operations and stores a reconnected wire", async () => {
  const { save, user } = editor();
  expect(screen.getByRole("region", { name: "GKQL operations and wires" })).toBeTruthy();
  await user.click(screen.getByRole("button", { name: "where choose" }));
  await user.selectOptions(screen.getByRole("combobox", { name: "Input operation" }), "live");
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(save.mock.calls[0][0].wires).toEqual([{ from: "live", to: "choose" }, { from: "choose", to: "cpuState" }]);
  expect(await screen.findByText("Projection checked")).toBeTruthy();
});

it("starts a match with a typed pattern node", async () => {
  const { save, user } = editor();
  await user.click(screen.getByRole("button", { name: "Add operation" }));
  await user.click(screen.getByRole("button", { name: "Add node" }));
  await user.type(screen.getByRole("textbox", { name: "Node type" }), "Resource");
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(save.mock.calls[0][0].verbs.at(-1)).toEqual({
    id: "graph_match", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n1", label: "Resource" }] },
  });
});

it("edits the two hysteresis recovery settings through the Canvas fields", async () => {
  const { save, user } = editor();
  await user.click(screen.getByRole("button", { name: "hysteresis cpuState" }));
  const exit = screen.getByRole("textbox", { name: "Exit at or below" });
  const windows = screen.getByRole("textbox", { name: "Exit after windows" });
  await user.clear(exit);
  await user.type(exit, "5");
  await user.clear(windows);
  await user.type(windows, "3");
  await user.click(screen.getByRole("button", { name: "Apply thresholds" }));
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(save.mock.calls[0][0].verbs.at(-1).hysteresis).toMatchObject({ above: 8, enterAfter: 2, below: 5, exitAfter: 3 });
});
