import { Badge, Button, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useEffect, useState } from "react";

type Failure = { section: string; key: string; expected: unknown; actual: unknown };
type Counts = { entityStates: number; signalStates: number; signalValues: number };
type Checkpoint = { capturedAt: string; checks: Counts; passed: boolean; failures: Failure[] };
type Report = { model: string; passed: boolean; checkpoints: Checkpoint[]; structureFailures: Failure[]; negativeControls?: Record<string, boolean> };
type Result = { status: "loading" | "unavailable" | "stale" } | { status: "ready"; report: Report };

const useStyles = makeStyles({
  box: { display: "grid", gap: tokens.spacingVerticalXS, padding: tokens.spacingHorizontalS, margin: `${tokens.spacingVerticalM} 0`,
    border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  heading: { display: "flex", alignItems: "center", flexWrap: "wrap", gap: tokens.spacingHorizontalS },
  table: { width: "100%", fontSize: tokens.fontSizeBase200, fontVariantNumeric: "tabular-nums", textAlign: "left",
    "& th": { fontWeight: tokens.fontWeightSemibold }, "& td, & th": { paddingBlock: tokens.spacingVerticalXXS } },
  controls: { justifySelf: "start" },
  evidence: { overflowWrap: "anywhere", fontSize: tokens.fontSizeBase200 },
});

function counts(row: Checkpoint, key: keyof Counts) {
  const total = row.checks[key] ?? 0;
  return `${total - row.failures.filter(f => f.section === key).length}/${total}`;
}

function Differences({ rows }: { rows: Failure[] }) {
  return <ul>{rows.map((r, i) => <li key={i}><strong>{r.key}</strong> · {r.section}<br />
    Expected {JSON.stringify(r.expected)} · Observed {JSON.stringify(r.actual)}</li>)}</ul>;
}

export function CheckpointComparison() {
  const styles = useStyles();
  const [refresh, setRefresh] = useState(0);
  const [result, setResult] = useState<Result>({ status: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      try {
        const response = await fetch("/example-evidence/core-parity", { signal: controller.signal, cache: "no-store" });
        if (!response.ok) {
          if (!controller.signal.aborted) setResult({ status: response.status === 409 ? "stale" : "unavailable" });
          return;
        }
        const report: Report = await response.json();
        if (!report.model || !report.checkpoints?.length) throw new Error("Missing checkpoint comparison");
        if (!controller.signal.aborted) setResult({ status: "ready", report });
      } catch { if (!controller.signal.aborted) setResult({ status: "unavailable" }); }
    }
    void load();
    const refocus = () => { if (document.visibilityState === "visible") void load(); };
    document.addEventListener("visibilitychange", refocus);
    return () => { controller.abort(); document.removeEventListener("visibilitychange", refocus); };
  }, [refresh]);

  const report = result.status === "ready" ? result.report : undefined;
  const days = report ? [...new Set(report.checkpoints.map(r => new Date(r.capturedAt).toISOString().slice(0, 10)))] : [];
  const failedControls = Object.entries(report?.negativeControls ?? {}).filter(([, detected]) => !detected);
  return <section className={styles.box} aria-label="Azure checkpoint comparison">
    <div className={styles.heading}><Text weight="semibold">Azure checkpoint comparison</Text>
      {report && <Badge color={report.passed ? "success" : "danger"}>{report.passed ? "Verified" : "Differences"}</Badge>}
    </div>
    {report ? <>
      <Text size={200}>{report.model} · {days.length === 1 ? days[0] + " UTC" : "UTC"}</Text>
      <table className={styles.table} aria-label="Matches in the captured replay">
        <thead><tr><th>Checkpoint</th><th>Entities</th><th>Signals</th><th>Values</th></tr></thead>
        <tbody>{report.checkpoints.map(row => {
          const stamp = new Date(row.capturedAt).toISOString();
          return <tr key={stamp}><td><time dateTime={row.capturedAt} title={row.capturedAt}>
            {days.length === 1 ? stamp.slice(11, 19) : stamp.slice(0, 19).replace("T", " ")}</time></td>
            <td>{counts(row, "entityStates")}</td><td>{counts(row, "signalStates")}</td><td>{counts(row, "signalValues")}</td></tr>;
        })}</tbody>
      </table>
      {report.checkpoints.filter(r => !r.passed).map(r => <details key={r.capturedAt} className={styles.evidence}>
        <summary>{new Date(r.capturedAt).toISOString().slice(11, 19)} UTC · {r.failures.length} differences</summary><Differences rows={r.failures} /></details>)}
      {report.structureFailures.length > 0 && <details className={styles.evidence}><summary>Graph differences ({report.structureFailures.length})</summary><Differences rows={report.structureFailures} /></details>}
      {failedControls.length > 0 && <Text size={200}>Undetected test changes · {failedControls.map(([name]) => name).join(", ")}</Text>}
    </> : <Text size={200} role="status">{result.status === "loading" ? "Reading captured checkpoints…" : result.status === "stale" ? "Capture changed. Run the comparison again." : "Run the local checkpoint comparison to see the matches."}</Text>}
    {result.status !== "loading" && <Button className={styles.controls} appearance="subtle" size="small" onClick={() => { setResult({ status: "loading" }); setRefresh(i => i + 1); }}>Reload comparison</Button>}
  </section>;
}
