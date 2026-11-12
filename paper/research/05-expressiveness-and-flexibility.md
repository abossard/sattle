# 05. Expressiveness and flexibility of the projection language

Status: research note, 11 November 2026. Claims with a status each: **proved** (argument here), **sketch + citation** (argument here, the hard step cited), **checked** (the definition passes `sattle check`), **run** (the definition was also replayed on the synthetic estate), **tested** (an engine test in the repo covers it), **open**. Companion: [04](04-compute-and-memory-efficiency.md) for cost; the fragment boundary of section 2 is the one agreed there (04, section 5).

Evidence base: the engine as built in the checkout (`engine/README.md`, `engine/src`, `engine/definitions`), the paper chapters 2, 5, 6, 8 at `76bdab2`, and checks run with the engine session's Release binary (built 11 Nov 13:13 from the working tree on top of `76bdab2`, engine turn 14) on a scratch copy of `engine/definitions` plus the files in [`05-suite/`](05-suite/). Rerun:

```bash
cp engine/definitions/*.sattle paper/research/05-suite/*.sattle "$SCRATCH"/ && dotnet engine/src/Sattle.Cli/bin/Release/net10.0/sattle.dll check --defs "$SCRATCH"
```

The replay of section 4 additionally binds the suite in a scratch copy of `acme.space` (lines in `05-suite/space-bindings.txt`, then `check --update-pins`) and runs `sattle replay engine/tests/Sattle.Tests/fixtures/estate --cluster synth --defs "$SCRATCH"`.

## 0. Findings

1. **As built, Sattle is a non-recursive relational language with aggregation, arithmetic, order and bounded recursion.** It captures relational algebra with aggregation (and so non-recursive Datalog with stratified negation) over data rows, conjunctive graph patterns, single-label bounded path queries and quotients. Every recursion has a static bound per definition: `Traverse` depth at most 64, `Fixpoint` at most 64 unrolled rounds, and nesting multiplies (two nested fixpoints cover 65 × 65 edges, checked). Unbounded reachability is not expressible: unconditionally for the fragment without `EntityId` and text order (locality), and under the standard conjecture NL ⊄ TC⁰ for the full language. One addition closes it: an unbounded `Traverse` (or a semi-naive `Fixpoint`).
2. **Time and telemetry are the narrowest part.** By design (P4) telemetry cannot be filtered, joined, grouped or ranked, and `Threshold` is the only crossing to structure. Independently of P4, an `Instant` cannot be shifted (`Add` on `Instant` is refused), so window-over-window comparison, sliding and hopping windows are not expressible; session windows, "no data for N windows" and cumulative counters (`Delta`) are not either. Each has a one-operator or one-function fix (section 1.5).
3. **Flexibility holds for data-shaped change and stops at the closed operator set.** A new ontology, signal kind, level of abstraction, output (graph, document, tree) or workload binding needs no engine change (evidence per claim in section 3). A new native source format, operator, function, aggregate or value kind does. Of the last two capability additions, both were engine changes (`Retain`, D25; `Lift`, `8540b5b`), and the `Lift` lowering shows that a catalog-level template mechanism would let most such additions be definitions instead.

Discrepancies found on the way (section 6): the engine allows `Traverse` depth 64 and `Fixpoint` 64 rounds where the paper says 16; the checker accepts any operator and arithmetic in a fixpoint step, so the admitted fragment of Theorem 6.5 is not enforced.

---

## 1. Expressiveness of the language as built

### 1.1 The object

The language `L` is the set of projections the checker accepts, with 20 operator kinds (`Match`, `Filter`, `Map`, `Join`, `Union`, `Distinct`, `Group`, `Rollup`, `Window`, `Series`, `Threshold`, `Retain`, `Values`, `Traverse`, `Fixpoint`, `Lift`, `EmitEntity`, `EmitRelationship`, `Link`, plus the declared but unbuilt `Delta`; ch. 5, Def. 2.2), parameters (Def. 5.14) and the closed function set of `Kernel.cs` (comparisons, Boolean connectives, `Add` to `Divide`, `Negate`, `ToDecimal`, `Concat`, `Coalesce`, `IsPresent`, `If`, `Rate`, `InList`, `LowerInvariant`, `Trim`, `EntityId`). For comparison with database theory a graph port is read as relations `Ent_T(x)`, `Rel_R(e, x, y)`, `Prop_p(x, v)`, and telemetry as `Tele_p(x, windowStart, v)`; a rows port is a relation of its row type; a projection maps these to the fragments of its sinks (ch. 6, Def. 6.4).

