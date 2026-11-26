// A Back, Play, Next stepper as a state machine. `at` is the current step, or null when every step shows.
export interface Stepper { readonly at: number | null; readonly playing: boolean; readonly count: number }
export type StepEvent = "back" | "next" | "play" | "pause" | "tick" | "all";

export const stepper = (count: number): Stepper => ({ at: null, playing: false, count });

export function step(state: Stepper, event: StepEvent): Stepper {
  const last = state.count - 1;
  switch (event) {
    case "next": return { ...state, playing: false, at: state.at === null ? 0 : Math.min(state.at + 1, last) };
    case "back": return { ...state, playing: false, at: state.at === null ? last : Math.max(state.at - 1, 0) };
    case "play": return { ...state, playing: true, at: state.at === null || state.at === last ? 0 : state.at };
    case "pause": return { ...state, playing: false };
    case "tick":
      if (!state.playing) return state;
      return state.at === null || state.at >= last ? { ...state, playing: false } : { ...state, at: state.at + 1 };
    case "all": return { ...state, playing: false, at: null };
  }
}
