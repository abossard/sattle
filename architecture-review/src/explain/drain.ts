import { CUTOFF, DRAIN_SCRIPT, minuteLabel, runDrain, type Frame } from "../sim/drain";
import { arrow, box, chip, lane, svg, text, weightLabel, type Explainer } from "./kit";

const LANES = { input: 16, memory: 208, drain: 500, output: 760 };
const HEIGHT = 316;
const BOX = { memory: 140, drain: 130, move: 86 };
const rowY = (index: number) => 44 + index * 36;
const boxY = (index: number) => 40 + index * 58;

function draw(frame: Frame): string {
  const gets = frame.moves.filter((move) => move.kind === "get").map((move) => move.minute);
  const visiting = frame.drain.filter((window) => gets.includes(window.minute));
  const held = [...frame.memory, ...visiting].sort((a, b) => a.minute - b.minute);
  const heldIndex = (minute: number) => held.findIndex((window) => window.minute === minute);
  const drainIndex = (minute: number) => frame.drain.findIndex((window) => window.minute === minute);
  const moved = new Set(frame.moves.map((move) => move.minute));

  const input = frame.input.map((row, index) => chip({
    x: LANES.input + 9, y: rowY(index), width: 150, swatch: gets.includes(row.minute) ? "warn" : "add",
    label: weightLabel(1, `${minuteLabel(row.minute)}: ${row.calls} calls`),
    from: { x: LANES.input - 40, y: rowY(index) }, delay: index * 60,
  })).join("");
  const memory = held.map((window, index) => box({
    x: LANES.memory + 10, y: boxY(index), width: BOX.memory, key: `${minuteLabel(window.minute)} calls`, value: String(window.total),
    swatch: gets.includes(window.minute) ? "warn" : frame.touched.includes(window.minute) ? "touched" : "dim", delay: 500,
  })).join("");
  const drain = frame.drain.map((window, index) => box({
    x: LANES.drain + 10, y: boxY(index), width: BOX.drain, key: `${minuteLabel(window.minute)} calls`, value: String(window.total),
    swatch: moved.has(window.minute) ? "touched" : "dim", delay: 1100,
  })).join("");
  const moves = frame.moves.map((move) => {
    const back = gets.includes(move.minute);
    if (move.kind === "get") {
      const from = frame.drainBefore.findIndex((window) => window.minute === move.minute);
      return chip({ x: LANES.memory + 156, y: boxY(heldIndex(move.minute)) + 12, width: BOX.move, swatch: "warn", label: `get ${minuteLabel(move.minute)}`, from: { x: LANES.drain + 146, y: boxY(from) + 12 }, delay: 250 });
    }
    const from = back ? heldIndex(move.minute) : frame.memoryBefore.findIndex((window) => window.minute === move.minute);
    return chip({ x: LANES.drain + 146, y: boxY(drainIndex(move.minute)) + 12, width: BOX.move, swatch: "touched", label: `put ${minuteLabel(move.minute)}`, from: { x: LANES.memory + 156, y: boxY(from) + 12 }, delay: back ? 1000 : 250 });
  }).join("");
  const output = frame.output.map((change, index) => chip({
    x: LANES.output + 12, y: rowY(index), width: 160, swatch: change.weight > 0 ? "add" : "remove",
    label: weightLabel(change.weight, `${minuteLabel(change.row.minute)} = ${change.row.total}`),
    from: { x: LANES.memory + 40, y: boxY(heldIndex(change.row.minute)) }, delay: 700 + index * 80,
  })).join("");
  const brought = visiting.map((window) => minuteLabel(window.minute)).join(", ");
  const counter = `${text(LANES.memory + 12, HEIGHT - 34, `in memory: ${frame.memory.length} windows, cutoff ${CUTOFF}`)}${frame.peak > frame.memory.length ? text(LANES.memory + 12, HEIGHT - 16, `${frame.peak} for a moment, while ${brought} was back`) : ""}`;
  const stored = text(LANES.drain + 12, HEIGHT - 34, `in the drain: ${frame.drain.length} ${frame.drain.length === 1 ? "window" : "windows"}`);

  return svg(HEIGHT, "Minute windows of one key moving between memory and a drain as rows arrive", `
    ${lane(LANES.input, 168, HEIGHT, "Rows arriving for api")}
    ${lane(LANES.memory, 252, HEIGHT, `In memory (cutoff ${CUTOFF})`, "state")}
    ${lane(LANES.drain, 236, HEIGHT, "Drain (storage)")}
    ${lane(LANES.output, 184, HEIGHT, "Output changes")}
    ${arrow(LANES.input + 170, 120, LANES.memory - 4)}${arrow(LANES.drain + 238, 120, LANES.output - 4)}
    ${arrow(LANES.memory + 254, 128, LANES.drain - 4)}${text(480, 118, "put", "", "middle")}
    ${arrow(LANES.drain - 4, 176, LANES.memory + 254)}${text(480, 196, "get", "", "middle")}
    ${input}${memory}${drain}${moves}${output}${counter}${stored}`);
}

export const drainExplainer: Explainer<Frame> = {
  id: "drain",
  theme: "time-chapter",
  title: DRAIN_SCRIPT.title,
  lede: DRAIN_SCRIPT.lede,
  frames: runDrain(DRAIN_SCRIPT.batches),
  notes: DRAIN_SCRIPT.notes,
  legend: [
    { swatch: "add", label: "+1 a row or a new total" },
    { swatch: "remove", label: "−1 takes an old total back" },
    { swatch: "touched", label: "window a row or a put reached" },
    { swatch: "warn", label: "late row, window brought back from the drain" },
    { swatch: "dim", label: "window nothing reached" },
  ],
  draw,
};
