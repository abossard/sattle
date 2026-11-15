// The reference projections of the research note (09, section 1) that the engine still has (Lift and Fixpoint are gone).
import seriesText from "../fixtures/acme.workload.kSeries.kql?raw";

/**
 * The reference projections in KQL: the projection, the graph the space makes of it and how that graph is bound (input ports to graphs of the
 * space, parameters to values). Series with threshold has no file in the definitions; its text is a fixture (`text`).
 */
export interface KqlReference { title: string; module: string; projection: string; graph: string; inputs: Record<string, string>; values: Record<string, string>; text?: string }
export const KQL_REFERENCES: KqlReference[] = [
  { title: "deployment health", module: "acme.deployhealth", projection: "deploymentHealth", graph: "deploymentHealth", inputs: { master: "master" }, values: {} },
  { title: "workload health", module: "acme.workload", projection: "workloadHealth", graph: "workload", inputs: { master: "master", authored: "authored", structure: "workloadStructure" }, values: { workloadNamespace: "shop" } },
  { title: "series with threshold", module: "acme.workload", projection: "kSeries", graph: "kSeries", inputs: { master: "master", structure: "workloadStructure" }, values: {}, text: seriesText },
];
