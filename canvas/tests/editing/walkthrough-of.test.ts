// The walkthroughs that rebuild a projection of the catalog are made from its definition (commands-of.ts): this checks that the
// recorded file is what the catalog gives today. `WRITE_WALKTHROUGHS=1 npx vitest run tests/editing/walkthrough-of.test.ts` writes them again
// after the definitions or the fixture changed (scripts/sync-catalog.mjs).
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { walkthroughOf, type Walk } from "./commands-of";
import { estateCatalog, estatePalette } from "./estate";
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync, writeFileSync } from "node:fs";

const env = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env;
/** Each generated walkthrough: its file, the projection it rebuilds, the twin's name, and what its preview must at least hold. */
const GENERATED: { file: string; original: string; twin: string; atLeast: Record<string, number> }[] = [
  { file: "workload-health", original: "d:acme.workload/projection:workloadHealth", twin: "workloadHealth2", atLeast: { entities: 40, relationships: 40 } },
];

describe.each(GENERATED)("the walkthrough $file", ({ file, original, twin, atLeast }) => {
  it("is what the definition in the catalog gives", () => {
    const cat = new Catalog(estateCatalog());
    const made = walkthroughOf(cat, estatePalette, original, twin);
    const graph = (name: string) => [...cat.g.relationships()].find((r) => r.type === "producedBy" && r.target === `d:${made.module}/projection:${name}`)?.source;
    const walk: Walk = { ...made, graphs: [cat.name(graph(made.original!) ?? ""), twin], nonEmpty: true, atLeast };
    const path = `tests/walkthroughs/${file}.json`, text = `${JSON.stringify(walk, null, 1)}\n`;
    if (env.WRITE_WALKTHROUGHS) writeFileSync(path, text);
    expect(String(readFileSync(path))).toBe(text);
  });
});
