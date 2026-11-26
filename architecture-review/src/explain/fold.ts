import { FOLD_SCRIPT, POLICY, runFold, type Frame, type KeyState } from "../sim/fold";
import { arrow, box, CHIP, chip, lane, svg, text, weightLabel, type Explainer } from "./kit";

const LANES = { input: 16, fold: 256, output: 716 };
const HEIGHT = 300;
const rowY = (index: number) => 44 + index * 36;
const boxY = (index: number) => 40 + index * 62;
const NODE = { y: 214, healthy: 280, unhealthy: 540, width: 120 };

const toward = (state: KeyState) => state.mode === "Healthy" ? `above ${POLICY.above.toFixed(1)}` : `below ${POLICY.below.toFixed(1)}`;

function draw(frame: Frame): string {
  const keys = frame.after.map((state) => state.key);
  const input = frame.input.map((window, index) => chip({
    x: LANES.input + 12, y: rowY(index), swatch: "accent",
    label: `${window.key}  min ${window.minute}  ${window.cpu.toFixed(1)}`,
    from: { x: LANES.input - 40, y: rowY(index) }, delay: index * 60,
  })).join("");
  const boxes = frame.after.map((state, index) => {
    const touched = frame.touched.includes(state.key);
    const swatch = !touched ? "dim" : state.mode === "Unhealthy" ? "warn" : "touched";
    return box({ x: LANES.fold + 12, y: boxY(index), width: 200, key: `${state.key}: ${state.run} in a row ${toward(state)}`, value: state.mode, swatch, delay: 400 });
  }).join("");
  const focus = frame.after.find((state) => frame.touched.includes(state.key));
  const node = (mode: "Healthy" | "Unhealthy", x: number) => chip({
    x, y: NODE.y, width: NODE.width, label: mode,
    swatch: focus?.mode !== mode ? "dim" : mode === "Unhealthy" ? "warn" : "touched", delay: 500,
  });
  const machine = `
    ${text(LANES.fold + 12, NODE.y - 30, `Same rule for every key${focus ? `, now ${focus.key}` : ""}:`)}
    ${node("Healthy", NODE.healthy)}${node("Unhealthy", NODE.unhealthy)}
    ${arrow(NODE.healthy + NODE.width + 2, NODE.y + 8, NODE.unhealthy - 4)}
    ${arrow(NODE.unhealthy - 2, NODE.y + 20, NODE.healthy + NODE.width + 4)}
    ${text((NODE.healthy + NODE.width + NODE.unhealthy) / 2, NODE.y - 4, `${POLICY.enterAfter} above ${POLICY.above.toFixed(1)}`, "", "middle")}
    ${text((NODE.healthy + NODE.width + NODE.unhealthy) / 2, NODE.y + 44, `${POLICY.exitAfter} below ${POLICY.below.toFixed(1)}`, "", "middle")}
    ${text(LANES.fold + 12, HEIGHT - 20, "Any other window resets the count to 0.")}`;
  const output = frame.output.map((change, index) => chip({
    x: LANES.output + 12, y: rowY(index), width: CHIP.width, swatch: change.weight > 0 ? "add" : "remove",
    label: weightLabel(change.weight, `${change.row.key} ${change.row.mode}`),
    from: { x: LANES.fold + 52, y: boxY(keys.indexOf(change.row.key)) }, delay: 800 + index * 80,
  })).join("");
  return svg(HEIGHT, "Windows reaching a keyed fold that carries one state per key", `
    ${lane(LANES.input, 220, HEIGHT, "Windows arriving")}
    ${lane(LANES.fold, 440, HEIGHT, "Fold per key, in time order", "state")}
    ${lane(LANES.output, 228, HEIGHT, "Output: state changes")}
    ${arrow(LANES.input + 224, 66, LANES.fold - 6)}${arrow(LANES.fold + 444, 66, LANES.output - 6)}
    ${input}${boxes}${machine}${output}`);
}

export const foldExplainer: Explainer<Frame> = {
  id: "fold",
  theme: "keyed-fold",
  title: FOLD_SCRIPT.title,
  lede: FOLD_SCRIPT.lede,
  frames: runFold(FOLD_SCRIPT.batches, FOLD_SCRIPT.keys),
  notes: FOLD_SCRIPT.notes,
  legend: [
    { swatch: "accent", label: "a window arriving" },
    { swatch: "touched", label: "state a window reached" },
    { swatch: "warn", label: "reached, now Unhealthy" },
    { swatch: "dim", label: "state no window reached" },
    { swatch: "add", label: "+1 adds a state row" },
    { swatch: "remove", label: "−1 takes a state row back" },
  ],
  draw,
};
