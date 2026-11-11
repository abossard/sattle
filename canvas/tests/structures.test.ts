import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import type { Edit } from "../src/core/model";
import { parsePalette } from "../src/editor/palette";
import { ent, rel, text } from "../src/editor/patches";
import { addConstraintEdits, addEquivalenceEdits, addFieldEdits, addRowTypeEdits, addRuleSetEdits, addValueRoleEdits, bindParamEdits, governsEdits, newProjectionEdits, policyProp, setCreateKeyEdits, setPolicyEdits, setRulePropEdits, setValueRoleEdits, valueSegment } from "../src/editor/structures";

const M = "d:m";
const E = (e: Edit) => e as Extract<Edit, { kind: "entity" }>;
const R = (e: Edit) => e as Extract<Edit, { kind: "relationship" }>;
const mk = (...es: Edit[]) => Graph.fromSnapshot({ version: { tenant: "t", graph: "catalog", revision: "0" }, entities: es.filter((e) => e.kind === "entity").map((e) => E(e).after!), relationships: [] });
const g = mk(ent(M, "Module"), ent(`${M}/entity:Cap`, "EntityType"), ent(`${M}/rel:Under`, "RelationshipType"), ent(`${M}/prop:status`, "PropertyType"), ent(`${M}/prop:owner`, "PropertyType"), ent(`${M}/identity:a`, "IdentityDomain"), ent(`${M}/identity:b`, "IdentityDomain"),
  ent(`${M}/projection:p/rule:r`, "Rule", { class: text("lens"), accept: { kind: "list", items: [text("create")] } }), ent(`${M}/projection:p/op:l`, "Link"));
