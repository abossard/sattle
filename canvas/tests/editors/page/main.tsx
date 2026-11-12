// A page for looking at the editors and measuring them in a browser: the five notations on `serviceDeps` of the synthetic estate
// as a current engine serves its catalog (tests/fixtures/catalog.estate.json.gz), on a draft that lives in memory (the test
// stand-in for the engine: it applies edits and checks nothing). The data for "By example" is the estate's master graph, named and
// drawn as the application does. Not part of the application.
// Open /tests/editors/page/index.html on the dev server. `?on=blocks` starts in a notation, `?theme=dark` or `contrast` changes
// the theme, `?grow=96` adds that many row sets of five steps, `?kinds` puts the draft behind the local stand-in for statement kinds.
import { FluentProvider, teamsHighContrastTheme, webDarkTheme, webLightTheme } from "@fluentui/react-components";
import { createRoot } from "react-dom/client";
import { Graph } from "../../../src/core/graph";
import { nameGraph } from "../../../src/core/naming";
import { checkDescriptor } from "../../../src/core/validate";
import { snapshotFromWire } from "../../../src/core/wire";
import type { GraphExtentWire } from "../../../src/core/wire-types";
import { docOf, perform, type DraftPort } from "../../../src/editing/draft";
import type { Gesture } from "../../../src/editing/gestures";
import { withStatements } from "../../../src/editing/statements";
import { parsePalette } from "../../../src/editor/palette";
import { Bench } from "../../../src/editors/Bench";
import { dataDrawing } from "../../../src/editors/data-drawing";
import type { ExampleData } from "../../../src/editors/ExampleEditor";
import type { Notation } from "../../../src/editors/Workbench";
import { StaticSource } from "../../../src/source/static";
import { rendererTheme } from "../../../src/state/effects";
import type { ThemeName } from "../../../src/state/types";
import palette from "../../fixtures/catalog.estate.palette.json";
import { memoryDraft } from "../../editing/memory-draft";

const SVC = "d:acme.svc/projection:serviceDeps", MASTER = `${SVC}/port:master`;
const q = new URLSearchParams(location.search);
// the three themes of the application (ui/App.tsx)
const THEMES = { light: webLightTheme, dark: webDarkTheme, contrast: teamsHighContrastTheme };
const theme = (q.get("theme") ?? "light") as ThemeName;

/** A graph from a snapshot file. A gzipped file is unpacked here unless the server already said it is gzipped and the browser unpacked it. */
async function graphOf(url: string): Promise<Graph> {
  const bytes = new Uint8Array(await (await fetch(url)).arrayBuffer());
  const packed = bytes[0] === 0x1f && bytes[1] === 0x8b;
  const text = await new Response(packed ? new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip")) : bytes).text();
  const file = JSON.parse(text) as { descriptor: unknown; extent: GraphExtentWire };
  const d = checkDescriptor(file.descriptor);
  if (!d.ok) throw new Error(d.errors.join());
  return Graph.fromSnapshot(snapshotFromWire(d.value, file.extent));
}

function Page({ draft, examples }: { draft: DraftPort; examples: Record<string, ExampleData> }) {
  return (
    <FluentProvider theme={THEMES[theme]} style={{ height: "100%" }}>
      <main style={{ height: "100%", padding: 12, boxSizing: "border-box" }}>
        <Bench draft={draft} projection={SVC} examples={examples} initial={(q.get("on") as Notation) ?? "structure"} kinds={q.has("kinds")} />
      </main>
    </FluentProvider>
  );
}

const draft = memoryDraft(await graphOf("/tests/fixtures/catalog.estate.json.gz"), parsePalette(palette));
// the master graph of the estate with the names and the drawing of the application
const data = await graphOf("/demo/estate/master.final.snapshot.json");
const info = (await new StaticSource("Synthetic estate", "/demo/estate/manifest.json").open()).graphs.find((g) => g.id === "master")!;
nameGraph(data, info.ontology, info.labels);
const examples = { [MASTER]: { graph: data, draw: dataDrawing(info, data, rendererTheme(theme)) } };

const sets = Number(q.get("grow") ?? 0);
if (sets) {
  const services = docOf(draft, SVC)!.statements.find((s) => s.name === "services")!.steps.at(-1)!.out!;
  const items: Gesture[] = [];
  for (let k = 0; k < sets; k++) for (let i = 0; i < 5; i++) {
    items.push({ g: "addStep", kind: "Filter", statement: `big${k}`, from: i > 0 ? undefined : { in: k % 8 === 0 ? services : `${SVC}/op:big${k - 1}/port:out` } });
    items.push({ g: "addEntry", step: `${SVC}/op:big${k}`, slot: "predicate", childType: "Expr", values: { src: `s.name != "" and ${i} < 5` } });
  }
  await perform(draft, SVC, { g: "batch", label: "grow", items });
}
createRoot(document.getElementById("app")!).render(<Page draft={q.has("kinds") ? withStatements(draft) : draft} examples={examples} />);
