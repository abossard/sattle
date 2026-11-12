// Statement kinds made in the editor: a row set of a projection becomes a kind of step that can be used again. A kind is a
// declaration (`StatementDecl`), its parameters (`ParamDecl`, each filling one place of the template) and a template, which is an
// ordinary projection whose input ports are what a use connects and whose row set `result` is what a use gives. The palette is
// rendered from the declarations, so every editor offers the new kind like any operator, and a use is lowered to the operators of
// its template before anything is sent on.
//
// LOCAL STAND-IN. The engine has no statement declarations: it stores none, lists none in its palette and lowers none. Everything
// in this file runs in the browser; `withStatements` keeps declarations, templates and uses in memory and sends the engine only
// the lowered operators. See the end of the file for what the engine has to provide for this to be more than a stand-in.
import { Catalog, moduleOfId } from "../core/catalog";
import { Graph } from "../core/graph";
import type { Edit, Entity, Rel } from "../core/model";
import { valueText } from "../core/model";
import { paramEdits, setExprEdits } from "../editor/commands";
import type { Palette, PaletteOperator, PaletteParam, PaletteSlot } from "../editor/palette";
import { paramOf } from "../editor/palette";
import { contains, ent, rel, text } from "../editor/patches";
import type { EngineDiagnostic } from "../source/types";
import { statementOf } from "./doc";
import type { DraftPort } from "./draft";
import { exprText } from "./exprtext";
import { Work, chain, drop, nameOfOp, opId, outOf, pieceName, port, spec, under, type Skeleton } from "./work";

export const DECL = "StatementDecl", PARAM = "ParamDecl", RESULT = "result", ROLE = "statement";

/** A parameter of a statement kind: the place of the template it fills, an expression slot or a parameter of one of its steps. */
export interface ParamDoc { id: string; name: string; step: string; slot: string; field: string }
export interface Decl { id: string; name: string; template: string; params: ParamDoc[] }

const prop = (e: Entity | undefined, name: string): string => { const v = e?.props[name]; return v ? valueText(v) : ""; };
const nameOk = (n: string): boolean => /^[A-Za-z_]\w*$/.test(n);

/** The statement kinds the catalog declares. */
export function declarations(cat: Catalog): Decl[] {
  return [...cat.g.entities()].filter((e) => e.type === DECL).map((d): Decl => ({
    id: d.id, name: prop(d, "name"), template: cat.targets(d.id, "expansion")[0] ?? "",
    params: cat.children(d.id, "param").map((p) => { const e = cat.entity(p); return { id: p, name: prop(e, "name"), step: cat.targets(p, "fills")[0] ?? "", slot: prop(e, "slot"), field: prop(e, "field") }; }),
  }));
}

/** The palette with one entry per statement kind: its ports are the inputs of the template, its slots and parameters the declared parameters. */
export function paletteWith(palette: Palette, cat: Catalog): Palette {
  const kinds = declarations(cat).map((d): PaletteOperator => {
    const ports = cat.children(d.template, "port").map((p) => ({ name: prop(cat.entity(p), "name"), direction: "in" as const, kind: prop(cat.entity(p), "kind") }));
    const fields = d.params.filter((p) => p.field).flatMap((p): PaletteParam[] => { const of = paramOf(palette, cat.typeOf(p.step), p.field); return of ? [{ ...of, name: p.name, required: false, default: prop(cat.entity(p.step), p.field) }] : []; });
    const slots = d.params.filter((p) => p.slot).map((p): PaletteSlot => ({ name: p.name, children: ["Expr"], multiplicity: "?", ordered: false }));
    return {
      kind: d.name, supported: true, role: ROLE, container: "Projection", slot: "op", strategy: null, sink: false, telemetry: false,
      ports: [...ports, { name: "out", direction: "out", kind: "rows" }], params: fields, slots: [...slots, { name: "port", children: ["Port"], multiplicity: "*", ordered: false }],
    };
  });
  return kinds.length ? { ...palette, operators: [...palette.operators.filter((o) => !kinds.some((k) => k.kind === o.kind)), ...kinds] } : palette;
}

