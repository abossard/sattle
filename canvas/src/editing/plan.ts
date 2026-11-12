// A command of an author as catalog edits: a calculation over the draft graph and the palette. No engine call, no state.
// The command type is the application's (state/types.ts), so a recorded walkthrough replays through here unchanged.
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import {
  addExprItemEdits, addNamedEdits, addParamChildEdits, addPatternEdgeEdits, addPatternNodeEdits, addReadEdits, addRuleEdits, addSetEdits,
  addValuesRowEdits, canWire, paramEdits, setExprEdits, setOfTypeEdits, unwireEdits, wireEdits, type Result,
} from "../editor/commands";
import { paramOf, type Palette } from "../editor/palette";
import { removeEdits } from "../editor/patches";
import { setRefEdits } from "../editor/refs";
import { addRuleSetEdits, newProjectionEdits, setCreateKeyEdits, setRulePropEdits } from "../editor/structures";
import type { Command } from "../state/types";

export type { Command };
export interface Planned { edits: Edit[]; label: string }

const ok = (edits: Edit[], label: string): Result<Planned> => ({ ok: true, value: { edits, label } });
const from = (r: Result<Edit[]>, label: string): Result<Planned> => (r.ok ? ok(r.value, label) : r);

const NONE: ReadonlySet<string> = new Set();

/**
 * The edits of one command against `g`, or the reason it cannot be done. Commands that need the engine (a new operator, text,
 * refactoring) are not planned here. `placeholders` are the children a skeleton made only so that an operator prints: the first
 * real entry of their slot replaces them, and nothing else is ever taken for one.
 */
export function plan(g: Graph, palette: Palette, projection: string, cmd: Command, placeholders: ReadonlySet<string> = NONE): Result<Planned> {
  switch (cmd.t) {
    case "delete": return ok(removeEdits(g, cmd.ids), `delete ${cmd.ids.length} element${cmd.ids.length === 1 ? "" : "s"}`);
    case "wire": { const can = canWire(g, cmd.from, cmd.to); return can.ok ? ok(wireEdits(cmd.from, cmd.to), "connect") : can; }
    case "unwire": return ok(unwireEdits(g, cmd.key), "disconnect");
    case "param": {
      const p = paramOf(palette, g.entity(cmd.id)?.type ?? "", cmd.name);
      return p ? from(paramEdits(g, cmd.id, p, cmd.value), `set ${cmd.name}`) : { ok: false, reason: `${cmd.name} is not a parameter of this element` };
    }
    case "child": return from(addParamChildEdits(g, cmd.id, cmd.slot, cmd.childType, cmd.key, cmd.params, palette), `add ${cmd.key}`);
    case "expr": return from(setExprEdits(g, cmd.id, cmd.slot, cmd.src, palette), `set ${cmd.slot}`);
    case "exprAdd": return from(addExprItemEdits(g, cmd.id, cmd.slot, cmd.src, palette), `add to ${cmd.slot}`);
    case "named": return from(addNamedEdits(g, cmd.id, cmd.slot, cmd.childType, cmd.name, cmd.src, palette, cmd.extra, placeholders), `add ${cmd.name}`);
    case "set": return from(addSetEdits(g, cmd.id, cmd.property, cmd.src, palette, cmd.slot ?? "set"), "set property");
    case "ref": return from(setRefEdits(g, cmd.id, cmd.rel, cmd.target), `set ${cmd.rel}`);
    case "ofType": return from(setOfTypeEdits(g, cmd.id, cmd.type), "set type");
    case "read": return from(addReadEdits(g, cmd.id, cmd.path, cmd.slot ?? "read", placeholders), "add read");
    case "valuesRow": return from(addValuesRowEdits(g, cmd.id, cmd.cells, palette), "add row");
    case "patternNode": return from(addPatternNodeEdits(g, cmd.id, cmd.variable, cmd.type), `add node ${cmd.variable}`);
    case "patternEdge": return from(addPatternEdgeEdits(g, cmd.id, cmd.variable, cmd.from, cmd.to, cmd.type), "add edge");
    case "newProjection": return from(newProjectionEdits(g, cmd.spec, palette), `new projection ${cmd.spec.name}`);
    case "rule": return from(addRuleEdits(g, projection, cmd.name, cmd.cls, cmd.reason, cmd.governs), `add rule ${cmd.name}`);
    case "ruleProp": return from(setRulePropEdits(g, cmd.id, cmd.name, cmd.value, palette), `set ${cmd.name}`);
    case "ruleSet": return from(addRuleSetEdits(g, cmd.id, cmd.slot, cmd.property, cmd.src, palette), `add ${cmd.slot}`);
    case "createKey": return from(setCreateKeyEdits(g, cmd.id, cmd.domain), "set create key");
    default: return { ok: false, reason: `${cmd.t} is not an edit of a projection` };
  }
}
