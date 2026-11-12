// Which items of a scrolled list are worth rendering: the ones in view and a margin around them. A calculation over sizes.

export interface Span { start: number; end: number; before: number; after: number }

/** Items `[start, end)` of a list with the given heights that a view of `height` scrolled to `top` shows, and the space taken by the rest. */
export function span(heights: readonly number[], top: number, height: number, margin = 600): Span {
  let y = 0, start = 0, before = 0;
  while (start < heights.length && y + heights[start] < top - margin) { y += heights[start]; start++; }
  before = y;
  let end = start;
  while (end < heights.length && y < top + height + margin) { y += heights[end]; end++; }
  let after = 0;
  for (let i = end; i < heights.length; i++) after += heights[i];
  return { start, end, before, after };
}
