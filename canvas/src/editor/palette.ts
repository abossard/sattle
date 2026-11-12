// What the editor can place, as the engine describes it (GET /palette): operators with ports, parameters and slots; rule classes; functions.
export interface PalettePort { name: string; direction: "in" | "out"; kind: string }
export interface PaletteParam { name: string; kind: string; required: boolean; unit?: string; values?: string[];
  /** engine 3.6: what the number measures (`duration`: integer nanoseconds on the wire, `count`, `value`, `ratio`), the text form of a duration and an example */
  quantity?: string; literal?: string; example?: string;
  /** engine 3.13: the value the text form has when the parameter is not written (`24h` as nanoseconds, `drop`); absent when there is none */
  default?: string }
/** engine 3.13: what a child type of a slot is made of, and what a child that holds a path or an expression offers (`fieldRef` or `expr`, the roots it may start with) */
/**
 * engine 3.21: a reference the kernel declares from a node to another definition element (`ofType` of a pattern node to an entity type, say):
 * the types it may point at, whether one is needed and how many, and where the target is found (`outputOntology`, `inputGraph`, `module`, `pattern`,
 * `domain`, `emittedType`, `kernel`, `step`, `projection`).
 */
export interface PaletteReference { name: string; targets: string[]; required: boolean; multiplicity: "1" | "?" | "+"; scope: string; meaning: string }
export interface PaletteChildType { type: string; params: PaletteParam[]; references?: PaletteReference[] }
export interface PaletteSlotValue { kind: "fieldRef" | "expr"; roots: string[]; offers: string }
export interface PaletteSlot { name: string; children: string[]; multiplicity: string; ordered: boolean; childTypes?: PaletteChildType[]; value?: PaletteSlotValue }
export interface PaletteOperator {
  kind: string; supported: boolean; unsupportedReason?: string; role: string; container: string; slot: string;
  ports: PalettePort[]; params: PaletteParam[]; slots: PaletteSlot[]; strategy: string | null; sink: boolean; telemetry: boolean;
  /** engine 3.21: the references of the operator (empty when it has none; absent from an older engine) */
  references?: PaletteReference[];
}
/** One module-level declaration the kernel allows (entity, rel, rows, constraint, ...): its slot, node type and parameters. */
export interface PaletteDeclaration {
  slot: string; type: string; container: string; params: PaletteParam[]; slots: PaletteSlot[];
  /** constraints: the kinds with the target they constrain and the properties of the constraint they use */
  kinds?: { kind: string; target: string; uses: string[]; text: string }[] | null;
}
export interface AggregateContext { operator: string; inputs: string; output: string; requires: string | null }
export interface AggregateSpec { name: string; supported: boolean; contexts?: AggregateContext[] }

/** Whether `aggregate` may be used in an operator of kind `operator`, by the palette's own declaration; allowed when the palette declares no contexts. */
export function aggregateAllowed(p: Palette, aggregate: string, operator: string): { ok: true; requires: string | null } | { ok: false; reason: string } {
  const a = p.aggregates.find((x) => x.name === aggregate);
  if (!a) return { ok: false, reason: `unknown aggregate ${aggregate}` };
  if (!a.supported) return { ok: false, reason: `${aggregate} is not supported` };
  if (!a.contexts) return { ok: true, requires: null };
  const c = a.contexts.find((x) => x.operator === operator);
  return c ? { ok: true, requires: c.requires } : { ok: false, reason: `${aggregate} is allowed only in ${a.contexts.map((x) => x.operator).join(" or ")}` };
}

export interface Palette {
  /** engine 3.2: what a module can declare, derived from the kernel; absent from older engines */
  declarations: Record<string, PaletteDeclaration>;
  rowTypes?: { type: string; fieldSlot: string; field: { type: string; params: PaletteParam[]; valueKinds: string[] } };
  equivalencePolicies?: { type: string; modes: string[]; params: PaletteParam[] };
  projectionPorts: { kind: string; text: string }[];
  rules?: { type: string; params: PaletteParam[]; slots: PaletteSlot[]; onDelete: string[]; references?: PaletteReference[] };
  /** engine 3.21: the references of every node type, keyed by the type (the same entries the operators, child types and rules carry); absent from an older engine */
  references?: Record<string, PaletteReference[]>;
  valueRoles: string[];
  valueRoleSlot?: { container: string; slot: string; type: string };
  kernelDigest: string; version: string; operators: PaletteOperator[];
  ruleClasses: { name: string; params: string[]; policies: string[]; supported: boolean }[];
  /** `contexts` (engine 3.5): where the aggregate is allowed: the operator (Group over data, Rollup over telemetry), what it takes, and what it requires of it. Absent on an older engine. */
  aggregates: AggregateSpec[];
  functions: { name: string; signature: string; role: string; supported: boolean }[];
  valueKinds: string[];
}

