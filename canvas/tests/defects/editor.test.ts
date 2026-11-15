// Defects found while rebuilding the projections of the estate catalog in the editor.
import { describe, expect, it } from "vitest";
import type { Edit } from "../../src/core/model";
import { coalesce, ent, text } from "../../src/editor/patches";

describe("edits that leave an element as it was are no edits, whatever the order of its properties", () => {
  const a = ent("x", "T", { slot: text("s"), ordinal: text("1") });
  const b = ent("x", "T", { ordinal: text("1"), slot: text("s") });
  it.each<[string, Edit[], number]>([
    ["the same properties in another key order", [{ kind: "entity", id: "x", before: a.after, after: b.after } as Edit], 0],
    ["a property that really changed", [{ kind: "entity", id: "x", before: a.after, after: ent("x", "T", { slot: text("s"), ordinal: text("2") }).after } as Edit], 1],
  ])("%s", (_, edits, kept) => expect(coalesce(edits).length).toBe(kept));
});
