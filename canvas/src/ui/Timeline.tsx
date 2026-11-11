// One shared time axis: revisions at commit time, telemetry frames at window start. Fluent Slider and toolbar, all through actions.
import { Badge, Button, Field, Select, Slider, SpinButton, Text, Toolbar, ToolbarButton, makeStyles, tokens } from "@fluentui/react-components";
import { Next20Regular, Pause20Regular, Play20Regular, Previous20Regular } from "@fluentui/react-icons";
import { useEffect, useState, useSyncExternalStore } from "react";
import { countAtOrBefore, formatTime } from "../core/time-axis";
import type { StreamControls as Controls } from "../source/types";
import { dispatch, useStore } from "../state/store";
import { emptyFilter } from "../state/types";
import { useWorkspace } from "./common";

const useStyles = makeStyles({ bar: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalXS, padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`, borderTop: `1px solid ${tokens.colorNeutralStroke2}` }, row: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap" }, grow: { flexGrow: 1, minWidth: "120px" } });

function StreamControls({ ctl, sourceId }: { ctl: Controls; sourceId: string }) {
  const s = useStyles();
  useSyncExternalStore((cb) => ctl.onChange(cb), () => `${ctl.state}|${ctl.speed}|${ctl.delivered}|${ctl.total}`);
  return (
    <div className={s.row} role="group" aria-label="Simulated stream">
      <Text weight="semibold">Simulated stream</Text>
      <Button size="small" icon={ctl.state === "playing" ? <Pause20Regular /> : <Play20Regular />} disabled={ctl.state === "ended" || ctl.total === 0} onClick={() => (ctl.state === "playing" ? ctl.pause() : ctl.play())}>{ctl.state === "playing" ? "Pause" : "Play"}</Button>
      <Button size="small" disabled={ctl.state === "ended"} onClick={() => ctl.step()}>Step</Button>
      <Button size="small" onClick={() => dispatch({ type: "source/opening", id: sourceId })}>Reset</Button>
      <Field label="patches/s" orientation="horizontal"><SpinButton size="small" value={ctl.speed} min={0.1} max={60} step={0.5} onChange={(_, d) => d.value && ctl.setSpeed(d.value)} /></Field>
      <Text size={200}>{ctl.delivered}/{ctl.total} delivered · {ctl.state}</Text>
    </div>
  );
}

export function Timeline({ graph, compact = false }: { graph: string; compact?: boolean }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const time = useStore((x) => x.time);
  const sourceId = useStore((x) => x.sourceId);
  const fs = useStore((x) => x.filters[graph]) ?? emptyFilter();
  const [playing, setPlaying] = useState(false);
  const points = ws?.axisPoints() ?? [0];
  const index = Math.max(0, countAtOrBefore(points, time.now) - 1);
  const go = (i: number) => dispatch({ type: "time/seek", t: points[Math.max(0, Math.min(i, points.length - 1))] });
  useEffect(() => {
    if (!playing) return;
    if (index >= points.length - 1) { setPlaying(false); return; }
    const t = setTimeout(() => go(index + 1), 300);
    return () => clearTimeout(t);
  }); // eslint-disable-line react-hooks/exhaustive-deps
  if (!ws) return null;
  const hist = ws.histories.get(graph);
  if (!hist) return null;
  const pt = ws.patchTimes(graph);
  const rev = hist.revisionAt(hist.cursor);
  const text = `${formatTime(time.now)} · rev ${rev}${time.following && ws.source.controls ? " · live" : ""}`;
  const c = hist.cursor > 0 ? hist.changesBetween(hist.cursor - 1, hist.cursor) : null;
  const k = { added: 0, removed: 0, changed: 0 };
  if (c) { for (const x of c.entities.values()) k[x.kind]++; for (const x of c.relationships.values()) k[x.kind]++; }
  const ctl = ws.source.controls;
  return (
    <div className={s.bar} role="group" aria-label={`Time of the graph space${compact ? ` for ${graph}` : ""}`}>
      {ctl && !compact && <StreamControls ctl={ctl} sourceId={sourceId} />}
      <div className={s.row}>
        <Toolbar size="small" aria-label="Time navigation">
          <ToolbarButton aria-label="Start" icon={<Previous20Regular />} disabled={index === 0} onClick={() => go(0)} />
          <ToolbarButton aria-label="Previous point in time" disabled={index === 0} onClick={() => go(index - 1)}>◀</ToolbarButton>
          <ToolbarButton aria-label={playing ? "Pause" : "Play"} aria-pressed={playing} icon={playing ? <Pause20Regular /> : <Play20Regular />} disabled={points.length <= 1} onClick={() => { if (!playing && index >= points.length - 1) go(0); setPlaying(!playing); }} />
          <ToolbarButton aria-label="Next point in time" disabled={index >= points.length - 1} onClick={() => go(index + 1)}>▶</ToolbarButton>
          <ToolbarButton aria-label="Latest" icon={<Next20Regular />} disabled={index >= points.length - 1} onClick={() => go(points.length - 1)} />
        </Toolbar>
        <Slider className={s.grow} min={0} max={Math.max(points.length - 1, 0)} step={1} value={index} disabled={points.length <= 1} aria-label="Time of the graph space: revision and telemetry" aria-valuetext={`${formatTime(time.now)}, revision ${rev}`} onChange={(_, d) => go(d.value)} />
        <Text title={pt.source !== "source" ? "This source gives no commit time for (some) revisions: those are placed by order on the telemetry windows." : "Revisions at commit time, telemetry at window start."}>{text}{!compact && pt.source !== "source" ? (pt.source === "ordinal" ? " · revisions placed by order" : " · some revisions placed by order") : ""}</Text>
      </div>
      {!compact && (
        <div className={s.row}>
          <Badge appearance="outline" color="success">+ {k.added} added</Badge><Badge appearance="outline" color="danger">− {k.removed} removed</Badge><Badge appearance="outline" color="warning">Δ {k.changed} changed</Badge>
          {hist.cursor > 0 && <Text size={200}>cause {hist.patches[hist.cursor - 1].cause.inputs.join(", ")}</Text>}
          <span style={{ flexGrow: 1 }} />
          <Field label="Highlight" orientation="horizontal"><Select size="small" value={fs.hlMode} onChange={(_, d) => dispatch({ type: "filter/set", graph, filter: { hlMode: d.value as typeof fs.hlMode } })}><option value="step">This revision</option><option value="since">Since revision…</option><option value="off">Off</option></Select></Field>
          {fs.hlMode === "since" && <Select size="small" aria-label="Compare against revision" value={String(fs.hlSince)} onChange={(_, d) => dispatch({ type: "filter/set", graph, filter: { hlSince: Number(d.value) } })}>{Array.from({ length: hist.length + 1 }, (_, i) => <option key={i} value={i}>rev {hist.revisionAt(i)}</option>)}</Select>}
        </div>
      )}
    </div>
  );
}
