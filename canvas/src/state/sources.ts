import { EngineSource } from "../source/engine";
import connections from "../../connections.json";
import type { GraphSource } from "../source/types";

export interface SourceRegistration { id: string; label: string; make: () => GraphSource }

const query = typeof location === "undefined" ? new URLSearchParams() : new URLSearchParams(location.search);
export const ENGINE_URL = query.get("engine") ?? "/engine";
const providers = import.meta.glob<{ default: SourceRegistration[] }>("../../examples/*/sources.ts", { eager: true });
export const SOURCES: SourceRegistration[] = [
  { id: "engine", label: `Engine (${ENGINE_URL})`, make: () => new EngineSource(ENGINE_URL) },
  ...(import.meta.env.VITE_CANVAS_EXAMPLES === "off" ? [] : [
    ...connections.map(({ id, label, url }) => ({ id, label, make: () => new EngineSource(url) })),
    ...Object.values(providers).flatMap(item => item.default),
  ]),
];
export const DEFAULT_SOURCE = SOURCES.find(s => s.id === "demo")?.id ?? SOURCES[0].id;
