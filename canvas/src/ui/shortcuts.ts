// Every keyboard shortcut, in one table: the help dialog and the tests read it.
export interface Shortcut { keys: string; does: string; where: string }
export const SHORTCUTS: Shortcut[] = [
  { keys: "?", does: "Show this list", where: "Anywhere" },
  { keys: "/", does: "Focus the search box", where: "Data views" },
  { keys: "Arrow keys", does: "Move the selection along edges", where: "Canvas" },
  { keys: "Enter", does: "Focus the neighbourhood of the selection", where: "Canvas" },
  { keys: "Space", does: "Collapse or expand a group", where: "Canvas" },
  { keys: "F", does: "Fit the graph", where: "Canvas" },
  { keys: "+ and -", does: "Zoom", where: "Canvas" },
  { keys: "Ctrl+Z, Ctrl+Shift+Z", does: "Undo and redo in the KQL text (typing and the notations' edits share one history)", where: "Projection editor" },
  { keys: "Ctrl+S", does: "Name and commit the draft", where: "Projection editor" },
];
