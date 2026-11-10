# Sattle — work checklist

Iteration 0.1: contract design and a finite test-only mathematical model. Production implementation remains outside this iteration. Review evidence is in docs/REVIEWS.md; test evidence is in docs/VERIFICATION.md.

## Confirmed requirements
- [x] Stored and virtual graphs as equal capabilities.
- [x] Opt-in bidirectional transformations.
- [x] Distinct ordered patches and signed deltas, with explicit conversion.
- [x] Runtime-loadable ontology/projection definitions; optional generated C# authoring types.
- [x] Explicit recursion with convergence restrictions.
- [x] Small mathematical reference model allowed only in tests.
- [x] Document projections and renderers.
- [x] Separate Markdown checklist for all open work.
- [x] Counter / rubber-duck reviews at important stages.

## Completed in this iteration
- [x] Review current ADRs and project site source.
- [x] Research Ousterhout, TGGs, lenses, Cypher, Memgraph, Quine and signed-delta theory.
- [x] Research the five native input families and their semantic differences.
- [x] Define five C# assemblies with inward dependencies and contract-only production source.
- [x] Define async-stream ingestion, graph inputs, logical time, configuration, reads and subscriptions.
- [x] Define scoped identity, ontology, graph, patch and delta DTOs.
- [x] Define stored/virtual access and explicit completion/budget outcomes.
- [x] Define typed arithmetic requests, inversion, squash and conversions.
- [x] Define portable operator graphs, limited typed authoring and a compiler boundary.
- [x] Define TGG correspondence templates and separate versioned lens context.
- [x] Define source observations, contribution withdrawals and coverage controls.
- [x] Define document templates, block/row identities, document patches and renderer ports.
- [x] Generate 12 strict wire schemas from the actual C# DTOs.
- [x] Add ontology, graph/document projection examples and native source fixtures.
- [x] Compile solution with warnings treated as errors.
- [x] Verify 14 finite reference-model groups with 1,921 assertions.
- [x] Verify 47 schema/selected semantic checks.
- [x] Verify 8 positive/negative compiler cases.
- [x] Complete architecture counter-review and contract/evidence counter-review.
- [x] Correct review findings and rerun verification.
- [x] Render and visually inspect the architecture diagram.
- [x] Package code, schemas, examples, evidence, review notes and this checklist.

## Master graph and site revision
- [x] Declare one master graph per graph space and explicit stacked/combined graph bindings.
- [x] Separate source ontology validation from output ontology validation.
- [x] Verify cross-ontology composition and rejection fixtures.
- [x] Record master-graph and 21-step-story counter-reviews.
- [x] Complete the colorful animated 21-step site guide with real code references.
- [x] Verify desktop/mobile layouts and navigation; inspect reduced-motion implementation (see docs/SITE-REVIEW.md).
- [x] Publish the revised site and package the current contracts and schemas.

## Design decisions to confirm next
- [ ] Confirm symbolic virtual-definition patches as the explicit extension to ADR-002; complete infinite enumeration is not promised.
- [ ] Confirm JSON as canonical machine encoding and YAML for human-authored conformance scenarios.
- [ ] Confirm .NET 10 / C# 14 baseline.
- [ ] Confirm positive finite-domain recursion as the initial supported fragment.
- [ ] Confirm per-record rejection and per-projection atomic commit boundaries.
- [ ] Confirm authoritative absence/reconciliation policy per source; ARG query completion alone does not establish deletion.
- [ ] Confirm document overflow policy, block/group ordering and full rendered replacement in v1.

## Open contract design
- [ ] Add graph subscription resume-token input and define reconnect/gap recovery; feed output tokens currently have no corresponding SubscribeGraph input field.
- [ ] Specify identity alias, rename, merge and split mutation protocol, including historical references.
- [ ] Specify canonical value equality for decimals, instants, units, lists and identity hashing, beyond wire-shape validation.
- [ ] Extend ontology constraints: imports, property cardinalities, value ranges, enums, inheritance and migration rules.
- [ ] Specify all operator port contracts and typing judgments, including join collisions, windows and conflict evidence.
- [ ] Complete typed C# authoring and generated ontology types beyond the current expression/row subset.
- [ ] Define general TGG rule admissibility, complement evolution, conflicting edits and consistency checking.
- [ ] Specify virtual-provider artifact distribution, definition schema validation, capability negotiation and historical availability.
- [ ] Specify symbolic patch hashing/equality and finite-scope expansion; keep rejection when expansion cannot complete.
- [ ] Define multi-source consistent-cut policy, checkpoint persistence and gap recovery in full detail.
- [ ] Specify active document-definition edits and transition behavior between renderer versions.
- [ ] Add richer document structures, safe links, nested sections and explicit pagination/chunked rendering.
- [ ] Complete portable conformance scenario schema with checkpoints, exact/partial expectations, input histories and telemetry expectations.
- [ ] Specify complete no-op/dedup behavior after interleaving changes, not only immediate repeated facts.

## Deferred production implementation
- [ ] Implement persistent graph indexes, virtual providers and bounded working memory.
- [ ] Implement identity/ontology validation and deterministic ID allocation.
- [ ] Implement portable projection compiler and all declared operators.
- [ ] Implement incremental recursion with correct deletion/support maintenance.
- [ ] Implement lens/TGG evaluation and atomic backward graph proposals.
- [ ] Implement Hubble/Cilium parser and transport bindings.
- [ ] Implement ARG paging and explicit reconciliation policies.
- [ ] Implement Kubernetes Event and list/watch bindings, including expiry and relist.
- [ ] Implement versioned Network Watcher VNet flow logs and optional legacy NSG parsing.
- [ ] Implement OTel traces/logs/metrics and temporality-aware interpretation.
- [ ] Implement document projection and Markdown/HTML/plain-text renderers.
- [ ] Implement durable acceptance, atomic commits, cancellation, retry and backpressure.
- [ ] Implement configuration edit replay and bounded evidence retention against all existing ADR cases.

## Deferred verification
- [ ] Run the conformance corpus against an actual production engine; the finite model is not that engine.
- [ ] Add end-to-end native-source-to-graph-to-document execution tests once parsers/evaluators exist.
- [ ] Add real-source version/compatibility fixtures and gap/loss scenarios.
- [ ] Verify formal typing and convergence claims for the admitted projection language fragment.
- [ ] Verify arbitrary virtual providers and nontrivial lens/TGG rule sets.
- [ ] Test crash recovery, checkpoint atomicity, duplicate delivery and cross-tenant isolation.
- [ ] Test snapshot handoff, scoped progress and mixed-source interleaving under load.
- [ ] Measure latency, memory bounds, graph size and partition behavior; no scale claims are established yet.
