// Color by property for nodes and for edges. With a presentation hint the values map to semantic roles (ok, warning, danger, unknown),
// otherwise the distinct values get categorical colors. Pure: the pane, the legend and the element list all use it.
import { valueText } from "../core/model";
import type { GraphValue, OntologyDefinition } from "../core/wire-types";
import { ROLES, type PresentationHint, type Role } from "../source/presentation";

export type Target = "node" | "edge";
export const PALETTE = ["#4ea1ff", "#ff9b3d", "#3ecf8e", "#e85d75", "#b388ff", "#f2d03b", "#2ec4c4", "#c58b5a", "#8fa1b3", "#ff7ad9"];
export const OTHER = "#6b7785";
export const MISSING = "#66758a";
export const MAX_VALUES = PALETTE.length;

/** Glyph and word of a role: the cues that do not depend on color. */
export const ROLE_GLYPH: Record<Role, string> = { ok: "✓", warning: "⚠", danger: "✖", unknown: "?", neutral: "○" };
export const ROLE_WORD: Record<Role, string> = { ok: "ok", warning: "warning", danger: "danger", unknown: "unknown", neutral: "neutral" };

export interface LegendEntry { value: string; color: string; count: number; role?: Role }

/** "@default" colors by the ontology's hint when it has one for this target, "" turns coloring off, anything else is a property id. */
export const DEFAULT_COLORING = "@default";
export interface Coloring {
  property: string;
  hint: PresentationHint | null;
  /** the default over an ontology that hints several properties of this target: an element takes the worst role among the hinted properties it has */
  hints?: PresentationHint[];
}
/** The last segment of a property id (`acme.health.state` is `state`). */
const shortName = (property: string): string => property.slice(property.lastIndexOf(".") + 1);
export const coloringLabel = (c: Coloring): string => (c.hints ? c.hints.map((h) => shortName(h.property)).join(", ") : c.property);
const SEVERITY: Role[] = ["neutral", "ok", "unknown", "warning", "danger"];

/** What an element's coloring says: the text of the value (with several hinted properties, `property: value`) and its role. */
export interface ColorValue { text: string; role?: Role }
export function colorValueOf(c: Coloring, props: Record<string, GraphValue>): ColorValue | undefined {
  if (!c.hints) {
    const v = props[c.property];
    if (!v) return undefined;
    const text = valueText(v);
    return { text, role: c.hint ? (c.hint.roles[text] ?? "neutral") : undefined };
  }
  let best: ColorValue | undefined;
  for (const h of c.hints) {
    const v = props[h.property];
    if (!v) continue;
    const t = valueText(v), role = h.roles[t] ?? "neutral";
    if (!best || SEVERITY.indexOf(role) > SEVERITY.indexOf(best.role!)) best = { text: `${shortName(h.property)}: ${t}`, role };
  }
  return best;
}
/** Counts per text over elements, how many have none, and the hint that maps the texts to roles (the property's own, or one built from the roles the elements took). */
export function tally(c: Coloring, items: Iterable<{ props: Record<string, GraphValue> }>): { counts: Map<string, number>; missing: number; hint: PresentationHint | null } {
  const counts = new Map<string, number>(), roles: Record<string, Role> = {};
  let missing = 0;
  for (const e of items) {
    const v = colorValueOf(c, e.props);
    if (!v) { missing++; continue; }
    counts.set(v.text, (counts.get(v.text) ?? 0) + 1);
    if (c.hints && v.role) roles[v.text] = v.role;
  }
  return { counts, missing, hint: c.hints ? { property: c.property, roles } : c.hint };
}

export function propertiesOf(ontology: OntologyDefinition, target: Target): Set<string> {
  const types = target === "node" ? ontology.entityTypes : ontology.relationshipTypes;
  return new Set(types.flatMap((t) => t.properties.map((p) => p.value)));
}

export function resolveColoring(info: { ontology: OntologyDefinition; presentation?: PresentationHint[] }, target: Target, setting: string): Coloring | null {
  if (setting === "") return null;
  const hints = info.presentation ?? [];
  if (setting === DEFAULT_COLORING) {
    const props = propertiesOf(info.ontology, target);
    const own = hints.filter((x) => props.has(x.property));
    return own.length ? { property: own[0].property, hint: own[0], ...(own.length > 1 ? { hints: own } : {}) } : null;
  }
  return { property: setting, hint: hints.find((x) => x.property === setting) ?? null };
}

export function legendOf(counts: Map<string, number>, missing = 0, hint: PresentationHint | null = null, roleColor?: Record<Role, string>): LegendEntry[] {
  const values = [...counts.keys()];
  const alpha = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true });
  let out: LegendEntry[];
  if (hint && roleColor) {
    const role = (v: string): Role => hint.roles[v] ?? "neutral";
    values.sort((a, b) => ROLES.indexOf(role(a)) - ROLES.indexOf(role(b)) || alpha(a, b));
    out = values.map((value) => ({ value, color: roleColor[role(value)], count: counts.get(value)!, role: role(value) }));
  } else {
    values.sort(alpha);
    out = values.slice(0, MAX_VALUES).map((value, i) => ({ value, color: PALETTE[i], count: counts.get(value)! }));
    const other = values.slice(MAX_VALUES).reduce((n, v) => n + counts.get(v)!, 0);
    if (other) out.push({ value: "(other values)", color: OTHER, count: other });
  }
  if (missing) out.push({ value: "(no value)", color: hint && roleColor ? roleColor.unknown : MISSING, count: missing, role: hint ? "unknown" : undefined });
  return out;
}

export function entryOf(legend: LegendEntry[], value: string | undefined): LegendEntry | undefined {
  if (value === undefined) return legend.find((l) => l.value === "(no value)");
  return legend.find((l) => l.value === value) ?? legend.find((l) => l.value === "(other values)");
}
export const colorOf = (legend: LegendEntry[], value: string | undefined): string => entryOf(legend, value)?.color ?? (value === undefined ? MISSING : OTHER);
