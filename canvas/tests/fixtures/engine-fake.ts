// Fakes of the engine for the source tests: HTTP routes, a socket the test pushes messages into, and the records of a small space.
import { EngineSource } from "../../src/source/engine";
import { patchToWire } from "../../src/core/wire";
import type { StreamEvent } from "../../src/source/types";
import { addN, ent, patch } from "../helpers";

export const BASE = "http://engine.test";
export const ont = (id: string, valueKind = "text") => ({
  schemaVersion: "1.0", reference: { id, version: "1.0.0", digest: { value: "sha256:x" } }, noEntityType: { value: "NoType" }, noRelationshipType: { value: "NoType" },
  properties: [{ id: { value: "name" }, valueKind, nullable: false, itemKind: null, unit: null, role: "graphData" }],
  entityTypes: [{ id: { value: "NoType" }, properties: [] }, { id: { value: "Pod" }, properties: [{ value: "name" }] }],
  relationshipTypes: [{ id: { value: "NoType" }, sourceTypes: [], targetTypes: [], properties: [] }],
});
export const descriptor = (graph: string, rev: string) => ({ version: { tenant: { value: "lab" }, graph: { value: graph }, revision: { value: rev } }, projection: { value: "ingest" }, ontology: { id: "acme.ops", version: "1.0.0", digest: { value: "sha256:x" } }, virtualRegions: [] });
export const space = (extra: Record<string, unknown> = {}) => ({
  space: "operations", protocol: "3", kernelDigest: "sha256:k", seq: "7", catalogRevision: "10",
  graphs: [
    { id: "catalog", role: "catalog", head: "10", ontology: "sattle.kernel", inputs: [], direction: "readonly", editable: false },
    { id: "master", role: "master", head: "3", ontology: "acme.ops", inputs: [], direction: "mixed", editable: true, projection: "ingest" },
  ], input: { mode: "replay", logicalTime: "2026-11-10T10:41:00Z", rows: "5" }, ...extra,
});
export const wirePatch = (from: number, to: number) => patchToWire({ ...patch(String(from), String(to), [addN(ent(`p${to}`, "Pod"))]), from: { tenant: "lab", graph: "master", revision: String(from) }, to: { tenant: "lab", graph: "master", revision: String(to) } });
export const ndjson = (xs: unknown[]) => xs.map((x) => JSON.stringify(x)).join("\n");
export const tel = (windowStart: string, v: number) => ({ graph: "master", tick: 1, t: 0, windowStart, target: "node", property: "cpu", ids: ["p1"], values: [v] });

export class FakeSocket {
  static instances: FakeSocket[] = [];
  sent: string[] = [];
  onopen: (() => void) | null = null; onmessage: ((e: { data: string }) => void) | null = null; onclose: (() => void) | null = null; onerror: (() => void) | null = null;
  constructor(public url: string) { FakeSocket.instances.push(this); queueMicrotask(() => this.onopen?.()); }
  send(m: string) { this.sent.push(m); }
  close() { queueMicrotask(() => this.onclose?.()); }
  push(m: unknown) { this.onmessage?.({ data: JSON.stringify(m) }); }
}

export function makeFetch(routes: Record<string, () => { status?: number; body: unknown }>, log: string[] = []): typeof fetch {
  return (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const key = `${init?.method ?? "GET"} ${url.pathname}${url.search}`;
    log.push(key);
    const r = routes[key] ?? routes[`${init?.method ?? "GET"} ${url.pathname}`];
    if (!r) return new Response(JSON.stringify({ error: { code: "NoRoute", message: key } }), { status: 404 });
    const { status = 200, body } = r();
    return new Response(typeof body === "string" ? body : JSON.stringify(body), { status });
  }) as typeof fetch;
}

export const baseRoutes = (over: Record<string, () => { status?: number; body: unknown }> = {}) => ({
  "GET /space": () => ({ body: space() }),
  "GET /graphs/catalog/ontology": () => ({ body: ont("sattle.kernel", "any") }),
  "GET /graphs/master/ontology": () => ({ body: ont("acme.ops") }),
  "GET /graphs/catalog/snapshot?revision=0&references=false": () => ({ body: { descriptor: descriptor("catalog", "0"), extent: { entities: [], relationships: [] } } }),
  "GET /graphs/master/snapshot?revision=0&references=false": () => ({ body: { descriptor: descriptor("master", "0"), extent: { entities: [], relationships: [] } } }),
  ...over,
});

export const collect = (src: EngineSource, graph: string) => {
  const events: StreamEvent[] = [];
  const off = src.subscribe(graph, (e) => events.push(e));
  return { events, off };
};
export const settle = (ms = 15) => new Promise((r) => setTimeout(r, ms));

