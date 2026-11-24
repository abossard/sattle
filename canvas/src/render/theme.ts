// The renderer's half of the Drawing Sheet (DESIGN.md): paper and ink, drafting blue for the selection and what follows from it
// (the same element in other graphs, the live telemetry flow), ink for a revision's changes (the ring pattern says which),
// Fluent's status colors for errors and warnings, and muted tints for the categories (types, roles, edge kinds), none of them blue.
// Color never carries meaning alone: shapes, ring patterns, glyphs and labels repeat every distinction.
import { webDarkTheme, webLightTheme } from "@fluentui/react-components";
import { Shape, parseColor } from "./scene";

type Rgb = [number, number, number];
export interface Theme {
  name: "dark" | "light";
  bg: string; bgRgb: Rgb;
  panel: string; text: string; muted: string; line: string;
  /** drafting blue: selection, hover, matches, the selection's identity in other graphs, the live flow */
  selected: Rgb;
  /** ink: an element added, changed or removed in the highlighted revision */
  change: Rgb;
  danger: Rgb; warning: Rgb;
  edge: string;
  /** muted categorical tints by role, also the type palette */
  hue: Record<string, string>;
}

// OKLCH tints at one lightness and low chroma (light L 0.55 C 0.08, dark L 0.78 C 0.075): they sort categories without reading
// as a state. Hues: projection teal 195, master violet 300, rule clay 55, identity mauve 350, document olive 110, input sage 160,
// diff neutral (a change is ink).
const HUES_LIGHT = { input: "#448061", master: "#79679a", projection: "#2a8080", rule: "#966543", identity: "#955e78", document: "#74763c", diff: "#717171" };
const HUES_DARK = { input: "#8dc7a7", master: "#beade1", projection: "#7bc7c6", rule: "#deab8a", identity: "#dda4be", document: "#babc84", diff: "#b7b7b7" };
const rgb01 = (hex: string): Rgb => {
  const h = hex.replace("#", "");
  return [parseInt(h.slice(0, 2), 16) / 255, parseInt(h.slice(2, 4), 16) / 255, parseInt(h.slice(4, 6), 16) / 255];
};
export function shade(hex: string, factor: number): string {
  const h = hex.replace("#", "");
  const c = [0, 2, 4].map((i) => Math.max(0, Math.min(255, Math.round(parseInt(h.slice(i, i + 2), 16) * factor))));
  return `#${c.map((x) => x.toString(16).padStart(2, "0")).join("")}`;
}

export const darkTheme: Theme = {
  name: "dark", bg: "#121316", bgRgb: rgb01("#121316"), panel: "#1b1d20", text: "#e6e7e9", muted: "#a4a7ad", line: "#3a3d42",
  selected: rgb01("#8fb0d4"), change: rgb01("#e6e7e9"),
  danger: rgb01(webDarkTheme.colorStatusDangerForeground1), warning: rgb01(webDarkTheme.colorStatusWarningForeground1),
  edge: "#80838a", hue: HUES_DARK,
};
export const lightTheme: Theme = {
  name: "light", bg: "#eceef1", bgRgb: rgb01("#eceef1"), panel: "#ffffff", text: "#202124", muted: "#5f6268", line: "#c9cbd0",
  selected: rgb01("#1d5fa6"), change: rgb01("#202124"),
  danger: rgb01(webLightTheme.colorStatusDangerForeground1), warning: rgb01(webLightTheme.colorStatusWarningForeground1),
  edge: "#868990", hue: HUES_LIGHT,
};

// categorical palette for entity types (the diff tint stays out: a neutral type would read as a change)
const PALETTE = ["projection", "master", "rule", "identity", "document", "input"] as const;
const SHAPES = [Shape.Circle, Shape.Square, Shape.Diamond, Shape.Triangle, Shape.Hexagon] as const;
export const SHAPE_NAME: Record<number, string> = { [Shape.Circle]: "circle", [Shape.Square]: "square", [Shape.Diamond]: "diamond", [Shape.Triangle]: "triangle", [Shape.Hexagon]: "hexagon", [Shape.Card]: "card" };

const KNOWN: Record<string, [number, number, number]> = {
  // type: [palette index, shape index, radius]
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
  // OKLCH, readable on both floors: composition, the structure, in a neutral (L 0.58); dependency clay and communication teal
  // (L 0.66 C 0.07); support a lighter neutral (L 0.66), drawn thin and faint
  composition: "#7a7a7a", dependency: "#b48769", communication: "#5ba09f", support: "#929292",
};

/** Cached per base color and type: the function runs for every edge on every refresh, and its answer depends on nothing else. */
const edgeColors = new Map<string, number>();
export function edgeColorForType(type: string, theme: Theme): number {
  const key = `${theme.edge}|${type}`;
  let c = edgeColors.get(key);
  if (c === undefined) {
    const h = hash(type) % 3;
    c = parseColor(h === 0 ? theme.edge : shade(theme.edge, h === 1 ? 0.85 : 1.15));
    edgeColors.set(key, c);
  }
  return c;
}

export const OPERATOR_ROLE: Record<string, keyof typeof HUES_DARK> = {
  input: "input", match: "input", filter: "rule", map: "rule", join: "rule", union: "rule", distinct: "rule", group: "rule", traverse: "rule",
  window: "rule", emitEntity: "identity", emitRelationship: "identity",
};
