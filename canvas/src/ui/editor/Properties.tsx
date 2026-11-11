// Property panel of the selection: parameters from the palette, slot contents (expressions, assignments, aggregates, set properties,
// pattern nodes and edges, read fields), wires, rules. Every field is a Fluent control; every change is an editor command.
import { Accordion, AccordionHeader, AccordionItem, AccordionPanel, Badge, Button, Field, Input, MessageBar, MessageBarBody, Select, Switch, Text, makeStyles, tokens } from "@fluentui/react-components";
import { Delete20Regular } from "@fluentui/react-icons";
import { policyProp } from "../../editor/structures";
import { refsOf } from "../../editor/refs";
import { useEffect, useState } from "react";
import { DIRECTION_CLASSES, type Catalog } from "../../core/catalog";
import type { Graph } from "../../core/graph";
import { valueText } from "../../core/model";
import { canWire, durationText, isDuration } from "../../editor/commands";
import { aggregateAllowed, type PaletteOperator, type PaletteParam, type PaletteSlot } from "../../editor/palette";
import type { EditorSession } from "../../editor/session";
import { dispatch } from "../../state/store";
import type { Command } from "../../state/types";

const useStyles = makeStyles({ col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, padding: tokens.spacingHorizontalM }, row: { display: "flex", columnGap: tokens.spacingHorizontalS, alignItems: "end", flexWrap: "wrap" }, grow: { flexGrow: 1, minWidth: "120px" } });
const run = (cmd: Command) => dispatch({ type: "editor/do", cmd });
const prop = (g: Graph, id: string, name: string) => { const v = g.entity(id)?.props[name]; return v ? valueText(v) : ""; };

/** An input that commits on Enter or blur, and only when the text changed. */
export function CommitInput({ label, value, onCommit, placeholder }: { label: string; value: string; onCommit: (v: string) => void; placeholder?: string }) {
  const [v, setV] = useState(value);
  useEffect(() => setV(value), [value]);
  const commit = () => { if (v !== value) onCommit(v); };
  return <Field label={label}><Input value={v} placeholder={placeholder} onChange={(_, d) => setV(d.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); commit(); } }} /></Field>;
}

export function AddRow({ fields, cta, onAdd }: { fields: { key: string; label: string; options?: { value: string; label: string; disabled?: boolean }[]; placeholder?: string }[]; cta: string; onAdd: (v: Record<string, string>) => void }) {
  const s = useStyles();
  const [v, setV] = useState<Record<string, string>>({});
  return (
    <div className={s.row}>
      {fields.map((f) => (
        <Field key={f.key} label={f.label} className={s.grow}>
          {f.options
            ? <Select value={v[f.key] ?? ""} onChange={(_, d) => setV({ ...v, [f.key]: d.value })}><option value="">(choose)</option>{f.options.map((o) => <option key={o.value} value={o.value} disabled={o.disabled}>{o.label}</option>)}</Select>
            : <Input value={v[f.key] ?? ""} placeholder={f.placeholder} onChange={(_, d) => setV({ ...v, [f.key]: d.value })} />}
        </Field>
      ))}
      <Button onClick={() => { onAdd(v); setV({}); }}>{cta}</Button>
    </div>
  );
}

export const ofKind = (g: Graph, type: string) => [...g.entities()].filter((e) => e.type === type).map((e) => ({ value: e.id, label: `${e.id.slice(2, e.id.indexOf("/"))}.${prop(g, e.id, "name") || e.id.slice(e.id.lastIndexOf(":") + 1)}` })).sort((a, b) => a.label.localeCompare(b.label));

