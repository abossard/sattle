// The five reference projections of the research note (09, section 1), each with the recorded walkthrough that rebuilds it, and how
// a test gets one to build after: the original, and an empty twin of it.
import type { ProjectionDoc } from "../../src/editing/doc";
import { docOf, perform, type DraftPort } from "../../src/editing/draft";
import type { Command } from "../../src/state/types";

export interface Walk { name: string; module: string; original?: string; graphs: string[]; commands: { label: string; cmd: Command }[] }
const files = import.meta.glob("../walkthroughs/*.json", { eager: true, import: "default" }) as Record<string, Walk>;
export const walk = (file: string): Walk => files[`../walkthroughs/${file}.json`];
/** The five reference projections of the research note (09, section 1), by the walkthrough that rebuilds each. */
export const REFERENCES: [string, string][] = [
  ["deployment health", "deployment-health"], ["service tree with lift", "lift-service-tree"], ["health roll-up", "health-rollup"],
  ["series with threshold", "series-threshold-aggregates"], ["fixpoint closure", "fixpoint-closure"],
];

/**
 * The projection a walkthrough is about, and an empty twin of it to build in. A walkthrough that rebuilds a projection of the
 * catalog gives that one; one that builds a new projection is first run as commands, and that result is what the notations must reach.
 */
export async function prepare(d: DraftPort, w: Walk, twin: string): Promise<{ like: ProjectionDoc; projection: string }> {
  const id = (name: string) => `d:${w.module}/projection:${name}`;
  const done = async (projection: string, g: Parameters<typeof perform>[2]) => { const r = await perform(d, projection, g); if (!r.ok) throw new Error(r.error); };
  const commands: { cmd: Command }[] = JSON.parse(JSON.stringify(w.commands));
  // a parameter bound to "$original" takes the value the space binds to the same parameter of the projection it rebuilds
  const bound = (port: string) => { for (const r of d.graph.relationships()) if (r.type === "bindsPort" && r.target === `${id(w.original!)}/port:${port}`) return d.graph.entity(r.source)?.props.value; return undefined; };
  const create = commands[0].cmd;
  if (create.t !== "newProjection") throw new Error("a walkthrough starts with its projection");
  for (const p of create.spec.params) if (p.value === "$original") p.value = String((bound(p.port) as { value: unknown }).value);
  if (!w.original) await done(id(w.name), { g: "batch", label: w.name, items: commands.map(({ cmd }) => ({ g: "do" as const, cmd })) });
  // under the walkthrough's own name the twin is the projection the walkthrough makes, with the graph it names for it
  await done(id(twin), { g: "do", cmd: twin === create.spec.name ? create : { ...create, spec: { ...create.spec, name: twin, graphName: twin } } });
  return { like: docOf(d, id(w.original ?? w.name))!, projection: id(twin) };
}

/** A sink is named by its position among the sinks (`emit.3`); two projections say the same when their sinks do in the same order. */
export const positional = (doc: ProjectionDoc): ProjectionDoc => { let k = 0; return { ...doc, statements: doc.statements.map((st) => (st.sink ? { ...st, name: `sink ${++k}`, steps: st.steps.map((s) => ({ ...s, name: `sink ${k}` })) } : st)) }; };
