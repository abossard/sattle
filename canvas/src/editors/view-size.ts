// The size of a scrolling view, for the editors that mount only what is in view.
import { useLayoutEffect, useState, type RefObject } from "react";

export interface Size { width: number; height: number }
const USUAL: Size = { width: 1200, height: 800 };

/**
 * The size of the element once the page has laid it out, and a usual window until then: the first render mounts a screenful, not
 * the whole document. `given` is a size the caller knows (a test, a measurement) and wins.
 */
export function useViewSize(ref: RefObject<HTMLElement | null>, given?: Partial<Size>): Size {
  const [size, setSize] = useState(USUAL);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const read = () => setSize((s) => { const width = el.clientWidth || s.width, height = el.clientHeight || s.height; return width === s.width && height === s.height ? s : { width, height }; });
    read();
    if (typeof ResizeObserver === "undefined") return;
    const seen = new ResizeObserver(read);
    seen.observe(el);
    return () => seen.disconnect();
  }, [ref]);
  return { width: given?.width ?? size.width, height: given?.height ?? size.height };
}
