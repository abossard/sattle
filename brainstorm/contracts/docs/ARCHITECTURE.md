# Sattle contract design — iteration 0.1

Status: reviewable design, compilable contracts, finite mathematical reference model. No production engine, parser, compiler, persistence system or renderer is implemented. The tests are evidence for the contracts and the finite model, not evidence of production performance or arbitrary graph transformation correctness.

## What is being designed

Sattle transforms source-native records and versioned graphs into graph views and documents. A projection is its configuration graph, not a separate procedural plugin. Nodes and directed edges carry ontology-defined properties. Shared entity identity is independent of the time, type and representation of an observation. Edges have projection-specific identity.

The first input families are Hubble/Cilium, Azure Resource Graph, Kubernetes Event resources and resource watch streams, Azure Network Watcher, and OpenTelemetry. Source transport and interpretation are separate: a file reader, queue, batch job or gRPC client can all supply the same asynchronous record stream.

The six answers accepted in this conversation are recorded in DECISIONS.md. Document projections and the open-work checklist were added explicitly afterwards. Existing ADRs remain authoritative unless this document explicitly labels a proposal for review.

## Master graph and composition

Each GraphSpaceDefinition declares exactly one MasterGraphDefinition. Its IngestionProjection turns adapter observations into the master graph. GraphProjectionDefinition binds additional output graphs to named master or upstream-projection inputs. Every derived graph must be reachable from the master. Graph and projection IDs are unique within the graph space; graph input ports are unique, declared and resolvable. Ordinary inter-projection dependencies form a DAG. Recursion belongs inside an explicit fixed-point operator, and backward lens edits use a separate command path.

ProjectionDefinition.Ontology is the output ontology. ProjectionGraphInput.ExpectedOntology pins each input ontology independently. Match validates source types and fields using that binding’s ontology; Emit and Scaffold validate using the output ontology. Equal local TypeId or PropertyId text in different ontologies does not imply equal meaning or value kind. Source ontology changes require downstream revalidation before compatible activation. CompileGraphSpaceAsync validates the bound composition; standalone CompileAsync is limited to definition validation and cannot establish graph-binding compatibility without graph-space context.

The initial composition policy evaluates the versions available in recorded input order and records those versions in InputFrontier. Two branches may therefore reflect different master revisions. The frontier is evidence of dependencies, not a claim of a coherent shared cut. A stricter aligned-frontier mode remains open design work. Each output commits and advances its revision independently; changes to a projection’s type or properties do not mutate the master or sibling graphs. Shared entity identity is reused only for the same entity; aggregates receive a new identity. Projections may deliberately emit different ontologies and can be stacked and combined through explicit bindings.

The graph-space fixture contains a master infrastructure graph, a metrics projection and a business projection consuming both. The metrics and infrastructure ontologies deliberately define Service.name with different scalar types. The test-only validator verifies binding-local matching and target-local emission, and rejects stale pins, invalid ports, unknown parents, duplicate IDs, cycles and incompatible field types. This verifies selected finite examples, not a production compiler.

## Module boundaries

| Assembly | Responsibility and hidden complexity | Public boundary |
|---|---|---|
| Sattle.Model | Shared vocabulary: identity, ontology, graph values, revisions, changes. This is a schema library, not a deep execution module. | Immutable records, discriminated values, distinct identifier types |
| Sattle.Projections | Describe and validate a portable operator graph; check references, expression types, recursion and declared lens capabilities. | IProjectionCompiler; portable ProjectionDefinition; typed authoring expressions |
| Sattle.Runtime | Own ingestion order, evaluation, consistent reads, snapshots, durable commit and configuration replay. Identity catalog and graph space hide their indexes and storage. | IProjectionEngine, IGraphSpace, IIdentityCatalog; evaluator/lens ports for engine extensions |
| Sattle.Adapters | Interpret source-native formats into typed identity candidates, observations and coverage controls. Hide vendor parsing and schema changes. | IGraphAdapter<TNative> and five source bindings |
| Sattle.Documents | Produce deterministic document structure and render it into a chosen encoding. Hide graph grouping, stable section mapping and escaping. | IDocumentProjection, IDocumentRenderer |

