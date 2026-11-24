// The URL hash and the store stay in step: `#source=engine&view=editor`. Pure parse and format here; main.tsx wires them to the browser.
import { DEFAULT_SOURCE, SOURCES } from "./sources";
import type { Action, State, View } from "./types";
import { lessonOf, sourceForLesson } from "../tutorial/lessons";

export const VIEW_IDS: View[] = ["data", "space", "editor", "ontology"];
export interface Route { source: string; view: View; guide?: string | null }

/** Missing or unknown parts keep the current route (or the defaults at start): a hash `#view=ontology` changes only the view. */
export function parseHash(hash: string, current: Route = { source: DEFAULT_SOURCE, view: "data" }): Route {
  const p = new URLSearchParams(hash.replace(/^#/, ""));
  const lesson = lessonOf(p.get("guide"));
  const specified = p.get("source");
  const source = lesson?.space ? sourceForLesson(lesson)
    : specified && SOURCES.some(s => s.id === specified) ? specified : lesson ? sourceForLesson(lesson) : current.source;
  const view = VIEW_IDS.includes(p.get("view") as View) ? (p.get("view") as View) : lesson?.view ?? current.view;
  return { source, view, ...(lesson ? { guide: lesson.id } : {}) };
}
export const formatHash = (r: Route): string => `#source=${r.source}&view=${r.view}${r.guide ? `&guide=${encodeURIComponent(r.guide)}` : ""}`;

/** Actions that bring the store to the route (nothing when it already is there). */
export function actionsFor(r: Route, s: Pick<State, "sourceId" | "view"> & Partial<Pick<State, "guide">>): Action[] {
  const out: Action[] = [];
  if ((r.guide ?? null) !== (s.guide ?? null)) out.push({ type: "ui/guide", lesson: r.guide ?? null });
  if (r.view !== s.view) out.push({ type: "ui/view", view: r.view });
  if (r.source !== s.sourceId) out.push({ type: "source/opening", id: r.source });
  return out;
}
