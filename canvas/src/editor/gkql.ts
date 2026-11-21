export interface GkqlVerb {
  id: string;
  verb: string;
  graph?: string;
  source?: string;
  pattern?: GkqlDrawing;
  target?: GkqlDrawing;
  [key: string]: unknown;
}

export interface GkqlDrawing {
  nodes?: { var: string; label: string; [key: string]: unknown }[];
  edges?: { var: string; label: string; from: string; to: string; [key: string]: unknown }[];
}

export interface GkqlWire { from: string; to: string; port?: string }

export interface GkqlProgram {
  graph: string;
  ontology: string;
  inputs: { name: string; ontology?: string; rows?: string; [key: string]: unknown }[];
  parameters: { name: string; kind: string; default?: unknown }[];
  verbs: GkqlVerb[];
  wires: GkqlWire[];
  [key: string]: unknown;
}

const record = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const strings = (value: unknown, ...keys: string[]) => record(value) && keys.every((key) => typeof value[key] === "string");
const items = (value: unknown, valid: (item: unknown) => boolean) => Array.isArray(value) && value.every(valid);
const drawing = (value: unknown) => value == null || record(value)
  && (value.nodes == null || items(value.nodes, (node) => strings(node, "var", "label")))
  && (value.edges == null || items(value.edges, (edge) => strings(edge, "var", "label", "from", "to")));

const rawJson = (JSON as typeof JSON & { rawJSON?: (text: string) => object }).rawJSON;

/** GKQL distinguishes integers from decimals and can represent integers larger than Number.MAX_SAFE_INTEGER. */
export const readGkqlJson = (text: string): unknown => JSON.parse(text, (_, value, context?: { source?: string }) => {
  const spelling = context?.source;
  return typeof value === "number" && spelling && rawJson && spelling !== JSON.stringify(value) ? rawJson(spelling) : value;
});

export function canEditGkqlStructure(text: string): boolean {
  if (rawJson) return true;
  const tokens = text.matchAll(/"(?:[^"\\]|\\.)*"|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/gs);
  return [...tokens].every(([, number]) => number === undefined || number === JSON.stringify(Number(number)));
}

/** Check the parts used by the editor. The engine validates the executable semantics on store. */
export function readGkql(text: string): GkqlProgram {
  const value = readGkqlJson(text);
  if (!record(value) || !strings(value, "graph", "ontology")
    || !items(value.inputs, (input) => strings(input, "name"))
    || !items(value.parameters, (parameter) => strings(parameter, "name", "kind"))
    || !items(value.verbs, (verb) => record(verb) && strings(verb, "id", "verb") && drawing(verb.pattern) && drawing(verb.target))
    || !items(value.wires, (wire) => record(wire) && strings(wire, "from", "to") && (wire.port == null || typeof wire.port === "string"))) {
    throw new Error("Expected a GKQL program with inputs, parameters, operations and wires.");
  }
  return value as GkqlProgram;
}

export const printGkql = (program: GkqlProgram) => JSON.stringify(program, null, 2) + "\n";
export const wirePort = (wire: GkqlWire) => wire.port ?? "in";

const starters: Readonly<Partial<Record<string, Omit<GkqlVerb, "id">>>> = {
  "graph-match": { verb: "graph-match", pattern: { nodes: [] } },
  rows: { verb: "rows", source: "" },
  where: { verb: "where", condition: { lit: true } },
  project: { verb: "project", columns: {} },
  extend: { verb: "extend", columns: {} },
  union: { verb: "union" },
  summarize: { verb: "summarize", aggregates: { count: { fn: "count", args: [] } } },
  join: { verb: "join", kind: "inner", on: [] },
  window: { verb: "window", window: { time: "time", period: "60s" } },
  series: { verb: "series", graph: "", series: { element: "", property: "", lookback: "1h" } },
  hysteresis: { verb: "hysteresis", hysteresis: { by: [], value: "value", above: 2, enterAfter: 2, below: 0, exitAfter: 2, high: "on", low: "off", initial: "off" } },
  nodes: { verb: "nodes", target: { nodes: [] } },
  edges: { verb: "edges", target: { nodes: [], edges: [] } },
};

export const authorableVerbs = Object.keys(starters);
const sourceVerbs = new Set(["graph-match", "rows"]);
const outputVerbs = new Set(["nodes", "edges"]);

export function verbStyle(verb: string): "input" | "transform" | "aggregate" | "output" {
  if (sourceVerbs.has(verb)) return "input";
  if (outputVerbs.has(verb)) return "output";
  if (["summarize", "join", "union", "hysteresis", "window", "series"].includes(verb)) return "aggregate";
  return "transform";
}

