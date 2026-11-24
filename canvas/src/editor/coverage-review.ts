// The engine computes findings. The reviewer edits the rule table bound to the selected graph.
import { valueText, type Entity } from "../core/model";
import { canEditGkqlStructure, readGkqlJson, type GkqlProgram } from "./gkql";
import type { EditorService } from "./service";

export const coverageModule = "sattle.coverage";
export function field(entity: Entity, name: string): string {
  const value = entity.props[name] ?? entity.props[coverageModule + "." + name];
  return value && value.kind !== "null" ? valueText(value) : "";
}

export interface CoverageRule {
  ruleId: string; revision: string; source: string; item: string; role: string; target: string;
  action: "bind" | "declare" | "exclude"; owner: string; reason: string; many: boolean;
}

export function proposedRule(finding: Entity): CoverageRule {
  const declaration = field(finding, "direction") === "declaration";
  const source = field(finding, "source"), item = field(finding, "item"), role = field(finding, "role");
  const action = field(finding, "action");
  const revision = declaration ? field(finding, "revision") : "";
  return {
    ruleId: declaration ? field(finding, "ruleId") : ["review", source, item, role].map(encodeURIComponent).join("/"),
    revision: !revision ? "1" : /^\d+$/.test(revision) ? String(BigInt(revision) + 1n) : "",
    source, item, role, target: declaration ? field(finding, "target") : field(finding, "observedTarget"),
    action: declaration && (action === "declare" || action === "exclude") ? action : "bind",
    owner: declaration ? field(finding, "owner") : "", reason: declaration ? field(finding, "reason") : "",
    many: declaration && field(finding, "many") === "true",
  };
}

function reviseRule(text: string, finding: Entity, rule: CoverageRule): string {
  if (!canEditGkqlStructure(text)) throw new Error("Use the GKQL text editor to preserve numeric precision in this projection.");
  const program = readGkqlJson(text) as GkqlProgram;
  const table = program.verbs.find(v => v.id === "rules" && v.verb === "values")?.values as { rows?: CoverageRule[] } | undefined;
  if (!Array.isArray(table?.rows)) throw new Error("The coverage projection has no editable rules table.");
  const prior = table.rows.find(row => row.ruleId === rule.ruleId);
  if (field(finding, "direction") === "declaration") {
    const originalFields = ["revision", "source", "item", "role", "target", "action", "owner", "reason", "many"] as const;
    if (!prior || originalFields.some(key => String(prior[key]) !== field(finding, key)))
      throw new Error("The published rule changed. Open its latest finding and review again.");
    if (rule.revision === prior.revision) throw new Error("Give the changed rule a new revision.");
    // Preserve cardinality and any columns introduced by the author of the projection.
    table.rows = table.rows.map(row => row === prior ? { ...row, ...rule, many: row.many } : row);
  } else {
    if (prior) throw new Error("This item already has a review rule. Open the linked declaration to edit it.");
    table.rows = [...table.rows, rule];
  }
  return JSON.stringify(program, null, 2) + "\n";
}

/** A complete engine diff and the private draft which produced it. Only Publish commits it. */
export async function previewRule(service: EditorService, graph: string, file: string, finding: Entity, rule: CoverageRule) {
  const draft = await service.createDraft();
  try {
    const checked = await service.check(draft.draft);
    const fileText = checked.modules[file]?.text;
    if (!fileText) throw new Error("The bound rules file is missing from the draft.");
    const result = await service.setDraftText(draft.draft, file, reviseRule(fileText, finding, rule), draft.revision);
    if (!result.ok) throw new Error(result.diagnostics.map(d => d.message).join("\n") || "The engine rejected the rule.");
    const preview = await service.preview(draft.draft, { graphs: [graph], include: ["diff"] });
    const diff = preview.graphs.find(g => g.id === graph)?.diff;
    if (!preview.scope.complete || !diff) throw new Error("The replay is incomplete. A full comparison is required to publish here.");
    const affectsRule = diff.edits.some(edit => {
      if (edit.kind !== "entity") return false;
      const entity = edit.after ?? edit.before;
      return entity && isFinding(entity) && field(entity, "direction") === "declaration" &&
        field(entity, "ruleId") === rule.ruleId && field(entity, "source") === rule.source &&
        field(entity, "item") === rule.item && (!edit.after || field(entity, "revision") === rule.revision);
    });
    if (!affectsRule) throw new Error("The draft did not change a finding for this rule in the selected graph.");
    const changes = diff.edits.flatMap(edit => {
      if (edit.kind !== "entity") return [];
      const entity = edit.after ?? edit.before;
      if (!entity || !isFinding(entity)) return [];
      return [{ id: edit.id, item: field(entity, "item"), role: field(entity, "role"),
        direction: field(entity, "direction"), from: edit.before ? field(edit.before, "status") : "absent",
        to: edit.after ? field(edit.after, "status") : "removed" }];
    });
    const scopes = diff.edits.flatMap(edit => {
      if (edit.kind !== "entity") return [];
      const scope = edit.after ?? edit.before;
      if (!scope || (scope.type !== "Scope" && scope.type !== coverageModule + ".Scope")) return [];
      const counts = ["eligible", "supportedBindings", "mapped", "unmapped", "ambiguous", "excluded"].flatMap(key => {
        const from = edit.before && field(edit.before, key), to = edit.after && field(edit.after, key);
        return from === to && key !== "eligible" && key !== "supportedBindings" ? []
          : [{ label: key === "supportedBindings" ? "supported bindings" : key, from, to }];
      });
      return [{ id: edit.id, source: field(scope, "source"), role: field(scope, "role"), counts }];
    });
    return { draft, changes, scopes };
  } catch (error) {
    await service.discard(draft.draft).catch(() => {});
    throw error;
  }
}

export const isFinding = (entity: Entity) => entity.type === "Finding" || entity.type === coverageModule + ".Finding";