No interface exists for every storage index, matching phase or graph operator. Those are implementation details. Assemblies enforce a dependency direction: Model is independent; Projections depends on Model; Runtime depends on both; Adapters and Documents depend inward. There are no cycles. Contracts with substantial future behavior are kept few; many records are vocabulary, not claims of modular depth.

Ousterhout's principles are applied by hiding volatile decisions (storage, scheduling, vendor formats), pulling complexity into those modules, and preventing common mistakes with distinct types. Splitting every step of the temporal execution pipeline into a public service would leak orchestration. The design therefore exposes an engine session boundary rather than public Begin/Parse/Resolve/Mutate/Index/Notify steps. See SOURCES.md, O1.

## Transport and execution

All operational ports accept IAsyncEnumerable<T> and a CancellationToken. A finite file and a never-ending gRPC stream use the same consumption model. Batch carriers contain ordered records, not a different semantics. Pure C# authoring expressions are values; constructing a predicate does not require asynchronous IO.

An async enumerable is single-consumption. Input pulls are bounded by the implementation's declared capacity. Awaiting a result is not an implicit durable commit. An InputAccepted receipt names a durable acceptance checkpoint: accepted input remains recoverable even if the caller cancels output enumeration. Accepted means queued durably, not necessarily projected; graph feeds report projection progress. A producer acknowledges a queue position only after the matching acceptance receipt. Retrying after an uncertain receipt uses the same stable delivery key. A distinct repeated observation is a new delivery; applying already represented graph facts is a no-op but telemetry may still change.

The engine assigns an ingestion sequence at durable acceptance. A projection processes its applicable inputs sequentially, following that recorded sequence, including late events. Independent projections may advance separately. Replay determinism is for the same ordered inputs, pinned definitions and explicit logical-time controls. The engine does not promise identical output for arbitrary changes to cross-source interleaving.

Transport batches preserve record order, event boundaries, timing controls and per-record rejection. They are not transactions. A malformed record is rejected without silently dropping the remaining records. A graph commit is atomic per projection event. Cross-projection atomicity is not promised. One source record may expand into several graph facts within one logical input. Empty graph changes produce no graph patch; progress receipts/frontiers still advance. Buffering output batches preserves constituent revisions; squashing is a separately requested operation.

Logical time advances only through an explicit input/control, not the wall clock. Window operators must declare timestamp field, width and late policy. Merely sleeping cannot mutate the graph. Window closure after an input is distinguishable from the batch transport ending.

## The graph data structure

The logical graph at a revision is a typed directed property graph, represented physically by independently pageable entity and relationship indexes. A stored graph is finite at each observed revision but has no fixed configured lifetime size. Bounded memory is a requirement on an implementation, not a claim established by these interfaces.

Proposed physical indexes, hidden behind IGraphSpace:

| Index | Key | Value / invariant |
|---|---|---|
| Entity state | tenant, graph, entity ID, revision interval | type, canonical property map |
| Relationship state | tenant, graph, relationship ID, revision interval | source ID, target ID, type, properties |
| Adjacency | tenant, graph, endpoint ID, direction, type | relationship IDs; can reference absent entities |
| Relationship uniqueness | tenant, projection, source, target, type | one surviving relationship ID |
| Identity binding | tenant, identity domain, canonical key | timeless entity ID; binding history is versioned |
| Recent evidence | tenant, projection, element ID | last N qualifying input references in processing order |
| Lens complement | tenant, projection, rule version, source/view revisions | correspondence and information omitted from the view |

No graph-wide in-memory dictionary or mandatory full materialization is exposed. A snapshot consists of immutable pages plus a seal; the seal binds page count, descriptor, digest and input frontier. Publication requires the seal. Subscriptions deliver a consistent snapshot followed by adjacent revisions without a handoff gap. Gap is an explicit failure requiring a new snapshot, never a successful partial reconstruction.

Graph values form a closed tagged union. Integer and decimal wire values use canonical strings to avoid JSON number precision loss. NullValue is an explicit present null; a missing property is absence. Property arrays are semantically maps keyed by PropertyId: duplicates are invalid and canonical order is ordinal by property ID. JSON object member order is irrelevant to equality. Instants are UTC; numeric normalization and units belong to validated ontology interpretation. A type change is graph data and emits a diff. Telemetry is a separate stream and does not affect graph equality.

