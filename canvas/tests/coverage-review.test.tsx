// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { CoverageReview } from "../src/ui/CoverageReview";
import type { Entity } from "../src/core/model";
import type { EditorService } from "../src/editor/service";

const rule = { ruleId: "ingress", revision: "1", source: "press", item: "sheet-42", role: "receiver",
  target: "sorter", action: "declare", many: false, owner: "operator", reason: "" };
const program = { graph: "coverage", ontology: "sattle.coverage", parameters: [], inputs: [],
  verbs: [{ id: "rules", verb: "values", values: { rows: [rule, { ...rule, ruleId: "other", item: "sheet-99" }] } }], wires: [] };
function finding(status: string, target: string): Entity {
  const fields = { ...rule, target, direction: "declaration", observedTarget: "press-2", sourceRef: "receipt/427", status };
  return { id: "finding-42", type: "Finding", props: Object.fromEntries(Object.entries(fields).map(([k, v]) =>
    [k, typeof v === "string" ? { kind: "text", value: v } : { kind: "boolean", value: v }])) };
}
function setup(complete = true, file = "project.bindings.gkql.json") {
  const before = finding("conflict", "sorter"), after = finding("mapped", "press-2");
  after.props.revision = { kind: "text", value: "2" };
  const svc = {
    createDraft: vi.fn(async () => ({ draft: "d1", revision: "5", baseRevision: "5" })),
    check: vi.fn(async () => ({ ok: true, modules: { [file]: { text: JSON.stringify(program) } } })),
    setDraftText: vi.fn(async () => ({ ok: true, revision: "6", diagnostics: [] })),
    preview: vi.fn(async () => ({ scope: { complete }, graphs: [{ id: "review", diff: complete ? { edits: [{ kind: "entity", id: before.id, before, after }] } : null }] })),
    commit: vi.fn(async () => ({ catalogRevision: "6", modules: ["sattle.coverage"] })),
    discard: vi.fn(async () => {}),
  };
  const service = svc as unknown as EditorService;
  const selected = { entity: before, graph: "review", rulesFile: file };
  const view = render(<CoverageReview selected={selected} service={service} />);
  return { ...svc, inspectReceipt: () => view.rerender(<CoverageReview service={service} />),
    inspectFinding: () => view.rerender(<CoverageReview selected={selected} service={service} />), unmount: view.unmount };
}
afterEach(cleanup);
it("previews a receiver correction against its original receipt before explicit publication", async () => {
  const svc = setup();
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.change(screen.getByLabelText("Target"), { target: { value: "press-2" } });
  fireEvent.change(screen.getByLabelText("Rule revision"), { target: { value: "2" } });
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  await screen.findByText("conflict → mapped");
  expect(screen.getAllByText("receipt/427").length).toBeGreaterThan(0);
  expect(svc.commit).not.toHaveBeenCalled();
  expect(svc.setDraftText).toHaveBeenCalledOnce();
  const [, path, text, revision] = svc.setDraftText.mock.calls[0] as unknown as [string, string, string, string];
  expect(JSON.parse(text).verbs[0].values.rows).toEqual([{ ...rule, revision: "2", target: "press-2" }, program.verbs[0].values.rows[1]]);
  expect(path).toBe("project.bindings.gkql.json");
  expect(revision).toBe("5");
  fireEvent.click(screen.getByRole("button", { name: "Publish rule" }));
  await screen.findByText("Published at catalog revision 6.");
  expect(svc.commit).toHaveBeenCalledWith("d1", "5");
});
it("leaves an unresolved review alone and never publishes an incomplete preview", async () => {
  const svc = setup(false);
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.click(screen.getByRole("button", { name: "Leave unresolved" }));
  expect(svc.createDraft).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.change(screen.getByLabelText("Rule revision"), { target: { value: "2" } });
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  await screen.findByRole("alert");
  expect(screen.queryByRole("button", { name: "Publish rule" })).toBeNull();
  expect(svc.discard).toHaveBeenCalledWith("d1");
});

it("keeps a preview open while inspecting its receipt and returning to the finding", async () => {
  const svc = setup();
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  await screen.findByText("conflict → mapped");
  svc.inspectReceipt();
  expect(screen.getByText("conflict → mapped")).toBeTruthy();
  svc.inspectFinding();
  expect(svc.discard).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Publish rule" }));
  await screen.findByText("Published at catalog revision 6.");
  expect(svc.commit).toHaveBeenCalledWith("d1", "5");
});

