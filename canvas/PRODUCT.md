# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users
Three audiences, served equally (confirmed):
- Operators (platform and site reliability engineers) who open a workload graph to see whether it is healthy and which part is to blame, on the synthetic estate today and on real Azure data in Phase 2.
- Researchers and reviewers, math-literate readers of the white paper, who inspect graphs, their projections, revisions, lineage and change sets as worked examples of the theory.
- Projection authors who build and edit projections in the editor (four notations and text) and check what their output graphs hold. A project owner can also review a single rule in the Inspector, follow its finding to the source receipts, and preview a correction before publishing.

## Product Purpose
Canvas shows the master graph, its derived graphs, their revision histories and their telemetry. An author can edit the KQL or GKQL projection that produced a result. A reviewer should be able to find what changed, trace a state to its evidence and check how a proposed rule would affect the graph.

## Positioning
Every view is a function of an engine output at a revision: incremental graphs with lineage, a timeline over revisions, a roll-up whose cause relation and origins are shown, and an editor whose commands are the engine's own edit protocol. Nothing is hand-drawn or mocked.

## Operating Context
- Sources: static engine output (the synthetic estate demo, `public/demo/estate`) or a live engine over HTTP and WebSocket.
- Views (screens): Data graph (WebGL graph, tree, document, telemetry), Graph space, Projection editor, Ontology.
- Used on desktop at 1280 px and wider, dark and light; long sessions of reading and scrubbing.

## Capabilities and Constraints
- React with Fluent UI v9 components and tokens: a binding commitment (confirmed). The redesign works inside Fluent.
- The graph renderer is WebGL with its own label placement; it stays.
- Redux-style store, pure transitions, actions at the edges (repository design rules).
- Terminology from the glossary (`paper/glossary.md`): source, envelope, row, ontology module, identity, projection, rule, graph, graph space, view, tree, revision, patch, roll-up, origin, cause relation, edge kinds (composition, dependency, communication, support).
- Examples must answer a user's question: no toy chains, no unconnected nodes, no internal ids as labels.

## Evidence on Hand
- The synthetic estate demo (thirteen graphs, workload model with scenarios A to E) and the reference projections built in each notation (`tests/engine/kql-references.test.tsx`).
- The local submission review links a trace and an ingress receipt to a receiver declaration. The real-engine test publishes the corrected rule, reads the earlier conflict by revision and compares the published graph with a fresh CLI replay. [Walkthrough](../engine/standard/README.md) · [Integration test](tests/engine/coverage-review.test.ts)
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
