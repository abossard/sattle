import { describe, expect, it } from "vitest";
import patchFx from "../../brainstorm/contracts/examples/graph-patch.json";
import ontologyFx from "../../brainstorm/contracts/examples/ontology.json";
import ontologyBusinessFx from "../../brainstorm/contracts/examples/ontology-business.json";
import projectionFx from "../../brainstorm/contracts/examples/graph-projection.json";
import spaceFx from "../../brainstorm/contracts/examples/graph-space.json";
import { checkDescriptor, checkGraphSpace, checkOntology, checkPatch, checkProjection } from "../src/core/validate";

const fixtures: Record<string, unknown> = { "graph-patch.json": patchFx, "ontology.json": ontologyFx, "ontology-business.json": ontologyBusinessFx, "graph-projection.json": projectionFx, "graph-space.json": spaceFx };
const load = (f: string) => fixtures[f];
const clone = <T>(x: T): T => JSON.parse(JSON.stringify(x));

describe("schema validation of repo fixtures", () => {
  it.each([
    ["graph-patch.json", checkPatch],
    ["ontology.json", checkOntology],
    ["ontology-business.json", checkOntology],
    ["graph-projection.json", checkProjection],
    ["graph-space.json", checkGraphSpace],
  ])("%s is accepted", (file, check) => expect(check(load(file)).ok).toBe(true));
});

describe("rejections carry readable errors", () => {
  const patch = () => load("graph-patch.json");
  it.each([
    ["unknown field", (p: any) => { p.unexpected = 1; }, "unexpected"],
    ["unsupported schema version", (p: any) => { p.schemaVersion = "999"; }, "schemaVersion"],
    ["unknown edit kind", (p: any) => { p.edits[0].kind = "invented"; }, "edits"],
    ["empty scoped id", (p: any) => { p.from.tenant.value = ""; }, "/from/tenant/value"],
    ["missing cause", (p: any) => { delete p.cause; }, "cause"],
  ])("patch with %s", (_n, mutate, fragment) => {
    const p = clone(patch());
    mutate(p);
    const r = checkPatch(p);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.errors.join("\n")).toContain(fragment);
  });

  it("descriptor requires ontology", () => {
    expect(checkDescriptor({ version: { tenant: { value: "t" }, graph: { value: "g" }, revision: { value: "1" } }, projection: { value: "p" }, virtualRegions: [] }).ok).toBe(false);
  });
});
