// Simulator for "a keyed fold": each key's windows are read in time order and a small state is carried from one to the next.
// Pure: a script of window batches in, one frame per batch out. The policy is data, not code.

export type Mode = "Healthy" | "Unhealthy";
export interface Window { key: string; minute: number; cpu: number }
export interface KeyState { key: string; mode: Mode; run: number }
export interface StateRow { key: string; mode: Mode }
export interface Change<T> { row: T; weight: 1 | -1 }
export interface Policy { above: number; below: number; enterAfter: number; exitAfter: number }

export interface Frame {
  input: Window[];
  touched: string[];
  before: KeyState[];
  after: KeyState[];
  output: Change<StateRow>[];
}

export const POLICY: Policy = { above: 8.0, below: 7.0, enterAfter: 2, exitAfter: 3 };

/** One window moves one key's state: `run` counts windows in a row toward the other mode; any other window resets it. */
export function step(state: KeyState, cpu: number, policy: Policy): KeyState {
  if (state.mode === "Healthy") {
    if (!(cpu > policy.above)) return { ...state, run: 0 };
    return state.run + 1 >= policy.enterAfter ? { ...state, mode: "Unhealthy", run: 0 } : { ...state, run: state.run + 1 };
  }
  if (!(cpu < policy.below)) return { ...state, run: 0 };
  return state.run + 1 >= policy.exitAfter ? { ...state, mode: "Healthy", run: 0 } : { ...state, run: state.run + 1 };
}

export function runFold(script: Window[][], keys: string[], policy: Policy = POLICY): Frame[] {
  let state: KeyState[] = [...keys].sort().map((key) => ({ key, mode: "Healthy", run: 0 }));
  return script.map((input) => {
    const ordered = [...input].sort((a, b) => a.minute - b.minute);
    const touched = [...new Set(ordered.map((window) => window.key))].sort();
    const after = state.map((current) => ordered.filter((window) => window.key === current.key).reduce((s, window) => step(s, window.cpu, policy), current));
    const output: Change<StateRow>[] = touched.flatMap((key) => {
      const old = state.find((s) => s.key === key)!;
      const now = after.find((s) => s.key === key)!;
      if (old.mode === now.mode) return [];
      return [{ row: { key, mode: old.mode }, weight: -1 as const }, { row: { key, mode: now.mode }, weight: 1 as const }];
    });
    const frame = { input, touched, before: state, after, output };
    state = after;
    return frame;
  });
}

const at = (key: string, minute: number, cpu: number): Window => ({ key, minute, cpu });

export const FOLD_SCRIPT = {
  title: "One keyed fold carries a state from window to window",
  lede: "A keyed fold reads each key's one-minute windows in time order and carries a small state from one window to the next, so it replaced a special threshold operator and a health policy is plain gkql with its numbers as data. In this example every key starts Healthy, turns Unhealthy after 2 windows in a row above 8.0 CPU and turns back after 3 windows in a row below 7.0; any other window resets the count. A key's state row changes only when its mode flips.",
  keys: ["api", "web"],
  batches: [
    [at("api", 1, 9.1)],
    [at("api", 2, 9.4)],
    [at("web", 2, 3.0)],
    [at("api", 3, 6.5)],
    [at("api", 4, 6.2)],
    [at("api", 5, 6.0)],
  ],
  notes: [
    "api's first window is above 8.0, so its count is 1 and it stays Healthy; nothing goes out and web is not touched.",
    "A second api window above 8.0 completes the run, so api Healthy is taken back (−1) and api Unhealthy added (+1), while web is not touched.",
    "A web window of 3.0 reaches only web's state, which stays Healthy with nothing to send, and api is not touched.",
    "api drops to 6.5, its first window below 7.0, so the count starts at 1 and it stays Unhealthy; nothing goes out and web is not touched.",
    "A second api window below 7.0 raises the count to 2, still short of 3, so nothing goes out and web is not touched.",
    "The third api window below 7.0 flips api back, as −1 api Unhealthy and +1 api Healthy, and web is not touched.",
  ],
};
