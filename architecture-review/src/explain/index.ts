import { changesExplainer } from "./changes";
import type { Explainer } from "./kit";

// Every explainer on the page, in reading order. Each sits next to the theme it names.
export const EXPLAINERS: readonly Explainer<any>[] = [changesExplainer];
