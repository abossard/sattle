import colocation from "../../../engine/tutorial/tutorial.colocation.gkql.json?raw";
import placement from "../../../engine/tutorial/tutorial.placement.gkql.json?raw";
import workloads from "../../../engine/tutorial/tutorial.workloads.gkql.json?raw";

export const projectionSources: Readonly<Record<string, string>> = {
  "tutorial.colocation.gkql.json": colocation,
  "tutorial.placement.gkql.json": placement,
  "tutorial.workloads.gkql.json": workloads,
};

