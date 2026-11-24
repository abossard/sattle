import { useEffect, useId, useRef, useState } from "react";
import type { Entity } from "../core/model";
import { field, previewRule, proposedRule, type CoverageRule } from "../editor/coverage-review";
import "./coverage-review.css";
import type { EditorService } from "../editor/service";

type Comparison = Awaited<ReturnType<typeof previewRule>>;
type Phase = { kind: "editing" | "previewing" } | { kind: "preview" | "publishing"; comparison: Comparison } | { kind: "published"; revision: string };
type ReviewTarget = { entity: Entity; graph: string; rulesFile: string };

export function CoverageReview({ selected, service }: { selected?: ReviewTarget; service: EditorService }) {
  const [review, setReview] = useState<ReviewTarget>();
  if (review) return <RuleForm {...review} service={service} close={() => setReview(undefined)} />;
  return selected ? <button type="button" onClick={() => setReview(selected)}>Review rule</button> : null;
}

function RuleForm({ entity, graph, rulesFile, service, close }: ReviewTarget & { service: EditorService; close: () => void }) {
  const id = useId(), [phase, setPhase] = useState<Phase>({ kind: "editing" });
  const [rule, setRule] = useState(() => proposedRule(entity));
  const [suggestion, setSuggestion] = useState(""), [error, setError] = useState("");
  const pending = useRef(""), active = useRef(true);
  const evidence = field(entity, "sourceRef"), observed = field(entity, "observedTarget");
  const declaration = field(entity, "direction") === "declaration";
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
      if (pending.current) void service.discard(pending.current).catch(() => {});
    };
  }, [service]);

  function leave(editAgain: boolean) {
    if (pending.current) void service.discard(pending.current).catch(() => {});
    pending.current = "";
    setError("");
    if (editAgain) setPhase({ kind: "editing" });
    else close();
  }

  async function preview() {
    setError(""); setPhase({ kind: "previewing" });
    try {
      const comparison = await previewRule(service, graph, rulesFile, entity, rule.action === "exclude" ? { ...rule, target: "" } : rule);
      if (!active.current) { await service.discard(comparison.draft.draft); return; }
      pending.current = comparison.draft.draft;
      setPhase({ kind: "preview", comparison });
    } catch (e) {
      if (active.current) { setError((e as Error).message); setPhase({ kind: "editing" }); }
    }
  }

  async function publish(comparison: Comparison) {
    if (pending.current !== comparison.draft.draft) return;
    // The commit owns this draft until it settles, even if the Inspector closes.
    pending.current = "";
    setError(""); setPhase({ kind: "publishing", comparison });
    try {
      const release = await service.commit(comparison.draft.draft, comparison.draft.baseRevision);
      if (active.current) setPhase({ kind: "published", revision: release.catalogRevision });
    } catch (e) {
      await service.discard(comparison.draft.draft).catch(() => {});
      if (active.current) { setPhase({ kind: "editing" }); setError((e as Error).message); }
    }
  }

  function useSuggestion() {
    try {
      const candidate = JSON.parse(suggestion);
      if (candidate?.sourceRef !== evidence || typeof candidate.role !== "string" || !candidate.role.trim()
          || typeof candidate.target !== "string" || !candidate.target.trim())
        throw new Error("Cite " + evidence + " and supply a role and target.");
      setRule({ ...rule, role: candidate.role, target: candidate.target });
      setError("");
    } catch (e) { setError((e as Error).message); }
  }

  function input(key: "role" | "target" | "revision" | "owner" | "reason", label: string, required = true) {
    return <div><label htmlFor={id + key}>{label}</label>
      <input id={id + key} value={rule[key]} required={required} pattern={required ? ".*\\S.*" : undefined}
        onChange={e => setRule({ ...rule, [key]: e.target.value })} /></div>;
  }
  if (phase.kind === "published") return <div><p role="status">Published at catalog revision {phase.revision}.</p>
    <button type="button" onClick={close}>Done</button></div>;
  const previewing = phase.kind === "previewing", publishing = phase.kind === "publishing";
  return <section className="coverage-review" aria-labelledby={id + "heading"}>
    <h3 id={id + "heading"}>Review rule</h3>
    <p id={id + "evidence"}>{field(entity, "source")} · {field(entity, "item")} · {field(entity, "role")}
      {observed && <> → <strong>{observed}</strong></>}{evidence && <> · <code>{evidence}</code></>}</p>
    {declaration && <p>Published rule <code>{field(entity, "ruleId")}</code> at {field(entity, "revision")} · {field(entity, "target")}</p>}
    {error && <p role="alert">{error}</p>}
    {phase.kind === "preview" || phase.kind === "publishing" ? <>
      <h4>Draft changes</h4>
      {phase.comparison.changes.length ? <ul>{phase.comparison.changes.map(c => <li key={c.id}>
        {c.item} · {c.role} ({c.direction}) · <strong>{c.from} → {c.to}</strong>
      </li>)}</ul> : <p>Finding statuses are unchanged.</p>}
      {phase.comparison.scopes.map(scope => <p key={scope.id}>{scope.source} · {scope.role}<br />{scope.counts.map(c => <span key={c.label}>{c.label} {c.from ?? "absent"}{c.from !== c.to && " → " + (c.to ?? "removed")}<br /></span>)}</p>)}
      <p>Proposed {rule.action} · {rule.role}{rule.action !== "exclude" && rule.target && " → " + rule.target} · revision {rule.revision}</p>
      {rule.action === "exclude" && <p>{rule.owner} · {rule.reason}</p>}
      <button type="button" disabled={publishing} onClick={() => void publish(phase.comparison)}>Publish rule</button>
      <button type="button" disabled={publishing} onClick={() => leave(true)}>Edit again</button>
      <button type="button" disabled={publishing} onClick={() => leave(false)}>Discard draft</button>
      {publishing && <p role="status">Publishing…</p>}
    </> : <form aria-labelledby={id + "heading"} onSubmit={e => { e.preventDefault(); void preview(); }}>
      <fieldset disabled={previewing}>
        <legend>Proposed rule</legend>
        <label htmlFor={id + "action"}>Action</label>
        <select id={id + "action"} value={rule.action} onChange={e => setRule({ ...rule, action: e.target.value as CoverageRule["action"] })}>
          <option value="bind">Bind observation</option><option value="declare">Declare role</option><option value="exclude">Exclude with reason</option>
        </select>
        {input("role", "Role")}
        {rule.action !== "exclude" && input("target", "Target")}
        {input("revision", "Rule revision")}
        {rule.action === "exclude" && <>{input("owner", "Exclusion owner")}{input("reason", "Exclusion reason")}</>}
        {evidence && <details><summary>Use an assistant suggestion</summary>
          <p>Paste a proposed role and target with <code>sourceRef</code> set to <a href={"#" + id + "evidence"} onClick={e => {
            e.preventDefault(); document.getElementById(id + "evidence")?.scrollIntoView({ block: "nearest" });
          }}>{evidence}</a>.</p>
          <label htmlFor={id + "suggestion"}>Suggested rule (JSON)</label>
          <textarea id={id + "suggestion"} value={suggestion} onChange={e => setSuggestion(e.target.value)} />
          <button type="button" onClick={useSuggestion}>Use suggestion</button>
        </details>}
        <button type="submit">Preview change</button>
        <button type="button" onClick={() => leave(false)}>Leave unresolved</button>
      </fieldset>
      {previewing && <p role="status">Replaying the draft…</p>}
    </form>}
  </section>;
}
