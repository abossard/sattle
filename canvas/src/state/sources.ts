import { EngineSource } from "../source/engine";
import { SimulatedStream } from "../source/simulated";
import { StaticSource } from "../source/static";
import { SyntheticSource } from "../source/synthetic";
import type { GraphSource } from "../source/types";

const query = typeof location === "undefined" ? new URLSearchParams() : new URLSearchParams(location.search);
/** The dev and preview servers proxy /engine to the engine on port 5080, because the engine sends no CORS headers. */
export const ENGINE_URL = query.get("engine") ?? "/engine";

/** The engine's output on the synthetic estate fixture (`npm run demo`): the master and every projection with its edges, lineage and documents. */
const ESTATE = "demo/estate/manifest.json";

export const SOURCES: { id: string; label: string; make: () => GraphSource }[] = [
  { id: "engine", label: `Engine (${ENGINE_URL})`, make: () => new EngineSource(ENGINE_URL) },
  { id: "demo", label: "Synthetic estate (engine output, static files)", make: () => new StaticSource("Synthetic estate", ESTATE) },
  { id: "demo-stream", label: "Synthetic estate (engine output, simulated stream)", make: () => new SimulatedStream(new StaticSource("Synthetic estate", ESTATE)) },
  { id: "synth-small", label: "Synthetic estate, small", make: () => new SyntheticSource("small", 60) },
  { id: "synth-medium", label: "Synthetic estate, medium (~5k)", make: () => new SyntheticSource("medium", 120) },
  { id: "synth-100k", label: "Synthetic estate, 100k nodes / 300k edges", make: () => new SyntheticSource("100k", 60) },
  { id: "synth-100k-stream", label: "Synthetic 100k (simulated stream)", make: () => new SimulatedStream(new SyntheticSource("100k", 60)) },
];
