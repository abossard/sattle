// @vitest-environment jsdom
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import { runtime } from "../../src/state/runtime";
import { ElementsPanel } from "../../src/ui/Elements";
import { ont } from "../fixtures/engine-fake";
import { ent, fakeWorkspace } from "../helpers";

afterEach(() => { cleanup(); runtime.ws = null; });

describe("the element list sorts all elements of the graph, not only the rows it shows", () => {
  it("sorting by name puts the first name of all elements on top, wherever its slot is", async () => {
    const g = Graph.fromSnapshot({
      version: { tenant: "demo", graph: "g", revision: "1" },
      entities: [...Array.from({ length: 400 }, (_, i) => ent(`n${i}`, "Service", { name: `m${String(i).padStart(3, "0")}` })), ent("last", "Service", { name: "a-first-by-name" })],
      relationships: [],
    });
    runtime.ws = { ...fakeWorkspace({ g: { graph: g } }), graphInfo: () => ({ id: "g", role: "master", descriptor: {}, ontology: ont("o"), inputs: [] }) } as never;
    const user = userEvent.setup();
    render(<FluentProvider theme={webLightTheme}><ElementsPanel graph="g" /></FluentProvider>);
    await user.click(await screen.findByRole("button", { name: /^Name/ }));
    const first = screen.getAllByRole("row")[1];
    expect(first.textContent, "the 401st element sorts first by name but was cut before the grid saw it").toMatch(/a-first-by-name/);
  });
});
