// The editors on a draft, as one piece: which document is open (a projection, or the template of a statement kind), the five
// notations on it, what the draft answered, and the statement kinds when the draft can hold them.
import { Field, MessageBar, MessageBarBody, Select, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { useMemo, useState, type ReactElement } from "react";
import type { ExampleData } from "./ExampleEditor";
import type { DraftPort } from "../editing/draft";
import { useDraft } from "./host";
import { StatementKinds } from "./StatementKinds";
import { Workbench, type Notation } from "./Workbench";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "auto minmax(0, 1fr)", rowGap: tokens.spacingVerticalS, height: "100%", minHeight: 0 },
  withKinds: { gridTemplateColumns: "minmax(0, 1fr) minmax(280px, 360px)", columnGap: tokens.spacingHorizontalL },
  head: { gridColumn: "1 / -1", display: "flex", alignItems: "flex-end", columnGap: tokens.spacingHorizontalM },
  grow: { flexGrow: 1 },
});

export interface BenchProps {
  draft: DraftPort; projection: string;
  examples?: Readonly<Record<string, ExampleData>>;
  /** show the statement kinds: for a draft that can hold them (editing/statements.ts) */
  kinds?: boolean;
  initial?: Notation;
}

export function Bench(p: BenchProps): ReactElement {
  const s = useStyles();
  const [open, setOpen] = useState(p.projection);
  const { props, notice } = useDraft(p.draft, open);
  const documents = useMemo(() => (props ? props.cat.projections().filter((x) => x.module === props.doc.module).map((x) => ({ id: x.id, name: x.name })) : []), [props?.cat, props?.doc.module]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className={mergeClasses(s.root, p.kinds && s.withKinds)}>
      <div className={s.head}>
        <Field label="Document" size="small" orientation="horizontal"><Select size="small" value={open} onChange={(_, d) => setOpen(d.value)}>{documents.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</Select></Field>
        <div className={s.grow} role="status">{notice && <MessageBar intent="warning"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}</div>
      </div>
      {props && <Workbench {...props} examples={p.examples} initial={p.initial} />}
      {props && p.kinds && <StatementKinds {...props} onOpen={setOpen} />}
    </div>
  );
}
