// @vitest-environment jsdom
// The reference projections built in each notation from an empty `graph <twin> : <ontology>;` (note 11, stage d): every statement of the
// original goes in through the notation's own "Add a statement" (a form the engine completes at a statement's start, then the rest of the
// statement), each on the engine's reading of the text so far. The text is stored in a draft, its graph bound in the space as the original's
// is, and previewed beside the original's graph: the state digests are equal. The three notations give the same text; it is committed and a new
// draft reads it back. The engine keeps a committed twin until it restarts (an engine without --write-definitions writes no file).
// Runs only with an engine: ENGINE=http://127.0.0.1:5094 npx vitest run tests/editors/kql-references.test.tsx
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ComponentType } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Edit } from "../../src/core/model";
import type { Place } from "../../src/editor/kql";
import { apply, listsOf, type Change } from "../../src/editor/kql-edit";
import { text as textValue } from "../../src/editor/patches";
import { bindingEdits } from "../../src/editor/structures";
import { EditorService } from "../../src/editor/service";
import { EditorSession } from "../../src/editor/session";
import { KqlOutline } from "../../src/ui/editor/KqlOutline";
import { KqlSteps, type NotationProps } from "../../src/ui/editor/KqlSteps";
import { KqlWires } from "../../src/ui/editor/KqlWires";
import { KQL_REFERENCES, type KqlReference } from "./references";

const ENGINE = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ENGINE;
const NOTATIONS: [string, ComponentType<NotationProps>][] = [
  ["Steps", (p) => <KqlSteps {...p} rows={null} />], ["Outline", (p) => <KqlOutline {...p} window={2000} />], ["Wires", KqlWires],
];
const SPACE = "d:acme.space/space:operations";
const twinOf = (r: KqlReference) => `${r.projection}Twin`;

/** The graph of the space that a projection makes, bound as the reference binds its own (absent edits when the space has it already). */
const binding = (s: EditorSession, r: KqlReference, name: string): Edit[] =>
  bindingEdits(s.graph, r.module, name, name, Object.fromEntries(Object.entries(r.inputs).map(([port, from]) => [port, `${SPACE}/graph:${from}`])),
    Object.fromEntries(Object.entries(r.values).map(([port, v]) => [port, textValue(v)])));

/** A draft that holds the twin's text (and the original's, when it has no file), both graphs bound in the space. */
async function draftWith(svc: EditorService, r: KqlReference, text: string): Promise<EditorSession> {
  const s = await EditorSession.open(svc);
  const store = async (name: string, t: string) => {
    expect((await s.storeKql(`${r.module}.${name}.kql`, t)).ok).toBe(true);
    const b = binding(s, r, name);
    if (b.length) expect((await s.apply(b, `bind ${name}`)).ok).toBe(true);
  };
  if (r.text) await store(r.projection, r.text);
  await store(twinOf(r), text);
  expect(s.diagnostics.filter((d) => d.severity === "error").map((d) => `${d.code}: ${d.message}`)).toEqual([]);
  return s;
}

/** The previews of the original's graph and the twin's: their digests and entity counts. */
async function digests(svc: EditorService, s: EditorSession, r: KqlReference) {
  const pv = await svc.preview(s.draft.draft, { graphs: [r.graph, twinOf(r)], include: ["counts"], limit: "3000" });
  const of = (id: string) => pv.graphs.find((g) => g.id === id)!;
  return { original: of(r.graph).digest, twin: of(twinOf(r)).digest, entities: Number(of(r.graph).counts.entities) };
}

afterEach(cleanup);

