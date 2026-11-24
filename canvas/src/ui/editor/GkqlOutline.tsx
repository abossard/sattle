// The outline of a GKQL program: its verbs as a step list in reading order (inputs first, outputs last), each with what it reads and its
// problems. Arrow keys, Home and End move the selection; Delete (the editor's) removes the step. A pure view of the editor's model, like the graph.
import { Badge, Text, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { useMemo, useRef, type KeyboardEvent } from "react";
import { layoutGkql, verbFocus, wirePort } from "../../editor/gkql";
import type { GkqlEvent, GkqlModel } from "../../editor/gkql-editing";

const useStyles = makeStyles({
  list: { listStyle: "none", margin: 0, padding: "4px 0", overflow: "auto", minHeight: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" } },
  row: {
    display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", alignItems: "center", columnGap: "6px", padding: "4px 8px", cursor: "pointer",
    borderLeft: "2px solid transparent", "&:hover": { backgroundColor: tokens.colorNeutralBackground1Hover },
    "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" },
  },
  selected: { backgroundColor: tokens.colorBrandBackground2, borderLeft: `2px solid ${tokens.colorBrandStroke1}` },
  id: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  reads: { gridColumn: "1 / -1", color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  error: { gridColumn: "1 / -1", color: tokens.colorPaletteRedForeground1, fontSize: tokens.fontSizeBase100 },
});

export function GkqlOutline({ model, problems, dispatch }: {
  model: GkqlModel; problems: ReadonlyMap<string, string[]>; dispatch: (e: GkqlEvent) => void;
}) {
  const s = useStyles();
  const program = model.program!;
  const list = useRef<HTMLUListElement>(null);
  // reading order: by column of the layout, then by row
  const steps = useMemo(() => layoutGkql(program).cards.slice().sort((a, b) => a.x - b.x || a.y - b.y).map((c) => c.verb), [program]);
  const at = steps.findIndex((v) => verbFocus(v.id) === model.selected);
  const go = (index: number) => {
    const verb = steps[Math.max(0, Math.min(steps.length - 1, index))];
    if (!verb) return;
    dispatch({ t: "select", focus: verbFocus(verb.id) });
    [...list.current?.querySelectorAll<HTMLElement>("[data-step]") ?? []].find((e) => e.dataset.step === verb.id)?.focus();
  };
  const key = (e: KeyboardEvent) => {
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    // from the focused step, or the selected one when the list itself has focus
    const focused = steps.findIndex((v) => v.id === (e.target as HTMLElement).dataset.step);
    const from = focused >= 0 ? focused : at;
    const moves: Record<string, number> = { ArrowDown: from + 1, ArrowUp: Math.max(0, from - 1), Home: 0, End: steps.length - 1 };
    if (e.key in moves) { e.preventDefault(); go(moves[e.key]); }
  };
  return (
    <ul ref={list} className={s.list} role="listbox" aria-label="Steps" tabIndex={-1} onKeyDown={key}>
      {steps.map((verb, index) => {
        const focus = verbFocus(verb.id), errors = problems.get(focus) ?? [];
        const reads = program.wires.filter((w) => w.to === verb.id).map((w) => wirePort(w) === "in" ? w.from : `${wirePort(w)}: ${w.from}`);
        const current = at < 0 ? index === 0 : index === at;
        return (
          <li key={steps.findIndex((v) => v.id === verb.id) === index ? verb.id : `${verb.id}/${index}`} data-step={verb.id} role="option" aria-selected={model.selected === focus} tabIndex={current ? 0 : -1}
            className={mergeClasses(s.row, model.selected === focus && s.selected)} onClick={() => dispatch({ t: "select", focus })}>
            <Text className={s.id} title={verb.id}>{verb.id}</Text>
            <Badge size="small" appearance="outline" color="informative">{verb.verb}</Badge>
            {reads.length > 0 && <Text className={s.reads}>reads {reads.join(", ")}</Text>}
            {errors.length > 0 && <Text className={s.error}>{errors.length === 1 ? errors[0] : `${errors.length} problems`}</Text>}
          </li>
        );
      })}
    </ul>
  );
}
