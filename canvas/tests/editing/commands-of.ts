// The editor commands that make what a projection says: per entry of a slot (the commands the application's editor sends, and that
// the gestures of the notations end in), and the whole projection as the recorded command sequence of a walkthrough.
import type { Catalog } from "../../src/core/catalog";
import { valueText } from "../../src/core/model";
import { readProjection, type EntryDoc, type SlotDoc, type StepDoc } from "../../src/editing/doc";
import type { Palette } from "../../src/editor/palette";
import type { Command } from "../../src/state/types";

const value = (e: EntryDoc, slot?: string): string => (slot ? e.parts.find((p) => p.slot === slot) : e.parts[0])?.entries[0]?.expr ?? "";
const field = (e: EntryDoc, name: string): string => e.fields.find((f) => f.name === name)?.value ?? "";
const ref = (e: EntryDoc, rel: string) => e.refs.find((r) => r.rel === rel);

/** The command that adds the entry `e` to `slot` of `step`, or null when the editors have no gesture for an entry of its type. */
export function entryCommand(step: StepDoc, slot: SlotDoc, e: EntryDoc): Command | null {
  const id = step.id;
  if (e.expr !== null) {
    if (slot.value?.kind === "fieldRef") return { t: "read", id, slot: slot.name, path: e.expr };
    return slot.multiplicity === "*" || slot.multiplicity === "+" ? { t: "exprAdd", id, slot: slot.name, src: e.expr } : { t: "expr", id, slot: slot.name, src: e.expr };
  }
  switch (e.type) {
    case "Assign": return { t: "named", id, slot: slot.name, childType: "Assign", name: e.key, src: value(e) };
    case "Aggregate": { const second = e.parts.filter((p) => p.slot !== "value" && p.entries[0]?.expr != null)[0]?.entries[0].expr; return { t: "named", id, slot: slot.name, childType: "Aggregate", name: e.key, src: value(e, "value"), extra: { fn: field(e, "fn"), ...(second ? { second } : {}) } }; }
    case "SetProperty": return ref(e, "property")?.target ? { t: "set", id, slot: slot.name, property: ref(e, "property")!.target!, src: value(e) } : null;
    case "PatternNode": return ref(e, "ofType")?.target ? { t: "patternNode", id, variable: e.key, type: ref(e, "ofType")!.target! } : null;
    case "PatternEdge": return { t: "patternEdge", id, variable: field(e, "var"), from: ref(e, "from")!.targetName, to: ref(e, "to")!.targetName, type: ref(e, "ofType")!.target! };
    default: return e.fields.some((f) => f.name === "target") && !e.parts.length && !e.refs.length ? { t: "child", id, slot: slot.name, childType: e.type, key: field(e, "target"), params: Object.fromEntries(e.fields.filter((f) => f.value !== "").map((f) => [f.name, f.value])) } : null;
  }
}

/** A walkthrough: the commands that make a projection, and what a replay checks against. */
export interface Walk {
  name: string; module: string; original?: string; graphs: string[];
  commands: { label: string; cmd: Command }[];
  nonEmpty?: boolean; atLeast?: Record<string, number>; allowDiagnostics?: string[] | null;
}

/**
 * The commands that make `original` again under the name `twin`, in the order an author works: the projection with its inputs and
 * parameters (bound as the original is bound, `$original`), then row set by row set every operator by its name, what each reads,
 * its parameters, its references and its entries. Every id names the twin.
 */
export function walkthroughOf(cat: Catalog, palette: Palette, original: string, twin: string): Omit<Walk, "graphs" | "nonEmpty" | "atLeast"> {
  const doc = readProjection(cat, palette, original);
  if (!doc) throw new Error(`no projection ${original}`);
  const module = doc.module, T = `d:${module}/projection:${twin}`;
  const here = (id: string): string => (id === original || id.startsWith(`${original}/`) ? T + id.slice(original.length) : id);
  const prop = (id: string, name: string) => { const v = cat.entity(id)?.props[name]; return v ? valueText(v) : ""; };
  const ports = cat.children(original, "port");
  const kind = (p: string) => prop(p, "kind");
  const graphOf = (p: string) => cat.sources(p, "bindsPort").flatMap((b) => cat.targets(b, "boundFrom")).map((g) => cat.name(g))[0] ?? "";
  const commands: { label: string; cmd: Command }[] = [{
    label: "new projection",
    cmd: {
      t: "newProjection",
      spec: {
        module, name: twin, description: prop(original, "description"), outputModule: cat.targets(original, "outputOntology")[0] ?? "", graphName: twin,
        purpose: prop(original, "purpose") || undefined, retain: prop(original, "retain") || undefined,
        inputs: ports.filter((p) => kind(p) === "graph").map((p) => ({ port: prop(p, "name"), graph: graphOf(p), ontology: cat.targets(p, "expects")[0] ?? "" })),
        params: ports.filter((p) => kind(p) === "param").map((p) => ({ port: prop(p, "name"), kind: cat.name(cat.targets(p, "valueKind")[0] ?? ""), value: "$original", ...(prop(p, "default") ? { default: prop(p, "default") } : {}) })),
      },
    },
  }];
  const add = (label: string, cmd: Command) => commands.push({ label, cmd: JSON.parse(JSON.stringify(cmd), (_k, v) => (typeof v === "string" ? here(v) : v)) });
  for (const st of doc.statements) {
    for (const s of st.steps) add(`add ${s.kind} ${s.name}`, { t: "addOperator", kind: s.kind, name: s.name });
    for (const s of st.steps) {
      for (const x of s.inputs) if (x.from) add(`wire ${x.fromName} to ${s.name}.${x.port}`, { t: "wire", from: x.from, to: x.portId });
      for (const f of s.fields) if (f.value !== "") add(`${s.name} ${f.name}`, { t: "param", id: s.id, name: f.name, value: f.value });
      for (const r of s.refs) if (r.target) add(`${s.name} ${r.rel}`, { t: "ref", id: s.id, rel: r.rel, target: r.target });
      for (const sl of s.slots) for (const e of sl.entries) {
        const cmd = entryCommand(s, sl, e);
        if (!cmd) throw new Error(`no command makes ${s.kind}.${sl.name}: ${e.type}`);
        add(`${s.name}.${sl.name} ${e.text}`, cmd);
        if (e.expr !== null) for (const f of e.fields) if (f.value !== "") add(`${s.name}.${sl.name} ${e.text} ${f.name}`, { t: "param", id: e.id, name: f.name, value: f.value });
      }
    }
  }
  return { name: twin, module, original: doc.name, commands };
}
