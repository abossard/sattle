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

Of the 105 statements counted as proved in one of the three ways, 2 are checked by a differential harness against an independent reference evaluator (Theorems 6.1 and 6.2; the engine passes it since evaluation 12), 10 by seeded property tests that a review found weakened, 54 by example tests or measurements only, some of them void or weakened (last section but one), and 39 by reading only. Of the fifteen statements closed in the gate 1a pass, nine were **restated** because their first form was false or too strong (2.5, 5.2, 5.6, 5.7, 7.5, 8.7, 9.7, 11.6, 13.5), and seven are proved under named premises (4.11, 5.2, 5.6, 5.7, 10.5, 10.7, 10.8). "Proved under premises" is not "proved": the premise is the part that a test must still check.

## The table

| Statement | Name | Status | Technique | Checked by, other than reading |
|---|---|---|---|---|
| Theorem 1.1 | canonical equality | proved | canonical bytes are a normal form: equality of values iff equality of encodings | example tests (`ValueTests.cs`) |
| Corollary 1.2 | decidability and cost | proved | from the definitions and the results its status names | nothing: reading only |
| Corollary 1.3 | digest compatibility | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 1.4 | allocation is a pure function, shared identity | proved | allocation is a function of the key; collision resistance assumed | example tests |
| Theorem 1.5 | timeless binding | proved | case analysis over the three binding rules | example tests |
| Theorem 1.6 | canonicalization change keeps ids | proved | totality of the migration; conflict case excluded | nothing: reading only |
| Theorem 1.7 | merge and split | proved | invariant on forwarding chains (strictly decreasing ids); hypothesis added at the second reading: a record leaves only a current representative | example tests (`IdentityMergeTests.cs`); **weakened**: assertions were loosened in the commit that changed the rule |
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
| Proposition 4.5 | algebraic structure, correction of the review | proved, restated | patches as partial injections on graphs; inverse category after adding a zero; groupoid on revisions only in net form | nothing: reading only |
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
| Theorem 6.1 | incremental correctness, T5 | proved, cited | structural induction on the plan; chain rule; two cited theorems of [budiu2023], statements checked against the arXiv text; cases added at gate 1a: `Match`, operators filtered by the frontier, `Threshold` as a fold of the recorded sequence, materialization | differential harness against an independent reference evaluator: at the engine of evaluation 12 the engine's incremental result equals the reference's batch result in all 32 worlds, under every batch cut and under reordering inside a batch (149 of 149, verified by the orchestrator). Not covered independently: `Lift` and `Fixpoint` (the reference uses the engine's lowering and unrolling), the type checker and the value library (shared), lens rules, partial windows, `Delta`. The earlier fixture evidence took its expected values from the engine itself |
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
| Law 7.5 | series identity under graph change | proved, restated; item 3 ruled | items 1 and 2 from Def. 7.3, 7.9, 3.6; item 3: stored windows combine as monoid values where the combine rule gives one, otherwise equal values or no value with a counted conflict; commutative and associative; a merged value is always the as-if value (Theorem 7.2) | nothing: reading only. The engine differs in four places (D37) |
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
| Theorem 9.3 | rebuild is exact for graph-input projections | proved, restated | from Theorem 6.1; hypothesis added: operators that are functions of the integrated input and the frontier (no `Threshold`) | nothing: reading only |
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
| T-B3 | `Identity_is_independent_of_key_arrival_order_on_generated_inputs` | per seed: 5 to 40 keys of one domain, links that form a random partition, 20 random orders of the first uses and links, 3 random chunkings of each; one world with a `Group` (a count and a sum) keyed by the identity that merges | for every key, `Rep` is the smallest own id of its class; the state digest is equal across all chunkings of one order; every forwarding record points to a smaller id; the grouped count and sum of a merged entity equal those of one group over both keys (Theorem 6.1; expected to fail today, D37) | premise B3 of Theorem 6.2, the engine side of Proposition 1.8, deviation D37 |
| T-B1 | `A_telemetry_sink_whose_group_key_is_finer_than_its_element_key_is_rejected` | projections with a `Rollup` or `Group` over telemetry whose keys canonicalize to one element key | the checker refuses them with a typed error | premise B1 as a checker rule (O6.7) |
| T-INC (exists as the differential harness since evaluation 11a, passes since evaluation 12) | `The_engine_equals_the_reference_however_the_log_is_cut` and its siblings | per operator kind: random sequences of inserted and retracted rows, late rows inside the horizon, frontier advances | after every input, the sum of the output deltas equals a naive evaluation of the operator on the summed input | O6.1: that each strategy is the rule of Theorem 6.1 |
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
| T-MERGE-TELE | `A_merge_combines_windows_as_if_written_under_the_survivor` | per combine rule (`sum`, `min`, `max`, a mean with sum and count, a mean as one number, a last with its order, a last without), two or three ids with windows, rows before the link and after it (among them rows that reach one window through two groups), every merge order | a monoid value and equal values give the as-if value; two different values that are not monoid values give no value and one counted conflict, also after a third id; the result is equal for every merge order and every chunking | Law 7.5 item 3 as ruled (O7.6, D36, D37). Fails today on the rows after the link and in the reference evaluator |

## An independent check exists, and what it shows

Since the engine of evaluation 11a the engine's tests hold a reference evaluator written from the definitions of this paper (`engine/tests/Sattle.Reference`, which uses the engine's value and language libraries and none of its runtime) and a differential harness (`engine/tests/Sattle.Differential`). The harness compares the engine with the reference per operator kind on generated inputs, with batch cuts and reorderings.

