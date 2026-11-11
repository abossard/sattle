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
  { keys: "Ctrl+Z, Ctrl+Shift+Z", does: "Undo and redo (99 steps)", where: "Projection editor" },
  { keys: "Ctrl+C, Ctrl+V, Ctrl+D", does: "Copy, paste and duplicate the selected operators", where: "Projection editor" },
  { keys: "Delete", does: "Delete the selection", where: "Projection editor" },
  { keys: "Arrow keys", does: "Select the next operator in that direction", where: "Projection editor" },
  { keys: "Shift+Arrow keys", does: "Move the selected operators (Alt for a fine step)", where: "Projection editor" },
  { keys: "A", does: "Add an operator (opens the quick-add list)", where: "Projection editor" },
  { keys: "W", does: "Wire the selected output to an input (pick the target, Enter to connect)", where: "Projection editor" },
  { keys: "Ctrl+A", does: "Select all operators", where: "Projection editor" },
  { keys: "L", does: "Select the loop of the fixpoint (it and its step operators) that has the selection", where: "Projection editor" },
  { keys: "M", does: "Select a region: arrow keys resize it from the selected operator (Shift for fine steps), Enter selects what it touches, Escape cancels", where: "Projection editor" },
  { keys: "Ctrl+G", does: "Move the selection into a new projection", where: "Projection editor" },
  { keys: "Ctrl+S", does: "Name and commit the draft", where: "Projection editor" },
  { keys: "Escape", does: "Cancel wiring, clear the selection, close a list", where: "Projection editor" },
];
