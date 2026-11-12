# Ch. 13 Performance

**Question.** How fast must the engine be, how much memory may it use, and how is a claim about speed decided rather than asserted?

**What you can state at the end.** The target of gate 1c is a budget of memory, work and stored bytes per node, per edge and per measurement, met at one million master nodes on one machine; the budgets themselves are not stated yet. Before that target existed, fourteen hypotheses H1 to H14 were written against an estate of 750,000 entities, each with a benchmark and a rule that refutes it. A status is decided by a written rule and holds only for a named configuration: a store, a machine and its load. By that rule three hypotheses are supported, each for one configuration; one has no status for the store it names; the rest are met in one run, or not met, or not measured. Four cost statements are proved: a join costs in proportion to what it produces; grouping commits lowers the cost per commit; a chain of stages runs at most at the rate of its slowest stage; and latency budgets of stacked steps add up by a union bound.

This chapter uses the log of versions (Ch. 3), the size of patches (Ch. 4), the evaluation step by step (Ch. 6), closed windows and thresholds (Ch. 7), and the stores, the estimate of a large estate and the hot graph of Ch. 11.

**Running example.** The shop's master graph receives the records of its cluster. How many records per second can it accept? How long after a record arrives is the new version there? How long does the service graph take to follow? How much memory does it take to hold everything?

## 13.1 The target of gate 1c, and what the hypotheses are

**The target.** Gate 1c of the research plan is a budget per node, per edge and per measurement, met at 1 million master nodes on one machine, with persistence that does not depend on one store and that scales.

**The budgets are not stated yet.** They come from a study of compute and memory efficiency that is not finished. This table is their place.

| Per | Memory | Work per change | Stored bytes |
|---|---|---|---|
| master node | not stated | not stated | not stated |
| master edge | not stated | not stated | not stated |
| measurement | not stated | not stated | not stated |

**What H1 to H14 are.** They were written before the gate, against a reference estate of about 750,000 entities (Ch. 11, 11.4), a machine of 8 cores and 32 GB, and Cosmos DB as the store. They and their measurements stay as the record against that estate. None of them is a budget per element.

| Hypothesis | Carries over to gate 1c | Why |
|---|---|---|
| H1 structural ingest, H2 ingest of measurements | yes, to be measured again at 1 million master nodes | rates of the engine, independent of the store |
| H3 time to commit | not as stated | it names Cosmos DB; under the gate it becomes a time per store, and no store is the target |
| H4 time per step of projection, H12 from measurement to property | yes | figures of the engine; already above target at 240,000 pairs (13.6) |
| H5 from source to graph | the engine's part yes, the store's part per store | supported on memory and SQLite at the scale of the recordings only |
| H6 reads of the hot graph, H8 view of an element | yes | figures of the engine; the sizes they name are those of the old estate |
| H7 durable reads | not as stated | it names the Cosmos store; it becomes a read time per store |
| H9 memory | replaced | it bounds the hot graph only (about 100 bytes per element, met in one run), not the whole engine, which holds 5.4 GB on the estate run; the budgets replace it |
| H10 restart | yes | measured on SQLite, with nothing specific to a store |
| H11 time of a definition edit, H14 cost of a join | yes | figures of the engine, not measured |
| H13 delay before version rows are written | no | it measures a step that no store of the prototype has |

A hypothesis is neither a claim nor a result. 13.6 reports what the prototype measured against each.

## 13.2 Definitions

**Definition 13.1 (load).** Called the *load* here, to keep it apart from the workload of Ch. 7. `W = (E, ρ, Σ, f, d)` with
- `E`: the size of the estate (entities, relationships, projections);
- `ρ`: the rate of events per source (pages and changes of the Azure resource list, the Kubernetes watch, flows of the network observer, network logs, measurements);
- `Σ`: the number of series of measurements and their resolution;
- `f`: the share of inputs that change graph data;
- `d`: the depth of the stack, the longest path from the master in the graph space.
The reference load is the estimate of Ch. 11 (11.4), in which every input is an assumption.

