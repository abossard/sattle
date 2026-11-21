import { readGkql } from "../editor/gkql";
import { projectionSources } from "./lessons";

export function exerciseProgram({ source, name }: { source: string; name: string }) {
  return { ...readGkql(projectionSources[source]), graph: name };
}