const portEdits = (owner: string, name: string, direction: "in" | "out", kind: string): Edit[] => [ent(port(owner, name), "Port", { name: text(name), direction: text(direction), kind: text(kind) }), contains(owner, port(owner, name), "port")];
const subtree = (g: Graph, roots: readonly string[]): { entities: Entity[]; rels: Rel[] } => {
  const inside = (id: string) => roots.some((r) => under(id, r));
  return { entities: [...g.entities()].filter((e) => inside(e.id)), rels: [...g.relationships()].filter((r) => inside(r.source) || inside(r.target)) };
};
const gone = (t: { entities: Entity[]; rels: Rel[] }): Edit[] => [...t.rels.map(drop), ...t.entities.map((e): Edit => ({ kind: "entity", id: e.id, before: e, after: null }))];

/**
 * Copies of operators under new ids, with everything below them. A relationship between two copied elements is copied; one that
 * leaves the copy (the type an emit names) points where it pointed; one that enters it is `entering`'s to decide.
 */
function copyEdits(g: Graph, moves: ReadonlyMap<string, string>, entering: (r: Rel, target: string) => Edit[]): Edit[] {
  const map = (id: string): string | null => { for (const [a, b] of moves) if (under(id, a)) return b + id.slice(a.length); return null; };
  const t = subtree(g, [...moves.keys()]);
  const out: Edit[] = t.entities.map((e) => { const id = map(e.id)!; return ent(id, e.type, moves.has(e.id) ? { ...e.props, name: text(nameOfOp(id)) } : e.props); });
  for (const r of t.rels) {
    const s = map(r.source), d = map(r.target);
    if (s && d) out.push(rel(s, r.type, d, r.props));
    else if (s) { if (r.type !== "wire") out.push(rel(s, r.type, r.target, r.props)); }
    else out.push(...entering(r, d!));
  }
  return out;
}

/** Make a statement kind `name` from the row set `statement` of the projection: its steps become the template, and one use of the kind stands where they stood. */
export function declare(w: Work, statement: string, name: string): void {
  const P = w.projection, M = `d:${moduleOfId(P)}`, T = `${M}/projection:${name}`, D = `${M}/statement:${name}`;
  if (!nameOk(name)) throw new Error("a name is letters, digits and _");
  if (w.g.entity(T) || w.g.entity(D) || spec(w.palette, name)) throw new Error(`${name} already exists`);
  const ops = chain(w, statement);
  if (!ops.length) throw new Error(`there is no row set ${statement}`);
  const end = ops[ops.length - 1], endOut = outOf(spec(w.palette, w.g.entity(end)?.type ?? ""));
  if (!endOut) throw new Error("a sink gives no rows, so it cannot be a statement kind");
  const inside = (id: string) => ops.some((o) => under(id, o));
  const tree = subtree(w.g, ops);
  // what the row set reads from outside: one input of the kind per source, named after it
  const sources = [...new Set(tree.rels.filter((r) => r.type === "wire" && !inside(r.source) && inside(r.target)).map((r) => r.source))];
  const taken = new Set<string>();
  const inputs = new Map(sources.map((s) => {
    const e = w.g.entity(s), kind = prop(e, "kind");
    let n = kind === "rows" ? "in" : prop(e, "name") || kind;
    for (let i = 2; taken.has(n); i++) n = `${n.replace(/\d+$/, "")}${i}`;
    taken.add(n);
    return [s, { name: n, kind }] as const;
  }));
  const readers = tree.rels.filter((r) => inside(r.source) !== inside(r.target) && r.type !== "contains" && (r.source === port(end, endOut) || r.target === port(end, endOut)));
  // what points at the row set from outside (a rule that governs it) points at the use; nothing outside may point into the middle of it
  const pointing = tree.rels.filter((r) => !inside(r.source) && inside(r.target) && r.type !== "contains" && r.type !== "wire" && !readers.includes(r));
  const inner = pointing.find((r) => r.target !== end);
  if (inner) throw new Error(`${w.cat.name(inner.source)} refers to a step inside ${statement} (${inner.type}), so the row set cannot be folded into one step`);
  // the template: the steps under the name `result`, the sources as ports
  w.push([ent(T, "Projection", { name: text(name) }), contains(M, T, "projection"), ent(D, DECL, { name: text(name) }), contains(M, D, "statement"), rel(D, "expansion", T)]);
  for (const [s, i] of inputs) w.push([...portEdits(T, i.name, "in", i.kind), ...w.cat.targets(s, "expects").map((o) => rel(port(T, i.name), "expects", o))]);
  const moves = new Map(ops.map((o, i) => [o, opId(T, pieceName(RESULT, i, ops.length))]));
  w.push(copyEdits(w.g, moves, (r, target) => (r.type === "contains" ? [rel(T, "contains", target, r.props)] : r.type === "wire" && inputs.has(r.source) ? [rel(port(T, inputs.get(r.source)!.name), "wire", target)] : [])));
  // the use: one step in place of the row set, reading what it read and read by what read it
  const U = opId(P, statement);
  w.push(gone(tree));
  w.push([ent(U, name, { name: text(statement) }), contains(P, U, "op"), ...portEdits(U, "out", "out", "rows")]);
  for (const [s, i] of inputs) w.push([...portEdits(U, i.name, "in", i.kind), rel(s, "wire", port(U, i.name))]);
  w.push(readers.map((r) => (inside(r.source) ? rel(port(U, "out"), r.type, r.target, r.props) : rel(r.source, r.type, port(U, "out"), r.props))));
  w.push(pointing.map((r) => rel(r.source, r.type, U, r.props)));
  w.select = [U];
}

