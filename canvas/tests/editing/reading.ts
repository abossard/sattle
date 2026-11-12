// What a projection says, without the ids it says it with: the comparison of a projection built again with the one it was built after.
import type { ProjectionDoc } from "../../src/editing/doc";

/** What a projection says, without the ids it says it with: one line per step, field, reference, read and slot. */
export function reading(doc: ProjectionDoc): string[] {
  return doc.statements.flatMap((st) => st.steps.flatMap((s, i) => {
    const at = `${st.name}[${i}] ${s.kind}`;
    return [
      at, ...s.fields.filter((f) => f.value !== "").map((f) => `${at} ${f.name}=${f.value}`), ...s.refs.map((r) => `${at} ${r.rel}->${r.targetName}`), ...s.inputs.map((x) => `${at} ${x.port}<-${x.fromName}`),
      ...s.slots.flatMap((sl) => (sl.ordered ? sl.entries.map((e) => e.text) : sl.entries.map((e) => e.text).sort()).map((t) => `${at} ${sl.name}: ${t}`)),
    ];
  }));
}
export const differing = (a: string[], b: string[]): string[] => [...a.filter((l) => !b.includes(l)).map((l) => `- ${l}`), ...b.filter((l) => !a.includes(l)).map((l) => `+ ${l}`)];