**Definition 13.2 (metrics).**

| Id | Metric | Unit |
|---|---|---|
| M1 | rate of structural input accepted before the engine pushes back | records/s per graph space |
| M2 | rate of appending samples of measurements | samples/s per machine |
| M3 | time to commit: from an evaluated commit record to a stored one | ms, percentiles |
| M4 | time per step of projection: from a commit of the master to the derived commit | s, percentiles |
| M5 | from source to graph: from receipt by the engine to the version of the master | s, percentiles |
| M6 | from measurement to property: from the input that closes a window to the derived commit | s, percentiles |
| M7 | time of a read: one element, one step of adjacency, three steps, durable element, durable scan | µs, ms |
| M8 | time of the view of an element (Def. 7.9) | ms |
| M9 | memory per entity, relationship and point of a series; stored bytes per version | bytes |
| M10 | restart: load a seal and process the rest of the log | s |
| M11 | time of a definition edit | s |
| M12 | delay before version rows are written (Ch. 11) | s |

A *percentile* p99 is the value below which 99 percent of the measured times lie.

**Definition 13.3 (tier).** A tier is `(scale factor over the reference load, reference machine, table of targets)`. Tier 1 is the reference load. Targets exist for tier 1 only.

**Definition 13.4 (reference machine).** One machine of 8 cores and 32 GB per graph space (assumption), and a Cosmos DB account with throughput bought in advance for the log of the master (Ch. 11, 11.5). The exact machine types, regions, throughput and consistency are to be fixed at the first run and written into every result. **No result is valid for this reference without them.** No run so far was on it.

**Definition 13.5 (hypothesis).** `Hn = (target, benchmark Bn, rule of refutation)`. Its status is one of `hypothesis`, `supported`, `refuted`, `inconclusive` (13.5).

**Definition 13.6 (cost of one input).** For one accepted structural input:

`cost(input) = parse + identity lookup + Σ_ops cost_op(Δ) + commit / groupSize`

with `cost_op` from Fact 11.1, an identity lookup in constant time in the binding set, and `commit` one batch of the store per group of commits (Ch. 11).

## 13.3 Cost theorems

**Theorem 13.1 (a join costs in proportion to what it produces).** Let `a` and `b` be indexed by the join key, and let `d_a` and `d_b` be the largest numbers of rows of their integrals that share a key. One step of an incremental join on changes `Δa, Δb` costs `|Δa| + |Δb|` lookups in the indexes plus the number of rows produced, and the number of rows produced is at most `|Δa|·d_b + |Δb|·d_a + |Δa|·|Δb|`.

It is proved relative to the rule of [budiu2023] for bilinear operators, cited in Theorem 6.1: `(a ⋈ b)^Δ = Δa ⋈ Δb + z⁻¹(I(a)) ⋈ Δb + Δa ⋈ z⁻¹(I(b))`, where `z⁻¹` delays a stream by one position.

*Proof.* Each of the three terms is a join between a change and an indexed set. An index finds the matching rows of the other side with one lookup per row of the change, and listing the matches costs their number. For a row `y` of a change with key `κ`, the number of matches in `I(a)` is `deg_a(κ) ≤ d_a`. Summing gives the stated bounds. ∎

*Example:* a new call from a pod of `web` to `api` is one row of `Δa`. If `api` has 3 pods, the join with the `Backs` facts produces 3 rows at most. A change that touches a *hub*, an element with `d` edges, costs `Θ(d)` produced rows (Fact 11.2).

**Proposition 13.2 (grouping commits lowers the cost per commit).** Let a batch of the store with `n` versions cost `C(n) = α + β·n`, where `α` is the cost per batch and `β` the cost per version, for `n ≤ n_max = min(100, ⌊2 MB / size of a version⌋)` (S1, Ch. 11). Then the cost per version `β + α/n` decreases strictly in `n` and is smallest at `n_max`. The first version of a group waits until the group is full or a deadline passes, so the size of a group trades time against cost.

