// An expression subtree of the catalog as the text an author types: the inverse of `parseExpr` (editor/expr.ts).
// `parseExpr(exprText(e))` gives the same tree, so every editor can show an expression as text and take text back.
import { Catalog, moduleOfId } from "../core/catalog";
import type { Entity } from "../core/model";
import { valueText } from "../core/model";

/** The node types an expression is made of (the kernel's `Expr` family). */
export const EXPR_TYPES: ReadonlySet<string> = new Set(["FieldRef", "Literal", "Call", "IdOf"]);

/** Infix spelling and precedence of a function, as the parser reads them. */
const INFIX: Record<string, [string, number]> = {
  Or: ["or", 1], And: ["and", 2], Equal: ["==", 3], NotEqual: ["!=", 3],
  LessOrEqual: ["<=", 4], GreaterOrEqual: [">=", 4], LessThan: ["<", 4], GreaterThan: [">", 4],
  Concat: ["++", 5], Add: ["+", 5], Subtract: ["-", 5], Multiply: ["*", 6], Divide: ["/", 6],
};
const PRIMARY = 9;

const lowerFirst = (s: string): string => s.charAt(0).toLowerCase() + s.slice(1);
const quote = (s: string): string => `"${s.replace(/[\\"]/g, (c) => `\\${c}`)}"`;

function literal(e: Entity): string {
  const v = e.props.value;
  if (!v || v.kind === "null") return "null";
  return v.kind === "text" ? quote(v.value) : valueText(v);
}

/** How the module `from` names an identity domain: bare in its own module, `alias.name` through the import that brings it in. */
export function domainLabel(cat: Catalog, from: string, domain: string): string {
  const name = cat.name(domain);
  const module = `d:${moduleOfId(domain)}`;
  if (module === from) return name;
  for (const r of cat.g.relationships()) {
    if (r.type !== "imports" || r.source !== from || r.target.replace(/@[^@/]*$/, "") !== module) continue;
    const alias = r.props.alias;
    if (alias?.kind === "text" && alias.value) return `${alias.value}.${name}`;
  }
  return name;
}

/** Text and binding strength of the expression at `id`. */
function print(cat: Catalog, id: string, from: string): [string, number] {
  const e = cat.entity(id);
  if (!e) return ["?", PRIMARY];
  const sub = (child: string, min: number): string => { const [t, p] = print(cat, child, from); return p < min ? `(${t})` : t; };
  switch (e.type) {
    case "FieldRef": return [valueText(e.props.path ?? { kind: "text", value: "?" }), PRIMARY];
    case "Literal": return [literal(e), PRIMARY];
    case "IdOf": {
      const domain = cat.targets(id, "domain")[0];
      const comps = cat.children(id, "component").map((c) => `${cat.name(cat.targets(c, "component")[0] ?? c)}: ${cat.children(c, "value").map((v) => sub(v, 1)).join("")}`);
      return [`${domain ? domainLabel(cat, from, domain) : "?"}(${comps.join(", ")})`, PRIMARY];
    }
    case "Call": {
      const fn = cat.name(cat.targets(id, "function")[0] ?? "");
      const args = cat.children(id, "arg");
      const infix = INFIX[fn];
      // a nested call of the same n-ary connective keeps its parentheses, or the parser would flatten it
      const nary = fn === "And" || fn === "Or";
      if (infix && args.length >= 2 && (nary || args.length === 2)) {
        const [op, prec] = infix;
        return [args.map((a, i) => sub(a, i === 0 && !nary ? prec : prec + 1)).join(` ${op} `), prec];
      }
      if (fn === "Not" && args.length === 1) return [`!${sub(args[0], PRIMARY)}`, PRIMARY];
      if (fn === "Negate" && args.length === 1) { const t = sub(args[0], PRIMARY); return [/^[\d.]/.test(t) ? `-(${t})` : `-${t}`, PRIMARY]; }
      return [`${lowerFirst(fn)}(${args.map((a) => sub(a, 1)).join(", ")})`, PRIMARY];
    }
    default: return [cat.name(id), PRIMARY];
  }
}

/** The expression at `id` as text, with identity domains named as the module that holds it names them. */
export const exprText = (cat: Catalog, id: string): string => print(cat, id, `d:${moduleOfId(id)}`)[0];
