# Decisions for iteration 0.1

These records supplement the existing project ADRs. They do not silently replace them.

## Confirmed by Andre in this conversation
- [x] Stored and virtual graphs are equal capabilities.
- [x] Only explicitly bidirectional transformations support backward graph updates.
- [x] Ordered patches and signed fact deltas are distinct types with explicit conversions.
- [x] A small mathematical reference model is allowed in tests; no production implementation.
- [x] Ontologies and projections load without recompiling; generated C# authoring types are optional.
- [x] Explicit recursion has defined convergence rules; unsupported cycles are rejected.
- [x] Include document projections.
- [x] Track open work in a separate Markdown checklist.
- [x] Perform independent counter/rubber-duck reviews at important stages.

## Proposed design choices for this iteration
- [ ] Confirm symbolic virtual-definition patches as an extension to ADR-002. Infinite extensional differences cannot always be enumerated. The proposal preserves reconstruction of a deterministic logical description, subject to provider and pinned-input availability.
- [ ] Confirm portable JSON as canonical machine encoding; human-edited conformance scenarios remain YAML per ADR-038. JSON Schema describes wire shape, while semantic validation is separate.
- [ ] Confirm .NET 10 / C# 14 baseline. The package uses no NuGet dependencies; validation tooling uses Python jsonschema.
- [ ] Confirm v1 recursion fragment: positive monotone set rules over finite pinned domains; general well-founded recursion and nonmonotone cycles are deferred.
- [ ] Confirm per-record rejection and per-projection commit boundaries; no implied atomicity across transport batches or projections.
- [ ] Confirm authoritative absence requires complete, scoped, sufficiently consistent inventory evidence; ordinary ARG query pages do not establish deletion.
- [ ] Confirm stable document block IDs, explicit total ordering, reversible document-model patches and full rendered replacements in v1.

## Existing decisions carried forward

ADR-001/090/094: timeless shared entity identity, field selection controlled by projections. ADR-003/055/086/087: projection-specific edge IDs, unique source-target-type, identity-preserving retype with declared collision policy. ADR-083/084: entity-only endpoints; dangling edges allowed. ADR-088/091: ontology-defined properties and versioned interpretation.

ADR-005/006/009/017/018: raw source input, events and batches, batch invariance under equivalent conditions, sequential per-projection ingestion order. ADR-021/023/028: graph no-op on duplicate observations, separate telemetry, no diff on unchanged graph.

ADR-062/030/031/042/063: projection is configuration graph, graph-plus-diff and multiple graph inputs, scaffold belongs to the projection. ADR-002/008: complete changes and gap-free snapshot handoff, with the virtual symbolic extension above still proposed.

ADR-046/064/066–082: separate configuration interface; previous graph first, retained input replay once per input and in order, final diff only, new inputs queued, per-element evidence retained or removed with the element. ADR-035–041/047: exact or partial portable YAML scenarios, inline expectations, separate native inputs, checkpoints and verification projections.

## Master graph clarification — accepted user requirement

One master graph is the common foundation. Projections can be stacked and combined into derived graphs, with different output ontologies. This is expressed by GraphSpaceDefinition and its explicit source bindings. Independent revisions and source/output ontology checks apply. Recorded available input versions are the initial proposed consistency policy; globally aligned master frontiers require further design.