export function appendVerb(program: GkqlProgram, verb: string): GkqlProgram {
  const starter = starters[verb];
  if (!starter) return program;
  const prefix = verb.replaceAll("-", "_");
  const used = new Set(program.verbs.map((node) => node.id));
  let id = prefix;
  for (let index = 2; used.has(id); ++index) id = `${prefix}${index}`;
  const source = verb === "graph-match" || verb === "series"
    ? { graph: program.inputs.find((input) => input.ontology)?.name ?? "" }
    : verb === "rows" ? { source: program.inputs.find((input) => input.rows)?.name ?? "" } : {};
  return { ...program, verbs: [...program.verbs, { ...starter, ...source, id } as GkqlVerb] };
}

export function removeVerb(program: GkqlProgram, id: string): GkqlProgram {
  return { ...program, verbs: program.verbs.filter((verb) => verb.id !== id), wires: program.wires.filter((wire) => wire.from !== id && wire.to !== id) };
}

export function rewire(program: GkqlProgram, to: string, port: string, from: string, previous?: GkqlWire): GkqlProgram {
  if (!from) return { ...program, wires: program.wires.filter((wire) => wire !== previous) };
  if (program.wires.some((wire) => wire.from === from && wire.to === to && wirePort(wire) === port)) return program;
  const wire = port === "in" ? { from, to } : { from, to, port };
  return { ...program, wires: previous ? program.wires.map((item) => item === previous ? wire : item) : [...program.wires, wire] };
}

/** Valid source candidates exclude the destination, sinks and downstream operations. */
export function sourcesFor(program: GkqlProgram, to: string): GkqlVerb[] {
  const downstream = new Set([to]);
  const queue = [to];
  for (const id of queue) {
    for (const wire of program.wires) if (wire.from === id && !downstream.has(wire.to)) {
      downstream.add(wire.to);
      queue.push(wire.to);
    }
  }
  return program.verbs.filter((node) => !downstream.has(node.id) && !outputVerbs.has(node.verb));
}

export const portsFor = (verb: string) => sourceVerbs.has(verb) ? [] : verb === "join" ? ["left", "right"] : ["in"];

export const VERB_WIDTH = 150;
const VERB_HEIGHT = 58;
type Card = { verb: GkqlVerb; x: number; y: number };

/** Lay out input-to-output. Unchecked drafts with cycles still give each operation a position. */
export function layoutGkql(program: GkqlProgram) {
  const parents = new Map(program.verbs.map((verb) => [verb.id, [] as string[]]));
  for (const wire of program.wires) if (parents.has(wire.from)) parents.get(wire.to)?.push(wire.from);
  const depths = new Map<string, number>();
  const pending = new Set(parents.keys());
  for (let progressed = true; progressed && pending.size > 0;) {
    progressed = false;
    for (const id of pending) {
      const inputs = parents.get(id) ?? [];
      if (inputs.some((parent) => !depths.has(parent))) continue;
      depths.set(id, Math.max(-1, ...inputs.map((parent) => depths.get(parent)!)) + 1);
      pending.delete(id);
      progressed = true;
    }
  }
  const byId = new Map<string, Card>();
  const levels = new Map<number, number>();
  const last = Math.max(0, ...depths.values());
  const cards = program.verbs.map((verb) => {
    const column = depths.get(verb.id) ?? last + 1;
    const row = levels.get(column) ?? 0;
    levels.set(column, row + 1);
    const card = { verb, x: 12 + column * 208, y: 12 + row * 82 };
    byId.set(verb.id, card);
    return card;
  });
  const wires = program.wires.flatMap((wire) => {
    const from = byId.get(wire.from), to = byId.get(wire.to);
    if (!from || !to) return [];
    const a = from.x + VERB_WIDTH, b = to.x - 5;
    const ay = from.y + VERB_HEIGHT / 2;
    const by = to.y + VERB_HEIGHT / 2 + (wire.port === "left" ? -8 : wire.port === "right" ? 8 : 0);
    const mid = (a + b) / 2;
    return [{ wire, d: `M${a},${ay} C${mid},${ay} ${mid},${by} ${b},${by}` }];
  });
  return {
    cards, wires,
    width: Math.max(184, ...cards.map((card) => card.x + VERB_WIDTH + 12)),
    height: Math.max(92, ...cards.map((card) => card.y + VERB_HEIGHT + 12)),
  };
}
