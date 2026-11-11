import { describe, expect, it } from "vitest";
import { actionsFor, formatHash, parseHash } from "../src/state/url";

const cur = { source: "engine", view: "data" as const };
describe("url hash", () => {
  it.each([
    ["#source=demo&view=space", cur, { source: "demo", view: "space" }],
    ["#view=editor", cur, { source: "engine", view: "editor" }],
    ["#source=synth-small", cur, { source: "synth-small", view: "data" }],
    ["#source=nope&view=nope", cur, { source: "engine", view: "data" }],
    ["", cur, { source: "engine", view: "data" }],
    ["", undefined, { source: "demo", view: "data" }],
  ])("%j parses to %j", (hash, current, want) => expect(parseHash(hash, current)).toEqual(want));
  it("format and parse are inverse", () => expect(parseHash(formatHash({ source: "synth-100k", view: "ontology" }))).toEqual({ source: "synth-100k", view: "ontology" }));
  it.each([
    [{ source: "engine", view: "editor" as const }, { sourceId: "engine", view: "data" as const }, ["ui/view"]],
    [{ source: "demo", view: "data" as const }, { sourceId: "engine", view: "data" as const }, ["source/opening"]],
    [{ source: "demo", view: "space" as const }, { sourceId: "engine", view: "data" as const }, ["ui/view", "source/opening"]],
    [{ source: "engine", view: "data" as const }, { sourceId: "engine", view: "data" as const }, []],
  ])("route %j against state %j gives %j", (r, s, types) => expect(actionsFor(r, s).map((a) => a.type)).toEqual(types));
});
