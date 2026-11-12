// Renders one editor on a draft in memory, the way the application will: the editor gets the document and emits gestures.
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render } from "@testing-library/react";
import axe from "axe-core";
import type { ComponentType, ReactElement } from "react";
import { afterEach } from "vitest";
import { Graph } from "../../src/core/graph";
import { useDraft, type EditorProps } from "../../src/editors/host";
import { demoCatalog, memoryDraft, type MemoryDraft } from "../editing/memory-draft";

afterEach(cleanup);

// jsdom lays nothing out, so an element has no size. The editors mount only what is in view; here the view is large enough for a
// whole document, and the tests that are about the window say its size themselves.
for (const side of ["clientWidth", "clientHeight"] as const) Object.defineProperty(HTMLElement.prototype, side, { configurable: true, get: () => 1_000_000 });

export const SVC = "d:acme.svc/projection:serviceDeps", OPS = "d:acme.ops/projection:ingest";
const base = demoCatalog();
export const freshDraft = (): MemoryDraft => memoryDraft(Graph.fromSnapshot(base.toSnapshot()));

function Host({ draft, projection, Editor }: { draft: MemoryDraft; projection: string; Editor: ComponentType<EditorProps> }): ReactElement {
  const { props, notice } = useDraft(draft, projection);
  return (
    <FluentProvider theme={webLightTheme}>
      <main style={{ height: 600 }}>{props && <Editor {...props} />}</main>
      <p role="status">{notice}</p>
    </FluentProvider>
  );
}

export function mount(Editor: ComponentType<EditorProps>, projection = SVC, draft = freshDraft()) {
  return { draft, ...render(<Host draft={draft} projection={projection} Editor={Editor} />) };
}

/** axe violations of the page; colour contrast needs real rendering and is checked in a browser. */
export async function violations(root: Element = document.body): Promise<string[]> {
  const r = await axe.run(root, { rules: { "color-contrast": { enabled: false }, region: { enabled: false } } });
  return r.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.slice(0, 3).map((n) => n.target.join(" ")).join(" | ")})`);
}
