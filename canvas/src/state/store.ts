// One Zustand store. `dispatch` is the only way in: effects apply the action to the runtime, then the pure reducer folds it into state.
import { create } from "zustand";
import { runEffects } from "./effects";
import { initialState, reduce } from "./reducer";
import type { Action, State, ThemeName } from "./types";

type Store = State & { dispatch: (a: Action) => void };

function savedTheme(): ThemeName {
  try {
    const t = localStorage.getItem("sattle-canvas-theme");
    if (t === "light" || t === "dark" || t === "contrast") return t;
  } catch { /* storage unavailable */ }
  return typeof matchMedia !== "undefined" && matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

export const useStore = create<Store>((set, get) => ({
  ...initialState(),
  theme: savedTheme(),
  dispatch: (a) => {
    runEffects(a, get(), get().dispatch);
    set((s) => reduce(s, a));
    if (a.type === "ui/theme") try { localStorage.setItem("sattle-canvas-theme", a.theme); } catch { /* storage unavailable */ }
  },
}));

export const dispatch = (a: Action): void => useStore.getState().dispatch(a);
