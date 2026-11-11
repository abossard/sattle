// Telemetry NDJSON for static sources: one sparse batch per line.
// {"graph":"master","tick":3,"t":3,"target":"edge","property":"requests","ids":["Calls:a>b"],"values":[12.5]}
import type { Rejection } from "../core/validate";
import type { TelemetryBatch } from "./types";

export function checkTelemetryLine(x: unknown): { ok: true; batch: TelemetryBatch } | { ok: false; errors: string[] } {
  const e: string[] = [];
  const o = x as Record<string, unknown> | null;
  if (!o || typeof o !== "object") return { ok: false, errors: ["not an object"] };
  if (typeof o.graph !== "string" || !o.graph) e.push("graph must be a non-empty string");
  if (!Number.isInteger(o.tick) || (o.tick as number) < 0) e.push("tick must be a non-negative integer");
  if (typeof o.t !== "number") e.push("t must be a number (seconds)");
  if (o.target !== "node" && o.target !== "edge") e.push('target must be "node" or "edge"');
  if (typeof o.property !== "string" || !o.property) e.push("property must be a non-empty string");
  if (!Array.isArray(o.ids) || !o.ids.every((i) => typeof i === "string")) e.push("ids must be an array of element ids");
  if (!Array.isArray(o.values) || !o.values.every((v) => typeof v === "number")) e.push("values must be an array of numbers");
  else if (Array.isArray(o.ids) && o.ids.length !== o.values.length) e.push("ids and values differ in length");
  if (e.length) return { ok: false, errors: e };
  return { ok: true, batch: { graph: o.graph as string, tick: o.tick as number, t: o.t as number, target: o.target as "node" | "edge", property: o.property as string, ids: o.ids as string[], values: Float32Array.from(o.values as number[]) } };
}

export function parseTelemetryNdjson(text: string, source: string, reject: (r: Rejection) => void): TelemetryBatch[] {
  const out: TelemetryBatch[] = [];
  text.split("\n").forEach((line, i) => {
    if (!line.trim()) return;
    let raw: unknown;
    try { raw = JSON.parse(line); } catch (err) { reject({ source, where: `line ${i + 1}`, errors: [`not JSON: ${(err as Error).message}`] }); return; }
    const r = checkTelemetryLine(raw);
    if (r.ok) out.push(r.batch); else reject({ source, where: `line ${i + 1}`, errors: r.errors });
  });
  return out;
}
