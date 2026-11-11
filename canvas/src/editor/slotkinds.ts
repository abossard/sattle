// The child kinds of operator slots the property panel can edit (or, where the engine derives the content, explain). /palette lists the
// slots of every operator; an operator whose slot has a kind outside this list would leave the author with no way to fill it but the
// text view, so the test reads the palette against this list.
export const SLOT_KINDS = ["Expr", "IdOf", "Assign", "Aggregate", "SetProperty", "PatternNode", "PatternEdge", "RowValue", "FieldRef", "Port", "RowType", "Operator", "LiftState"] as const;
export const slotKindHandled = (kind: string | undefined): boolean => (SLOT_KINDS as readonly string[]).includes(kind ?? "");