*Proof.* With `α > 0`, `β + α/n` decreases strictly in `n`. ∎ This is proved arithmetic on a model of cost; `α` and `β` are not measured, so whether the model fits a store is open.

**Theorem 13.3 (a chain of stages).** Consider stages `1 … m` (acceptance, evaluation, commit, each step of projection) with service rates `μ_i` records per second. With bounded queues that make their producers wait, the sustained rate is at most `min_i μ_i`. With unbounded queues, a stage whose arrival rate `λ_i` exceeds `μ_i` builds a backlog at a rate of at least `λ_i − μ_i`. A projection that processes its inputs one after another has `μ ≤ 1 / s̄`, where `s̄` is its mean service time per record or group.

*Proof.* What arrives either leaves or waits: the backlog of stage `i` changes at `λ_i` minus the rate of departure, which is at least `λ_i − μ_i`. With queues that make producers wait, a producer cannot go faster than its consumer leaves, so every stage runs at most at the slowest rate. One-at-a-time processing serves one record or group at a time, so its rate of departure is at most the reciprocal of the mean service time. ∎

*Consequence for H1.* 5,000 records per second through one evaluator that works one record at a time needs a mean service time of at most 200 µs per record, the share of commits included. More speed comes from many graphs (one per customer and cluster, Ch. 11), not from inside one projection.

**Theorem 13.4 (budgets of stacked steps add up).** Let `L = X₁ + … + X_d` with `P(X_j > q_j) ≤ ε_j`. Then `P(L > q₁ + … + q_d) ≤ ε₁ + … + ε_d`. Hence, for a stack of `d` steps: if every step meets its budget `q` at the quantile `1 − 0.01/d`, then `d·q` is a budget for the whole stack at p99; and if every step meets `q` only at p99, then `d·q` is guaranteed only at the quantile `1 − 0.01·d`.

*Proof.* If `L > Σ q_j`, then at least one `X_j > q_j`, since otherwise the sum would be at most `Σ q_j`. The union bound gives the claim. ∎

*Example:* take a stack of projections four deep. If each step meets 1 s at p99, the whole stack meets 4 s only at the 96th percentile. To have 4 s at p99, each step must meet 1 s at the quantile 0.9975.

**Claim 13.5 (cost of a read of the hot graph).** Take the layout of the hot graph designed in Ch. 11 (11.5): small integer handles from a table of interned ids, one column per property in fixed segments, and adjacency as a sorted base plus, per vertex, a log of changes that only grows, each entry with an interval of versions.
- (a) A read of one property of an element by its handle at the published base costs two array accesses, a segment and an offset. From an id it costs one lookup in the table first: a hash of the id, and an expected constant number of probes in a table whose load is bounded.
- (b) A read at a pinned version also inspects the entries of that element's log that are not yet merged into the base: `O(1 + ℓ_k)` for `ℓ_k` such entries.
- (c) A scan of the neighbours of a vertex `v` at a pinned version visits `deg_base(v) + L_v` entries, each tested against the pinned version in constant time, where `deg_base(v)` is the length of the slice of `v` in the base and `L_v` the length of its log. With the log kept below a merge threshold `θ`, that is `O(deg(v) + θ)`.

These are counts on a design, proved below. That the engine's hot graph has this layout, and what the constants are, is a hypothesis of gate 1c, settled by measurement and not by proof. A read is not constant time without a condition: an element edited `ℓ` times since the last merge, read at a version before those edits, costs `ℓ` tests of entries, which no constant bounds unless a merge threshold applies per element.

*Proof.* (a) A handle is a dense integer. A column lives in fixed segments of 2¹⁶ elements, so the value of handle `i` is at segment `⌊i / 2¹⁶⌋`, offset `i mod 2¹⁶`: two accesses. The table maps an id to its handle by hashing; with bounded load the expected number of probes is constant, and hashing reads the id once. (b) The base arrays never change once published, and a reader at version `r` filters the entries of the log by `from ≤ r < to`: the entries of the element are inspected once each. (c) The base is sorted by `(vertex, direction, type)`, so the edges of `v` are one contiguous slice, found by two offsets; each entry is tested once. The log of `v` is scanned once. ∎

