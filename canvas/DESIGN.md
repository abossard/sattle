---
name: Sattle canvas
description: A drawing sheet for reading and editing graph projections, on Fluent UI v9.
world: "a93cd76e85b1e30e"
colors:
  drafting-blue: "#1d5fa6"
  drafting-blue-light: "#8fb0d4"
  drafting-tint: "#dae5f1"
  paper: "#ffffff"
  paper-2: "#f6f7f8"
  paper-3: "#eceef1"
  ink: "#202124"
  ink-2: "#46484d"
  ink-3: "#5f6268"
  rule: "#868990"
  rule-2: "#c9cbd0"
  rule-3: "#e3e5e8"
  night: "#1b1d20"
  night-2: "#17191c"
  night-3: "#121316"
  night-4: "#0e0f11"
  chalk: "#e6e7e9"
  chalk-2: "#c3c5ca"
  chalk-3: "#a4a7ad"
  night-rule: "#80838a"
  night-rule-2: "#3a3d42"
  night-rule-3: "#2c2e32"
typography:
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, -apple-system, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: "20px"
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, -apple-system, sans-serif"
    fontSize: "13px"
    fontWeight: 600
  label-mono:
    fontFamily: "Cascadia Mono, SF Mono, ui-monospace, Menlo, Consolas, monospace"
    fontSize: "11px"
    fontWeight: 400
  caption-caps:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, -apple-system, sans-serif"
    fontSize: "9px"
    fontWeight: 400
    lineHeight: "11px"
    letterSpacing: "0.06em"
rounded:
  sm: "1px"
  md: "2px"
  lg: "3px"
  xl: "4px"
  balloon: "50%"
spacing:
  zone-w: "310px"
  zone-h: "160px"
  margin: "28px"
  sheet-gutter: "24px"
components:
  sheet-frame:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "0px"
  part-card:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "0px"
    width: "220px"
  part-balloon:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "{rounded.balloon}"
    size: "20px"
  part-balloon-selected:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.drafting-blue}"
  title-block:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "0px"
    width: "520px"
  badge-direction:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink-2}"
    rounded: "{rounded.sm}"
---

# Design System: Sattle canvas

## Overview

**Creative North Star: "The Drawing Sheet"**

The canvas reads like an engineering drawing: paper, ink, a ruled frame and one drafting blue. A projection is a sheet. Its parts are numbered, its zones are lettered and numbered along the margin, and its latest changes sit in a title block in the lower right corner. Operators, reviewers and authors all read the same sheet, so the surface is quiet, dense and exact, built for long sessions in light and dark.

Everything is built on Fluent UI v9 components and tokens. The look lives in one theme file that overrides Fluent's palette, corners, shadows and fonts, so the shell inherits the sheet without per-component styling. High contrast is Fluent's own Teams high contrast theme, untouched.

Color carries meaning only where something is selected, focused or wrong. Ink does the structural work.

**Key Characteristics:**
- Paper and ink neutrals, near square corners (1 to 4px), hairline rules.
- One hue: drafting blue, for selection, focus, caret and live wires.
- Amber and red stay with diagnostics and errors.
- Tabular figures everywhere; monospace for ids, ports and zone references.
- Technical drawing furniture: frame, ticks, balloons, title block.

## Colors

A cool-neutral paper and ink scale with a single blue accent. Light and dark are both full themes, not an inversion.

