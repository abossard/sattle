import { LOOP_SCRIPT, runLoop, STAGES, type Event, type Frame } from "../sim/loop";
import { arrow, box, chip, lane, svg, type Explainer, type Swatch } from "./kit";

const HEIGHT = 316;
const LANES = { events: 16, stage: 232, criteria: 548, cost: 764 };
const boxY = (index: number, gap = 54) => 40 + index * gap;

function eventChip(event: Event, frame: Frame): { label: string; swatch: Swatch } {
  switch (event.kind) {
    case "open": return { label: "issue opened", swatch: "accent" };
    case "claim": return { label: `claim, worker ${frame.state.sessions}`, swatch: "accent" };
    case "proof": return { label: "proof posted", swatch: "accent" };
    case "fail": return { label: `verdict fail, A${event.criterion + 1}`, swatch: "remove" };
    case "handover": return { label: "handover, card released", swatch: "warn" };
    case "pass": return { label: "verdict pass", swatch: "add" };
  }
}

function draw(frame: Frame): string {
  const { state } = frame;
  const events = frame.events.map((event, index) => {
    const { label, swatch } = eventChip(event, frame);
    return chip({ x: LANES.events + 12, y: boxY(index, 44), swatch, label, width: 176, from: { x: LANES.events - 40, y: boxY(index, 44) }, delay: index * 120 });
  }).join("");
  const stages = STAGES.map((stage, index) => {
    const current = stage === state.stage;
    const held = current && state.holder !== null ? `worker ${state.holder}` : current && !state.closed ? "no worker" : current ? "closed" : "";
    return box({ x: LANES.stage + 12, y: boxY(index), width: 276, key: held ? `${stage}, ${held}` : stage, value: current ? "card is here" : "", swatch: current ? "touched" : "dim", delay: 300 });
  }).join("");
  const criteria = state.criteria.map((status, index) => box({
    x: LANES.criteria + 12, y: boxY(index, 62), width: 176, key: `A${index + 1}`,
    value: status === "open" ? "open" : status === "proof" ? "proof posted" : "ticked",
    swatch: status === "open" ? "warn" : status === "proof" ? "touched" : "add", delay: 450,
  })).join("");
  const cost = [["review rounds", state.rounds], ["workers used", state.sessions]].map(([key, value], index) => box({
    x: LANES.cost + 12, y: boxY(index, 62), width: 156, key: String(key), value: String(value), swatch: "accent", delay: 600,
  })).join("");
  return svg(HEIGHT, "One issue moving through plan, doing, review and done, with its criteria and cost", `
    ${lane(LANES.events, 200, HEIGHT, "Events")}
    ${lane(LANES.stage, 300, HEIGHT, "Card stage", "state")}
    ${lane(LANES.criteria, 200, HEIGHT, "Criteria", "state")}
    ${lane(LANES.cost, 180, HEIGHT, "Cost")}
    ${arrow(LANES.events + 204, 160, LANES.stage - 6)}
    ${events}${stages}${criteria}${cost}`);
}

export const loopExplainer: Explainer<Frame> = {
  id: "loop",
  theme: "managed-run",
  title: LOOP_SCRIPT.title,
  lede: LOOP_SCRIPT.lede,
  frames: runLoop(LOOP_SCRIPT.script),
  notes: LOOP_SCRIPT.notes,
  legend: [
    { swatch: "touched", label: "where the card is, or proof posted" },
    { swatch: "remove", label: "failed verdict" },
    { swatch: "add", label: "passed, ticked" },
    { swatch: "warn", label: "criterion still open" },
    { swatch: "dim", label: "stage the card is not in" },
  ],
  draw,
};