Three properties of `L` decide which theory applies:
- **Order and arithmetic.** `LessThan` and its relatives take `Integer`, `Decimal`, `Instant` and `Text`; `Add`, `Multiply`, `sum`, `count` give arithmetic. `EntityId : Entity → Text` (Kernel.cs) maps entities injectively to ordered text. So `L` is in the *ordered* setting of [Libkin], not the unordered one.
- **Value invention only at sinks.** `IdOf` mints ids (an injective function of key tuples), but fixpoint relations must be plain data rows ("a fixpoint relation is plain data rows", Checker.cs) and `IdOf` occurs only in emits, so invented values never feed a recursion inside one projection.
- **Bounded recursion.** `Traverse` has `0 ≤ min ≤ max ≤ 64` and `Fixpoint` has `1 ≤ maxIterations ≤ 64` (Checker.cs, lines 466 and 370 at the binary checked).

### 1.2 Upper bound: every definition is a relational query with aggregation

**Claim E1.** For every accepted projection `P` there is an expression of the ordered aggregate algebra `Alg<_aggr` of [Libkin] (relational algebra with grouping, aggregates and arithmetic, plus order comparisons on the base sort), extended by an injective function at the sinks, that denotes the same output fragments. The same holds for a graph space, a finite DAG of projections.
Status: **sketch + citation**.

*Sketch, operator by operator.* `Match` with a connected pattern is a select-join over `Ent`, `Rel`, `Prop` (a conjunctive query). `Filter`, `Map`, `Join`, `Union` (bag), `Distinct` are σ, generalized π with arithmetic, ⋈ with residual θ, ⊎, δ. `Group` and `Rollup` are γ with aggregates. `Window` is π with a bucketing term `floor(t / w) · w`, `Series` a selection on `Tele` with `windowStart ∈ [λ − within, λ)` for the recorded frontier `λ` (a constant of the input, ch. 6, Def. 6.8), `Retain` a selection `time + age > λ`, `Values` a constant relation. `Threshold` over an ordered sequence of closed windows of one key is first-order with order: the state at window `j` is `high` iff some `i ≤ j` ends a run of `enterAfter` consecutive windows with value `≥ above` and no `k ∈ (i, j]` ends a run of `exitAfter` windows with value `≤ below` (or `initial` is `high` and no exit run occurs up to `j`); "consecutive" and "ends a run of n" are definable with the order on `windowStart` for a fixed `n`. `Traverse` with depth `m..n` is the union over lengths `m..n` of path conjunctive queries. `Fixpoint` with `max k` is, by the engine's own unrolling (`Unroll`, Checker.cs), the `k`-fold composition `R_{i+1} = δ(R_i ∪ step(R_i))`. `Lift` is lowered to the operators above before evaluation (ch. 5, 5.8). Sinks project into the output schema; `IdOf` is the injective function. A composition of finitely many such expressions is one. ∎

**Consequence E1a.** Data complexity: for a fixed definition, evaluation on the integrated input is polynomial time, and every Boolean query it defines lies in P-uniform TC⁰ ([Libkin], Proposition 2, for `sql<_N`). Status: **sketch + citation**. Combined complexity (definition part of the input) is NP-hard already for `Match`, because evaluating conjunctive patterns is NP-complete ([Angles et al.], graph patterns; [AHV] ch. 4 and 6).

### 1.3 Lower bound: relational algebra with aggregation over data rows

**Claim E2.** Every relational algebra query (σ, π, ×, ∪, −, ρ) over data relations of a projection (rows ports, `Match` results, `Values`) is expressible in `L`, and so are grouping and the aggregates `count`, `sum`, `min`, `max`, `mean` over numeric data. By the equivalence theorem ([AHV] ch. 5, Theorem 5.3.10) this includes the domain-independent relational calculus and non-recursive Datalog with negation.
Status: **proved** by construction below, **checked** on examples, **run** on the estate.

| RA operator | Construction in `L` | Example (05-suite) |
|---|---|---|
| σ | `\|> where` | every suite projection |
| π with duplicate elimination | `\|> select { … } \|> distinct` | `acme.tree` (`allLabels`) |
| ρ | `select { new: old }` | `topServices` |
| × | `join a, b on ka == kb` with a constant column on both sides | `topServices` |
| θ-join | equalities plus a residual predicate (`join a, b on ka == kb and nb > na`) | `topServices` |
| ∪ | `union a, b` (same columns in the same order) | `drift` |
| − | tag rows of `A` with `a: 1, b: 0` and of `B` with `a: 0, b: 1`, `union`, `group by` all columns of `A` with `sum` (or `max`) of each tag, `where a > 0 and b == 0` | `drift`, `orphans`, `required` |
| γ, aggregates | `group by { … } aggregate { … }` | `topServices` |

*Proof of −.* For a set `A` and a set `B` over the same columns, the group of a row `r` has tag sums `(1, 1)` if `r ∈ A ∩ B`, `(1, 0)` if `r ∈ A \ B`, `(0, 1)` if `r ∈ B \ A`; the filter keeps exactly `A \ B`. Z-set weights do not change the result because the filter tests positivity of sums of non-negative tags. ∎

