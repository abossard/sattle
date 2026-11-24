// The GKQL editor's calculations and its state machine: every action as a pure step, checked on one small program.
import { describe, expect, it } from "vitest";
import { checkGkql, connect, focusOf, printGkql, spanOf, verbAt, type GkqlProgram } from "../src/editor/gkql";
import { openModel, step, type GkqlEvent, type GkqlModel } from "../src/editor/gkql-editing";

const program: GkqlProgram = {
  graph: "p", ontology: "m", inputs: [{ name: "master", ontology: "m" }], parameters: [],
  verbs: [
    { id: "match", verb: "graph-match", graph: "master", pattern: { nodes: [{ var: "n", label: "Node" }] } },
    { id: "keep", verb: "where", condition: { lit: true } },
    { id: "out", verb: "nodes", target: { nodes: [] } },
  ],
  wires: [{ from: "match", to: "keep" }, { from: "keep", to: "out" }],
};
const text = printGkql(program);
const P = "d:m/projection:p";
const run = (events: GkqlEvent[], m: GkqlModel = openModel(text)) => events.reduce(step, m);
const verbs = (m: GkqlModel) => m.program?.verbs.map((v) => v.id);

describe("the editor's own check", () => {
  it.each<[string, Partial<GkqlProgram>, { focus: string; message: string }[]]>([
    ["a whole program", {}, []],
    ["an unconnected input", { wires: [{ from: "match", to: "keep" }] }, [{ focus: "verb:out", message: "the input of out is not connected" }]],
    ["a wire to an operation the program lacks", { wires: [...program.wires, { from: "gone", to: "out" }] }, [{ focus: "wire:gone>out.in", message: "the wire gone → out joins an operation the program does not have" }]],
    ["a port the verb does not have", { wires: [...program.wires, { from: "match", to: "out", port: "left" }] }, [{ focus: "wire:match>out.left", message: "out has no input left" }]],
    ["a missing source graph", { inputs: [] }, [{ focus: "verb:match", message: "match needs a source graph" }]],
    ["two operations with one name", { verbs: [...program.verbs, { id: "keep", verb: "where", condition: { lit: true } }], wires: [...program.wires, { from: "match", to: "keep" }] }, [{ focus: "verb:keep", message: "two operations are named keep" }]],
    ["a cycle", { verbs: [...program.verbs, { id: "again", verb: "where" }], wires: [{ from: "match", to: "keep" }, { from: "keep", to: "again" }, { from: "again", to: "keep" }, { from: "keep", to: "out" }] }, [{ focus: "verb:keep", message: "keep reads its own output" }, { focus: "verb:again", message: "again reads its own output" }]],
  ])("%s", (_, patch, problems) => expect(checkGkql({ ...program, ...patch })).toEqual(problems));
});

describe("an engine diagnostic names an element of the program", () => {
  it.each([
    ["a verb", `${P}/verb:keep`, "verb:keep"],
    ["an operator lowered from a verb", `${P}/op:keep_2`, "verb:keep"],
    ["a wire", `${P}/wire:match>keep.in`, "wire:match>keep.in"],
    ["an input port", `${P}/port:master`, "port:master"],
    ["a port of a lowered operator", `${P}/op:keep_1/port:in`, "verb:keep"],
    ["a verb the program does not have", `${P}/verb:gone`, null],
    ["the whole projection", P, null],
    ["another projection", "d:m/projection:q/verb:keep", null],
  ])("%s", (_, focus, element) => expect(focusOf(program, P, focus)).toBe(element));
});

describe("connecting two operations", () => {
  const join: GkqlProgram = { ...program, verbs: [...program.verbs, { id: "both", verb: "join", kind: "inner", on: [] }], wires: [...program.wires, { from: "match", to: "both", port: "left" }] };
  it.each<[string, GkqlProgram, string, string, string]>([
    ["takes the free port", { ...program, wires: [{ from: "match", to: "keep" }] }, "keep", "out", "keep>out.in"],
    ["takes the right port of a join when left is used", join, "keep", "both", "keep>both.right"],
  ])("%s", (_, p, from, to, wire) => {
    const r = connect(p, from, to);
    if (typeof r === "string") throw new Error(r);
    expect(r.wires.map((w) => `${w.from}>${w.to}.${w.port ?? "in"}`)).toContain(wire);
  });
  it.each([
    ["into a source", "keep", "match", "match reads no other operation"],
    ["from downstream", "out", "keep", "out cannot feed keep: it is an output or reads from keep"],
    ["into a full port", "match", "out", "every input of out is connected; change one in the Operation panel"],
  ])("refuses %s", (_, from, to, reason) => expect(connect(program, from, to)).toBe(reason));
});

