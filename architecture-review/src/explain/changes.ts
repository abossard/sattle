import { CHANGES_SCRIPT, runChanges, type Frame } from "../sim/changes";
import { arrow, box, CHIP, chip, lane, svg, weightLabel, type Explainer } from "./kit";

const LANES = { input: 16, map: 256, reduce: 496, output: 760 };
const HEIGHT = 230;
const rowY = (index: number) => 44 + index * 36;

function draw(frame: Frame): string {
  const kept = new Set(frame.mapped);
  const input = frame.input.map((change, index) => chip({
    x: LANES.input + 12, y: rowY(index), swatch: change.weight > 0 ? "add" : "remove",
    label: weightLabel(change.weight, `${change.row.service}, ${change.row.status}`),
    from: { x: LANES.input - 40, y: rowY(index) }, delay: index * 60, struck: !kept.has(change),
  })).join("");
  const mapped = frame.mapped.map((change, index) => chip({
    x: LANES.map + 12, y: rowY(index), swatch: change.weight > 0 ? "add" : "remove",
    label: weightLabel(change.weight, `${change.row.service}, ${change.row.status}`),
    from: { x: LANES.input + 12, y: rowY(frame.input.indexOf(change)) }, delay: 350 + index * 60,
  })).join("");
  const keys = [...new Set([...frame.before, ...frame.after].map((count) => count.service))].sort();
  const boxes = keys.map((key, index) => {
    const value = frame.after.find((count) => count.service === key)?.errors ?? 0;
    const touched = frame.touched.includes(key);
    return box({ x: LANES.reduce + 12, y: 40 + index * 62, width: 216, key: `${key}: errors`, value: String(value), swatch: touched ? "touched" : "dim", delay: 650 });
  }).join("");
  const output = frame.output.map((change, index) => chip({
    x: LANES.output + 12, y: rowY(index), width: CHIP.width, swatch: change.weight > 0 ? "add" : "remove",
    label: weightLabel(change.weight, `${change.row.service} = ${change.row.errors}`),
    from: { x: LANES.reduce + 40, y: 40 + keys.indexOf(change.row.service) * 62 }, delay: 900 + index * 80,
  })).join("");
  return svg(HEIGHT, "Rows moving through a map step and a reduce step", `
    ${lane(LANES.input, 200, HEIGHT, "Input changes")}
    ${lane(LANES.map, 200, HEIGHT, "Map: keep errors", "calc")}
    ${lane(LANES.reduce, 240, HEIGHT, "Reduce: errors per service", "state")}
    ${lane(LANES.output, 190, HEIGHT, "Output changes")}
    ${arrow(LANES.input + 204, 120, LANES.map - 6)}${arrow(LANES.map + 204, 120, LANES.reduce - 6)}${arrow(LANES.reduce + 244, 120, LANES.output - 6)}
    ${input}${mapped}${boxes}${output}`);
}

export const changesExplainer: Explainer<Frame> = {
  id: "changes",
  theme: "one-language",
  title: CHANGES_SCRIPT.title,
  lede: CHANGES_SCRIPT.lede + " Every gkql step is one of these two kinds.",
  frames: runChanges(CHANGES_SCRIPT.batches),
  notes: CHANGES_SCRIPT.notes,
  legend: [
    { swatch: "add", label: "+1 adds a row" },
    { swatch: "remove", label: "−1 takes a row back" },
    { swatch: "touched", label: "state a change reached" },
    { swatch: "dim", label: "state no change reached" },
  ],
  draw,
};