- **At evaluation 11a** the engine failed 34 of 107 cases, on decimal keys, `Retain`, `count` together with `countDistinct`, `last` on ties, `Traverse` at the maximum depth, `Series` outside the look-back, and a retraction that precedes its insertion inside one batch.
- **At evaluation 12** those defects are fixed and the harness passes: 149 of 149, in all 32 worlds, under every batch cut and under reordering inside a batch (verified by the orchestrator; the 150-seed run is reported by the engine core session only). The 21 tests written for the known defects pass. This paper ran neither.
- **What this means for gate 1a.** Theorem 6.1 (incremental equals batch) and Theorem 6.2 (batch invariance) are tested against something that is not the engine, per operator kind, and the engine now agrees with it. This is the first evidence for these two theorems that could have failed and did fail.
- **What the harness does not cover.** `Lift` and `Fixpoint` go through the front end's lowering and unrolling in the reference too, so tests T-LIFT and T-FIX are not independent yet and stay wanted. No lens or bijective rule, no partial window, no draft, no document. `Delta` is not built.
- **Four places where the paper did not determine the result** were found by writing the reference and are ruled in the chapters: the horizon of a `Threshold` (Def. 6.7, 7.10), conflicting fragments (Def. 6.4), the depth of a `Traverse` (Def. 6.3) and the retention of telemetry windows (Def. 7.13). Later rulings: a row without its time at a `Retain` (Def. 5.15), type conflicts (Def. 6.4), telemetry on an identity merge (Law 7.5).

## Ruled and not yet built

- **Telemetry combined by the property's rule on an identity merge** (Law 7.5, item 3; deviation D36). The engine adds the windows of the two ids for every property.
- **Declared retention by the logical frontier** (Def. 7.13; deviation D35). The store drops windows 48 h behind the newest window it has seen.
- **Group state re-keyed on an identity merge, and telemetry rows after a link combined by the property's rule** (Theorem 6.1, Law 7.5 item 3; deviation D37). The engine keeps two groups and lets their contributions meet at the sink under the conflict rule.
- **Conflicts reported with the commit** (Def. 6.4; the open part of D34). The engine applies the conflict rule and counts conflicts per run (`conflicts: 0 property, 0 element, 0 telemetry window, 0 uniqueness` on both captures); a commit does not list them.

## Test evidence that is void or weakened

A code review (`paper/research/07-code-review-findings.md`, section 2) found tests that cannot fail or do not test what their name says. The table above marks the affected rows. In words:

