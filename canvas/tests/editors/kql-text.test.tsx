// @vitest-environment jsdom
// The KQL text editor (note 11, stage a) through its port, on the engine's recorded answers (scripts/record-kql.mjs, tests/fixtures/kql.json.gz):
// the shipped files open without problems, a seeded dcount shows the profile's message at its place, completion after a pipe offers the profile's
// operators only and after `s.` the properties of the type, hover on a column gives its type, and an answer to another text is dropped.
import { CompletionContext } from "@codemirror/autocomplete";
import { EditorState } from "@codemirror/state";
import { render, screen } from "@testing-library/react";
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import type { Analysis, Completion, Depth, Hover } from "../../src/editor/kql";
import KqlText, { kqlSources, type KqlPort } from "../../src/ui/editor/KqlText";

interface Call<A> { request: { projection: string; text: string; offset?: number }; answer: A }
const rec = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as {
  files: { projection: string; digest: string; text: string; analyse: Analysis }[];
  cases: { dcount: Call<Analysis>; afterPipe: Call<Completion>; afterDot: Call<Completion>; hoverColumn: Call<Hover> };
};

/** The engine as recorded: an analysis per text, a completion and a hover per text and offset. */
const recorded = (): KqlPort & { kqlText(p: string): Promise<{ digest: string; text: string }> } => {
  const analyses = [...rec.files.map((f) => f.analyse), rec.cases.dcount.answer];
  const texts = [...rec.files.map((f) => f.text), rec.cases.dcount.request.text];
  const at = <A,>(calls: Call<A>[], text: string, offset: number) => { const c = calls.find((x) => x.request.text === text && x.request.offset === offset); if (!c) throw new Error(`not recorded at ${offset}`); return c.answer; };
  return {
    kqlText: async (p) => rec.files.find((f) => f.projection === p)!,
    kqlAnalyse: async (_p: string, text: string, _d: Depth) => analyses[texts.indexOf(text)] ?? Promise.reject(new Error("not recorded")),
    kqlComplete: async (_p, text, offset) => at([rec.cases.afterPipe, rec.cases.afterDot], text, offset),
    kqlHover: async (_p, text, offset) => at([rec.cases.hoverColumn], text, offset),
  };
};
const context = (c: Call<unknown>) => new CompletionContext(EditorState.create({ doc: c.request.text }), c.request.offset!, true);

describe("the KQL text editor on the engine's answers", () => {
  it.each(rec.files.map((f) => [f.projection, f.text] as const))("%s opens without problems", async (projection, text) => {
    expect(await kqlSources(recorded(), projection).lint(text)).toEqual([]);
  });

  it("a seeded dcount shows the profile's message at its place", async () => {
    const { request } = rec.cases.dcount;
    const got = await kqlSources(recorded(), request.projection).lint(request.text);
    const at = request.text.indexOf("dcount(service)");
    expect(got).toEqual([expect.objectContaining({ from: at, to: at + "dcount(service)".length, severity: "error", source: "profile Profile", message: expect.stringMatching(/^dcount is an estimate/) })]);
  });

  it.each([
    ["after a pipe, the profile's operators and none it refuses", rec.cases.afterPipe, ["where", "summarize", "join", "graph-match"], ["take", "sample", "sort", "top"]],
    ["after `s.` in a graph-match of Service, the properties of Service", rec.cases.afterDot, ["cluster", "namespace", "name"], ["where"]],
  ])("completion %s", async (_what, call, offered, refused) => {
    const got = (await kqlSources(recorded(), call.request.projection).complete(context(call)))!;
    const labels = got.options.map((o) => o.label);
    expect(labels).toEqual(expect.arrayContaining(offered));
    for (const r of refused) expect(labels).not.toContain(r);
    expect([got.from, got.to]).toEqual(call.answer.replace);
  });

  it("hover on a column gives its type, over the name hovered", async () => {
    const { request, answer } = rec.cases.hoverColumn;
    expect(await kqlSources(recorded(), request.projection).hover(request.text, request.offset!)).toEqual({ from: answer.place![0], to: answer.place![1], lines: ["service: string"] });
  });

  it.each([
    ["an analysis", (p: KqlPort, t: string) => kqlSources(p, "x").lint(t), []],
    ["a completion", (p: KqlPort, t: string) => kqlSources(p, "x").complete(new CompletionContext(EditorState.create({ doc: t }), 0, true)), null],
    ["a hover", (p: KqlPort, t: string) => kqlSources(p, "x").hover(t, 0), null],
  ])("%s of another text is dropped", async (_what, ask, none) => {
    const other = { ...rec.files[0], digest: "sha256:0" };
    const stale: KqlPort = {
      kqlAnalyse: async () => ({ ...other.analyse, digest: "sha256:0", diagnostics: rec.cases.dcount.answer.diagnostics }),
      kqlComplete: async () => ({ ...rec.cases.afterPipe.answer, digest: "sha256:0" }),
      kqlHover: async () => ({ ...rec.cases.hoverColumn.answer, digest: "sha256:0" }),
    };
    expect(await ask(stale, other.text)).toEqual(none);
  });

  it("the editor shows the file as the space holds it, and says when the engine finds no problem", async () => {
    const f = rec.files.find((x) => x.projection === "acme.svc.serviceDeps")!;
    render(<KqlText port={recorded()} projection={f.projection} />);
    expect(await screen.findByText("acme.svc.serviceDeps.kql")).toBeTruthy();
    const box = await screen.findByRole("textbox", { name: "KQL text of acme.svc.serviceDeps" });
    expect(box.textContent).toContain("graph serviceDeps : acme.svc with (");
    expect(await screen.findByText("the engine reads the text without problems", {}, { timeout: 5000 })).toBeTruthy();
  });
});
