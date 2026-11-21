import { lazy, type ComponentType } from "react";
import type { View } from "../state/types";
type Role = "input" | "master" | "projection" | "rule" | "identity" | "document";
export type Step = readonly [label: string, role: Role];

export interface Lesson {
  id: string;
  space?: string;
  source?: string;
  exercise?: boolean;
  title: string;
  goal: string;
  action: string;
  check: string;
  view: View;
  graph: string;
  projection?: string;
  module?: string;
  flow: Step[][];
  flowLabel?: string;
  companionFlow?: { label: string; levels: Step[][] };
  commands?: { label: string; code: string }[];
  files?: string[];
  build?: { name: string; inputs: string[]; source: string };
  example?: { type: string; label: string; explanation: string };
  prompt: string;
}

export interface GuidePack {
  lessons: readonly Lesson[];
  sources: Readonly<Record<string, string>>;
  panel: () => Promise<{ default: ComponentType<{ lesson: Lesson }> }>;
}
const modules = import.meta.env.VITE_CANVAS_EXAMPLES === "off" ? {} :
  import.meta.glob<{ default: GuidePack }>("../../examples/*/guide-pack.ts", { eager: true });
const packs = Object.values(modules).map(item => item.default);
export const LESSONS = packs.flatMap(pack => pack.lessons);
export const lessonOf = (id: string | null) => LESSONS.find(lesson => lesson.id === id);
export const sourceForLesson = (lesson: Lesson) => lesson.source ?? "engine";
export const projectionSources = Object.assign({}, ...packs.map(pack => pack.sources)) as Readonly<Record<string, string>>;
const panels = packs.map(pack => ({ ids: new Set(pack.lessons.map(lesson => lesson.id)), panel: lazy(pack.panel) }));
export const panelForLesson = (id: string) => panels.find(item => item.ids.has(id))?.panel;
