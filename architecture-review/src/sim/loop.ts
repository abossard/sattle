// Simulator for "one issue through the managed run": a state machine with a pure transition (state, event) -> state.
// Pure: a script of event groups in, one frame per group out.

export type Stage = "plan" | "doing" | "verify" | "review" | "done";
export type Criterion = "open" | "proof" | "ticked";

export type Event =
  | { kind: "open" }
  | { kind: "claim" }
  | { kind: "proof" }
  | { kind: "fail"; criterion: number }
  | { kind: "handover" }
  | { kind: "pass" };

export interface State {
  stage: Stage;
  holder: number | null;
  criteria: Criterion[];
  rounds: number;
  sessions: number;
  closed: boolean;
}

export interface Frame {
  events: Event[];
  state: State;
}

export const STAGES: readonly Stage[] = ["plan", "doing", "verify", "review", "done"];

export const initial: State = { stage: "plan", holder: null, criteria: ["open", "open", "open"], rounds: 0, sessions: 0, closed: false };

/** Events that do not fit the current state leave it unchanged. */
export function step(state: State, event: Event): State {
  switch (event.kind) {
    case "open":
      return initial;
    case "claim":
      if (state.holder !== null || state.closed) return state;
      return { ...state, stage: state.stage === "plan" ? "doing" : state.stage, holder: state.sessions + 1, sessions: state.sessions + 1 };
    case "proof":
      if (state.stage !== "doing") return state;
      return { ...state, stage: "review", criteria: state.criteria.map((c) => (c === "open" ? "proof" : c)) };
    case "fail":
      if (state.stage !== "review") return state;
      return { ...state, stage: "doing", rounds: state.rounds + 1, criteria: state.criteria.map((c, i) => (i === event.criterion ? "open" : c)) };
    case "handover":
      if (state.holder === null || state.stage === "done") return state;
      return { ...state, holder: null };
    case "pass":
      if (state.stage !== "review") return state;
      return { ...state, stage: "done", rounds: state.rounds + 1, criteria: state.criteria.map(() => "ticked"), closed: true };
  }
}

export function runLoop(script: Event[][]): Frame[] {
  let state = initial;
  return script.map((events) => {
    state = events.reduce(step, state);
    return { events, state };
  });
}

export const LOOP_SCRIPT = {
  title: "One issue through the managed run",
  lede: "An issue has numbered acceptance criteria. A worker holds its card and moves it from plan to doing to review, posting proof for each criterion. An independent review either passes it, and the issue closes, or sends it back. When a worker runs low on context it hands over, and a fresh one picks the card up.",
  script: [
    [{ kind: "open" }],
    [{ kind: "claim" }],
    [{ kind: "proof" }],
    [{ kind: "fail", criterion: 1 }],
    [{ kind: "handover" }, { kind: "claim" }],
    [{ kind: "proof" }],
    [{ kind: "pass" }],
  ] as Event[][],
  notes: [
    "The issue is opened with three acceptance criteria, criterion 1 to criterion 3, all open, and its card waits at plan.",
    "The first worker claims the card and moves it to doing.",
    "The worker posts proof for every criterion at a named commit, and the card moves to review.",
    "The independent review fails criterion 2. The card goes back to doing, criterion 2 is open again, and the review round count goes to 1.",
    "At 30% context the worker writes a handover and releases the card, and a second worker claims it and goes on.",
    "The second worker posts proof for criterion 2 only, since criteria 1 and 3 already have proof, and the card returns to review.",
    "The second review passes: all criteria are ticked, the orchestrator closes the issue, and the run took 2 review rounds and 2 workers.",
  ],
};
