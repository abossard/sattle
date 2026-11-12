# Inventory of laws, theorems and propositions

**Claim.** Every numbered theorem, proposition, corollary, law and claim of the paper is listed here with its status, the technique of its proof and what checked it besides reading.
**Status.** Complete for Ch. 1 to 13 at the text of this revision. No statement is a sketch or open. The third pass found four statements with a part that is false as stated or short of a hypothesis. Two of them (5.5, 10.3) are restated in their chapters. For the other two (5.3, 7.1) the restatement is pending a choice of the project; their chapters say so, and the two proposed forms are in the section "Third pass". No proof in this paper is mechanized. Where the last column says "reading only", the proof was written and read, and nothing else; the statements that a second reader derived again at gate 1a are listed in their own section, with every difference found.
**Gate.** 1a (theory). This file is the evidence of that gate.

## Reading the table

- *Set A* and *set B* name runs of the project that the authors did not repeat: set A the full test and check runs made before a state of the software is recorded, set B the runs of the engine's developers (Ch. 14, 14.1).

- **proved**: a full proof is in the chapter.
- **proved, cited**: a full proof relative to a result of the literature that is stated in the proof and not re-proved.
- **proved under premises**: a full proof from named properties that the model does not give: properties of the engine or of its programs (each with a test, existing or wanted), or of an external system (taken from its documentation).
- **restated**: the first form was false or too strong; the statement now carries the hypothesis that makes it true, and the chapter gives the counterexample to the first form.
- **restatement pending**: the proof in the chapter is complete for a part of the statement; another part is false as stated or needs a hypothesis that the statement does not have. The proposed restatement is in this file and not yet in the chapter.
- **sketch**: an argument that is not a full proof. The chapter says why it is not closed.
- **tested**: no proof is written; the statement is a property of the implementation or follows at once from a definition, and tests exercise it.
- **open**: neither proved nor tested as stated.
- A statement with several parts takes the status of its weakest part. A statement that is proved relative to stated assumptions of its chapter (the store properties S1 to S5, a coverage declaration, collision resistance) counts as proved; the assumptions are in the statement.
- The numbers T1 to T16 of the reviews appear in the names ("T2", "T6"). Facts (Ch. 0, Ch. 11) and rules (Ch. 1, Ch. 7) are stipulations or citations and are not listed.

## Counts

| Status | Two revisions ago | Before the gate 1a pass | After the second pass | After the third pass | With 5.5 and 10.3 restated | After the reading of Ch. 7 | With 7.4 and 7.7 restated | With Corollary 7.9 | With Proposition 13.2 proved | With 5.3 and 7.1 restated | After the derivation of 5.3 and 7.1 (F14) | With F14 applied |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| proved | 81 | 86 | 94 | 90 | 92 | 90 | 92 | 93 | 94 | 96 | 93 | 96 |
| proved, cited | 1 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 | 2 |
| proved under premises | 0 | 2 | 9 | 9 | 9 | 9 | 9 | 9 | 9 | 9 | 9 | 9 |
| restatement pending | 0 | 0 | 0 | 4 | 2 | 4 | 2 | 2 | 2 | 0 | 3 | 0 |
| sketch | 19 | 11 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| tested | 8 | 8 | 8 | 8 | 8 | 8 | 8 | 8 | 7 | 7 | 7 | 7 |
| open | 4 | 4 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| all | 113 | 113 | 113 | 113 | 113 | 113 | 113 | 114 | 114 | 114 | 114 | 114 |