describe("a verb's place in the text", () => {
  it.each(["match", "keep", "out"])("%s spans its own object and the caret inside it selects it", (id) => {
    const span = spanOf(text, id)!;
    expect(JSON.parse(text.slice(...span)).id).toBe(id);
    expect(verbAt(text, program, span[0] + 2)).toBe(id);
  });
  it("outside every verb nothing is selected", () => expect(verbAt(text, program, 1)).toBeNull());
});

describe("each action of the editor", () => {
  it.each<[string, GkqlEvent[], (m: GkqlModel) => unknown, unknown]>([
    ["remove a verb takes its wires along", [{ t: "remove", focus: "verb:keep" }], (m) => [verbs(m), m.program?.wires], [["match", "out"], []]],
    ["remove a wire", [{ t: "remove", focus: "wire:keep>out.in" }], (m) => m.program?.wires, [{ from: "match", to: "keep" }]],
    ["undo restores the text", [{ t: "remove", focus: "verb:keep" }, { t: "undo" }], (m) => m.text, text],
    ["redo applies it again", [{ t: "remove", focus: "verb:keep" }, { t: "undo" }, { t: "redo" }], (m) => verbs(m), ["match", "out"]],
    ["a new edit clears redo", [{ t: "remove", focus: "verb:keep" }, { t: "undo" }, { t: "remove", focus: "verb:out" }], (m) => m.future, []],
    ["a typing burst is one undo step", [{ t: "type", text: text + " " }, { t: "type", text: text + "  " }, { t: "undo" }], (m) => m.text, text],
    ["selecting ends a typing burst", [{ t: "type", text: text + " " }, { t: "select", focus: null }, { t: "type", text: text + "  " }, { t: "undo" }], (m) => m.text, text + " "],
    ["an invalid text keeps the last good program", [{ t: "type", text: "{" }], (m) => [m.error !== null, verbs(m)], [true, ["match", "keep", "out"]]],
    ["a valid text replaces the program", [{ t: "type", text: printGkql({ ...program, verbs: program.verbs.slice(0, 1), wires: [] }) }], (m) => [m.error, verbs(m)], [null, ["match"]]],
    ["moving a card changes layout only", [{ t: "move", id: "keep", to: { x: 40.4, y: 7 } }], (m) => [m.text === text, m.past.length, m.moved], [true, 0, { keep: { x: 40, y: 7 } }]],
    ["select names an element", [{ t: "select", focus: "wire:match>keep.in" }], (m) => m.selected, "wire:match>keep.in"],
    ["select of an unknown element selects nothing", [{ t: "select", focus: "verb:gone" }], (m) => m.selected, null],
    ["removing the selected verb clears the selection", [{ t: "select", focus: "verb:keep" }, { t: "remove", focus: "verb:keep" }], (m) => m.selected, null],
    ["connect from a card to another", [{ t: "remove", focus: "wire:keep>out.in" }, { t: "connect-start", from: "keep" }, { t: "connect-end", to: "out" }], (m) => [m.gesture.t, m.text, m.selected], ["idle", text, "verb:out"]],
    ["a refused connect says why and changes nothing", [{ t: "connect-start", from: "out" }, { t: "connect-end", to: "keep" }], (m) => [m.gesture.t, m.text, m.notice], ["idle", text, "out cannot feed keep: it is an output or reads from keep"]],
    ["cancel ends the gesture", [{ t: "connect-start", from: "keep" }, { t: "connect-cancel" }], (m) => [m.gesture.t, m.past.length], ["idle", 0]],
    ["a change can select what it made", [{ t: "change", program: { ...program, verbs: [...program.verbs, { id: "x", verb: "where" }] }, select: "verb:x" }], (m) => m.selected, "verb:x"],
    ["replace is one undo step, not a typing burst", [{ t: "replace", text: printGkql({ ...program, verbs: [], wires: [] }) }, { t: "type", text: text + " " }, { t: "undo" }], (m) => verbs(m), []],
    ["an edit of the graph waits for a valid text", [{ t: "type", text: "{" }, { t: "remove", focus: "verb:keep" }], (m) => [m.text, verbs(m), m.notice !== null], ["{", ["match", "keep", "out"], true]],
    ["load replaces the text without history", [{ t: "load", text: printGkql({ ...program, verbs: [] , wires: [] }) }], (m) => [verbs(m), m.past.length], [[], 0]],
  ])("%s", (_, events, read, expected) => expect(read(run(events))).toEqual(expected));
});
