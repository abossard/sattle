// Expressions as text: a small parser for the expression syntax of `.sattle` (paths, literals, calls, infix) and the catalog
// subtree it becomes (FieldRef, Literal, Call with `function` and ordered `arg` children). The engine owns the real parser;
// this one lets the editor build the subtrees of a slot without a round trip, and the engine checks the result.
import type { Edit } from "../core/model";
import { contains, ent, rel, text } from "./patches";

export type Expr =
  | { k: "field"; path: string }
  | { k: "lit"; kind: "text" | "integer" | "decimal" | "boolean" | "null"; value: string }
  | { k: "call"; fn: string; args: Expr[] }
  /** `id domain(component: expr, ...)`: an identity of an identity domain */
  | { k: "idof"; domain: string; components: { name: string; expr: Expr }[] };

const INFIX: [string, string, number][] = [
  ["||", "Or", 1], ["&&", "And", 2], ["==", "Equal", 3], ["!=", "NotEqual", 3], ["<=", "LessOrEqual", 4], [">=", "GreaterOrEqual", 4], ["<", "LessThan", 4], [">", "GreaterThan", 4],
  ["++", "Concat", 5], ["+", "Add", 5], ["-", "Subtract", 5], ["*", "Multiply", 6], ["/", "Divide", 6],
];

const NS_PER: Record<string, bigint> = { ms: 1_000_000n, s: 1_000_000_000n, m: 60_000_000_000n, h: 3_600_000_000_000n };

