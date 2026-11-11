import { cleanup, render, waitFor } from "@testing-library/react";
import axe from "axe-core";
import { afterEach } from "vitest";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import type { Dialog, ThemeName, View } from "../../src/state/types";
import { App } from "../../src/ui/App";

afterEach(() => { cleanup(); runtime.ws?.dispose(); runtime.ws = null; });

/** Render the application on a source and wait until it has loaded. */
export async function openApp(source = "synth-small", view: View = "data", theme: ThemeName = "light") {
  dispatch({ type: "ui/theme", theme });
  dispatch({ type: "ui/view", view });
  dispatch({ type: "ui/dialog", dialog: null });
  dispatch({ type: "source/opening", id: source });
  const utils = render(<App />);
  await waitFor(() => { if (useStore.getState().load.status !== "ready") throw new Error("loading"); }, { timeout: 20000 });
  return utils;
}

export const openDialog = (dialog: Dialog) => dispatch({ type: "ui/dialog", dialog });

/**
 * axe violations of a subtree. Color contrast needs real layout and rendering, which jsdom does not have: it is checked in a real browser
 * (README, "Accessibility") and disabled here.
 */
export async function violations(root: Element = document.body): Promise<string[]> {
  const r = await axe.run(root, { rules: { "color-contrast": { enabled: false }, region: { enabled: false } } });
  return r.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.slice(0, 3).map((n) => n.target.join(" ")).join(" | ")})`);
}
