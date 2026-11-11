# Inventory of laws, theorems and propositions

**Claim.** Every numbered theorem, proposition, corollary, law and claim of the paper is listed here with its status, the technique of its proof and what checked it besides reading.
**Status.** Complete for Ch. 1 to 13 at the text of this revision. No statement is a sketch or open. No proof in this paper is mechanized. Where the last column says "reading only", the proof was written and read, and nothing else; the statements that a second reader derived again at gate 1a are listed in their own section, with every difference found.
**Gate.** 1a (theory). This file is the evidence of that gate.

## Reading the table

- **proved**: a full proof is in the chapter.
- **proved, cited**: a full proof relative to a result of the literature that is stated in the proof and not re-proved.
- **proved under premises**: a full proof from named properties that the model does not give: properties of the engine or of its programs (each with a test, existing or wanted), or of an external system (taken from its documentation).
- **restated**: the first form was false or too strong; the statement now carries the hypothesis that makes it true, and the chapter gives the counterexample to the first form.
- **sketch**: an argument that is not a full proof. The chapter says why it is not closed.
- **tested**: no proof is written; the statement is a property of the implementation or follows at once from a definition, and tests exercise it.
- **open**: neither proved nor tested as stated.
- A statement with several parts takes the status of its weakest part. A statement that is proved relative to stated assumptions of its chapter (the store properties S1 to S5, a coverage declaration, collision resistance) counts as proved; the assumptions are in the statement.
- The numbers T1 to T16 of the reviews appear in the names ("T2", "T6"). Facts (Ch. 0, Ch. 11) and rules (Ch. 1, Ch. 7) are stipulations or citations and are not listed.

## Counts

| Status | Two revisions ago | Before the gate 1a pass | Now |
|---|---|---|---|
| proved | 81 | 86 | 94 |
| proved, cited | 1 | 2 | 2 |
| proved under premises | 0 | 2 | 9 |
| sketch | 19 | 11 | 0 |
| tested | 8 | 8 | 8 |
| open | 4 | 4 | 0 |
| all | 113 | 113 | 113 |

Of the 105 statements counted as proved in one of the three ways, 2 are checked by a differential harness against an independent reference evaluator (Theorems 6.1 and 6.2; the engine passes it since `ad83e63`), 10 by seeded property tests that a review found weakened, 54 by example tests or measurements only, some of them void or weakened (last section but one), and 39 by reading only. Of the fifteen statements closed in the gate 1a pass, nine were **restated** because their first form was false or too strong (2.5, 5.2, 5.6, 5.7, 7.5, 8.7, 9.7, 11.6, 13.5), and seven are proved under named premises (4.11, 5.2, 5.6, 5.7, 10.5, 10.7, 10.8). "Proved under premises" is not "proved": the premise is the part that a test must still check.

## The table

