---
name: Sattle canvas
description: A drawing sheet for reading graphs and editing the projections that write them, on Fluent UI v9.
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
rounded:
  sm: "1px"
  md: "2px"
  lg: "3px"
  xl: "4px"
components:
  wires-card:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "0px"
    width: "210px"
  badge:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink-2}"
    rounded: "{rounded.md}"
---

# Design System: Sattle canvas

## Overview

**Creative North Star: "The Drawing Sheet"**

The canvas reads like an engineering drawing: paper, ink, a ruled frame and one drafting blue. A projection is a sheet: its KQL text, numbered by line, with its steps, outline and wires beside it, and the engine's verdict on the draft below. Operators, reviewers and authors all read the same sheet, so the surface is quiet, dense and exact, built for long sessions in light and dark.

Everything is built on Fluent UI v9 components and tokens. The look lives in one theme file that overrides Fluent's palette, corners, shadows and fonts, so the shell inherits the sheet without per-component styling. High contrast is Fluent's own Teams high contrast theme, untouched.

Color carries meaning only where something is selected, focused or wrong. Ink does the structural work.

**Key Characteristics:**
- Paper and ink neutrals, near square corners (1 to 4px), hairline rules.
- One hue: drafting blue, for selection, focus, caret and live wires.
- Amber and red stay with diagnostics and errors.
- Tabular figures everywhere; monospace for ids, KQL and statement names.
- Technical drawing furniture: a framed sheet, line numbers, square cards and hairline wires.

## Colors

A cool-neutral paper and ink scale with a single blue accent. Light and dark are both full themes, not an inversion.

