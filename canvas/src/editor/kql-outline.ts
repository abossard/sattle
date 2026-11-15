// The Outline notation's rows (note 11, section 4): the engine's outline of a text as a tree of rows, each with its place in the text. The
// graph line with its settings, the inputs, the parameters, the functions, then every `let` and step; under a statement its operators, under
// an operator its parts. A row that is an item of a list (`listsOf`) names it, so the notation removes it with the editing module's rule.
import type { Analysis, Place } from "./kql";
import { listsOf, type List } from "./kql-edit";

export interface OutlineRow {
  id: string; parent: string | null; level: number;
  /** what the row is: `graph`, `setting`, `input`, `parameters`, `parameter`, `function`, a statement kind, an operator kind, a part name */
  kind: string;
  /** what the row says before its text (a statement's name or type, a part's name); its text is the reading's text at `place` */
  label: string;
  place: Place;
  /** the list the row is an item of, and its index there */
  item: { list: List; i: number } | null;
  /** the statement whose pipeline a new operator goes into, on the row that offers one */
  adds?: number;
  hasChildren: boolean;
}

interface Op { kind: string; place: Place; parts?: Record<string, unknown>; inner?: Op[][] }
interface Stmt { kind: string; name?: string | null; type?: string | null; place: Place; settings?: { name: string; place: Place }[]; operators?: Op[] }

const placeOf = (x: unknown): Place | null => (Array.isArray(x) && x.length === 2 && typeof x[0] === "number" ? (x as Place) : x && typeof x === "object" && Array.isArray((x as { place?: unknown }).place) ? (x as { place: Place }).place : null);
/** A part's places: one place, a list of places, or items that carry one. */
const partPlaces = (v: unknown): Place[] => { const one = placeOf(v); return one ? [one] : Array.isArray(v) ? v.map(placeOf).filter((p): p is Place => !!p) : []; };

/** The rows of the outline of `a`, in text order, parents before their children. */
export function outlineRows(a: Analysis): OutlineRow[] {
  const lists = new Map(listsOf(a).map((l) => [l.id, l]));
  const itemOf = (listId: string, p: Place) => { const list = lists.get(listId); const i = list ? list.items.findIndex((x) => x[0] === p[0] && x[1] === p[1]) : -1; return list && i >= 0 ? { list, i } : null; };
  const rows: OutlineRow[] = [], byId = new Map<string, OutlineRow>();
  const push = (r: Omit<OutlineRow, "hasChildren">) => { const row = { ...r, hasChildren: false }; rows.push(row); byId.set(r.id, row); const parent = r.parent ? byId.get(r.parent) : undefined; if (parent) parent.hasChildren = true; };
  const g = a.graph as { name?: string; ontology?: string; place?: Place; settings?: { name: string; place: Place }[] } | undefined;
  if (g?.place) {
    push({ id: "graph", parent: null, level: 1, kind: "graph", label: `${g.name ?? ""} : ${g.ontology ?? ""}`, place: g.place, item: null });
    (g.settings ?? []).forEach((s, k) => push({ id: `graph.settings.${k}`, parent: "graph", level: 2, kind: "setting", label: s.name, place: s.place, item: itemOf("graph.settings", s.place) }));
  }
  ((a.inputs as { name: string; kind: string; place: Place }[] | undefined) ?? []).forEach((x, k) => push({ id: `inputs.${k}`, parent: null, level: 1, kind: "input", label: `${x.name}: ${x.kind}`, place: x.place, item: itemOf("statements", x.place) }));
  const params = a.parameters as { place?: Place; items?: { name: string; place: Place }[] } | null | undefined;
  if (params?.place) {
    push({ id: "parameters", parent: null, level: 1, kind: "parameters", label: "", place: params.place, item: itemOf("statements", params.place) });
    (params.items ?? []).forEach((p, k) => push({ id: `parameters.${k}`, parent: "parameters", level: 2, kind: "parameter", label: p.name, place: p.place, item: itemOf("parameters", p.place) }));
  }
  ((a.functions as { name: string; place: Place; parameters?: { name: string; place: Place }[] }[] | undefined) ?? []).forEach((f, k) => {
    push({ id: `functions.${k}`, parent: null, level: 1, kind: "function", label: f.name, place: f.place, item: itemOf("statements", f.place) });
    (f.parameters ?? []).forEach((p, m) => push({ id: `functions.${k}.parameters.${m}`, parent: `functions.${k}`, level: 2, kind: "parameter", label: p.name, place: p.place, item: itemOf(`functions.${k}.parameters`, p.place) }));
  });
  const pipeline = (listId: string, parent: string, level: number, ops: Op[]) => ops.forEach((o, j) => {
    const id = `${listId}.${j}`;
    push({ id, parent, level, kind: o.kind, label: "", place: o.place, item: itemOf(listId, o.place) });
    for (const [name, v] of Object.entries(o.parts ?? {})) {
      partPlaces(v).forEach((p, m) => push({ id: `${id}.${name}.${m}`, parent: id, level: level + 1, kind: name, label: "", place: p, item: itemOf(`${id}.${name}`, p) }));
    }
    (o.inner ?? []).forEach((inner, m) => pipeline(`${id}.inner.${m}`, id, level + 1, inner));
  });
  ((a.statements as Stmt[] | undefined) ?? []).forEach((s, i) => {
    const id = `statements.${i}`;
    push({ id, parent: null, level: 1, kind: s.kind, label: s.name ?? s.type ?? "", place: s.place, item: itemOf("statements", s.place) });
    (s.settings ?? []).forEach((x, k) => push({ id: `${id}.settings.${k}`, parent: id, level: 2, kind: "setting", label: x.name, place: x.place, item: itemOf(`${id}.settings`, x.place) }));
    pipeline(`${id}.operators`, id, 2, s.operators ?? []);
    if (lists.has(`${id}.operators`)) push({ id: `${id}.add`, parent: id, level: 2, kind: "add", label: "", place: [s.place[1], s.place[1]], item: null, adds: i });
  });
  return rows;
}
