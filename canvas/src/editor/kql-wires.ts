// The Wires notation's picture (note 11, section 4): the inputs, the `let`s and the steps of a text as cards, wired by what each statement
// reads, and the places where a statement names the table it reads, so a notation can point it at another `let`. Pure: a reading in, cards,
// wires and targets out.
import type { Analysis, Place } from "./kql";

export interface WireCard { id: string; kind: string; title: string; place: Place; reads: string[]; x: number; y: number }
export interface Wire { from: string; to: string; d: string }
/** The steps of one type, drawn in one frame. */
export interface WireGroup { type: string; x: number; y: number; w: number; h: number }
export interface WireLayout { cards: WireCard[]; wires: Wire[]; groups: WireGroup[]; width: number; height: number }

export const CARD_W = 210, CARD_H = 40, GAP_X = 56, GAP_Y = 10, PAD = 12, GROUP_HEAD = 18;

interface Reads { lets?: string[]; graphs?: string[]; rows?: string[] }
interface Stmt { kind: string; name?: string | null; type?: string | null; place: Place; query?: Place | null; reads?: Reads; operators?: { kind: string; parts?: { right?: Place } }[] }
const statementsOf = (a: Analysis) => (a.statements as Stmt[] | undefined) ?? [];
const isStep = (s: Stmt) => s.kind !== "let";

/** The card each statement reads, by name: the inputs, and for a `let` name the last `let` of that name before the statement. */
function readsOf(a: Analysis): { inputs: { id: string; name: string; kind: string; place: Place }[]; reads: string[][] } {
  const inputs = ((a.inputs as { name: string; kind: string; place: Place }[] | undefined) ?? []).map((x) => ({ id: `input:${x.name}`, name: x.name, kind: x.kind, place: x.place }));
  const known = new Set(inputs.map((x) => x.name));
  const lastLet = new Map<string, string>();
  const reads = statementsOf(a).map((s, i) => {
    const r = s.reads ?? {};
    const out = [...(r.graphs ?? []), ...(r.rows ?? [])].filter((n) => known.has(n)).map((n) => `input:${n}`);
    for (const n of r.lets ?? []) { const id = lastLet.get(n); if (id) out.push(id); }
    if (s.kind === "let" && s.name) lastLet.set(s.name, `statements.${i}`);
    return [...new Set(out)];
  });
  return { inputs, reads };
}

/** Columns by depth: inputs first, a `let` one column right of the furthest card it reads, every step in one last column grouped by type. */
export function wiresOf(a: Analysis): WireLayout {
  const stmts = statementsOf(a);
  const { inputs, reads } = readsOf(a);
  const column = new Map<string, number>(inputs.map((x) => [x.id, 0]));
  stmts.forEach((s, i) => { if (!isStep(s)) column.set(`statements.${i}`, 1 + Math.max(0, ...reads[i].map((r) => column.get(r) ?? 0))); });
  const last = 1 + Math.max(0, ...[...column.values()]);
  const title = (s: Stmt) => `${s.kind} ${s.name ?? s.type ?? ""}`.trim();
  const cols: { id: string; kind: string; title: string; place: Place; reads: string[]; type?: string }[][] = [];
  const put = (c: number, card: (typeof cols)[number][number]) => { (cols[c] ??= []).push(card); };
  for (const x of inputs) put(0, { id: x.id, kind: "input", title: `${x.kind}("${x.name}")`, place: x.place, reads: [] });
  stmts.forEach((s, i) => { if (!isStep(s)) put(column.get(`statements.${i}`)!, { id: `statements.${i}`, kind: s.kind, title: title(s), place: s.place, reads: reads[i] }); });
  const steps = stmts.map((s, i) => ({ s, i })).filter(({ s }) => isStep(s));
  const types = [...new Set(steps.map(({ s }) => s.type ?? ""))];
  for (const t of types) for (const { s, i } of steps.filter(({ s }) => (s.type ?? "") === t)) put(last, { id: `statements.${i}`, kind: s.kind, title: title(s), place: s.place, reads: reads[i], type: t });

  const cards: WireCard[] = [], groups: WireGroup[] = [];
  let height = 0;
  cols.forEach((col, c) => {
    const x = PAD + c * (CARD_W + GAP_X);
    let y = PAD;
    // the open frame of a type's steps, as one holder the closure reads and writes
    const open: { group: { type: string; y: number; n: number } | null } = { group: null };
    const close = () => { const g = open.group; if (g && g.n > 1) groups.push({ type: g.type, x: x - 6, y: g.y - GROUP_HEAD, w: CARD_W + 12, h: y - g.y + GROUP_HEAD - GAP_Y + 6 }); open.group = null; };
    for (const card of col ?? []) {
      if (card.type !== undefined && open.group?.type !== card.type) { close(); open.group = { type: card.type, y: y + GROUP_HEAD, n: 0 }; y += GROUP_HEAD; }
      if (open.group) open.group.n++;
      cards.push({ id: card.id, kind: card.kind, title: card.title, place: card.place, reads: card.reads, x, y });
      y += CARD_H + GAP_Y;
    }
    close();
    height = Math.max(height, y);
  });
  const at = new Map(cards.map((c) => [c.id, c]));
  const wires = cards.flatMap((to) => to.reads.map((r) => at.get(r)).filter((f): f is WireCard => !!f).map((from) => {
    const x1 = from.x + CARD_W, y1 = from.y + CARD_H / 2, x2 = to.x, y2 = to.y + CARD_H / 2, mid = (x1 + x2) / 2;
    return { from: from.id, to: to.id, d: `M${x1},${y1} C${mid},${y1} ${mid},${y2} ${x2},${y2}` };
  }));
  return { cards, wires, groups, width: PAD + cols.length * (CARD_W + GAP_X) - GAP_X + PAD, height: height + PAD };
}

/** Where statement `i` names a table it reads by a bare name: its query, or the right side of a join; with the `let`s it could read instead. */
export interface ReadTarget { what: string; name: string; place: Place; options: string[] }
export function targetsOf(text: string, a: Analysis, i: number): ReadTarget[] {
  const stmts = statementsOf(a), s = stmts[i];
  if (!s) return [];
  const options = [...new Set(stmts.slice(0, i).filter((x) => x.kind === "let" && x.name).map((x) => x.name!))];
  const bare = (p: Place, what: string): ReadTarget[] => {
    const m = /^(\(\s*)?([A-Za-z_]\w*)\s*\)?$/.exec(text.slice(p[0], p[1]));
    if (!m || (m[1] && !text.slice(p[0], p[1]).trimEnd().endsWith(")")) || !options.includes(m[2])) return [];
    const from = p[0] + (m[1]?.length ?? 0);
    return [{ what, name: m[2], place: [from, from + m[2].length], options }];
  };
  return [
    ...(s.query ? bare(s.query, "reads") : []),
    ...(s.operators ?? []).flatMap((o, j) => (o.kind === "join" && o.parts?.right ? bare(o.parts.right, `join ${j + 1} reads`) : [])),
  ];
}
