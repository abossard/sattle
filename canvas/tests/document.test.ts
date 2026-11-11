import { describe, expect, it } from "vitest";
import { parseDocument } from "../src/core/markdown";
import { EngineSource } from "../src/source/engine";
import { parseSpace } from "../src/source/engine-protocol";

describe("the Markdown of a document graph as blocks", () => {
  it.each([
    ["a heading", "# Title\n", [{ kind: "heading", level: 1, text: "Title" }]],
    ["the sixth level", "###### Deep\n", [{ kind: "heading", level: 6, text: "Deep" }]],
    ["a paragraph over two lines", "one\ntwo\n", [{ kind: "paragraph", text: "one two" }]],
    ["a list, items and stats", "- Deployments: 2\n- Down: 0\n", [{ kind: "list", items: ["Deployments: 2", "Down: 0"] }]],
    ["two lists apart", "- a\n\n# H\n\n- b\n", [{ kind: "list", items: ["a"] }, { kind: "heading", level: 1, text: "H" }, { kind: "list", items: ["b"] }]],
    ["a table", "| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n", [{ kind: "table", header: ["A", "B"], rows: [["1", "2"], ["3", "4"]] }]],
    ["escaped punctuation in text", "a \\*b\\* \\# c\\.\n", [{ kind: "paragraph", text: "a *b* # c." }]],
    ["an escaped pipe in a cell is not a column", "| A | B |\n| --- | --- |\n| x \\| y | z |\n", [{ kind: "table", header: ["A", "B"], rows: [["x | y", "z"]] }]],
    ["an escaped backslash before a pipe ends the cell", "| A | B |\n| --- | --- |\n| x \\\\| z |\n", [{ kind: "table", header: ["A", "B"], rows: [["x \\", "z"]] }]],
    ["a data line that looks like a heading stays text", "\\# not a heading\n", [{ kind: "paragraph", text: "# not a heading" }]],
    ["an empty document", "\n", []],
  ] as [string, string, unknown[]][])("%s", (_, md, want) => expect(parseDocument(md)).toEqual(want));

  it("reads what the engine writes for the example report", () => {
    const md = "# Deployment health\n\n- Deployments: 2\n- Unhealthy deployments: 0\n\n| Namespace | Deployment | State | Ready pods |\n| --- | --- | --- | --- |\n| shop | db | Healthy | 1 |\n| shop | web | Healthy | 1 |\n";
    expect(parseDocument(md).map((b) => b.kind)).toEqual(["heading", "list", "table"]);
    expect(parseDocument(md)[2]).toMatchObject({ header: ["Namespace", "Deployment", "State", "Ready pods"], rows: [["shop", "db", "Healthy", "1"], ["shop", "web", "Healthy", "1"]] });
  });
});

describe("documents from the engine", () => {
  const BASE = "http://engine.test";
  const respond = (log: { url: string; inm?: string }[], routes: Record<string, { status?: number; etag?: string; rev?: string; body?: string }>): typeof fetch => (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const key = url.pathname + url.search;
    log.push({ url: key, inm: (init?.headers as Record<string, string> | undefined)?.["If-None-Match"] });
    const r = routes[key];
    if (!r) return new Response("", { status: 404 });
    const headers: Record<string, string> = { "X-Revision": r.rev ?? "1", "X-Document-Digest": "sha256:abc" };
    if (r.etag) headers.ETag = r.etag;
    return new Response(r.status === 304 ? null : r.body ?? "# T\n", { status: r.status ?? 200, headers });
  }) as typeof fetch;

  it("a graph entry of the space says whether it is a document", () => {
    const base = { space: "s", protocol: "3", kernelDigest: "k", catalogRevision: "1" };
    const g = (document: unknown) => ({ id: "r", role: "projection", head: "8", ontology: "sattle.doc", inputs: [], direction: "forward", editable: false, projection: "r", document });
    expect(parseSpace({ ...base, graphs: [g(true), g(false), g(undefined)] }).graphs.map((x) => x.document)).toEqual([true, false, false]);
  });
  it("a revision is revalidated with its content tag, because a revision number is unique only within one run of the engine", async () => {
    const log: { url: string; inm?: string }[] = [];
    const routes: Record<string, { status?: number; etag?: string; rev?: string; body?: string }> = { "/graphs/r/document?revision=3": { etag: '"e3-markdown"', rev: "3", body: "# Three\n" } };
    const src = new EngineSource(BASE, { fetch: respond(log, routes) });
    const a = await src.document("r", "3");
    routes["/graphs/r/document?revision=3"] = { status: 304, etag: '"e3-markdown"' };
    const b = await src.document("r", "3");
    routes["/graphs/r/document?revision=3"] = { etag: '"other-markdown"', rev: "3", body: "# Restarted\n" };
    const c = await src.document("r", "3");
    expect([a?.markdown, a?.revision, a?.digest, b === a, c?.markdown, log.map((l) => l.inm)]).toEqual(["# Three\n", "3", "sha256:abc", true, "# Restarted\n", [undefined, '"e3-markdown"', '"e3-markdown"']]);
  });
  it("the head asks again with the ETag it holds and keeps its copy on 304", async () => {
    const log: { url: string; inm?: string }[] = [];
    const routes: Record<string, { status?: number; etag?: string; rev?: string; body?: string }> = { "/graphs/r/document": { etag: '"e8-markdown"', rev: "8", body: "# Eight\n" } };
    const src = new EngineSource(BASE, { fetch: respond(log, routes) });
    const first = await src.document("r");
    routes["/graphs/r/document"] = { status: 304, etag: '"e8-markdown"' };
    const second = await src.document("r");
    expect([second === first, log.map((l) => l.inm)]).toEqual([true, [undefined, '"e8-markdown"']]);
    routes["/graphs/r/document"] = { etag: '"e9-markdown"', rev: "9", body: "# Nine\n" };
    expect((await src.document("r"))?.markdown).toBe("# Nine\n");
  });
  it.each([[404, "is not a document: null"], [500, "fails"]])("a %i answer %s", async (status, _) => {
    const src = new EngineSource(BASE, { fetch: respond([], { "/graphs/r/document": { status, body: "x" } }) });
    if (status === 404) expect(await src.document("r")).toBeNull(); else await expect(src.document("r")).rejects.toThrow(/HTTP 500/);
  });
});
