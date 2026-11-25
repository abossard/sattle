// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { readGkql, spanOf, type GkqlProgram } from "../src/editor/gkql";
import { EditorService } from "../src/editor/service";
import GkqlEditor from "../src/ui/editor/GkqlEditor";
import type { EngineDiagnostic } from "../src/source/types";

const base: GkqlProgram = {
  graph: "health", ontology: "tutorial",
  inputs: [{ name: "master", ontology: "tutorial" }], parameters: [],
  verbs: [
    { id: "all", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n", label: "Resource" }] } },
    { id: "live", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n", label: "Resource", props: { ready: { lit: true } } }] } },
    { id: "choose", verb: "where", condition: { lit: true } },
    { id: "cpuPolicy", verb: "values", values: { columns: { above: "Decimal", below: "Decimal" }, rows: [[8, 7]] } },
    { id: "cpuState", verb: "fold", fold: { by: ["n"], observe: { v: { fn: "max", args: [{ ref: "cpu" }] } }, policy: ["above", "below"], state: { state: { start: { lit: "Healthy" }, next: { ref: "state" } } }, show: "state" } },
  ],
  wires: [{ from: "all", to: "choose" }, { from: "choose", to: "cpuState" }, { from: "cpuPolicy", to: "cpuState", port: "policy" }],
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

describe("a stored draft opens again on the engine's check of it", () => {
  const stored = JSON.stringify({ ...base, graph: "storedHealth" });
  const problem = (focus: string): EngineDiagnostic => ({ code: "Problem", severity: "error", focus, message: "a problem", line: 0, col: 0 });
  it.each<[string, EngineDiagnostic[] | undefined, string | undefined, boolean, boolean]>([
    ["passed", [], undefined, true, false],
    ["passed, with an error in another projection", [problem("d:tutorial/projection:other")], undefined, true, false],
    ["refused", [problem("d:tutorial/projection:health/verb:cpuState")], undefined, false, false],
    ["with no check known", undefined, undefined, false, false],
    ["with a local text", [], JSON.stringify({ ...base, graph: "localHealth" }), false, true],
  ])("%s", async (_, diagnostics, recoveredText, checked, storable) => {
    const user = userEvent.setup();
    const port = new EditorService("http://localhost/engine");
    const store = vi.fn().mockResolvedValue(null);
    const first = render(<GkqlEditor file={metadata} source={JSON.stringify(base)} initialTab="source" port={port} store={store} />);
    fireEvent.change(screen.getByRole("textbox", { name: "GKQL source" }), { target: { value: stored } });
    await user.click(screen.getByRole("button", { name: "Check & store" }));
    expect(await screen.findByText("Projection checked")).toBeTruthy();
    first.unmount();
    // the reload: a new editor on the text the draft holds and the engine's check of the draft
    render(<GkqlEditor file={metadata} source={stored} diagnostics={diagnostics} recoveredText={recoveredText} initialTab="source" port={port} store={store} />);
    expect(screen.queryByText("Projection checked") !== null).toBe(checked);
    expect((screen.getByRole("button", { name: "Check & store" }) as HTMLButtonElement).disabled).toBe(!storable);
  });
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
  expect(save.mock.calls[0][0].wires).toEqual([{ from: "live", to: "choose" }, { from: "choose", to: "cpuState" }, { from: "cpuPolicy", to: "cpuState", port: "policy" }]);
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

it("builds a projection from an empty program: palette, connect, fields, Check & store", async () => {
  const { save, user } = editor({ ...base, verbs: [], wires: [] });
  const add = async (verb: string) => {
    await user.click(screen.getByRole("button", { name: "Add operation" }));
    await user.click(await screen.findByRole("menuitem", { name: verb }));
  };
  const node = async (label: string) => {
    await user.click(screen.getByRole("button", { name: "Add node" }));
    await user.type(screen.getByRole("textbox", { name: "Node type" }), label);
  };
  await add("graph-match");
  await node("Pod");
  // a match reads no operation: the second one starts unwired, the join after it reads it on its left
  await add("graph-match");
  await node("Resource");
  await add("join");
  screen.getByRole("button", { name: "Connect from graph_match" }).focus();
  await user.keyboard("{Enter}");
  screen.getByRole("button", { name: "join join" }).focus();
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("button", { name: "join join" }));
  await add("nodes");
  await node("Workload");
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  const stored = save.mock.calls[0][0];
  expect(stored.verbs.map((v: { id: string; verb: string }) => `${v.verb} ${v.id}`)).toEqual(["graph-match graph_match", "graph-match graph_match2", "join join", "nodes nodes"]);
  expect(stored.verbs[0].pattern.nodes).toEqual([{ var: "n1", label: "Pod" }]);
  expect(stored.verbs[3].target.nodes).toEqual([{ var: "n1", label: "Workload" }]);
  expect(stored.wires).toEqual([
    { from: "graph_match2", to: "join", port: "left" },
    { from: "graph_match", to: "join", port: "right" },
    { from: "join", to: "nodes" },
  ]);
});

it("shows the rows after each operation of the previewed draft until the text changes", async () => {
  const user = userEvent.setup();
  const op = (id: string) => `d:tutorial/projection:health/op:${id}`;
  const rows = vi.fn().mockResolvedValue([{ op: op("all_1"), rows: "40" }, { op: op("all_2"), rows: "30" }, { op: op("choose_1"), rows: "12" }]);
  render(<GkqlEditor file={metadata} source={JSON.stringify(base)} port={new EditorService("http://localhost/engine")} store={async () => null} rows={rows} />);
  await user.click(screen.getByRole("button", { name: /^Show rows/ }));
  expect((await screen.findByRole("button", { name: "where 12 rows choose" })).getAttribute("data-verb")).toBe("choose");
  expect(screen.getByRole("button", { name: "graph-match 30 rows all" })).toBeTruthy();
  expect(screen.getByRole("button", { name: "graph-match live" })).toBeTruthy();
  await user.click(screen.getByRole("tab", { name: "GKQL" }));
  await user.type(screen.getByRole("textbox", { name: "GKQL source" }), " ");
  expect(screen.getByRole("button", { name: "where choose" })).toBeTruthy();
  expect((screen.getByRole("button", { name: /^Show rows/ }) as HTMLButtonElement).disabled).toBe(true);
});

it.each<[string, Record<string, unknown>]>([
  ["values", { values: { columns: { name: "Text" }, rows: [{ name: "web" }], key: "name" } }],
  ["distinct", {}],
  ["retain", { retain: { time: "time", age: "1h" } }],
  ["traverse", { graph: "master", traverse: { edge: "Reaches", from: "n", to: "d", direction: "out", min: 1, max: 3 } }],
  ["fixpoint", { fixpoint: { key: "n", value: "state", order: ["ok", "bad"], edges: { from: "a", to: "b" } } }],
  ["link", { link: { policy: "sameName", left: { identity: "byName", args: [{ ref: "n" }] }, right: { identity: "byUid", args: [{ ref: "u" }] }, confidence: 0.5 } }],
  ["summarize", { by: ["n"], aggregates: { p95: { fn: "percentile", args: [{ ref: "cpu" }, { lit: 95 }] } } }],
])("sets the fields of %s through Apply operation", async (verb, fields) => {
  const { save, user } = editor();
  await user.click(screen.getByRole("button", { name: "Add operation" }));
  await user.click(await screen.findByRole("menuitem", { name: verb }));
  const json = screen.getByRole("textbox", { name: "Operation JSON" });
  await user.clear(json);
  await user.click(json);
  await user.paste(JSON.stringify(fields));
  const apply = screen.getByRole("button", { name: "Apply operation" }) as HTMLButtonElement;
  // distinct has no fields: its JSON is already {}, so there is nothing to apply
  expect(apply.disabled).toBe(Object.keys(fields).length === 0);
  if (!apply.disabled) await user.click(apply);
  await user.click(screen.getByRole("button", { name: "Check & store" }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(save.mock.calls[0][0].verbs.at(-1)).toEqual({ id: verb, verb, ...fields });
});

describe("a fold shows its policy where the step on its policy input gives it", () => {
  const policy = { id: "cpuPolicy", verb: "extend", columns: { above: { lit: 8 }, below: { lit: 7 }, mode: { lit: "strict" }, scale: { ref: "n.scale" } } };
  const folded: GkqlProgram = {
    ...base, verbs: [...base.verbs.filter((verb) => verb.id !== "cpuPolicy").slice(0, -1), policy, { ...base.verbs.at(-1)!, fold: { ...base.verbs.at(-1)!.fold as object, policy: ["above", "below", "mode", "scale"] } }],
    wires: [...base.wires, { from: "choose", to: "cpuPolicy" }],
  };
  it("edits the literal columns of the policy step and leaves the computed one", async () => {
    const { save, user } = editor(folded);
    await user.click(screen.getByRole("button", { name: "fold cpuState" }));
    expect(screen.getByText("scale: computed by cpuPolicy")).toBeTruthy();
    const below = screen.getByRole("textbox", { name: "below" });
    const mode = screen.getByRole("textbox", { name: "mode" });
    await user.clear(below);
    await user.type(below, "5.5");
    await user.clear(mode);
    await user.type(mode, "lenient");
    await user.click(screen.getByRole("button", { name: "Apply policy" }));
    await user.click(screen.getByRole("button", { name: "Check & store" }));
    await waitFor(() => expect(save).toHaveBeenCalledOnce());
    const stored = save.mock.calls[0][0];
    expect(stored.verbs.find((verb: { id: string }) => verb.id === "cpuPolicy").columns).toEqual({ above: { lit: 8 }, below: { lit: 5.5 }, mode: { lit: "lenient" }, scale: { ref: "n.scale" } });
    expect(stored.verbs.find((verb: { id: string }) => verb.id === "cpuState")).toEqual(folded.verbs.at(-1));
  });
  it.each([["below", "x", "Expected a number."], ["below", "", "Expected a number."]])("%s typed as %j is refused with %s", async (name, typed, message) => {
    const { user } = editor(folded);
    await user.click(screen.getByRole("button", { name: "fold cpuState" }));
    await user.clear(screen.getByRole("textbox", { name }));
    if (typed) await user.type(screen.getByRole("textbox", { name }), typed);
    expect(screen.getByText(message)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Apply policy" }) as HTMLButtonElement).disabled).toBe(true);
  });
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

  it.each([["live", "graph-match live"], ["cpuState", "fold cpuState"]])("the caret in the text and the selection of %s follow each other", async (id, name) => {
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

  it.each(["{Delete}", "{Backspace}"])("Enter selects a wire on the graph and %s removes it", async (press) => {
    const { save, user } = editor();
    const wire = card("Wire: choose feeds cpuState");
    wire.focus();
    await user.keyboard("{Enter}");
    expect(wire.getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("heading", { name: "Wire" })).toBeTruthy();
    await user.keyboard(press);
    expect(screen.queryByRole("button", { name: "Wire: choose feeds cpuState" })).toBeNull();
    expect(card("where choose")).toBeTruthy();
    expect(screen.getByRole("button", { name: "cpuState: the input of cpuState is not connected" })).toBeTruthy();
    await user.click(card("Check & store"));
    await waitFor(() => expect(save).toHaveBeenCalledOnce());
    expect(save.mock.calls[0][0].wires).toEqual([{ from: "all", to: "choose" }, { from: "cpuPolicy", to: "cpuState", port: "policy" }]);
  });

  it.each([["{ArrowRight}", "left", 16], ["{ArrowDown}", "top", 16], ["{ArrowLeft}", "left", -16]] as const)("Alt+%s moves the focused card, layout only", async (arrow, side, by) => {
    const { user } = editor();
    const choose = card("where choose");
    choose.focus();
    const before = parseFloat(choose.style[side]);
    await user.keyboard(`{Alt>}${arrow}{/Alt}`);
    expect(parseFloat(card("where choose").style[side])).toBe(before + by);
    expect((card("Undo edit") as HTMLButtonElement).disabled).toBe(true);
    expect((card("Check & store") as HTMLButtonElement).disabled).toBe(true);
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

  it.each([["{ArrowDown}", "live", "graph-match live"], ["{End}", "cpuState", "fold cpuState"]])("%s in the steps selects %s on the graph too", async (press, id, name) => {
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
