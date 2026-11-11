// Module-level definitions of the draft: row types with fields, constraints, equivalence policies. Fluent forms over editor commands.
import { Accordion, AccordionHeader, AccordionItem, AccordionPanel, Button, Field, Input, Select, Text, makeStyles, tokens } from "@fluentui/react-components";
import { Delete20Regular } from "@fluentui/react-icons";
import { useState } from "react";
import { valueText } from "../../core/model";
import type { EditorSession } from "../../editor/session";
import { constraintKinds, constraintParams } from "../../editor/structures";
import { dispatch } from "../../state/store";
import type { Command } from "../../state/types";
import { AddRow, ofKind } from "./Properties";

const useStyles = makeStyles({ col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, padding: tokens.spacingHorizontalM }, row: { display: "flex", columnGap: tokens.spacingHorizontalS, alignItems: "center", flexWrap: "wrap" }, grow: { flexGrow: 1 } });
const run = (cmd: Command) => dispatch({ type: "editor/do", cmd });

function ConstraintForm({ session, module }: { session: EditorSession; module: string }) {
  const s = useStyles();
  const g = session.graph;
  const kinds = constraintKinds(session.palette);
  const params = constraintParams(session.palette);
  const [kind, setKind] = useState("");
  const [v, setV] = useState<Record<string, string>>({});
  const [props, setProps] = useState<string[]>([]);
  const k = kinds.find((x) => x.kind === (kind || kinds[0]?.kind));
  if (!k) return <Text>The engine's palette lists no constraint kinds (it needs protocol 3.2).</Text>;
  const set = (key: string, value: string) => setV({ ...v, [key]: value });
  const onRel = k.target === "relationship";
  const targets = [...ofKind(g, "EntityType").map((o) => ({ ...o, group: "entity" })), ...ofKind(g, "RelationshipType").map((o) => ({ ...o, group: "relationship" }))].filter((o) => (onRel ? o.group === "relationship" : k.target === "entity" ? o.group === "entity" : true));
  const label = (p: { name: string }) => ({ values: "Allowed values (comma separated)", min: "Minimum", max: "Maximum", pattern: "Pattern (regular expression)", side: "Side" } as Record<string, string>)[p.name] ?? p.name;
  const uses = params.filter((p) => k.uses.includes(p.name) && p.name !== "onProperty");
  const severity = params.find((p) => p.name === "severity");
  return (
    <div className={s.col}>
      <Field label="Kind" hint={k.text}><Select value={k.kind} onChange={(_, d) => { setKind(d.value); setV({}); setProps([]); }}>{kinds.map((x) => <option key={x.kind}>{x.kind}</option>)}</Select></Field>
      <Field label={`Applies to (${k.target})`}><Select value={v.target ?? ""} onChange={(_, d) => set("target", d.value)}><option value="">(choose)</option>{targets.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}</Select></Field>
      {k.uses.includes("onProperty") && (
        <Field label={k.kind === "key" ? "Properties (the key)" : "Property"}>
          <Select multiple={k.kind === "key"} value={k.kind === "key" ? (props as unknown as string) : props[0] ?? ""} onChange={(e, d) => setProps(k.kind === "key" ? [...(e.target as HTMLSelectElement).selectedOptions].map((o) => o.value) : d.value ? [d.value] : [])}>
            {k.kind !== "key" && <option value="">(choose)</option>}{ofKind(g, "PropertyType").map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </Select>
        </Field>
      )}
      {uses.map((a) => a.values
        ? <Field key={a.name} label={label(a)}><Select value={v[a.name] ?? ""} onChange={(_, d) => set(a.name, d.value)}><option value="">(choose)</option>{a.values.map((o) => <option key={o}>{o}</option>)}</Select></Field>
        : <Field key={a.name} label={label(a)}><Input value={v[a.name] ?? ""} onChange={(_, d) => set(a.name, d.value)} /></Field>)}
      {severity?.values && <Field label="Severity"><Select value={v.severity ?? severity.values[0]} onChange={(_, d) => set("severity", d.value)}>{severity.values.map((o) => <option key={o}>{o}</option>)}</Select></Field>}
      <Button onClick={() => run({ t: "constraint", module, kind: k.kind, target: v.target ?? "", properties: props, severity: v.severity ?? severity?.values?.[0] ?? "error", attrs: v })}>Add constraint</Button>
    </div>
  );
}

/** What the values of a property mean (ok, warning, danger, unknown, neutral): clients color by it, the engine does not read it. */
function ValueRoles({ session, module }: { session: EditorSession; module: string }) {
  const s = useStyles();
  const g = session.graph, c = session.catalog;
  const slot = session.palette.valueRoleSlot;
  const roles = session.palette.valueRoles;
  const [property, setProperty] = useState("");
  if (!slot) return <Text>The engine's palette does not describe value roles (it needs protocol 3.2).</Text>;
  const props = c.children(`d:${module}`, "prop");
  const current = property || props[0] || "";
  return (
    <div className={s.col}>
      <Field label="Property"><Select value={current} onChange={(_, d) => setProperty(d.value)}>{props.map((p) => <option key={p} value={p}>{c.name(p)}</option>)}</Select></Field>
      {current && c.children(current, slot.slot).map((r) => (
        <div key={r} className={s.row}>
          <Text className={s.grow}>{valueText(g.entity(r)!.props.value)}</Text>
          <Select aria-label={`Role of ${valueText(g.entity(r)!.props.value)}`} value={valueText(g.entity(r)!.props.role)} onChange={(_, d) => run({ t: "setValueRole", id: r, role: d.value })}>{roles.map((x) => <option key={x}>{x}</option>)}</Select>
          <Button size="small" icon={<Delete20Regular />} aria-label={`Remove the role of ${valueText(g.entity(r)!.props.value)}`} onClick={() => run({ t: "delete", ids: [r] })} />
        </div>
      ))}
      {current && <AddRow cta="Give a role" fields={[{ key: "value", label: "Value" }, { key: "role", label: "Role", options: roles.map((r) => ({ value: r, label: r })) }]} onAdd={(v) => run({ t: "valueRole", property: current, value: v.value ?? "", role: v.role ?? "" })} />}
    </div>
  );
}

export function Definitions({ session, module }: { session: EditorSession; module: string }) {
  const s = useStyles();
  const g = session.graph, c = session.catalog;
  const mod = `d:${module}`;
  const name = (id: string) => g.entity(id) ? (valueText(g.entity(id)!.props.name ?? { kind: "text", value: id.slice(id.lastIndexOf(":") + 1) })) : id;
  const remove = (id: string, label: string) => <Button size="small" icon={<Delete20Regular />} aria-label={`Remove ${label}`} onClick={() => run({ t: "delete", ids: [id] })} />;
  const rows = c.children(mod, "rows");
  const constraints = c.children(mod, "constraint");
  const policies = c.children(mod, "equivalence");
  const domains = ofKind(g, "IdentityDomain");
  const attrText = (id: string) => ["side", "min", "max", "pattern", "values"].map((a) => { const v = g.entity(id)?.props[a]; return v ? `${a} ${valueText(v)}` : ""; }).filter(Boolean).join(", ");
  return (
    <Accordion multiple collapsible defaultOpenItems={["rows", "constraints", "equivalence", "roles"]}>
      <AccordionItem value="rows"><AccordionHeader>Row types ({rows.length})</AccordionHeader><AccordionPanel className={s.col}>
        {rows.map((r) => (
          <div key={r} className={s.col}>
            <div className={s.row}><Text weight="semibold" className={s.grow}>{name(r)}</Text>{remove(r, name(r))}</div>
            {c.children(r, "field").map((f) => <div key={f} className={s.row}><Text className={s.grow}>{name(f)}: {c.name(c.targets(f, "valueKind")[0] ?? "")}{g.entity(f)?.props.optional && valueText(g.entity(f)!.props.optional) === "true" ? " (optional)" : ""}</Text>{remove(f, name(f))}</div>)}
            <AddRow cta="Add field" fields={[{ key: "name", label: "Field" }, { key: "kind", label: "Kind", options: (session.palette.rowTypes?.field.valueKinds ?? session.palette.valueKinds).map((k) => ({ value: k, label: k })) }, { key: "optional", label: "Optional", options: [{ value: "no", label: "required" }, { value: "yes", label: "optional" }] }]}
              onAdd={(v) => run({ t: "field", rows: r, name: v.name ?? "", kind: v.kind ?? "", optional: v.optional === "yes" })} />
          </div>
        ))}
        <AddRow cta="Add row type" fields={[{ key: "name", label: "Name" }]} onAdd={(v) => run({ t: "rowType", module, name: v.name ?? "" })} />
      </AccordionPanel></AccordionItem>
      <AccordionItem value="constraints"><AccordionHeader>Constraints ({constraints.length})</AccordionHeader><AccordionPanel className={s.col}>
        {constraints.map((k) => <div key={k} className={s.row}><Text className={s.grow}>{name(k)} ({valueText(g.entity(k)!.props.severity ?? { kind: "text", value: "error" })}{attrText(k) ? `, ${attrText(k)}` : ""})</Text>{remove(k, name(k))}</div>)}
        <ConstraintForm session={session} module={module} />
      </AccordionPanel></AccordionItem>
      <AccordionItem value="roles"><AccordionHeader>Value roles</AccordionHeader><AccordionPanel><ValueRoles session={session} module={module} /></AccordionPanel></AccordionItem>
      <AccordionItem value="equivalence"><AccordionHeader>Equivalence policies ({policies.length})</AccordionHeader><AccordionPanel className={s.col}>
        {policies.map((p) => <div key={p} className={s.row}><Text className={s.grow}>{name(p)}: {valueText(g.entity(p)!.props.mode)}, threshold {valueText(g.entity(p)!.props.threshold)}, between {c.targets(p, "between").map((d) => c.name(d)).join(" and ")}</Text>{remove(p, name(p))}</div>)}
        <AddRow cta="Add policy" fields={[{ key: "name", label: "Name" }, { key: "mode", label: "Mode", options: (session.palette.equivalencePolicies?.modes ?? []).map((m) => ({ value: m, label: m })) }, { key: "threshold", label: "Threshold", placeholder: "1" }, { key: "a", label: "Identity domain", options: domains }, { key: "b", label: "and identity domain", options: domains }]}
          onAdd={(v) => run({ t: "equivalence", module, name: v.name ?? "", mode: v.mode ?? "", threshold: v.threshold ?? "", between: [v.a ?? "", v.b ?? ""] })} />
        <Text size={200}>A Link operator applies a policy from its own properties.</Text>
      </AccordionPanel></AccordionItem>
    </Accordion>
  );
}