### Primary
- **Drafting Blue** (#1d5fa6, ramp from #060d19 to #f2f6fb): brand background, selection strokes, selected balloons, the caret, the focus-adjacent accent color and the active wire. In dark mode the foreground and link role moves to Light Drafting Blue (#8fb0d4) and the compound stroke to #759dc9.
- **Drafting Tint** (#dae5f1): the selection highlight and marquee fill (Fluent brand background 2, at 30% for the marquee).

### Neutral (light)
- **Paper** (#ffffff): sheet, cards, title block, header and status bar.
- **Paper 2** (#f6f7f8), **Paper 3** (#eceef1), **Paper 4** (#e4e6ea): panels and the graph view floor. Paper 3 is the WebGL graph background.
- **Ink** (#202124): text, frame, card outlines, wires, balloon outlines. Ink 2 (#46484d), Ink 3 (#5f6268, also the accessible stroke) and Ink 4 (#696b70) for secondary text and quiet drawing lines.
- **Rule** (#868990): control strokes. Rule 2 (#c9cbd0): panel dividers. Rule 3 (#e3e5e8): faint separators.

### Neutral (dark)
- **Night** (#1b1d20), Night 2 (#17191c), Night 3 (#121316), Night 4 (#0e0f11): sheet through floor. The WebGL graph background and panel are aligned to Night 3 and Night.
- **Chalk** (#e6e7e9), Chalk 2 (#c3c5ca), Chalk 3 (#a4a7ad, also the accessible stroke): text steps.
- **Night Rule** (#80838a), Night Rule 2 (#3a3d42), Night Rule 3 (#2c2e32): strokes, dividers, hairlines.

### Diagnostics
- Fluent's palette red border marks an errored card (dashed outline). Fluent's warning intent marks notices. Neither is used for decoration.

### Named Rules
**The One Blue Rule.** Drafting blue means selected, focused or live. Any other use of blue is wrong.

**The Ink Does the Work Rule.** Structure is drawn in ink and rule weights, not in tinted fills. Amber and red belong to diagnostics only.

## Typography

**Body Font:** Segoe UI Variable Text (with Segoe UI, system-ui, -apple-system, sans-serif)
**Mono Font:** Cascadia Mono (with SF Mono, ui-monospace, Menlo, Consolas)

**Character:** A plain system face for words, a monospace for everything that is a reference: ids, ports, zone labels, change steps. Figures are tabular across the whole provider.

### Hierarchy
- **Brand** (600, 14px): the product name in the header.
- **Card title** (600, 13px): part titles on the sheet.
- **Body** (400, 14px, 20px line): panels, forms, tables.
- **Card line** (mono, 11px): port names and card rows. Port labels use 9px.
- **Zone and balloon text** (mono, 11px and 10px): margin references and part numbers.
- **Cell caption** (9px caps, 0.06em tracking, 11px line): the small captions of the title block cells.
- **Cell value** (mono, 10px): title block values.

### Named Rules
**The Reference Is Mono Rule.** Anything that names a place or an id (zone, part number, port, projection id, change step) is set in the monospace.

## Layout

The editor is a three column grid: part list (200 to 260px), the sheet (flexible), properties (260 to 340px). Below 1000px it collapses to one column with the sheet first (at least 70dvh). The shell is a four row grid at 100dvh: header, view tabs, content, status bar.

The sheet has a 28px margin ring that holds zone ticks and labels. Zones are 310px wide (card width plus column gap) and 160px high; columns are numbered from 1 along the top, rows lettered from A down the side, and a part's zone (for example B3) is computed from its position. The sheet extends 170px below the last part so the title block never covers a part. The title block is pinned 24px from the lower right corner, at most 520px wide, three cells per row, and is hidden below 600px.

Toolbars and tab lists that overflow scroll horizontally and fade their last 32px, so a cut label reads as scrollable. The header wraps; the view tabs scroll under the same fade below 700px.

Spacing uses Fluent's spacing tokens (XXS to L); no custom spacing scale beyond the sheet constants above.

## Elevation & Depth

Flat by default. Depth is drawn with rule weights, not shadow: sheet frame 1.5px ink, inner rule 1px, card outline 1.5px (2.5px when selected), table cells 0.75px.

### Shadow Vocabulary
- **shadow2** (`0 1px 2px rgba(0,0,0,.10)`), **shadow4** (`0 1px 3px rgba(0,0,0,.12)`), **shadow8** (`0 2px 6px rgba(0,0,0,.14)`), **shadow16** (`0 4px 12px rgba(0,0,0,.16)`): Fluent's soft shadow slots, flattened and tightened. They serve menus, popovers and dialogs only.

### Named Rules
**The Rule Weight Rule.** On the sheet, importance is stroke width and dash pattern. Selected is thicker and blue; errored is dashed red; a binding is dotted.

## Shapes

Near square. Corners are 1px (small), 2px (medium), 3px (large), 4px (extra large). Cards, the sheet frame and the title block are fully square. Badges use the square shape with an outline. The one round form is the part balloon, a circle (20px in the panel, radius 10 on the sheet) with a thin ink outline.

Dash vocabulary on the sheet: dotted 2 4 for bindings and loops, 3 2 for notes, 4 3 for the marquee, 6 3 for an errored card, 7 4 for a wire being dragged, 8 3 2 3 for groups. Ports are 6px radius circles with a crosshair cursor.

## Components

### Sheet frame
Double frame: outer rule 1.5px ink, inner rule 1px ink, ticks across the 28px margin at each zone boundary, zone letters and numbers in mono 11px, ink 3. Background is paper.

### Part card
Square, paper fill, 1.5px ink outline, 220px wide, title in 13px semibold with mono rows beneath. Selected: 2.5px drafting blue outline. Errored: dashed red outline.

### Part balloon
A numbered circle at the top right corner of each card (radius 10, paper fill, 1px ink outline, mono 10px). The same balloon, 20px, heads the properties panel for the selected part. Selected: 2px drafting blue outline.

### Wires and ports
Wires are 1px ink bezier curves, 2px drafting blue when selected. A port is a 6px paper circle with a 1.25px ink outline; a valid drop target gets a tint fill and a 2.5px blue outline, an invalid one fades to 25%.

### Title block
Table in the lower right of the sheet: 1.5px ink outer border, 0.75px ink cell borders, 9px caps captions over mono values. It lists module, projection, reads, writes and the latest changes of the draft, with the base revision.

### Direction badge
Outline badge, square, informative color, a glyph and a label for the direction class of a projection (forward only, bidirectional, mixed). It never stretches: badges are capped to their content width.

### Header, tabs, toolbars
Paper header with a 1px rule underneath, a 16px sheet mark (a frame with zone ticks, stroked in current color) beside the product name, Fluent tab list for the views. Toolbars are small, with subtle icon buttons; the draft toolbar holds the single primary button, Commit, at the right.

### Status bar
Paper strip with a 1px top rule, small text, wrapping.

## Do's and Don'ts

### Do:
- **Do** use Fluent components and theme tokens; change the look in `theme.ts`, not per component.
- **Do** draw the sheet in ink, with stroke weight and dash pattern carrying meaning.
- **Do** use drafting blue only for selection, focus, caret and live wires.
- **Do** keep corners at 4px or less; keep cards, frame and title block square.
- **Do** set zone labels, part numbers, ports and ids in the monospace.
- **Do** keep high contrast as Fluent's own theme; test every new surface in light, dark and contrast.
- **Do** let long toolbars scroll with the 32px fade instead of clipping silently.

### Don't:
- **Don't** put amber or red on anything that is not a diagnostic or an error.
- **Don't** use soft large shadows, gradients as fills, or rounded cards.
- **Don't** let a badge stretch to the width of its column.
- **Don't** give the title block or zone labels a role beyond the sheet; they belong to the drawing view.
- **Don't** introduce a second accent hue.

## Not canonized

- The WebGL renderer palette (`src/render/theme.ts`) keeps its older role hues (added, removed, changed, identity, source, and the per-kind hues). Only its background and panel match the sheet. They are not part of this system until aligned.
- The 9px caption and 9px port label sizes are below Fluent's smallest text token. They are recorded because the build uses them, not as a size to add elsewhere.