/** Make a place of a template a parameter of its kind: an expression slot of a step, or one of its parameters. What stands there is the default. */
export function addParam(w: Work, decl: string, name: string, step: string, place: { slot?: string; field?: string }): void {
  const d = declarations(w.cat).find((x) => x.id === decl);
  if (!d) throw new Error("no such statement kind");
  if (!nameOk(name) || name === "port" || d.params.some((p) => p.name === name)) throw new Error(`${name} cannot be the name of a parameter here`);
  if (!under(step, d.template) || !w.g.entity(step)) throw new Error("a parameter fills a place of the template");
  const id = `${decl}/param:${name}`;
  w.push([ent(id, PARAM, { name: text(name), ...(place.slot ? { slot: text(place.slot) } : { field: text(place.field ?? "") }) }), contains(decl, id, "param"), rel(id, "fills", step)]);
}

/** Put the operators of its template in place of a use, with the values the use gives its parameters. Returns the ids of the operators that stand for it. */
export function expand(w: Work, use: string): string[] {
  const u = w.g.entity(use), cat = w.cat;
  const d = u && declarations(cat).find((x) => x.name === u.type);
  if (!u || !d) throw new Error("not a use of a statement kind");
  const P = use.slice(0, use.lastIndexOf("/op:")), S = statementOf(nameOfOp(use));
  const mine = chain(w, S, P), at = mine.indexOf(use);
  const result = chain(w, RESULT, d.template), others = cat.children(d.template, "op").filter((o) => !result.includes(o));
  if (!result.length) throw new Error(`the template of ${d.name} has no row set ${RESULT}`);
  // what the use is connected to, and what it holds, read before it goes
  const feeds = new Map(cat.children(use, "port").map((p) => [prop(cat.entity(p), "name"), cat.sources(p, "wire")[0] ?? null]));
  const out = port(use, "out");
  const readers = [...w.g.relationships()].filter((r) => r.type !== "contains" && (r.source === out || r.target === out));
  const pointing = [...w.g.relationships()].filter((r) => r.target === use && r.type !== "contains");
  const values = d.params.map((p) => ({ p, expr: p.slot ? cat.children(use, p.name).map((c) => exprText(cat, c))[0] : undefined, field: p.field ? prop(u, p.name) : "" }));
  w.push(gone(subtree(w.g, [use])));
  // the row set makes room: its other steps take the names of a longer chain
  const n = mine.length - 1 + result.length;
  const renamed = new Map<string, string>();
  mine.filter((o) => o !== use).forEach((o, i) => { const to = opId(P, pieceName(S, i < at ? i : i + result.length, n)); if (to !== o) renamed.set(o, to); });
  const now = (id: string): string => { for (const [a, b] of renamed) if (under(id, a)) return b + id.slice(a.length); return id; };
  if (renamed.size) w.rename(renamed);
  // the steps of the template: `result` in the row set, any other row set of it under the name of the use
  const moves = new Map<string, string>([...result.map((o, i) => [o, opId(P, pieceName(S, at + i, n))] as const), ...others.map((o) => [o, opId(P, `${S}__${nameOfOp(o)}`)] as const)]);
  w.push(copyEdits(w.g, moves, (r, target) => {
    if (r.type === "contains") return [rel(P, "contains", target, r.props)];
    const feed = r.type === "wire" && under(r.source, d.template) ? feeds.get(prop(w.g.entity(r.source), "name")) : null;
    return feed ? [rel(now(feed), "wire", target)] : [];
  }));
  const last = moves.get(result[result.length - 1])!, lastOut = port(last, outOf(spec(w.palette, w.g.entity(last)?.type ?? "")) ?? "out");
  w.push(readers.map((r) => (r.source === out ? rel(lastOut, r.type, now(r.target), r.props) : rel(now(r.source), r.type, lastOut, r.props))).filter((e) => e.kind !== "relationship" || !w.g.rel(e.id)));
  w.push(pointing.map((r) => rel(now(r.source), r.type, last, r.props)));
  for (const { p, expr, field } of values) {
    const place = moves.get(p.step);
    if (!place) continue;
    const edits = expr !== undefined ? setExprEdits(w.g, place, p.slot, expr, w.palette) : field ? (() => { const of = paramOf(w.palette, w.g.entity(place)?.type ?? "", p.field); return of ? paramEdits(w.g, place, of, field) : null; })() : null;
    if (edits && !edits.ok) throw new Error(`${d.name}, parameter ${p.name}: ${edits.reason}`);
    if (edits) w.push(edits.value);
  }
  return [...moves.values()];
}