export function parseExpr(src: string): { ok: true; expr: Expr } | { ok: false; error: string } {
  let i = 0;
  const ws = () => { while (i < src.length && /\s/.test(src[i])) i++; };
  const fail = (m: string): never => { throw new Error(`${m} at ${i + 1}`); };
  function primary(): Expr {
    ws();
    const c = src[i];
    if (c === "(") { i++; const e = binary(1); ws(); if (src[i] !== ")") fail("expected )"); i++; return e; }
    if (c === "!") { i++; return { k: "call", fn: "Not", args: [primary()] }; }
    if (/^not\b/.test(src.slice(i))) { i += 3; return { k: "call", fn: "Not", args: [binary(3)] }; }
    if (c === "-" && /[\d.]/.test(src[i + 1] ?? "")) { i++; const n = primary(); if (n.k === "lit" && (n.kind === "integer" || n.kind === "decimal")) return { ...n, value: `-${n.value}` }; return fail("expected a number after -"); }
    if (c === "-") { i++; return { k: "call", fn: "Negate", args: [primary()] }; }
    if (c === '"') {
      let s = ""; i++;
      while (i < src.length && src[i] !== '"') { if (src[i] === "\\") i++; s += src[i++]; }
      if (src[i] !== '"') fail("unterminated string");
      i++; return { k: "lit", kind: "text", value: s };
    }
    // a duration literal is the integer of its nanoseconds, as the engine reads it (5m, 24h, 250ms, 30s)
    const dur = /^(\d+)(ms|s|m|h)(?![\w.])/.exec(src.slice(i));
    if (dur) { i += dur[0].length; return { k: "lit", kind: "integer", value: String(BigInt(dur[1]) * NS_PER[dur[2]]) }; }
    const num = /^\d+(\.\d+)?/.exec(src.slice(i));
    if (num) { i += num[0].length; return { k: "lit", kind: num[1] ? "decimal" : "integer", value: num[0] }; }
    const id = /^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*/.exec(src.slice(i));
    if (!id) return fail(c === undefined ? "unexpected end" : `unexpected ${c}`);
    i += id[0].length;
    const idof = id[0] === "id" ? /^\s+([A-Za-z_][\w.]*)\s*\(/.exec(src.slice(i)) : null;
    if (idof) {
      i += idof[0].length;
      const components: { name: string; expr: Expr }[] = [];
      ws();
      if (src[i] === ")") { i++; return { k: "idof", domain: idof[1], components }; }
      for (;;) {
        ws();
        const name = /^[A-Za-z_]\w*/.exec(src.slice(i));
        if (!name) fail("expected a component name");
        i += name![0].length; ws();
        if (src[i] !== ":" && !(src[i] === "=" && src[i + 1] !== "=")) fail("expected : after the component name");
        i++;
        components.push({ name: name![0], expr: binary(1) }); ws();
        if (src[i] === ",") { i++; continue; }
        if (src[i] === ")") { i++; break; }
        fail("expected , or )");
      }
      return { k: "idof", domain: idof[1], components };
    }
    if (id[0] === "true" || id[0] === "false") return { k: "lit", kind: "boolean", value: id[0] };
    if (id[0] === "null") return { k: "lit", kind: "null", value: "" };
    ws();
    if (src[i] === "(" && /^\(\s*[A-Za-z_]\w*\s*:(?!:)/.test(src.slice(i))) {
      // `domain(component: expr, ...)` without the `id` keyword, as the engine prints identities in emit statements
      i++;
      const components: { name: string; expr: Expr }[] = [];
      for (;;) {
        ws();
        const name = /^[A-Za-z_]\w*/.exec(src.slice(i));
        if (!name) fail("expected a component name");
        i += name![0].length; ws();
        if (src[i] !== ":") fail("expected : after the component name");
        i++;
        components.push({ name: name![0], expr: binary(1) }); ws();
        if (src[i] === ",") { i++; continue; }
        if (src[i] === ")") { i++; break; }
        fail("expected , or )");
      }
      return { k: "idof", domain: id[0], components };
    }
    if (src[i] === "(") {
      i++; const args: Expr[] = []; ws();
      if (src[i] === ")") { i++; return { k: "call", fn: id[0], args }; }
      for (;;) { args.push(binary(1)); ws(); if (src[i] === ",") { i++; continue; } if (src[i] === ")") { i++; break; } fail("expected , or )"); }
      return { k: "call", fn: id[0], args };
    }
    return { k: "field", path: id[0] };
  }
  function binary(min: number): Expr {
    let left = primary();
    let chain: string | null = null; // the function of an unparenthesized `a and b and c`: one n-ary call, as the engine keeps it
    for (;;) {
      ws();
      const word = /^(and|or)\b/.exec(src.slice(i));
      const op = word ? [word[1], word[1] === "and" ? "And" : "Or", word[1] === "and" ? 2 : 1] as [string, string, number] : INFIX.find(([s, , p]) => p >= min && src.startsWith(s, i));
      if (!op || op[2] < min) return left;
      i += op[0].length;
      const right = binary(op[2] + 1);
      if ((op[1] === "And" || op[1] === "Or") && chain === op[1] && left.k === "call" && left.fn === op[1]) left = { ...left, args: [...left.args, right] };
      else left = { k: "call", fn: op[1], args: [left, right] };
      chain = op[1];
    }
  }
  try { const e = binary(1); ws(); if (i < src.length) fail(`unexpected ${src[i]}`); return { ok: true, expr: e }; } catch (e) { return { ok: false, error: (e as Error).message }; }
}

export const KERNEL = "d:sattle.kernel";
export const builtinId = (name: string) => `${KERNEL}/builtin:${name}`;

/** Functions used by an expression that the palette does not list (the engine would reject them). */
export function unknownFunctions(e: Expr, known: Set<string>): string[] {
  if (e.k === "idof") return e.components.flatMap((c) => unknownFunctions(c.expr, known));
  if (e.k !== "call") return [];
  return [...(known.has(e.fn) ? [] : [e.fn]), ...e.args.flatMap((a) => unknownFunctions(a, known))];
}
/** The palette's spelling of every function in the expression (the text syntax spells them `concat`, `lowerInvariant`, `if`; the catalog `Concat`, `LowerInvariant`, `If`); unknown names are listed. */
export function canonicalFunctions(e: Expr, names: Iterable<string>): { expr: Expr; unknown: string[] } {
  const byLower = new Map([...names].map((n) => [n.toLowerCase(), n]));
  const unknown: string[] = [];
  const walk = (x: Expr): Expr => {
    if (x.k === "call") { const c = byLower.get(x.fn.toLowerCase()); if (!c) unknown.push(x.fn); return { ...x, fn: c ?? x.fn, args: x.args.map(walk) }; }
    if (x.k === "idof") return { ...x, components: x.components.map((c) => ({ ...c, expr: walk(c.expr) })) };
    return x;
  };
  return { expr: walk(e), unknown };
}

/** Components of `id domain(...)` that the identity domain does not declare (`d:<module>/identity:<name>/component:<component>`). */
export function unknownComponents(e: Expr, domains: Map<string, string>, has: (id: string) => boolean): string[] {
  if (e.k === "idof") return [...e.components.filter((c) => !has(`${domains.get(e.domain) ?? e.domain}/component:${c.name}`)).map((c) => `${e.domain}.${c.name}`), ...e.components.flatMap((c) => unknownComponents(c.expr, domains, has))];
  return e.k === "call" ? e.args.flatMap((a) => unknownComponents(a, domains, has)) : [];
}
export function unknownDomains(e: Expr, domains: Map<string, string>): string[] {
  if (e.k === "idof") return [...(domains.has(e.domain) ? [] : [e.domain]), ...e.components.flatMap((c) => unknownDomains(c.expr, domains))];
  return e.k === "call" ? e.args.flatMap((a) => unknownDomains(a, domains)) : [];
}

/** The subtree of `e` under `parent` in `slot`, as edits that create it. Ids follow the catalog scheme `<parent>/<slot>:#<ordinal>`. */
export function exprEdits(parent: string, slot: string, ordinal: number, e: Expr, ordered = false, domains: Map<string, string> = new Map()): Edit[] {
  const id = `${parent}/${slot}:#${ordinal}`;
  const link = contains(parent, id, slot, ordered ? ordinal : undefined);
  switch (e.k) {
    case "field": return [ent(id, "FieldRef", { path: text(e.path) }), link];
    case "lit": return [ent(id, "Literal", { value: e.kind === "null" ? { kind: "null" } : e.kind === "boolean" ? { kind: "boolean", value: e.value === "true" } : { kind: e.kind, value: e.value } }), link];
    case "call": return [ent(id, "Call", {}), rel(id, "function", builtinId(e.fn)), link, ...e.args.flatMap((a, n) => exprEdits(id, "arg", n, a, true, domains))];
    case "idof": return [
      ent(id, "IdOf", {}), rel(id, "domain", domains.get(e.domain) ?? e.domain), link,
      ...e.components.flatMap((c) => { const cid = `${id}/component:${c.name}`; return [ent(cid, "SetComponent", {}), rel(cid, "component", `${domains.get(e.domain) ?? e.domain}/component:${c.name}`), contains(id, cid, "component"), ...exprEdits(cid, "value", 0, c.expr, false, domains)]; }),
    ];
  }
}