describe.skipIf(!ENGINE)("the reference projections built in each notation from an empty graph line", () => {
  describe.each(KQL_REFERENCES.map((r) => [r.title, r] as const))("%s", (_title, r) => {
    const projection = `${r.module}.${twinOf(r)}`, built = new Map<string, string>();

    it.each(NOTATIONS)("through %s", async (notation, View) => {
      const svc = new EditorService(ENGINE!);
      const original = r.text ?? (await svc.kqlText(`${r.module}.${r.projection}`)).text;
      const a0 = await svc.kqlAnalyse(`${r.module}.${r.projection}`, original, "fast");
      // the statements of the original after its graph line, in text order: the graph line and the inputs own their `;`, the rest do not
      const wanted = listsOf(a0).find((l) => l.id === "statements")!.items.slice(1).map((p) => original.slice(p[0], p[1]).replace(/;\s*$/, ""));
      let text = `graph ${twinOf(r)} : ${r.module};\n`;
      const end = text.length - 1;
      const forms = (await svc.kqlComplete(projection, text, end + 1)).items.filter((i) => i.kind === "step").map((i) => i.insert ?? i.text);
      const formless: string[] = [];
      for (const statement of wanted) {
        const reading = { text, analysis: await svc.kqlAnalyse(projection, text, "fast") };
        const onChanges = vi.fn<(c: Change[], s: Place | null) => void>();
        render(<View reading={reading} forms={forms} selected={null} busy={false} onSelect={() => {}} onChanges={onChanges} />);
        // the form the statement starts with (the longest: `extend nodes` before `nodes`); a statement no form starts is written over the first
        const form = [...forms].sort((x, y) => y.length - x.length).find((f) => statement.startsWith(`${f} `)) ?? (formless.push(statement.split(/[\s(]/)[0]), forms[0]);
        fireEvent.click(screen.getByRole("button", { name: "Add a statement" }));
        fireEvent.click(screen.getByRole("menuitem", { name: form }));
        const box = screen.getByRole("textbox", { name: "Text of the new statement" });
        expect((box as HTMLInputElement).value).toBe(`${form} `);
        fireEvent.change(box, { target: { value: statement } });
        fireEvent.keyDown(box, { key: "Enter" });
        expect(onChanges, `${notation}: ${statement.slice(0, 60)}`).toHaveBeenCalledOnce();
        text = apply(text, onChanges.mock.calls[0][0]);
        cleanup();
      }
      // only a parameters declaration has no form of its own
      expect([...new Set(formless)]).toEqual(r.projection === "deploymentHealth" || r.projection === "workloadHealth" ? ["declare"] : []);
      const full = await svc.kqlAnalyse(projection, text, "full");
      expect(full.diagnostics.filter((d) => d.severity === "error").map((d) => d.message), notation).toEqual([]);
      built.set(notation, text);
      const s = await draftWith(svc, r, text);
      try {
        const d = await digests(svc, s, r);
        expect(d.entities).toBeGreaterThan(0);
        expect(d.twin, `${notation}: state digest`).toBe(d.original);
      } finally { await svc.discard(s.draft.draft).catch(() => {}); }
    }, 600_000);

    it("the three notations give one text; committed, a new draft reads it back and previews the original's digest", async () => {
      expect(built.size).toBe(NOTATIONS.length);
      const [text, ...rest] = [...built.values()];
      for (const t of rest) expect(t).toBe(text);
      const svc = new EditorService(ENGINE!);
      const s = await draftWith(svc, r, text);
      await svc.commit(s.draft.draft, s.draft.baseRevision);
      const fresh = await EditorSession.open(svc);
      try {
        expect((await svc.kqlText(projection, fresh.draft.draft)).text).toBe(text);
        // an original with no file is not committed: the new draft holds it again
        if (r.text) { expect((await fresh.storeKql(`${r.module}.${r.projection}.kql`, r.text)).ok).toBe(true); const b = binding(fresh, r, r.projection); if (b.length) expect((await fresh.apply(b, "bind")).ok).toBe(true); }
        const d = await digests(svc, fresh, r);
        expect(d.twin).toBe(d.original);
      } finally { await svc.discard(fresh.draft.draft).catch(() => {}); }
    }, 600_000);
  });
});