const addNewProjection = (g: Graph, spec: Parameters<typeof newProjectionEdits>[1]) => newProjectionEdits(g, spec, palette);
const params = (...n: [string, string, boolean, string[]?][]) => n.map(([name, kind, required, values]) => ({ name, kind, required, ...(values ? { values } : {}) }));
const palette = parsePalette({
  operators: [], functions: [], valueKinds: ["Text", "Integer", "Decimal", "Boolean", "Instant", "Entity", "List"], valueRoles: ["ok", "warning", "danger", "unknown", "neutral"],
  valueRoleSlot: { container: "PropertyType", slot: "presentation", type: "ValueRole" },
  declarations: [
    { slot: "rows", type: "RowType", container: "Module", params: params(["name", "Text", true]), slots: [] },
    { slot: "equivalence", type: "EquivalencePolicy", container: "Module", params: params(["mode", "Text", true, ["assert", "review"]], ["threshold", "Decimal", true], ["name", "Text", true]), slots: [] },
    { slot: "constraint", type: "Constraint", container: "Module", slots: [],
      params: params(["kind", "Text", true], ["severity", "Text", true, ["error", "warning"]], ["values", "List", false], ["min", "Decimal", false], ["max", "Decimal", false], ["pattern", "Text", false], ["side", "Text", false, ["source", "target"]], ["name", "Text", true]),
      kinds: [{ kind: "acyclic", target: "relationship", uses: [], text: "" }, { kind: "cardinality", target: "relationship", uses: ["side", "min", "max"], text: "" }, { kind: "enum", target: "entity or relationship property", uses: ["onProperty", "values"], text: "" }, { kind: "range", target: "entity or relationship property", uses: ["onProperty", "min", "max"], text: "" }, { kind: "key", target: "entity", uses: ["onProperty"], text: "" }] },
  ],
  rowTypes: { type: "RowType", fieldSlot: "field", field: { type: "Field", params: params(["name", "Text", true], ["optional", "Boolean", true]), valueKinds: ["Text", "Integer"] } },
  equivalencePolicies: { type: "EquivalencePolicy", modes: ["assert", "review"], params: [] },
  rules: { type: "Rule", slots: [], onDelete: ["remove", "set"], params: params(["name", "Text", true], ["putput", "Boolean", true], ["accept", "List", false], ["onDelete", "Text", false, ["remove", "set"]]) },
});
const ids = (r: { ok: boolean; value?: Edit[] }) => (r.value ?? []).map((e) => (e as { id: string }).id.replace(/d:m\//g, ""));

describe("row types and fields", () => {
  it("a row type is contained by the module; a field has a kind relationship and an ordinal", () => {
    expect(ids(addRowTypeEdits(g, M, "Row", palette))).toEqual(["rows:Row", "d:m|contains|rows:Row"]);
    const r = addFieldEdits(g, `${M}/rows:Row`, "a", "Integer", true, palette);
    expect(ids(r)).toEqual(["rows:Row/field:a", "rows:Row/field:a|valueKind|d:sattle.kernel/builtin:Integer", "rows:Row|contains|rows:Row/field:a"]);
    expect(E(r.ok ? r.value[0] : ent("x", "x")).after!.props.optional).toEqual({ kind: "boolean", value: true });
  });
  it.each([["bad name", "x y", "Text"], ["a kind the palette does not list for fields", "x", "List"]])("%s is refused", (_, name, kind) => expect(addFieldEdits(g, `${M}/rows:Row`, name, kind, false, palette).ok).toBe(false));
  it("an engine that does not describe declarations gets a message, not a guess", () => {
    const old = parsePalette({ operators: [] });
    expect(addRowTypeEdits(g, M, "Row", old)).toEqual({ ok: false, reason: "the engine's palette does not describe rows declarations (it needs protocol 3.2)" });
    expect(addConstraintEdits(g, M, "acyclic", `${M}/rel:Under`, [], "error", {}, old).ok).toBe(false);
  });
});

describe("constraints from the palette's kinds", () => {
  it.each([
    ["acyclic", `${M}/rel:Under`, [], {}, ["constraint:acyclic-Under", "constraint:acyclic-Under|constrains|rel:Under", "d:m|contains|constraint:acyclic-Under"]],
    ["cardinality", `${M}/rel:Under`, [], { side: "target", min: "0", max: "3" }, ["constraint:cardinality-Under", "constraint:cardinality-Under|constrains|rel:Under", "d:m|contains|constraint:cardinality-Under"]],
    ["enum", `${M}/entity:Cap`, [`${M}/prop:status`], { values: "a, b" }, ["constraint:enum-Cap.status", "constraint:enum-Cap.status|constrains|entity:Cap", "constraint:enum-Cap.status|onProperty|prop:status", "d:m|contains|constraint:enum-Cap.status"]],
    ["range", `${M}/entity:Cap`, [`${M}/prop:status`], { min: "0", max: "100" }, ["constraint:range-Cap.status", "constraint:range-Cap.status|constrains|entity:Cap", "constraint:range-Cap.status|onProperty|prop:status", "d:m|contains|constraint:range-Cap.status"]],
    ["key", `${M}/entity:Cap`, [`${M}/prop:status`, `${M}/prop:owner`], {}, ["constraint:key-Cap", "constraint:key-Cap|constrains|entity:Cap", "constraint:key-Cap|onProperty|prop:status", "constraint:key-Cap|onProperty|prop:owner", "d:m|contains|constraint:key-Cap"]],
  ])("%s (a kind the engine lists)", (kind, target, properties, attrs, want) => expect(ids(addConstraintEdits(g, M, kind, target, properties, "error", attrs, palette))).toEqual(want));
  it.each([
    ["an unknown kind", "nope", `${M}/rel:Under`, [], {}],
    ["a missing attribute", "cardinality", `${M}/rel:Under`, [], { side: "target", min: "0" }],
    ["a bad number", "cardinality", `${M}/rel:Under`, [], { side: "target", min: "x", max: "1" }],
    ["a side the palette does not list", "cardinality", `${M}/rel:Under`, [], { side: "up", min: "0", max: "1" }],
    ["a property kind without a property", "enum", `${M}/entity:Cap`, [], { values: "a" }],
    ["two properties for a kind that takes one", "enum", `${M}/entity:Cap`, [`${M}/prop:status`, `${M}/prop:status`], { values: "a" }],
  ])("refuses %s", (_, kind, target, properties, attrs) => expect(addConstraintEdits(g, M, kind, target, properties, "error", attrs, palette).ok).toBe(false));
  it("types the parts by the palette: decimals, lists of text, enumerations", () => {
    const c = addConstraintEdits(g, M, "cardinality", `${M}/rel:Under`, [], "warning", { side: "source", min: "0", max: "1" }, palette);
    expect(c.ok && E(c.value[0]).after!.props).toMatchObject({ min: { kind: "decimal", value: "0" }, max: { kind: "decimal", value: "1" }, severity: text("warning") });
    const e = addConstraintEdits(g, M, "enum", `${M}/entity:Cap`, [`${M}/prop:status`], "error", { values: "a, b" }, palette);
    expect(e.ok && E(e.value[0]).after!.props.values).toEqual({ kind: "list", items: [text("a"), text("b")] });
  });
});

describe("equivalence policies, value roles and rule programs", () => {
  it("a policy lists its two domains in order, takes its mode from the palette, and an operator applies it", () => {
    const r = addEquivalenceEdits(g, M, "eq", "assert", "1", [`${M}/identity:a`, `${M}/identity:b`], palette);
    expect(r.ok && R(r.value[1]).after!.props.ordinal).toEqual({ kind: "integer", value: "0" });
    expect(r.ok && R(r.value[2]).after!.props.ordinal).toEqual({ kind: "integer", value: "1" });
    expect(setPolicyEdits(g, `${M}/projection:p/op:l`, `${M}/equivalence:eq`).map((e) => (e as { id: string }).id.replace(/d:m\//g, ""))).toEqual(["projection:p/op:l|policy|equivalence:eq"]);
  });
  it.each([["the same domain twice", "assert", [`${M}/identity:a`, `${M}/identity:a`]], ["one domain", "assert", [`${M}/identity:a`]], ["an unknown domain", "assert", [`${M}/identity:a`, `${M}/identity:z`]], ["a mode the palette does not list", "guess", [`${M}/identity:a`, `${M}/identity:b`]]])("refuses %s", (_, mode, between) => expect(addEquivalenceEdits(g, M, "eq", mode, "1", between, palette).ok).toBe(false));

  it.each([
    [{ kind: "text", value: "Healthy" } as const, "Healthy"], [{ kind: "text", value: "a b:c~" } as const, "a%20b%3Ac%7E"], [{ kind: "integer", value: "0" } as const, "~i0"],
    [{ kind: "decimal", value: "0.50" } as const, "~d0.5"], [{ kind: "boolean", value: true } as const, "~btrue"],
  ])("the id segment of %j is %s, as the engine writes it", (v, want) => expect(valueSegment(v)).toBe(want));
  it("a value role is a ValueRole node in the property's presentation slot; the value is read by the property's kind", () => {
    const r = addValueRoleEdits(g, `${M}/prop:status`, "Healthy", "ok", palette);
    expect(ids(r)).toEqual(["prop:status/presentation:Healthy", "prop:status|contains|prop:status/presentation:Healthy"]);
    expect(r.ok && E(r.value[0]).after!.props).toEqual({ value: text("Healthy"), role: text("ok") });
    expect(addValueRoleEdits(g, `${M}/prop:status`, "x", "purple", palette).ok).toBe(false);
    expect(addValueRoleEdits(g, `${M}/entity:Cap`, "x", "ok", palette).ok).toBe(false);
    expect(addValueRoleEdits(g, `${M}/prop:status`, "Healthy", "ok", parsePalette({ operators: [] })).ok).toBe(false);
    expect(E(setValueRoleEdits(g, `${M}/prop:status`, "danger")[0]).after!.props.role).toEqual(text("danger"));
  });

  it.each([["accept", "create, delete", { kind: "list", items: [text("create"), text("delete")] }], ["onDelete", "set", text("set")], ["putput", "true", { kind: "boolean", value: true }]] as const)("rule property %s is typed by the palette", (name, raw, want) => {
    const e = setRulePropEdits(g, `${M}/projection:p/rule:r`, name, raw, palette);
    expect(e.ok && E(e.value[0]).after!.props[name]).toEqual(want);
  });
  it.each([["onDelete", "keep"], ["putput", "maybe"], ["nope", "x"]])("refuses %s = %s", (name, raw) => expect(setRulePropEdits(g, `${M}/projection:p/rule:r`, name, raw, palette).ok).toBe(false));
  it("an empty value removes the property; policy names map to property names", () => {
    const e = setRulePropEdits(g, `${M}/projection:p/rule:r`, "accept", " ", palette);
    expect(e.ok && E(e.value[0]).after!.props.accept).toBeUndefined();
    expect([policyProp("on delete"), policyProp("on create"), policyProp("accept")]).toEqual(["onDelete", "onCreate", "accept"]);
  });
  it("create key, defaults and governs", () => {
    const rule = `${M}/projection:p/rule:r`;
    expect(ids(setCreateKeyEdits(g, rule, `${M}/identity:a`))).toEqual(["projection:p/rule:r|createKey|identity:a"]);
    expect(setCreateKeyEdits(g, rule, `${M}/prop:status`).ok).toBe(false);
    const d = addRuleSetEdits(g, rule, "createDefault", `${M}/prop:status`, '"active"', palette);
    expect(ids(d)[0]).toBe("projection:p/rule:r/createDefault:status");
    expect(governsEdits(g, rule, `${M}/projection:p/op:l`, true).ok).toBe(true);
    expect(governsEdits(g, rule, `${M}/projection:p/op:l`, false).ok).toBe(false);
  });
});

describe("binding a parameter of an existing projection", () => {
  const gb = (() => {
    const base = mk(ent("d:sp/space:s/binding:x.n", "Binding", { value: { kind: "integer", value: "1" } }), ent("d:m/projection:p/port:n", "Port"));
    const snap = base.toSnapshot();
    const rels = [rel("d:sp/space:s/binding:x.n", "bindsPort", "d:m/projection:p/port:n"), rel("d:m/projection:p/port:n", "valueKind", "d:sattle.kernel/builtin:Integer")].map((e) => R(e).after!);
    return Graph.fromSnapshot({ ...snap, relationships: rels });
  })();
  it("changes the literal, typed by the port's value kind", () => {
    const r = bindParamEdits(gb, "d:sp/space:s/binding:x.n", "3");
    expect(r.ok && E(r.value[0]).after!.props.value).toEqual({ kind: "integer", value: "3" });
    expect(r.ok && E(r.value[0]).before!.props.value).toEqual({ kind: "integer", value: "1" });
  });
  it.each([["a value of the wrong kind", "d:sp/space:s/binding:x.n", "three"], ["something that is not a binding", "d:m/projection:p/port:n", "3"]])("refuses %s", (_, id, v) => expect(bindParamEdits(gb, id, v).ok).toBe(false));
});

describe("a new projection and its place in the space", () => {
  const S = "d:sp/space:s";
  const base = mk(ent(M, "Module"), ent("d:out", "Module"), ent("d:ops", "Module"), ent(S, "GraphSpace"), ent(`${S}/graph:master`, "Graph"));
  const spec = { module: "m", name: "podReady", description: "", outputModule: "d:out", graphName: "podReady", inputs: [{ port: "master", graph: "master", ontology: "d:ops" }], params: [{ port: "minReady", kind: "Integer", value: "1" }] };
  const rels = (r: { ok: boolean; value?: Edit[] }) => (r.value ?? []).filter((e) => e.kind === "relationship").map((e) => (e as { id: string }).id.replace(/d:m\/projection:podReady/g, "P").replace(new RegExp(S.replace(/[/]/g, "\\/"), "g"), "S"));
  it("creates the projection with its ports, its graph in the space and one binding per port", () => {
    const r = addNewProjection(base, spec);
    expect(r.ok).toBe(true);
    expect(rels(r)).toEqual(expect.arrayContaining([
      "P|outputOntology|d:out", "d:m|contains|P", "S|contains|S/graph:podReady", "S/graph:podReady|producedBy|P",
      "P/port:master|expects|d:ops", "S/binding:podReady.master|bindsPort|P/port:master", "S/binding:podReady.master|boundFrom|S/graph:master",
      "P/port:minReady|valueKind|d:sattle.kernel/builtin:Integer", "S/binding:podReady.minReady|bindsPort|P/port:minReady",
    ]));
    const b = (r.ok ? r.value : []).find((e) => e.kind === "entity" && e.id.endsWith("binding:podReady.minReady")) as Extract<Edit, { kind: "entity" }>;
    expect(b.after!.props.value).toEqual({ kind: "integer", value: "1" });
  });
  it.each([
    ["a name that exists", { ...spec, graphName: "master" }],
    ["a bad name", { ...spec, name: "bad name" }],
    ["no output ontology", { ...spec, outputModule: "d:nope" }],
    ["a graph the space does not have", { ...spec, inputs: [{ port: "master", graph: "nope", ontology: "d:ops" }] }],
    ["a port name used twice", { ...spec, params: [{ port: "master", kind: "Integer", value: "1" }] }],
    ["a parameter that is not a number", { ...spec, params: [{ port: "t", kind: "Integer", value: "x" }] }],
  ])("refuses %s", (_, s) => expect(addNewProjection(base, s).ok).toBe(false));
});