### Primary
- **Drafting Blue** (#1d5fa6, ramp from #060d19 to #f2f6fb): brand background, selection strokes, selected cards, the caret, the focus-adjacent accent color and the active wire. In dark mode the foreground and link role moves to Light Drafting Blue (#8fb0d4) and the compound stroke to #759dc9.
- **Drafting Tint** (#dae5f1): the selection highlight and marquee fill (Fluent brand background 2, at 30% for the marquee).

### Neutral (light)
- **Paper** (#ffffff): sheet, cards, header and status bar.
- **Paper 2** (#f6f7f8), **Paper 3** (#eceef1), **Paper 4** (#e4e6ea): panels and the graph view floor. Paper 3 is the WebGL graph background.
- **Ink** (#202124): text, frame, card outlines, wires. Ink 2 (#46484d), Ink 3 (#5f6268, also the accessible stroke) and Ink 4 (#696b70) for secondary text and quiet drawing lines.
- **Rule** (#868990): control strokes. Rule 2 (#c9cbd0): panel dividers. Rule 3 (#e3e5e8): faint separators.

### Neutral (dark)
- **Night** (#1b1d20), Night 2 (#17191c), Night 3 (#121316), Night 4 (#0e0f11): sheet through floor. The WebGL graph background and panel are aligned to Night 3 and Night.
- **Chalk** (#e6e7e9), Chalk 2 (#c3c5ca), Chalk 3 (#a4a7ad, also the accessible stroke): text steps.
- **Night Rule** (#80838a), Night Rule 2 (#3a3d42), Night Rule 3 (#2c2e32): strokes, dividers, hairlines.

### Diagnostics
- Fluent's palette red border marks an errored card (dashed outline). Fluent's warning intent marks notices. Neither is used for decoration.
- Status colors (success, warning, danger) belong to states and diagnostics only. Roles, counts and changes are neutral.

### Named Rules
**The One Blue Rule.** Drafting blue means selected, focused or live. Any other use of blue is wrong.

**The Ink Does the Work Rule.** Structure is drawn in ink and rule weights, not in tinted fills. Amber and red belong to diagnostics only.

## Typography

**Body Font:** Segoe UI Variable Text (with Segoe UI, system-ui, -apple-system, sans-serif)
**Mono Font:** Cascadia Mono (with SF Mono, ui-monospace, Menlo, Consolas)

**Character:** A plain system face for words, a monospace for everything that is a reference: ids, KQL text, statement names, change steps. Figures are tabular across the whole provider.

### Hierarchy
- **Brand** (600, 14px): the product name in the header.
- **Card title** (600, 13px): statement names in the Steps notation.
- **Body** (400, 14px, 20px line): panels, forms, tables.
- **Card line** (mono, 11px): the KQL of a step.

### Named Rules
**The Reference Is Mono Rule.** Anything that names a place or an id (a line, a statement, a projection id, a change step) is set in the monospace.

## Layout

The editor is one column: the projection bar and the toolbar, the KQL text with its notation beside it (Steps, Outline or Wires), and the draft details below (at least 140px, 28% of the height). The shell is a four row grid at 100dvh: header, view tabs, content, status bar.

The Wires notation places the inputs, the `let`s and the steps as cards of 210 by 40px in columns 56px apart, wired by what each statement reads; steps of one type share a dashed frame.

Toolbars and tab lists that overflow scroll horizontally and fade their last 32px, so a cut label reads as scrollable. The header wraps; the view tabs scroll under the same fade below 700px.

Spacing uses Fluent's spacing tokens (XXS to L); no custom spacing scale beyond the card constants above.

## Elevation & Depth

Flat by default. Depth is drawn with rule weights, not shadow: card outline 1.5px ink, wires 1px (2px drafting blue when they belong to the selected card), panel dividers 1px.

### Shadow Vocabulary
- **shadow2** (`0 1px 2px rgba(0,0,0,.10)`), **shadow4** (`0 1px 3px rgba(0,0,0,.12)`), **shadow8** (`0 2px 6px rgba(0,0,0,.14)`), **shadow16** (`0 4px 12px rgba(0,0,0,.16)`): Fluent's soft shadow slots, flattened and tightened. They serve menus, popovers and dialogs only.

### Named Rules
**The Rule Weight Rule.** On the sheet, importance is stroke width and dash pattern. Selected is thicker and blue; an input card and the frame of steps of one type are dashed.

## Shapes

Near square. Corners are 1px (small), 2px (medium), 3px (large), 4px (extra large). Cards are fully square. Badges have medium (2px) corners and an outline.

Dash vocabulary on the sheet: 4 3 for the frame around steps of one type; an input card has a dashed outline.

## Components

### Wires card
Square, paper fill, 1.5px ink outline, 210 by 40px, the statement's title in semibold (Fluent size 200) over what it reads in the smallest size, ink 3. An input card has a dashed outline; the selected card takes the brand tint and the drafting blue stroke. Wires are 1px ink curves from what a statement reads to the statement, 2px drafting blue for the selected card. A card selects its statement in the text.

### Badges
Outline, 2px corners (the pill shape is overridden once in `canvas.css`), informative (neutral) color. A badge names a role, a count or a reference: `projection`, `8 supporting`, `ontology acme.ops@1.0.0`. Only a state or a diagnostic takes a status color (Healthy, Unhealthy, origin, a meter over its threshold). Fluent's default badge color is brand, so every badge states its color. It never stretches: badges are capped to their content width.

The direction badge adds a glyph to the label for the direction class of a projection (forward only, bidirectional, mixed).

### Revision counts
The timeline's `+ added`, `− removed` and `Δ changed` are neutral badges; the glyph and the word carry the meaning. A zero count drops its outline (ghost appearance).

### Header, tabs, toolbars
Paper header with a 1px rule underneath, a 16px sheet mark (a frame with zone ticks, stroked in current color) beside the product name, Fluent tab list for the views. Toolbars are small, with subtle icon buttons; the draft toolbar holds the single primary button, Commit, at the right.

### Status bar
Paper strip with a 1px top rule, small text, wrapping.

## Do's and Don'ts

### Do:
- **Do** use Fluent components and theme tokens; change the look in `theme.ts`, not per component.
- **Do** draw the sheet in ink, with stroke weight and dash pattern carrying meaning.
- **Do** use drafting blue only for selection, focus, caret and live wires.
- **Do** keep corners at 4px or less; keep cards square.
- **Do** set KQL, statement names and ids in the monospace.
- **Do** keep high contrast as Fluent's own theme; test every new surface in light, dark and contrast.
- **Do** let long toolbars scroll with the 32px fade instead of clipping silently.

### Don't:
- **Don't** put amber or red on anything that is not a diagnostic or an error.
- **Don't** use soft large shadows, gradients as fills, or rounded cards.
- **Don't** let a badge stretch to the width of its column.
- **Don't** leave a badge on Fluent's default brand color, or put a sentence in a badge.
- **Don't** mark a role or a kind with a colored side stripe; use a small square swatch before the label, as the type legend does.
- **Don't** introduce a second accent hue.

## Not canonized

- The WebGL renderer palette (`src/render/theme.ts`) keeps its older role hues (added, removed, changed, identity, source, and the per-kind hues). Only its background and panel match the sheet. They are not part of this system until aligned. The developer guide's role squares use these hues.
- The 9px caption and 9px port label sizes are below Fluent's smallest text token. They are recorded because the build uses them, not as a size to add elsewhere.