| Statement | Name | Status | Technique | Checked by, other than reading |
|---|---|---|---|---|
| Theorem 1.1 | canonical equality | proved | canonical bytes are a normal form: equality of values iff equality of encodings | example tests (`ValueTests.cs`) |
| Corollary 1.2 | decidability and cost | proved | from the definitions and the results its status names | nothing: reading only |
| Corollary 1.3 | digest compatibility | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 1.4 | allocation is a pure function, shared identity | proved | allocation is a function of the key; collision resistance assumed | example tests |
| Theorem 1.5 | timeless binding | proved | case analysis over the three binding rules | example tests |
| Theorem 1.6 | canonicalization change keeps ids | proved | totality of the migration; conflict case excluded | nothing: reading only |
| Theorem 1.7 | merge and split | proved | invariant on forwarding chains (strictly decreasing ids) | example tests (`IdentityMergeTests.cs`); **weakened**: assertions were loosened in the commit that changed the rule |
| Proposition 1.8 | order-independent survivor | proved | induction on the operation sequence with an invariant | example tests (weakened, as 1.7), two captures at five chunk sizes; no generated orders |
| Theorem 2.1 | conformance is decidable | proved | finite check over a finite graph | example and mutation tests (`KernelTests.cs`) |
| Theorem 2.2 | the kernel is self-describing | tested | finite check executed by the engine | executed at start-up; mutation tests; the edge-count assertion cannot fail |
| Theorem 2.3 | closure is well-defined | proved | well-founded recursion over an acyclic import graph | nothing: reading only |
| Theorem 2.4 | release digest | proved | from the definitions and the results its status names | example tests |
| Theorem 2.5 | compatible changes preserve typing | proved, restated | a derivation consults the closure in four forms; each compatible row leaves the answers unchanged or strengthens presence; three rows of the table of Def. 2.9 corrected, with counterexamples | nothing: reading only. The classifier is not built (T-COMPAT) |
| Theorem 2.6 | migration does not reinterpret history | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 2.7 | value roles are inert data | tested | from the definitions and the results its status names | example tests; (b) a source search |
| Theorem 3.1 | committed states are well-formed | proved | induction on the log | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 3.2 | history is immutable | proved | append-only log by definition | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 3.3 | equality by digest | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 3.4 | constraint check is local | proved | from the definitions and the results its status names | example tests |
| Theorem 3.5 | frontier monotonicity | proved | monotonicity under premise F1 | example tests |
| Theorem 3.6 | scoped graph identity | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 3.7 | evaluation order | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 4.1 | associativity, T2 | proved | case analysis on guards | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.2 | application respects composition, T2 | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.3 | inverse law, T1 | proved | guards of the inverse match the result | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.4 | edit order is irrelevant; dangling edges remove intra-patch ordering | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Proposition 4.5 | algebraic structure, correction of the review | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 4.6 | group of signed deltas, T3 | proved | pointwise group of integer weights | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.7 | delta is a homomorphism, T4 | proved | computation on facts | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.8 | materialization and conversion, T4 | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.9 | non-commutativity and non-closure | proved | counterexamples | example test |
| Proposition 4.10 | undo is a patch | proved | from the definitions and the results its status names | example tests (`DraftTests.cs`, canvas) |
| Proposition 4.11 | two views | proved under premises (RT), restated | from Theorems 4.3, 4.8 and the premise RT of Theorem 5.6, on canonical drafts | example test on the example modules; `Unprintable` example |
| Theorem 5.1 | checking is decidable | proved | structural recursion over a finite definition graph | example tests (`LanguageTests.cs`) |
| Theorem 5.2 | type soundness | proved under premises (KS), restated | induction over expressions, operators and sinks; rows sound up to counted faults; (a) kinds and ownership always, (b) required properties up to counted faults and reported conflicts, (c) C6 and C7 not implied; three counterexamples to the first form | example tests of typed errors; no conformance assertion on generated projections (T-SOUND) |
| Theorem 5.3 | role non-interference | proved | induction over the typing derivation: only `Threshold` moves telemetry to data | nothing: reading only |
| Proposition 5.4 | closed language | proved | inspection of the kernel table | nothing: reading only |
| Theorem 5.5 | relationship identity is retype-stable | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 5.6 | lossless syntax | proved under premises (RT), restated | on the canonical graphs (the image of `parse`): bijection, idempotent `fmt`, equal canonical form iff equal graph | example test on the example modules (T-ROUNDTRIP wanted) |
| Proposition 5.7 | refactoring preserves outputs | proved under premises (R0 to R5), restated | every sink keeps its fragments; materialization splits over disjoint keys; six counterexamples to the first form | 10 cases (`RefactorTests.cs`); R3, R4, R5 unchecked by the engine and untested (T-REFACTOR) |
| Proposition 5.8 | parameters are constants of the binding | tested | from the definitions and the results its status names | example tests |
| Proposition 5.9 | retention is a function of the frontier | proved | induction over the positions of the input with the held weights as invariant | example tests, two captures (`RetentionTests.cs`); the `retain` world of the differential harness (engine against a reference written from Def. 5.15, every cut) |
| Proposition 5.10 | lifting is a quotient | proved | direct from Def. 5.17 | 28 and 36 example cases (`ProjectionEdgesTests.cs`, `LiftTests.cs`) |
| Proposition 5.11 | conservation | proved | partition of the edges by fibre | the same cases |
| Proposition 5.12 | lifting composes | proved | composition of partial functions; sums over a disjoint union | two cases against a one-step lift |
| Theorem 6.1 | incremental correctness, T5 | proved, cited | structural induction on the plan; chain rule; two cited theorems of [budiu2023], statements checked against the arXiv text; cases added at gate 1a: `Match`, operators filtered by the frontier, `Threshold` as a fold of the recorded sequence, materialization | differential harness against an independent reference evaluator: at engine `ad83e63` the engine's incremental result equals the reference's batch result in all 32 worlds, under every batch cut and under reordering inside a batch (149 of 149, verified by the orchestrator). Not covered independently: `Lift` and `Fixpoint` (the reference uses the engine's lowering and unrolling), the type checker and the value library (shared), lens rules, partial windows, `Delta`. The earlier fixture evidence took its expected values from the engine itself |
| Theorem 6.2 | batch invariance, T6, ADR-009 | proved | from Theorem 6.1 and four premises B1 to B4 (B4 found at gate 1a); one known exception (Law 7.5, remark) | differential harness as for 6.1 (every cut, reordering inside a batch); both captures at five chunk sizes; targeted tests of B1 and B2; B3 on examples and the `link` world; B4 by the harness as far as its worlds put an advance and a structure change into one step (T-B4) |
| Theorem 6.3 | replay determinism, T7 | proved | function of the recorded input sequence | two replays compared on fixtures; capture tests |
| Theorem 6.4 | reconstruction and handoff, T8 | proved | from the definitions and the results its status names | example tests on memory and SQLite (`StoreTests.cs`) |
| Theorem 6.5 | recursion terminates at the least fixpoint, T9 | proved | ascending chain in a finite lattice | example tests on acyclic input only |
| Theorem 6.6 | retraction under recursion, T10 | proved | from Theorem 6.1; counterexample | example tests on acyclic input only |
| Proposition 6.7 | cross-graph telemetry reads are deterministic | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 6.8 | stateful telemetry operators under revision | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 7.1 | plane separation, ADR-023 | proved | the telemetry plane has no revisions by definition | example tests; revision counts on the captures |
| Theorem 7.2 | additivity and roll-up | proved | additivity of exact sums | nothing: reading only |
| Theorem 7.3 | replayable telemetry-derived structure, L12 | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 7.4 | bounded revision churn | proved | counting of runs of a state machine | one example test |
| Law 7.5 | series identity under graph change | proved, restated | items 1 and 2 from Def. 7.3, 7.9, 3.6; item 3 from Theorem 7.2 for the cases in which the as-if value is a function of the two windows; the remaining case is an open ruling and not claimed | nothing: reading only. The engine adds windows for every property (D36) |
| Theorem 7.6 | element view is well defined | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 7.7 | roll-up | proved | monotone maximum; state is a function of `A` and the threshold states; churn from Theorems 5.3 and 7.4 | one fixture test, two captures; that the engine's `health` graph equals Def. 7.12 is tested by example only |
| Proposition 7.8 | bounded reach | proved | breadth-first reach | example test |
| Theorem 8.1 | bijective rules | proved | inverse function | **void for GetPut** (the test sends only edits with before equal to after, which are dropped before any lens runs); PutGet on generated edit sequences on one lens stands |
| Theorem 8.2 | the lens steps | proved | direct verification of GetPut, PutGet, PutPut; counterexample for PutPut | **void for GetPut**, as 8.1; PutGet on generated edit sequences on one lens stands |
| Theorem 8.3 | laws survive stacking, T11 | proved | composition of lenses ([foster2007]) on accepted domains | one link only; GetPut evidence void, as 8.1 |
| Theorem 8.4 | class of a stack | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 8.5 | runtime law check | proved | the staged check is the law it tests | example tests |
| Theorem 8.6 | scope of backward edits | proved | from the definitions and the results its status names | example tests of rejections |
| Theorem 8.7 | TGG laws for the v1 fragment, T12 | proved, restated | for the grammar of Def. 8.7 and `FWD` of Def. 8.8: a lemma on the shape of the language, then determinism, correctness, completeness, hippocraticness; counterexample to the first form | nothing: reading only. `sync` is not built, so no engine evidence can exist |
| Theorem 9.1 | edit replay, T13 | proved | from the definitions and the results its status names | example tests |
| Theorem 9.2 | an edit is not recomputation from scratch, ADR-073 | proved | counterexample | nothing: reading only |
| Theorem 9.3 | rebuild is exact for graph-input projections | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.4 | evidence is a derived index of the log | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.5 | support semantics, ADR-075, ADR-076 | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.6 | definitions are pinned, replay of history | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.7 | lineage soundness | proved, restated | (a) polynomial form of positive plans and restriction; (b) one per-key operator last, non-negative input; (c) `Threshold` by its episode; three counterexamples to the first form | nothing: reading only |
| Proposition 9.8 | cost of evidence | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 9.9 | releases | tested | from the definitions and the results its status names | example test |
| Proposition 9.10 | preview | tested | from the definitions and the results its status names | example test for (a) and (c) |
| Proposition 9.11 | evidence elements are idempotent and withdrawable | tested | from the definitions and the results its status names | nothing: reading only |
| Theorem 10.1 | idempotent delivery | proved | idempotence of a set of keyed contributions | not tested for evidence elements |
| Theorem 10.2 | withdrawal removes one source's support | proved | from the definitions and the results its status names | example tests |
| Theorem 10.3 | soundness of reconciliation | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 10.4 | loss is not deletion | proved | from the definitions and the results its status names | example tests on fixtures |
| Theorem 10.5 | list then watch, resumption | proved under premises (KW1 to KW3, external), restated | induction over the watch events | nothing: reading only. The premises are the documented contract of the Kubernetes API and cannot be tested here |
| Theorem 10.6 | replay of a capture is deterministic | proved | from the definitions and the results its status names | capture tests |
| Proposition 10.7 | live run reproduced by replay | proved under premises (LV1 to LV4), restated | same input sequence in both runs, then Theorem 6.2(b) | **weakened**: the test writes the stream itself and drives the replay; the live loop, its envelope handling and its recording are never replayed (T-LIVE) |
| Proposition 10.8 | a killed live run resumes to the state of an uninterrupted run | proved under premises (LR1 to LR4), restated | Proposition 11.9 for the journal, idempotent delivery, then Theorem 6.2(b) | simulated kill against a stand-in (T-RESTART) |
| Theorem 11.1 | reconstruction, L1 | proved | from the definitions and the results its status names | example tests on memory and SQLite |
| Theorem 11.2 | linear chain, L3 | proved | conditional write on the predecessor (S1, S2) | example tests on memory and SQLite |
| Theorem 11.3 | per-revision visibility and overlay reads, L2 | proved | from the definitions and the results its status names | example tests |
| Theorem 11.4 | idempotent materialization, L6 | proved | from the definitions and the results its status names | example tests |
| Theorem 11.5 | gap-free follow, L5 | proved | from 11.2 and S4, S5 | example tests |
| Theorem 11.6 | deterministic replay, L4 | proved, restated | induction over the inputs from Theorems 6.3, 11.2, 11.3; hypothesis (iii) on the writers made explicit | nothing: reading only (E6 not run) |
| Theorem 11.7 | canonical seal, L7 | proved | from the definitions and the results its status names | random histories on two stores |
| Theorem 11.8 | uniqueness without cross-partition reservation | proved | from the definitions and the results its status names | example tests |
| Proposition 11.9 | restart reproduces the state | proved under premises | from Theorem 6.3 under J1 to J3 | example tests, end-to-end restarts; **not exercised**: a stored commit whose content differs from the recomputed one |
| Proposition 11.10 | restart from a state seal | proved under premises | from Proposition 11.9 under SL1 (renamed from S1) | example tests; one deleted-entry case; **weakened**: the foreign-build case also carries a bad digest, so the build check is not isolated |
| Theorem 12.1 | replica correctness | proved | induction on the feed | canvas tests on a fake socket; no end-to-end digest; the engine-side stream test is void, as 12.9 |
| Proposition 12.2 | late join | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 12.3 | same entity across graphs | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 12.4 | alignment is well defined | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 12.5 | definitions need no new mechanism | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 12.6 | stable block identity, T14 | proved | function of canonical bytes | by construction; document tests |
| Theorem 12.7 | document patch inversion, T14 | proved | two independent components, each as Theorem 4.3 | nothing: reading only |
| Theorem 12.8 | rendering is a function, escaping | tested | from the definitions and the results its status names | four escape examples |
| Proposition 12.9 | reducer contract | proved | from the definitions and the results its status names | canvas tests on a fake socket; **void on the engine side**: the event-stream test asserts over lists that may be empty |
| Proposition 12.10 | resources, tags and caching | proved | from the definitions and the results its status names | example tests (`EtagTests.cs`) |
| Proposition 12.11 | a document digest per revision | proved | from the definitions and the results its status names | example tests, one capture |
| Theorem 13.1 | join cost is output-sensitive | proved, cited | from the definitions and the results its status names | nothing: reading only |
| Proposition 13.2 | group commit amortization | tested | from the definitions and the results its status names | nothing: reading only |
| Theorem 13.3 | tandem pipeline | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 13.4 | latency budget composition | proved | from the definitions and the results its status names | nothing: reading only |
| Claim 13.5 | hot read cost | proved, restated | counts on the layout of 11.9: by handle, at a pinned revision, one-hop scan | nothing: reading only. That the engine has this layout and its constants is benchmark B6 (gate 1c) |

## Tests wanted from the engine

Each closes a premise or a gap named in a chapter. Name, what it generates, what it asserts.

| Id | Test name | Generates | Asserts | Closes |
|---|---|---|---|---|
| T-B3 | `Identity_is_independent_of_key_arrival_order_on_generated_inputs` | per seed: 5 to 40 keys of one domain, links that form a random partition, 20 random orders of the first uses and links, 3 random chunkings of each | for every key, `Rep` is the smallest own id of its class; the state digest is equal across all orders and chunkings; every forwarding record points to a smaller id | premise B3 of Theorem 6.2, the engine side of Proposition 1.8 |
| T-B1 | `A_telemetry_sink_whose_group_key_is_finer_than_its_element_key_is_rejected` | projections with a `Rollup` or `Group` over telemetry whose keys canonicalize to one element key | the checker refuses them with a typed error | premise B1 as a checker rule (O6.7) |
| T-INC (exists as the differential harness since `3629c63`, passes since `ad83e63`) | `The_engine_equals_the_reference_however_the_log_is_cut` and its siblings | per operator kind: random sequences of inserted and retracted rows, late rows inside the horizon, frontier advances | after every input, the sum of the output deltas equals a naive evaluation of the operator on the summed input | O6.1: that each strategy is the rule of Theorem 6.1 |
| T-FIX | `Fixpoint_on_cyclic_input_reaches_the_least_fixpoint_or_reports_the_bound` | random directed graphs with cycles (up to 30 nodes), a closure by `Fixpoint`, random edge retractions | with `max` at least the node count, the result equals a reference transitive closure, also after each retraction; with a smaller `max` the limit is reported and not silent; a body with an arithmetic assignment and no declared bound is refused (F5) | Theorems 6.5 and 6.6 on cyclic input, F5 as a checker rule |
| T-LENS | `Lens_steps_satisfy_their_laws_on_generated_edits` | per step (`Filter`, a `Map` that drops fields, a one-element `Match`, an emit with literals): random source tables and accepted view edits, among them delete and create of one key | GetPut and PutGet always; PutPut where the rule declares it; a rule over a dropping `Map` that accepts `delete` cannot declare `putput` | Theorem 8.2, experiment Q3 |
| T-LIFT | `A_lift_equals_definitions_5_17_and_5_18_on_generated_graphs` | random lower graphs (up to 50 nodes, counters over 8 or more windows), random mappings with nodes of 0, 1 and 2 images, each `images` policy, `selfloops`, `when`, two stacked mappings | the lifted edge set equals a reference implementation of Def. 5.17; counters equal the fibre sums per window; conservation under `images reject`; ratio and state equal a recomputation; two lifts equal the one-step lift | the lowering of `lift`, Propositions 5.10 to 5.12 on generated data, O5.8 |
| T-RESTART | `A_process_killed_at_a_generated_journal_position_restarts_to_the_uninterrupted_state` | random stop positions, among them inside the write of an entry; a process kill and not an exception; SQLite and the live path with a stand-in source | digest, heads, sequence number and `/space` equal those of an uninterrupted run; a cut last entry is discarded or refused and never half applied | premise J1 of Proposition 11.9, Proposition 10.8 |
| T-SEAL | `A_seal_restore_equals_a_full_replay_in_every_field` | random seal positions on generated inputs, then further random input | the restored state equals the replayed state in a digest over every field (interner, identity catalog, support counts, fold states, telemetry ring), and both continue with equal commits | premise S1 of Proposition 11.10 |
| T-SOUND | `A_well_typed_projection_never_faults_and_emits_conforming_elements` | random well-typed operator chains from the palette, conforming rows, among them rows that divide by zero and overflow, and two create sinks on one key | no exception; every committed element satisfies C2, C3, C5; with fault and conflict counters at zero also C4; a fault is counted, leaves its value absent and does not stop the rows around it; the digest is equal under every chunking; every builtin and aggregate returns its declared kind (premise KS) | Theorem 5.2 (O5.1) |
| T-ROUNDTRIP | `Parse_of_print_is_the_identity_on_generated_definition_graphs` | random definition graphs produced by `parse` with every node kind, `Lift`, `LiftState` and `ValueRole` among them; and well-formed graphs outside the parser's naming scheme (a step node renamed, an explicit clause equal to its default) | on the first set `parse(print(G)) = G` by digest and printing is idempotent (premise RT); on the second set the test reports which graphs round-trip and which are `Unprintable` or change | Theorem 5.6, Proposition 4.11 |
| T-REFACTOR | `Extract_inline_and_move_preserve_the_output_union_on_generated_projections` | random stacked projections, every plan the engine accepts; one negative case per premise: two sinks on one key, two sinks that conflict, two key rules on one tuple (R3), a reader of the parent graph (R4), `moveRule` across output ontologies (R5) | the output union (elements, ids, telemetry) is equal before and after when R3 to R5 hold; in each negative case the engine either refuses the plan or does not report `preserves: union` | Proposition 5.7 (O5.5) |
| T-RETAIN | `Retention_keeps_exactly_the_rows_younger_than_the_age_under_generated_frontiers` | rows that are expired on arrival, retractions of held rows, random frontier advances and chunkings | the retained set is the rows younger than the age at the frontier; the digest does not depend on the chunking | Proposition 5.9 |
| T-COMPAT | `The_compatibility_class_of_a_change_predicts_the_recheck` | one scripted change per row of the corrected table of Def. 2.9, and the four counterexamples of Theorem 2.5 | the predicted class equals the result of rechecking the dependents | the classifier that Theorem 2.5 describes (Q11); it is not built |
| T-PREVIEW | `A_complete_preview_equals_the_activation_a_commit_performs` | drafts over the fixtures with no input in between | the complete preview's output digest equals the digest after the commit | Proposition 9.10 (b) |
| T-LIVE | `The_reader_loop_of_Live_cs_is_reproduced_by_a_replay_of_its_recording` | per seed a shuffled envelope sequence of the fixtures with duplicates and records that are not envelopes, random flush points, a stand-in source that feeds the real `LiveInput.Run` | the recording holds the consumed envelopes once each and in order (LV1); a replay with the same horizon and options ends in the live digest (LV2, LV3); a replay with another horizon is reported as different or shown equal | premises LV1 to LV3 of Proposition 10.7 |
| T-B4 | `A_threshold_gives_one_state_however_an_advance_and_a_structure_change_share_a_step` | worlds with a `Threshold` over a roll-up whose key an element leaves, windows that leave the retained range, random cuts that put the advance and the structure change into one step and into two | the state and the carried state equal the reference's fold of the sequence under every cut | premise B4 of Theorem 6.2 |
| T-MERGE-TELE | `A_merge_combines_windows_as_if_written_under_the_survivor` | two ids with windows of a property per combine rule (`sum`, `min`, `max`, `mean`, `last`), rows before the link and after it | the merged window is the as-if value for `sum`, `min`, `max` and for equal values; for different values under `mean` or `last` the test records what the engine does in both orders | Law 7.5 item 3 (O7.6, D36) |

## An independent check exists, and what it shows

Since engine commit `3629c63` the engine's tests hold a reference evaluator written from the definitions of this paper (`engine/tests/Sattle.Reference`, which uses the engine's value and language libraries and none of its runtime) and a differential harness (`engine/tests/Sattle.Differential`). The harness compares the engine with the reference per operator kind on generated inputs, with batch cuts and reorderings.

