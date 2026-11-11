// The URL hash and the store stay in step: `#source=engine&view=editor`. Pure parse and format here; main.tsx wires them to the browser.
import { SOURCES } from "./sources";
import type { Action, State, View } from "./types";

export const VIEW_IDS: View[] = ["data", "space", "editor", "ontology"];
export interface Route { source: string; view: View }

/** Missing or unknown parts keep the current route (or the defaults at start): a hash `#view=ontology` changes only the view. */
export function parseHash(hash: string, current: Route = { source: "demo", view: "data" }): Route {
  const p = new URLSearchParams(hash.replace(/^#/, ""));
  const source = SOURCES.some((s) => s.id === p.get("source")) ? p.get("source")! : current.source;
  const view = VIEW_IDS.includes(p.get("view") as View) ? (p.get("view") as View) : current.view;
  return { source, view };
}
export const formatHash = (r: Route): string => `#source=${r.source}&view=${r.view}`;

/** Actions that bring the store to the route (nothing when it already is there). */
export function actionsFor(r: Route, s: Pick<State, "sourceId" | "view">): Action[] {
  const out: Action[] = [];
  if (r.view !== s.view) out.push({ type: "ui/view", view: r.view });
  if (r.source !== s.sourceId) out.push({ type: "source/opening", id: r.source });
  return out;
}
