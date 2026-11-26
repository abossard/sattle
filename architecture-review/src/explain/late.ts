import { clockTime, LATE_SCRIPT, minute, runLate, totalsInEventOrder, WINDOW, type Frame, type Total } from "../sim/late";
import { arrow, box, chip, lane, svg, text, weightLabel, type Explainer } from "./kit";

const LANES = { input: 16, windows: 300, output: 636 };
const HEIGHT = 300;
const rowY = (index: number) => 44 + index * 36;
const boxY = (window: number) => 40 + (window / WINDOW) * 62;
const list = (totals: readonly Total[]) => totals.map(({ window, total }) => `${minute(window)} = ${total}`).join(", ") || "none";

function draw(frame: Frame, index: number): string {
  const input = frame.arrivals.map(({ row, late }, at) => chip({
    x: LANES.input + 12, y: rowY(at), width: 226, swatch: late ? "warn" : "add",
    label: weightLabel(1, `${clockTime(row.event)}, ${row.errors} err${late ? ", late" : ""}`),
    from: { x: LANES.input - 40, y: rowY(at) }, delay: at * 80,
  })).join("");
  const boxes = frame.windows.map(({ window, total, shown }) => box({
    x: LANES.windows + 12, y: boxY(window), width: 276,
    key: `${minute(window)} to ${minute(window + WINDOW)}, shown from ${minute(window + 2 * WINDOW)}`,
    value: `${total} errors, ${shown ? "shown" : "held"}`,
    swatch: frame.revised.includes(window) ? "warn" : frame.touched.includes(window) ? "touched" : "dim", delay: 500,
  })).join("");
  const output = frame.output.map((change, at) => chip({
    x: LANES.output + 12, y: rowY(at), width: 200, swatch: change.weight > 0 ? "add" : "remove",
    label: weightLabel(change.weight, `${minute(change.row.window)} = ${change.row.total}`),
    from: { x: LANES.windows + 52, y: boxY(change.row.window) + 12 }, delay: 800 + at * 100,
  })).join("") || text(LANES.output + 12, 62, "nothing shown in this step", "lane-title");
  const last = index === LATE_SCRIPT.clocks.length - 1;
  const compare = last
    ? text(LANES.output + 12, 250, "Same rows in event order", "lane-title") + text(LANES.output + 12, 270, list(totalsInEventOrder(LATE_SCRIPT.rows)))
    : "";
  return svg(HEIGHT, "Rows arriving out of order reach one-minute windows; a late row revises a shown total", `
    ${lane(LANES.input, 250, HEIGHT, "Rows by arrival")}
    ${lane(LANES.windows, 300, HEIGHT, "Windows of api, lateness 1 minute", "state")}
    ${lane(LANES.output, 308, HEIGHT, "Shown totals (output)")}
    ${arrow(LANES.input + 254, 120, LANES.windows - 6)}${arrow(LANES.windows + 304, 120, LANES.output - 6)}
    ${text(LANES.windows + 12, 250, "Clock", "lane-title")}${text(LANES.windows + 12, 272, clockTime(frame.clock))}
    ${text(LANES.output + 12, 190, "Shown so far", "lane-title")}${text(LANES.output + 12, 210, list(frame.shown))}
    ${input}${boxes}${output}${compare}`);
}

export const lateExplainer: Explainer<Frame> = {
  id: "late",
  theme: "gkql-complete",
  title: LATE_SCRIPT.title,
  lede: LATE_SCRIPT.lede,
  frames: runLate(LATE_SCRIPT.rows, LATE_SCRIPT.clocks),
  notes: LATE_SCRIPT.notes,
  legend: [
    { swatch: "add", label: "+1 a row arrives or a total is shown" },
    { swatch: "remove", label: "−1 takes a shown total back" },
    { swatch: "warn", label: "late row and the shown window it revises" },
    { swatch: "touched", label: "window a row or the clock reached" },
    { swatch: "dim", label: "window nothing reached" },
  ],
  draw,
};