- **At `3629c63`** the engine failed 34 of 107 cases, on decimal keys, `Retain`, `count` together with `countDistinct`, `last` on ties, `Traverse` at the maximum depth, `Series` outside the look-back, and a retraction that precedes its insertion inside one batch.
- **At `ad83e63`** those defects are fixed and the harness passes: 149 of 149, in all 32 worlds, under every batch cut and under reordering inside a batch (verified by the orchestrator; the 150-seed run is reported by the engine core session only). The 21 tests written for the known defects pass. This paper ran neither.
- **What this means for gate 1a.** Theorem 6.1 (incremental equals batch) and Theorem 6.2 (batch invariance) are tested against something that is not the engine, per operator kind, and the engine now agrees with it. This is the first evidence for these two theorems that could have failed and did fail.
- **What the harness does not cover.** `Lift` and `Fixpoint` go through the front end's lowering and unrolling in the reference too, so tests T-LIFT and T-FIX are not independent yet and stay wanted. No lens or bijective rule, no partial window, no draft, no document. `Delta` is not built.
- **Four places where the paper did not determine the result** were found by writing the reference and are ruled in the chapters: the horizon of a `Threshold` (Def. 6.7, 7.10), conflicting fragments (Def. 6.4), the depth of a `Traverse` (Def. 6.3) and the retention of telemetry windows (Def. 7.13). Later rulings: a row without its time at a `Retain` (Def. 5.15), type conflicts (Def. 6.4), telemetry on an identity merge (Law 7.5).

