import { describe, expect, it } from "vitest";
import { slotKindHandled } from "../src/editor/slotkinds";
import { parsePalette } from "../src/editor/palette";
import raw from "./fixtures/palette.json";

// the engine's own palette (`npm run sync-palette` writes it): a slot of a new kind fails here until the panel can edit it
const palette = parsePalette(raw);
const SLOTS = palette.operators.flatMap((o) => o.slots.map((s) => [o.kind, s.name, s.children[0] ?? ""] as const));

describe("slots the property panel edits", () => {
  it("the palette has operators with slots", () => expect(SLOTS.length).toBeGreaterThan(20));
  it.each(SLOTS)("%s.%s (%s) has an editor, so no slot sends the author to the text view", (_op, _slot, kind) => expect(slotKindHandled(kind)).toBe(true));
  it("a kind nobody wrote an editor for is reported, not guessed", () => expect(slotKindHandled("Mystery")).toBe(false));
});