Restrictions observed by the checker: `min` and `max` take numeric arguments only (Checker.cs), so the extremes of a `Text` column need the rank construction; `union` needs the same column order, and `values` orders its columns by name, so a `select` has to reorder (checker message `Union: union inputs must have the same columns`). **The capture holds for data rows only.** Telemetry fields cannot enter σ, ⋈, γ keys or ranking (P4, section 1.5), so relational algebra over measures is not in `L` by design.

### 1.4 Graph patterns and path queries

**Claim E3 (conjunctive graph patterns).** Every conjunctive pattern over node and edge facts with homomorphism semantics is expressible: a connected pattern is one `Match`; components are combined by `Join` on a constant or on `EntityId`; injective (isomorphism) semantics adds `where x != y` per pair. Status: **proved** by construction, **checked** (`blastRadius`, `drift`).

**Claim E4 (path queries).** `Traverse g via R out|in|either depth m .. n from x as d` is the single-label bounded two-way path query `R^{m..n}` (and its inverse) with `n ≤ 64`. A regular path query with several labels and a length bound `N` is expressible as a finite union of conjunctive patterns of length at most `N` (size exponential in `N` for alternation under a star). Unbounded regular path queries are not expressible (E7). `Match` has no quantified paths: `-[:R]->{1,3}` is a syntax error ("quantified path patterns is not supported in this engine slice", `limit7`).
Status: **proved** (construction and bound), **checked**. `Traverse` also requires the reached end to be one entity type (Checker.cs: "the reached type must be one entity type").

One addition closes E4 and the unbounded case at once: quantified path patterns in `Match` evaluated by a product of the graph with the pattern automaton, which answers reachability under a regular expression in `O(|G| · |L|)` per source ([Angles et al.], path queries).

### 1.5 Recursion: Datalog

**Claim E5.** Non-recursive Datalog with stratified negation and aggregation over data relations is captured (E2 with the equivalence theorem; negation is the − construction, which is a stratum boundary because it goes through `Group`). Status: **proved**.

**Claim E6 (what `Fixpoint` is).** `fixpoint seed via step max k` denotes `R_k` with `R_0 = δ(seed)` and `R_{i+1} = δ(R_i ∪ step(R_i))`, `k ≤ 64`. This is *bounded inflationary* iteration ([AHV] ch. 14 for inflationary semantics): the step may contain any operator, including `Group` and a filter on an aggregate (non-monotone, `nonMonotone`, checked); arithmetic on a column (`limit9`) was accepted and is refused since rule F5 became a checker rule (section 6). For a monotone step over a finite domain and `k` at least the derivation depth, `R_k` is the least fixpoint (ch. 6, Theorem 6.5). Fixpoints nest and their bounds multiply: a second fixpoint seeded by the first and stepping by whole paths of the first covers paths of up to `65 × 65` edges (`nested`, checked and run). Status: **proved** from the unrolling code; **checked**, **run**.

There is no squaring trick: a step reads its relation once (a `let` that reads the fixpoint before it is defined is `UnknownName`), so one fixpoint extends derivations by one step per round.

**Claim E7 (reachability is not expressible).** There is no projection in `L` that computes the transitive closure of one relationship type on every finite graph.
- (a) Unconditionally for `L⁻`, the language without `EntityId` and without order comparisons on `Text`: on graphs whose nodes carry no data properties, `L⁻` lies in the unordered `Alg_aggr` (E1 without order), whose queries are local, and reachability, connectivity and acyclicity are not local ([Libkin], locality theorem and section 9). Status: **sketch + citation**.
- (b) For `L` itself, conditionally: if some projection computed directed reachability, reachability would be in P-uniform TC⁰ (E1a), and since directed reachability is NL-complete this would put NL inside TC⁰, against the standard conjecture. A proof without the conjecture is out of reach: [Libkin] (Corollary 1) shows that proving reachability inexpressible in `sql<_N` would separate uniform TC⁰ from NLOGSPACE. Status: **sketch + citation**, conditional.
- (c) Operationally and unconditionally: every definition `P` has a static number `B(P)` (product of the nested fixpoint bounds times the traversal depths along a derivation) such that `P` computes reachability exactly on graphs whose relevant paths are no longer than `B(P)`. On the estate `ancestors` and `nested` give the same closure (2 pairs over a three-node hierarchy, run). Status: **proved** from E6, **run**.

The identity catalog is the one place where the system computes an unbounded closure: `Link` merges are closed under transitivity by the kernel (ch. 1, Theorem 1.7), not by the language.

### 1.6 Time, windows and streams

Against the Dataflow model ([Akidau et al.]: event time against processing time, watermarks, fixed, sliding and session windows, discarding, accumulating, and accumulating-and-retracting refinements):

