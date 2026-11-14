// A projection's KQL text in the editor: the engine's answers (PROTOCOL 3.41, 3.42) as data, the calculations that turn them into what the
// text editor shows, and the highlighter. Places are [start, end) in UTF-16 units, the units of a JS string and of CodeMirror's positions.
import { StreamLanguage, type StringStream } from "@codemirror/language";

export type Place = [number, number];
export type Depth = "fast" | "full";
export interface KqlText { digest: string; text: string }
export interface KqlDiagnostic { start: number; end: number; line: number; column: number; severity: "error" | "warning"; source: string; code: string; message: string }
/** The analyse answer. The outline (graph, inputs, statements, ...) is kept as it comes; the editing module of stage b reads it. */
export interface Analysis { digest: string; depth: Depth; diagnostics: KqlDiagnostic[]; [outline: string]: unknown }
export interface CompletionItem { text: string; kind: string; insert?: string; source: string }
export interface Completion { digest: string; replace: Place; items: CompletionItem[] }
export interface Hover { digest: string; place: Place | null; kusto: string | null; ontology: Record<string, unknown> | null }
export interface Related { digest: string; places: Place[]; current: Place | null }

/** The digest the engine puts on its answers: SHA-256 of the UTF-8 bytes, `sha256:` and lower-case hex. */
export async function digestOf(text: string): Promise<string> {
  const hash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return `sha256:${Array.from(new Uint8Array(hash), (b) => b.toString(16).padStart(2, "0")).join("")}`;
}

/** Lint diagnostics of an analysis, for the text it was asked on: `source` says who found it (kusto, profile, engine) with its code. */
export const diagnosticsOf = (a: Analysis) =>
  a.diagnostics.map((d) => ({ from: d.start, to: d.end, severity: d.severity, source: `${d.source} ${d.code}`, message: d.message }));

/** The icon of a completion item: Kusto's kinds and the ontology's, to CodeMirror's types. */
export function completionType(kind: string): string {
  const k = kind.toLowerCase();
  if (k.includes("column") || k.includes("property")) return "property";
  if (k.includes("function")) return "function";
  if (k.includes("parameter") || k.includes("variable") || k.includes("let")) return "variable";
  if (k.includes("table") || k.includes("type") || k.includes("graph") || k.includes("domain")) return "type";
  if (k.includes("keyword") || k.includes("prefix") || k.includes("step") || k.includes("operator") || k.includes("setting")) return "keyword";
  return "text";
}

/** CodeMirror's completion result for an answer: `replace` is the stretch an item replaces; Kusto's items carry `insert`, the others their text. */
export const completionOf = (c: Completion) => ({
  from: c.replace[0], to: c.replace[1],
  options: c.items.map((i) => ({ label: i.text, apply: i.insert ?? i.text, type: completionType(i.kind), detail: i.source === "ontology" ? "ontology" : undefined })),
});

/** The lines a hover shows: Kusto's description (a column with its type), then the ontology's (a type's description; a property's kind, unit, role). */
export function hoverLines(h: Hover): string[] {
  const out: string[] = [];
  if (h.kusto) out.push(h.kusto);
  const o = h.ontology;
  if (o) {
    if (typeof o.type === "string") out.push(`${o.relationship ? "relationship" : "type"} ${o.type}`);
    if (typeof o.property === "string") out.push([`property ${o.property}`, o.kind, o.unit && `unit ${o.unit}`, o.role && `role ${o.role}`, o.telemetry ? "telemetry" : null].filter(Boolean).join(" · "));
    if (typeof o.description === "string" && o.description) out.push(o.description);
  }
  return out;
}

// ---- highlighting: KQL and our additions, no engine call ----
const KEYWORDS = new Set([
  "let", "where", "project", "project-away", "project-keep", "project-rename", "project-reorder", "extend", "summarize", "by", "join", "kind", "on",
  "union", "distinct", "invoke", "datatable", "as", "and", "or", "not", "in", "has", "has_any", "has_all", "contains", "startswith", "endswith",
  "between", "true", "false", "null", "graph-match", "graph-to-table", "make-graph", "lookup", "count", "declare", "query_parameters", "inner",
  "leftouter", "rightouter", "fullouter", "leftanti", "rightanti", "leftsemi", "rightsemi", "innerunique", "with", "cycles", "nodes", "edges",
]);
/** Our additions to KQL: the graph line, inputs and the step forms. */
const ADDITIONS = new Set(["graph", "input", "nodes", "edges", "link", "from", "to", "with"]);

interface HighlightState { comment: boolean }
export const kqlLanguage = StreamLanguage.define<HighlightState>({
  name: "kql",
  startState: () => ({ comment: false }),
  token(stream: StringStream): string | null {
    if (stream.eatSpace()) return null;
    if (stream.match("//")) { stream.skipToEnd(); return "comment"; }
    const q = stream.peek();
    if (q === '"' || q === "'") {
      stream.next();
      let escaped = false;
      for (let c = stream.next(); c !== undefined; c = stream.next()) { if (c === q && !escaped) break; escaped = !escaped && c === "\\"; }
      return "string";
    }
    if (stream.match(/^\d+(\.\d+)?(ms|s|m|h|d)?\b/)) return "number";
    if (stream.match(/^[A-Za-z_][\w-]*/)) {
      const w = stream.current();
      // `graph(...)` reads a graph, `graph x : y` declares one: the call is Kusto's, the statement ours
      if (w === "graph" && stream.match(/^\s*\(/, false)) return "keyword";
      return ADDITIONS.has(w) && stream.column() === stream.indentation() ? "macroName" : KEYWORDS.has(w) ? "keyword" : "variableName";
    }
    if (stream.match(/^[|=<>!~+\-*/%:;,.()[\]{}]/)) return "operator";
    stream.next();
    return null;
  },
  languageData: { commentTokens: { line: "//" } },
});