const never: Skeleton = async () => { throw new Error("lowering places no operator"); };

/**
 * The graph the engine can take: every use replaced by the operators of its template, and the declarations and templates left out.
 * `from` says which use each operator of an expansion came from.
 */
export function lower(g: Graph, palette: Palette): { graph: Graph; from: Map<string, string> } {
  const graph = Graph.fromSnapshot(g.toSnapshot());
  const from = new Map<string, string>();
  for (let round = 0; ; round++) {
    const cat = new Catalog(graph), decls = declarations(cat);
    const templates = decls.map((d) => d.template);
    if (round === 0 && !decls.length) return { graph, from };
    if (round === 0) {
      // a kind whose template uses the kind, directly or through others, has no expansion
      const uses = new Map(decls.map((d) => [d.name, [...graph.entities()].filter((e) => under(e.id, d.template) && decls.some((x) => x.name === e.type)).map((e) => e.type)]));
      const reaches = (kind: string, goal: string, seen: Set<string>): boolean => (uses.get(kind) ?? []).some((k) => k === goal || (!seen.has(k) && reaches(k, goal, seen.add(k))));
      const self = decls.find((d) => reaches(d.name, d.name, new Set()));
      if (self) throw new Error(`the statement kind ${self.name} uses itself`);
    }
    const use = [...graph.entities()].find((e) => decls.some((d) => d.name === e.type) && !templates.some((t) => under(e.id, t)));
    if (!use) {
      // what only the editor knows goes: declarations, their parameters and the templates
      const local = subtree(graph, [...decls.map((d) => d.id), ...templates]);
      new Work(graph, palette, "", never).push(gone(local));
      return { graph, from };
    }
    const P = use.id.slice(0, use.id.lastIndexOf("/op:"));
    for (const id of expand(new Work(graph, paletteWith(palette, cat), P, never), use.id)) from.set(id, from.get(use.id) ?? use.id);
  }
}

/** The edits that turn `a` into `b`. */
export function diffEdits(a: Graph, b: Graph): Edit[] {
  // most elements of two versions of one graph are the same objects: only the others are compared by what they hold
  const same = (x: { type: string; props: Entity["props"] }, y: { type: string; props: Entity["props"] }) => x === y || (x.type === y.type && JSON.stringify(Object.entries(x.props).sort()) === JSON.stringify(Object.entries(y.props).sort()));
  const out: Edit[] = [];
  for (const r of a.relationships()) { const to = b.rel(r.id); if (!to || !same(r, to)) out.push({ kind: "relationship", id: r.id, before: r, after: null }); }
  for (const e of a.entities()) { const to = b.entity(e.id); if (!to) out.push({ kind: "entity", id: e.id, before: e, after: null }); else if (!same(e, to)) out.push({ kind: "entity", id: e.id, before: e, after: to }); }
  for (const e of b.entities()) if (!a.entity(e.id)) out.push({ kind: "entity", id: e.id, before: null, after: e });
  for (const r of b.relationships()) { const was = a.rel(r.id); if (!was || !same(was, r)) out.push({ kind: "relationship", id: r.id, before: null, after: r }); }
  return out;
}

