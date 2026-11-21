import { createRoot } from "react-dom/client";
import { installPerf } from "./app/perf";
import { runtime } from "./state/runtime";
import { dispatch, useStore } from "./state/store";
import { actionsFor, formatHash, parseHash } from "./state/url";
import { App } from "./ui/App";

const route = () => { const s = useStore.getState(); return { source: s.sourceId || "demo", view: s.view, guide: s.guide }; };
// hash to store: at start and whenever the hash changes (typed, linked, back and forward)
const follow = () => { for (const a of actionsFor(parseHash(location.hash, route()), useStore.getState())) dispatch(a); };
follow();
addEventListener("hashchange", follow);
addEventListener("popstate", follow);
// store to hash: when the source or the view changes
useStore.subscribe((s, prev) => {
  if (!s.sourceId || (s.sourceId === prev.sourceId && s.view === prev.view && s.guide === prev.guide)) return;
  const h = formatHash(route());
  if (location.hash !== h) history.replaceState(null, "", h);
});
installPerf(() => runtime.ws, () => useStore.getState().activeGraph);
Object.defineProperty(window, "__sattle", { value: { get ws() { return runtime.ws; }, get session() { return runtime.session; }, get preview() { return runtime.preview; }, store: useStore } });
createRoot(document.getElementById("app")!).render(<App />);
