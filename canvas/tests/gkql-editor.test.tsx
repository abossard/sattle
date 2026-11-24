// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { readGkql, spanOf, type GkqlProgram } from "../src/editor/gkql";
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
afterEach(() => { cleanup(); Reflect.deleteProperty(document, "elementFromPoint"); });

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

function editor(program: GkqlProgram = base) {
  const save = vi.fn();
  function Draft() {
    const [source, setSource] = useState(JSON.stringify(program));
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
  await user.click(await screen.findByRole("menuitem", { name: "graph-match" }));
  await user.click(screen.getByRole("button", { name: "Add node" }));
  await user.type(screen.getByRole("textbox", { name: "Node type" }), "Resource");
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(save.mock.calls[0][0].verbs.at(-1)).toEqual({
    id: "graph_match", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n1", label: "Resource" }] },
  });
});

it.each([["Node type", "Resource", "Res"], ["Node variable", "n", "node"]])("the keystrokes in %s are one undo step", async (label, before, typed) => {
  const { user } = editor();
  await user.click(screen.getByRole("button", { name: "graph-match all" }));
  const field = () => screen.getByRole("textbox", { name: label }) as HTMLInputElement;
  await user.clear(field());
  await user.type(field(), typed);
  expect(field().value).toBe(typed);
  await user.click(screen.getByRole("button", { name: "Undo edit" }));
  expect(field().value).toBe(before);
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

describe("every action works from the keyboard and every view follows", () => {
  const unwired = { ...base, wires: [{ from: "choose", to: "cpuState" }] };
  const card = (name: string) => screen.getByRole("button", { name });

  it("connects two operations with Enter on a handle and then on a card", async () => {
    const { save, user } = editor(unwired);
    card("Connect from live").focus();
    await user.keyboard("{Enter}");
    expect(screen.getByText(/Connecting from live/)).toBeTruthy();
    card("where choose").focus();
    await user.keyboard("{Enter}");
    await user.click(card("Check & store"));
    await waitFor(() => expect(save).toHaveBeenCalledOnce());
    expect(save.mock.calls[0][0].wires).toContainEqual({ from: "live", to: "choose" });
  });

  it.each([["onto a card", "where choose", true], ["onto the empty sheet", null, false]] as const)("dragging a handle %s connects or cancels", async (_, onto, wired) => {
    const { save, user } = editor(unwired);
    const handle = card("Connect from live");
    const sheet = screen.getByRole("region", { name: "GKQL operations and wires" });
    const at = vi.fn(() => (onto ? card(onto) : sheet));
    document.elementFromPoint = at;
    fireEvent.pointerDown(handle, { button: 0, pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(handle, { pointerId: 1, clientX: 60, clientY: 40 });
    expect(screen.getByText(/Connecting from live/)).toBeTruthy();
    fireEvent.pointerUp(handle, { pointerId: 1, clientX: 120, clientY: 80 });
    expect(at).toHaveBeenCalledWith(120, 80);
    expect(screen.queryByText(/Connecting from live/)).toBeNull();
    const store = card("Check & store") as HTMLButtonElement;
    expect(store.disabled).toBe(!wired);
    if (!wired) return;
    await user.click(store);
    await waitFor(() => expect(save).toHaveBeenCalledOnce());
    expect(save.mock.calls[0][0].wires).toContainEqual({ from: "live", to: "choose" });
  });

  it.each([["live", "graph-match live"], ["cpuState", "hysteresis cpuState"]])("the caret in the text and the selection of %s follow each other", async (id, name) => {
    const { user } = editor();
    await user.click(screen.getByRole("tab", { name: "GKQL" }));
    const area = screen.getByRole("textbox", { name: "GKQL source" }) as HTMLTextAreaElement;
    const [start] = spanOf(area.value, id)!;
    // the caret inside the operation's object selects its card and its step
    area.setSelectionRange(start + 2, start + 2);
    fireEvent.select(area);
    expect(card(name).getAttribute("aria-pressed")).toBe("true");
    expect(within(screen.getByRole("listbox", { name: "Steps" })).getByRole("option", { selected: true }).textContent).toContain(id);
    // a card selected on the graph puts the caret at the start of its object
    await user.click(card("where choose"));
    expect(area.selectionStart).toBe(spanOf(area.value, "choose")![0]);
    await user.click(card(name));
    expect(area.selectionStart).toBe(start);
  });

  it.each(["{Delete}", "{Backspace}"])("%s removes the selected card with its wires, and Ctrl+Z brings both back", async (press) => {
    const { user } = editor();
    await user.click(card("where choose"));
    await user.keyboard(press);
    expect(screen.queryByRole("button", { name: "where choose" })).toBeNull();
    // the step and the wires that read it are gone: cpuState now reads nothing
    expect(screen.getByRole("listbox", { name: "Steps" }).textContent).not.toContain("choose");
    expect(document.activeElement?.getAttribute("aria-label")).toBe("GKQL operations and wires");
    await user.keyboard("{Control>}z{/Control}");
    expect(card("where choose")).toBeTruthy();
    await user.keyboard("{Control>}{Shift>}z{/Shift}{/Control}");
    expect(screen.queryByRole("button", { name: "where choose" })).toBeNull();
  });

  it("Delete acts on the focused card, not on the one selected before", async () => {
    const { user } = editor();
    await user.click(card("graph-match all"));
    card("where choose").focus();
    await user.keyboard("{Delete}");
    expect(screen.queryByRole("button", { name: "where choose" })).toBeNull();
    expect(card("graph-match all")).toBeTruthy();
  });

  it("Delete on a toolbar button removes nothing", async () => {
    const { user } = editor();
    await user.click(card("where choose"));
    screen.getByRole("tab", { name: "Operation" }).focus();
    await user.keyboard("{Backspace}");
    expect(card("where choose")).toBeTruthy();
  });

  it.each([["{ArrowDown}", "live", "graph-match live"], ["{End}", "cpuState", "hysteresis cpuState"]])("%s in the steps selects %s on the graph too", async (press, id, name) => {
    const { user } = editor();
    const steps = screen.getByRole("listbox", { name: "Steps" });
    within(steps).getAllByRole("option")[0].focus();
    await user.keyboard(press);
    expect(within(steps).getByRole("option", { selected: true }).textContent).toContain(id);
    expect(card(name).getAttribute("aria-pressed")).toBe("true");
  });

  it("an invalid text says why and keeps the last good graph", async () => {
    const { user } = editor();
    await user.click(screen.getByRole("tab", { name: "GKQL" }));
    await user.type(screen.getByRole("textbox", { name: "GKQL source" }), "{{");
    expect(screen.getByText(/The text is not a GKQL program/)).toBeTruthy();
    expect(card("where choose")).toBeTruthy();
    expect((card("Check & store") as HTMLButtonElement).disabled).toBe(true);
  });

  it("a problem sits on its card and selecting it selects the card", async () => {
    const { user } = editor(unwired);
    const choose = card("where choose");
    expect(document.getElementById(choose.getAttribute("aria-describedby")!)?.textContent).toBe("the input of choose is not connected");
    await user.click(card("choose: the input of choose is not connected"));
    expect(choose.getAttribute("aria-pressed")).toBe("true");
  });
});