The engine of evaluation 12 changed none of the tests named here (it changed `EvidenceAdapterTests.cs` and added `ServerTests.cs`), so every mark below stands, with one exception: the operator-level evidence for Theorems 6.1 and 6.2 is now the differential harness, which can fail.

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
| Law 7.5 | (b), (a) | items 1 and 2 are consequences of the definitions (Def. 7.9 gained two read outcomes); item 3 claims the as-if value only where it is a function of the two windows; the other case was ruled in the second pass (no value, counted conflict) |
| Theorem 8.7 | (b), (a) | proved for a fragment made exact by the new Def. 8.7 and 8.8 (conditions V1 to V3); a statement about a model, the class is not built |
| Theorem 9.7 | (b), (a) | three parts; `Group` is not a positive operator; `Threshold` by its episode |
| Theorem 10.5 | (c) | conditional on the documented list-and-watch contract KW1 to KW3 |
| Proposition 10.7 | (c) | premises LV1 to LV4 named; about the digest, not the log |
| Proposition 10.8 | (c) | premises LR1 to LR4 named |
| Theorem 11.6 | (a) | hypothesis (iii) on the writers made explicit; no cited premise |
| Claim 13.5 | (b), (a) | counts on the layout of 11.9 with the conditions under which a read is constant; the engine's cost stays benchmark B6 |

Two typing rules of Ch. 5 are now stated as the engine's checker has them, because Theorems 2.5 and 5.2 need them: `Union` compares kinds and roles and joins presence; an emit refuses an optional value for a required property.

## Proofs derived again at gate 1a

Two passes. In the first, Proposition 1.8 was derived before its proof was read; the other proofs had already been read. In the second pass each statement below was derived again from the definitions with the chapter closed and then compared with the text. That is not independent of a memory of the written proof, and it is said so here; it found the differences listed. No statement is marked "not proved". Where a difference changed a statement, the old form is in the next section.

| Statement | Result | Differences |
|---|---|---|
| Theorem 1.7 (merge) | holds with one more hypothesis | `rep` "follows forwarding records until none applies" is a function only if at most one record leaves an id. The statement did not say that a record is added only from a current representative. Added |
| Proposition 1.8 (order-independent survivor) | derived independently, same invariant | the written proof ends with "chunking does not reorder operations"; the invariant gives more, since its right side depends on the set of processed operations only. Both need `own` injective, which the proof states |
| Theorem 6.1 (incremental equals batch) | holds after the corrections of the first pass | missing cases (`Match`, operators filtered by the frontier, materialization), `Values` not linear, a circle with Proposition 5.9, and the `Threshold` case wrong for Def. 7.10. **The engine does not satisfy the theorem in one case found in the second pass:** a `Group` keyed by an identity that merges is not re-keyed (D37) |
| Theorem 6.2 (batch invariance) | holds as restated | hidden premise B4; (b) said the step's patch "equals" the net composition, but only the edit sets are equal: revision numbers differ, and a step whose edits cancel commits nothing; what is recorded per commit (reported conflicts, evidence histories) follows the steps; (a) holds only when the same inputs are the same steps, and the prototype makes a chunk one step, so only (b) applies to its replays; the merge of a non-monoid telemetry window is a function of the sequence |
| Theorem 6.3 (replay determinism) | holds as restated | two proof steps contradicted Def. 6.4 (first pass); the statement could be read as equal commit records for any cut of the same records. It now says that a batch is one input of the recorded sequence and that both runs use one engine release |
| Theorems 8.1, 8.2 (a), (b), 8.3 | follow as written | none. In 8.2(a) the key condition of `Acc` is needed for the result to be a keyed table, not for PutGet |
| Theorem 8.2 (c) | holds with one more condition | the emit is injective only while no two keys are forwarded to one representative; after a merge or an alias two rows give one element. The accepted domain now excludes such elements (the engine rejects them `AmbiguousKey`) |
| Propositions 5.10, 5.11 | follow as written | none |
| Proposition 5.12 | follows as written on the model | written as `lift` statements the two routes agree only for equal look-backs; remark added |
| Proposition 11.9 (restart) | holds as restated | the statement said that a stored log that ends early refuses the start. The journal is written first, so after a kill the log is short by design: the replay appends the missing commits and refuses only a difference or a log that ends inside one recomputed group. After a kill the space serves the state after the last journaled entry, not "the state before the stop" |
| Proposition 11.10 (seal) | holds with a wider premise | SL1 covered "every part that a later entry reads"; the conclusion also needs the parts that the space serves (history, event ring). Widened; renamed from S1 in the first pass |

**Reading-only statements read again in the second pass** (Ch. 3, 4, 9, 12, 13).