## Ruled and not yet built

- **Telemetry combined by the property's rule on an identity merge** (Law 7.5, item 3; deviation D36). The engine adds the windows of the two ids for every property.
- **Declared retention by the logical frontier** (Def. 7.13; deviation D35). The store drops windows 48 h behind the newest window it has seen.
- **Conflicts reported with the commit** (Def. 6.4; the open part of D34). The engine applies the conflict rule and counts conflicts per run (`conflicts: 0 property, 0 element, 0 telemetry window, 0 uniqueness` on both captures); a commit does not list them.

## Test evidence that is void or weakened

A code review (`paper/research/07-code-review-findings.md`, section 2) found tests that cannot fail or do not test what their name says. The table above marks the affected rows. In words:

The engine commit `ad83e63` changed none of the tests named here (it changed `EvidenceAdapterTests.cs` and added `ServerTests.cs`), so every mark below stands, with one exception: the operator-level evidence for Theorems 6.1 and 6.2 is now the differential harness, which can fail.

- **Void until fixed.** GetPut for the lens (8.1 to 8.3): the test sends only edits that change nothing. The engine side of the event stream (12.1, 12.9): assertions over lists that may be empty. The edge count of the kernel (2.2): an assertion that cannot fail. Fixture evidence for Theorems 6.1 and 6.2 at the level of one operator: the expected values came from the engine under another configuration, and no operator test cut batches or reordered a retraction and an insertion; the differential harness replaces it.
- **Weakened.** The seeded property tests of Ch. 3 and 4 (3.1 to 3.3, 4.1 to 4.8): the generator discards patches the engine's check rejects, and the comparison graph shares the engine's check and encoding, so they test the laws on accepted patches and cannot see a wrong rejection or a wrong encoding. Identity merge (1.7, 1.8): assertions loosened with the rule. Live reproduction (10.7): the test does not replay the live loop. Restart and seal (11.9, 11.10): no test changes the content of a stored commit, and the build check of a seal is not isolated.
- **Valid only when their data were present.** Tests on a real capture, the space overlay or the Cosmos emulator return early and report passed when the file or the emulator is missing. This paper's capture runs in evaluations 4 to 9 had the captures linked and are backed by its own replays; a run without them proves nothing.
- **Not in the default run.** Eleven test classes (drafts, lens, live reproduction, forwarder markers, live crash, refactoring, live resume, served history, restart, snapshot, seal) run only in the slow category. This paper ran that category in evaluations 6, 7 and 9.

