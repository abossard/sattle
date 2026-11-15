# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users
Three audiences, served equally (confirmed):
- Operators (platform and site reliability engineers) who open a workload graph to see whether it is healthy and which part is to blame, on the synthetic estate today and on real Azure data in Phase 2.
- Researchers and reviewers, math-literate readers of the white paper, who inspect projections, revisions, lineage and change sets as worked examples of the theory.
- Projection authors who build and edit projections in the editor (four notations and text) and check what their output graphs hold.

## Product Purpose
The canvas is the client of the Sattle engine: it shows the graphs the engine computes (a master graph and the projections over it), how they change revision by revision, and the telemetry on them, and it lets an author edit the projections that produce them. Success: a person answers their question (is it healthy, what changed, why does this element have this state, does my projection produce what I meant) from what is on screen, without reading engine internals.

## Positioning
Every view is a function of an engine output at a revision: incremental graphs with lineage, a timeline over revisions, a roll-up whose cause relation and origins are shown, and an editor whose commands are the engine's own edit protocol. Nothing is hand-drawn or mocked.

## Operating Context
- Sources: static engine output (the synthetic estate demo, `public/demo/estate`) or a live engine over HTTP and WebSocket.
- Views: data graph (WebGL graph, tree, document, telemetry), graph space, projection editor, ontology.
- Used on desktop at 1280 px and wider, dark and light; long sessions of reading and scrubbing.

## Capabilities and Constraints
- React with Fluent UI v9 components and tokens: a binding commitment (confirmed). The redesign works inside Fluent.
- The graph renderer is WebGL with its own label placement; it stays.
- Redux-style store, pure transitions, actions at the edges (repository design rules).
- Terminology from the engine and the paper: graph, projection, revision, patch, roll-up, origin, cause relation, edge kinds (composition, dependency, communication, support).
- Examples must answer a user's question: no toy chains, no unconnected nodes, no internal ids as labels.

## Evidence on Hand
- The synthetic estate demo (twelve graphs, workload model with scenarios A to E) and the reference projections built in each notation (`tests/editors/kql-references.test.tsx`).
- Two real captures exist outside the repository; their names and ids must not appear in tracked files.
- No user studies, testimonials or benchmarks against other tools exist; none may be invented.

## Product Principles
1. Every element on screen is traceable to engine output at a revision.
2. A view answers a question; layout, labels and defaults serve that question.
3. Precision over decoration: units, counts and states are stated, never implied by color alone.
4. The same element is recognisable across graph, tree, inspector and editor.
5. Dense where the work is dense, but every control is reachable by keyboard.

## Accessibility & Inclusion
WCAG 2.2 AA everywhere, AAA where the density of a graph tool allows (confirmed): AAA text contrast and target sizes in chrome and lists; keyboard access to every control and every graph element; no meaning by color alone; reduced motion honoured.
