import { describe, expect, it } from "vitest";
import { formFor, parseAggregates, parseAssignments, parsePattern, parseRow } from "../src/core/projection/forms";
import { defaultOperator } from "../src/core/projection/edit";
import { OPERATOR_KINDS } from "../src/core/projection/ports";

const ctx = { bindings: ["a"], entityTypes: ["Service"], relationshipTypes: ["Calls"] };

describe("form parsers", () => {
  it.each([
    ["name = lowerInvariant(s.name)", true], ["a = 1\nb = \"x\"", true], ["", true], ["no equals sign", false], ["a = equal(", false],
  ])("assignments %j", (t, ok) => expect(parseAssignments(t).ok).toBe(ok));
  it.each([["uid: text\nn: integer?", true], ["uid text", false], ["x: nosuchtype", false]])("row %j", (t, ok) => expect(parseRow(t).ok).toBe(ok));
  it.each([["s: Service", "e: s -Calls-> t", true], ["bad line", "", false], ["s: Service", "e: s Calls t", false]])("pattern %j / %j", (n, e, ok) => expect(parsePattern(n, e).ok).toBe(ok));
  it.each([["c = countDistinct(id)", true], ["c = median(id)", false]])("aggregates %j", (t, ok) => expect(parseAggregates(t).ok).toBe(ok));
});

describe("forms round-trip every operator kind", () => {
  it.each(OPERATOR_KINDS)("%s: re-applying each field's own text keeps the operator", (kind) => {
    const op = defaultOperator(kind, "x");
    for (const f of formFor(op, ctx)) {
      const r = f.apply(f.value);
      expect(r.ok, `${kind}.${f.key}`).toBe(true);
      if (r.ok) expect(JSON.stringify(r.op)).toBe(JSON.stringify(op));
    }
  });
  it("applies an edit", () => {
    const op = defaultOperator("filter", "f");
    const field = formFor(op, ctx)[0];
    const r = field.apply('equal(health, "ok")');
    expect(r.ok && r.op.kind === "filter" && r.op.predicate.kind === "call").toBe(true);
  });
});