## 13.4 Hypotheses

All values are hypotheses. "Refuted if" follows the rule of 13.5. The results are in 13.6.

| Id | Target (tier 1) | Benchmark | Refuted if |
|---|---|---|---|
| H1 structural ingest | at least 5,000 accepted records/s per graph space; 50 versions/s steady; a burst of 2,000 versions/s for 10 minutes is worked off within 10 minutes after it ends | B1: process recorded inputs at 1, 10 and 100 times their speed; rate of acceptance, versions/s, time to work off the backlog | the sustained rate is below 5,000 records/s, or working off takes longer than 10 minutes |
| H2 ingest of measurements | at least 200,000 samples/s per machine into the ring in memory; at least 4,000 summed flow records/s into the database for time series | B2: a generator of series with the shapes seen in the recordings | either rate below target |
| H3 time to commit | p99 at most 20 ms from evaluated to stored commit record | B3: a sweep of group sizes on a real Cosmos account | p99 above 20 ms at any group size that sustains H1 |
| H4 time per step of projection | p99 at most 1 s from a commit of the master to the derived commit for linear projections; at most 5 s for joins whose touched degrees are at most 1,000 | B4: a chain of 3 stacked projections over the input of B1; per-step histograms and the end-to-end percentile (Theorem 13.4) | p99 per step above target |
| H5 from source to graph | p99 at most 2 s from receipt by the engine to the version of the master; the delays of the sources are excluded (Fact 0.1) | B5: times at receipt by the adapter and at commit | p99 above 2 s |
| H6 reads of the hot graph | one element at most 1 µs; a scan of neighbours at most 0.5 µs + 5 ns per neighbour; three steps around 10,000 elements at most 20 ms | B6: micro-benchmarks on the hot graph at the size of Ch. 11 | any median above target |
| H7 durable reads | p99 at most 10 ms for one element from the Cosmos store; a scan of 100 neighbours at most 20 ms | B7: a load test of reads | p99 above target |
| H8 view of an element | the structure at `r` joined with 1 hour of 4 series for 10,000 elements in at most 100 ms, from memory | B8: queries of Def. 7.9 inside the hour kept in memory | above 100 ms |
| H9 memory | hot graph at most 400 MB for 750,000 entities and 2.5 million relationships, without the ring of measurements and the look-back of `Series` | B9: memory after loading, at scale factors 1, 4 and 16 | above 400 MB at tier 1 |
| H10 restart | load a seal and process 1 hour of log in at most 60 s | B10: stop and start the engine during B1 | above 60 s |
| H11 time of a definition edit | grows linearly in the number of retained inputs of the affected projection, with a measured slope | B11: definition edits at 1, 4 and 16 times the retained inputs | growth faster than linear |
| H12 from measurement to property | p99 at most 1 s from the input that closes a window to the derived commit, for a threshold; the same as a linear step of H4, because a threshold reads closed records once each, as a step of H4 does (as an operator it keeps state per key, Ch. 6) | B12: a threshold projection over series of latency, with and without hysteresis | p99 above 1 s |
| H13 delay before version rows | p99 at most 5 s at steady load | B13 | p99 above 5 s |
| H14 cost of a join against the degree of a hub | time per change grows linearly in the matched degree (Theorem 13.1), with a slope within a factor of 2 over degrees 10 to 100,000 | B14: synthetic hubs and the first ten real projections | slope outside the factor, or faster than linear |

Two comparisons have no target. One would run standard queries of streaming systems ([nexmark]) on this engine and on two incremental view engines, for the operators all three have. The other would run a standard stream of updates of a social network ([erling2015]) against the hot graph and two graph databases. Neither has been run, and no claim of superiority is made before results exist on fair hardware and settings.

## 13.5 Method