/** Children that are made of parameters (a lift's `state`): each with its parameters as fields, typed by the palette, and a row to add one. */
function ParamChildren({ slot, c, g, id }: { slot: PaletteSlot; c: Catalog; g: Graph; id: string }) {
  const s = useStyles();
  const ct = slot.childTypes?.[0];
  if (!ct) return <Text size={200}>{slot.name} ({slot.children.join(", ")}) has no editor in the panel yet.</Text>;
  const label = (p: PaletteParam) => `${p.name}${isDuration(p) ? " (duration)" : p.unit ? ` (${p.unit})` : ""}`;
  return (
    <div className={s.col}><Text weight="semibold">{slot.name}</Text>
      {c.children(id, slot.name).map((k) => (
        <div key={k} className={s.col} role="group" aria-label={`${slot.name} ${prop(g, k, "target")}`}>
          <div className={s.row}><Text className={s.grow} weight="semibold">{prop(g, k, "target")}</Text><Button size="small" icon={<Delete20Regular />} aria-label={`Remove ${slot.name} ${prop(g, k, "target")}`} onClick={() => run({ t: "delete", ids: [k] })} /></div>
          {ct.params.filter((p) => p.name !== "target").map((p) => p.values
            ? <Field key={p.name} label={label(p)}><Select value={prop(g, k, p.name)} onChange={(_, d) => run({ t: "param", id: k, name: p.name, value: d.value })}>{p.values.map((x) => <option key={x}>{x}</option>)}</Select></Field>
            : <CommitInput key={p.name} label={`${label(p)}${p.required ? "" : " (optional)"}`} value={prop(g, k, p.name)} onCommit={(v) => run({ t: "param", id: k, name: p.name, value: v })} />)}
        </div>
      ))}
      <AddRow cta={`Add ${slot.name}`} fields={ct.params.map((p) => ({ key: p.name, label: p.name === "target" ? "Property it sets" : label(p), placeholder: p.example ?? p.default, ...(p.values ? { options: p.values.map((x) => ({ value: x, label: x })) } : {}) }))}
        onAdd={(v) => run({ t: "child", id, slot: slot.name, childType: ct.type, key: v.target ?? "", params: Object.fromEntries(Object.entries(v).filter(([k]) => k !== "target")) })} />
    </div>
  );
}

