// The application's data canvas as the drawing an example is picked from: the same renderer, layout and names as the data view.
import { DataPane } from "../app/data-pane";
import { History } from "../app/history";
import type { Graph } from "../core/graph";
import type { Theme } from "../render/theme";
import type { GraphInfo } from "../source/types";
import { webgl2Available } from "../ui/CanvasHost";
import type { Drawing } from "./ExampleEditor";

export function dataDrawing(info: GraphInfo, graph: Graph, theme: Theme): Drawing {
  return (host, on) => {
    if (!webgl2Available()) return null;
    const pane: DataPane = new DataPane({ ...info, inputs: [] }, new History(graph.toSnapshot()), host, theme, {
      select: (sel) => { pane.setSelection(sel); if (sel) on.pick(sel.id); },
      hover: () => {}, activate: () => {}, changed: () => {}, announce: () => {}, key: () => false,
    });
    pane.start();
    return () => pane.dispose();
  };
}
