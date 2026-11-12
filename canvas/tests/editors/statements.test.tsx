// @vitest-environment jsdom
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { docOf } from "../../src/editing/draft";
import { withStatements } from "../../src/editing/statements";
import { Bench } from "../../src/editors/Bench";
import { shape } from "../editing/memory-draft";
import { freshDraft, SVC, violations } from "./harness";

const steps = (d: Parameters<typeof docOf>[0], name: string) => docOf(d, SVC)!.statements.find((s) => s.name === name)?.steps ?? [];

describe("statement kinds in the editor", () => {
  it("a row set becomes a kind, gets a parameter in its template, is used again, and the engine sees operators only", async () => {
    const user = userEvent.setup();
    const inner = freshDraft(), before = shape(inner.graph);
    const draft = withStatements(inner);
    render(<FluentProvider theme={webLightTheme}><main style={{ height: 600 }}><Bench draft={draft} projection={SVC} kinds /></main></FluentProvider>);
    const kinds = screen.getByRole("region", { name: "Statement kinds" });
    expect(within(kinds).getByText(/kept in this browser only/)).toBeTruthy();

    // make the kind `pairs` from the row set `deps`
    await user.selectOptions(within(kinds).getByRole("combobox", { name: "Make a kind from the row set" }), "deps");
    await user.type(within(kinds).getByRole("textbox", { name: "named" }), "pairs{Enter}");
    await waitFor(() => expect(steps(draft, "deps").map((s) => s.kind)).toEqual(["pairs"]));
    expect(shape(inner.graph)).toEqual(before);
    await waitFor(() => expect(screen.getAllByRole("treeitem").some((t) => t.getAttribute("data-row-id") === `${SVC}/op:deps` && /pairs/.test(t.textContent ?? ""))).toBe(true));

    // its template opens as a document of its own, in the same editors
    const kind = within(kinds).getByRole("group", { name: "statement kind pairs" });
    await user.click(within(kind).getByRole("button", { name: "Open template" }));
    expect(await screen.findByRole("tree", { name: "Structure of pairs" })).toBeTruthy();
    expect((screen.getByRole("combobox", { name: "Document" }) as HTMLSelectElement).selectedOptions[0].textContent).toBe("pairs");

    // the predicate of its filter becomes the parameter `keep`
    const place = within(kind).getByRole("combobox", { name: "A place of the template" });
    await user.selectOptions(place, within(place).getByRole("option", { name: "filter predicate: s != t" }));
    await user.type(within(kind).getByRole("textbox", { name: "as the parameter" }), "keep");
    await user.click(within(kind).getByRole("button", { name: "Add parameter" }));
    await waitFor(() => expect(within(kind).getByText("(keep)", { exact: false })).toBeTruthy());

    // back in the projection, the kind is a block of the palette; a use of it takes the parameter as a slot
    await user.selectOptions(screen.getByRole("combobox", { name: "Document" }), "serviceDeps");
    await user.click(screen.getByRole("tab", { name: "Blocks" }));
    await user.click(await screen.findByRole("button", { name: "pairs" }));
    await user.click(screen.getByRole("button", { name: "pairs here: a new row set" }));
    await waitFor(() => expect(steps(draft, "rows1").map((s) => s.kind)).toEqual(["pairs"]));
    const use = within(screen.getByRole("group", { name: "row set rows1" }));
    await user.click(use.getByRole("button", { name: "+ keep, edit" }));
    await user.type(use.getByRole("textbox", { name: "keep" }), "s == t{Enter}");
    await waitFor(() => expect(steps(inner, "rows1").map((s) => `${s.kind} ${s.slots.find((x) => x.name === "predicate")?.entries[0]?.expr ?? ""}`.trim())).toEqual(["Match", "Filter s == t"]));
    expect(await violations()).toEqual([]);

    // expanding the first use gives the author its steps back
    await user.click(within(kinds).getByRole("button", { name: "Expand deps" }));
    await waitFor(() => expect(steps(draft, "deps").map((s) => s.kind)).toEqual(["Match", "Filter"]));
  }, 30_000);
});