- **The harness is deterministic.** Generators have fixed seeds; inputs come from the recordings where possible; harness and results are kept with the project.
- **Runs.** A warm-up, then at least 10 independent runs per configuration. Reported: the median, p50, p99, the 95 percent confidence interval of the median across runs, and the variance.
- **Rule of decision.** A target of time or size is *supported* iff the reported statistic is at or below the target in every run, *refuted* iff the median run is above the target, and *inconclusive* otherwise. A target of rate is supported iff the lower bound of the 95 percent interval is at or above the target, refuted iff the upper bound is below it, and inconclusive otherwise.
- **Scope of a status.** The rule says nothing about the machine, and Def. 13.4 names a reference that no run used. A status is therefore stated for a *configuration*: a store, a machine and the load on it. The rule is applied to the runs of one configuration, and the status names it ("supported on SQLite on the laptop under load"). It is the status of the hypothesis itself only where the hypothesis names no store (H5 and H10 name none; H3 and H7 name Cosmos). For an upper bound on time, other load on the machine can only raise times, so a pass under load holds all the more for that configuration, while a failure under load is weak evidence. A `refuted` on the local emulator of Cosmos is reported as such and does not refute the hypothesis for a real account.
- **What is recorded.** The machine, the build of the engine and the load actually used.
- **Separate causes.** Benchmarks of the hot plane run without a store, so that the store does not enter H4, H6 and H8. B3 must run against a real Cosmos account, because the emulator does not count costs.

## 13.6 Results

**Setup.** One laptop with an Apple M1 Max processor (10 cores) and 64 GB of memory, a release build of the engine, the stores in memory and on SQLite and, for the Cosmos figures, the local emulator of Cosmos. One run per figure, except H5, H7, H8 and H10, which have ten runs per set. The machine always had other work running: its load average was between 14 and 115 in the ten-run sets, and 239 during one set of three restarts. **No run was on a machine without other load**, none on the reference of Def. 13.4, and none on a real cloud account. The ten-run sets were taken by the authors and in runs of the project that the authors did not repeat (Ch. 14, 14.1), at three successive states of the engine; the benchmarks were not repeated at later states.

