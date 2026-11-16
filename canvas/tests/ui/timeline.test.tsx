// @vitest-environment jsdom
// Playing the timeline steps to the next point every 300 ms until the last one.
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { runtime } from "../../src/state/runtime";
import { useStore } from "../../src/state/store";
import { Timeline } from "../../src/ui/Timeline";
import { fakeWorkspace } from "../helpers";

afterEach(() => { cleanup(); runtime.ws = null; });

describe("Timeline play", () => {
  it("asks again when a seek leaves the shown time where it was (the space keeps showing second 0)", async () => {
    const hist = { cursor: 0, length: 0, patches: [], revisionAt: () => "1" };
    runtime.ws = { ...fakeWorkspace({ g: hist }), info: { graphs: [] }, source: {}, axisPoints: () => [0, 10], patchTimes: () => ({ times: [], source: "source" }), seek: () => {}, timeline: { k: "at", t: 0 } } as never;
    useStore.setState({ time: { now: 0, following: false, first: 0, last: 10 } });
    let seeks = 0;
    // the space answers each seek at once by saying it still shows second 0: after every seek the next point is the same one as before
    const off = useStore.subscribe((s, prev) => { if (s.time.now === 10 && prev.time.now !== 10) { seeks++; useStore.setState({ time: { ...s.time, now: 0 } }); } });
    try {
      render(<FluentProvider theme={webLightTheme}><Timeline graph="g" /></FluentProvider>);
      await userEvent.setup().click(screen.getByRole("button", { name: "Play" }));
      await waitFor(() => expect(seeks, "playback stalled after the first seek").toBeGreaterThanOrEqual(3), { timeout: 2000 });
    } finally { off(); }
  });
});