## What the gate 1a pass did to the fifteen statements

One of three things per statement: (a) a full proof written into the chapter; (b) the statement restated with the hypothesis that makes it true, with a counterexample to the first form; (c) the statement proved from a named premise on a program or an external system, with the test that checks the premise.

| Statement | Done | What changed in its meaning |
|---|---|---|
| Theorem 2.5 | (b), then (a) | three rows of the table of Def. 2.9 corrected (narrow endpoint types, add supertype, rules that accept `create`), a name condition (N) for `lift` |
| Proposition 4.11 | (c) | on canonical drafts, under RT |
| Theorem 5.2 | (b), (c) | no "fails atomically": a runtime fault is local to its row (new Def. 5.19); required properties hold up to counted faults and reported conflicts; C6 and C7 are not consequences of typing; premise KS. Def. 3.9 says what the commit rule enforces |
| Theorem 5.6 | (b), (c) | about the canonical graphs (the image of `parse`), not every well-formed graph; premise RT |
| Proposition 5.7 | (b), (a) | six premises R0 to R5; the engine checks R1 and R2 only |
| Proposition 5.9 | (a) | stated per position; proved without Theorem 6.1, which removes a circle |
| Proposition 7.7 | (a) | (b) says "no memory" exactly; (c) has an explicit bound; the engine's graph is not part of the statement |
| Law 7.5 | (b), (a) | items 1 and 2 are consequences of the definitions (Def. 7.9 gained two read outcomes); item 3 claims the as-if value only where it is a function of the two windows; the other case is an open ruling |
| Theorem 8.7 | (b), (a) | proved for a fragment made exact by the new Def. 8.7 and 8.8 (conditions V1 to V3); a statement about a model, the class is not built |
| Theorem 9.7 | (b), (a) | three parts; `Group` is not a positive operator; `Threshold` by its episode |
| Theorem 10.5 | (c) | conditional on the documented list-and-watch contract KW1 to KW3 |
| Proposition 10.7 | (c) | premises LV1 to LV4 named; about the digest, not the log |
| Proposition 10.8 | (c) | premises LR1 to LR4 named |
| Theorem 11.6 | (a) | hypothesis (iii) on the writers made explicit; no cited premise |
| Claim 13.5 | (b), (a) | counts on the layout of 11.9 with the conditions under which a read is constant; the engine's cost stays benchmark B6 |

