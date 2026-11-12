// One row set at a time, as the steps that make it: the list of row sets on the left with what reads each, the chosen one on the
// right as a card per step, in order, with the rows after each step when the engine returns them. The notation for understanding a
// projection and finding the step that is wrong.
import { Button, Caption1, MessageBar, MessageBarActions, MessageBarBody, Spinner, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useMemo, useState, type ReactElement } from "react";
import { stepHolding } from "../editing/doc";
import type { StepRows } from "../editing/results";
import type { EditorProps } from "./host";
import { RowLine } from "./parts";
import { kindWord, rowsOf, type Row } from "./rows";
import { StepCard } from "./StepCard";

export type { StepRows };
/** Where the rows after each step come from: what the last preview gave, and how to ask for them again. Absent where no engine answers. */
export interface StepResults {
  rowsAfter(stepId: string): StepRows | undefined;
  /** run a preview of the projection's graphs with the rows after each operator */
  load(): void;
  loading: boolean;
  /** what the last preview said instead of rows (a draft that does not check, an engine error) */
  notice: string;
  /** the rows are of an earlier version of the draft */
  stale: boolean;
  /** the columns after a step, from the engine's check of the draft (`name: kind`) */
  columnsAfter(stepId: string): string[] | undefined;
}
export interface PipelineProps extends EditorProps { results?: StepResults }

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(180px, 260px) minmax(0, 1fr)", columnGap: tokens.spacingHorizontalL, height: "100%", minHeight: 0 },
  list: { overflow: "auto", minHeight: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  // a step keeps its height and the list scrolls: a column of cards in a short view would otherwise squeeze each card and cut off its rows
  steps: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalM, padding: tokens.spacingHorizontalM, "> *": { flexShrink: 0 } },
  tab: { display: "flex", flexDirection: "column", alignItems: "flex-start" },
  result: { overflowX: "auto", borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  columns: { fontFamily: tokens.fontFamilyMonospace, color: tokens.colorNeutralForeground3 },
  // the table is as wide as its values, and scrolls sideways in its card when that is wider
  table: { width: "auto", tableLayout: "auto" },
  cell: { whiteSpace: "nowrap" },
});

function Result({ data }: { data: StepRows }): ReactElement {
  const s = useStyles();
  if (!data.total) return <Caption1>No rows after this step.</Caption1>;
  return (
    <>
    <Caption1>{data.complete ? `${data.total} row${data.total === 1 ? "" : "s"} after this step` : `The first ${data.rows.length} of ${data.total} rows after this step`}</Caption1>
    <Table size="extra-small" className={s.table} aria-label={`${data.total} rows after this step`}>
      <TableHeader><TableRow>{data.columns.map((c) => <TableHeaderCell key={c} className={s.cell}>{c}</TableHeaderCell>)}</TableRow></TableHeader>
      <TableBody>{data.rows.map((r, i) => <TableRow key={i}>{r.map((v, j) => <TableCell key={j} className={s.cell}>{v}</TableCell>)}</TableRow>)}</TableBody>
    </Table>
    </>
  );
}

export function PipelineEditor(p: PipelineProps): ReactElement {
  const s = useStyles();
  const rows = useMemo(() => rowsOf(p.doc, p.palette, p.cat), [p.doc, p.palette, p.cat]);
  const byParent = useMemo(() => { const m = new Map<string, Row[]>(); for (const r of rows) if (r.parent) m.set(r.parent, [...(m.get(r.parent) ?? []), r]); return m; }, [rows]);
  // the row set on show: the one that holds the selected element, else the one chosen here, else the first
  const on = stepHolding(p.doc, p.selected);
  const holding = on && p.doc.statements.find((st) => st.steps.includes(on));
  const [chosen, setChosen] = useState("");
  const current = holding ?? p.doc.statements.find((st) => st.name === chosen) ?? p.doc.statements[0];
  const add = rows.find((r) => r.kind === "addStatement");
  if (!current) return <div className={s.steps}>{add && <RowLine row={add} busy={p.busy} onGesture={p.onGesture} />}</div>;
  const sid = `stmt:${current.name}`;
  const head = rows.find((r) => r.id === sid)!;
  const addStep = byParent.get(sid)?.find((r) => r.kind === "addStep");
  return (
    <div className={s.root}>
      <div className={s.list}>
        <TabList vertical size="small" aria-label="Row sets" selectedValue={current.name} onTabSelect={(_, d) => { setChosen(String(d.value)); p.onSelect(p.doc.statements.find((st) => st.name === d.value)?.steps[0].id ?? null); }}>
          {p.doc.statements.map((st) => {
            const problems = st.steps.reduce((n, x) => n + x.missing.length + x.diagnostics.length, 0);
            return (
              <Tab key={st.name} value={st.name} data-row-set={st.name}>
                <span className={s.tab}>
                  <Text weight="semibold">{st.name}</Text>
                  <Text size={200}>{st.steps.map((x) => kindWord(x.kind)).join(" › ")}{st.readers.length ? `, read by ${st.readers.join(", ")}` : ""}{problems ? `, ${problems} to fix` : ""}</Text>
                </span>
              </Tab>
            );
          })}
        </TabList>
        {add && <RowLine row={add} busy={p.busy} onGesture={p.onGesture} />}
      </div>
      <div className={s.steps} role="tabpanel" aria-label={`Steps of ${current.name}`}>
        <RowLine row={head} busy={p.busy} onGesture={p.onGesture} />
        {!p.results && <MessageBar intent="info"><MessageBarBody>The rows after each step come from the engine; this draft has none to ask.</MessageBarBody></MessageBar>}
        {p.results && (
          <MessageBar intent={p.results.notice ? "warning" : "info"}>
            <MessageBarBody>{p.results.loading ? <Spinner size="tiny" label="Running the projection on the data" labelPosition="after" /> : p.results.notice || (p.results.stale ? "The rows below are from before the last change." : "See what each step gives on the data of the space.")}</MessageBarBody>
            <MessageBarActions><Button size="small" disabled={p.results.loading} onClick={p.results.load}>{p.results.stale || p.results.notice ? "Run again" : "Show the rows after each step"}</Button></MessageBarActions>
          </MessageBar>
        )}
        {current.steps.map((step, i) => {
          const self = rows.find((r) => r.id === step.id)!;
          const data = p.results?.rowsAfter(step.id), columns = p.results?.columnsAfter(step.id);
          return (
            <StepCard key={step.id} self={self} inner={byParent.get(step.id) ?? []} label={`Step ${i + 1}: ${kindWord(step.kind)}`} badge={`${i + 1} of ${current.steps.length}`} selected={p.selected === step.id} busy={p.busy} onGesture={p.onGesture}>
              {columns && <Caption1 className={s.columns}>gives {columns.length ? columns.join(", ") : "no columns"}</Caption1>}
              {data && <div className={s.result}><Result data={data} /></div>}
            </StepCard>
          );
        })}
        {addStep && <RowLine row={addStep} busy={p.busy} onGesture={p.onGesture} />}
      </div>
    </div>
  );
}
