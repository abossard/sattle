import { describe, expect, it } from "vitest";
import { EngineSource } from "../src/source/engine";
import { parseDiagnostics, parseSpace, parseWsMessage, batchFromWire, parseMeters } from "../src/source/engine-protocol";
import type { StreamEvent } from "../src/source/types";
import type { Rejection } from "../src/core/validate";

import { BASE, FakeSocket, baseRoutes, collect, descriptor, makeFetch, ndjson, ont, settle, space, tel, wirePatch } from "./fixtures/engine-fake";

describe("protocol parsing", () => {
  it("rejects other protocol versions", () => expect(() => parseSpace({ ...space(), protocol: "2" })).toThrow(/protocol 2/));
  it("space graphs carry role, direction and editability", () => {
    const d = parseSpace(space());
    expect(d.graphs.map((g) => [g.id, g.role, g.direction, g.editable])).toEqual([["catalog", "catalog", "readonly", false], ["master", "master", "mixed", true]]);
  });
  it.each([
    [{ type: "patch", graph: "g", patch: {} }, "patch"], [{ type: "gap", firstSeq: "5" }, "gap"], [{ type: "draft", draft: "d1", revision: "2", ok: true, diagnostics: [] }, "draft"], [{ type: "time", logicalTime: "x", rows: "1" }, "time"],
    [{ type: "activation", catalogRevision: "11", graphs: [] }, "activation"], [{ type: "end" }, "end"], [{ type: "future" }, "other"], [null, "other"],
  ])("ws message %j is %s", (m, t) => expect(parseWsMessage(m).type).toBe(t));
  it("telemetry batches keep the window start as epoch seconds", () => {
    const r = batchFromWire(tel("2026-11-10T10:41:00Z", 3));
    expect(r.ok && r.batch.t).toBe(Date.parse("2026-11-10T10:41:00Z") / 1000);
    expect(batchFromWire({ ...tel("nope", 1) }).ok).toBe(false);
  });
  it.each<[string, unknown, string | undefined]>([["a caption", "Services and what they depend on.", "Services and what they depend on."], ["no description (null)", null, undefined], ["an empty one", "", undefined], ["not text", 3, undefined]])("the description of a graph in /space: %s", (_, description, want) => {
    const doc = parseSpace({ protocol: "3", space: "s", graphs: [{ id: "g", role: "projection", description }] });
    expect(doc.graphs[0].description).toBe(want);
  });
  it("diagnostics of the engine's checker: position, element and message", () => {
    expect(parseDiagnostics([{ code: "UnknownField", severity: "error", message: "m", line: 3, column: 4, element: "d:a/b" }])).toEqual([{ code: "UnknownField", severity: "error", focus: "d:a/b", message: "m", line: 3, col: 4 }]);
    expect(parseDiagnostics(undefined)).toEqual([]);
  });
});