/** References of a palette entry as the engine sends them; an entry without a name or targets is left out, a multiplicity it does not know reads as "?". */
export function parseReferences(list: unknown): PaletteReference[] {
  return (Array.isArray(list) ? list : []).flatMap((x): PaletteReference[] => {
    const o = x as Record<string, unknown> | null;
    if (!o || typeof o.name !== "string" || !Array.isArray(o.targets)) return [];
    const m = String(o.multiplicity ?? "?");
    return [{ name: o.name, targets: o.targets.map(String), required: o.required === true, multiplicity: m === "1" || m === "+" ? m : "?", scope: String(o.scope ?? ""), meaning: String(o.meaning ?? "") }];
  });
}

export function parsePalette(raw0: unknown): Palette {
  const o = raw0 as Partial<Palette> | null;
  if (!o || !Array.isArray(o.operators)) throw new Error("GET /palette: no operators");
  const raw = raw0 as { declarations?: PaletteDeclaration[]; rowTypes?: Palette["rowTypes"]; equivalencePolicies?: Palette["equivalencePolicies"]; projectionPorts?: Palette["projectionPorts"]; rules?: Palette["rules"]; references?: unknown; valueRoles?: string[]; valueRoleSlot?: Palette["valueRoleSlot"] };
  return { declarations: Object.fromEntries((raw.declarations ?? []).map((d) => [d.slot, d])), rowTypes: raw.rowTypes, equivalencePolicies: raw.equivalencePolicies, projectionPorts: raw.projectionPorts ?? [], rules: raw.rules, valueRoles: raw.valueRoles ?? [], valueRoleSlot: raw.valueRoleSlot, ...(raw.references && typeof raw.references === "object" ? { references: Object.fromEntries(Object.entries(raw.references as Record<string, unknown>).map(([t, l]) => [t, parseReferences(l)])) } : {}), kernelDigest: String(o.kernelDigest ?? ""), version: String(o.version ?? ""), operators: o.operators, ruleClasses: o.ruleClasses ?? [], aggregates: o.aggregates ?? [], functions: o.functions ?? [], valueKinds: o.valueKinds ?? ["Text", "Integer", "Decimal", "Boolean", "Instant", "Entity", "List"] };
}

/** Groups in the order an author builds a projection: sources, linear steps, telemetry steps, sinks. */
export const ROLE_ORDER = ["source", "linear", "telemetry", "lift", "sink"];
export const ROLE_LABEL: Record<string, string> = { source: "Sources", linear: "Transform", telemetry: "Telemetry", lift: "Lift to a higher level", sink: "Emit" };
export const scalarFunctions = (p: Palette): Set<string> => new Set(p.functions.filter((f) => f.role === "scalar").map((f) => f.name));

/** The palette's description of a parameter of an operator, or of a child of one of its slots (a `LiftState` has nine): found by the owner's node type. */
export function paramOf(p: Palette, ownerType: string, name: string): PaletteParam | undefined {
  const op = p.operators.find((o) => o.kind === ownerType);
  if (op) return op.params.find((x) => x.name === name);
  for (const o of p.operators) for (const sl of o.slots) { const ct = sl.childTypes?.find((c) => c.type === ownerType); if (ct) return ct.params.find((x) => x.name === name); }
  return undefined;
}
/** The slot of an operator kind, with the child types and value kind the palette gives it. */
export const slotOf = (p: Palette, opKind: string, slot: string): PaletteSlot | undefined => p.operators.find((o) => o.kind === opKind)?.slots.find((x) => x.name === slot);