Relationship endpoints are entities only. Parallel directed edges of different types are valid; equal source-target-type tuples are not. Retyping preserves relationship ID. Removing an endpoint may leave its edges unresolved. Reads distinguish absence from not-examined or unavailable; a truncated page is never evidence of absence.

## Stored and virtual graphs

A virtual graph denotes a deterministic function of a provider version, finite definition and pinned inputs. No unrecorded clock, random source, network read or latest mutable version may influence a result. The same read contract serves stored and virtual data. Queries specify roots, relationship types, depth and deterministic work budgets; continuations are bound to revision, scope and provider version.

Work budgets are operational limits, not proofs of finiteness. A request that exhausts its budget returns BudgetExhausted or MoreAvailable, not Complete. Global counts, sorting, negation and aggregation over an unbounded extent cannot be claimed complete. A virtual provider must declare which bounded queries are supported. Complete document rendering requires a finite selection or explicit truncation.

**Proposed ADR extension, still to confirm:** complete enumerated changes are available for declared finite scopes. An infinite virtual-definition change may instead use DiffProfile.Symbolic, carrying a before/after VirtualRegion definition. With the same available deterministic provider and pinned inputs, a consumer reconstructs the logical graph description. It does not enumerate infinitely many changed nodes. A symbolic patch is neither an invalidation nor an ordinary signed fact delta. Converting it to an extensional delta requires a finite complete scope. Arbitrary provider definitions are not verified by this prototype. See OPEN-WORK.md.

For example, changing the virtual set of nonnegative even integer nodes to all nonnegative integer nodes has an infinite extensional difference but a finite definition change. The finite test oracle verifies sampled membership equivalence only; it does not prove a general provider implementation.

## Projection language

A ProjectionDefinition is a graph of operator nodes and typed wires. Its source bindings identify raw/normalized row contracts or graph inputs and versions. Operators include graph matching, selection, mapping, equijoin, union, distinct, grouping/aggregation, bounded traversal, entity/relationship construction, windows and an explicit fixed-point block. Expressions use a closed function set; arbitrary C# delegates, reflection, shell commands and ambient IO are absent.

Cypher supplies the useful declarative pattern vocabulary. Memgraph informs the separation between stream transport and transformations. Quine informs standing, incremental matches and positive/negative result changes. DBSP motivates finite-support signed deltas and distinguishes linear operators from joins and distinct. Sattle does not claim full compatibility with any of these languages or use their licenses as a substitute for an implementation. See SOURCES.md.

The compiler must infer every operator's input/output row, check wire port names, validate ontology references and property ownership, and reject missing fields, incompatible expressions, ambiguous matches and illegal cycles. Equality and booleans are type checked. Arithmetic requires compatible numeric kinds and units; division by zero is a typed evaluation failure. Missing values require explicit Coalesce or rejection. C# typed expressions catch a subset at compile time; portable definitions still require semantic validation. Generated C# ontology types are an optional future authoring facility, not implemented here.

A union's conflict policy must be explicit. PreserveEvidence emits conflict/evidence structure defined in the target ontology, not two incompatible values in a supposedly single-valued property. Group identity uses a named identity domain plus canonical group keys. Roll-up/roll-down are compositions of match/join/group/map/emit operators, not special engine health logic. Health, business capability and clinical trial models are replaceable ontologies.

Ordinary projection dependencies and ordinary operator wires are acyclic. Recursion is explicit inside FixpointOperator. Version 1 admits positive monotone set rules over a finite pinned domain, with RequireEqual set-compatible union only, with no fresh identities, negation, nonmonotone aggregation or external IO in the cycle. The compiler must establish those conditions; MaximumIterations does not establish convergence. If resource limits are exceeded, evaluation fails atomically. Retraction requires re-evaluation or correct support maintenance; naive support counts on recursive cycles are insufficient.

Graph matching is not an identity operation. TGG correspondence records can link source/target nodes or edges through ElementRef. They are transformation metadata, not domain edges, preserving ADR-083. TGG consistency checking, rule application order, conflict detection and source/target mappings need implementation; the schema does not prove arbitrary grammar correctness.