| Statement | Result | Differences |
|---|---|---|
| Theorems 3.6, 3.7 | follow as written | none |
| Proposition 4.5 (algebraic structure) | **two claims false as stated**, restated with a proof | "inverse category": `then` is undefined for some pairs with matching revisions, so patches are a category only after a zero is added; "groupoid on committed segments": `p then inverse(p)` is a guard identity with edits and not the empty patch, so the groupoid holds only for net forms |
| Theorems 9.1, 9.2, 9.4, 9.6, Proposition 9.8 | follow as written | 9.4 and 9.6 inherit the reading of "recorded sequence" of Theorem 6.3. Def. 9.5 does not say which element survives a uniqueness collision between a replayed and a carried element (Def. 6.4 would) |
| Theorem 9.3 (rebuild is exact) | holds with one more hypothesis | a rebuild is the batch result on the input graphs, which is the output only for operators that are functions of the integrated input. A `Threshold` is a fold of the sequence: a rebuild starts in `initial` and loses states entered behind the retained range. The health signals are such a view. Hypothesis and consequence added |
| Theorem 9.5 (support) | follows; wording | "no replayed input produces it" is wrong when the replayed inputs hold retractions; stated by the sign of the support |
| Proposition 12.2, 12.5, Theorem 12.7 | follow as written | none |
| Theorem 12.3 (same entity across graphs) | first sentence follows; second is not a theorem | the converse (one entity, one id) holds only through `Rep`; "an aggregate never reuses a member's id" is a rule for authors that no judgment enforces. Status text changed |
| Theorem 12.4 (alignment) | holds after a definition | the proof took the logical time of a revision from Def. 3.8 and Theorem 3.5, neither of which mentions logical time. Defined in the proof |
| Theorem 12.6 (block identity) | follows | "iff" lacked "unless `H` has a collision" |
| Theorems 13.1, 13.3, 13.4 | follow as written | 13.1 counts lookups for the two-term form of the join rule, and its degrees are those of the updated integrals |

**Not read again:** Corollaries 1.2, 1.3, Theorem 1.6; Theorems 2.3, 2.6; Theorems 5.3, 5.5, Proposition 5.4; Propositions 6.7, 6.8; Theorems 7.2, 7.3, 7.6; Theorem 8.4; Theorem 10.3.

## Statements whose meaning changed, with the old form

