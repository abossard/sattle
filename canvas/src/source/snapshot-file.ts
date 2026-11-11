// Snapshot files {descriptor, extent} and patch lists, validated record by record. Shared by the static and engine sources.
import type { Patch, Snapshot } from "../core/model";
import { checkDescriptor, checkEntityState, checkPatch, checkRelationshipState, type Rejection } from "../core/validate";
import type { EntityState, GraphDescriptorWire, GraphExtentWire, RelationshipState } from "../core/wire-types";
import { entityFromWire, patchFromWire, relFromWire, versionFromWire } from "../core/wire";

export interface SnapshotFile { descriptor: GraphDescriptorWire; extent: GraphExtentWire }
export type Reject = (r: Rejection) => void;

export function parseSnapshot(raw: unknown, source: string, reject: Reject): { snapshot: Snapshot; descriptor: GraphDescriptorWire } | null {
  const file = raw as SnapshotFile;
  const d = checkDescriptor(file?.descriptor);
  if (!d.ok) { reject({ source, where: "descriptor", errors: d.errors }); return null; }
  const entities: EntityState[] = [];
  const relationships: RelationshipState[] = [];
  (file.extent?.entities ?? []).forEach((e, i) => {
    const r = checkEntityState(e);
    if (r.ok) entities.push(r.value); else reject({ source, where: `entity #${i}`, errors: r.errors });
  });
  (file.extent?.relationships ?? []).forEach((e, i) => {
    const r = checkRelationshipState(e);
    if (r.ok) relationships.push(r.value); else reject({ source, where: `relationship #${i}`, errors: r.errors });
  });
  return {
    descriptor: d.value,
    snapshot: { version: versionFromWire(d.value.version), entities: entities.map(entityFromWire), relationships: relationships.map(relFromWire) },
  };
}

/** One patch record; null (and a rejection) when it does not validate. */
export function parsePatch(raw: unknown, source: string, where: string, reject: Reject): Patch | null {
  const r = checkPatch(raw);
  if (!r.ok) { reject({ source, where, errors: r.errors }); return null; }
  return patchFromWire(r.value);
}

/** NDJSON or JSON text into raw records; parse errors are rejected with their line number. */
export function parseRecords(text: string, source: string, ndjson: boolean, reject: Reject): unknown[] {
  const trimmed = text.trim();
  if (!trimmed) return [];
  if (!ndjson) { const parsed = JSON.parse(trimmed); return Array.isArray(parsed) ? parsed : [parsed]; }
  const out: unknown[] = [];
  trimmed.split("\n").forEach((line, i) => {
    if (!line.trim()) return;
    try { out.push(JSON.parse(line)); } catch (e) { reject({ source, where: `line ${i + 1}`, errors: [`not JSON: ${(e as Error).message}`] }); }
  });
  return out;
}
