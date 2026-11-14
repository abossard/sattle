// @vitest-environment jsdom
// Steps on the KQL outline (note 11, stage b), as a pure view of a recorded reading: it shows every step with its operators, a click selects the
// operator's place, and moving, removing and editing an operator hand back text changes that touch only that pipeline.
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Analysis, Place } from "../../src/editor/kql";
import { apply, type Change } from "../../src/editor/kql-edit";
import { KqlSteps } from "../../src/ui/editor/KqlSteps";

const rec = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as { files: { projection: string; text: string; analyse: Analysis }[] };
const f = rec.files.find((x) => x.projection === "acme.biz.capabilityView")!;
const reading = { text: f.text, analysis: f.analyse };
const images = "let images = realized | join kind=inner (realized | summarize n = count() by service | where n == 1) on service | project service, capability;";

const show = () => {
  const onChanges = vi.fn<(c: Change[], s: Place | null) => void>(), onSelect = vi.fn<(p: Place) => void>();
  render(<KqlSteps reading={reading} selected={null} busy={false} rows={null} onSelect={onSelect} onChanges={onChanges} />);
  const after = () => apply(f.text, onChanges.mock.calls.at(-1)![0]);
  return { onChanges, onSelect, after };
};

afterEach(cleanup);

describe("Steps on the outline", () => {
  it("shows every step with its operators", () => {
    show();
    const steps = (f.analyse.statements as { kind: string; name: string | null }[]).length;
    expect(screen.getAllByRole("list", { name: /^Operators of / })).toHaveLength(steps);
    expect(within(screen.getByRole("list", { name: "Operators of let images" })).getAllByRole("listitem")).toHaveLength(3);
  });

  it("a click on an operator selects its place in the text", () => {
    const { onSelect } = show();
    const ops = screen.getByRole("list", { name: "Operators of let images" });
    fireEvent.click(within(ops).getByRole("button", { name: /^join kind=inner/ }));
    const [from, to] = onSelect.mock.calls[0][0];
    expect(f.text.slice(from, to)).toBe("join kind=inner (realized | summarize n = count() by service | where n == 1) on service");
  });

  it.each([
    ["moving the join down", "Move join of let images down", "let images = realized | project service, capability | join kind=inner (realized | summarize n = count() by service | where n == 1) on service;"],
    ["removing the project", "Remove project of let images", "let images = realized | join kind=inner (realized | summarize n = count() by service | where n == 1) on service;"],
  ])("%s changes that pipeline only", (_what, button, want) => {
    const { after } = show();
    fireEvent.click(screen.getByRole("button", { name: button }));
    expect(after()).toBe(f.text.replace(images, want));
  });

  it("editing an operator in place sets its text", () => {
    const { after } = show();
    fireEvent.click(screen.getByRole("button", { name: "Edit project of let images" }));
    const box = screen.getByRole("textbox", { name: "Text of project of let images" });
    fireEvent.change(box, { target: { value: "project capability, service" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(after()).toBe(f.text.replace(images, images.replace("project service, capability", "project capability, service")));
  });

  it("adding an operator puts the starter at the end of a let, naming the columns before it", async () => {
    const { after } = show();
    fireEvent.click(screen.getByRole("button", { name: "Add an operator to let images" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "where isnotempty(service)" }));
    expect(after()).toBe(f.text.replace(images, images.replace("project service, capability;", "project service, capability | where isnotempty(service);")));
  });
});
