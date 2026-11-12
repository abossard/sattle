// One step with everything in it, as lines an author can edit: the card of the pipeline editor and the inspector of the graph editor.
import { Badge, Card } from "@fluentui/react-components";
import type { ReactElement, ReactNode } from "react";
import type { Gesture } from "../editing/gestures";
import { RowLine } from "./parts";
import type { Row } from "./rows";

export interface StepCardProps {
  /** the row of the step and the rows in it */
  self: Row; inner: Row[];
  label: string; badge?: string; selected: boolean; busy: boolean;
  onGesture(g: Gesture): void;
  children?: ReactNode;
}

export function StepCard(p: StepCardProps): ReactElement {
  return (
    <Card size="small" aria-label={p.label} role="group" appearance={p.selected ? "filled" : "outline"}>
      <RowLine row={p.self} busy={p.busy} onGesture={p.onGesture} extra={p.badge ? <Badge appearance="tint" size="small">{p.badge}</Badge> : undefined} />
      {p.inner.map((r) => <RowLine key={r.id} row={r} busy={p.busy} onGesture={p.onGesture} />)}
      {p.children}
    </Card>
  );
}