it.each([true, false])("waits for an in-flight publish before cleaning up on unmount (success %s)", async (success) => {
  const svc = setup();
  let finish!: () => void;
  svc.commit.mockImplementationOnce(() => new Promise((resolve, reject) => {
    finish = () => success ? resolve({ catalogRevision: "6", modules: [] }) : reject(new Error("Conflict"));
  }));
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  fireEvent.click(await screen.findByRole("button", { name: "Publish rule" }));
  svc.unmount();
  expect(svc.discard).not.toHaveBeenCalled();
  await act(async () => { finish(); });
  if (success) expect(svc.discard).not.toHaveBeenCalled();
  else expect(svc.discard).toHaveBeenCalledWith("d1");
});
it("requires an assistant's candidate to cite this observation and applies it only to the form", () => {
  const svc = setup();
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.click(screen.getByText("Use an assistant suggestion"));
  const input = screen.getByLabelText("Suggested rule (JSON)");
  fireEvent.change(input, { target: { value: JSON.stringify({ sourceRef: "another receipt", role: "receiver", target: "press-2" }) } });
  fireEvent.click(screen.getByRole("button", { name: "Use suggestion" }));
  expect(screen.getByRole("alert").textContent).toMatch(/receipt/);
  fireEvent.change(input, { target: { value: JSON.stringify({ sourceRef: "receipt/427", role: "participant", target: "press-2" }) } });
  fireEvent.click(screen.getByRole("button", { name: "Use suggestion" }));
  expect((screen.getByLabelText("Role") as HTMLInputElement).value).toBe("participant");
  expect(svc.createDraft).not.toHaveBeenCalled();
});

it("refuses to overwrite a changed declaration even when another author reused its revision", async () => {
  const svc = setup();
  const concurrent = structuredClone(program);
  concurrent.verbs[0].values.rows[0].target = "new-conveyor";
  svc.check.mockResolvedValueOnce({ ok: true, modules: { "project.bindings.gkql.json": { text: JSON.stringify(concurrent) } } });
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  expect((await screen.findByRole("alert")).textContent).toContain("published rule changed");
  expect(svc.setDraftText).not.toHaveBeenCalled();
  expect(svc.discard).toHaveBeenCalledWith("d1");
});

it("discards a change that produces no matching finding in the reviewed graph", async () => {
  const svc = setup();
  svc.preview.mockResolvedValueOnce({ scope: { complete: true }, graphs: [{ id: "review", diff: { edits: [] } }] });
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  expect((await screen.findByRole("alert")).textContent).toMatch(/finding/);
  expect(svc.discard).toHaveBeenCalledWith("d1");
  expect(screen.queryByRole("button", { name: "Publish rule" })).toBeNull();
});

it("previews an exclusion with its owner, reason and population breakdown", async () => {
  const svc = setup();
  const before = finding("conflict", "sorter"), after = finding("excluded", "");
  after.props.revision = { kind: "text", value: "2" };
  const scope = (excluded: boolean): Entity => ({ id: "scope", type: "Scope", props: {
    source: before.props.source, role: before.props.role, eligible: { kind: "integer", value: "1" },
    supportedBindings: { kind: "integer", value: "0" }, unmapped: { kind: "integer", value: excluded ? "0" : "1" },
    excluded: { kind: "integer", value: excluded ? "1" : "0" },
  } });
  svc.preview.mockResolvedValueOnce({ scope: { complete: true }, graphs: [{ id: "review", diff: { edits: [
    { kind: "entity", id: before.id, before, after }, { kind: "entity", id: "scope", before: scope(false), after: scope(true) },
  ] } }] });
  fireEvent.click(screen.getByRole("button", { name: "Review rule" }));
  fireEvent.change(screen.getByLabelText("Action"), { target: { value: "exclude" } });
  fireEvent.change(screen.getByLabelText("Exclusion reason"), { target: { value: "Calibration run" } });
  fireEvent.click(screen.getByRole("button", { name: "Preview change" }));
  await screen.findByText("excluded 0 → 1");
  expect(screen.getByText("unmapped 1 → 0")).toBeTruthy();
  expect(screen.getByText("eligible 1")).toBeTruthy();
  expect(screen.getByText("supported bindings 0")).toBeTruthy();
  expect(screen.getByText("operator · Calibration run")).toBeTruthy();
  expect(svc.commit).not.toHaveBeenCalled();
});