The four statements with a pending restatement (5.3, 5.5, 7.1, 10.3) were counted as proved and read only, except 7.1, which has example tests. Theorems 5.5 and 10.3 are restated in their chapters as part D of the third pass proposes, and count as proved again. Theorems 5.3 and 7.1 stay pending until the project chooses between restating them and adding the typing rule for windowed rows. The sentence below still gives the figures after the second pass.

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
| Theorem 2.2 | the kernel describes itself | tested | finite check executed by the engine | executed at start-up; mutation tests; the edge-count assertion cannot fail |
| Theorem 2.3 | a closure is well defined | proved | well-founded recursion over an acyclic import graph | nothing: reading only |
| Theorem 2.4 | release digest | proved | from the definitions and the results its status names | example tests |
| Theorem 2.5 | compatible changes keep projections well typed | proved, restated | a derivation consults the closure in four forms; each compatible row leaves the answers unchanged or strengthens presence; three rows of the table of Def. 2.9 corrected, with counterexamples | nothing: reading only. The classifier is not built (T-COMPAT) |
| Theorem 2.6 | a migration does not rewrite history | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 2.7 | value roles do not affect evaluation | tested | from the definitions and the results its status names | example tests; (b) a source search |
| Theorem 3.1 | committed states are well-formed | proved | induction on the log | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 3.2 | the past does not change | proved | append-only log by definition | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 3.3 | equality by written form | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 3.4 | checking a change is local | proved | from the definitions and the results its status names | example tests |
| Theorem 3.5 | frontiers only grow | proved | monotonicity under premise F1 | example tests |
| Theorem 3.6 | identity of scoped graphs | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 3.7 | order of evaluation | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 4.1 | associativity, T2 | proved, strengthened (2026-11-12: holds without the hypothesis that both inner compositions are defined; counted as proved) | case analysis on guards | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.2 | applying a composition, T2 | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.3 | inverse law, T1 | proved | guards of the inverse match the result | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.4 | the order of edits does not matter | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Proposition 4.5 | what algebraic structure patches form | proved, restated | patches as partial injections on graphs; inverse category after adding a zero; groupoid on revisions only in net form | nothing: reading only |
| Theorem 4.6 | signed deltas form a group, T3 | proved | pointwise group of integer weights | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.7 | delta respects patches, T4 | proved | computation on facts | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.8 | from deltas back to graphs, T4 | proved | from the definitions and the results its status names | seeded property tests, 4,000 cases per property (`AlgebraTests.cs`); **weakened**: the generator keeps only patches that the engine's own check accepts, so a wrong rejection cannot be seen |
| Theorem 4.9 | two things that fail | proved | counterexamples | example test |
| Proposition 4.10 | undo is a patch | proved | from the definitions and the results its status names | example tests (`DraftTests.cs`, canvas) |
| Proposition 4.11 | two views | proved under premises (RT), restated | from Theorems 4.3, 4.8 and the premise RT of Theorem 5.6, on canonical drafts | example test on the example modules; `Unprintable` example |
| Theorem 5.1 | checking is decidable | proved | structural recursion over a finite definition graph | example tests (`LanguageTests.cs`) |
| Theorem 5.2 | type soundness | proved under premises (KS), restated | induction over expressions, operators and sinks; rows sound up to counted faults; (a) kinds and ownership always, (b) required properties up to counted faults and reported conflicts, (c) C6 and C7 not implied; three counterexamples to the first form | example tests of typed errors; no conformance assertion on generated projections (T-SOUND) |
| Theorem 5.3 | role non-interference | proved, restated (F14 applied 2026-11-12) | restated 2026-11-12 with the supports of windowed relations (Def. 5.20); derived again (F14): holds except that a merged window without a value by conflict has no record under a time-to-live, so the support depends on whether two stored values are equal (F14, item 1); the proof's reason does not cover filled and no-data records | nothing: reading only |
| Proposition 5.4 | closed language | proved | inspection of the kernel table | nothing: reading only |
| Theorem 5.5 | relationship identity is retype-stable | proved | restated in the third pass by Def. 6.4: the id does not depend on the type, and of two relationships on one tuple the smaller id is present | nothing: reading only |
| Theorem 5.6 | lossless syntax | proved under premises (RT), restated | on the canonical graphs (the image of `parse`): bijection, idempotent `fmt`, equal canonical form iff equal graph | example test on the example modules (T-ROUNDTRIP wanted) |
| Proposition 5.7 | refactoring preserves outputs | proved under premises (R0 to R5), restated | every sink keeps its fragments; materialization splits over disjoint keys; six counterexamples to the first form | 10 cases (`RefactorTests.cs`); R3, R4, R5 unchecked by the engine and untested (T-REFACTOR) |
| Proposition 5.8 | parameters are constants of the binding | tested | from the definitions and the results its status names | example tests |
| Proposition 5.9 | retention is a function of the frontier | proved | induction over the positions of the input with the held weights as invariant | example tests, two captures (`RetentionTests.cs`); the `retain` world of the differential harness (engine against a reference written from Def. 5.15, every cut) |
| Proposition 5.10 | lifting is a quotient | proved | direct from Def. 5.17 | 28 and 36 example cases (`ProjectionEdgesTests.cs`, `LiftTests.cs`) |
| Proposition 5.11 | conservation | proved | partition of the edges by fibre | the same cases |
| Proposition 5.12 | lifting composes | proved | composition of partial functions; sums over a disjoint union | two cases against a one-step lift |
| Theorem 6.1 | step by step equals from scratch, T5 | proved, cited | structural induction on the plan; chain rule; two cited theorems of [budiu2023], statements checked against the arXiv text; cases added at gate 1a: `Match`, operators filtered by the frontier, `Threshold` as a fold of the recorded sequence, materialization | differential harness against an independent reference evaluator: at the engine of evaluation 12 the engine's incremental result equals the reference's batch result in all 32 worlds, under every batch cut and under reordering inside a batch (149 of 149, from set A). Not covered independently: `Lift` and `Fixpoint` (the reference uses the engine's lowering and unrolling), the type checker and the value library (shared), lens rules, partial windows, `Delta`. The earlier fixture evidence took its expected values from the engine itself |
| Theorem 6.2 | batch invariance, T6 | proved | from Theorem 6.1 and four premises B1 to B4 (B4 found at gate 1a); one known exception (Law 7.5, remark) | differential harness as for 6.1 (every cut, reordering inside a batch); both captures at five chunk sizes; targeted tests of B1 and B2; B3 on examples and the `link` world; B4 by the harness as far as its worlds put an advance and a structure change into one step (T-B4) |
| Theorem 6.3 | replay determinism, T7 | proved | function of the recorded input sequence | two replays compared on fixtures; capture tests |
| Theorem 6.4 | a snapshot and the following records rebuild every version, T8 | proved | from the definitions and the results its status names | example tests on memory and SQLite (`StoreTests.cs`) |
| Theorem 6.5 | recursion ends at the least fixpoint, T9 | proved | ascending chain in a finite lattice | example tests on acyclic input only |
| Theorem 6.6 | retraction under recursion, T10 | proved | from Theorem 6.1; counterexample | example tests on acyclic input only |
| Proposition 6.7 | cross-graph telemetry reads are deterministic | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 6.8 | stateful telemetry operators under revision | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 7.1 | the planes are separate | proved, restated (F14 applied 2026-11-12) | restated 2026-11-12 with the supports of windowed relations; the first two sentences follow from Theorem 5.3; the bound on the key support fails when a window's first record arrives late inside the late horizon (counterexample, F14, item 2) | example tests; revision counts on the captures |
| Theorem 7.2 | sums add up | proved | additivity of exact sums | nothing: reading only |
| Theorem 7.3 | structure derived from measurements can be replayed | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 7.4 | bounded change of structure | proved, restated (F14 applied 2026-11-12) | toggles bounded as runs of a state machine (proved); "over `N` windows without such a gap it appears at most once and goes at most once" fails when a window's first record arrives late (F14, item 2), a gap in the form proposed in F6, item 1 | one example test |
| Law 7.5 | series follow the element when the graph changes | proved, restated; item 3 ruled | items 1 and 2 from Def. 7.3, 7.9, 3.6; item 3: stored windows combine as monoid values where the combine rule gives one, otherwise equal values or no value with a counted conflict; commutative and associative; a merged value is always the as-if value (Theorem 7.2) | nothing: reading only. The engine differs in four places (D37) |
| Theorem 7.6 | element view is well defined | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 7.7 | roll-up | proved, restated (F6, item 2, applied 2026-11-12) | (a) and (b) proved; (c) restated with the appearance and end of a signal's rows and the hypothesis that graph data derive from measurements only through `Threshold` outputs, which the shipped definitions do not meet (F13, item 1); no longer uses Theorem 5.3 | one fixture test, two captures; that the engine's `health` graph equals Def. 7.12 is tested by example only |
| Corollary 7.9 | the workload model | proved | Proposition 7.7 (a) and (b) for the four differences of the workload model; (c) shown not to hold, with the two kinds of change that it misses | `WorkloadTests.cs`, five scenarios on the `estate` |
| Proposition 7.8 | bounded reach | proved | breadth-first reach | example test |
| Theorem 8.1 | bijective rules | proved | inverse function | **void for GetPut** (the test sends only edits with before equal to after, which are dropped before any lens runs); PutGet on generated edit sequences on one lens stands |
| Theorem 8.2 | the lens steps | proved | direct verification of GetPut, PutGet, PutPut; counterexample for PutPut | **void for GetPut**, as 8.1; PutGet on generated edit sequences on one lens stands |
| Theorem 8.3 | laws survive stacking | proved | composition of lenses ([foster2007]) on accepted domains | one link only; GetPut evidence void, as 8.1 |
| Theorem 8.4 | class of a stack | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 8.5 | the law is checked on every edit | proved | the staged check is the law it tests | example tests |
| Theorem 8.6 | scope of backward edits | proved | from the definitions and the results its status names | example tests of rejections |
| Theorem 8.7 | laws of the first sync fragment | proved, restated | for the grammar of Def. 8.7 and `FWD` of Def. 8.8: a lemma on the shape of the language, then determinism, correctness, completeness, hippocraticness; counterexample to the first form | nothing: reading only. `sync` is not built, so no engine evidence can exist |
| Theorem 9.1 | edit replay | proved | from the definitions and the results its status names | example tests |
| Theorem 9.2 | an edit is not recomputation from scratch | proved | counterexample | nothing: reading only |
| Theorem 9.3 | rebuild is exact for projections that read graphs | proved, restated | from Theorem 6.1; hypothesis added: operators that are functions of the integrated input and the frontier (no `Threshold`) | nothing: reading only |
| Theorem 9.4 | evidence is a derived index of the log | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.5 | support | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.6 | definitions are pinned, the past can be computed again | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 9.7 | lineage soundness | proved, restated | (a) polynomial form of positive plans and restriction; (b) one per-key operator last, non-negative input; (c) `Threshold` by its episode; three counterexamples to the first form | nothing: reading only |
| Proposition 9.8 | cost of evidence | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 9.9 | releases | tested | from the definitions and the results its status names | example test |
| Proposition 9.10 | preview | tested | from the definitions and the results its status names | example test for (a) and (c) |
| Proposition 9.11 | evidence elements are idempotent and can be withdrawn | tested | from the definitions and the results its status names | nothing: reading only |
| Theorem 10.1 | delivering twice changes nothing | proved | idempotence of a set of keyed contributions | not tested for evidence elements |
| Theorem 10.2 | withdrawal removes one source's support | proved | from the definitions and the results its status names | example tests |
| Theorem 10.3 | soundness of reconciliation | proved | restated in the third pass with the hypothesis that the engine holds nothing of that source from behind the scan; counterexample to the first form in the chapter | nothing: reading only |
| Theorem 10.4 | loss is not deletion | proved | from the definitions and the results its status names | example tests on fixtures |
| Theorem 10.5 | list then watch, resumption | proved under premises (KW1 to KW3, external), restated | induction over the watch events | nothing: reading only. The premises are the documented contract of the Kubernetes API and cannot be tested here |
| Theorem 10.6 | processing a recording again gives the same versions | proved | from the definitions and the results its status names | capture tests |
| Proposition 10.7 | a live run is reproduced by its recording | proved under premises (LV1 to LV4), restated | same input sequence in both runs, then Theorem 6.2(b) | **weakened**: the test writes the stream itself and drives the replay; the live loop, its envelope handling and its recording are never replayed (T-LIVE) |
| Proposition 10.8 | a killed live run resumes to the state of a run that was never killed | proved under premises (LR1 to LR4), restated | Proposition 11.9 for the journal, idempotent delivery, then Theorem 6.2(b) | simulated kill against a stand-in (T-RESTART) |
| Theorem 11.1 | reconstruction | proved | from the definitions and the results its status names | example tests on memory and SQLite |
| Theorem 11.2 | one line of versions | proved | conditional write on the predecessor (S1, S2) | example tests on memory and SQLite |
| Theorem 11.3 | a reader sees whole versions | proved | from the definitions and the results its status names | example tests |
| Theorem 11.4 | writing version rows twice does no harm | proved | from the definitions and the results its status names | example tests |
| Theorem 11.5 | following without gaps | proved | from 11.2 and S4, S5 | example tests |
| Theorem 11.6 | processing again writes the same log | proved, restated | induction over the inputs from Theorems 6.3, 11.2, 11.3; hypothesis (iii) on the writers made explicit | nothing: reading only (E6 not run) |
| Theorem 11.7 | a seal depends on the state only | proved | from the definitions and the results its status names | random histories on two stores |
| Theorem 11.8 | uniqueness without a reservation across partitions | proved | from the definitions and the results its status names | example tests |
| Proposition 11.9 | a restart reproduces the state | proved under premises | from Theorem 6.3 under J1 to J3 | example tests, end-to-end restarts; **not exercised**: a stored commit whose content differs from the recomputed one |
| Proposition 11.10 | restart from a state seal | proved under premises | from Proposition 11.9 under SL1 (renamed from S1) | example tests; one deleted-entry case; **weakened**: the foreign-build case also carries a bad digest, so the build check is not isolated |
| Theorem 12.1 | a replica is correct | proved | induction on the feed | canvas tests on a fake socket; no end-to-end digest; the engine-side stream test is void, as 12.9 |
| Proposition 12.2 | joining late | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 12.3 | the same entity across graphs | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 12.4 | alignment is well defined | proved | from the definitions and the results its status names | nothing: reading only |
| Proposition 12.5 | definitions need no new mechanism | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 12.6 | stable block identity | proved | function of canonical bytes | by construction; document tests |
| Theorem 12.7 | inverting a patch of a document | proved | two independent components, each as Theorem 4.3 | nothing: reading only |
| Theorem 12.8 | rendering is a function; escaping | tested | from the definitions and the results its status names | four escape examples |
| Proposition 12.9 | the rule of a client | proved | from the definitions and the results its status names | canvas tests on a fake socket; **void on the engine side**: the event-stream test asserts over lists that may be empty |
| Proposition 12.10 | resources, tags and caching | proved | from the definitions and the results its status names | example tests (`EtagTests.cs`) |
| Proposition 12.11 | one digest of a document per version | proved | from the definitions and the results its status names | example tests, one capture |
| Theorem 13.1 | a join costs in proportion to what it produces | proved, cited | from the definitions and the results its status names | nothing: reading only |
| Proposition 13.2 | grouping commits lowers the cost per commit | proved (arithmetic, with α > 0; F13 applied) | from the definitions and the results its status names | nothing: reading only |
| Theorem 13.3 | a chain of stages | proved | from the definitions and the results its status names | nothing: reading only |
| Theorem 13.4 | budgets of stacked steps add up | proved | from the definitions and the results its status names | nothing: reading only |
| Claim 13.5 | cost of a read of the hot graph | proved, restated | counts on the layout of 11.9: by handle, at a pinned revision, one-hop scan | nothing: reading only. That the engine has this layout and its constants is benchmark B6 (gate 1c) |

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
- **At evaluation 12** those defects are fixed and the harness passes: 149 of 149, in all 32 worlds, under every batch cut and under reordering inside a batch (from set A; the 150-seed run is reported by set B only). The 21 tests written for the known defects pass. This paper ran neither.
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
| Theorem 8.2 (c) | holds with one more condition | the emit is injective only while no two keys are forwarded to one representative; after a merge or an alias two rows give one element. The accepted domain now excludes such elements; the engine is to reject them `AmbiguousKey` and does not yet (F8, item 1) |
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
| Theorem 5.5, Def. 5.10 (third pass) | a retype under the policies `reject`, `keepExisting`, `takeNew` "either keeps the id or fails atomically" | a retype keeps the id; a collision on `(source, target, type)` is decided by Def. 6.4 (the smaller id is present); the three policies are withdrawn |
| Theorem 10.3 (third pass) | the withdrawals of `reconcile` are exactly the deleted records, given a complete scan | the same, given also that the engine holds no contribution of that source from behind the scan's snapshot |
| Theorem 6.1 (third pass) | the frontier as an argument for `Retain`, `Series` and window release; the recorded sequence for `Threshold` | also: a `Series` with a fill or a time-to-live takes the recorded progress of its sources and, per element, the advance since which it is present |
| Theorem 7.6 (third pass) | the view is a function of the graph state and the series | and of the frontier, through the retention of Def. 7.13 |
| Def. 7.13 (third pass) | "`ρ` is at least the late horizon in time"; retained while `b > φ − ρ` | the first clause dropped; retained while `b > λ(φ) − ρ` |
| Def. 7.14 (third pass) | `since(k)`: the first advance at which `k` has a row "after an advance at which it had none"; progress recorded with each envelope; covered iff `b ≤ coveredUntil(σ)` | the first advance at which `k` has a row and no `since`; progress recorded once per source before each advance; covered iff `b + L ≤ progress(σ)`; the premise of coverage named |
| Def. 7.16 (third pass) | `e(k)`: the end of the newest record, or `since(k)` if there is none; time to live at most `within`; "enters and leaves no data only at an advance" | `e(k)`: the later of `since(k)` and the end of the newest record; time to live at most `within` less one window; enters at an advance or when a loss mark or retraction removes the newest record, leaves at an advance or on a late row; a no-data record is not a window of the episode |
| Def. 5.8 (reader rewrite) | the declared contract digest of a `Source` equals the pinned row-contract digest | the reader of every `Source` is known to the program and delivers rows of the declared shape (follows the prototype; a weaker check) |
| Def. 5.10, `Union.conflict` (reader rewrite) | `requireEqual` fails on unequal values | `requireEqual`, `preferFirst`, `preferLast` defined only for rows with a key; under `requireEqual` two values for one key give no value and a reported conflict; set union inside a `Fixpoint` body; only `preserveEvidence` built |
| Def. 5.1 (reader rewrite, new) | none | a projection may carry a description; presentation data like value roles, kinds and labels of Ch. 2, not read by evaluation |
| Theorem 4.1 (reader rewrite) | holds if both inner compositions are defined | holds without that hypothesis (strengthened; the proof is unchanged) |
| Proposition 6.8 (reader rewrite) | the output after any revision is the fold over the revised sequence | the same inside the revisable range; behind it the correction is dropped and counted (B2) |
| Def. 6.7, batch semantics (reader rewrite) | a function of the set of closed windows per key and the frontier | each window with its current value inside the retained range and the value it had on leaving it after that |
| Theorem 7.4 (F6, applied) | at most `⌊N/m⌋` edits, emitted only at a toggle | at most `⌊N/m⌋` toggles plus one change each time the key's row appears or goes; the row goes only after a gap of more than `within − w − L` |
| Proposition 7.7 (c) (F6, applied) | revisions only at toggles, by Theorem 5.3 | toggles or a `Threshold` row that appears or goes, plus two per appearance; hypothesis that graph data derive from measurements only through `Threshold` outputs |
| Def. 7.6 (F6, applied) | no late horizon | a late horizon `H`: revisable until the frontier passes `b + L + H`, later rows dropped and counted |
| Def. 8.4, `Map` and `Union` rows; Theorem 8.2 (c) (F8, applied) | literal outputs of `Map` unclassified; `Union` with `preserveEvidence` over different types ambiguous; (c) for literal set values | literal or repeated outputs make `Map` a lens; `Union` lens for any policy over different types; (c) for field references or literals |
| Def. 9.5 (F9, applied) | `Carry_keep` is every element of `G` that `Eval` does not produce | only those none of whose creating inputs is in `R`; `Carry_drop` empty |
| Theorem 9.5 (F9, applied) | remains iff support is positive | and a create fragment exists |
| Proposition 9.10 (b) (F9, applied) | for a definition edit | for the prototype's full replay, not for Def. 9.4 with a carry |
| Theorem 10.3 (F10, applied) | the engine holds no contribution from behind the snapshot; withdrawals are exactly the deleted records | the engine processed nothing newer than the snapshot, neither a contribution nor a deletion; withdrawals are exactly the held elements that were deleted |
| Theorem 10.6 (F10, applied) | the records of a recording, sorted by capture time | the records of a capture sorted, a run's recording in line order; same settings |
| Proposition 10.8 (F10, applied) | premises LR1 to LR4 | and LR5 (premise LV2), which the proof used |
| Proposition 11.9 (F11, applied) | after a kill, the state of a run never killed | the same on a store that writes a group of commits all or nothing; on the Cosmos store a kill between the batches of one group refuses the start |
| Theorem 12.1, Proposition 12.9 (F12, applied) | for every version; never skips a version | up to the first rejected patch; except across a reported gap |
| Theorems 5.3 and 7.1 (restated 2026-11-12) | graph data a function of the data fields and the `Threshold` outputs only; a closed-window record creates a revision only through a crossing operator | and of the supports of the windowed relations (Def. 5.20, new); a revision also when a support changes, bounded for key supports; rule P6 not adopted, an optional stricter profile under which the first form holds |
| Proposition 7.7 (c) (restated 2026-11-12) | hypothesis: graph data derive from measurements only through `Threshold` outputs | no hypothesis; each change of a data input or a support causes at most one revision per graph |
| Theorems 5.3, 7.1, 7.4 and Def. 7.16 (F14 applied) | supports independent of values without exception; key support changes at most twice per run of windows without a gap | a merged window with conflicting values keeps a record without value (Def. 7.16), so supports stay independent of values; the bound counts each record accepted while no other is in the look-back, with the gap `within − w − L − H` under late records |
| Def. 5.9 (protocol 3.22) | an identity expression may stand in any expression | only in the id or the ends of an emit and in a `Link`; elsewhere refused (`IdentityOutsideSink`) |
| Theorems 5.3 and 7.1 (third pass, pending) | "graph data are a function of the data fields and of the outputs of `Threshold` only"; "a closed-window record creates a revision only if a crossing operator changes graph data" | not yet restated: false for a projection that derives data from the existence of windows; two forms proposed |


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
- **Deviation D37** (reported by set B): a `Group` keyed by an identity is not re-keyed on a merge, so the engine does not give the batch result of Theorem 6.1 there; telemetry rows after a link meet under the rule of B1 and not under the combine rule; the reference evaluator shares the second behaviour, so the differential harness does not see it. Tests T-B3 and T-MERGE-TELE are written to fail on it.
- **A view with a `Threshold` is not rebuilt exactly** (Theorem 9.3 as restated). The configuration edit by full replay is exact; a rebuild mode is not built, so nothing is wrong today, but the proposal "rebuild for graph-input projections" of Ch. 9 needs this exception.
- **R3, R4, R5** of Proposition 5.7: the engine reports `preserves: union` without checking them.
- **B4** has no targeted test (T-B4).
- **Not read again:** the statements listed at the end of the section on derivations.

## Third pass (2026-11-12): derivations, premises, and the paper as its reader sees it

Written by a review of the text. It changed no chapter. Every disagreement below comes with the text that would resolve it; the chapter owner applies it or overrules it. Engine facts were read from the source and from set B's notes; nothing was built or run.

### A. Def. 7.13 to 7.16 (retention, coverage, fill, time to live) derived from the rest of the paper

These are definitions with claims inside them. Each claim was derived from Def. 7.6, 7.10, 6.7, Theorems 6.1 and 6.2 and the premises B1 to B4.

| Claim in the text | Result | Disagreement and proposed text |
|---|---|---|
| 7.13: retention is a function of the frontier and of declared data, the same in a live run and its replay | follows, given that both runs have the same logical time (premise LV2 of Proposition 10.7) | notation: "`b > φ − ρ`" subtracts a duration from a frontier; write `λ(φ)` as Def. 7.16 does |
| 7.13: "`ρ` is at least the late horizon in time" and "the range that can still be revised is the smaller of the late horizon and `ρ`" | **the two sentences contradict each other**: if the first holds the minimum is always the late horizon; and in a replay the horizon is a million windows, which no declared retention reaches | drop "and at least the late horizon in time". Keep `ρ ≥ within` (needed so that the fold of Def. 7.10 can be recomputed over the look-back) and the minimum. Premise B2 then reads "a row for a window that is behind the late horizon or no longer retained is dropped and counted" |
| 7.13: a read of a range that is no longer retained says so | definition; consistent | Theorem 7.6 says the view is a function of the graph state and the series; with retention it is also a function of the frontier (the answer can be "not retained") |
| 7.14: `coveredUntil` only grows | follows (a greatest element of a growing set) | the allowed lateness belongs to a series (Def. 7.6) and `coveredUntil` to a source: say that it is taken per property, with the lateness of that property |
| 7.14: "coverage says that the source was there and would have reported" | holds under an unnamed premise | name it: **(CV) every record of the source is captured at most the allowed lateness after its observed time.** Without CV a filled value can be false; a row that arrives later replaces it inside the late horizon and is dropped and counted behind it |
| 7.14: `since(k)` is "the frontier of the first advance at which `k` has a row after an advance at which it had none" | **no base case**: an element present at the first advance of a run never had an advance at which it had none, so `since(k)` is undefined and nothing is ever covered for it | "the frontier of the first advance at which `k` has a row and no `since`" (set B's rule), or "after an advance at which it had none, or after the start" |
| 7.14: progress is recorded "with each envelope" | disagrees with set B's notes, and matters | the engine records progress once per source immediately before each advance. Say that **coverage is read at advances, like presence**. Otherwise a window can be covered before it is closed (logical time can lag the newest capture time between two advances), and filled records can appear and disappear between advances |
| 7.15: a filled record is a function of coverage and absence | follows: it is a function of the stored windows, the structural input, `coveredUntil`, `since` and the frontier | say "every **closed** window of the look-back that is covered"; with coverage read at advances the two coincide. Not said and needed: a record of a `Series` that reads several properties needs every one of them, stored or filled (engine notes) |
| 7.15, consequence not stated | follows | when `k` leaves and returns, `since(k)` moves forward and the windows before it are no longer covered for `k`: fills that existed for them are withdrawn and the fold of the threshold is recomputed inside the retained range. Intended or not, it should be said |
| 7.16: no data iff `λ(φ) − e(k) > D`, with `e(k)` "the end of its newest record, stored or filled, or `since(k)` if it has none" | **does not match the stated meaning** ("the subject existed for longer than `D` without a record") | a record from before `since(k)` counts. A subject that returns after an absence, with an old record still in the look-back, is in no data at once, although it has existed for less than `D`; and when that record leaves the look-back `e(k)` jumps forward to `since(k)`. Proposed: **`e(k)` is the later of `since(k)` and the end of the newest record of `k`**. Set B's condition ("no record ends within `D`, and an older record is in the look-back or `since(k)` is older than `D`") has the same behaviour; the proposal drops its middle clause |
| 7.16: "a filled series never ends in its fill value when the source is gone" | follows, if logical time still advances | when a source stops, `coveredUntil` stops, no later window is covered, and after `D` the key is in no data. If every source stops, logical time stops too and nothing changes (Ch. 3, 3.4): say so |
| 7.16: "a key enters and leaves no data only at a logical-time advance" | **true for entering, false for leaving** | `e(k)` grows when a late row creates a newer record, and shrinks when a loss mark or a retraction removes the newest record or fill. Proposed: "a key enters no data at an advance, or when a loss mark or a retraction removes its newest record; it leaves at an advance, or when a late row gives it a newer record" |
| 7.16: "the bound of Theorem 7.4 grows by two edits per silence" | follows by counting, a silence being a maximal interval in no data | without late rows there are at most `N·w/D + 1` silences in `N` windows of width `w`, since each needs a gap longer than `D`. Worth stating, because "per silence" alone bounds nothing |
| 7.16: "not retained never produces no data, because `D ≤ within ≤ ρ`" | follows, with one boundary case | with `D = within` a key enters no data up to one window early, at the moment its last record leaves the look-back and `e(k)` falls back to `since(k)`. Require `D ≤ within − w`, or keep `e(k)` as one instant per key instead of reading it from the look-back |
| 7.16: the state and the run counter follow the episode rule unchanged | follows only with a sentence that is missing | **a no-data record is not a window of the key** in the sense of Def. 7.10. It sits at the newest grid position, so if it counted it would keep every episode alive. Set B has this rule ("marker rows are no windows of the episode") |
| 7.16: `Series`, `Rollup`, `Threshold` treat no data as stated | incomplete for the path of Ch. 5, which has a `Map` between roll-up and threshold | say how the mark passes a `Map`, a `Filter` and a `Join`: in the engine it is a Boolean column that a `Map` must pass on explicitly. A record marked no data has no value in a column typed required, which the proof of Theorem 5.2 does not allow for: add the case |
| "State and batch invariance": no dependence on the cut, "(Theorem 6.2)" | follows from **premise B4**, not from the theorem alone | `since(k)` is state sampled at advances, a function of the recorded sequence like the carried state of a threshold. Cite B4, and add `Series` with a fill (arguments: coverage and `since`) to the operators of Theorem 6.1 whose batch semantics takes more than the integrated input |
| "Agreed with set B, not built" | out of date | the engine commit of 2026-11-12 builds it. One point of Def. 7.14 is open there: a window with a loss or partial mark is still filled |

**State of this table after an edit of the text on 2026-11-12** (Ch. 7, 5, 10 and 0, uncommitted when this was written). Applied as proposed: Def. 7.13 (the contradiction and the notation), Def. 7.14 (base case of `since(k)`, coverage read at advances, the lateness of the series in the test `b + L ≤ progress(σ)`, the premise of coverage named), Def. 7.16 (`e(k)` as the later of `since(k)` and the newest record's end, the time to live at most `within` less one window, the no-data record outside the episode, the mark passed on, a record needs every read property, premise B4 cited, the status). Still open:
- applied since: the sentence on entering and leaving no data;
- applied since, by a review of the text in Ch. 6 and Ch. 7: Theorem 7.6 with the frontier, `Series` with a fill or a time-to-live as a case of Theorem 6.1, the count of silences, and the example in the proof of Theorem 7.4;
- that fills are withdrawn for the windows before a new `since(k)` when a subject returns;
- the question to set B below.

**A question for set B, from the roll-up rule, and its answer (2026-11-12).** The engine's notes derived "key `K` is in no data iff every member is" from "a member that is not in no data has a record that ends within `D`", which fails for a member that appeared less than `D` ago and has no record yet. Answer of set B: the engine now counts per member like the reference (a key is in no data iff every member that has a row has a no-data record; `any`: one). A roll-up knows a member only through the rows of the `Series`, and a new member without a record has no row, so as built "every member" means "every member that has a record, stored, filled or marked no data". A member that should keep the key out of no data before it has reported would need a presence row per element from the `Series`, which is a change of the operator. **Recommendation of a review of the text: keep the rule as built.** It is what the key itself shows: no member that the key can see has had a record for longer than `D`. It differs from "every member" only during the first `D` after a member appears without reporting, and closing that difference changes an operator for a transient case. Ch. 7, Def. 7.16, already says this. Two wording points remain, for the chapter owner:
- the rule as written is vacuously true for a key with no member that has a record; such a key has no row at all. Proposed: "A roll-up key has a row only when some member has a record, stored, filled or marked no data; it is in no data iff every such member is."
- Ch. 5, the paragraph on `fill`, `ttl` and `nodata`, still says "A roll-up key is in no data when every member is"; it should follow Def. 7.16.

**Confirmed by set B from its source (2026-11-12).**
- *Windowed rows* (part D): a `Map` that does not pass `windowStart` gives a row that is no longer windowed, and a `Group` whose keys lack `windowStart` gives data rows; both are accepted, so the counterexample to Theorems 5.3 and 7.1 type-checks in the engine. Which shipped definitions the typing rule P6 would refuse is not measured; the steps from signals to components of the health model are the likely ones.
- *Retype policies* (part D): parsed, printed and carried into the typed plan, read nowhere at run time; a collision is decided by Def. 6.4. The restatement of Theorem 5.5 matches the engine; removing the clause from the language is a small change, not done.
- *A window with a loss or partial mark* is no longer filled, and a mark that arrives later takes the fill back; the time-to-live is at most `within` less one window; a fill of a property that an authored source feeds is refused at activation. In the engine's working tree, not committed when this was written. When it is committed, the difference named in the status paragraph of Def. 7.14 to 7.16 in Ch. 7 goes.
- *`e(k)`* as the later of `since(k)` and the end of the newest record is built in engine and reference, with tests.

**Checked again on the text of 2026-11-12, 08:57** (after later changes to Def. 7.13 to 7.16 and Law 7.5). Everything in the table above that is marked applied holds. Three points are new; each is for the chapter owner of Ch. 7.
- *"A covered window is therefore closed" (Def. 7.14) does not follow from the definition.* Covered is `b + L ≤ progress(σ)`, closed is `b + L ≤ λ(φ)` (Def. 7.6). The first implies the second only if logical time is never behind the progress of a source, `progress(σ) ≤ λ(φ)` at every advance, and the chapter does not say so. The engine does not rely on it: it takes the smaller of the frontier and the progress ("a window is filled only if it ends at or before both"). Proposed: define a window as covered iff it is closed and `b + L ≤ progress(σ)` for every source that feeds the property, and drop "therefore".
- *Law 7.5 item 3, case (i): a window without a value stays without one.* "Keeps none under further combination" is said for case (ii) only. Under a combine rule that is a monoid, a stored window can still be without a value: two different rows under one id are a conflict by premise B1. The as-if value then has the same conflict, so the merged window has no value under every rule. Proposed: move the sentence out of (ii) and say it for both cases.
- *Law 7.5 item 3, case (i), `last` with its order:* the greater of two pairs is a commutative operation only if two pairs with equal order and different values are ordered too. Def. 7.8 orders samples by `(τ, i)`, whose input id makes the pair unique; an order given by an expression of the definition (`last(value, order)`) can tie. Proposed: "the pair with the greater order, and of two with equal order the one whose value has the greater canonical encoding (Def. 1.4), compared as byte strings". The engine breaks such ties by the greater text, which is the same rule for text values.

Not changed by these: the statements derived, the counts, and the verdicts on the premises in part E.

### B. The merge rule derived from Theorem 6.1 (the fuller rule)

Law 7.5 item 3 says how stored windows combine. D37 says the engine does not re-key a group. The rule that covers both follows from Theorem 6.1 and Proposition 1.8; it is written here as a proposed statement for Ch. 6. It is not in a chapter and is not counted.

**Proposed Proposition 6.9 (an identity merge is a change of rows).** Let `X` be the integrated rows of a projection behind its identity expressions, with every entity reference written as the own id of its key, and let `ρ_S` replace every own id by its representative under the links of the input state `S` (Proposition 1.8). The batch result is `Q(ρ_S(X))`, where `Q` is the composition of the operators behind the identity expressions. Let a link merge `e₂` into `e₁`, both representatives in `S`, and let `τ` replace `e₂` by `e₁`.
- (a) The batch result after the link is `Q(τ(ρ_S(X)))`. The change of the output is `Q^Δ` of the change of rows `τ(Y) − Y`, where `Y` holds the rows of `ρ_S(X)` that mention `e₂`.
- (b) For a `Group`, or a `Rollup` over windows that are still open or revisable, keyed by the entity: the rows of `e₁` and of `e₂` in the output are replaced by one row, whose aggregates are those of the union of the two groups. It can be computed from the operator's own state whenever that state determines the aggregate of a union: a multiset of rows, or an accumulator in a commutative monoid. Every aggregate of the kernel is of this kind.
- (c) At a sink the fragments of `e₂` become fragments of `e₁`, the id of a relationship is computed again from the representatives of its endpoints (Def. 5.9), and Def. 6.4 decides the element.
- (d) For windows of which only the stored values are held, Law 7.5 item 3 (i) and the equal-values half of (ii) give the value of (a). Case (ii) with different values is the one case in which the held state does not determine (a).
- (e) For a `Threshold` whose key contains the entity, Def. 7.10 applies as for every change of structure: inside the retained range the windows of the two keys are combined and the fold is recomputed from the carried state of the key that survives; the carried state of the other key is dropped.

*Proof.* (a) Both ids are representatives and the link joins their classes, so the representative after the link is `τ` after the representative before it (Proposition 1.8): `ρ_{S'} = τ ∘ ρ_S`. `τ` fixes every row that does not mention `e₂`, so the integrated input changes by `τ(Y) − Y`, and Theorem 6.1 gives the change of the output. (b) The rows of key `e₂` become rows of key `e₁`, so the group of `e₁` afterwards is the union of the two groups; this is the per-key case of Theorem 6.1. (c) Emits are linear and key their fragments by the representative; `rid` is a function of the endpoint ids. (d) Theorem 7.2. (e) Def. 7.10. ∎

*What follows.* D37 is a deviation of the engine and not a limit of the theory: by (b) a group can always be re-keyed from its own state. The rule "rows that reach one window from groups formed under two ids combine by the property's rule" of Law 7.5 is what (b) gives when the groups are not re-keyed and the rule is a monoid; with re-keying it is not needed. A row held under another key that carries `e₂` in a column (in the index of a `Join`) has to be rewritten as well. What cannot be recovered is only what is no longer held: case (d). The survivor's carried state in (e) is preferred over the other's; which id survives does not depend on arrival order (Proposition 1.8), so the result does not either.

### C. Statements derived in the third pass

Ch. 1 had never been read by a review of the text, so Theorems 1.1 to 1.6 were derived before their proofs were read. The others had been read once in the first pass and were derived again from the definitions.

| Statement | Result | Differences |
|---|---|---|
| Theorem 1.1 (canonical equality) | derived independently, same argument (the scheme's output parses back; one string per denoted value; induction on lists) | none. The derivation also needs Rule 1.2 (no JSON numbers) and the rejection of broken character codes, both in the text. Def. 1.1 reads a value as what is denoted and the examples as what is written (`integer(+007)`); the proof holds for both readings |
| Corollaries 1.2, 1.3 | follow | 1.2 holds for values; a property map needs a sort of its names first |
| Theorem 1.4 (the name is a function of the key) | derived independently, same argument | "two writers that bind the same key write the same entry" holds for the rule *allocate*; a concurrent *alias* of the same key would write another entry. Equality of keys is by domain name, so domain names must be unique |
| Theorem 1.5 (timeless binding) | derived independently, same argument | `resolve` also depends on which rule set is current |
| Theorem 1.6 (change of the rules) | derived independently, same argument | two old entries of different entities whose new keys coincide are the conflict case, which the proof covers only if entries are migrated one after another. "No entity id changes" is about the carried binding set; a replay from an empty set under the new rules gives other ids, which the chapter's limits mention |
| Theorems 2.3, 2.6 | follow as written | none |
| **Theorem 5.3 (role non-interference)** | **first sentence false as stated** | see D below |
| Proposition 5.4 (closed language) | an inspection of a finite list, not a derivation | none |
| **Theorem 5.5 (retype-stable id)** | **second sentence rests on a superseded definition** | see D below |
| Propositions 6.7, 6.8 | follow | 6.8 holds inside the revisable range; behind it the row is dropped (premise B2). The statement does not say so |
| **Theorem 7.1 (plane separation)** | **second sentence false as stated** | see D below |
| Theorem 7.2 (additivity) | follows | the conversion of a counter to increments is a function of the whole sample sequence, so a late sample changes the increment of its successor in another window; the theorem is about the function and stands |
| Theorem 7.3, 7.6 | follow | 7.3 does not need Theorem 5.3: any path is deterministic. 7.6 with retention: see A |
| Theorem 7.4 (bounded churn) | follows | the example in the proof sets "`below = above`", which Def. 7.10 forbids; it is not needed: alternate between a value at `above` and a value at `below` |
| Theorem 8.4 (class of a stack) | follows from 8.1 and 8.3 | none |
| Theorem 10.1 (idempotent delivery) | follows | the input id `i` of a sample (Def. 7.2) must be the delivery key, not the ingestion sequence, or a redelivered record counts twice in telemetry |
| **Theorem 10.3 (reconciliation)** | **a hypothesis is missing** | see D below |
| Theorems 3.1 to 3.5, 4.1 to 4.4, 4.6 to 4.9, 2.1, 2.4, 5.1, 6.4 to 6.6, 8.5, 8.6, 10.2, 10.4, 10.6, 11.1 to 11.5, 11.7, 11.8, 12.1, 13.3, 13.4 | derived again from the definitions; each follows | 10.4 lists the two ways a source withdraws; elements also go by retention (`Retain`) and by loss of support upstream, which is not a withdrawal by a source |

### D. Proposed restatements for the four statements with a pending restatement

**Theorem 5.3 and Theorem 7.1: one cause.** A row that a `Series` produces carries data fields (the keys of its input row, `windowStart`, `windowEnd`), and it exists exactly when a closed window exists. The typing rules treat those fields as data and let them reach a structural position.

*Counterexample.* `let w = match master (p: Pod) |> series master p.warnings within 1h; let seen = w |> select { p: p } |> distinct; emit update entity Pod from seen identity p set { noisy: true };` Every rule of Ch. 5 (5.2) accepts it: the `Map` keeps a data field, `Distinct` sees data only, the emit sets a data property, and P4 holds. The property `noisy` is set exactly while the pod has a closed window of `warnings` in the last hour. So graph data depend on telemetry without a `Threshold` (against the first sentence of 5.3), a closed-window record creates a revision without a crossing operator (against the second sentence of 7.1), and the bound of Theorem 7.4 does not apply to it. The engine's checker accepts the same shape: a `Map` that drops `windowStart` gives a row that is no longer windowed, and a `Group` over windowed rows without `windowStart` among its keys gives data rows.

*Two ways out; the project chooses.*
- *Restate.* Theorem 5.3: "the graph data of every commit is a function of the data fields of the inputs, of the set of closed windows that exist (their keys and times, not their values), and of the outputs of `Threshold` operators". Theorem 7.1: "a closed-window record creates a revision only if a projection that consumes it changes graph data: through a `Threshold`, or through a rule that reads the existence or the time of windows". The claims "only through a threshold" in the README and in Ch. 0 then need the same qualification, and the bound on churn holds only for the threshold path.
- *Close the hole with a typing rule (recommended, because the headline claim then holds as written).* **P6 (windowed rows).** A row is *windowed* if it comes from a `Series`, a `Window` or a `Rollup`, and stays windowed through `Map` and `Filter` whatever fields they keep. `Rollup` and `Threshold` take a windowed row; the output of a `Threshold` is not windowed. `Distinct`, `Group` without `windowStart` among its keys, `Join`, `Retain`, `Traverse`, a `Fixpoint` body, every create emit and every set of a data property take a row that is not windowed. With P6 the proof of Theorem 5.3 goes through for its first sentence, and Theorem 7.1 holds as stated. The engine needs the rule in its checker, and the shipped definitions have to be checked against it.

**Theorem 5.5.** Proposed: "`rid(o, κ_r, s, t)` does not depend on the relationship type, so a retype keeps the id. If the retyped relationship meets another one on the same `(source, target, type)`, Def. 6.4 decides: the one with the smaller id is present, the other is absent and reported, and `U` holds after every commit." Def. 5.10 then loses its retype policies: `reject` fails the event, which Def. 6.4 and Def. 5.19 exclude, and `keepExisting` and `takeNew` choose by which relationship was there first, which is arrival order. The engine parses and prints the policy and never applies it.

**Theorem 10.3.** Proposed addition to the hypothesis: "and the engine holds no contribution of that source to the scope that it received from a position behind the scan's snapshot". *Counterexample to the present form:* a list at one version returns `{a}`; the same source reports the creation of `b` at a later version, and the engine processes that record before it applies the list; `b` is not in the list and is withdrawn, although it exists. With the list-then-watch order of Theorem 10.5 the hypothesis holds, because the watch starts after the list.

### E. The nine statements under named premises: discharge the premise or keep it

A premise can be *discharged on the model* (made a definition or a theorem of the paper, leaving a conformance test for the program), *discharged by a check at run time* (the program refuses what would break it, as the staged check of Theorem 8.5 does for PutGet), or it *must stay*.

| Statement | Premise | Verdict | How, or why not |
|---|---|---|---|
| Theorem 5.2 (type soundness) | KS: every builtin and aggregate returns its declared kind or faults | **discharge on the model** | the kernel has about 25 builtins and 10 aggregates. Give each its denotation in a table of the paper; KS is then read off the table, and what remains is that the program implements the table (test T-SOUND), which is conformance and not a premise of the theorem |
| Theorem 5.6 (lossless syntax), Proposition 4.11 (two views) | RT: `parse(print(G)) = G` on canonical graphs | **discharge by a run-time check** | a commit already prints and parses every changed module. Let it compare the digest of the parsed graph with the digest of the draft graph and refuse on a difference. Every committed module then satisfies RT by the check. Proving RT on the model would need the whole grammar and a printer in the paper |
| Proposition 5.7 (refactoring) | R0 full replay | discharge on the model | make it part of the statement: the configuration edit of the engine is defined as a full replay (Ch. 9) |
| | R1 closed selection, R2 copied ports | **discharge on the model** | they are rejection rules and construction steps of Def. 5.12; say there that a refactoring that breaks them is not accepted |
| | R5 one output ontology | discharge on the model | add to Def. 5.12 that `moveRule` is refused across output ontologies |
| | R3 disjoint keys, R4 no reader of what moves | **discharge by a static rule or by a run-time check** | static and sufficient: the moved and the remaining sinks share no identity domain, no relationship key rule and no relationship type, and no graph matches a type that a moved sink emits. Or at run time: compare `Out` before and after the commit and report "preserved" only if they are equal. Today neither exists, so both stay premises |
| Theorem 10.5 (list and watch) | KW1 to KW3 | **must stay** | properties of an outside system. One part can go for clusters whose versions are ordered (Kubernetes 1.35 and later for the built-in types): the order of events of one object can then be taken from the versions and need not be assumed |
| Proposition 10.7 (live run and replay) | LV1 the recording is the consumed sequence | discharge by construction | let the engine consume what was written (one journal that is both the recording and the input), so the two cannot differ |
| | LV2 an envelope gives the same inputs in both runs | **must stay**, as an audit | it says that adapters and the loop read no clock, no random source and no outside state: a property of program code, like Proposition 5.4, checked by inspection and by test T-LIVE |
| | LV3 same horizon | discharge by construction | record the options of the live run with its start (the journal already holds them) and let the replay take them from there |
| | LV4 = B1 to B4 | mixed | B1 can become a checker rule (T-B1). B2 is a definition. B3 is Proposition 1.8. B4 holds by construction where an advance is always a step of its own, which set B states; for a link and the rows around it, it stays (D37) |
| Proposition 10.8 (live resume) | LR1 journal first, whole entries | discharge relative to the store | whole entries follow from store property S1 if an entry is one item or one batch of one partition, and for a body in several items if the item that names them is written last, as for a seal. "First" follows by construction if the engine consumes from the journal. Durability under a kill is a property of the store and must be added to S1 to S5 |
| | LR2 the source resumes after a position | **must stay** | a property of the outside source (Event Hubs), with the bound of 20,000 remembered keys as a condition on it |
| | LR3, LR4 | as J2, J3 and B1 to B4 | |
| Proposition 11.9 (restart) | J1 journal before commit, in order | discharge by construction and store | as LR1 |
| | J2 nothing outside the journal influences the state | **must stay**, partly checked | the comparison of every recomputed commit with the stored one checks it for what reached a commit; state that never reached one is not compared. An audit as for LV2 |
| | J3 the restart cuts the input as the journal records | discharge by construction | the cuts are journaled and the replay follows them; a test compares the cuts |
| Proposition 11.10 (seal) | SL1 the loaded state equals the replayed state | **must stay**, checkable at a cost | a digest over every field of the state, taken by walking the state type and not by a hand-written list, turns a forgotten field into a failed check; a restart that also replays the journal (the verify mode) checks SL1 for that run at the cost of a full replay. Test T-SEAL |

In short: KS, RT, R0 to R5, LV1, LV3, J1 and J3 can be discharged, on the model or by a check; KW1 to KW3, LR2, LV2, J2 and SL1 must stay, the last three as audits or checks of program code.

### F. README, Ch. 0 and Ch. 1 read as a reader with mathematics and little engineering

These three files have had the style pass. Listed is every sentence that such a reader cannot parse without a term from engineering. None blocks the argument.

README.
- "A measurement can change structure only through a threshold with memory": "memory" is not explained here (Ch. 0 explains it by the flickering graph).
- "replay determinism and independence of batching": "replay" is used before it is said what a replay is (computing again from the recorded inputs).
- "Two recordings of one small cluster with a demonstration shop": "cluster" is defined in Ch. 0, not here; likewise "master graph" in the figure, "component", "workload", "estate".
- "in 99 of 100 cases": clear. "with a file database": clear enough. "a saved snapshot of the state": clear.
- "A cloud database was designed for and run only on a local emulator": "emulator" (a local program that imitates the cloud database).
- "Nothing was measured from a source to the screen on a live stream": "live stream".
- "listing conflicts with each version" reads as if listing were in conflict with a version; meant is "a list of the conflicts, given with each version".
- Not a matter of the reader: "9 under a named premise" and the two table lines in Ch. 0 below.

Ch. 0.
- 0.1: "the cluster belongs to a subscription in Azure": "subscription" is not explained (the unit under which a customer rents).
- 0.2, figure: "typed rows" appears in the figure and nowhere in the text of the chapter.
- Def. 0.1: "cloud resources": a "resource" (anything rented: a machine, a network, a database) is not explained.
- Def. 0.3: "the position in that source's stream": "stream" (the sequence of its records).
- Fact 0.1: "it is not strongly consistent": a term of distributed systems; the half sentence after it says what follows but not what it means (an answer need not reflect one moment).
- Fact 0.2: "Event records are kept for one hour": an "event" in the sense of Kubernetes (a note about an object, such as a warning) is not explained.
- Fact 0.4: "The older log format retires": which logs, and of what, is not said before.
- After the facts: "An engine must treat what it receives as evidence": "engine" is used for the first time and never defined (the program that builds the model); R-meta then says "only the engine is program code".
- 0.1, last bullet, and claim C1: "a change of the rules can be replayed", "stable under replay": "replay" undefined, as in the README.
- 0.6: "that is always live": not explained (it follows the estate as it changes).
- Claim C5: "atomic versions": "atomic" (all of a change or none of it).
- Claim C7: "type soundness": a term of programming languages (a definition that passes the check produces only data of the declared types). Also the premise is about the functions of the core, not about "the checking program".
- Claim C10: "lineage soundness relative to a cited theorem" is out of date: Theorem 9.7 is proved without a cited premise.
- 0.7: "one writer per graph": "writer" (a process that commits changes).

Ch. 1.
- Rule 1.2: "a floating-point number with 53 bits of precision": say "a number kept with about 16 decimal digits".
- Def. 1.5: `hex(...)` is not explained (the writing of the bits in base 16); "byte string" is used from the claim on without a line on what a byte is.
- Theorem 1.4: "two replays", "two writers that bind the same key at the same time".
- Limits after Corollary 1.3: "use 64 bits", "the prototype's encoder has not been compared with an independent implementation of the scheme".
- Proposition 1.8, proof: "Cutting the input into batches does not reorder it (Ch. 6, Theorem 6.2)": a forward reference that the reader has to take on trust; the invariant already gives the claim without it, because its right side depends only on which keys and links were processed.

### F2. Ch. 3 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version of the chapter, statement by statement. No theorem statement changed status. Sent to the paper session as a numbered list; the numbers below are the same.

*Meaning drifted in the rewrite.*
1. Def. 3.4: "states equal by `≈`, property by property" drops the type, the id and the two ends; the earlier text compared states component by component.
2. Def. 3.5: the order of the sorted pairs is gone (it was the order of UTF-16 code units, as the canonical JSON scheme sorts names). Entity and relationship ids are not restricted to the alphabet of Rule 1.1, so the order must be named; Theorem 3.3 rests on it.
3. Def. 3.6: "effective iff `b ≠ a`" lost "by `≈`".
4. Def. 3.9 says "`apply(E, G_h)` is well-formed", which includes WF4, and the paragraph after it says the rule enforces WF1 to WF3 only. The first sentence should say WF1 to WF3. (The contradiction came in with the gate 1a pass, not with the style pass.)
5. §3.7: "the marker for measurements" for the telemetry watermark of Def. 6.8; "marker" already names loss marks and forwarder markers.

*Statements with a gap that the new proofs expose.*
6. Theorem 3.6: the scoped id hashes the customer, the graph name and the scope. If a graph name is unique only within a space (Def. 3.11), two spaces of one customer with a graph of one name get the same scoped ids. Either names are unique per customer, or the space enters the hash.
7. Theorem 3.5, proof: "the version of the definitions only moves forward" is an unstated premise. It holds because the definitions are themselves a graph with a log (Ch. 2) and a commit pins the version in force; say so, or add it to F1.
8. Theorem 3.3: "decided in time proportional to the lengths of the written forms" holds once the written forms exist; producing them sorts the keys.
9. Def. 3.14: "the others in their order" names no order. The topological order of Theorem 3.7 is not unique.
10. Def. 3.14: "the identity key from which its id was computed" fails for an id introduced by an alias (Ch. 1, Def. 1.10); "the key by which it was named" covers both.
11. Def. 3.14, example: an endpoint known only by an address; the measured paragraph and Ch. 10 have pods named by namespace and name. Use a pod that Kubernetes never listed.

*Used before it is defined, in this chapter or in Ch. 0 to 2 as rewritten.*
12. "ontology" and "release" (Def. 3.1): neither is defined in Ch. 0 or Ch. 1.
13. `𝓡`, relationship ids (Def. 3.1): how they are formed is only in 3.5 (Ch. 5, Def. 5.9); point there at first use.
14. "commit" as a verb and "commit record" (Def. 3.8) before the commit rule (Def. 3.9).
15. "input" (Def. 3.8, the cause), "batches" (Def. 3.9), "replayed" and "the engine" (3.4): defined only in Ch. 6, or nowhere.
16. "kind of `k`" (Def. 3.6) reuses "kind", which Ch. 1 defines for values.
17. The opening says the chapter "points forward to Ch. 2"; Ch. 2 comes before it.

*Needs engineering background.*
18. 3.4: "system time", "application time", "the SQL standard of 2011", "periods": one line on what SQL is and what the two periods are.
19. Def. 3.12, example: "namespace" (a named group of objects in a Kubernetes cluster).
20. Def. 3.12: "a value that selects a part of `g`": how a value selects elements is not said; if only the written form of the scope matters here, say that and point to where selection is defined.
21. 3.4, table: "accepts an input for good" (durably: it will not be lost).
22. Measured paragraph: "replay", "warning events" (a Kubernetes event, a note about an object).
23. 3.7: "Versions ... decimal strings between programs": fine; "properties on 4,000 random cases" should carry its caveat here (the cases are drawn only among changes the prototype accepts, so a wrong refusal would not be seen), as the rule "limits where the claim is made" asks.

*One term, one meaning.*
24. Def. 3.13 "measurement plane"; Ch. 7, the glossary and Def. 0.7 say "telemetry plane". Choose one for the whole paper.
25. "What you can state at the end": "the graphs of one system" for a graph space.

### F3. Ch. 4 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version, statement by statement. No statement changes status; one proposal strengthens Theorem 4.1, and one proof of mine (Proposition 4.5, second pass) has a gap.

*Mathematics.*
1. **Theorem 4.1 holds without its hypotheses, and the remark after it is only true for that stronger form.** The proof reduces both bracketings to the same three conditions without using that `p then q` and `q then u` are defined. So the theorem holds in the strong form: "`(p then q) then u` is defined iff `p then (q then u)` is defined, and then they are equal." Under the present, weaker form (both inner compositions assumed defined), dropping joined guard-only edits does *not* break associativity: the cancelled keys are edited by `q`, so "`q then u` is defined" already gives the condition the dropped guard would have checked. The remark's example (`u` composes with `p then q` but not with `q`) needs `q then u` undefined, which the present hypothesis excludes. Proposed: state Theorem 4.1 in the strong form; the proof stands as written; the remark is then exact. Add that dropping the guard also breaks Theorem 4.2: the dropped composite applies to graphs on which `p` does not.
2. **Proposition 4.5, proof (written in the second pass): "the action determines the patch" is false as stated.** Two patches with the same edits and different versions act on graphs in the same way. The action determines the edit set; the versions are part of the patch. Proposed: "a patch is its two versions together with the partial injection `G ↦ apply(p, G)`; the action determines the edit set, and the versions are kept as the object part of the category". The rest of the proof goes through, hom-set by hom-set.
3. **Proposition 4.5, the groupoid:** the composition of two net forms can produce guard-only edits again (a key changed and changed back), so the groupoid's composition is `net(p then q)`. Say so.
4. **Def. 4.8: "materializable" should require that the facts of weight 1 form a graph**, each state under its own key (Def. 3.2, WF1), not only that they satisfy `U`. For `z = δ(p)` of a valid patch this holds by Theorem 4.8; for an arbitrary `z` handed to `toPatch` it does not.

*Meaning drifted in the rewrite.*
5. Def. 4.2: "the after-state of `p` on `k` equals the before-state of `q`" lost "by `≈`" (as Ch. 3, item 3).
6. After Theorem 4.4: the order of edits in a written patch is "by kind and then by id", with no order of ids named (as Ch. 3, item 2), and "kind" again for entity, relationship or ontology.

*Used before it is defined.*
7. Def. 4.10: "catalog", "kernel", "release", "refactoring"; Def. 4.12: "module", "canonical text", `print`, `parse`. Ch. 2 is not yet rewritten; until it is, one line each or a pointer.
8. Def. 4.3: "an inverse cause" is not defined.
9. After Theorem 4.6: "Z-sets", "integration and differentiation" (defined in Ch. 6).
10. Def. 4.12, example: `name: Text label;` uses syntax that the reader has not seen.

*Needs engineering background.*
11. §4.7: "The viewer", "The engine", "redo list": "engine" is still undefined in Ch. 0.
12. §4.7: the 4,000 random cases need their caveat here (drawn only among changes the prototype accepts, so a wrong refusal is not seen), as in Ch. 3, item 23.
13. §4.5 is clear. One phrase: "copies of one object" could say "copies of one piece of data kept on several computers".

*Notation.*
14. The running example writes a patch by its edit set alone (`(k, a, b)`, `ε((k, a, b)) = (k, a, a)`). Say once that a patch is written by its edits when the graph and versions are clear.

### F4. Ch. 5 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version. No statement changes status. Two definitions changed meaning in the rewrite without being listed, and two examples teach something the rest of the paper forbids.

*Meaning changed, not listed.*
1. Def. 5.8: the check on a source changed from "its declared contract digest equals the pinned digest of the row contract" to "delivers rows of the declared shape". This follows the prototype (Ch. 2 records that adapters declare a shape), but it is a weaker check and belongs in the table "Statements whose meaning changed".
2. Def. 5.1 gained the projection's *description* (text for people, not read by evaluation). New, harmless; list it with the other presentation data of Ch. 2.

*Conflicts with rulings elsewhere.*
3. Def. 5.10: "`requireEqual` fails on unequal values" contradicts Def. 5.19 and Def. 6.4, under which nothing fails the event. And a `Union` of plain rows has no key, so "unequal values" has no meaning for it (the prototype refuses every policy except `preserveEvidence` for that reason). Proposed: the three other policies are defined only for rows with a key and are not built; under `requireEqual` two different values for one key give no value and a reported conflict, as Def. 6.4. Theorem 6.5 admits `Union(requireEqual)` in a `Fixpoint` body; there it is set union, which should be said.
4. Example after Def. 5.9 (a `Link` "under a policy that the address of a running pod identifies it") and the sentence before Theorem 1.7 ("the pod was seen under its address"): a merge is permanent, and an address is reused by later pods. Ch. 10 (10.4) says an address is not a timeless identity and keys an address-only endpoint by cluster, address and a time-bounded lease. With the example's policy, the next pod that gets the address is merged into the first one forever. Use the link of Ch. 10 instead (pod uid and pod name), or key the address with its lease.
5. Fill paragraph (5.5): "A roll-up key is in no data when every member is" is the older wording; Def. 7.16 now says "every member that has a record, stored, filled or marked no data".
6. "What follows from the lowering" (5.8): "the crossing from telemetry to data through `Threshold` only" inherits the open gap of Theorem 5.3; say "the value of a measurement reaches data only through `Threshold`".

*Small inaccuracies.*
7. §5.4 opens with "Five properties"; it holds six numbered statements (5.1, 5.2, 5.3, 5.4, 5.5, 5.6).
8. Def. 5.11: `Series` also yields filled records and no-data records when the definition declares `fill` or `ttl` (Def. 7.15, 7.16); one clause.
9. Def. 5.15: "one malformed row must not stop the rows around it (Ch. 6, Theorem 6.2(a))": the principle is Def. 5.19 (a fault is local to its row); 6.2(a) is about the transport.
10. The running example (Def. 5.2) computes the dependencies with two `Match`es and two `Join`s; the worked example (5.6) does it with one `Match` of three edges. Both are right; say that they are two ways to write the same thing, or use one.

*One term, one meaning.*
11. "measurement" in the prose and "telemetry" in the rules, Def. 5.11, Def. 5.18 and the `lift` table (Ch. 3, item 24).
12. "threshold" for the policy's minimum confidence of a `Link` (after Def. 5.9) and for the operator `Threshold`. Say "the policy's minimum confidence".
13. "canonicalizer" (Def. 5.19) and "normalization" (premise KS) for one thing; Ch. 1 says normal form.
14. "the planner" and "after planning" (5.8) for what the text has just defined as *lowering*.

*Used before it is defined, or never defined.*
15. "kernel" (Def. 5.2), "catalog", "module", "release", "ontology", "output ontology": Ch. 2 is not rewritten yet; pointers exist for some.
16. `gauge`, `delta`, `cumulative` (Def. 5.4): one line each (a reading at a moment; an amount per period; a running total). Def. 5.11 already says "a running total" once.
17. "hysteresis" (worked example, `lift` table): two thresholds with a band between them, so that a value inside the band changes nothing. The example after Def. 5.11 shows it without the word.
18. "reset-aware" (Def. 5.11) for what Def. 5.6 says as "starting again when the value drops".
19. "signed rows" (5.6, `Distinct`), "Z-set of fragments" (proof of 5.7): defined in Ch. 6.
20. "a `readonly` rule" (start of the `lift` statement): Ch. 8.
21. "made active" (Def. 5.8, Prop. 5.8): when definitions take effect; one line, or a pointer to Ch. 9.

*Needs engineering background.*
22. "PG-Schema", "Cypher", "GQL" (5.6): one line, languages for describing and querying graph databases.
23. "append-only scan" (Def. 5.11).
24. "a typed error" (5.9): an error that names what is wrong and where.

### F5. Ch. 6 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version. No statement changes status. Four points are inconsistencies inside the chapter that the rewrite makes visible; two are corrections from the third pass that are still missing.

*Inside the chapter.*
1. Def. 6.7 ends: "their batch semantics is the function of the set of closed windows per key, in `windowStart` order, together with the frontier". For `Threshold` the proof of Theorem 6.1 now says the batch semantics is a fold over the recorded sequence, with the value each window had when it left the retained range. The two agree only if Def. 6.7 says "the set of closed windows per key, each with the value it had when it left the retained range". Proposed: that wording.
2. Theorem 6.2, last sentence of the proof: "Without them … and (a) fails." Part (a) holds without the premises (it is about the transport); the premises are needed for (b). Proposed: "(b) fails".
3. Premise B4 names `Threshold` and a link with the rows around it. The proof of Theorem 6.1 now also rests on B4 for a `Series` with a fill or a time-to-live (`since(k)` is set at advances). Add it to B4.
4. Remark after Theorem 6.5: "the limits only turn non-termination into an atomic failure" contradicts Def. 5.19 and 6.6 (the bound of a `Fixpoint` cuts the result short and does not fail; `maxFacts` is recorded and not enforced). Proposed: "the bound only cuts the result short".

*Corrections from the third pass still missing.*
5. Proposition 6.8 holds inside the revisable range; a correction for a window behind it is dropped and counted (premise B2). Add the range to the statement. The example after it also needs "and no later run of three exists".
6. Def. 6.3, `Union` "per its conflict policy (Def. 5.10)": a `Union` of rows has no key, and the only built policy is `preserveEvidence`, which is the sum of the inputs (see F4, item 3). Proposed: "`Union`: the sum of its inputs".

*Out of date.*
7. §6.6: "Conflicts … are not yet listed with each version." The prototype now sends the conflicts of a commit right after it, when they appear, change or end. Remove the sentence or state what is still missing.
8. §6.4, table: the state of `Series` with a fill or a time-to-live (one instant per present element, the progress per source) and of `Threshold` (the windows of the retained range and one carried state per key, Def. 7.10) are missing from the column "State".

*Notation.*
9. `π` now has four meanings: presence in a field type (Ch. 5), carry policy (Ch. 9), the set of rows `π(λ)` in the proof of Theorem 6.1, and the relational projection in 6.5 (`R = seed ∪ π(R ⋈ E)`). Proposed: write `live(λ)` in the proof (as Proposition 5.9 does) and the recursion of 6.5 in words; `⋈` also needs a word.

*Used before it is defined, or never defined.*
10. "plan" (proof of Theorem 6.1, "by induction on the plan"): the network of operators that is evaluated, after `lift` is replaced.
11. "Z-set": used in the title of Def. 6.2, defined in the example after it; put the definition in the definition.
12. "snapshot" (Def. 6.1); "sealed", "consumer", "subscription", "follower", "handoff" (Theorem 6.4): Ch. 11 terms; one line or a pointer.
13. "stratum" (Theorem 6.6 (a)): a part of a recursive definition that is evaluated as a unit.
14. "semi-naive iteration" (table of 6.4, example 6.5): iteration that joins only the new rows of the last round; and the prototype does not use it.
15. "the engine" throughout: still undefined in Ch. 0.

*Needs engineering background.*
16. "equijoin", "selection" (Def. 6.3): a join on equal values; keeping the rows that satisfy a condition.
17. "timely dataflow", "source partitions", "consistent snapshots in distributed systems" (Def. 6.6); Ch. 3 says "input streams" for what is here "source partitions".
18. "transport chunks" (Theorem 6.2 (a)).
19. §6.4: "hash arrangement", "entity handle", "breadth-first reach", "cursor"; and after the table "laid out by column", "interpreter", "fused", "pipeline".

*Examples.*
20. The running example and the example after Def. 6.2 have the observer "correct" a call count by retracting it. No source of Ch. 0 sends retractions of counts; Kubernetes replaces an object by a new version, which is a retraction of the old contribution and an insertion of the new (Ch. 10). Either use that, or say the correction is a revision of a window inside the late horizon.
21. The example after Def. 6.4 is followed by the bullet "What batch invariance requires", which belongs to the definition above the example; move the example after the bullet.

### F6. Ch. 7 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version and with the definitions the prototype ships. Two statements are found false as stated and are marked "restatement pending" (Theorem 7.4, Proposition 7.7); one proof leans on the open gap of Theorem 5.3 without needing it.

*Mathematics.*
1. **Theorem 7.4 and Def. 7.10 miss the edits at the start and end of a key's row.** Def. 7.10 says "an edit of graph data is emitted only at a toggle". But a `Threshold` gives a key a row only while the key has a window in the look-back (Def. 6.7): the row appears with its state when the first window is read, and goes when the last window leaves the look-back. Both change the output, and downstream they create and remove elements: the health model emits each `Signal` entity from a `Threshold` output, so a signal that starts or stops having windows creates or removes a `Signal`. *Counterexample to Theorem 7.4:* `n_in = n_out = 3`, `N = 2` windows that start an episode. The bound is `⌊2/3⌋ = 0`; the operator emits the key's row once. The row can also go and come back inside one episode, when two consecutive windows are almost `within` apart, so that neither is closed and in the look-back for a while. *Proposed:* "it emits at most `⌊N/m⌋` toggles, plus one change each time the key's row appears or goes. The row goes only after a gap of more than `within − w − L` between two consecutive windows of the key, so over `N` windows without such a gap it appears at most once and goes at most once." Def. 7.10: "an edit is emitted at a toggle, and when the key's row appears or goes".
2. **Proposition 7.7 (c) is false as stated, for the same reason, and its proof uses the false first sentence of Theorem 5.3.** "While the data inputs of the stack do not change, a revision of `health` is caused by a toggle of a `Threshold`": a signal whose windows start or stop changes `health` without a toggle (the component goes from `Unknown` to a state, or back). The proof cites "by Theorem 5.3 the graph data … is a function of the data fields … and of the outputs of its `Threshold` operators" (first sentence, false as stated). *Proposed (c):* "while the data inputs do not change, a revision of `health` is caused by a toggle of a `Threshold`, or by a `Threshold` row that appears or goes; over `N` windows a signal causes at most `⌊N/m⌋ + ⌊N/m'⌋` revisions plus two per appearance of each of its two rows". The proof then needs only that the stack's graph data derive from measurements through `Threshold` outputs alone, which holds for the shipped definitions (each `Signal` and each state comes from a `Threshold` output) and in general under the typing rule P6.
3. **Theorem 7.3 does not need Theorem 5.3, but its proof uses it.** "By Theorem 5.3 no other path leads from telemetry to structure" is false as stated (existence of windows). The theorem holds without it: whatever path a closed-window record takes, it is a recorded input, and Theorem 6.3 applies. Proposed: drop the sentence and the reference in the basis line.
4. **"Rolling up is therefore a linear operator in the sense of Ch. 6"** (after Theorem 7.2) contradicts Ch. 6, where `Rollup` is a per-key function and not linear (a changed aggregate retracts one row and inserts another). Theorem 7.2 says the aggregate is additive over samples. Proposed: "the aggregate is additive over samples; as an operator on rows `Rollup` is a per-key function (Ch. 6)".
5. **Def. 7.10, episode bullet:** "the state is a function of the set of closed windows and the frontier" against the next bullet, "a function of the recorded input sequence". Same as F5, item 1: "the set of closed windows, each with the value it had when it left the retained range".
6. **"The late horizon" is used and not defined.** Premise B2, Def. 7.13 ("the smaller of the late horizon and `ρ`") and Def. 7.10 rely on it; §7.6 says "Def. 7.6 has no horizon of its own". Proposed: add to Def. 7.6 a horizon `H`: a closed window stays revisable until the frontier passes `b + L + H`, and a later row for it is dropped and counted.
7. Still open from the third pass: "a covered window is therefore closed" (Def. 7.14) needs either the premise that logical time is never behind a source's progress or the definition "covered iff closed and …"; Law 7.5 (i) needs "a window without a value stays without one" for monoid rules too, and `last` with an order needs a tie-break.

*One term, one meaning.*
8. Def. 7.12: "Unknown means 'no data'". *No data* is now the defined state of Def. 7.16 (asserted, with a cause). `Unknown` is *unknown by omission* in the terms of 7.5. Proposed: "Unknown means that no reached signal has a threshold state: unknown by omission, not no data in the sense of Def. 7.16".
9. `D` is the depth bound (Def. 7.12, Prop. 7.8) and the time-to-live (Def. 7.16), besides `D` of Ch. 6 and `D_n` of Ch. 4. Write the time-to-live as `T`, as its syntax `ttl T` does.
10. The chapter is titled "Measurements"; its definitions say "telemetry". Choose one (Ch. 3, item 24).
11. Def. 7.1 declares a series by `(kind, …, widths, …)` with four kinds; Ch. 2 and the syntax declare `(temporality, combine, period)` with one period. The mapping "named combinations" is not total (a `delta` property with combine `max` has no kind), and Def. 7.8 defines the aggregate per kind. Proposed: define the aggregate of Def. 7.8 by temporality and combine rule, and keep the four kinds as names.

*Qualifications that the gap of Theorem 5.3 asks for, still missing.*
12. §7.1, third bullet: "Telemetry influences structure only through crossing operators".
13. §7.4, first paragraph: "telemetry reaches structure only through a crossing operator (Theorem 5.3)".

*Out of date.*
14. §7.6: "the prototype still fills a window that carries a loss or partial mark". The engine now holds no fill on such a window and takes a fill back when a mark arrives later; refuses a fill on a property that an authored source feeds; and counts no data per roll-up member as the reference does.

*Needs engineering background.*
15. §7.4 is dense with terms of Azure and Kubernetes: load balancer, virtual network, Event Hubs namespace, resource group, node, `providerID`, virtual machine scale set, system pool, Activity Log, and the warning reasons (`FailedScheduling`, `FailedMount`, `NetworkNotReady`, `BackOff`). One short paragraph of plain glosses at the start of 7.4 would carry them.
16. "at-least-once delivery" (Def. 7.2), "aligned to the epoch" (Def. 7.5), "scaled integers" (Rule 7.1), "ring", "live store", "query-through", "external store of measurements" (figure, Theorem 7.3 and its remark), "heartbeat envelopes from the forwarder", "state seal" (7.5).
17. "measured by others in the project" (limit 7): the style rule asks not to say who measured; give the result and point to Ch. 14.

### F7. Check of the applied lists F2 to F5, and one inconsistency they expose (2026-11-12)

Spot-checked in the chapters: Def. 3.4, 3.5, 3.6 and 3.9; Theorem 4.1 in its strong form with the remark and Theorem 4.2; the order of edits after Theorem 4.4; Proposition 4.5 with versions as objects and composition `net(p then q)`; Def. 5.10; Def. 6.7; Proposition 6.8; the last sentence of the proof of Theorem 6.2; the identity examples of Ch. 1. Each reads as proposed. The paper session's choice that a *measurement* is one value and *telemetry* the series of them (Def. 0.7) resolves F4 item 11 differently from the proposal and is consistent.

*An inconsistency between Ch. 1 and Ch. 10 that the new examples expose.* Ch. 1 now aliases a pod's name key to the id of its uid (Def. 1.10, *alias*) and says "an alias is permanent, and a pod name can be reused". Ch. 10 (10.4) says a name key "rebinds when a pod of a StatefulSet is recreated under a new UID", and that "a flow is attributed to the pod that held the name at that position of the log". Under Def. 1.10 and Theorem 1.5 a binding never changes, so a name key cannot rebind: after the pod is recreated, every flow that names it resolves to the old pod. Either name keys are not identity keys in the sense of Ch. 1 (they are resolved through a lookup that is graph data and changes with the log, not through the binding set), or the name domain carries the incarnation (the uid when known, or the version at which the name was bound), or Ch. 1 gains bindings that hold for an interval. The first matches what Ch. 10 describes and keeps Ch. 1 as it is; it should be said in Ch. 1, after the alias example, and in Ch. 10, where "rebinds" is used.

*Counts.* README and Ch. 0 say "92 are proved in full"; after F6 the inventory has 90 and 4 pending (5.3, 7.1, 7.4, 7.7).

### F8. Ch. 8 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version and with what the prototype raises. No statement is false. Two descriptions of the prototype are wrong (items 1, 6), and the removal of the rule text took away the cause of the running example's delete (item 2).

*Mathematics and the prototype.*
1. **`AmbiguousKey` is not built.** Def. 8.4 and Theorem 8.2 (c) say an edit of an element whose identity has several keys "is rejected `AmbiguousKey`", and 8.5 says the reasons not asserted by the tests "are built". The engine raises no `AmbiguousKey`: an update or a delete addresses the master entity by its id, and no step counts the keys bound to it. The theorem stands (it is about the lens on its accepted domain); the prototype does not enforce that domain, and only the staged check of Theorem 8.5 stands between such an edit and the master. No shipped definition binds two keys to one capability (the one equivalence declared is between pod uid and pod name), so the case is not reached. *Proposed:* "is to be rejected `AmbiguousKey`; the prototype does not yet check the number of keys", and in 8.5 name the reasons that are built and not asserted (`TelemetryNotEditable`, `NotAccepted`, `KeyMismatch`, `BadEdit`) instead of "the other reasons". Test T-AMBKEY (G). Engine-core will build the check with the property tests of the proof pass; until it is committed the paper says that it is not built.
2. **The delete of the running example has no stated cause, and it is not the backward step of Theorem 8.2 (a).** The committed version showed the rule with `on delete set { status: "retired" }`; the restyled one says "deleting a capability sets its status to `retired`" and nothing says why. The engine carries a delete back only through such a clause and refuses any other delete `NotAccepted`. So the removal of a source row, the `put` of Theorem 8.2 (a) and (b) on a delete, is never run, and the capability lens is not a composition of the steps of 8.2: on a delete its `put` changes a field of the complement. Its PutGet on deletes holds because the values set make the row fail the filter, a condition on the rule that no check enforces and the staged check catches. *Proposed:* one sentence in 8.2 that a lens rule has three clauses, `accept`, `on create … default` and `on delete set`, and that a delete is carried back only as the update of `on delete set`; and in the example: "PutGet holds on a delete because the values of `on delete set` make the row fail the filter".
3. **Def. 8.4, `Map` row, is not total.** A field that appears twice, or an output that is a literal, falls under none of the three cases, or under iso, while edits of the copy or of the literal have no preimage. The emit row already treats literals as lens. *Proposed:* "iso if every input field appears exactly once … and no output is a literal; lens if fields are dropped or repeated, or outputs are literals (edits must keep them)". The prototype's checker is stricter (a `Map` with anything but field references is none), so nothing built is unsound.
4. **Theorem 8.2 (c), statement:** "whose other set values are literals"; the proof treats set values that are field references or literals. *Proposed:* "whose other set values are field references or literals".
5. **Def. 8.2, the citation of the complement form.** `put : V × C → S` is the constant-complement form of view update ([bancilhon1981]); the symmetric lenses of [hofmann2011] have `putr : S × C → V × C` and `putl : V × C → S × C`. The capability lens changes its complement on a delete (item 2), so it is not of the constant-complement form either; the form used is the asymmetric one of [foster2007], with the old source as argument. *Proposed:* drop the sentence, or cite [bancilhon1981] and say that the chapter's lenses need not keep the complement constant.
6. **8.5, GetPut.** "Putting the unchanged view back leaves the digest of the state and the version of the master unchanged." The engine drops an edit whose element is unchanged before any backward step runs, so GetPut holds for the engine's `put` by construction and the test does not exercise a backward step (the test says so itself; rows 8.1 to 8.3 of the table mark the GetPut evidence void). *Proposed:* "GetPut holds by construction: an edit that leaves an element unchanged is dropped before any backward step runs. The test confirms that such an edit changes nothing; it does not exercise a backward step."
7. **Theorem 8.6, first sentence of the proof.** "A telemetry property is owned by a `readonly` rule or lies in a `forward` projection (8.1, P2)" needs the typing rules of Ch. 5 (telemetry enters a rule only through operators whose class is none). The engine rejects an edit of a telemetry property directly, by the role of the property. *Proposed, shorter and without Ch. 5:* "An edit that touches a telemetry property is rejected `TelemetryNotEditable` (8.1)."
8. **Union row of Def. 8.4 and Def. 5.10.** Consistent, but in the prototype every `Union` is none (only `preserveEvidence` is built), so no lens may contain a `Union`; one clause in 8.5 "What is built". The row reads ambiguously for a `Union` with `preserveEvidence` over inputs of different types: lens by the first clause, none by the second. *Proposed:* "lens for `preferFirst` or `preferLast`, and for any policy when the inputs have different types; none otherwise".

*One term, one meaning.*
9. `δ` is the information class (Def. 8.4, 8.5) and the delta of a patch (Def. 4.7, used in Ch. 4 and 6). Write the class as `ι`, or `info(o)`.
10. `C` is the complement (Def. 8.2) and the set of correspondences (Def. 8.7, 8.8).
11. *Source* is the input `S` of a lens, a `Source` of Ch. 10 (rule S5, "the authoring source") and the source graph of a triple. The first and third are close; S5 could say "bound to an adapter other than the authoring source".
12. 8.1 says "a second rule of the same projection", 8.5 "a second rule of the same module". Both are true (the rules sit in one projection of one module); use "projection" in both, the narrower word.
13. Def. 8.5 says "one input stream" for `bijective` and "exactly one input port" for `lens`. Same thing; use "input port" in both.
14. Theorem 8.7 (d): "a triple that is already consistent" is not defined; the hypothesis is `(S, C, T) ∈ L(G)`. *Proposed:* "a triple of `L(G)` is left as it is".

*Terms used before they are defined, or never.*
15. The clause names `accept`, `on create` default, `keep` path, `equate`, `correspond … identity` and emit "mode `create`" (Def. 8.5) are named without the rule text the committed version showed. Item 2 covers the first three; "mode `create`" needs "(Ch. 5, an emit that creates elements, not one that updates them)".
16. The Def. 8.1 table names correctness, completeness and hippocraticness; they are defined in Theorem 8.7. A gloss in the table ("hippocraticness: a triple already in the language is left as it is") or the pointer "(Theorem 8.7)" instead of "(8.4)".
17. Def. 8.3: "a proposal, not a commit". *Commit* is not defined in the paper. *Proposed:* "it changes nothing until it is accepted".

*Needs engineering background.*
18. Def. 8.2: "the integrated input graphs and rows it reads" ("integrated" is the operator `I` of Ch. 6): "the current contents of the graphs and rows it reads".
19. Theorem 8.5: "a copy that is not yet in use (a staged copy)". What is copied is not said. *Proposed:* "computes the master graph and the view from the stored inputs and the proposed rows, without making the result current".
20. 8.4, concurrent edits: "an input that the engine has stored but not yet processed" assumes that the engine stores an input before it evaluates it; say so once ("the engine first stores an input, then evaluates it").
21. Def. 8.6: "activation derives an entity type `<projection>#<rule>` that extends the kernel type `Correspondence`". *Activation* is defined in Def. 5.8 ("made active"); the kernel type and the name pattern need a pointer to Ch. 2.
22. 8.5: "the digest of the state", "8 seeded groups", "the engine's interface", "the viewer". *Proposed:* "a hash of the whole state"; "8 runs with fixed random seeds, each of 12 sequences"; "through the engine's programming interface"; "the program that displays views (Ch. 12)".

### F9. Ch. 9 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version: the statements are unchanged in meaning. One definition contradicts the step it formalizes (item 1). The proof of Theorem 9.2 needs one more hypothesis (item 2). Two counts of the measurements do not agree with each other (item 7).

*Mathematics.*
1. **Def. 9.5 carries too much, against step 4 of Def. 9.4.** Step 4: the carry policy decides about the elements of `G` "whose creating inputs are no longer in `R_h`". Def. 9.5: `Carry_γ(G, R)` is every element of `G` that `Eval(P', R)` does not produce and that the policy retains. Under `keep`, a definition that narrows (from "not `Failed`" back to `Running`) then removes nothing: a pod in `Pending` whose input is still retained is not produced by `P'` and is carried. Theorem 9.5 ("or the carry policy retains it") inherits this. *Proposed:* "`Carry_keep(G, R)` is the set of elements of `G` that `Eval(P', R)` does not produce and none of whose creating inputs is in `R`; `Carry_drop` is empty", with *creating inputs of `k`*: the cause inputs of the commit at which `k` last appeared. Theorem 9.1 then names its arguments as `(G, R_h, P', γ)` and the creating inputs, which are a function of the log (Theorem 9.4).
2. **Def. 9.2 attributes every cause input of a commit to every key the commit edits, and the counterexample of Theorem 9.2 needs `i₁` alone in its commit.** If `i₁` (the pod in `Pending`) is processed in one commit with an input that edits some key, `i₁` qualifies for that key, is retained, and the replay under `P'` creates `p₁`. The theorem ("in general") stands with the hypothesis "`i₁` is the only input of its commit". A consequence worth one sentence: histories, `R_h`, and so the result of a definition edit depend on how inputs were grouped into commits, while the output graph does not (batch invariance, Ch. 6). *Proposed:* add the hypothesis to the proof, and either say this in Def. 9.2 or attribute by contribution: an input qualifies for `k` iff it contributes a fragment of `k` whose weight the commit changes.
3. **Def. 9.4, step 2, against Def. 9.5.** Step 2 "takes the previous output graph `G` as the starting state"; Def. 9.5 evaluates "starting from the empty state", and the proof of Theorem 9.5 says "starting from `G` does not by itself retain `k`". *Proposed:* "keeps the previous output graph `G` for the carry and for the difference".
4. **Theorem 9.3, consequence: the recommendation contradicts the paragraph before it.** The paragraph shows that a view that reads graphs and has a `Threshold` loses state under rebuild; then "this paper therefore proposes rebuild for projections that read graphs". *Proposed:* "rebuild for projections that read graphs and have no `Threshold`, replay for all others".
5. **Theorem 9.5 drops the second condition of presence.** Def. 9.7 and Def. 6.4: present iff `σ(k) > 0` and a create fragment exists. Theorem 9.5: "remains iff its support … is positive". An element whose create input is no longer replayed while update inputs are has positive support and no create fragment; it is absent. *Proposed:* "iff its support … is positive and a create fragment exists". This is the case of the last sentence of the consequence of Theorem 9.3 (a bounded history).
6. **Def. 9.10:** "Ids are path ids (Def. 2.3), so a rename appears as a changed property." A path id contains the name (Def. 2.3), and a rename keeps the id only when the text says `was <old name>`. *Proposed:* "so a changed property appears as a changed property; a rename does so when the text says `was <old name>`, and is otherwise a removal and an addition".
7. **The counts of 9.6 and the table count different sets of entities.** Now: 15,154 entities of which 14,622 evidence, so 532 other entities; 48,201 and 47,598, so 603. The table (earlier state): 456 and 491 other entities, matching its rows for 10 and 2 minutes. Ch. 10 (10.6) explains the difference: 532 = 456 + 76 meters and 603 = 491 + 112 meters, which the earlier state did not have. The text gives only the reason for the higher evidence counts. *Proposed:* add "and had no meters (Ch. 10, 10.6), so its other entities are 456 and 491 instead of 532 and 603". Ch. 10 also says that 231 events of the newest recording still get two identities, so "could get two identities" should read "got two identities more often".
8. **`AuditEvent` is not an evidence element by Def. 9.12**, which requires a relationship `e_ε → x`; the list says it "has no relationship in the master graph". Either Def. 9.12 allows an event whose subject is not in the graph, or the list and the counts of item 7 separate `AuditEvent`.
9. **Proposition 9.10 (b) holds for the prototype's edit, not for Def. 9.4.** A complete preview computes `Eval(P', R)` with no carry and over the whole retained log; the commit of Def. 9.4 replays `R_h` with a carry. *Proposed:* "… that a commit would perform in the prototype (full replay, policy `drop`)".
10. **Def. 9.8, "the sets of input ids":** with union for both operations the empty set is neutral for both, and `0 · a = 0` fails. The lineage semiring needs an added bottom element (`Lin(X)` in [green2007]). *Proposed:* name the operations and the added element, or drop this instance.
11. **Theorem 9.7 (a):** "a finite sum over multisets `m` … with coefficients that depend on `Q` and `f` only". For `Map` the rows with `map(x) = f` need not be finitely many, so the formal sum may be infinite; it is finite at every `S` of finite support, as the proof says. *Proposed:* "a sum over multisets `m` …, with coefficients that depend on `Q` and `f` only, of which finitely many terms are non-zero at any `S` of finite support".
12. **Def. 9.2:** "qualifies for nothing, also when it changes telemetry (Theorem 7.1)". The sentence holds by Def. 9.2 alone; Theorem 7.1 has a pending restatement (a measurement can change graph data when a `Threshold` row appears), and then the input does qualify. *Proposed:* drop the reference.

*One term, one meaning.*
13. *Retain* and *retained* have five meanings here: the number `retain` of a projection (history length, Def. 9.2), the retained set `R_h` (Def. 9.3), the input rows a host keeps in its log (Def. 9.11, Proposition 9.10, 9.5, where `R` is "the full retained input"), the operator `Retain` that removes evidence elements by age (9.6), and the retained range of windows (Def. 7.13, in the consequence of Theorem 9.3). Proposition 9.10 writes `R` and Def. 9.3 writes `R_r` for different sets. *Proposed:* "the log of inputs" for the host's rows, `R_log` or no letter in 9.10, and "kept" for histories.
14. In Theorem 9.7, `L` is an arbitrary set of input rows, `L_S(f)` the lineage at `S`, and Def. 9.8 has `L(f)`. Write the arbitrary set as `U`.

*Needs engineering background.*
15. "digest of the state" (Proposition 9.10), "the number of the last event" (Proposition 9.10, the event stream of Ch. 12), "in pages" and "the pages give the same list" (lineage in the prototype), "renewals of leases" (`AuditEvent`), "the table of interned strings", "the input rows a host retains". *Proposed:* "a hash of the whole state", "the sequence number of the last event the engine published", "in parts of fixed size", "the periodic messages by which Kubernetes components show they are alive", "the table that stores each distinct string once", "the inputs the engine keeps in its log".

### F10. Ch. 10 after its style pass, read as the target reader (2026-11-12)

Compared with the committed version and with the identity catalog of the prototype. One change of wording changed a meaning (item 3). Two descriptions of identity contradict Ch. 1 and Ch. 5 (items 1, 2). Two proofs use a premise they do not state (items 5, 6).

*Mathematics and meaning.*
1. **A pod recreated under a stable name merges with its predecessor; it does not get the name (F7, still open, now derived).** 10.4 says the key `podName(cluster, namespace, name)` "points to a new pod" when the pod is recreated, and that "a flow is attributed to the pod that held the name at that position of the log". By Def. 1.10 the alias of the name to the old pod's id is permanent. By Ch. 5 (Def. 5.9) the `Link` of the new uid to the name is a `sameAs` between keys bound to different identities, which is a merge (Theorem 1.7). The engine does the same: a key already bound to another identity of the same entity type merges the two, and the smaller id survives. So the two pods become one entity, and every flow, old or new, is attributed to it. The third bullet of 10.4 says the effect "has not been measured". *Proposed:* state the derived behaviour as a limit ("the old and the new pod become one entity; flows are not separated by incarnation"), or take option (a) of F7 (the name as a lookup in graph data, not an identity key) in Ch. 1 and Ch. 10 together.
2. **§10.7, "a flow before its pod … the later link is a counted conflict".** By Ch. 5 and in the engine the later link merges the two identities, and the final id does not depend on which came first. The engine counts a conflict only when the two identities have different entity types. *Proposed:* "… gives the pod an id of its own; when the pod's row arrives the two identities merge, and the final id does not depend on the order. A conflict is counted only between identities of different entity types; both recordings had none."
3. **Theorem 10.6 now says "recording"; the committed version said "capture". The change makes it contradict Proposition 10.7.** The theorem orders the records by `(capturedAt, file path, seq)`. The recording of a live run (Def. 10.7) is one file in the order of consumption, and a test checks that it is read in file order, not by capture time. With arrivals out of capture order, the sort of Theorem 10.6 reorders it and breaks LV1. *Proposed:* "Order the records of a capture (10.5) by …; the recording of a run is one file and is processed in its line order", or let `seq` lead for one file. The theorem also needs the same settings: with another batch size the log of versions differs (Theorem 6.2 (b), as the remark after Proposition 10.7 says). *Proposed:* "with pinned definitions and the same settings (batch size, step of logical time, late horizon)".
4. **"Contribution" and "key" are used in two senses, and the engine paragraph after Theorem 10.5 is wrong as written.** Def. 9.7: a contribution is "an input, a rule and a weight"; Def. 10.3: "the set of fragments that one record of a source causes", named by a contribution key. "An `ADDED` or `MODIFIED` event replaces the contribution of the object under its delivery key": the delivery key of a watch event contains the `resourceVersion`, so every `MODIFIED` has a new delivery key, and under it the event would add a contribution, not replace one. The second sentence of Theorem 10.1, "a repeated observation with a new key is a new contribution", reads the same way. *Proposed:* one definition of contribution (Def. 10.3, and Def. 9.7 refers to it); "replaces the contribution of the object under its contribution key (kind and uid); the delivery key only drops repeats"; Theorem 10.1: "an observation with a new delivery key is processed; it replaces the contribution with the same contribution key, and adds one otherwise".
5. **Proposition 10.8 uses LV2 and does not assume it.** The proof ends "the count of rows is a function of the consumed envelopes (premise LV2 of Proposition 10.7)", and LV2 is not among LR1 to LR4. *Proposed:* add LV2 to the premises (LR5).
6. **LV3 names the horizon only.** LV2 makes the advances of logical time a function of "the settings of the run", among them how often time advances. The second processing must use the live run's step as well as its horizon. *Proposed:* "LV3 (same settings). The second processing uses the late horizon and the step of logical time of the live run."
7. **Theorem 10.3, second hypothesis.** "From a position behind the scan's snapshot": the example shows that "newer than" is meant. The hypothesis also misses a deletion. If a `DELETED` newer than the snapshot is processed before the list, the list puts the object back. The theorem, which speaks of withdrawals only, does not see this, but the state is wrong. *Proposed:* "Let the engine have processed no record of that source about the scope that is newer than the scan's snapshot." And "the withdrawals are exactly the elements of the scope that the engine holds from that source and that have been deleted at the source" (not "the records … deleted at the source", which counts also what the engine never held).
8. **Theorem 10.3 on the live path.** A pair `relist-start` … `relist-end` is applied as a coverage declaration, and the forwarder's watch events share the stream. The second hypothesis holds only if no watch event of that kind that is newer than the list's version lies between the two markers. Nothing in the chapter says whether the forwarder stops its watch during a new list. *Proposed:* state it as a property the forwarder must have and that is not checked.
9. **Theorem 10.4, proof.** "A withdrawal is produced only by an explicit record of deletion and by a reconcile rule." The operator `Retain` (Def. 5.15) also withdraws, when logical time passes an age. The theorem stands. *Proposed:* "only by an explicit record of deletion, by a reconcile rule, and by `Retain` when logical time passes an age; none of them is caused by a loss".
10. **Def. 10.4:** "level of consistency" and "`level ≥ ℓ`" assume an ordered set of levels that is never given. *Proposed:* name the levels (for example a consistent picture at one version, above eventually consistent) and their order.
11. **Table of 10.2 against 10.6:** the key of a record of the network logs of Azure is "hash" in the table and "the path of the file and the time of the record" in 10.6.
12. **"Lateness" and "late horizon".** §10.7 computes `λ = floor(capturedAt / step) × step − lateness`; LV3, Def. 10.9 and 7.6 of the prototype speak of the late horizon (three window widths live, a million in a recording). Def. 10.9 says late rows are "judged by the late horizon (Ch. 7, Def. 7.6)", and Def. 7.6 does not define it (F6, item 6). *Proposed:* say which quantity of Def. 7.6 "lateness" is, and define the horizon there.

*One term, one meaning.*
13. *Position* is a place in the engine's input sequence (Def. 10.3, "recorded position") and a place in a source (the partition and sequence number of Event Hubs, LR1, Def. 10.8). Three sequences have similar names: `seq` of a file, the ingestion sequence number, and the `sequenceNumber` of Event Hubs. *Proposed:* "source position" for the second.

*Needs engineering background.*
14. Not glossed: `BOOKMARK` ("an event that reports only the current version"), "410 Gone" ("the answer that the requested version is no longer kept"), "eventually consistent" ("different parts may show different moments"), "namespace" ("a named group of objects in a cluster"), "endpoint slices" ("the lists of pod addresses behind a service"), "the network observer's relay" ("the program that collects the flows of all machines"), "a lease bounded in time" in 10.4 ("the time during which an address belongs to one pod"; Ch. 9 uses *lease* for a Kubernetes object), "file database" ("a store in one file on disk"), "state seal" (pointer to Ch. 11), "retries", "queue held at 64 MB".

### F11. Ch. 11 after its restructuring, read as the target reader (2026-11-12)

Compared with the committed version: Def. 11.10, 11.11 and Propositions 11.9, 11.10 are new as numbered statements, with the text of the committed bullets; the other statements are unchanged in meaning. One leftover of the restructuring (item 1). One proposition claims more than the prototype's Cosmos store allows (item 2). One cited result is stated too strongly (item 4).

*Mathematics and meaning.*
1. **"No number of 11.4 to 11.6 is measured. The prototype's own figures are in 11.7."** There is no 11.7, and 11.6 holds the prototype's measured figures (337 MB, request counts, 7.84 million samples per second). *Proposed:* "Every number in 11.4 and 11.5 carries one of these labels. No number of 11.4 and 11.5 is measured. The prototype's own figures are in 11.3 and 11.6."
2. **Proposition 11.9 against the Cosmos store of 11.6.** The proposition refuses a start when "a stored log … ends inside one computed group of patches", and says "after a kill it is the state that a run that was never killed has after that entry; the kill may have cut short the writing of its commits". On Cosmos a group of commits that spans two parts of the log or more than 100 records is several batches, and the group is not all or nothing (11.6). A kill between those batches leaves the stored log inside a group, and the start is refused. So the last sentence holds for a store that writes a group all or nothing (SQLite, the store in memory), and on Cosmos only for groups of one batch. *Proposed:* say so, or let the restart complete a group that the stored log holds in part when the computed group agrees with it.
3. **Theorem 11.2, proof:** "A commit record `c_r` has the id `c:r` in the logical partition of its part of the log. By S2 at most one create of `c_r` succeeds." S2 is per logical partition, so this needs the part of the log of version `r` to be a function of `r`. *Proposed:* "… in the logical partition of the part of the log that `r` determines".
4. **Fact 11.2:** "for the others, time per update below linear is impossible under … the OMv conjecture". The lower bound of [berkholz2017] excludes update time `O(n^{1/2−ε})` together with delay `O(n^{1−ε})` (for a Boolean query, together with answer time `O(n^{1−ε})`); it does not exclude every sublinear update time. *Proposed:* "for the others, no algorithm has update time `O(n^{1/2−ε})` and delay `O(n^{1−ε})` under … the OMv conjecture".
5. **Theorem 11.8, proof:** "the single writer checks each edit set against its hot graph, which equals `G_h`". Theorem 11.2 holds without the lease, and so does this one: each writer checks against its hot graph at its expected head `h'`, and by Theorem 11.2 a commit that succeeds has `h' = h`. *Proposed:* "each writer checks … against its hot graph, which equals `G_{h'}` for its expected head `h'`; by Theorem 11.2 a commit that succeeds has `h' = h`".
6. **Theorem 11.6 (ii) and `Unavailable`.** A read may return `Unavailable` when the store refuses it for load (Def. 11.3, 11.7), and that is not a function of `(graph, version, key)`. (ii) then requires that the engine retries such a read until it answers and never evaluates on `Unavailable`. *Proposed:* say this after the hypothesis, next to "a duty of each store adapter".
7. **Transport estimate against the measurement of Ch. 10.** "Summed in windows of 60 s: … about 4,000 per second, about 4 units" uses the documented 1,000 events per second per unit. Ch. 10 measured about 125 events per second with one unit, one partition and one send at a time. At that rate 4,000 per second needs 32 units and more partitions. *Proposed:* add the measured rate and what it implies, or the condition under which the documented rate is reached.
8. **A1 and A2 lean on statements with a pending restatement.** A1: "They create no version (Theorem 7.1)"; A2: "This is a threshold (Def. 7.10), with the bound of Theorem 7.4". Presence with hysteresis is exactly the case that F6, item 1 found missing from Theorem 7.4: a relationship that appears when its key first has a window and goes when the last window leaves. *Proposed:* refer to the restated Theorem 7.4 once it is applied, or count those changes here ("two per appearance").
9. **The Cosmos store "differs from 11.5" in that "identity bindings are written after the commit record, not before it"**, but 11.5 says nothing about when bindings are written. *Proposed:* state the designed order in 11.5 (and why it matters: a reader that sees a commit must find its bindings) or drop the point.
10. **Def. 11.10:** "a crash leaves at most one entry that the log of commits has not caught up with" holds only if the engine writes entry `n+1` after the commits of entry `n`. Say so, or "at most the entries after the last one whose commits were written".
11. **Two sizes of the estate.** The estimate has about 2.25 million structural relationships; the hot graph of 11.6 and Ch. 13 is measured with 2.5 million. *Proposed:* say that the benchmark rounds up, or use one number.
12. **Def. 11.9, "a documented property of Cosmos DB … unless noted":** S5 has no citation and nothing is noted. Under the weakest consistency level of Cosmos, "readable within finite time" is the documented promise; name the level the design assumes.

*One term, one meaning.*
13. `w` is the version up to which version rows are written (Def. 11.3, Theorem 11.1) and the weight of a sample `(t, g, k, p, τ, w, x, i)` (Def. 11.4), in two tables a page apart.
14. Def. 11.8 writes `JCS`, which no chapter defines; Ch. 1 writes the canonical encoding `enc`. *Proposed:* `digest(enc([pageSize, [page digests…]]))`.
15. *Seal* is the page seal of Def. 11.8 (not built) and the state seal of Def. 11.11 (built). Def. 11.1 ("the truth from the oldest kept seal on") and Theorem 11.5 ("older than the oldest kept seal") mean the first; the prototype's `Gap` comes from the limit of served history (11.6). The text says the two differ; Theorem 11.5 could add that in the prototype `Gap` is caused by the served limit.

*Needs engineering background.*
16. "cache", "object storage", "one primary machine", "chunks", "the local emulator" ("a program that imitates the cloud database on one machine"), "synchronous", "a linked list per entity", "Queues between the parts of the engine are bounded", "workload" (used throughout 11.4: a deployment and its pods), "pool" ("a group of machines of one size"), "identities derived from labels", "rollout", "differences of differences of times and the bit differences of values", "entities are stored as columns … which avoids interpreting row by row", "the build of the engine", "byte-equal".

### F12. Ch. 12 after its style pass, read as the target reader (2026-11-12)

Compared with the inventory's statements: unchanged in meaning. One proposition is false across a gap (item 1). The numbering is out of order (item 7).

*Mathematics and meaning.*
1. **Proposition 12.9 "never skips … a version" fails across a gap.** By Def. 12.7 a subscription whose `after` is older than the kept events receives `gap` and then snapshots. The client then holds the snapshot's version without having held the versions in between. *Proposed:* "… and never skips or repeats a version, except across a reported `gap`, after which it holds the version of the snapshot".
2. **Proposition 12.10 (b):** "different bytes never share a tag". A tag is the first 128 bits of SHA-256, so two bodies share a tag only by a collision. The other statements of the paper say "unless `H` has a collision". *Proposed:* add it here.
3. **Theorem 12.3, second sentence**, "An aggregate has a new identity and never reuses the id of a member", is stated as a fact. The text after it says it is a rule for authors that no check enforces. *Proposed:* "By Def. 5.9 an aggregate is to have a new identity …; the checker does not enforce this", or move the sentence out of the theorem. The first sentence also proves less than the highlight needs. "Same id if equal components" is Theorem 1.4. The highlight needs "same id, hence same entity", which holds because an id is minted from one key and other keys are bound to it only by alias or merge (Def. 1.10), unless `H` has a collision. *Proposed:* state that.
4. **Theorem 12.4, proof:** "or from a field that the store adds (Ch. 11, 11.5)". Ch. 11 after its restructuring says nothing about a field for logical time. *Proposed:* drop the clause, or add the field to Ch. 11, 11.5. The statement's `M` (measurements) is not defined in the chapter.
5. **Theorem 12.1:** "holds `G_r` … for every `r`", but a rejected patch stops the replica (proof). *Proposed:* "for every `r` up to the first rejected patch". The proof cites Theorem 11.7 for "the snapshot is the state at its version". That theorem is about the seal's digest; Theorem 6.4 is the fitting reference.
6. **A preview "over part of the input (500,824 of 579,175 rows)"**, while Def. 9.11 replays "the first `n` retained input rows", by default 500,000. The run does not give `n`. With the default the count would be 500,000. Give `n`, or say that a preview reads to the end of the batch that contains row `n`.
7. **Numbering out of order.** Order in the text: Def. 12.1 to 12.4, Def. 12.7, Def. 12.5, Def. 12.8, with no Def. 12.6. Results: Theorem 12.1, 12.2, 12.3, 12.4, 12.5, then Propositions 12.9 and 12.10 (in 12.4), then Theorems 12.6 to 12.8 and Proposition 12.11 (in 12.5). A reader who looks for Def. 12.6 or follows the numbers is lost. *Proposed:* renumber in reading order and rename the inventory rows, or say in a note that the numbers are kept from the earlier version.

*One term, one meaning.*
8. `bid = base32(H(JCS([rule, group key])))`: `JCS` is not defined in any chapter (as in Ch. 11, F11 item 14); Ch. 1 writes `enc`. The engine's block id is minted by the identity catalog (`alloc` of Ch. 1, with tenant and domain), not by this formula. Theorem 12.6 holds for both, but Def. 12.5 and Def. 12.8 then give two formulas for one id.
9. `w` in Def. 12.4 is a window length; in Ch. 11 it is the version up to which version rows are written and the weight of a sample.

*Needs engineering background.*
10. "subscriber", "fake connection", "media type", "tag" (glossed), "caching", "browser", "focusable group", "screen reader", "Markdown", "a template appends a stable identity as the last key", "summaries at most every 100 ms … so a stream of patches does not overload the page", "a standard checker of accessibility". The ones a mathematician cannot guess: "media type" ("the declared format of a body, such as JSON"), "focusable group" ("one element that the keyboard can reach as a whole"), "screen reader" ("a program that reads the page aloud").

### F13. The workload model of Ch. 7 (7.4), and every chapter changed since F2 to F12 (2026-11-12, paper at the commit of the workload model)

Read as the target reader, and compared with the definition text of the workload model. Ch. 3 to 6 and 8 to 12 were compared word by word with the versions read for F2 to F12: the items of F8 to F12 are applied as proposed, and F7's pod-name inconsistency is resolved (Ch. 1, Ch. 5 and Ch. 10 now agree that a recreated pod merges with its predecessor, stated as a limit). Ch. 13 to 15 are read for the first time after their style pass.

*A. Propositions 7.7 and 7.8 against the workload model.*
1. **Prop. 7.7 (c): "true of the shipped definitions" is false.** The hypothesis is that graph data derive from measurements only through `Threshold` outputs. In the shipped definitions, the master's `Calls`, `Sends` and `Destination` are created from rows of a `Window` grouped per window (the ingestion rules for observed calls and destinations). The workload model then derives `DependsOn` from those calls (way 5) and so `Affects`, and creates `TalksTo` from the rows of a `Series` and a `Rollup`. So a pair of services that first calls changes `Affects`, and with it states, without a toggle. A window is never withdrawn by retention (Def. 7.13), so each such edge appears once. *Proposed:* drop the parenthetical. In "How the workload model differs", add that (c) needs a further term in the model: one revision chain per pair of components whose first call creates a dependency.
2. **"`Affects` … never from communication: an observed call is evidence that one part uses another, not that a failure travels along it."** Way 5 turns calls between the pods of two services into `DependsOn` from caller to callee, and `Affects` follows `DependsOn`. So a failure of the callee does reach the caller; it never travels from caller to callee. *Proposed:* "`Affects` is made from dependency and composition; communication edges are not walked. A call is used as evidence that the caller depends on the callee (way 5), so a failure travels against the direction of a call and never along it."
3. **The direction of `Affects` is not stated.** In the definitions an edge `c → d` of `Affects` means that `d` affects `c` (`workload → service`, `service → deployment`, `caller → callee`, `pool → node`). Def. 7.12 takes `state(c)` over the `d` with `(c, d) ∈ A^{≤D}`, which is right for that direction. Read with the name in its natural sense ("`c` affects `d`"), it is backwards. *Proposed:* say so in 7.4 ("an edge `c → d` of `Affects` means that `d` affects `c`"), or rename the relation.
4. **The model differs from Def. 7.12 in a fourth point.** A pod's own severity includes its node's own severity through the pod's placement on the node, not through an `Affects` edge, and only the node's own signals count, not its pool or cluster. The list of three differences omits this.
5. **The share rule counts only pods that have a severity, and it maps no data to healthy.** `n` counts pods with an own severity; pods with none are not counted. A pod in no data (1) is neither "unhealthy" (≥ 3) nor "degraded or worse" (≥ 2). A deployment whose pods are all in no data is therefore `Healthy`, and so is the service above it. "No data ranks between healthy and degraded" holds for every maximum of the model but not across the share rule. Prop. 7.7 (a) still carries over (the share rule does not decrease). *Proposed:* say "of its pods that have a severity", and either state that a deployment of pods in no data is healthy or give the share rule a no-data level ("no data when at least half are in no data or worse").
6. **Def. 7.12 and the model number severities differently.** Def. 7.12: 0 healthy, 1 degraded, 2 unhealthy. Model: 0 healthy, 1 no data, 2 degraded, 3 unhealthy. "1" means degraded in one and no data in the other. Def. 7.12 also writes the worst severity as `w(c)`, while `w` is the window width throughout Ch. 7 (Def. 7.2, 7.5, Theorem 7.4). *Proposed:* give Def. 7.12 the values 0, 2, 3 (or names), and write the worst severity `sev*(c)`.
7. **Prop. 7.7 (c) and its parenthetical still name `health` and `Signal`.** The graph is `workload`, and the signal element is `ComponentSignal`.
8. Prop. 7.8 matches: the definition reads `Affects` outward to depth 0 to 16. Prop. 7.7 (a) and (b) carry over by the argument given, with items 4 and 5 added. The claims for the model sit in an unnumbered paragraph after Prop. 7.8, where a reader looks for a corollary. *Proposed:* "Corollary 7.9 (the workload model)", with (a), (b) and the extra terms of (c).

*B. The typing rule P6 would refuse the shipped definitions.*
9. P6 (section D) lets no create emit take a windowed row. The shipped definitions do exactly that in at least six rules: `Calls` (observed calls), `Destination` and `Sends` (observed destinations), and `Meter` with `MeterOf` (Azure meters, node meters, pod warnings), all from `Window` grouped per window. The workload model adds `TalksTo`, from `Series` and `Rollup`. The row T-WINDOWED said "the shipped definitions still check"; that is false. The choice between restating Theorems 5.3 and 7.1 and adopting P6 therefore includes this cost. One option is to adopt P6 and rewrite those rules so that presence comes from a `Threshold` (Ch. 11, A2). The other is to restate and count, in Theorem 7.4 and Prop. 7.7, the edits that the appearance of windows causes. Ch. 15 (15.2, row 1a) should name this cost.

*C. Ch. 7, other points.*
10. Def. 7.16 now writes the time-to-live `T`, but its last bullet and "Three absences" still use `D` ("longer than `D`", "`N·w/D + 1` silences", "`D < within ≤ ρ`"). In this chapter `D` is the depth bound of Def. 7.12.
11. 7.6, "The shipped definitions do not use fill or a time-to-live yet", is out of date: the workload model reads pod warnings with `fill 0` and every meter with a time-to-live of five minutes (7.4). "Def. 7.6 has no horizon of its own" is out of date too: Def. 7.6 now defines `H`.
12. Theorem 7.1, proof, last sentences: "By Def. 7.10 a crossing operator emits an edit only at a toggle". Def. 7.10 now says "at a toggle, and when the key's row appears or goes". Since the second sentence of the theorem is marked false, the proof should prove the first sentence only.
13. Def. 7.14, "A covered window is therefore closed": the premise of coverage added there (a source delivers a window's records before its progress passes the end by `L`) is not the one this needs. Closing is by logical time (Def. 7.6), coverage by progress. It follows only if logical time is never behind progress (third pass, A).
14. *Engineering background in 7.4:* "service account", "the role that identity holds on a resource", "environment variable", "secrets", "backend", "crash-looping", "cloud identity". The paragraph "Words used in this section" covers the others.

*D. Ch. 9, 10 and 11, new points after the applied items.*
15. Ch. 9 now says that `AuditEvent` is not an evidence element in the sense of Def. 9.12. But 9.6 ("evidence elements are 14,622 of 15,154"), Ch. 10 ("the three evidence types are 14,622 and 47,598") and Ch. 14 (14.2) still count the 10,487 and 42,244 audit events as evidence elements. *Proposed:* "events kept as elements", or evidence elements 4,135 and 5,354.
16. Ch. 9 (9.6) lists `DependencyEvidence` among "the evidence elements that exist". It stands for no source event and has no delivery key, so it is not one by Def. 9.12 either. *Evidence* now has three meanings, while 9.6 says two.
17. Def. 9.7 now says "*contribution* (Def. 10.3): the fragments that one input causes through one rule", and Def. 10.3 "the set of fragments that one record of a source causes". One is per input and rule, the other per record.
18. Ch. 11, proof of Theorem 11.2: "`r` shifted right by 16 bits". *Proposed:* "`⌊r / 2¹⁶⌋`".

*E. Ch. 13 (first reading after the style pass).*
19. **H12, "a threshold is linear on closed records"**, contradicts Ch. 6, where `Threshold` is a per-key state machine and not linear. *Proposed:* "a threshold does constant work per closed record and key".
20. **Theorem 13.1:** the rule `(a ⋈ b)^Δ = Δa ⋈ Δb + I(a) ⋈ Δb + Δa ⋈ I(b)` is right only with `I(a)` and `I(b)` the integrals *before* the step. With the integrals after it, `Δa ⋈ Δb` is counted twice. *Proposed:* write `z⁻¹(I(a))` and `z⁻¹(I(b))`, as Ch. 6 does.
21. **Prop. 13.2** is arithmetic on a model: it holds for `α > 0`, which should be stated, and it is proved, not tested. "The proposition counts as tested" asks the reader to accept a test that does not exist. *Proposed:* "proved on the model; `α` and `β` are not measured".
22. **Theorem 13.4:** "if every step meets `q` only at p99, then `d·q` holds only at the quantile `1 − 0.01·d`". The union bound guarantees that quantile; it does not show that a better one fails. *Proposed:* "is guaranteed only at". The example ("meets 4 s only at the 96th percentile") needs the same change.
23. **Rule of decision (13.5)** gives "supported" for a target of rate and nothing else. Refuted and inconclusive are undefined for rates.
24. "Speed of evaluation: 17.0 million rows per second, against 47.5 million for a loop written by hand (2.8 times faster)." The loop is the faster one; the sentence reads as if the program were. "The median, p50" names one statistic twice.
25. 13.7, "The health model of Ch. 7 (7.4) is a stack of four projections", describes the earlier model. The workload model is two graphs.
26. *One term, one meaning.* Def. 13.1 calls the benchmark load a *workload*. Ch. 7 calls one namespace of an application a workload, and Ch. 14 (14.4) calls the 22 deployments of the deployment health graph "workloads". `d` is the depth of the stack (Def. 13.1, Theorem 13.4), the largest degree (`d_a`, `d_b`, Theorem 13.1) and the degree of a hub (example). `λ` is an arrival rate (Theorem 13.3) and the logical frontier everywhere else. `ρ` is a rate of events (Def. 13.1), a retention (Def. 7.13) and a release (Ch. 9). H6 says 2.25 million relationships and H9 2.5 million (also F11, item 11).
27. *Engineering background:* "load average" (used to scope every status: "the average number of programs ready to run"), "release build", "interned ids", "a hash … an expected constant number of probes in a table whose load is bounded", "handles", "pushes back" (M1), "harness", "micro-benchmarks".

*F. Ch. 14 (first reading after the style pass).*
28. 14.3 lists "the health model (Ch. 7)" among the examples, and 14.4 ("What this shows") says "a stack of four projections computes a health model that finds failing services". Both describe the earlier model, which the table labels as replaced.
29. 14.10 lists "thresholds as data" as open for 2a. The workload model reads its thresholds from rules a person typed in (`SignalRule`, 7.4). "Def. 7.6 stated on logical time" (1a) is unclear, since Def. 7.6 closes windows by logical time already.
30. 14.0: "Determinism of replay and batch invariance hold on both real recordings at five batch sizes". By 14.5 that is at earlier states. At the last state it is the default size (the authors) and 1, 50 and 100,000 (set B, not repeated).
31. *Engineering background:* "network policy" (the running example's `intruder`), "frames per second", "ticks", "a warm-up inside the benchmark", "in every category" of tests.

*G. Ch. 15.*
32. Row 1a "Theorems 5.3 and 7.1 as stated": the column "What it needs" should add that a typing rule for windowed rows refuses six shipped rules unless they change (item 9).

### F14. Theorems 5.3 and 7.1 as restated with Def. 5.20, derived before reading their proofs (2026-11-12)

Derived from Def. 5.20, the typing rules of 5.2 (P4, E-Prop, the rules of `Window`, `Series`, `Rollup`, `Delta`, `Threshold`), Def. 5.19 and Def. 7.6 to 7.16, then compared with the proofs. Two statements have a case they do not cover, and a third (Theorem 7.4) has the same gap as 7.1. All three are marked "restatement pending". The gap in 7.4 is in the form I proposed in F6, item 1.

*What the derivation gives.* By P4 every position that decides whether a row exists or where it goes (filter predicates, join, group and roll-up keys, the rows of `Distinct`, identity components, ids and ends) is `data`, and by the role join a value computed from a `tele` operand is `tele`. A runtime fault leaves its row in place with an absent value (Def. 5.19), and an emit skips a row only for an absent id or end, which are `data`. So below the measurement operators, whether a row exists is a function of data fields and of which rows those operators produce. The derivation agrees with the theorem in form. Graph data are a function of the data fields of the inputs, of the logical-time inputs, of the outputs of `Threshold`, and of which rows `Window`, `Series`, `Rollup` (and `Delta`) produce.

1. **Point (1): do the supports depend on measured values? Per operator:**
   - `Window` over rows: one output row per input row, assigned by a `data` time column (rule (Window)), closed by logical time. No value enters.
   - `Series` without fill or time-to-live: a record per element of the structural input and per closed window that has samples. Samples are deduplicated by `(k, p, τ, i)` (Def. 7.2), not by value, and a late revision retracts and reinserts a row with the same data part. No value enters.
   - `Series` with a fill: a filled record exists for every window that is covered for `k` and has no stored record (Def. 7.15). Coverage is a function of recorded progress, loss marks and `since(k)` (Def. 7.14), all recorded data. A loss mark that arrives later takes the fill back. No value enters.
   - `Series` with a time-to-live: the no-data record depends on `e(k)`, which depends on `since(k)` and on the newest record existing (Def. 7.16). No value enters.
   - `Rollup`: a row per roll-up key and window that has a record of a member not in no data, with `data` keys. No value enters.
   - `Delta`: one row per window of its input; values only. Def. 5.20 does not list `Delta` among the producers of windowed relations; it should, or say that a `Delta` row is windowed because its input is.
   - **One exception.** Two ids merge, and their stored windows hold values that are not monoid values and differ: the window has no value (Law 7.5 (ii)). A `Series` with a time-to-live gives no record for a window in which a property it reads has no value (Def. 7.16: "a record exists for a window only when every property that the Series reads has a value there"). So whether that record exists depends on whether two stored values are equal: two runs that differ only in those values have different supports. In the prototype the conflicting window is removed for every `Series` (7.6). *Counterexample:* a mean stored as one number for two ids of one meter, with window values 3 and 3 in one run and 3 and 5 in the other. After the merge the first run has a record and the second none. A projection that sets a data property from the key support then differs between the runs without any `Threshold`. *Proposed:* let such a window keep a record without a value, as the record of a key in no data has ("a record exists … when every property that the `Series` reads has a value there, stored or filled, or has none by a conflict of a merge"). Then the sentence holds as written. Either that, or name the exception in Theorem 5.3. The prototype then differs in one more point (7.6).
   - *The proof's reason is too narrow.* "Whether a closed window exists depends on which samples arrived, not on their values." A filled record exists without a sample, and a no-data record without a window. Their existence depends on coverage, `since(k)` and the newest record, which are recorded data and existence. The conclusion holds for them, but the proof should say why.
2. **Point (2): the bound of Theorem 7.1 fails with late records, and so does the same sentence of Theorem 7.4.** The key support of `k` is the union over its windows `j` of the interval of frontiers at which window `j` has a record and is in the look-back. The proof takes that interval as `[a_j + w + L, a_j + within]`, which is right only if the window's first record exists when the window closes. When the first sample of a window arrives late, inside the late horizon `H`, the record exists only from its arrival, so the interval is `[t_j, a_j + within]` with `t_j` up to `a_j + w + L + H`. The same holds for a filled record whose coverage arrives after the window closed (progress behind logical time). A record that a loss mark or a retraction removes also leaves the support early. *Counterexample within the live horizon* (`H = 3w`): `within = 30w`, `L = 0`, advances every `w`. Key `k` has a window at `0` with an on-time sample and a window at `28w` whose only sample arrives at the frontier `32w`, accepted because `32w ≤ 29w + 3w`. The starts are `28w ≤ within − w − L = 29w` apart. The key is in the support on `[w, 30w]`, out at `31w`, and in again from `32w` to `58w`: four changes for two windows, against "at most twice". A `Threshold` row over the same series appears and goes the same way, against Theorem 7.4 ("over `N` windows without such a gap it appears at most once and goes at most once"). In a replay the late horizon is a million window widths, so no gap of starts excludes this. *Proposed for both:* "over `N` windows of one key, the key support (the row of a `Threshold`) changes at most twice, and twice more for each window whose first record is accepted at a frontier at which no other window of the key is in the look-back. When every window has its record from its closing on, and no loss mark or retraction removes a record, that happens only after a gap of more than `within − w − L` between window starts. With late records inside the late horizon `H`, it happens only after a gap of more than `within − w − L − H`." With a time-to-live the key also keeps its no-data record, so it leaves the key support only when it leaves the structural input. "A key's first window entering the look-back or its last window leaving it" should say "record" for that reason.
3. **Point (3): the counterexample of the third pass is covered, not excluded.** A `Series` of pod warnings, then a `Map` keeping the pod, a `Distinct` and a data property set on the pods that remain is well typed (P4). Every row on that path belongs to a windowed relation by Def. 5.20. The property is a function of the key support of the `Series`, so it falls under (iii) of Theorem 5.3, the second case of Theorem 7.1, and the key-support bound (with item 2). The chapter now gives it as "Why (iii) is needed", and P6 is an optional profile that would refuse it.
4. Smaller points.
   - Theorem 5.3, last clause: "a `Threshold` state is a function of the ordered closed windows of its key". By Def. 7.10 it is a function of the set of closed windows of the key, each with the value it had when it left the retained range, and of the frontier. The proof's "depends only on the sequence of input values" also omits the window starts that delimit episodes. Both are supports, so nothing changes but the wording.
   - Prop. 7.7 (c) as restated holds. A change of a support happens at an input, and an engine step makes at most one commit per graph per input (Def. 6.5). It inherits item 2 only through the Theorem 7.4 count it cites for rows that appear and go, which is stated per appearance and is therefore correct.

### G. Tests proposed by the third pass (not yet in the list of 17)

| Id | Name | Generates | Asserts | Backs |
|---|---|---|---|---|
| T-WINDOWED | `Data_cannot_be_derived_from_the_existence_of_windows_without_a_threshold` | projections that lead a row of a `Series`, a `Window` or a `Rollup` through `Map`, `Filter`, `Distinct`, `Group` or `Join` into a create emit or a data property | the checker refuses each, naming the operator (rule P6); the shipped definitions check only after the six rules of F13, item 9 are rewritten | Theorems 5.3 and 7.1, if P6 is adopted |
| T-NODATA-NEW-MEMBER | `A_roll_up_key_with_a_new_member_without_a_record_is_not_in_no_data` | a roll-up key with one member long silent and one member that appeared less than the time to live ago and has no record | engine and reference agree, and the key is in no data only if every member is | Def. 7.16, set B's roll-up rule |
| T-NODATA-RETURN | `A_subject_that_returns_is_not_in_no_data_before_its_time_to_live` | an element with an old record that leaves the structural input and returns | after the return the element is not in no data until the time to live has passed since its return | Def. 7.16 with the proposed `e(k)` |
| T-AMBKEY | `An_edit_of_an_element_with_two_keys_is_rejected_AmbiguousKey` | a lens over authored rows whose key domain has an equivalence, two rows whose keys are bound to one entity, then an update and a delete of that entity in the view | each edit is rejected `AmbiguousKey` and changes nothing; an element with one key is still accepted | Def. 8.4 and Theorem 8.2 (c), the accepted domain; engine-core builds the check (2026-11-12), not yet committed |