**Claim E8 (captured).** Event-time tumbling windows aligned to the epoch (`Window`, telemetry `period`); a watermark that is a recorded input (`Advance(λ)`, so results are deterministic, ch. 6, Theorem 6.3); late policies `ignore`, `reject` and `revise`, the last being accumulating-and-retracting (a revised window is a retraction plus an insertion, ch. 6, Def. 6.7); time-to-live of facts (`Retain`, ch. 5, Prop. 5.9); hysteresis over closed windows (`Threshold`); roll-up with a declared decomposable combine (`Rollup`, ch. 7). Status: **tested** (the paper's tests for each, ch. 5 to 7).

**Claim E9 (not expressible today, checked).**

| Task | Why not | Diagnostic or reason | One addition |
|---|---|---|---|
| window-over-window (this window against the previous, or yesterday's) | an `Instant` cannot be shifted | `limit1`: `E-Call: Add needs two numbers of one kind and unit, found Instant and Integer` | builtin `shift(instant, duration)` that keeps `W(R)` when the shift is a multiple of the period |
| sliding or hopping windows (5-minute moving sum over 1-minute windows) | `Window` has a width and no hop; no shift to fan a window into the k windows it belongs to | same as above | a `hop` parameter on `Window` and `Rollup`, or `shift` plus `Values` of offsets |
| session windows (bursts separated by a gap) | window boundaries depend on data; no operator segments by gaps | no construct | a per-key `Session(gap)` operator, a state machine like `Threshold` |
| "no data for N windows" (staleness, Unknown with a reason) | a missing window is no row; `Series` and `Threshold` see only windows that exist | README Example 4: "Unknown is no row" | `series … fill` that emits an absent marker for every closed window without a sample |
| cumulative counters (Prometheus counters) | `Delta` is declared and not built; `Rollup` refuses `cumulative` | `limit5`: `step 'delta' is not supported in this engine slice` | build `Delta` (designed, ch. 6, Def. 6.7) |
| filter, join, group key or rank on a measure (top talkers) | P4 | `limit2` (`Filter: filter predicate must be (Boolean, req, data)`), `limit3` and `limit10` (`Join: join takes data rows`), `limit6` (`Aggregate: group aggregates take data; use rollup`) | a telemetry `TopK` whose output stays telemetry (section 5, D4) |
| percentiles and histograms of a measure | no histogram kind, no percentile aggregate | Def. 5.6 | a histogram value kind with a mergeable sketch (OTel `ExponentialHistogram`, research 03 Q15) |
| group, join or filter by an identity computed from a key in the same projection (`select { p: podName(name: src) } |> group by { p: p }`) | rule IdOf-in-sinks (engine-core, 2026-11-12): an identity expression stands only in the identity or the endpoints of an emit and in a link. A row that carried an identity into another operator would keep it after a later link merges that identity, and the group would stay split (Law 7.5 item 3 would fail); before the rule the checker accepted it and the engine refused to build the plan | `IdentityOutsideSink: an identity expression stands only in the identity or the endpoints of an emit and in a link …; to group, join or filter by an element, emit it and read it through a match on a view of the graph` | none needed: emit the element in the ingestion, then `match` it in a view and group by the matched element; a merge reaches the view as a patch and its group re-keys (differential world "a view grouped by a pod that a link merges") |

Over **data** rows the rank construction of E2 gives top-k and percentiles (quadratically, `topServices`, run: ranks 0, 0 and 2 for the three estate services). The gap is specific to measures.

### 1.7 Graph transformation and quotients

**Claim E10 (quotients).** A quotient of nodes by a key function is `Group` plus `IdOf` (the service group of a namespace, `acme.svc`); a quotient of edges by a partial node map is `Lift` (ch. 5, Propositions 5.10 to 5.12, proved there); quotients compose (Proposition 5.12). A quotient by an equivalence that is not a key function, for example connected components or strongly connected components as nodes, needs reachability and is bounded by E7. Status: **proved** (ch. 5), **tested** (`LiftTests`, `ProjectionEdgesTests`).

**Claim E11 (rewriting).** One parallel step of a set of non-overlapping rewrite rules (match a left side, delete, add a right side) is expressible as a derived graph: copy the unmatched elements (the − construction of E2 against the matches), emit the right sides with `IdOf` for new nodes. Iterated rewriting to a normal form is not: iteration is bounded and cannot invent nodes (no `IdOf` in a fixpoint). A graph never rewrites its own input: graph production is a DAG and the master is written only by ingestion (ch. 5, Def. 5.8, S3). Status: **sketch**.

### 1.8 Summary

| Class | Captured by | Status | Not captured | Closing addition |
|---|---|---|---|---|
| Relational algebra with aggregation (data rows) | `Filter`, `Map`, `Join`, `Union`, `Distinct`, `Group`, tag-and-group difference | proved, run | the same over measures (by P4) | none needed for data; `TopK` for measures |
| Conjunctive graph patterns | `Match`, `Join` | proved, checked | | |
| Regular path queries | `Traverse` (one label, depth ≤ 64), unions of patterns | proved, checked | unbounded; several labels with a star | quantified paths in `Match`, unbounded `Traverse` |
| Datalog with stratified negation and aggregation | non-recursive: all; recursive: up to `B(P)` | proved, sketch + citation | unbounded recursion (E7) | semi-naive `Fixpoint` with a monotonicity check |
| Stream algebra (Dataflow) | tumbling event-time windows, recorded watermark, late policies, TTL, hysteresis, decomposable roll-up | tested | shift, sliding, session, absence, cumulative, histograms | `shift`, `hop`, `Session`, `fill`, `Delta`, histogram kind |
| Graph quotients and rewriting | `Group` + `IdOf`, `Lift`; one rewriting step | proved (ch. 5), sketch | quotients by reachability; rewriting to normal form | as for reachability |

---

## 2. The cost of expressiveness

The fragments are those of 04, section 5 (agreed with that session). Cost measures: work per incremental step is input rows plus produced rows, so `Join` and edge `Match` cost `O((|Δin| + |Δout|) · polylog)` with `|Δout| ≤ |Δ| · degree` (polylog in `|Δin|` alone is false on hubs); state is bytes per entry of the indexed integrated input; telemetry operators (`Group` over `Window`, `Series`, `Rollup`, `Threshold`) are budgeted per window close, `O(active keys)`, independent of `|Δ|`.
- **C**: linear operators (no state), bilinear operators (both inputs indexed), per-key accumulators (`count`, `sum`, `mean`, `weightedMean`, and `any`/`all` as two counters per key), `Threshold`, `Retain` (expiry `O(expired rows)` amortized through a time-ordered queue), emits, `Link` (resolution `O(1)` amortized; a merge costs `O(degree of the loser)` to re-key its relationships), `Series` (state: closed windows inside `within`; windows older than the hot period are read from disk, a cost per read and not per update), `Lift` as lowered.
- **C+**: `min`, `max`, `last` with a per-key sorted multiset, state `O(rows per key)` and `O(log m)` per update, no recompute on retraction while the multiset is kept; `countDistinct` and `Distinct`, a count per distinct value, `O(1)` amortized, state `O(distinct values)`.
- **C-tree**: `Traverse` over a relationship declared `hierarchy` (at most one parent, checked) with a decomposable aggregate (worst or max through a per-node multiset, sum, count): ancestor aggregation, `O(depth)` per change, state `O(nodes)`; in C when the depth is bounded by the tree (about 8 on the estate tree).
- **outside**: general `Traverse` and `Fixpoint`, with declared bounds and work budgets.

Measured numbers and per-kind budgets are 04's and `engine/PERF.md`'s; this section only maps expressiveness onto them.

**Claim C1.** Everything in section 1 that is *not* recursion stays in C or C+. Relational algebra with `count`, `sum`, `any` and `all` (including the difference construction with `sum` tags), conjunctive patterns of fixed size (output-sensitive, so their cost grows with the degree of the matched nodes), `Lift`, `Series`, `Rollup`, `Threshold`, `Retain` are C; aggregates by extremes, `countDistinct` and `Distinct` are C+. Status: **proved** by the classification of 04, section 5, applied to the constructions of section 1 (the difference construction is in C when its tags are summed and in C+ when it uses `max`; `drift` uses `max` and should use `sum`).

**Claim C2.** Recursion is where cost and expressiveness trade. A fixpoint with bound `k` costs `k` copies of its step in state and work (the unrolling clones the step per round); nesting multiplies the reach (`65^m` for `m` levels) and adds the costs; a general `Traverse` of depth `d` costs up to `fanout^d` per touched start. The one recursive shape with a cheap incremental form is the ancestor walk of a hierarchy (C-tree). Status: **proved** from the unrolling code and 04.

**Claim C3.** Evaluation complexity: data complexity polynomial and, for Boolean queries, within P-uniform TC⁰ (E1a); combined complexity NP-hard (patterns). Status: **sketch + citation**.

**Claim C4 (checker).** Checking a module is decidable and syntax-directed with a bounded number of lookups per definition element (ch. 5, Theorem 5.1); `Lift` lowering is linear in the statement and `Fixpoint` unrolling multiplies its step by `k ≤ 64`. Status: **proved** (ch. 5) plus the unrolling bound from the code.

**Claim C5 (what no checker can decide).** Whether two definitions have equal outputs, or whether two rules can emit the same element, is undecidable for `L`: `L` contains relational algebra with difference (E2), and satisfiability of relational calculus queries is undecidable ([AHV] ch. 6, Theorem 6.3.1). For difference-free fragments it is NP-complete (homomorphism theorem, [AHV] ch. 6). Consequence: static guarantees about overlapping outputs must be syntactic (ownership P2, the refactoring rejections of ch. 5, 5.7) or runtime (conflicts fail at commit, Def. 6.4). Condition (3) of Proposition 5.7 cannot become a complete checker rule. Status: **proved** by reduction from the cited theorem (a definition whose sink fires iff a calculus query is satisfiable).

---

## 3. Flexibility as claims

Each claim says what changes, where, and the evidence that no engine change was needed.

| # | Change | Needs | Evidence |
|---|---|---|---|
| F1 | a new ontology (types, properties, constraints, identity domains, presentation roles) | a module in the catalog; re-activation | README "What exists": "Changing a definition needs no rebuild"; `POST /definitions/{module}`; Example 5 audit: "Nothing about health, states or thresholds is in compiled code" |
| F2 | a new source of a kind the engine reads (another cluster, subscription, namespace) | a `source` line and a binding | `acme.space` binds 19 sources to existing adapters; adapters "declare a row-contract shape, not a digest, so a definition change needs no rebuild" (README, Deviations) |
| F2' | a new native source format | **engine change**: a compiled adapter | `Sattle.Runtime/Adapters.cs`; the live input reads one hub (README, Not done) |
| F3 | a new signal kind, threshold, severity, roll-up policy or curation | an authored record | README turn 14: "A new signal kind, a changed threshold or severity is a record; no statement changes" (`HealthTreeTests`) |
| F4 | a new level of abstraction (services to capabilities) | an ontology plus `emit entity` and one `lift` | `acme.biz` rule `lifted` (7 lines); composition, ch. 5, Proposition 5.12; `LiftTests` |
| F5 | a new output: graph, document, tree | a projection into any ontology; `sattle.doc` blocks; a relationship marked `hierarchy` | `acme.report`, `acme.treereport` (one generic renderer, README "Document projections"); `acme.tree` (`Contains … hierarchy`) |
| F6 | the same model for another workload | parameters bound per graph | `acme.healthmodel` (five `param` ports), `acme.deployhealth` (`minReady`); Proposition 5.8 tests |
| F7 | an editable view | a `lens` rule with edit policies | `acme.biz` `capabilities` and `tree` rules; but the backward path exists only for "one entity sink and one relationship sink over one authored rows port" (README, "The host"); other shapes are `NotEditable` |

**Claim F8 (what needs an engine change).** A new operator kind, builtin function, aggregate, canonicalizer, value kind, rule class (`sync`), `Union` policy, adapter, or a new form of an existing operator. Status: **proved** by the closed language (ch. 5, Proposition 5.4: the function set is fixed at engine release) and **evidence from history**: the two most recent capability additions were engine changes, `Retain` (ch. 5, D25) and `Lift` (`8540b5b`), and so was the turn-14 form of `Threshold` with parameters read from row columns (README turn 14: "a new language form").

**Claim F9 (operators defined in the language).** Today no operator can be defined in `.sattle`: there are no user functions, macros or templates. The mechanism exists inside the engine: `Lift` is lowered by generating `.sattle` text that the ordinary front end parses (README, "Edge lifting"), and everything proved for the lowered operators then holds for the statement (ch. 5, 5.8). A catalog-level **template** (a kernel node with typed ports and parameters, expanded at check time) would make `Lift`-like abstractions definitions. It cannot add evaluation semantics: unbounded recursion, sessions, absence detection and `Delta` need operator kinds. Status: **open**; smallest experiment: express `Lift` as a catalog template and run `LiftTests` against it.

---

## 4. Capability suite

Status words: **run** (checked and replayed on the estate, result given), **checked**, **in repo** (an existing definition does it, tested by the named test), **bounded** (expressible up to a static bound), **system** (offered by the engine or protocol, not by the language), **no** (not expressible, reason and diagnostic).

| # | Task | Status | How | Evidence |
|---|---|---|---|---|
| 1 | Reachability and impact (who depends on `db`) | run, bounded 64 | `Traverse … in depth 1 .. 16` | `impact`: `web`, `api`, `db`; hops 1, 1, 2 |
| 2 | Blast radius of a node across two graphs | run, bounded | `Match` node → pods → services, `Traverse` callers in the service tree, `Join` on `EntityId` | `blastRadius`: 5 entities, 5 edges |
| 3 | Roll-up with policies (worst, share, ignore) | in repo | `acme.treehealth` | `HealthTreeTests` (27 cases) |
| 4 | Drift between declared and observed | run | tag-and-group difference over `Realizes` and `Backs` | `drift`: 0 on the estate (every authored service runs) |
| 5 | Negation: pods that back no service | run | difference | `orphans`: 0 at the end (graph revision 3) |
| 6 | Required items missing (constant list against data) | run | `values` + difference + parameter | `required`: `payments`, `checkout` |
| 7 | Anomaly thresholds with hysteresis, thresholds as data | in repo | `Threshold` with row-valued parameters | `acme.tree` `treeSignals`, `HealthTreeTests` |
| 8 | Quotient to a higher level, edges included | in repo | `lift` | `acme.svc`, `acme.biz`; `LiftTests` |
| 9 | Join of two sources on identity (Kubernetes node and Azure VM) | in repo | two sinks into one identity domain; `Link` equivalence for pod uid and name | `acme.healthmodel`, `acme.ops`; `IdentityMergeTests` |
| 10 | Correlation of two measures of one element per window | run | `Series` of two properties, `Rollup`, derived ratio | `correlate`: 2 nodes, 20 samples |
| 11 | Top-k by a data value | run | rank by θ-join and count | `topServices`: ranks 0, 0, 2 |
| 12 | Top-k by traffic | no | P4 | `limit3`, `limit10` |
| 13 | Cycle detection | run, bounded 64 | `Traverse out` with `where d == x` | `cycles`: none on the estate |
| 14 | Shortest hop distance | run, bounded 64 | `Traverse` then `min(d_depth)` | `distance`: 1, 1, 2 |
| 15 | Transitive closure of a hierarchy | run, bounded `65^m` | `Fixpoint` (`ancestors`), nested (`nested`) | both: 2 pairs |
| 16 | Unbounded reachability | no | E7 | `limit4` with depth 65: `Traverse: depth must satisfy 0 <= min <= max <= 64` |
| 17 | Regular path query with a quantifier | no | no quantified paths | `limit7` |
| 18 | Window-over-window comparison, sliding window | no | no `Instant` arithmetic, no hop | `limit1` |
| 19 | No data for N windows | no | absent windows are no rows | README Example 4 |
| 20 | Rate of a cumulative counter | no | `Delta` not built | `limit5` |
| 21 | Time travel: compare two revisions | system | a projection reads current inputs only; revision-addressed reads and patches between revisions are the change algebra (ch. 4) and the protocol | `PROTOCOL.md` |
| 22 | What-if on a draft definition | system | drafts, preview, undo by inverse patches | README "What exists", `DraftTests` |
| 23 | Bidirectional edit through a lens | in repo | `lens` rules with edit policies | `acme.biz`; ch. 8 tests |
| 24 | Document and tree outputs | in repo | `sattle.doc` blocks; `hierarchy` | `acme.report`, `acme.treereport`, `acme.tree` |
| 25 | One model, many workloads | in repo | parameters | `acme.healthmodel`, `DeployHealthTests` |
| 26 | Provenance of a result | system | lineage per sink row | `LineageTests` |

Definitions of the run and checked tasks are in [`05-suite/research.suite.sattle`](05-suite/research.suite.sattle), the refusals in `05-suite/research.limit1` to `limit10`. The two definitions that show the main constructions:

```text
/// difference by tagging and grouping (drift)
let declared = match master (s: infra.Service)-[:ops.Realizes]->(c: ops.Capability) |> select { sid: entityId(s), dec: 1, obs: 0 };
let observed = match master (p: infra.Pod)-[:infra.Backs]->(s: infra.Service) |> select { sid: entityId(s), dec: 0, obs: 1 };
let both = union declared, observed |> group by { sid: sid } aggregate { dec: max(dec), obs: max(obs) };
let declaredOnly = both |> where dec == 1 and obs == 0;

/// nested bounded fixpoints: paths of up to 65 * 65 edges
let step1 = edges |> select { a2: a, b2: b };
let level1 = fixpoint edges via join level1, step1 on b == a2 |> select { a: a, b: b2 } max 64;
let step2 = level1 |> select { a2: a, b2: b };
let level2 = fixpoint level1 via join level2, step2 on b == a2 |> select { a: a, b: b2 } max 64;
```

Estate run: state digest `sha256:946d3a46…67ca` with the suite bound (the space differs from the tracked one, so the digest is not comparable with the paper's).

---

## 5. Decisions for the user

**D1. Recursion: bounded unrolling or a true fixpoint?**
- (a) Keep bounded `Traverse` and `Fixpoint` (today: 64 each, nestable). Predictable cost, reachability up to a declared bound.
- (b) Add unbounded single-label reachability (`Traverse … depth 0 .. *`), evaluated by an index per relationship type, in C-tree for hierarchies and outside otherwise.
- (c) Add a semi-naive `Fixpoint` with a checked monotone body and delete-and-rederive on retraction (ch. 6, Theorem 6.6).
- Recommendation: **(b) now, (c) as research.** (b) covers impact, blast radius, ancestors and cycles, which are the reachability tasks of the suite; (c) is the general answer but its retraction cost is open (O6.4). In either case make the checker enforce the admitted body of Theorem 6.5 (section 6).

**D2. Are `sync` rules in the research scope?**
- (a) In Phase 1: build the TGG fragment of ch. 8 and prove correctness and completeness on it.
- (b) Out of Phase 1: `lens` and `bijective` cover every edit the prototype needs; `sync` stays a designed class.
- Recommendation: **(b).** No graph of the live health model has information owned by the target side, which is the only case `sync` exists for (research 03, section 4.1).

**D3. Time arithmetic and window forms.**
- (a) Add `shift(instant, duration)` and a `hop` on windows (sliding windows, window-over-window), plus `series … fill` for absent windows.
- (b) Also add session windows.
- (c) Leave time as it is.
- Recommendation: **(a).** Each is a per-key state or a pure function, so it stays in C; "no data" is the reason the health model reports Unknown without a cause. Sessions (b) have no task in the suite that needs them yet.

**D4. Ranking measures (top talkers) under P4.**
- (a) Keep P4 strict and add a telemetry `TopK` whose output stays telemetry (no graph churn).
- (b) Allow closed-window measures into `Group` and `Filter` as data, marked as derived from telemetry.
- Recommendation: **(a).** (b) would make graph diffs depend on traffic in general, which Theorem 5.3 exists to prevent.

**D5. Operators defined in the language.**
- (a) Catalog templates expanded at check time, as `Lift` is.
- (b) Keep every abstraction an engine release.
- Recommendation: **(a)**, as a research item with the experiment of F9.

**D6. Order on entities.** `EntityId` makes entity ids ordered text, so queries need not be invariant under renaming of ids and the unordered inexpressibility proofs do not apply (E7). Keep it (cross-graph joins need it) and state it, or restrict its result to equality?
- Recommendation: **keep it and state it**; the results stay deterministic because ids are (ch. 1).

---

## 6. Discrepancies for the paper and engine sessions

- `Traverse` depth: Checker.cs allows `max ≤ 64`; ch. 5 Def. 5.3 and the syntax table say 16. `Fixpoint.maxIterations ≤ 64` is in the code and not in the paper.
- Fixpoint body: the checker accepts any operator in the step (a `Group` with a filter on its count, `nonMonotone`). The admitted fragment of Theorem 6.5 (operator set) is not a checker rule; with bounded unrolling the semantics is still defined (inflationary, E6), but Theorem 6.5's least-fixpoint reading applies only to definitions that keep to the fragment. F5 is a checker rule since engine commit fc88d37: a step that computes a value is refused (code `Fixpoint`, "(F5)"), so `limit9` is now refused rather than checked, and `replay` reports a fixpoint whose last step still added facts.
- `union` compares column order, and `values` sorts its columns by name; a union of a `values` with a `select` needs the `select` in name order.
- `min` and `max` in `Group` take numeric arguments only.

---

## Sources

Opened on 11 November 2026 unless noted:
- [AHV] S. Abiteboul, R. Hull, V. Vianu, *Foundations of Databases*, chapters [4](http://webdam.di.ens.fr/Alice/pdfs/Chapter-4.pdf) (conjunctive queries), [5](http://webdam.di.ens.fr/Alice/pdfs/Chapter-5.pdf) (Theorem 5.3.10, equivalence of algebra, calculus and non-recursive Datalog with negation), [6](http://webdam.di.ens.fr/Alice/pdfs/Chapter-6.pdf) (homomorphism theorem; Theorem 6.3.1, satisfiability of calculus queries undecidable), [12](http://webdam.di.ens.fr/Alice/pdfs/Chapter-12.pdf) (Datalog, polynomial evaluation, monotonicity), [15](http://webdam.di.ens.fr/Alice/pdfs/Chapter-15.pdf) (stratified negation), [16](http://webdam.di.ens.fr/Alice/pdfs/Chapter-16.pdf) (data and expression complexity), [17](http://webdam.di.ens.fr/Alice/pdfs/Chapter-17.pdf) (transitive closure not first-order; fixpoint captures PTIME on ordered databases). Chapter 14 (inflationary semantics) is cited by its title in the [table of contents](http://webdam.di.ens.fr/Alice/) and was not opened.
- [Libkin] L. Libkin, [Expressive power of SQL](https://homepages.inf.ed.ac.uk/libkin/papers/tcs-icdt01.pdf), Theoretical Computer Science, journal version of the ICDT 2001 paper (preprint opened): unordered SQL with aggregates is local and cannot express reachability, connectivity or acyclicity; with order, Proposition 2 (P-uniform TC⁰) and Corollary 1 (inexpressibility would separate TC⁰ from NLOGSPACE).
- [Angles et al.] R. Angles, M. Arenas, P. Barceló, A. Hogan, J. Reutter, D. Vrgoč, [Foundations of Modern Query Languages for Graph Databases](https://arxiv.org/abs/1610.06264), ACM Computing Surveys 2017: conjunctive graph patterns NP-complete, regular path queries, linear-time evaluation by automata.
- [Akidau et al.] T. Akidau et al., [The Dataflow Model](https://www.vldb.org/pvldb/vol8/p1792-Akidau.pdf), PVLDB 8(12), 2015: event time, watermarks, fixed, sliding and session windows.
- DBSP, OpenTelemetry metrics and the lens and TGG literature as cited in [03](03-projection-language-and-metagraph.md) and ch. 6 to 8.
- Not opened: Arasu, Babu, Widom, *The CQL continuous query language* (VLDB Journal 2006); every URL tried was paywalled or gone, so CQL is not cited.

Repository: `engine/README.md`, `engine/src/Sattle.Core/Kernel.cs` (functions), `engine/src/Sattle.Language/Checker.cs` (bounds, `Unroll`, aggregate rules), `engine/definitions/*.sattle`, `paper/ch02`, `ch05`, `ch06`, `ch08`, [04](04-compute-and-memory-efficiency.md) section 5.
