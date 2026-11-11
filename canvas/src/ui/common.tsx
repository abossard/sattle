// Small shared pieces: hooks that bridge the runtime to React, shape glyphs, direction badges.
import { Badge, Tooltip } from "@fluentui/react-components";
import { useStore } from "../state/store";
import { runtime } from "../state/runtime";
import { DIRECTION, isDirectionClass } from "../core/catalog";
import type { GraphSummary } from "../state/types";

/** Re-render when the data of `graph` changed. Returns the runtime workspace (null before a source is open). */
export function useWorkspace(graph?: string) {
  useStore((s) => (graph ? s.graphs.find((g) => g.id === graph)?.version : s.generation));
  useStore((s) => s.load.status);
  return runtime.ws;
}
export const useGraphSummary = (graph: string): GraphSummary | undefined => useStore((s) => s.graphs.find((g) => g.id === graph));

const SHAPES: Record<string, string> = {
  circle: "M7 1.5a5.5 5.5 0 1 0 0 11a5.5 5.5 0 1 0 0-11z", square: "M2 2h10v10H2z", diamond: "M7 1l6 6l-6 6l-6-6z", triangle: "M7 1.5l6 10.5H1z",
  hexagon: "M7 1l5.6 3.2v6.4L7 13.8L1.4 10.6V4.2z", card: "M1.5 3h11v8h-11z",
};
/** Shape and color of a type. The shape repeats the type, so color is never the only cue. */
export const TypeGlyph = ({ shape, color, size = 14 }: { shape: string; color: string; size?: number }) => (
  <svg viewBox="0 0 14 14" width={size} height={size} aria-hidden="true" focusable="false"><path d={SHAPES[shape] ?? SHAPES.circle} fill={color} stroke="currentColor" strokeWidth="0.6" /></svg>
);

export const DirectionBadge = ({ direction }: { direction: string }) => {
  const d = isDirectionClass(direction) ? DIRECTION[direction] : direction === "mixed" ? { glyph: "⇅", label: "mixed", help: "Some rules edit backward, others are forward only." } : { glyph: "?", label: direction, help: "unknown direction class" };
  return <Tooltip content={d.help} relationship="description"><Badge appearance="outline" color={direction === "forward" || direction === "readonly" ? "informative" : "brand"}>{d.glyph} {d.label}</Badge></Tooltip>;
};

export const roleColor = (role: string) => (role === "master" ? "success" : role === "catalog" ? "important" : "informative") as "success" | "important" | "informative";
