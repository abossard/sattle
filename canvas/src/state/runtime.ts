// The mutable side of the application: the open workspace (graphs, histories, GL panes, telemetry stores).
// State holds summaries of it; only effects write it, in the order actions arrive.
import type { Workspace } from "../app/workspace";
import type { EditorSession } from "../editor/session";
import type { Preview } from "../editor/service";

/** `opener`: the element that had focus when a dialog opened, so focus can return to it. */
export const runtime: { ws: Workspace | null; session: EditorSession | null; preview: Preview | null; opener: HTMLElement | null; versions: Map<string, { head: string; patches: number; nodes: number; edges: number; cursor: number; version: number }> } = { ws: null, session: null, preview: null, opener: null, versions: new Map() };