describe("EngineSource", () => {
  it("opens the space: catalog and data graphs, genesis snapshots, visible rejection for the kernel's valueKind any", async () => {
    const src = new EngineSource(BASE, { fetch: makeFetch(baseRoutes()) });
    const rejected: Rejection[] = [];
    src.onRejected((r) => rejected.push(r));
    const info = await src.open();
    expect(info.graphs.map((g) => [g.id, g.role, g.direction])).toEqual([["catalog", "catalog", "readonly"], ["master", "master", "mixed"]]);
    expect(src.catalogGraph).toBe("catalog");
    expect(rejected.map((r) => r.where)).toEqual(["ontology of catalog"]);
    expect(rejected[0].errors.join()).toContain("valueKind");
  });

  it("delivers history in order, then caught-up, and reports invalid records", async () => {
    const routes = baseRoutes({
      "GET /graphs/master/patches?after=0&to=3": () => ({ body: ndjson([wirePatch(0, 1), { nonsense: true }, wirePatch(1, 2)]) }),
      "GET /graphs/master/telemetry?format=batches": () => ({ body: ndjson([tel("2026-11-10T10:41:00Z", 1), tel("2026-11-10T10:42:00Z", 2), { graph: "master" }]) }),
    });
    const src = new EngineSource(BASE, { fetch: makeFetch(routes), WebSocket: FakeSocket as never });
    const rejected: Rejection[] = [];
    src.onRejected((r) => rejected.push(r));
    await src.open();
    const { events } = collect(src, "master");
    await settle();
    expect(events.map((e) => (e.type === "patch" ? `p${e.patch.to.revision}` : e.type === "telemetry" ? `t${e.batch.values[0]}` : e.type))).toEqual(["p1", "p2", "t1", "t2", "caught-up"]);
    expect(rejected.some((r) => r.where === "patch #2")).toBe(true);
    expect(rejected.some((r) => r.where === "line 3")).toBe(true);
    src.dispose();
  });

  it("subscribes over the socket after the last revision, ignores duplicates, fills a skipped revision over HTTP, stamps live patches with logical time", async () => {
    FakeSocket.instances = [];
    const log: string[] = [];
    let after1: string | null = null;
    const routes = baseRoutes({
      "GET /graphs/master/patches?after=0&to=3": () => ({ body: ndjson([wirePatch(0, 1)]) }),
      "GET /graphs/master/patches?after=1": () => { after1 = "asked"; return { body: ndjson([wirePatch(1, 2), wirePatch(2, 3)]) }; },
      "GET /graphs/master/telemetry?format=batches": () => ({ body: "" }),
    });
    const src = new EngineSource(BASE, { fetch: makeFetch(routes, log), WebSocket: FakeSocket as never });
    await src.open();
    const { events } = collect(src, "master");
    await settle();
    const ws = FakeSocket.instances[0];
    expect(JSON.parse(ws.sent[0])).toEqual({ op: "subscribe", graphs: ["master"], drafts: false, after: "7" }); // only subscribed graphs, resumed after the sequence number of the space document
    ws.push({ type: "time", logicalTime: "2026-11-10T10:45:00Z", rows: "9" });
    ws.push({ type: "patch", graph: "master", patch: wirePatch(0, 1) });   // duplicate of history
    ws.push({ type: "patch", graph: "master", patch: wirePatch(2, 3) });   // skipped revision 2
    await settle();
    const revs = events.flatMap((e) => (e.type === "patch" ? [e.patch.to.revision] : []));
    expect(revs).toEqual(["1", "2", "3"]);
    expect(after1).toBe("asked");
    expect(src.connection.status.gapsFilled).toBe(1);
    ws.push({ type: "patch", graph: "master", patch: wirePatch(3, 4) });
    await settle();
    const last = events.filter((e) => e.type === "patch").pop() as Extract<StreamEvent, { type: "patch" }>;
    expect(last.patch.to.revision).toBe("4");
    expect(last.time).toBe(Date.parse("2026-11-10T10:45:00Z") / 1000);
    src.dispose();
  });

  it("reconnects with backoff and resumes after the last sequence number", async () => {
    FakeSocket.instances = [];
    const routes = baseRoutes({
      "GET /graphs/master/patches?after=0&to=3": () => ({ body: ndjson([wirePatch(0, 1)]) }),
      "GET /graphs/master/patches?after=1": () => ({ body: ndjson([wirePatch(1, 2)]) }),
      "GET /graphs/master/telemetry?format=batches": () => ({ body: "" }),
      "GET /graphs/master/telemetry": () => ({ body: "" }), // the catch-up after the reconnect reads from the last window
    });
    const src = new EngineSource(BASE, { fetch: makeFetch(routes), WebSocket: FakeSocket as never, backoff: [1] });
    await src.open();
    const { events } = collect(src, "master");
    await settle();
    expect(src.connection.status.state).toBe("live");
    FakeSocket.instances[0].push({ type: "telemetry", graph: "master", seq: "12", batch: tel("2026-11-10T10:41:00Z", 5) });
    FakeSocket.instances[0].close();
    await settle(30);
    expect(FakeSocket.instances.length).toBe(2);
    expect(src.connection.status.reconnects).toBe(1);
    expect(src.connection.status.state).toBe("live");
    expect(JSON.parse(FakeSocket.instances[1].sent[0]).after).toBe("12");
    expect(events.flatMap((e) => (e.type === "patch" ? [e.patch.to.revision] : []))).toEqual(["1", "2"]); // read over HTTP after the reconnect
    src.dispose();
  });

  it("a definition activation resets telemetry and reads it again; a history gap asks for a resync; end is forwarded", async () => {
    FakeSocket.instances = [];
    let reads = 0;
    const routes = baseRoutes({
      "GET /graphs/master/patches?after=0&to=3": () => ({ body: "" }),
      "GET /graphs/master/patches?after=0": () => ({ body: "" }),
      "GET /graphs/master/telemetry?format=batches": () => { reads++; return { body: ndjson([tel("2026-11-10T10:41:00Z", reads)]) }; },
    });
    const src = new EngineSource(BASE, { fetch: makeFetch(routes), WebSocket: FakeSocket as never });
    await src.open();
    const { events } = collect(src, "master");
    await settle();
    const ws = FakeSocket.instances[0];
    ws.push({ type: "activation", catalogRevision: "11", graphs: [{ id: "master", from: "1", to: "2" }] });
    await settle();
    expect(events.some((e) => e.type === "telemetry-reset")).toBe(true);
    expect(events.filter((e) => e.type === "telemetry").length).toBe(2);
    ws.push({ type: "gap", firstSeq: "5" });
    ws.push({ type: "end" });
    await settle();
    expect(events.some((e) => e.type === "resync")).toBe(true);
    expect(events.some((e) => e.type === "end")).toBe(true);
    expect(src.connection.status.state).toBe("ended");
    src.dispose();
  });

  it("an activation that brings a graph the space did not have asks for a reload", async () => {
    FakeSocket.instances = [];
    const src = new EngineSource(BASE, { fetch: makeFetch(baseRoutes({ "GET /graphs/master/patches?after=0&to=3": () => ({ body: "" }), "GET /graphs/master/telemetry?format=batches": () => ({ body: "" }) })), WebSocket: FakeSocket as never });
    await src.open();
    const { events } = collect(src, "master");
    await settle();
    FakeSocket.instances[0].push({ type: "activation", catalogRevision: "11", graphs: [{ id: "master", from: "3", to: "3" }, { id: "newGraph", from: "0", to: "1" }] });
    await settle();
    expect(events.some((e) => e.type === "resync")).toBe(true);
    expect(events.filter((e) => e.type === "telemetry-reset").length).toBe(0);
    src.dispose();
  });

  it("the catalog graph is read without telemetry", async () => {
    FakeSocket.instances = [];
    const log: string[] = [];
    const src = new EngineSource(BASE, { fetch: makeFetch(baseRoutes({ "GET /graphs/catalog/patches?after=0&to=10": () => ({ body: "" }) }), log), WebSocket: FakeSocket as never });
    await src.open();
    collect(src, "catalog");
    await settle();
    expect(log.some((l) => l.includes("/graphs/catalog/telemetry"))).toBe(false);
    expect(JSON.parse(FakeSocket.instances[0].sent[0]).graphs).toEqual(["catalog"]);
    src.dispose();
  });

  it("definitions: lists modules with their text", async () => {
    const routes = baseRoutes({
      "GET /definitions": () => ({ body: [{ name: "acme.svc", version: "1.0.0", digest: "sha256:a", revision: "9" }] }),
      "GET /definitions/acme.svc": () => ({ body: "module acme.svc @ 1.0.0;" }),
    });
    const src = new EngineSource(BASE, { fetch: makeFetch(routes) });
    const d = src.definitions;
    expect(await d.modules()).toEqual([{ module: "acme.svc", version: "1.0.0", digest: "sha256:a", revision: "9", text: "module acme.svc @ 1.0.0;" }]);
  });

  it("lineage maps the engine record, and 404 is no lineage", async () => {
    const rec = { graph: "serviceTree", element: "r:1", kind: "relationship", projection: "serviceDeps", sink: "emit.4", rule: null, direction: "forward", operators: ["a", "b"], sources: [{ graph: "master", element: "e:2", kind: "entity", role: "s" }], sourceCount: "1", complete: true, groupKey: null };
    const src = new EngineSource(BASE, { fetch: makeFetch({ "GET /graphs/serviceTree/lineage?element=r%3A1&limit=500": () => ({ body: rec }), "GET /graphs/serviceTree/lineage?element=r%3A9&limit=500": () => ({ status: 404, body: { error: { code: "UnknownElement" } } }) }) });
    expect((await src.lineage("serviceTree", "r:1"))!.sources[0]).toEqual({ graph: "master", element: "e:2", kind: "entity", role: "s" });
    expect(await src.lineage("serviceTree", "r:9")).toBeNull();
  });

  it("a lifted edge's lineage is read page by page until the cursor ends", async () => {
    const rec = (sources: unknown[], next: string | null) => ({ graph: "capabilities", element: "r:L", kind: "relationship", projection: "capabilityView", sink: "lifted.1", rule: "lifted", direction: "readonly", operators: [], sources, sourceCount: "3", contributions: "3", complete: true, groupKey: null, next });
    const log: string[] = [];
    const src = new EngineSource(BASE, { fetch: makeFetch({
      "GET /graphs/capabilities/lineage?element=r%3AL&limit=500": () => ({ body: rec([{ graph: "serviceTree", element: "r:1", kind: "relationship", role: "d" }], "c1") }),
      "GET /graphs/capabilities/lineage?element=r%3AL&limit=500&after=c1": () => ({ body: rec([{ graph: "master", element: "e:2", kind: "entity", role: "sa" }, { graph: "master", element: "e:3", kind: "entity", role: "sb" }], null) }),
    }, log) });
    const l = (await src.lineage("capabilities", "r:L"))!;
    expect([l.sources.map((x) => x.element), l.contributions, l.next, log.length]).toEqual([["r:1", "e:2", "e:3"], "3", null, 2]);
  });

  it("reverse lineage lists what an element is part of, over pages, and 404 is nothing", async () => {
    const rev = (partOf: unknown[], next: string | null) => ({ graph: "master", element: "e:1", kind: "entity", partOf, count: "2", next, incompleteIn: ["health"] });
    const part = (g: string, e: string) => ({ graph: g, element: e, kind: "entity", role: "r", sink: "emit.1", rule: null });
    const src = new EngineSource(BASE, { fetch: makeFetch({
      "GET /graphs/master/lineage/reverse?element=e%3A1&limit=500": () => ({ body: rev([part("serviceTree", "e:a")], "n1") }),
      "GET /graphs/master/lineage/reverse?element=e%3A1&limit=500&after=n1": () => ({ body: rev([part("capabilities", "e:b")], null) }),
      "GET /graphs/master/lineage/reverse?element=e%3A9&limit=500": () => ({ status: 404, body: { error: { code: "UnknownElement" } } }),
    }) });
    const r = (await src.reverseLineage("master", "e:1"))!;
    expect([r.partOf.map((p) => `${p.graph}:${p.element}`), r.incompleteIn, r.next]).toEqual([["serviceTree:e:a", "capabilities:e:b"], ["health"], null]);
    expect(await src.reverseLineage("master", "e:9")).toBeNull();
  });

  it("references are read alone from the references route, page by page", async () => {
    const entry = (id: unknown, over: object = {}) => ({ id, state: "absent", graph: null, entityType: "AzureResource", identity: { domain: "azure", key: { resourceId: "/subscriptions/s/x" } }, uses: [{ relationshipType: "Concerns", end: "target", count: 4 }], ...over });
    const log: string[] = [];
    const src = new EngineSource(BASE, { fetch: makeFetch({
      "GET /graphs/master/references?limit=1000": () => ({ body: { graph: "master", references: [entry({ value: "e:x" }), { nonsense: true }], count: 3, next: "e:x" } }),
      "GET /graphs/master/references?limit=1000&after=e%3Ax": () => ({ body: { graph: "master", references: [entry("e:y", { state: "inOtherGraph", graph: "master", identity: null, uses: [] })], count: 3, next: null } }),
    }, log) });
    expect((await src.references("master")).map((r) => [r.id, r.state, r.graph, r.identity?.key.resourceId])).toEqual([["e:x", "absent", null, "/subscriptions/s/x"], ["e:y", "inOtherGraph", "master", undefined]]);
    expect(log.some((l) => l.includes("snapshot"))).toBe(false);
  });

  it.each([
    ["a retraction next to values", { ids: ["a"], values: [1], removed: ["b"] }, { ids: ["a"], removed: ["b"] }],
    ["a retraction alone, with empty ids and values", { ids: [], values: [], removed: ["b", "c"] }, { ids: [], removed: ["b", "c"] }],
    ["no retraction", { ids: ["a"], values: [1] }, { ids: ["a"], removed: undefined }],
  ])("a telemetry batch with %s", (_, body, want) => {
    const r = batchFromWire({ graph: "g", windowStart: "2026-01-01T00:00:00Z", target: "node", property: "cpu", tick: 3, ...body });
    expect(r.ok && { ids: r.batch.ids, removed: r.batch.removed }).toEqual(want);
  });
  it("a retraction that is not a list of ids is refused", () => expect(batchFromWire({ graph: "g", windowStart: "2026-01-01T00:00:00Z", target: "node", property: "cpu", ids: [], values: [], removed: [1] })).toMatchObject({ ok: false }));
  it("a meter whose threshold is read from the row has no warning level, and that is not an error", () => {
    expect(parseMeters({ meters: [{ id: "value", target: "node", warn: null, min: 0, max: 5 }], thresholds: [{ graph: "g", above: null, byRow: { above: "x" } }] }).meters[0].warn).toBeUndefined();
  });
});