| Id | Status under the rule of 13.5 | Measured |
|---|---|---|
| H1 | met in one run, not supported | about 67,000 records/s on the real recording (243,457 records in 3.6 s, with an early engine that read fewer kinds of record), in memory; 3.6 million flow rows/s at the size of the estate, generation of the rows included. Burst and working off not measured. The transport of the test environment delivers about 125 events/s (Ch. 10, 10.6) |
| H2 | met in one run, not supported | 7.84 million samples/s into the ring in memory. The part for the database of time series is not built |
| H3 | not tested on Cosmos; met locally | p99 0.079 ms in memory, 4.31 ms on SQLite |
| H4 | met in one run for two graphs at 5,000 pairs; not met in one run at 240,000 pairs; no stack of 3 steps | p99 62.3 ms; at 240,000 pairs the first window closes in 9.0 s and windows 1 to 3 in 1.5 to 1.7 s each (4 windows), above 1 s |
| H5 | **supported** for the engine's part, in memory and on SQLite, on the laptop under load: every run of every ten-run set at the last three measured states of the engine is at or below 2,000 ms. `hypothesis` for the Cosmos store | p99 of the records that make a version of the master: 1.11 ms in memory, 23.5 ms on SQLite (the authors' last set). The worst run of any set is 25.9 ms, about 77 times below the target. One single record took 1.75 s in one set on SQLite: a target for p99 does not bound one record. On the emulator: 0.11 to 0.13 s per version, a mean and not a p99 |
| H6 | met in one run for one element and for one step | 250 to 410 ns for one element and 230 to 270 ns for 6 neighbours with the second layout (two runs); 20 ns and 109 ns with the reference structure. Three steps and the size of 2.25 million relationships not measured |
| H7 | **no status** for a real account, which the hypothesis names. On the emulator two ten-run sets get different labels: `refuted` in the earlier set, `inconclusive` in the later | p99 for one element 22.5 ms in the first set, and 6.55 ms with a worst run of 31 ms in the second, against 10 ms. The path of the read is the same in both, so the emulator's figure follows the load of the machine it shares. SQLite: 0.05 ms, which is not a test of H7 |
| H8 | **supported** for the plane in memory at the head version, on the laptop under load: every run of every set is at or below 100 ms since the scan of windows stopped allocating memory | median 29.5 ms, range 16.7 to 41.1 ms (the authors' last set). The worst run of any set is 49.4 ms, half the target. Before the change: median 73.5 ms, with one run of ten at 305 ms, in which the collector of unused memory ran |
| H9 | met in one run for the hot graph, not supported | 337 MB against 400 MB, with the second layout; the reference structure took 2,395 MB. The memory of the whole engine at the size of the estate is 2.5 GB after the structure and 5.4 GB at the end, outside the scope of H9 |
| H10 | **supported** for a restart from a SQLite journal after a clean stop, with a state seal and without, at 1.2 and 4.1 hours of repeated recording, in every set. `hypothesis` for a kill during input, for Cosmos and for new data of that length | with a seal 7.35 s at 1.2 h and 7.42 s at 4.1 h; without 7.11 s and 17.4 s (the authors' last set). With a seal the cost does not grow between the two histories; without one it grows with the journal; at 1.2 h a seal does not pay. Journals of the two real recordings: 6 to 8 s. The worst run of any set is 39.8 s |
| H12 | as H4 | p99 62.3 ms at 5,000 series; 1.5 to 1.7 s per close at 240,000 pairs |
| H11, H13, H14 | not measured, or not applicable | `Join` is built and its cost not measured; H13 does not apply, because the stores of the prototype write version rows inside the commit |

**Speed of evaluation.** The program that evaluates expressions on many rows at once handles 17.0 million rows per second, against 47.5 million for a loop written by hand for the same computation (the loop is 2.8 times faster); it uses 1.8 bytes per row. Closing one window takes 29.2 ms at p50 and 62.3 ms at p99.

**What the figures do not say.** Three hypotheses are supported, each for a named configuration: H5 for the engine's part in memory and on SQLite, H8 for the plane in memory at the head, and H10 for a restart from SQLite after a clean stop. H10 is not the defined path in three respects: the stop is clean, the history is one recording repeated, and a seal is valid only for the build that wrote it. H7 has two labels on the emulator and none for a real account. Nothing here says anything about the reference machine of Def. 13.4, about the Cosmos store on a real account, or about a machine without other load. Ch. 14 (14.7) gives the figures of every set.

## 13.7 What the prototype does differently

- **Method.** The benchmarks time with the program's own clock and memory counters, not with a library of micro-benchmarks, and each figure is one run, except the ten-run sets.
- **Load.** The synthetic load has 1,000 pods, 5,000 pairs of pods and 100 services. The run at the size of the estate has 150,000 pods, 30,000 services, 300,000 Azure resources, 240,000 active pairs of pods, 1 million flow rows per window of 60 s, and 4 windows: one run, not a distribution, no p99.
- **H4 and H12** were measured by one advance of logical time that closes windows of the master graph and commits the derived graph, not by a stack of three steps. At the size of the estate a close takes 1.5 to 1.7 s, above the target of 1 s; the plan still keeps up with a window of 60 s on one thread. The health model of Ch. 7 (7.4) was, in its earlier form, a stack of four projections, and its time per step was not measured.
- **The earlier health model in the last sets of H5 and H10.** The benchmarks load the definitions kept with the project, not the local names of the real cluster that the processing of a recording adds. In the last sets those definitions bind the health model to invented names, so on the real recording the health graphs are almost empty there (3 signals, not 12). The earlier sets ran the whole model and are inside the targets too, so the statuses do not depend on it.
- **Hot graph.** It differs from the design of Ch. 11 (11.5): adjacency is a linked list per entity, so a removal costs time in proportion to the degree; and the interface takes and returns strings, so a read assembles the element from columns (250 to 410 ns).