Bidirectionality is opt-in and versioned. A backward proposal requires source revision, view revision, rule version and complement revision. GetPut and PutGet are obligations on the declared supported domain. Complement retains information omitted from the view and has a lifecycle separate from bounded input history. Missing or stale complement is a rejection. Backward proposals are internal graph edits; they do not authorize changes to Azure or Kubernetes. Noninvertible aggregation does not silently become a lawful lens.

## Identity and ontology

IdentityKey is scoped by tenant and domain and consists of ordered canonical components. Projections choose the fields/expressions, so a pod UID and deployment name may intentionally create different identities. Time, ontology version and current type do not enter the identity merely because state changes. Canonicalization version records interpretation, not a mandate to create a new ID whenever canonicalization evolves.

IdentityEntry is a binding, not the identity itself. Multiple proven equivalent keys may bind to one timeless entity. Correlation evidence is not automatic equivalence. Renames, aliases, merges and splits require explicit projection rules and binding decisions; their mutation protocol is deferred. Deterministic new identity allocation must be based on canonical logical keys or a replay-pinned allocation record. Auto-increment allocation order across concurrent projections is insufficient. Relationship IDs use a stable creation/derivation key independent of mutable type; collisions resolve atomically with an explicit survivor.

The ontology defines entity types, relationship types, property data types, nullability, list item types, units, allowed endpoint types and whether a property is graph data or telemetry. NoType is explicitly declared for both entity and relationship vocabularies. Ontology references include immutable version and digest. Runtime loads validate references and constraints before activation. A migration is an explicit graph transition including OntologyEdit and any necessary state edits; changing the ontology registry alone cannot reinterpret historical data in place.

This iteration provides a minimal composable infrastructure ontology. It can represent services, pods, deployments, Azure resources, network endpoints, calls, membership, hosting and dependencies. Health is optional ontology data. Expected and observed relationships are distinct ontology meanings; source data never silently establishes intent. Richer constraints such as arbitrary cardinality expressions and ontology imports remain open work.

## Changes and arithmetic

An ordered GraphPatch is a partial state transition with from/to graph revisions, complete before/after element states and cause. Addition means ordered composition and is defined only for matching scopes and adjacent revisions. Negation reverses operations and swaps before/after and endpoints. Composition is associative where defined; it is not commutative. Inversion is a hypothetical inverse transition; committing an undo creates a new forward revision, never rewinds the durable log.

A SignedDelta is a finite-support map from canonical immutable graph facts to integer coefficients. Addition combines coefficients, zero removes terms, and negation flips signs. These form an additive group. The normalized graph state, however, must have set-valued existence and unique entity/property/edge keys. An arbitrary group element need not materialize to a valid graph. Projection support multiplicities and telemetry counts are not graph multiplicities.

Patch-to-delta conversion on finite extensional patches subtracts before facts and adds after facts. It discards intermediate order and may discard cause detail. Delta-to-patch requires an explicit base, the target ontology, validation and a chosen target revision; it produces a canonical net transition, not the original history. Symbolic and ontology-changing patches are rejected by this conversion unless separately expanded/migrated under a declared rule. Arithmetic on arbitrary conflicting updates is never assumed commutative merely because delta addition is.

Batching preserves all constituent patches and revision boundaries. Squashing calculates a net transition and explicitly loses intermediate visibility. Snapshots plus complete patches reconstruct each emitted revision; snapshot plus a squashed batch reconstructs only its final boundary.

## Configuration edits and evidence

Configuration commands use a separate interface. An edit pauses that projection's ordinary processing, queues newly accepted inputs, supplies the previous output graph first, then replays the union of retained inputs once each in original processing order. Only the final graph difference is emitted. Existing per-element histories are not rebuilt by replay; removed elements lose their histories. The projection determines the fate of elements whose creating inputs were evicted. New input drains afterwards in ingestion order.

N is configured per projection and applies separately to each graph element. Only inputs changing that element's graph data/structure enter its history. Telemetry-only changes and no-op observations do not displace evidence. This is not a full event archive and cannot support arbitrary full historical re-execution. New projections initialize from available graphs and retained context, without claiming access to discarded input.

