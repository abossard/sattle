---
version: 1
slug: "src-ui-editorview-tsx"
primary_target: "src/ui/EditorView.tsx"
related_targets: ["src/ui/editor/EditorCanvas.tsx","src/ui/App.tsx"]
---

## Scope

Projection editor first (EditorView, EditorCanvas, Properties), then the shell (App header, status line) inherits the same system. Mode: Operate. Audience: modelers, operators and researchers equally; desk, mixed light.

## Direction contract

THESIS: a projection is an engineering drawing. The canvas is a sheet with zone references, operators are numbered parts, and every change is a line in a revision table. Refuses the floating card board on a soft grey ground.

OWN-WORLD: paper ground, ink strokes at two weights (1.5 for parts and the sheet frame, 1 for wires and hairlines), square corners, one drafting blue for the selection and focus, amber and red for diagnostics only. Workhorse sans for words; mono only for numbers, references and engine values, tabular.

STORY: the reader sees which part feeds which, can name any part by its reference and zone ("14 in C2"), and sees what changed in this draft without leaving the sheet.

FIRST VIEWPORT: header strip, projection bar, toolbar; the sheet fills the centre with zone numbers on the top edge and letters on the left; title block bottom right (module, projection, reads, writes, draft and revision); change table top right; properties on the right headed by reference and zone. Commit stays the one primary button in the toolbar.

FORM: Drawing sheet, list position 1, seed:a93cd76e85b1e30e

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