Two typing rules of Ch. 5 are now stated as the engine's checker has them, because Theorems 2.5 and 5.2 need them: `Union` compares kinds and roles and joins presence; an emit refuses an optional value for a required property.

## Proofs derived again at gate 1a

The statements that the rest depends on most were derived again and compared with the written proof. Only Proposition 1.8 was derived before its proof was read; the others had been read while the fifteen statements were closed, so for them this is a second reading step by step, not an independent derivation. No statement is marked "not proved" by this pass; three proofs were corrected.

| Statement | Result | Differences |
|---|---|---|
| Proposition 1.8 (identity merge) | derived independently, same invariant (the representative of a class of seen keys is the least own id) | the written proof ends with "chunking does not reorder operations"; the invariant gives more: its right side depends on the set of processed operations only, so an engine that reorders inside a step still agrees at the end of the step. Both need `own` injective (collision resistance), which the proof states |
| Theorem 1.7 (merge) | (a) to (d) follow as written | none |
| Theorem 6.1 (incremental equals batch) | holds after corrections | the case list missed `Match`, the operators filtered by the frontier (`Series`, the release of a `Window`) and materialization; `Values` was called linear and is a constant; the `Retain` case cited Proposition 5.9 while its sketch cited this theorem; **the `Threshold` case was wrong**: under Def. 7.10 the state is a fold of the recorded sequence and not a per-key function of the integrated input. All corrected in Ch. 6. The two cited theorems of [budiu2023] say what the proof uses (arXiv 2203.16684, Theorems 3.3 and 3.4) |
| Theorem 6.2 (batch invariance) | holds with one more premise and one exception | hidden premise **B4** (a step is its inputs in order for carried `Threshold` state; Def. 7.10 asked for it, the theorem did not list it); exception: `mean` or `last` telemetry on an identity merge (Law 7.5, remark); evidence histories depend on the steps and are outside the statement. Part (a) holds in the model without any premise; the premises are needed for (b) |
| Theorem 6.3 (replay determinism) | holds after corrections | steps (5) and (6) of the proof said that conflicts and fixpoint limits fail atomically, which contradicted Def. 6.4 and the bounded unrolling; corrected |
| Theorems 8.1 to 8.3 (lens laws, stacking) | follow as written | in 8.2(a) the key condition of `Acc` is needed for the result to be a keyed table, not for PutGet; the composition in 8.3 uses every law "where the functions are defined", which the statement says |
| Propositions 5.10 to 5.12 (lifting) | follow as written | none. They are statements about Def. 5.17 and 5.18; `images each` and `selfloops keep` leave them, as the chapter says |
| Proposition 11.9 (restart) | follows from J1 to J3 | a restart with another engine build is covered by the comparison with the stored commits (the start is refused), not by a premise |
| Proposition 11.10 (seal) | follows from SL1 | its premise was named S1, a name that Def. 5.8 and Def. 11.9 already use; renamed SL1 |

