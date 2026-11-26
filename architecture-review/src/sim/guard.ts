// Simulator for "the core knows no domain": a simplified version of the engine's neutrality guard.
// Pure: a script of source words in, one frame per step out. The real guard lists many more words; this one knows two families.

export interface Checked { word: string; caught: boolean; why: string }
export interface Frame {
  /** Words checked so far, in order; the last one is the new word of this step (unless `latest` is -1). */
  checked: Checked[];
  latest: number;
  verdict: "passes" | "fails";
}

// "pod" as a word or inside a compound (PodsReady, podsByNode, nodePod, kubePod, nodePods), never inside a longer word (Podium, Podcast).
// "health" in any form, except the liveness route written exactly "/health".
const DOMAIN_WORD = /\b[Pp]ods?\b|\b[Pp]ods?(?=[A-Z_0-9])|(?<=[a-z])Pods?(?![a-z])|(?<!"\/)[Hh]ealth|"\/Health|"\/health(?!")/;

/** The domain word found in a source line, or undefined when the line is neutral. */
export const findDomainWord = (line: string): string | undefined => DOMAIN_WORD.exec(line)?.[0];

export const isCaught = (line: string) => findDomainWord(line) !== undefined;

export interface Source { word: string; why: string }

export function runGuard(script: Source[]): Frame[] {
  const checks: Checked[] = script.map((source) => ({ word: source.word, why: source.why, caught: isCaught(source.word) }));
  const frames: Frame[] = checks.map((_, index) => {
    const checked = checks.slice(0, index + 1);
    return { checked, latest: index, verdict: checked.some((c) => c.caught) ? "fails" : "passes" };
  });
  const last = frames[frames.length - 1];
  return [...frames, { checked: last.checked, latest: -1, verdict: last.verdict }];
}

export const GUARD_SCRIPT = {
  title: "A guard test keeps domain words out of the core",
  lede: "The core knows no domain: a test reads every engine source line and fails the build when a domain word comes back. This is a simplified version of the real guard, which knows many more words; it shows two families, the word pod and the word health.",
  sources: [
    { word: "RecordAdapter", why: "a neutral name" },
    { word: "Podium", why: "contains the letters but is another word" },
    { word: "\"/health\"", why: "the engine's own liveness route is allowed by its exact form" },
    { word: "podsByNode", why: "a pod word inside a compound name" },
    { word: "\"/HealthTree\"", why: "every other route form of health is caught" },
  ] as Source[],
  notes: [
    "RecordAdapter names no domain, so it passes.",
    "Podium only shares letters with the pod word, so it passes.",
    "The liveness route \"/health\" is the one allowed form, so it passes.",
    "podsByNode hides a pod word inside a compound, and the guard catches it.",
    "\"/HealthTree\" is a health route in another form, so the guard catches it.",
    "One caught word is enough: the build fails until the word is removed from the core.",
  ],
};