/**
 * A draft that can hold statement kinds, over one that cannot: the author's graph (with declarations, templates and uses) lives
 * here, and every change is sent to `inner` as the difference it makes to the lowered graph. A change the engine refuses is not
 * kept. What is lost: the declarations and uses are in this object only, so a reload shows the lowered operators, and undo of the
 * inner draft does not reach them.
 */
export function withStatements(inner: DraftPort): DraftPort & { lowered(): Graph } {
  const graph = Graph.fromSnapshot(inner.graph.toSnapshot());
  graph.version = inner.graph.version;
  const placeholders = new Set(inner.placeholders);
  let palette = inner.palette, from = new Map<string, string>(), stale = false;
  const refresh = () => { if (stale) { palette = paletteWith(inner.palette, new Catalog(graph)); stale = false; } };
  const patch = (g: Graph, edits: Edit[]) => { const v = g.version ?? { tenant: "", graph: "catalog", revision: "0" }; return g.apply({ from: v, to: { ...v, revision: String(Number(v.revision) + 1) }, projection: "catalog", profile: "extensionalFinite", edits, cause: { inputs: [], configurationVersion: "0" } }); };
  return {
    graph, placeholders,
    get palette(): Palette { refresh(); return palette; },
    /** diagnostics of an expansion are reported at the use it came from */
    get diagnostics(): EngineDiagnostic[] { return inner.diagnostics.map((d) => { for (const [op, use] of from) if (under(d.focus, op)) return { ...d, focus: use }; return d; }); },
    skeleton: async (kind, container, name, params) => {
      refresh();
      const s = palette.operators.find((o) => o.kind === kind);
      if (s?.role !== ROLE) return inner.skeleton(kind, container, name, params);
      const id = opId(container, name);
      return { edits: [ent(id, kind, { name: text(name) }), contains(container, id, "op"), ...s.ports.flatMap((p) => portEdits(id, p.name, p.direction, p.kind))], placeholders: [] };
    },
    async apply(edits, label) {
      const next = Graph.fromSnapshot(graph.toSnapshot());
      next.version = graph.version;
      const tried = patch(next, edits);
      if (!tried.ok) return { ok: false, error: tried.conflicts[0]?.message ?? "the edit does not apply" };
      let low: ReturnType<typeof lower>;
      try { low = lower(next, inner.palette); } catch (e) { return { ok: false, error: (e as Error).message }; }
      const diff = diffEdits(inner.graph, low.graph);
      const sent = diff.length ? await inner.apply(diff, label) : { ok: true as const };
      if (!sent.ok) return sent;
      patch(graph, edits);
      for (const e of edits) if (e.kind === "entity" && e.before !== null) placeholders.delete(e.id);
      from = low.from; stale = true;
      return { ok: true };
    },
    lowered: () => lower(graph, inner.palette).graph,
  };
}

// What the engine has to provide for statement kinds to be real (each is done here, in the browser, by the function named):
// 1. node types `StatementDecl`, `ParamDecl` (with `fills`) and a use of a kind in the catalog's kernel, so a draft can hold them and a
//    commit keeps them (here: this object's memory);
// 2. `GET /palette` listing the declared kinds with ports, parameters and slots (`paletteWith`);
// 3. the skeleton of a use (`withStatements.skeleton`);
// 4. lowering at check and at plan time, with the use each operator came from, so diagnostics, lineage and the plan name the use
//    and its parameter (`lower` and its `from`);
// 5. a text form of a declaration and of a use, parsed and printed (nothing here: a use has no canonical text);
// 6. names a template brings in kept apart from the author's (`expand` prefixes them with the name of the use, as `lift` does today).