| Statement | Old form | New form |
|---|---|---|
| Def. 2.9, table | narrow endpoint types: compatible for readers; add supertype: compatible for writers; add required property: compatible for every reader; add optional property: compatible | breaking for a reader whose pattern uses a removed type; breaking for writers if a required property is gained; breaking for a `lens` or `bijective` rule that accepts `create`; compatible under the name condition (N) |
| Theorem 2.5 | "if the delta contains only compatible changes and the re-check is run, `D` remains well-typed" (sketch) | the judgments stay derivable, for the corrected table and under (N) |
| Def. 3.9 | committable iff the result is well-formed (WF1 to WF4) | the commit rule enforces the guards and WF1 to WF3; an element that misses a required property after a counted fault or a reported conflict is committed and reported |
| Proposition 4.5 | "an inverse category"; "a groupoid on the revisions" for committed segments | an inverse category after a zero is added; a groupoid for net forms only |
| Proposition 4.11 | conditional on `parse(print(G)) = G` for every well-formed graph | for canonical drafts, under premise RT |
| Typing rule `Union` | "all `Rᵢ` equal" | same fields with equal kinds and roles; presence is joined |
| Typing rule EmitEntity | "required properties present for create" | every required property set, and with a `req` value |
| Def. 5.19 (new) | Theorem 5.2 listed division by zero, overflow and conflicting assignments as failures of the event | a runtime fault is local to its row: the value is absent and counted, the event does not fail |
| Theorem 5.2 | every committed fact conforms "or the projection event fails atomically" | kinds and ownership always; required properties up to counted faults and reported conflicts; C6 and C7 not implied; premise KS |
| Theorem 5.6 | `parse(print(G)) = G` for every well-formed definition graph | on canonical graphs (the image of `parse`), under premise RT |
| Proposition 5.7 | `Out` is equal after any accepted refactoring | under premises R0 to R5 |
| Theorem 6.1 | the integral of the output deltas equals `⟦P⟧` on the integrated inputs, for every operator | the same with the frontier as an argument for `Retain`, `Series` and window release, and with the recorded sequence for `Threshold` |
| Theorem 6.2 | premises B1 to B3; the step's patch "equals" the net composition; (a) without condition | premises B1 to B4; equal edit sets, other revision numbers; (a) when the same inputs are the same steps; B1 is about rows written under one id |
| Theorem 6.3 | equal commit records for the same recorded sequence | the same, with a batch as one input of the sequence and one engine release |
| Law 7.5, items 1 and 2 | design rules | consequences of Def. 7.3, 7.9, 3.6; Def. 7.9 gained the read outcomes `Undeclared` and "undefined" |
| Law 7.5, item 3 | first: combine by the property's rule, or conflict (no proof); before that: policies `keepSeparate` and `preferSurvivor` | ruled: monoid values combine in their monoid (`sum`, `min`, `max`, a mean with sum and count, a last with its order); any other stored value: equal or no value with a counted conflict, absorbing; rows after the link under two ids combine the same way |
| Proposition 7.7 | (b) "the states equal those computed from the data without the incident" | (b) the state is a function of `A` and the threshold states; (c) with an explicit bound |
| Theorem 8.2 (c) | the accepted domain excludes edits of the literal values | and elements whose identity has more than one key |
| Def. 8.7, 8.8 (new), Theorem 8.7 | four laws claimed for "the v1 fragment" of Def. 8.6, with no translation defined | proved for the grammar of Def. 8.7 (V1 to V3) and the translation of Def. 8.8 |
| Theorem 9.3 | rebuild is exact for every projection whose inputs are graph ports | for those without a `Threshold` |
| Theorem 9.5 | removed iff no replayed input produces it | remains iff its support on the replayed inputs is positive |
| Theorem 9.7 | lineage for the positive operators "and additive `Group`"; `Threshold` by the deciding run | three parts: positive plans; one per-key operator last on non-negative input; `Threshold` by its episode |
| Theorem 10.5 | list and watch delivers every change (citation) | conditional on the documented contract KW1 to KW3 |
| Propositions 10.7, 10.8 | sketches with unnamed premises | premises LV1 to LV4 and LR1 to LR4; 10.7 is about the digest, not the log |
| Theorem 11.6 | hypotheses (i) and (ii) | and (iii): every writer evaluates the one recorded input sequence |
| Proposition 11.9 | a stored log that ends early refuses the start; the space serves the state before the stop | missing commits are appended; refused are a difference and a log that ends inside a recomputed group; the state served is the one after the last journaled entry |
| Proposition 11.10 | premise S1: equal in every part that a later entry reads | premise SL1: equal in every part that a later entry reads or that the space serves |
| Theorem 12.3 | proved, both sentences | first sentence proved, converse only through `Rep`; second sentence a rule for authors |
| Theorem 12.6 | same id iff rule and key are equal | unless `H` has a collision |
| Claim 13.5 | a hot point read is `O(1)` | constant by handle at the base; `O(1 + ℓ_k)` at a pinned revision; one-hop scan `O(deg_base(v) + L_v)` |

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

## Open points after the second pass

- **Ruled:** telemetry windows on an identity merge (Law 7.5, item 3), on 2026-11-11, by the principle of D34. Accepted by the project: Def. 5.19, Def. 3.9, the two typing rules, the corrected table of Def. 2.9, premise B4, Def. 8.7 and 8.8.
- **Deviation D37** (reported by the engine session): a `Group` keyed by an identity is not re-keyed on a merge, so the engine does not give the batch result of Theorem 6.1 there; telemetry rows after a link meet under the rule of B1 and not under the combine rule; the reference evaluator shares the second behaviour, so the differential harness does not see it. Tests T-B3 and T-MERGE-TELE are written to fail on it.
- **A view with a `Threshold` is not rebuilt exactly** (Theorem 9.3 as restated). The configuration edit by full replay is exact; a rebuild mode is not built, so nothing is wrong today, but the proposal "rebuild for graph-input projections" of Ch. 9 needs this exception.
- **R3, R4, R5** of Proposition 5.7: the engine reports `preserves: union` without checking them.
- **B4** has no targeted test (T-B4).
- **Not read again:** the statements listed at the end of the section on derivations.
