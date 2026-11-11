import { describe, expect, it } from "vitest";
import { DEFAULT_COLORING, MAX_VALUES, MISSING, OTHER, PALETTE, ROLE_GLYPH, colorOf, colorValueOf, coloringLabel, legendOf, resolveColoring, tally } from "../src/app/color-by";
import { roleColors } from "../src/app/roles";
import { readPresentation } from "../src/source/presentation";
import type { OntologyDefinition } from "../src/core/wire-types";
import { labelOf } from "../src/core/model";
import { text } from "./helpers";

describe("color by property", () => {
  it("values get palette colors in alphabetical order, so a value keeps its color", () => {
    const l = legendOf(new Map([["unhealthy", 2], ["healthy", 9], ["degraded", 1]]));
    expect(l.map((x) => [x.value, x.color])).toEqual([["degraded", PALETTE[0]], ["healthy", PALETTE[1]], ["unhealthy", PALETTE[2]]]);
    expect(colorOf(l, "healthy")).toBe(PALETTE[1]);
  });
  it("numbers sort numerically, many values collapse to other, missing values are listed", () => {
    const counts = new Map(Array.from({ length: MAX_VALUES + 3 }, (_, i) => [String(i + 1), 1] as const));
    const l = legendOf(counts, 4);
    expect(l.slice(0, 3).map((x) => x.value)).toEqual(["1", "2", "3"]);
    expect(l.find((x) => x.value === "(other values)")).toMatchObject({ color: OTHER, count: 3 });
    expect(l[l.length - 1]).toMatchObject({ value: "(no value)", color: MISSING, count: 4 });
    expect(colorOf(l, String(MAX_VALUES + 2))).toBe(OTHER);
    expect(colorOf(l, undefined)).toBe(MISSING);
  });
});

describe("labelOf", () => {
  it.each([
    ["name", { name: text("a") }, "a"],
    ["display when there is no name", { display: text("d") }, "d"],
    ["a module qualified name", { "acme.health.name": text("node drop rate"), state: text("x") }, "node drop rate"],
    ["name wins over a qualified one", { name: text("n"), "m.name": text("q") }, "n"],
    ["the id when nothing names it", { state: text("x") }, "e1"],
  ])("%s", (_, props, want) => expect(labelOf({ id: "e1", props })).toBe(want));
});

const ids = (...v: string[]) => v.map((value) => ({ value }));
const ontology = { entityTypes: [{ id: { value: "Pod" }, properties: ids("state", "name") }], relationshipTypes: [{ id: { value: "Calls" }, sourceTypes: [], targetTypes: [], properties: ids("health") }] } as unknown as OntologyDefinition;
const pres = (...e: [string, string][]) => e.map(([v, role]) => ({ value: { kind: "text", value: v }, role }));
const hints = readPresentation({ properties: [{ id: { value: "state" }, presentation: pres(["Healthy", "ok"], ["Unhealthy", "danger"]) }] }).hints;

describe("presentation hints (engine version 3.2)", () => {
  it.each([
    ["a text value", { properties: [{ id: { value: "state" }, presentation: pres(["a", "ok"], ["b", "danger"]) }] }, { state: { a: "ok", b: "danger" } }],
    ["integer and boolean values by their text", { properties: [{ id: { value: "n" }, presentation: [{ value: { kind: "integer", value: "0" }, role: "danger" }, { value: { kind: "boolean", value: true }, role: "ok" }] }] }, { n: { "0": "danger", true: "ok" } }],
    ["all five roles", { properties: [{ id: { value: "s" }, presentation: pres(["a", "ok"], ["b", "warning"], ["c", "danger"], ["d", "unknown"], ["e", "neutral"]) }] }, { s: { a: "ok", b: "warning", c: "danger", d: "unknown", e: "neutral" } }],
    ["an entry with a role the kernel does not have is dropped", { properties: [{ id: { value: "s" }, presentation: pres(["a", "purple"], ["b", "ok"]) }] }, { s: { b: "ok" } }],
    ["a property without presentation", { properties: [{ id: { value: "s" } }] }, {}],
    ["no properties", {}, {}],
  ])("reads %s", (_, raw, want) => expect(Object.fromEntries(readPresentation(raw).hints.map((h) => [h.property, h.roles]))).toEqual(want));
  it("removes the hint from the ontology, so the contract schema (no extra fields) still accepts it", () => {
    const { ontology: o } = readPresentation({ schemaVersion: "1.0", properties: [{ id: { value: "s" }, presentation: pres(["a", "ok"]) }, { id: { value: "t" } }] });
    expect(JSON.stringify(o)).not.toContain("presentation");
    expect((o as { properties: unknown[] }).properties.length).toBe(2);
  });
});

