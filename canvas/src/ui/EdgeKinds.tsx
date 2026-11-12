// What the four kinds of edge look like and are called (engine 3.17). The pattern of the line and the shape of the head say the kind; color does not.
import type { EdgeKind } from "../source/presentation";
import { KIND_EDGE_COLOR } from "../render/theme";

export const KIND_WORD: Record<EdgeKind, { word: string; help: string }> = {
  composition: { word: "Composition", help: "one end is a part of the other or is carried by it: solid line, diamond head" },
  dependency: { word: "Dependency", help: "the source needs the target, so a failure of the target reaches the source: dashed and dotted line, narrow head" },
  communication: { word: "Communication", help: "the source was observed talking to the target: dotted line, triangle head" },
  support: { word: "Support", help: "evidence, measurements and rules that explain an element: thin faint line, no head" },
};

/** The line and the head of a kind in a small box, in the pattern the canvas draws them. Decorative: the words beside it carry the meaning. */
export function KindSample({ kind, width = 44 }: { kind: EdgeKind; width?: number }) {
  const y = 8, end = width - 9;
  const line = { composition: { w: 2, dash: undefined, op: 1 }, dependency: { w: 1.5, dash: "7 2 2 3", op: 1 }, communication: { w: 1.5, dash: "1.5 3.5", op: 1 }, support: { w: 1, dash: undefined, op: 0.55 } }[kind];
  return (
    <svg width={width} height={16} viewBox={`0 0 ${width} 16`} aria-hidden focusable="false" style={{ flex: "none", color: KIND_EDGE_COLOR[kind] }}>
      <line x1={1} y1={y} x2={kind === "support" ? width - 2 : end} y2={y} stroke="currentColor" strokeWidth={line.w} strokeDasharray={line.dash} strokeLinecap={kind === "communication" ? "round" : "butt"} opacity={line.op} />
      {kind === "composition" && <polygon points={`${width - 1},${y} ${end + 3},${y - 4} ${end - 3},${y} ${end + 3},${y + 4}`} fill="currentColor" />}
      {kind === "dependency" && <polygon points={`${width - 1},${y} ${end - 8},${y - 1.8} ${end - 8},${y + 1.8}`} fill="currentColor" />}
      {kind === "communication" && <polygon points={`${width - 1},${y} ${end - 1},${y - 3.6} ${end - 1},${y + 3.6}`} fill="currentColor" />}
    </svg>
  );
}

/** The kinds a graph declares, with the relationship types of each, in the order the legend lists them. */
export function kindsOf(edgeKinds: Record<string, EdgeKind> | undefined): { kind: EdgeKind; types: string[] }[] {
  if (!edgeKinds) return [];
  return (["composition", "dependency", "communication", "support"] as EdgeKind[])
    .map((kind) => ({ kind, types: Object.entries(edgeKinds).filter(([, k]) => k === kind).map(([t]) => t).sort() }))
    .filter((x) => x.types.length > 0);
}
