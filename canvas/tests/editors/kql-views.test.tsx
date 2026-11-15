// @vitest-environment jsdom
// The notations beside the KQL text (note 11, stage c) as pure views of recorded readings: every shipped file opens in Steps, Outline and
// Wires; the same edit made in each notation gives the same text; no notation reaches the engine, the app's state or the text editor.
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import type { ComponentType } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Analysis, Place } from "../../src/editor/kql";
import { apply, type Change } from "../../src/editor/kql-edit";
import { targetsOf, wiresOf } from "../../src/editor/kql-wires";
import { KqlOutline } from "../../src/ui/editor/KqlOutline";
import { KqlSteps, type NotationProps } from "../../src/ui/editor/KqlSteps";
import { KqlWires } from "../../src/ui/editor/KqlWires";

const rec = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as { files: { projection: string; text: string; analyse: Analysis }[] };
const file = (p: string) => rec.files.find((x) => x.projection === p)!;
const Steps = (p: NotationProps) => <KqlSteps {...p} rows={null} />;
const Outline = (p: NotationProps) => <KqlOutline {...p} window={2000} />;
const NOTATIONS: [string, ComponentType<NotationProps>][] = [["Steps", Steps], ["Outline", Outline], ["Wires", KqlWires]];

/** Show a file in a notation; `after()` is the text after the last change it handed back. */
const show = (View: ComponentType<NotationProps>, f: { text: string; analyse: Analysis }, selected: number | null = null) => {
  const onChanges = vi.fn<(c: Change[], s: Place | null) => void>();
  render(<View reading={{ text: f.text, analysis: f.analyse }} selected={selected} busy={false} onSelect={() => {}} onChanges={onChanges} />);
  return { onChanges, after: () => apply(f.text, onChanges.mock.calls.at(-1)![0]) };
};
const row = (id: string) => document.querySelector<HTMLElement>(`[data-row-id="${id}"]`)!;
const outlineEdit = (id: string, value: string) => {
  fireEvent.keyDown(row(id), { key: "Enter" });
  const box = within(row(id)).getByRole("textbox");
  fireEvent.change(box, { target: { value } });
  fireEvent.keyDown(box, { key: "Enter" });
};

afterEach(cleanup);

describe.each(NOTATIONS)("%s", (_name, View) => {
  it.each(rec.files.map((f) => [f.projection, f] as const))("opens %s", (_p, f) => {
    show(View, f);
    expect(document.body.textContent).not.toContain("Waiting for the engine");
    expect(document.body.textContent!.length).toBeGreaterThan(100);
  });
});

describe("the same edit in each notation gives the same text", () => {
  const caps = file("acme.biz.capabilityView");
  const images = (caps.analyse.statements as { name?: string | null }[]).findIndex((s) => s.name === "images");
  const imagesOps = (caps.analyse.statements as { operators: { kind: string }[] }[])[images].operators;

  it("removing the project of a let: Steps and Outline", () => {
    const steps = show(Steps, caps);
    fireEvent.click(screen.getByRole("button", { name: "Remove project of let images" }));
    const want = steps.after();
    cleanup();
    const outline = show(Outline, caps);
    fireEvent.keyDown(row(`statements.${images}.operators.${imagesOps.findIndex((o) => o.kind === "project")}`), { key: "Delete" });
    expect(outline.after()).toBe(want);
  });

  it("adding a where to a let: Steps and Outline", () => {
    const steps = show(Steps, caps);
    fireEvent.click(screen.getByRole("button", { name: "Add an operator to let images" }));
    const pick = screen.getAllByRole("menuitem").find((m) => m.textContent!.startsWith("where"))!;
    const op = pick.textContent!;
    fireEvent.click(pick);
    const want = steps.after();
    cleanup();
    const outline = show(Outline, caps);
    fireEvent.click(within(row(`statements.${images}.add`)).getByRole("button"));
    fireEvent.click(screen.getAllByRole("menuitem").find((m) => m.textContent === op)!);
    expect(outline.after()).toBe(want);
  });

  // the first join whose right side is a bare `let` name in parentheses: Outline edits the right side, Wires points it at another let
  const found = rec.files.flatMap((f) => (f.analyse.statements as { place: Place; operators: { kind: string; parts?: { right?: Place } }[] }[]).flatMap((s, i) =>
    targetsOf(f.text, f.analyse, i).filter((t) => t.what.startsWith("join") && t.options.length > 1).map((t) => ({ f, i, s, t, j: Number(t.what.split(" ")[1]) - 1 }))))[0];
  it("pointing a join at another let: Outline and Wires", () => {
    const { f, i, s, t, j } = found;
    const other = t.options.find((o) => o !== t.name)!;
    const right = s.operators[j].parts!.right!;
    const outline = show(Outline, f);
    fireEvent.keyDown(row(`statements.${i}.operators.${j}`), { key: "ArrowRight" });
    outlineEdit(`statements.${i}.operators.${j}.right.0`, f.text.slice(right[0], right[1]).replace(t.name, other));
    const want = outline.after();
    cleanup();
    const wires = show(KqlWires, f, s.place[0]);
    fireEvent.change(screen.getByRole("combobox", { name: t.what }), { target: { value: other } });
    expect(wires.after()).toBe(want);
    expect(want).not.toBe(f.text);
  });

  it("the Wires card of a statement selects it in the text", () => {
    const onSelect = vi.fn<(p: Place) => void>();
    const f = file("acme.svc.serviceDeps");
    render(<KqlWires reading={{ text: f.text, analysis: f.analyse }} selected={null} busy={false} onSelect={onSelect} onChanges={() => {}} />);
    const cards = wiresOf(f.analyse).cards;
    expect(screen.getAllByRole("listitem")).toHaveLength(cards.length);
    fireEvent.click(screen.getByRole("button", { name: /^let calls/ }));
    expect(f.text.slice(...onSelect.mock.calls[0][0])).toMatch(/^let calls = /);
  });
});

describe("no notation reaches the engine, the app's state or the text editor", () => {
  it.each([["Steps", "KqlSteps"], ["Outline", "KqlOutline"], ["Wires", "KqlWires"]])("%s", (_n, src) => {
    const imports = [...String(readFileSync(`src/ui/editor/${src}.tsx`)).matchAll(/from "([^"]+)"/g)].map((m) => m[1]);
    expect(imports.filter((i) => /service|session|state\/|KqlText|@codemirror/.test(i))).toEqual([]);
  });
});