function Slot({ op, slot, c, g, id, session }: { op: PaletteOperator; slot: PaletteSlot; c: Catalog; g: Graph; id: string; session: EditorSession }) {
  const s = useStyles();
  const kids = c.children(id, slot.name);
  const kind = slot.children[0];
  const list = (describe: (k: string) => string) => kids.map((k) => (
    <div key={k} className={s.row}><Text className={s.grow}>{describe(k)}</Text><Button size="small" icon={<Delete20Regular />} aria-label={`Remove ${describe(k)}`} onClick={() => run({ t: "delete", ids: [k] })} /></div>
  ));
  if (slot.name === "port") return null;
  void op;
  if (kind === "Expr" && slot.multiplicity === "*") {
    return (
      <div className={s.col}><Text weight="semibold">{slot.name}</Text>{list((k) => c.expr(k))}
        <AddRow cta="Add" fields={[{ key: "src", label: "Expression", placeholder: "a.x == b.y" }]} onAdd={(v) => run({ t: "exprAdd", id, slot: slot.name, src: v.src ?? "" })} />
      </div>
    );
  }
  if (kind === "Expr" || kind === "IdOf") {
    const cur = kids.length ? c.expr(kids[0]) : "";
    return <CommitInput label={`${slot.name}${slot.multiplicity === "1" ? " (required)" : ""}${slot.value ? ` (${slot.value.offers})` : ""}`} value={cur} placeholder={kind === "IdOf" ? "id domain(component: expr)" : "expression, for example s != t"} onCommit={(v) => run({ t: "expr", id, slot: slot.name, src: v })} />;
  }
  if (kind === "LiftState") return <ParamChildren slot={slot} c={c} g={g} id={id} />;
  if (kind === "Assign" || kind === "Aggregate") {
    return (
      <div className={s.col}><Text weight="semibold">{slot.name}{slot.multiplicity === "+" ? " (at least one)" : ""}</Text>
        {slot.value && <Text size={200}>{slot.value.kind === "fieldRef" ? "A field: " : "An expression over "}{slot.value.offers}</Text>}
        {list((k) => `${prop(g, k, "target")}${kind === "Aggregate" ? ` = ${prop(g, k, "fn")}(` : " = "}${c.children(k, "value").map((v) => c.expr(v)).join("")}${kind === "Aggregate" ? ")" : ""}`)}
        <AddRow cta="Add" fields={[{ key: "name", label: "Name" }, ...(kind === "Aggregate" ? [{ key: "fn", label: "Function", options: session.palette.aggregates.filter((a) => a.supported).map((a) => { const ok = aggregateAllowed(session.palette, a.name, op.kind); return ok.ok ? { value: a.name, label: ok.requires ? `${a.name} (${ok.requires})` : a.name } : { value: a.name, label: `${a.name} (${ok.reason.replace(`${a.name} is `, "")})`, disabled: true }; }) }] : []), { key: "src", label: "Expression", placeholder: "s.name" }, ...(kind === "Aggregate" ? [{ key: "second", label: "Second argument (order of last, weight of weightedMean)" }] : [])]}
          onAdd={(v) => run({ t: "named", id, slot: slot.name, childType: kind, name: v.name ?? "", src: v.src ?? "", extra: kind === "Aggregate" ? { fn: v.fn ?? "", second: v.second ?? "" } : undefined })} />
      </div>
    );
  }
  if (kind === "SetProperty") {
    return (
      <div className={s.col}><Text weight="semibold">{slot.name}</Text>
        {list((k) => `${c.name(c.targets(k, "property")[0] ?? k)} = ${c.children(k, "value").map((v) => c.expr(v)).join("")}`)}
        <AddRow cta="Set" fields={[{ key: "property", label: "Property", options: ofKind(g, "PropertyType") }, { key: "src", label: "Value", placeholder: "s.name" }]} onAdd={(v) => run({ t: "set", id, slot: slot.name, property: v.property ?? "", src: v.src ?? "" })} />
      </div>
    );
  }
  if (kind === "PatternNode") {
    return (
      <div className={s.col}><Text weight="semibold">Pattern nodes</Text>
        {list((k) => `${prop(g, k, "var")}: ${c.name(c.targets(k, "ofType")[0] ?? k)}`)}
        <AddRow cta="Add node" fields={[{ key: "variable", label: "Variable" }, { key: "type", label: "Entity type", options: ofKind(g, "EntityType") }]} onAdd={(v) => run({ t: "patternNode", id, variable: v.variable ?? "", type: v.type ?? "" })} />
      </div>
    );
  }
  if (kind === "PatternEdge") {
    const vars = c.children(id, "node").map((n) => ({ value: prop(g, n, "var"), label: prop(g, n, "var") }));
    return (
      <div className={s.col}><Text weight="semibold">Pattern edges</Text>
        {list((k) => `${prop(g, c.targets(k, "from")[0] ?? "", "var")} -[${prop(g, k, "var")}:${c.name(c.targets(k, "ofType")[0] ?? k)}]-> ${prop(g, c.targets(k, "to")[0] ?? "", "var")}`)}
        <AddRow cta="Add edge" fields={[{ key: "variable", label: "Variable (optional)" }, { key: "from", label: "From", options: vars }, { key: "to", label: "To", options: vars }, { key: "type", label: "Relationship type", options: ofKind(g, "RelationshipType") }]}
          onAdd={(v) => run({ t: "patternEdge", id, variable: v.variable ?? "", from: v.from ?? "", to: v.to ?? "", type: v.type ?? "" })} />
      </div>
    );
  }
  if (kind === "RowValue") {
    return (
      <div className={s.col}><Text weight="semibold">{slot.name} (constant rows)</Text>
        {kids.map((k) => <div key={k} className={s.row}><Text className={s.grow}>{c.children(k, "assign").map((a) => `${prop(g, a, "target")}: ${c.children(a, "value").map((v) => c.expr(v)).join("")}`).join(", ")}</Text><Button size="small" icon={<Delete20Regular />} aria-label="Remove row" onClick={() => run({ t: "delete", ids: [k] })} /></div>)}
        <AddRow cta="Add row" fields={[{ key: "cells", label: "Columns, one per line as name: expression", placeholder: "a: 1, b: \"x\"" }]}
          onAdd={(v) => run({ t: "valuesRow", id, cells: (v.cells ?? "").split(/,(?=\s*[A-Za-z_]\w*\s*:)/).map((x) => { const i = x.indexOf(":"); return { name: x.slice(0, i).trim(), src: x.slice(i + 1).trim() }; }) })} />
      </div>
    );
  }
  if (kind === "FieldRef") {
    return (
      <div className={s.col}><Text weight="semibold">{slot.name}</Text>{list((k) => prop(g, k, "path"))}
        <AddRow cta="Add" fields={[{ key: "path", label: "Field", placeholder: "c.flows" }]} onAdd={(v) => run({ t: "read", id, slot: slot.name, path: v.path ?? "" })} />
      </div>
    );
  }
  // a fixpoint: the engine infers the relation's columns from the seed, and its step operators are the siblings between its `out` port and
  // the step result (`over`), listed here and selected together with the loop
  if (kind === "RowType") return <Text size={200}>{slot.name}: the columns are inferred from the input; nothing to enter.</Text>;
  if (kind === "Operator") {
    const loop = (c.projectionGraph(id.slice(0, id.lastIndexOf("/op:"))).loops ?? []).find((l) => l.fixpoint === id);
    return (
      <div className={s.col}><Text weight="semibold">Step operators</Text>
        {loop?.members.length ? loop.members.map((m) => <Text key={m}>{c.name(m)}</Text>) : <Text size={200}>None yet: wire operators from this fixpoint's output and point the step result at the last of them.</Text>}
        {loop && <Button onClick={() => dispatch({ type: "editor/select", ids: [id, ...loop.members] })}>Select the loop</Button>}
      </div>
    );
  }
  return <Text size={200}>{slot.name} ({slot.children.join(", ")}) has no editor in the panel yet.</Text>;
}

/** A rule: class, reason, what it governs, and the program the class allows (its parameters, policies, create key, defaults), all from the palette's rule classes. */
function RuleProgram({ session, id, projection }: { session: EditorSession; id: string; projection: string }) {
  const s = useStyles();
  const g = session.graph, c = session.catalog;
  const cls = prop(g, id, "class");
  const rc = session.palette.ruleClasses.find((r) => r.name === cls);
  const policies = rc?.policies ?? [];
  // the parameters the class defines (putput) and the properties its policies set (accept, onDelete), typed by the palette's rule parameters
  const wanted = new Set([...(rc?.params ?? []), ...policies.map(policyProp)]);
  const ruleParams = (session.palette.rules?.params ?? []).filter((p) => wanted.has(p.name) && p.name !== "reason");
  const governs = c.targets(id, "governs");
  const targets = c.children(projection, "op").concat(c.children(projection, "port"));
  const listOf = (name: string) => { const v = g.entity(id)?.props[name]; return v && v.kind === "list" ? v.items.map(valueText).join(", ") : ""; };
  const setList = (slot: string, title: string) => (
    <div className={s.col}><Text weight="semibold">{title}</Text>
      {c.children(id, slot).map((k) => <div key={k} className={s.row}><Text className={s.grow}>{c.name(c.targets(k, "property")[0] ?? k)} = {c.children(k, "value").map((v) => c.expr(v)).join("")}</Text><Button size="small" icon={<Delete20Regular />} aria-label={`Remove ${c.name(k)}`} onClick={() => run({ t: "delete", ids: [k] })} /></div>)}
      <AddRow cta="Add" fields={[{ key: "property", label: "Property", options: ofKind(g, "PropertyType") }, { key: "src", label: "Value", placeholder: "\"unassigned\"" }]} onAdd={(v) => run({ t: "ruleSet", id, slot, property: v.property ?? "", src: v.src ?? "" })} />
    </div>
  );
  return (
    <div className={s.col}>
      <Field label="Direction class"><Select value={cls} onChange={(_, d) => run({ t: "ruleClass", id, cls: d.value })}>
        {[...new Set([cls, ...session.palette.ruleClasses.map((r) => r.name)])].map((x) => { const r = session.palette.ruleClasses.find((y) => y.name === x); return <option key={x} value={x} disabled={!!r && !r.supported && x !== cls}>{x}{r && !r.supported ? " (not built)" : ""}</option>; })}
      </Select></Field>
      <CommitInput label="Reason" value={prop(g, id, "reason")} onCommit={(v) => run({ t: "ruleProp", id, name: "reason", value: v })} />
      {ruleParams.map((p) => p.kind === "Boolean"
        ? <Switch key={p.name} label={p.name} checked={prop(g, id, p.name) === "true"} onChange={(_, d) => run({ t: "ruleProp", id, name: p.name, value: String(d.checked) })} />
        : p.values
          ? <Field key={p.name} label={p.name}><Select value={prop(g, id, p.name)} onChange={(_, d) => run({ t: "ruleProp", id, name: p.name, value: d.value })}><option value="">(none)</option>{p.values.map((x) => <option key={x}>{x}</option>)}</Select></Field>
          : <CommitInput key={p.name} label={p.kind === "List" ? `${p.name} (comma separated)` : p.name} value={p.kind === "List" ? listOf(p.name) : prop(g, id, p.name)} onCommit={(v) => run({ t: "ruleProp", id, name: p.name, value: v })} />)}
      {policies.length > 0 && (
        <Field label="Create key (identity domain)"><Select value={c.targets(id, "createKey")[0] ?? ""} onChange={(_, d) => run({ t: "createKey", id, domain: d.value })}><option value="">(none)</option>{ofKind(g, "IdentityDomain").map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}</Select></Field>
      )}
      {policies.includes("on create") && setList("createDefault", "Defaults on create")}
      {policies.includes("on delete") && setList("deleteSet", "Set on delete")}
      {(session.palette.rules?.slots ?? []).filter((x) => !["createDefault", "deleteSet"].includes(x.name)).map((x) => <Slot key={x.name} slot={x} op={null as never} c={c} g={g} id={id} session={session} />)}
      <Text weight="semibold">Governs</Text>
      {governs.map((t) => <div key={t} className={s.row}><Text className={s.grow}>{c.name(t)}</Text><Button size="small" icon={<Delete20Regular />} aria-label={`Stop governing ${c.name(t)}`} onClick={() => run({ t: "governs", id, target: t, on: false })} /></div>)}
      <Field label="Govern another"><Select value="" onChange={(_, d) => d.value && run({ t: "governs", id, target: d.value, on: true })}><option value="">(choose)</option>{targets.filter((t) => !governs.includes(t)).map((t) => <option key={t} value={t}>{c.name(t)}</option>)}</Select></Field>
    </div>
  );
}

/** A parameter port: its default, and for each graph the projection produces the literal the space binds (or "uses the default"), all editable. */
function ParamBindings({ session, port }: { session: EditorSession; port: string }) {
  const s = useStyles();
  const g = session.graph, c = session.catalog;
  const bindings = [...g.relationships()].filter((r) => r.type === "bindsPort" && r.target === port).map((r) => r.source);
  const projection = port.slice(0, port.lastIndexOf("/port:"));
  const graphs = [...g.relationships()].filter((r) => r.type === "producedBy" && r.target === projection).map((r) => r.source);
  const bound = new Set(bindings.map((b) => b.slice(0, b.lastIndexOf("/binding:")) + "/graph:" + b.slice(b.lastIndexOf("/binding:") + 9, b.lastIndexOf("."))));
  const kind = c.name([...g.relationships()].find((r) => r.source === port && r.type === "valueKind")?.target ?? "");
  const hasDefault = !!g.entity(port)?.props.default;
  return (
    <div className={s.col}>
      <Text weight="semibold">Parameter ({kind})</Text>
      <CommitInput label="Default (used by a graph that binds no value)" value={prop(g, port, "default")} onCommit={(v) => run({ t: "portDefault", port, value: v })} />
      {bindings.map((b) => (
        <div key={b} className={s.row}>
          <CommitInput label={`Value for graph ${c.name(b.slice(0, b.lastIndexOf(".")))}`} value={prop(g, b, "value")} onCommit={(v) => run({ t: "bindParam", binding: b, value: v })} />
          {hasDefault && <Button size="small" icon={<Delete20Regular />} aria-label={`Use the default for graph ${c.name(b.slice(0, b.lastIndexOf(".")))}`} onClick={() => run({ t: "delete", ids: [b] })} />}
        </div>
      ))}
      {graphs.filter((gr) => !bound.has(gr)).map((gr) => (
        <AddRow key={gr} cta="Bind" fields={[{ key: "value", label: `Graph ${c.name(gr)} uses the default${hasDefault ? ` (${prop(g, port, "default")})` : ""}: bind a value`, placeholder: "value" }]} onAdd={(v) => run({ t: "bindNew", port, graph: gr, value: v.value ?? "" })} />
      ))}
      {graphs.length === 0 && <Text>No graph of the space is produced by this projection.</Text>}
    </div>
  );
}

/** Output ports of the operators that read the fixpoint's `out` (directly or through other operators): where the step can end. */
function outPortsAfter(session: EditorSession, fixpoint: string): { value: string; label: string }[] {
  const g = session.graph, c = session.catalog;
  const owner = (p: string) => p.slice(0, p.lastIndexOf("/"));
  const wires = [...g.relationships()].filter((r) => r.type === "wire");
  const seen = new Set<string>([fixpoint]);
  const stack = [fixpoint];
  while (stack.length) { const x = stack.pop()!; for (const w of wires) if (owner(w.source) === x && !seen.has(owner(w.target))) { seen.add(owner(w.target)); stack.push(owner(w.target)); } }
  seen.delete(fixpoint);
  return [...seen].flatMap((o) => c.children(o, "port").filter((p) => g.entity(p)?.props.direction && valueText(g.entity(p)!.props.direction) === "out").map((p) => ({ value: p, label: `${c.name(o)}.${c.name(p)}` })));
}

export function Properties({ session, selected, projection, diagnostics }: { session: EditorSession; selected: string[]; projection: string; diagnostics: { focus: string; message: string; severity: string }[] }) {
  const s = useStyles();
  const g = session.graph, c = session.catalog;
  const id = selected[0];
  const e = id ? g.entity(id) : undefined;
  const mine = diagnostics.filter((d) => id && (d.focus === id || d.focus.startsWith(`${id}/`)));
  if (!id || !e) {
    const rules = c.projections().find((p) => p.id === projection)?.rules ?? [];
    const ops = c.children(projection, "op").concat(c.children(projection, "port"));
    return (
      <div className={s.col} id="editor-props" tabIndex={-1}>
        <Text weight="semibold" as="h2">Projection {c.name(projection)}</Text>
        <Text>Select an operator to edit its parameters and expressions. Nothing selected: add a rule to this projection.</Text>
        <Text weight="semibold" as="h3">Rules ({rules.length})</Text>
        {rules.map((r) => <Text key={r.id}>{r.name}: {r.direction}{r.reason ? `, ${r.reason}` : ""}</Text>)}
        <AddRow cta="Add rule" fields={[{ key: "name", label: "Name" }, { key: "cls", label: "Direction class", options: session.palette.ruleClasses.filter((r) => r.supported).map((r) => ({ value: r.name, label: r.name })) }, { key: "reason", label: "Reason (optional)" }, { key: "governs", label: "Governs", options: ops.map((o) => ({ value: o, label: c.name(o) })) }]}
          onAdd={(v) => run({ t: "rule", name: v.name ?? "", cls: v.cls ?? "", reason: v.reason ?? "", governs: v.governs ? [v.governs] : [] })} />
      </div>
    );
  }
  const op = session.palette.operators.find((o) => o.kind === e.type);
  const outs = c.children(id, "port").filter((p) => prop(g, p, "direction") === "out");
  const inputs = [...g.entities()].filter((x) => x.type === "Port" && x.id.startsWith(`${projection}/op:`) && prop(g, x.id, "direction") === "in");
  return (
    <div className={s.col} id="editor-props" tabIndex={-1}>
      <Text weight="semibold" as="h2">{e.type} {prop(g, id, "name")}</Text>
      <Text size={200} style={{ wordBreak: "break-all" }}>{id}</Text>
      {e.type === "Port" && prop(g, id, "kind") === "param" && <ParamBindings session={session} port={id} />}
      {op && !op.supported && <MessageBar intent="warning"><MessageBarBody>The engine cannot evaluate {op.kind} yet: {op.unsupportedReason}</MessageBarBody></MessageBar>}
      {mine.map((d, i) => <MessageBar key={i} intent={d.severity === "error" ? "error" : "warning"}><MessageBarBody>{d.message}</MessageBarBody></MessageBar>)}
      {e.type === "Rule" && <RuleProgram session={session} id={id} projection={projection} />}
      {op && (
        <Accordion multiple collapsible defaultOpenItems={["params", "slots", "wires", "refs"]}>
          {op.params.length > 0 && (
            <AccordionItem value="params"><AccordionHeader>Parameters</AccordionHeader><AccordionPanel className={s.col}>
              {op.params.map((p) => p.values
                ? <Field key={p.name} label={`${p.name}${p.required ? " (required)" : ""}${p.default !== undefined ? ` (default ${p.default})` : ""}`}><Select value={prop(g, id, p.name)} onChange={(_, d) => run({ t: "param", id, name: p.name, value: d.value })}><option value="">{p.required ? "(choose)" : "(not set)"}</option>{p.values.map((x) => <option key={x}>{x}</option>)}</Select></Field>
                : <CommitInput key={p.name} label={`${p.name}${isDuration(p) ? ` (duration${p.example ? `, for example ${p.example}` : ""})` : p.unit ? ` (${p.unit})` : ""}${p.required ? " (required)" : ""}${p.default !== undefined ? ` (default ${isDuration(p) ? durationText(p.default) : p.default})` : ""}`} value={isDuration(p) ? durationText(prop(g, id, p.name)) : prop(g, id, p.name)} onCommit={(v) => run({ t: "param", id, name: p.name, value: v })} />)}
            </AccordionPanel></AccordionItem>
          )}
          {refsOf(op.kind).length > 0 && (
            <AccordionItem value="refs"><AccordionHeader>References</AccordionHeader><AccordionPanel className={s.col}>
              {refsOf(op.kind).map((r) => {
                const options = r.to === "OutPort" ? outPortsAfter(session, id) : ofKind(g, r.to);
                return (
                  <Field key={r.rel} label={`${r.label}${r.required ? " (required)" : ""}`}>
                    <Select value={c.targets(id, r.rel)[0] ?? ""} onChange={(_, d) => run({ t: "ref", id, rel: r.rel, target: d.value })}><option value="">{r.required ? "(choose)" : "(none)"}</option>{options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}</Select>
                  </Field>
                );
              })}
              {op.kind === "Fixpoint" && <Button onClick={() => dispatch({ type: "editor/select", ids: (c.projectionGraph(projection).loops ?? []).find((l) => l.fixpoint === id)?.members.concat(id) ?? [id] })}>Select the loop</Button>}
            </AccordionPanel></AccordionItem>
          )}
          <AccordionItem value="slots"><AccordionHeader>Expressions and contents</AccordionHeader><AccordionPanel className={s.col}>
            {op.slots.map((sl) => <Slot key={sl.name} op={op} slot={sl} c={c} g={g} id={id} session={session} />)}
          </AccordionPanel></AccordionItem>
          {outs.length > 0 && (
            <AccordionItem value="wires"><AccordionHeader>Connections</AccordionHeader><AccordionPanel className={s.col}>
              {outs.map((out) => (
                <div key={out} className={s.col}>
                  <Text weight="semibold">output {prop(g, out, "name")}</Text>
                  {c.targets(out, "wire").map((t) => <div key={t} className={s.row}><Text className={s.grow}>→ {c.name(t.slice(0, t.lastIndexOf("/")))}.{prop(g, t, "name")}</Text><Button size="small" icon={<Delete20Regular />} aria-label="Disconnect" onClick={() => run({ t: "unwire", key: `${out}|wire|${t}` })} /></div>)}
                  <Field label="Connect to input"><Select value="" onChange={(_, d) => d.value && run({ t: "wire", from: out, to: d.value })}>
                    <option value="">(choose an input)</option>
                    {inputs.map((i) => { const ok = canWire(g, out, i.id); return <option key={i.id} value={i.id} disabled={!ok.ok}>{c.name(i.id.slice(0, i.id.lastIndexOf("/")))}.{prop(g, i.id, "name")}{ok.ok ? "" : ` (${ok.reason})`}</option>; })}
                  </Select></Field>
                </div>
              ))}
            </AccordionPanel></AccordionItem>
          )}
        </Accordion>
      )}
      <Badge appearance="outline">{selected.length > 1 ? `${selected.length} selected, showing the first` : "1 selected"}</Badge>
    </div>
  );
}
