// Whether the space follows the newest data or shows a time: one value, decided here. The graphs, the telemetry values and the timeline read it.

export type Timeline = { k: "live" } | { k: "at"; t: number };

export const LIVE: Timeline = { k: "live" };

/** A seek to `t` on the shared axis: at or past its newest point the space follows the newest data again. */
export const timelineAt = (points: readonly number[], t: number): Timeline => (!points.length || t >= points[points.length - 1] ? LIVE : { k: "at", t });

/** The time the space shows: the newest point when it follows, else the time it was moved to. */
export const shownTime = (tl: Timeline, newest: number): number => (tl.k === "live" ? newest : tl.t);