Not derived again: the other statements checked by reading only (Ch. 3, 4, 9, 12, 13).

## What the differential harness tests of each theorem, and what it does not

The harness (`engine/tests/Sattle.Differential`) generates input logs for 32 small graph spaces, runs the engine under every way of cutting a log (one row per batch, the largest batches, random cuts, a cut at every position, and the largest batches with their rows reversed), and after every batch compares the engine's graphs and telemetry with the reference evaluator's batch result on the inputs consumed so far (`engine/tests/Sattle.Reference`). The reference has its own denotations, materialization and identity catalog. It shares with the engine the parser, the type checker and the typed plan (`Sattle.Language`) and the value library (`Sattle.Core`).

| Statement | What the harness tests of it | What it does not test |
|---|---|---|
| Theorem 6.1 | incremental result equals batch result at every prefix, for worlds of `Filter`, `Map`, `Union`, `Distinct`, `Join`, `Values`, `Retain`, `Group`, `Match`, `Traverse`, `Series` with `Rollup`, `Threshold`, the emits with `update`, conflicting fragments, two key rules, `Link` | `Lift` and `Fixpoint` independently (both run the front end's lowering and unrolling, so a wrong lowering is wrong on both sides); typing and value encoding (shared libraries); lens and bijective rules; partial windows; `Delta`; drafts and configuration edits; stacks of more than the small worlds; scale |
| Theorem 6.2 (b) | the same state under every cut and under reversed rows in a batch, so a retraction before its insertion | (a) as a statement about a transport; evidence histories |
| Premises B1 to B4 | B2 through worlds with advances; B3 through the `link` world on generated logs; B4 as far as a world puts an advance and a change of structure into one step | B1 as a checker rule (T-B1); B3 on generated partitions of many keys (T-B3); B4 by construction (T-B4) |
| Proposition 5.9 | the `retain` world: the retained set under every cut against a reference written from Def. 5.15 | rows expired on arrival and retractions of held rows only as far as the generator produces them (T-RETAIN) |
| Def. 6.4 (conflicts) | property and uniqueness conflicts as functions of the fragments, whatever arrived first | conflicts listed with the commit (not built) |
| Theorem 6.3 | nothing directly: equality with the reference on every cut implies equal states in two runs of these worlds | equal commit records of two runs, seals |
| Theorems 6.5, 6.6 | the engine's evaluation of the unrolled plan against the reference's evaluation of the same unrolled plan, on generated edge sets with retractions | that the unrolling reaches the least fixpoint (T-FIX) |
| Propositions 5.10 to 5.12 | nothing independent | the lowering of `lift` against Def. 5.17 and 5.18 (T-LIFT) |
| Theorem 5.2 | nothing: the checker is shared and no conformance is asserted | T-SOUND |
| Proposition 1.8 | the representative on the logs of the `link` world, against the reference's own catalog | generated orders of many keys (T-B3) |
| Ch. 3, 4, 8, 9, 10, 11, 12 | nothing | |

## Open rulings and questions found by the pass

- **Telemetry under `mean` or `last` on an identity merge** (Law 7.5, remark): the ruled outcome depends on arrival order and differs from the batch result that Theorem 6.1 asks for. Needs a ruling of the paper session.
- **A runtime fault is local to its row** (Def. 5.19) and the commit rule enforces WF1 to WF3 only (Def. 3.9): written here because Theorem 6.2(b) forces it and the engine does it; to be confirmed by the paper session as a ruling.
- **B4** is new as a named premise; whether the harness already covers it is a question for the engine core session.
- **R3, R4, R5** of Proposition 5.7: the engine reports `preserves: union` without checking them.
