// @vitest-environment jsdom
import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { parseIdentity } from "../../src/editor/service";
import { IdentityEffectTable } from "../../src/ui/EditorView";
import { violations } from "./helpers";

const c = (minted: string, aliased: string, merged: string, conflicts: string) => ({ minted, aliased, merged, conflicts });

describe("identity effect of a preview (engine 3.7)", () => {
  it.each([
    ["an older engine answers none", undefined, undefined],
    ["a malformed answer is not guessed", { preview: { minted: "1" }, live: c("1", "0", "0", "0") }, undefined],
    ["counters that are not integer strings are rejected", { preview: c("1", "0", "x", "0"), live: c("1", "0", "0", "0") }, undefined],
    ["a partial preview is not comparable even when a difference is sent", { preview: c("9", "1", "2", "0"), live: c("8", "1", "1", "0"), comparable: false, difference: c("1", "0", "1", "0") }, { preview: c("9", "1", "2", "0"), live: c("8", "1", "1", "0"), comparable: false, difference: c("1", "0", "1", "0") }],
    ["a comparable one carries its difference", { preview: c("9", "1", "2", "0"), live: c("8", "1", "1", "0"), comparable: true, difference: c("1", "0", "1", "0") }, { preview: c("9", "1", "2", "0"), live: c("8", "1", "1", "0"), comparable: true, difference: c("1", "0", "1", "0") }],
    ["comparable without a difference is not comparable", { preview: c("1", "0", "0", "0"), live: c("1", "0", "0", "0"), comparable: true }, { preview: c("1", "0", "0", "0"), live: c("1", "0", "0", "0"), comparable: false }],
  ])("parses: %s", (_, raw, want) => expect(parseIdentity(raw)).toEqual(want));

  it("the table shows preview, live and the signed difference, and has no axe violations", async () => {
    render(<IdentityEffectTable identity={{ preview: c("99", "4", "6", "0"), live: c("94", "4", "1", "0"), comparable: true, difference: c("5", "0", "5", "0") }} />);
    const table = screen.getByRole("table", { name: "Identity effect of the preview" });
    const row = (name: RegExp) => within(table).getByRole("row", { name }).textContent;
    expect(row(/joined by a Link/)).toBe("Identities joined by a Link or late evidence61+5");
    expect(row(/Keys bound/)).toBe("Keys bound to an identity by a Link440");
    expect(await violations()).toEqual([]);
  });

  it("a partial preview says it is not comparable and shows no difference", () => {
    render(<IdentityEffectTable identity={{ preview: c("9", "1", "2", "0"), live: c("8", "1", "1", "0"), comparable: false }} />);
    const table = screen.getByRole("table", { name: "Identity effect of the preview" });
    expect(within(table).getByRole("columnheader", { name: /not comparable/ })).toBeTruthy();
    expect(within(table).getAllByText("n/a")).toHaveLength(4);
  });
});
