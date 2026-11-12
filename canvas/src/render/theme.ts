// Semantic colors. Role hues are the ones the explanatory site uses (brainstorm/tools/story.py, brainstorm/dist/atlas-next.css):
// input amber, master teal, projection blue, rule purple, identity pink, document lime, diff coral.
// Color never carries meaning alone: shapes, ring patterns, glyphs and labels repeat every distinction.
import { Shape, parseColor } from "./scene";

export interface Theme {
  name: "dark" | "light";
  bg: string; bgRgb: [number, number, number];
  panel: string; text: string; muted: string; line: string;
  selected: [number, number, number];
  added: [number, number, number]; removed: [number, number, number]; changed: [number, number, number];
  identity: [number, number, number]; source: [number, number, number];
  edge: string;
  hue: Record<string, string>;
}

const HUES_DARK = { input: "#ffcf7c", master: "#83ead1", projection: "#8ec8ff", rule: "#c2a5ff", identity: "#f3a8d4", document: "#d7e68f", diff: "#ff9b9e" };
const rgb01 = (hex: string): [number, number, number] => {
  const h = hex.replace("#", "");
  return [parseInt(h.slice(0, 2), 16) / 255, parseInt(h.slice(2, 4), 16) / 255, parseInt(h.slice(4, 6), 16) / 255];
};
export function shade(hex: string, factor: number): string {
  const h = hex.replace("#", "");
  const c = [0, 2, 4].map((i) => Math.max(0, Math.min(255, Math.round(parseInt(h.slice(i, i + 2), 16) * factor))));
  return `#${c.map((x) => x.toString(16).padStart(2, "0")).join("")}`;
}

export const darkTheme: Theme = {
  name: "dark", bg: "#11181f", bgRgb: rgb01("#11181f"), panel: "#18222c", text: "#e8eef4", muted: "#9aa9b8", line: "#2a3a48",
  selected: [1, 1, 1], added: rgb01("#83ead1"), removed: rgb01("#ff9b9e"), changed: rgb01("#ffcf7c"), identity: rgb01("#f3a8d4"), source: rgb01("#8ec8ff"),
  edge: "#7f93a6", hue: HUES_DARK,
};
export const lightTheme: Theme = {
  name: "light", bg: "#f4f6f8", bgRgb: rgb01("#f4f6f8"), panel: "#ffffff", text: "#16212b", muted: "#52626f", line: "#cfd8e0",
  selected: rgb01("#0b1a26"), added: rgb01("#0d8a6c"), removed: rgb01("#c43d45"), changed: rgb01("#b87a00"), identity: rgb01("#b83d8a"), source: rgb01("#1a63a6"),
  edge: "#7c8c9a", hue: Object.fromEntries(Object.entries(HUES_DARK).map(([k, v]) => [k, shade(v, 0.62)])),
};

// categorical palette for entity types (coral is reserved for removal and errors)
const PALETTE = ["projection", "master", "rule", "identity", "document", "input"] as const;
const SHAPES = [Shape.Circle, Shape.Square, Shape.Diamond, Shape.Triangle, Shape.Hexagon] as const;
export const SHAPE_NAME: Record<number, string> = { [Shape.Circle]: "circle", [Shape.Square]: "square", [Shape.Diamond]: "diamond", [Shape.Triangle]: "triangle", [Shape.Hexagon]: "hexagon", [Shape.Card]: "card" };

const KNOWN: Record<string, [number, number, number]> = {
  // type: [palette index, shape index, radius]
  Subscription: [2, 4, 14], Cluster: [1, 4, 11], Node: [0, 1, 6], Namespace: [4, 1, 8], Workload: [3, 2, 6.5], Service: [5, 0, 6], Pod: [0, 0, 3.6],
  Deployment: [3, 2, 7], AzureResource: [1, 4, 9], NetworkEndpoint: [5, 3, 6], Capability: [4, 4, 9],
  EntityType: [0, 4, 9], Property: [5, 0, 5],
};
function hash(s: string): number { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }

export interface TypeStyle { hue: string; shape: number; radius: number; color: number; shapeName: string }
export function styleForType(type: string, theme: Theme): TypeStyle {
  const k = KNOWN[type];
  const h = hash(type);
  const pi = k ? k[0] : h % PALETTE.length;
  const si = k ? k[1] : (h >>> 5) % SHAPES.length;
  const hue = theme.hue[PALETTE[pi]];
  const shape = SHAPES[si];
  return { hue, shape, radius: k ? k[2] : 5, color: parseColor(hue), shapeName: SHAPE_NAME[shape] };
}

/** Relationship color: muted neutral, with a small stable hue shift per type so parallel types stay distinguishable. */
/** The color of an edge of a declared kind (engine 3.17). It repeats what the line pattern and the head say; it never says it alone. */
export const KIND_EDGE_COLOR: Record<"composition" | "dependency" | "communication" | "support", string> = {
  composition: "#8ea4c4", dependency: "#e3a33b", communication: "#3fb5c9", support: "#8b949e",
};

export function edgeColorForType(type: string, theme: Theme): number {
  const h = hash(type) % 3;
  const base = theme.edge;
  return parseColor(h === 0 ? base : shade(base, h === 1 ? 0.85 : 1.15));
}

export const OPERATOR_ROLE: Record<string, keyof typeof HUES_DARK> = {
  input: "input", match: "input", filter: "rule", map: "rule", join: "rule", union: "rule", distinct: "rule", group: "rule", traverse: "rule",
  window: "rule", fixpoint: "rule", emitEntity: "identity", emitRelationship: "identity",
};
