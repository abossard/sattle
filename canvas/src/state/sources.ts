import { EngineSource } from "../source/engine";
import { connections, sourceProviders } from "virtual:canvas-examples";
import type { GraphSource } from "../source/types";

export interface SourceRegistration { id: string; label: string; make: () => GraphSource }

const query = typeof location === "undefined" ? new URLSearchParams() : new URLSearchParams(location.search);
export const ENGINE_URL = query.get("engine") ?? "/engine";
export const SOURCES: SourceRegistration[] = [
  { id: "engine", label: `Engine (${ENGINE_URL})`, make: () => new EngineSource(ENGINE_URL) },
  ...connections.map(({ id, label, url }) => ({ id, label, make: () => new EngineSource(url) })),
  ...sourceProviders,
];
export const DEFAULT_SOURCE = sourceProviders[0]?.id ?? SOURCES[0].id;
