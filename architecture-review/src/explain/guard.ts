import { GUARD_SCRIPT, runGuard, type Frame } from "../sim/guard";
import { arrow, chip, lane, svg, text, type Explainer } from "./kit";

const LANES = { source: 16, guard: 296, pass: 584, fail: 776 };
const HEIGHT = 300;
const rowY = (index: number) => 44 + index * 36;

function draw(frame: Frame): string {
  const rows = frame.checked.map((item, index) => {
    const isNew = index === frame.latest;
    const swatch = item.caught ? "warn" : "add";
    const source = chip({
      x: LANES.source + 12, y: rowY(index), swatch: isNew ? "accent" : "dim", label: item.word, width: 230,
      ...(isNew ? { from: { x: LANES.source - 40, y: rowY(index) }, delay: 0 } : {}),
    });
    if (!isNew) {
      const lanes = item.caught ? LANES.fail : LANES.pass;
      return source + chip({ x: lanes + 12, y: rowY(index), swatch, label: item.word, width: 170, struck: item.caught });
    }
    const target = item.caught ? LANES.fail : LANES.pass;
    const through = chip({ x: LANES.guard + 12, y: rowY(index), swatch, label: item.caught ? "domain word" : "neutral", width: 230, from: { x: LANES.source + 12, y: rowY(index) }, delay: 350 });
    const landed = chip({ x: target + 12, y: rowY(index), swatch, label: item.word, width: 170, from: { x: LANES.guard + 12, y: rowY(index) }, delay: 750, struck: item.caught });
    return source + through + landed;
  }).join("");
  const failed = frame.verdict === "fails";
  const verdict = frame.latest === -1
    ? text(LANES.guard + 12, 280, failed ? "Verdict: the build fails while any word is caught" : "Verdict: the build passes", failed ? "kit-verdict warn" : "kit-verdict", "start")
    : "";
  return svg(HEIGHT, "Source words checked by a guard that sends them to a passing or a failing build", `
    ${lane(LANES.source, 260, HEIGHT, "Engine source")}
    ${lane(LANES.guard, 260, HEIGHT, "Guard: domain words?", "calc")}
    ${lane(LANES.pass, 180, HEIGHT, "Build passes", "state")}
    ${lane(LANES.fail, 168, HEIGHT, "Build fails", "state")}
    ${arrow(LANES.source + 264, 150, LANES.guard - 6)}${arrow(LANES.guard + 264, 110, LANES.pass - 6)}${arrow(LANES.guard + 264, 190, LANES.fail - 6, 190, true)}
    ${rows}${verdict}`);
}

export const guardExplainer: Explainer<Frame> = {
  id: "guard",
  theme: "neutral-core",
  title: GUARD_SCRIPT.title,
  lede: GUARD_SCRIPT.lede,
  frames: runGuard(GUARD_SCRIPT.sources),
  notes: GUARD_SCRIPT.notes,
  legend: [
    { swatch: "accent", label: "the word being checked" },
    { swatch: "add", label: "passes the guard" },
    { swatch: "warn", label: "caught: a domain word" },
    { swatch: "dim", label: "already checked" },
  ],
  draw,
};
