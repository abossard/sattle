// One row set at a time, as the steps that make it: the list of row sets on the left with what reads each, the chosen one on the
// right as a card per step, in order, with the rows after each step when the engine returns them. The notation for understanding a
// projection and finding the step that is wrong.
import { MessageBar, MessageBarBody, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useMemo, useState, type ReactElement } from "react";
import { stepHolding } from "../editing/doc";
import type { EditorProps } from "./host";
import { RowLine } from "./parts";
import { kindWord, rowsOf, type Row } from "./rows";
import { StepCard } from "./StepCard";

/** The rows after a step, as the engine samples them. */
export interface StepRows { columns: string[]; rows: string[][]; total: number }
export interface PipelineProps extends EditorProps { /** absent while the engine does not return rows per step */ rowsAfter?: (stepId: string) => StepRows | undefined }

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(180px, 260px) minmax(0, 1fr)", columnGap: tokens.spacingHorizontalL, height: "100%", minHeight: 0 },
  list: { overflow: "auto", minHeight: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  steps: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalM, padding: tokens.spacingHorizontalM },
  tab: { display: "flex", flexDirection: "column", alignItems: "flex-start" },
  result: { overflowX: "auto", borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
});

function Result({ data }: { data: StepRows }): ReactElement {
  return (
    <Table size="extra-small" aria-label={`${data.total} rows after this step`}>
      <TableHeader><TableRow>{data.columns.map((c) => <TableHeaderCell key={c}>{c}</TableHeaderCell>)}</TableRow></TableHeader>
      <TableBody>{data.rows.map((r, i) => <TableRow key={i}>{r.map((v, j) => <TableCell key={j}>{v}</TableCell>)}</TableRow>)}</TableBody>
    </Table>
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
              <Tab key={st.name} value={st.name}>
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
        {!p.rowsAfter && <MessageBar intent="info"><MessageBarBody>This engine does not return the rows after a step, so the steps are shown without their results.</MessageBarBody></MessageBar>}
        {current.steps.map((step, i) => {
          const self = rows.find((r) => r.id === step.id)!;
          const data = p.rowsAfter?.(step.id);
          return (
            <StepCard key={step.id} self={self} inner={byParent.get(step.id) ?? []} label={`Step ${i + 1}: ${kindWord(step.kind)}`} badge={`${i + 1} of ${current.steps.length}`} selected={p.selected === step.id} busy={p.busy} onGesture={p.onGesture}>
              {data && <div className={s.result}><Result data={data} /></div>}
            </StepCard>
          );
        })}
        {addStep && <RowLine row={addStep} busy={p.busy} onGesture={p.onGesture} />}
      </div>
    </div>
  );
}