describe("what colors a graph", () => {
  it.each([
    ["the hint by default, for the target that has the property", "node", DEFAULT_COLORING, "state"],
    ["no default for edges when the hinted property is not on them", "edge", DEFAULT_COLORING, null],
    ["type colors", "node", "", null],
    ["another property overrides the hint", "node", "name", "name"],
    ["a property on edges", "edge", "health", "health"],
  ] as const)("%s", (_, target, setting, want) => expect(resolveColoring({ ontology, presentation: hints }, target, setting)?.property ?? null).toBe(want));
  it("an explicit choice of a hinted property keeps its roles; another property has none", () => {
    expect(resolveColoring({ ontology, presentation: hints }, "node", "state")?.hint).not.toBeNull();
    expect(resolveColoring({ ontology, presentation: hints }, "node", "name")?.hint).toBeNull();
  });
});

describe("legend by roles", () => {
  const colors = roleColors("dark");
  it("orders values by role, colors them by role, counts them; a value without a role is neutral and a missing one unknown", () => {
    const l = legendOf(new Map([["Unhealthy", 1], ["Healthy", 7], ["Odd", 2]]), 3, hints[0], colors);
    expect(l.map((x) => [x.value, x.role, x.count])).toEqual([["Healthy", "ok", 7], ["Unhealthy", "danger", 1], ["Odd", "neutral", 2], ["(no value)", "unknown", 3]]);
    expect(colorOf(l, "Healthy")).toBe(colors.ok);
    expect(colorOf(l, "Unhealthy")).toBe(colors.danger);
    expect(colorOf(l, undefined)).toBe(colors.unknown);
  });
  it.each(["light", "dark"] as const)("roles use the Fluent status colors of the %s theme, and they differ per role", (t) => {
    const c = roleColors(t);
    expect(new Set(Object.values(c)).size).toBe(5);
    expect(new Set(Object.values(ROLE_GLYPH)).size).toBe(5);
  });
});

describe("an ontology that hints several properties of the target", () => {
  const onto = { entityTypes: [{ id: { value: "Component" }, properties: ids("acme.health.state", "name") }, { id: { value: "Signal" }, properties: ids("degraded", "unhealthy") }], relationshipTypes: [] } as unknown as OntologyDefinition;
  const several = readPresentation({ properties: [
    { id: { value: "degraded" }, presentation: pres(["off", "ok"], ["on", "warning"]) },
    { id: { value: "acme.health.state" }, presentation: pres(["Healthy", "ok"], ["Unhealthy", "danger"], ["Unknown", "unknown"]) },
    { id: { value: "unhealthy" }, presentation: pres(["off", "ok"], ["on", "danger"]) },
  ] }).hints;
  const c = resolveColoring({ ontology: onto, presentation: several }, "node", DEFAULT_COLORING)!;
  const t = (value: string) => ({ kind: "text", value });
  it("the default keeps every hinted property of the target and names them", () => {
    expect(c.hints?.map((h) => h.property)).toEqual(["degraded", "acme.health.state", "unhealthy"]);
    expect(coloringLabel(c)).toBe("degraded, state, unhealthy");
    expect(resolveColoring({ ontology: onto, presentation: several }, "node", "unhealthy")?.hints).toBeUndefined();
  });
  it.each([
    ["a component takes the role of its state", { "acme.health.state": t("Unhealthy") }, "state: Unhealthy", "danger"],
    ["a signal that is on at both levels is danger, the worse one", { degraded: t("on"), unhealthy: t("on") }, "unhealthy: on", "danger"],
    ["a signal that is only degraded is a warning", { degraded: t("on"), unhealthy: t("off") }, "degraded: on", "warning"],
    ["unknown is worse than ok", { degraded: t("off"), "acme.health.state": t("Unknown") }, "state: Unknown", "unknown"],
    ["a value the property gave no role is neutral", { "acme.health.state": t("Odd") }, "state: Odd", "neutral"],
    ["an element with none of them has no value", { name: t("x") }, undefined, undefined],
  ])("%s", (_, props, text, role) => expect(colorValueOf(c, props as never)).toEqual(text ? { text, role } : undefined));
  it("the tally counts what each element decided and builds the roles from them", () => {
    const r = tally(c, [{ props: { "acme.health.state": t("Healthy") } }, { props: { "acme.health.state": t("Healthy") } }, { props: { degraded: t("on"), unhealthy: t("off") } }, { props: {} }] as never);
    expect([...r.counts]).toEqual([["state: Healthy", 2], ["degraded: on", 1]]);
    expect(r.missing).toBe(1);
    expect(r.hint?.roles).toEqual({ "state: Healthy": "ok", "degraded: on": "warning" });
  });
});
