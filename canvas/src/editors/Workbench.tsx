// The five editors on one document: the author changes the notation, not the draft. Selection goes along from one to the next,
// so a step chosen as a card is the step open as a block.
import { Tab, TabList, makeStyles, tokens } from "@fluentui/react-components";
import { useState, type ReactElement } from "react";
import { BlockEditor } from "./BlockEditor";
import { ExampleEditor, type ExampleData } from "./ExampleEditor";
import { GraphEditor } from "./GraphEditor";
import type { EditorProps } from "./host";
import { PipelineEditor, type StepResults } from "./PipelineEditor";
import { StructureEditor } from "./StructureEditor";

export const NOTATIONS = [
  { id: "structure", label: "Structure", hint: "the whole projection as an outline, keyboard first" },
  { id: "pipeline", label: "Steps", hint: "one row set at a time, step by step" },
  { id: "blocks", label: "Blocks", hint: "stacks of blocks from a palette" },
  { id: "graph", label: "Wires", hint: "cards and wires, laid out by data flow" },
  { id: "example", label: "By example", hint: "a pattern from elements picked in the data" },
] as const;
export type Notation = (typeof NOTATIONS)[number]["id"];

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr)", rowGap: tokens.spacingVerticalS, height: "100%", minHeight: 0 },
  body: { minHeight: 0, overflow: "hidden" },
});

export interface WorkbenchProps extends EditorProps {
  /** data graphs the projection reads, by graph input: what the example notation picks from */
  examples?: Readonly<Record<string, ExampleData>>;
  /** the rows after each step, where an engine answers */
  results?: StepResults;
  initial?: Notation;
}

export function Workbench({ examples, results, initial, ...p }: WorkbenchProps): ReactElement {
  const s = useStyles();
  const [on, setOn] = useState<Notation>(initial ?? "structure");
  return (
    <div className={s.root}>
      <TabList size="small" selectedValue={on} onTabSelect={(_, d) => setOn(d.value as Notation)} aria-label={`Notation for ${p.doc.name}`}>
        {NOTATIONS.map((n) => <Tab key={n.id} value={n.id} title={n.hint}>{n.label}</Tab>)}
      </TabList>
      <div className={s.body} role="tabpanel" aria-label={NOTATIONS.find((n) => n.id === on)!.label}>
        {on === "structure" && <StructureEditor {...p} />}
        {on === "pipeline" && <PipelineEditor {...p} results={results} />}
        {on === "blocks" && <BlockEditor {...p} />}
        {on === "graph" && <GraphEditor {...p} />}
        {on === "example" && <ExampleEditor {...p} examples={examples ?? {}} />}
      </div>
    </div>
  );
}