## Document projections

A document projection is a first-class configured transformation. It selects graph facts, groups and orders rows, assigns stable block IDs and emits a DocumentSnapshot. A renderer receives that document and emits Markdown, HTML or plain text. Documents have their own revisions, source graph revision vector and completeness status. They are not disguised graph snapshots.

DocumentTemplate references a row-producing operator and declares the block kind, block identity fields, row identity fields, sort keys, output fields and missing-value policy. Order must be total: append a stable identity tie-breaker. Block IDs depend on rule ID and source identity/group key, not row position or label. A label rename preserves identity; duplicate labels are permitted when IDs differ. Structured headings, paragraphs, tables, lists and code blocks cover the initial representation.

Document patches change blocks and order independently and carry before/after source revision vectors, configuration versions and completeness. Patching or inversion is against an exact base. A renderer can initially emit a full UTF-8 replacement per document revision. Markdown byte editing, source writeback and arbitrary document lenses are not implied. The Markdown reference renderer implements only headings, paragraphs and tables; other declared renderers are contracts only.

A query over an infinite graph needs a finite document scope or explicit overflow policy. Hidden truncation is invalid. Source text is escaped, URLs need scheme validation when link nodes are added, and the renderer version pins formatting. Raw HTML from untrusted graph properties is not treated as document markup.

## Verification boundary

See VERIFICATION.md and generated results. Compilation verifies signatures and dependencies. C# negative examples demonstrate particular rejected type combinations. JSON Schema verifies the portable wire shapes mechanically generated from the actual DTOs. Selected semantic checks verify ontology references, invalid operator cycles and finite recursion restrictions in fixtures. The finite oracle exercises state reconstruction, inversion, delta arithmetic, retyping, reachability, lens laws, bounded virtual queries and document transformations.

None of these tests prove production concurrency, durable IO, upstream adapter compatibility, arbitrary TGG correctness, all virtual graph termination or automatic generated-type behavior. Those remain checkboxes in OPEN-WORK.md. Production src contains no operational method bodies and does not reference the test model.

## Contract review corrections

The ingestion union includes native batches, graph snapshot pages/seals, graph changes and explicit logical-time input. Snapshot staging never publishes an unsealed graph. All items share the recorded engine sequence. Source withdrawal removes that source's contribution, not all evidence for a canonical entity.

IGraphChangeAlgebra evaluates distinct PatchExpression and DeltaExpression trees. PatchThen and PatchInverse are ordered operators; DeltaAdd, DeltaSubtract and DeltaNegate are additive operators. A C# attempt to place a DeltaExpression in PatchThen is a compiler error. The service also defines explicit-base conversions and typed rejection. Operator evaluation is not implemented.

A scoped subscription creates a stable scoped graph identity from source graph and canonical scope. Its revisions advance on scoped graph changes. SubscriptionProgress reports the source frontier when out-of-scope changes occur, without inventing an empty graph diff. The source version and scoped output revision must not be confused.

Document ProjectAsync pins source revisions. ObserveAsync accepts FollowDocument with named source graphs/scopes and a resume token; it follows their frontier and produces pinned output snapshots and patches. Tenant scope is explicit on configuration, graph subscription, telemetry and document requests.

Typed Rows<T>, UnionRows<T>, FilterRows<T> and row predicates provide a limited compile-time composition surface. IProjectionAuthoring lowers these into the portable graph. Field names and ontology versions still require semantic validation. This is not a complete generated C# DSL for all graph operators.


Document templates distinguish block identity from table-row identity. For a table, block identity fields are group keys (empty means one block for that rule); row identity fields identify rows within it. Other block kinds use block identity fields for their selected row/group. Template order is explicit; groups have ordinal canonical key order, and row OrderBy must include stable identity tie-breakers. Document definitions are validated when ProjectAsync/ObserveAsync accepts them and pinned for that request. A changed definition starts a new version; silently swapping rules inside a subscription is invalid.

RequestId values must be unique among outstanding requests on one operational stream. Adapter outputs and telemetry observations carry explicit tenant scope; an InputId is interpreted within its tenant and recorded source context.
