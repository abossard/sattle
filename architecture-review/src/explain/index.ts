import { changesExplainer } from "./changes";
import { drainExplainer } from "./drain";
import { foldExplainer } from "./fold";
import { guardExplainer } from "./guard";
import type { Explainer } from "./kit";
import { lateExplainer } from "./late";
import { loopExplainer } from "./loop";

// Every explainer on the page. Each sits next to the theme it names.
export const EXPLAINERS: readonly Explainer<any>[] = [changesExplainer, lateExplainer, guardExplainer, foldExplainer, drainExplainer, loopExplainer];
