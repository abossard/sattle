export {}; // a module: the top-level await below needs it
// jsdom has no WebGL2 and no layout: canvas contexts answer null, so the canvas host shows its fallback.
if (typeof HTMLCanvasElement !== "undefined") HTMLCanvasElement.prototype.getContext = (() => null) as never;
if (typeof window !== "undefined") {
  window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as never;
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as never;
}

// `findBy...` and `waitFor` wait on the condition, not on a time: the limit is a ceiling that a busy machine (another suite beside this one,
// a big tree to query in jsdom) does not reach. A condition that is met returns at once.
if (typeof window !== "undefined") {
  const { configure } = await import("@testing-library/dom");
  configure({ asyncUtilTimeout: 15000 });
}
