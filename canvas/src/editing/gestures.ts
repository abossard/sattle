// What an author does in any of the editors, as one change of the draft. A gesture is larger than a command: adding a step
// names it, renumbers the pieces of its statement and connects it; all of that is one patch and one step of undo.
// `compose` works on a scratch copy of the draft graph; the only thing it asks of the outside is the engine's skeleton of a new operator.
import { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import type { Result } from "../editor/commands";
import type { Palette, PaletteOperator } from "../editor/palette";
import { coalesce, rel } from "../editor/patches";
import { statementOf } from "./doc";
import { plan, type Command } from "./plan";
import { entryShape } from "./shapes";
import { addParam, declare, expand } from "./statements";
import { Work, add, chain, chainIn, drop, fresh, inPort, nameOfOp, opId, outOf, outPort, pieceName, port, relsFrom, relsTo, renameEdits, spec, under, type Skeleton } from "./work";

export type { Skeleton };

export type Gesture =
  /** one command as it is */
  | { g: "do"; cmd: Command }
  /** several gestures as one change */
  | { g: "batch"; label: string; items: Gesture[] }
  /** a step of `kind` in `statement` at position `at` (the end when absent); a statement of that name is created when there is none. `from` connects inputs by port name. */
  | { g: "addStep"; kind: string; statement: string; at?: number; from?: Record<string, string> }
  | { g: "removeStep"; id: string }
  /** a step to another place, in its statement or in another one, with what is in it; `at` counts the statement without the step */
  | { g: "moveStep"; id: string; statement: string; at?: number }
  /** another name for a statement: its pieces move with it and its readers keep reading it */
  | { g: "rename"; statement: string; to: string }
  /** a new entry in a slot of a step, from the values of the fields its shape asks for */
  | { g: "addEntry"; step: string; slot: string; childType: string; values: Record<string, string> }
  /** what an input port reads: a port, or nothing */
  | { g: "connect"; to: string; from: string | null }
  /** a statement kind from a row set: its steps become the template, and one use of the kind stands in their place (statements.ts) */
  | { g: "declare"; statement: string; name: string }
  /** the operators of its template in place of a use of a statement kind */
  | { g: "expand"; id: string }
  /** a place of a template as a parameter of its kind: an expression slot of a step, or one of its parameters */
  | { g: "param"; decl: string; name: string; step: string; slot?: string; field?: string }
  /** a match from elements picked on the data: one pattern node per entity, one pattern edge per relationship between them, and a step that keeps the rows `where` holds */
  | { g: "match"; statement: string; graph: string; nodes: { variable: string; type: string }[]; edges: { from: string; to: string; type: string }[]; where?: string };

export interface Composed { edits: Edit[]; label: string; select: string[] }

/** Put an operator at position `at` of a statement and connect it: `make` creates it under the id it is given. Returns that id. */
async function putIn(w: Work, statement: string, position: number | undefined, s: PaletteOperator, make: (id: string) => Promise<void> | void): Promise<string> {
  const old = chain(w, statement);
  const at = Math.max(0, Math.min(position ?? old.length, old.length));
  // the names of a chain one longer; the existing operators move to them
  const final = Array.from({ length: old.length + 1 }, (_, i) => opId(w.projection, pieceName(statement, i, old.length + 1)));
  const moves = new Map<string, string>();
  old.forEach((o, i) => { const to = final[i < at ? i : i + 1]; if (to !== o) moves.set(o, to); });
  if (moves.size) w.push(renameEdits(w.g, moves));
  const me = final[at], prev = at > 0 ? final[at - 1] : null, next = at < old.length ? final[at + 1] : null;
  await make(me);
  const myIn = chainIn(s), myOut = outOf(s);
  if (next) {
    // the step after reads this one; what it read before feeds this one
    const nextIn = inPort(w, next);
    const feeding = nextIn ? relsTo(w.g, nextIn).filter((r) => r.type === "wire") : [];
    w.push(feeding.map(drop));
    if (myIn) w.push(feeding.map((r) => add(r, r.source, port(me, myIn))));
    if (nextIn && myOut) w.push([rel(port(me, myOut), "wire", nextIn)]);
  } else if (prev) {
    // the new end of the statement: it reads the old end, and what read the old end from outside reads it
    const prevOut = outPort(w, prev);
    const inChain = (id: string) => final.some((o) => under(id, o));
    const outside = prevOut ? [...relsFrom(w.g, prevOut), ...relsTo(w.g, prevOut)].filter((r) => !inChain(r.source === prevOut ? r.target : r.source)) : [];
    if (myOut) { w.push(outside.map(drop)); w.push(outside.map((r) => (r.source === prevOut ? add(r, port(me, myOut), r.target) : add(r, r.source, port(me, myOut))))); }
    if (prevOut && myIn) w.push([rel(prevOut, "wire", port(me, myIn))]);
  }
  return me;
}

/** A new operator under the id given: the engine's skeleton, with the parameters the palette gives a default at that default. */
async function place(w: Work, kind: string, id: string): Promise<void> {
  const s = spec(w.palette, kind);
  if (!s?.supported) throw new Error(s ? `${kind} is not supported: ${s.unsupportedReason ?? ""}` : `unknown operator ${kind}`);
  const defaults = Object.fromEntries(s.params.filter((p) => p.default !== undefined).map((p) => [p.name, p.default!]));
  w.push(await w.skeleton(kind, w.projection, nameOfOp(id), defaults));
}

async function addStep(w: Work, x: Extract<Gesture, { g: "addStep" }>): Promise<void> {
  const s = spec(w.palette, x.kind);
  if (!s) throw new Error(`unknown operator ${x.kind}`);
  const me = await putIn(w, x.statement, x.at, s, (id) => place(w, x.kind, id));
  for (const [name, source] of Object.entries(x.from ?? {})) w.push([rel(source, "wire", port(me, name))]);
  w.select = [me];
}

/** Take a step out of its statement: what read it reads what it read, and the rest is renumbered. The step is deleted, or kept under the id `keepAs`. */
function takeOut(w: Work, id: string, keepAs: string | null): void {
  const statement = statementOf(nameOfOp(id));
  const old = chain(w, statement);
  const at = old.indexOf(id);
  if (at < 0) throw new Error("no such step");
  const myIn = inPort(w, id), myOut = outPort(w, id);
  const feeding = myIn ? relsTo(w.g, myIn).filter((r) => r.type === "wire") : [];
  const readers = myOut ? [...relsFrom(w.g, myOut), ...relsTo(w.g, myOut)] : [];
  if (keepAs) {
    w.push([...feeding, ...readers].map(drop));
    w.push(renameEdits(w.g, new Map([[id, keepAs]])));
  } else {
    const gone: Edit[] = [];
    for (const r of [...w.g.relationships()]) if (under(r.source, id) || under(r.target, id)) gone.push(drop(r));
    for (const e of [...w.g.entities()]) if (under(e.id, id)) gone.push({ kind: "entity", id: e.id, before: e, after: null });
    w.push(gone);
  }
  const rest = old.filter((o) => o !== id);
  const moves = new Map<string, string>();
  rest.forEach((o, i) => { const to = opId(w.projection, pieceName(statement, i, rest.length)); if (to !== o) moves.set(o, to); });
  const moved = (p: string): string => { for (const [a, b] of moves) if (under(p, a)) return b + p.slice(a.length); return p; };
  if (moves.size) w.push(renameEdits(w.g, moves));
  // what read the step reads what it read; at the end of a statement, the new end
  const end = rest.length ? outPort(w, opId(w.projection, statement)) : null;
  const feed = feeding.length ? moved(feeding[0].source) : at === old.length - 1 ? end : null;
  if (feed) w.push(readers.filter((r) => r.source === myOut && r.type === "wire").map((r) => rel(feed, "wire", moved(r.target))).filter((e) => fresh(w.g, e)));
  if (end && at === old.length - 1) w.push(readers.filter((r) => r.target === myOut).map((r) => add(r, moved(r.source), end)).filter((e) => fresh(w.g, e)));
}

function removeStep(w: Work, id: string): void { takeOut(w, id, null); w.select = []; }

/** Move a step to position `at` of a statement (its own or another), with everything in it. Positions count the statement without the step. */
async function moveStep(w: Work, id: string, statement: string, at: number | undefined): Promise<void> {
  const s = spec(w.palette, w.g.entity(id)?.type ?? "");
  if (!s) throw new Error("no such step");
  const waiting = opId(w.projection, "~moving");
  takeOut(w, id, waiting);
  w.select = [await putIn(w, statement, at, s, (me) => w.push(renameEdits(w.g, new Map([[waiting, me]]))))];
}

function connect(w: Work, to: string, from: string | null): void {
  w.push(relsTo(w.g, to).filter((r) => r.type === "wire").map(drop));
  if (from) run(w, { t: "wire", from, to });
}

function run(w: Work, cmd: Command): string {
  const p = plan(w.g, w.palette, w.projection, cmd);
  if (!p.ok) throw new Error(p.reason);
  w.push(p.value.edits);
  return p.value.label;
}

async function step(w: Work, x: Gesture): Promise<string> {
  switch (x.g) {
    case "do": {
      if (x.cmd.t !== "addOperator") return run(w, x.cmd);
      // an operator by the name the command gives it, connected to nothing: what a recorded walkthrough does before it wires
      const base = x.cmd.name || x.cmd.kind.toLowerCase();
      let name = base;
      for (let i = 2; w.g.entity(opId(w.projection, name)); i++) name = `${base}${i}`;
      await place(w, x.cmd.kind, opId(w.projection, name));
      w.select = [opId(w.projection, name)];
      return `add ${x.cmd.kind}`;
    }
    case "batch": for (const i of x.items) await step(w, i); return x.label;
    case "addStep": await addStep(w, x); return `add ${x.kind}`;
    case "removeStep": removeStep(w, x.id); return "remove step";
    case "moveStep": await moveStep(w, x.id, x.statement, x.at); return "move step";
    case "rename": {
      if (!/^[A-Za-z_]\w*$/.test(x.to)) throw new Error("a name is letters, digits and _");
      if (chain(w, x.to).length) throw new Error(`${x.to} already exists`);
      const old = chain(w, x.statement);
      w.push(renameEdits(w.g, new Map(old.map((o, i) => [o, opId(w.projection, pieceName(x.to, i, old.length))]))));
      return `rename ${x.statement}`;
    }
    case "connect": connect(w, x.to, x.from); return x.from ? "connect" : "disconnect";
    case "declare": declare(w, x.statement, x.name); return `make statement kind ${x.name}`;
    case "expand": w.select = expand(w, x.id).slice(0, 1); return "expand";
    case "param": addParam(w, x.decl, x.name, x.step, x); return `add parameter ${x.name}`;
    case "addEntry": {
      const kind = w.g.entity(x.step)?.type ?? "";
      const shape = entryShape(w.palette, kind, x.slot, x.childType);
      if (!shape) throw new Error(`${x.slot} takes no ${x.childType}`);
      return run(w, shape.command(x.step, x.values));
    }
    case "match": {
      const kind = w.palette.operators.find((o) => o.supported && o.slots.some((s) => s.children.includes("PatternNode")))?.kind;
      const graphIn = kind && spec(w.palette, kind)?.ports.find((p) => p.direction === "in" && p.kind === "graph")?.name;
      if (!kind || !graphIn) throw new Error("the engine offers no operator that matches a pattern");
      await addStep(w, { g: "addStep", kind, statement: x.statement, from: { [graphIn]: x.graph } });
      const me = w.select[0];
      for (const n of x.nodes) run(w, { t: "patternNode", id: me, variable: n.variable, type: n.type });
      for (const e of x.edges) run(w, { t: "patternEdge", id: me, variable: "", from: e.from, to: e.to, type: e.type });
      if (x.where) {
        // the operator that keeps rows by one condition: rows in, rows out, and nothing to fill but one expression
        const keep = w.palette.operators.find((o) => o.supported && !o.params.length && o.ports.length === 2 && chainIn(o) && outOf(o) && o.slots.filter((s) => s.name !== "port").length === 1 && o.slots.some((s) => s.multiplicity === "1" && s.children.includes("Expr")));
        if (!keep) throw new Error("the engine offers no operator that keeps rows by a condition");
        await addStep(w, { g: "addStep", kind: keep.kind, statement: x.statement });
        run(w, { t: "expr", id: w.select[0], slot: keep.slots.find((s) => s.name !== "port")!.name, src: x.where });
      }
      return `match ${x.nodes.length} element${x.nodes.length === 1 ? "" : "s"}`;
    }
  }
}

/** The gesture as one list of edits against `g`. `g` is not changed. */
export async function compose(g: Graph, palette: Palette, projection: string, gesture: Gesture, skeleton: Skeleton): Promise<Result<Composed>> {
  const w = new Work(Graph.fromSnapshot(g.toSnapshot()), palette, projection, skeleton);
  try {
    const label = await step(w, gesture);
    return { ok: true, value: { edits: coalesce(w.edits), label, select: w.select } };
  } catch (e) { return { ok: false, reason: (e as Error).message }; }
}
