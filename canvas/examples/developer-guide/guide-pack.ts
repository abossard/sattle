import type { GuidePack } from "../../src/tutorial/lessons";
import { LESSONS } from "./lessons";
import { projectionSources } from "./projection-sources";

export default {
  lessons: LESSONS,
  sources: projectionSources,
  panel: () => import("./TutorialPanel").then(module => ({ default: module.TutorialPanel })),
} satisfies GuidePack;
