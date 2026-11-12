// A page for looking at the editors and measuring them in a browser: the five notations on the demo's `serviceDeps`, on a draft
// that lives in memory (the test stand-in for the engine: it applies edits and checks nothing). Not part of the application.
// Open /tests/editors/page/index.html on the dev server; `?on=blocks` starts in a notation, `?grow=96` adds that many row sets of five
// steps, `?dark` uses the dark theme, `?kinds` puts the draft behind the local stand-in for statement kinds and shows their panel.
import { FluentProvider, webDarkTheme, webLightTheme } from "@fluentui/react-components";
import { createRoot } from "react-dom/client";
import { docOf, perform, type DraftPort } from "../../../src/editing/draft";
import type { Gesture } from "../../../src/editing/gestures";
import { withStatements } from "../../../src/editing/statements";
import { Bench } from "../../../src/editors/Bench";
import type { Notation } from "../../../src/editors/Workbench";
import { demoCatalog, demoData, memoryDraft } from "../../editing/memory-draft";

const SVC = "d:acme.svc/projection:serviceDeps";
const q = new URLSearchParams(location.search);

function Page({ draft }: { draft: DraftPort }) {
  return (
    <FluentProvider theme={q.has("dark") ? webDarkTheme : webLightTheme} style={{ height: "100%" }}>
      <main style={{ height: "100%", padding: 12, boxSizing: "border-box" }}>
        <Bench draft={draft} projection={SVC} examples={{ [`${SVC}/port:master`]: data }} initial={(q.get("on") as Notation) ?? "structure"} kinds={q.has("kinds")} />
      </main>
    </FluentProvider>
  );
}

const data = demoData();
const draft = memoryDraft(demoCatalog());
const sets = Number(q.get("grow") ?? 0);
if (sets) {
  const deps = docOf(draft, SVC)!.statements.find((s) => s.name === "deps")!.steps.at(-1)!.out!;
  const items: Gesture[] = [];
  for (let k = 0; k < sets; k++) for (let i = 0; i < 5; i++) {
    items.push({ g: "addStep", kind: "Filter", statement: `big${k}`, from: i > 0 ? undefined : { in: k % 8 === 0 ? deps : `${SVC}/op:big${k - 1}/port:out` } });
    items.push({ g: "addEntry", step: `${SVC}/op:big${k}`, slot: "predicate", childType: "Expr", values: { src: `s != t and n > ${i}` } });
  }
  await perform(draft, SVC, { g: "batch", label: "grow", items });
}
createRoot(document.getElementById("app")!).render(<Page draft={q.has("kinds") ? withStatements(draft) : draft} />);
