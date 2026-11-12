// What an author fills in to add an entry to a slot, by the type of the entry, and the command it becomes.
// One table, keyed by the kernel's child types (the palette names them per slot), so every editor asks the same questions.
import type { Palette } from "../editor/palette";
import { slotOf } from "../editor/palette";
import type { Command } from "./plan";

/** What a field holds: a name, an expression, a field path, a definition of some type, one of several values, or text. */
export type FieldKind = "name" | "expr" | "path" | "ref" | "choice" | "text";
export interface ShapeField { key: string; label: string; kind: FieldKind; optional?: boolean; refTo?: string; values?: string[] }
export interface EntryShape { fields: ShapeField[]; command(step: string, values: Record<string, string>): Command }

const second = (fn: string): string => ({ last: "order", weightedMean: "weight" } as Record<string, string>)[fn] ?? "";

/** The shape of a new `childType` entry in `slot` of an operator of `kind`, or null when entries of that type are not added by hand (ports, the body of a fixpoint). */
export function entryShape(palette: Palette, kind: string, slot: string, childType: string): EntryShape | null {
  const s = slotOf(palette, kind, slot);
  if (!s || !s.children.includes(childType)) return null;
  const path = s.value?.kind === "fieldRef";
  const value: ShapeField = path ? { key: "src", label: "field", kind: "path" } : { key: "src", label: "value", kind: "expr" };
  switch (childType) {
    case "Assign":
      return { fields: [{ key: "name", label: "name", kind: "name" }, value], command: (id, v) => ({ t: "named", id, slot, childType, name: v.name, src: v.src }) };
    case "Aggregate": {
      const fns = palette.aggregates.filter((a) => a.supported && (!a.contexts || a.contexts.some((c) => c.operator === kind))).map((a) => a.name);
      return {
        fields: [{ key: "name", label: "name", kind: "name" }, { key: "fn", label: "aggregate", kind: "choice", values: fns }, { ...value, optional: true }, { key: "second", label: "order or weight", kind: "expr", optional: true }],
        command: (id, v) => ({ t: "named", id, slot, childType, name: v.name, src: v.src ?? "", extra: { fn: v.fn, ...(second(v.fn) ? { second: v.second ?? "" } : {}) } }),
      };
    }
    case "Expr": case "IdOf":
      return { fields: [value], command: (id, v) => (s.multiplicity === "*" || s.multiplicity === "+" ? { t: "exprAdd", id, slot, src: v.src } : { t: "expr", id, slot, src: v.src }) };
    case "SetProperty":
      return { fields: [{ key: "property", label: "property", kind: "ref", refTo: "PropertyType" }, value], command: (id, v) => ({ t: "set", id, slot, property: v.property, src: v.src }) };
    case "FieldRef":
      return { fields: [{ key: "path", label: "field", kind: "path" }], command: (id, v) => ({ t: "read", id, slot, path: v.path }) };
    case "PatternNode":
      return { fields: [{ key: "variable", label: "variable", kind: "name" }, { key: "type", label: "entity type", kind: "ref", refTo: "EntityType" }], command: (id, v) => ({ t: "patternNode", id, variable: v.variable, type: v.type }) };
    case "PatternEdge":
      return {
        fields: [{ key: "from", label: "from", kind: "name" }, { key: "to", label: "to", kind: "name" }, { key: "type", label: "relationship type", kind: "ref", refTo: "RelationshipType" }, { key: "variable", label: "variable", kind: "name", optional: true }],
        command: (id, v) => ({ t: "patternEdge", id, variable: v.variable ?? "", from: v.from, to: v.to, type: v.type }),
      };
    default: {
      // an entry made of parameters only and named by its target (the state of a lift): one field per parameter the palette lists
      const params = s.childTypes?.find((c) => c.type === childType)?.params ?? [];
      if (!params.some((p) => p.name === "target")) return null;
      return {
        fields: params.map((p): ShapeField => ({ key: p.name, label: p.name, kind: p.values ? "choice" : p.name === "target" ? "name" : "text", optional: !p.required, values: p.values })),
        command: (id, v) => ({ t: "child", id, slot, childType, key: v.target, params: v }),
      };
    }
  }
}
