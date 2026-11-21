import { SimulatedStream } from "../../src/source/simulated";
import { StaticSource } from "../../src/source/static";
import { SyntheticSource } from "./synthetic";
import type { SourceRegistration } from "../../src/state/sources";

const ESTATE = "demo/estate/manifest.json";
export default [
  { id: "demo", label: "Synthetic estate (engine output, static files)", make: () => new StaticSource("Synthetic estate", ESTATE) },
  { id: "demo-stream", label: "Synthetic estate (engine output, simulated stream)", make: () => new SimulatedStream(new StaticSource("Synthetic estate", ESTATE)) },
  { id: "synth-small", label: "Synthetic estate, small", make: () => new SyntheticSource("small", 60) },
  { id: "synth-medium", label: "Synthetic estate, medium (~5k)", make: () => new SyntheticSource("medium", 120) },
  { id: "synth-100k", label: "Synthetic estate, 100k nodes / 300k edges", make: () => new SyntheticSource("100k", 60) },
  { id: "synth-100k-stream", label: "Synthetic 100k (simulated stream)", make: () => new SimulatedStream(new SyntheticSource("100k", 60)) },
] satisfies SourceRegistration[];
