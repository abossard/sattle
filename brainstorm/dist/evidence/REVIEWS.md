# Counter / rubber-duck reviews

Two independent review stages were requested by Andre and performed. A follow-up review checked the structural corrections. Reviews challenged the architecture and evidence; they are not a certification of an unimplemented engine.

## Stage 1 — architecture

| Finding | Resolution |
|---|---|
| Virtual invalidation cannot satisfy complete-diff reconstruction | Complete finite scopes and symbolic definition patches are separate profiles. Symbolic reconstruction is explicitly a proposed ADR extension requiring confirmation. No invalidation is called a complete diff. |
| TGG correspondence can refer to edges, but domain edges cannot | Correspondence uses ElementRef metadata. Rule templates refer to source/target variables; runtime links refer to actual elements. |
| Signed deltas may be algebraically valid but graph-invalid | Materialization is partial; negative existence, duplicate entity states and edge uniqueness violations are rejection cases. |
| Bounded input history cannot supply arbitrary lens complement | LensContext and correspondence state are explicit and versioned, separate from element evidence. |
| Work budgets do not prove recursive convergence | Finite positive fragment is explicit. Exhaustion is failure/incomplete, never a false fixed point. |
| Mutable type cannot determine immutable edge ID | Edge identity and source-target-type uniqueness are separate. Retyping is tested. |
| Async signatures do not settle durability | Acceptance, cancellation, retry, source acknowledgement and graph commit semantics are documented separately. |
| Documents need stable semantic identity | Block and row keys are distinct; position/label changes do not create new identity. |

## Stage 2 — contracts and evidence

| Finding | Resolution |
|---|---|
| No public graph/time ingress shape | EngineInput includes native, snapshot page/seal, graph-change and logical-time variants. |
| Arithmetic interfaces disconnected from DTOs | Replaced with typed PatchExpression/DeltaExpression and IGraphChangeAlgebra, including conversions and squash loss reporting. |
| Missing tenant scope | Added explicit tenant on configuration, subscriptions, telemetry and adapter output; request correlation remains stream-scoped. |
| No adapter retraction | AdaptedWithdrawal removes a named source contribution. |
| Document observation pinned to immutable sources | FollowDocument separates following sources from ProjectDocument pinned versions. |
| Scoped feed could skip global revisions | Scoped output identity/revisions plus SubscriptionProgress preserve source frontier without invented empty diffs. |
| Phantom generic graph wrapper overclaims typing | Removed it; added Rows<T>, typed composition and lowering, and documented the limited static safety surface. |
| Projection round-trip test did not test projections | Added strict JSON round trips for graph and document projection definitions. |
| Property-map order could produce false changes | Canonicalize at Diff, Facts and Apply boundaries; test reordered properties yield no edits or delta. |
| Block identity and row identity were conflated | Split BlockIdentityFields/RowIdentityFields; regenerated schema and strict round-trip checks. |
| Recursive union policy unchecked | Restrict admitted recursive union to RequireEqual; add rejection fixture. The checker remains a limited semantic oracle, not a monotonicity proof. |

## Evidence limits retained after review

- The finite support-retention example demonstrates support-set semantics, not a production incremental maintenance algorithm.
- Lens tests use a small lawful example, not arbitrary user-defined lens rules.
- Virtual-graph equivalence is tested on a finite sampled scope, not all virtual providers.
- Source fixtures are design specifications; parsers are not implemented or compatibility-tested.
- Source and schema records are untrusted proposals. Construction of a C# record does not certify its runtime validity.
- A complete typed DSL, ontology imports, general TGG analysis, identity merge/split protocol and distributed storage remain open work.

## Master-graph counter-review

The independent reviewer found that a graph-space wrapper alone would leave matching incorrectly tied to the output ontology. Added per-input ontology validation in the test oracle and a combined-input fixture with conflicting local names and scalar types. Added rejection fixtures for stale ontology pins, undeclared or duplicate ports, duplicate graph/projection IDs, missing parents, rootless graphs and dependency cycles. Documented independent graph revisions and recorded-available-version composition; coherent shared frontiers remain open. No extra execution service was introduced.

## Site story counter-review

Split TGGs from lenses. Retain Wardley as a capability overview, with separate graph-topology demonstrations. Every lesson has a causal sequence with pause and manual controls. Explicitly distinguish batches from transactions, signed algebra from valid graphs, finite virtual reads from infinite enumeration, recursion budgets from convergence, correspondence from identity, retained evidence from an archive, and document rendering from writeback. Excerpts are extracted from actual C# declarations.
