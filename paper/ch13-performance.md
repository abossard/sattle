# Ch. 13 Performance model and benchmarks

**Claim.** Performance targets are hypotheses H1 to H14, each with a benchmark and a refutation rule. A status is given by a written decision rule and belongs to a named configuration: a store, a machine and the recorded load.
**Status.** Measured once per figure: Q14, H1 to H4, H6, H9, H12, and a 240,000-pair estate. Measured in ten runs each, on a machine under load (load average 14 to 115, 239 once; no machine without other load was available): H5, H7, H8, H10. By the rule of 13.6, **H5 (engine part, memory and SQLite), H8 (hot plane, head revision) and H10 (restart from a SQLite journal of 1.2 and 4.1 hours of repeated capture, with and without a state seal) are `supported` for those configurations**. **H7 has no status for a real account**; on the Cosmos emulator one set of runs is `refuted` and a later one `inconclusive`. H9 is met in one run (337 MB). The window close at the estate size is above the target of H4 and H12 in one run. Not measured: H11, H13, H14 (`Join` is built and has no cost measurement), B15, B16. Nothing here is on the reference hardware of Def. 13.4. The figures are in 13.7 and Ch. 14 (14.7). Claim 13.5 (hot read cost) is proved as counts on the layout of Ch. 11 (11.9); the engine's cost is benchmark B6.
**Gate.** 1c (efficient). The hypotheses H1 to H14 were written before the gate: they name a reference estate of 750,000 entities, Azure hardware and Cosmos DB, and none is a budget per node, edge or telemetry point at 1 million master nodes. They stay as the record against that estate; 13.0 states the target of the gate, leaves a marked place for the budgets and says which hypotheses carry over.
Uses: Ch. 3 (revision log), Ch. 4 (patch size), Ch. 7 (closed windows, crossing operators), Ch. 11 (stores, estate estimate, hot graph, experiments E1 to E20).
Forward references: Ch. 5, 6, 9 for operators, T5, T6 and configuration edits (written).
Used by: Ch. 14.
Sources: [R01] section 4.2 (P-a to P-g), ADR-093; [R02] section 5, section 6; `ARCHITECTURE.md:3,51,75`; `OPEN-WORK.md` items on bounded memory and load tests.

## 13.0 The target of gate 1c, and what the hypotheses below are

**The target.** Gate 1c of the research plan is: a budget per node, per edge and per telemetry point, met at 1 million master nodes on one machine, with persistence that does not depend on one store and that scales.

**The budgets are not stated yet.** They come from the investigation `paper/research/04-compute-and-memory-efficiency.md` when it is finished. This table is the place for them.

| Per | Memory | Work per change | Persisted bytes |
|---|---|---|---|
| master node | not stated | not stated | not stated |
| master edge | not stated | not stated | not stated |
| telemetry point | not stated | not stated | not stated |

**What H1 to H14 are.** They were written before the gate, against a reference estate of about 750,000 entities, a node of 8 vCPU and 32 GB, and Cosmos DB as the store (Def. 13.1 to 13.4, [R02]). They and their measurements stay unchanged as the record against that estate. None of them is a budget per element.

| Hypothesis | Carries over to gate 1c | Why |
|---|---|---|
| H1 structural ingest, H2 telemetry ingest | yes, to be measured again at 1 million master nodes | rates of the engine, independent of the store |
| H3 commit latency | not as stated | it names Cosmos DB. Under the gate it is a commit latency per backend, and no backend is the target |
| H4 projection latency per hop, H12 telemetry to property | yes | engine figures. Already above target at 240,000 pairs (13.7) |
| H5 source to graph | the engine part yes, the store part per backend | `supported` on memory and SQLite at the scale of the lab capture only |
| H6 hot reads, H8 element view | yes | engine figures. The sizes they name are those of the old estate |
| H7 durable reads | not as stated | it names the Cosmos `state` container. It becomes a read latency per backend |
| H9 memory | superseded | it bounds the hot graph only (about 100 bytes per element, met in one run) and not the whole engine, which holds 5.4 GB on the estate run. The budgets replace it |
| H10 cold start | yes | measured on SQLite, with nothing specific to a store |
| H11 edit replay, H14 join cost | yes | engine figures, not measured |
| H13 materialization lag | no | it measures a watermark that no adapter has; it was specific to the Cosmos design |

## 13.1 What "very high performance" means here

ADR-093 states "low maintenance and broad scale" as a goal and leaves limits open. This chapter turns the goal into **falsifiable hypotheses**, each with a named benchmark and a refutation rule. A hypothesis is neither a claim nor a result. Section 13.7 reports what the first prototype measured against them.

## 13.2 Definitions

**Definition 13.1 (workload model).** `W = (E, ρ, Σ, f, d)` with
- `E`: estate size (entities, relationships, projections);
- `ρ`: source event rate per family (ARG pages and changes, Kubernetes watch, Hubble flows, network flow logs, OTel metrics);
- `Σ`: number of telemetry series and their resolution;
- `f`: the fraction of inputs that change graph data;
- `d`: stack depth, the longest path from the master in the graph space.
The reference workload is the estimate of Ch. 11 section 11.3 (all inputs assumptions). The lab capture (`lab/`) replaces assumptions with measurements (E5, E20).

**Definition 13.2 (metrics).**

| Id | Metric | Unit |
|---|---|---|
| M1 | sustained structural ingest before backpressure | records/s per graph space |
| M2 | telemetry append rate | samples/s per node |
| M3 | commit latency: evaluated commit record to durable | ms, percentiles |
| M4 | freshness latency per hop: master commit to derived commit | s, percentiles |
| M5 | source-to-graph latency: engine receipt to master revision | s, percentiles |
| M6 | telemetry-to-derived-property latency: closing input to derived commit | s, percentiles |
| M7 | read latency: hot point, hot adjacency, hot k-hop, durable point, durable scan | µs, ms |
| M8 | element view latency (Def. 7.9) | ms |
| M9 | memory per entity, relationship and series point; storage per revision | bytes |
| M10 | cold start: load seal and replay the log tail | s |
| M11 | configuration edit replay time | s |
| M12 | materialization lag: commit to watermark (Ch. 11) | s |

**Definition 13.3 (tier).** A tier is `(scale factor over the reference workload, reference hardware, target table)`. Tier 1 is the reference workload (factor 1). Tiers at factor 4 and 16 are used by B9 only (E15). Targets exist for tier 1 only. Targets for other tiers are set after tier-1 results.

**Definition 13.4 (reference hardware).** One engine node of 8 vCPU and 32 GB per graph space (assumption). A Cosmos DB account with provisioned autoscale throughput on the master log (11.4). The exact Azure SKUs, regions, throughput settings and consistency level are fixed at the first run and written into every result file. **No result is valid without them.**

**Definition 13.5 (hypothesis).** `Hn = (target, benchmark Bn, refutation rule)`. Status is one of `hypothesis`, `supported`, `refuted`, `inconclusive` (13.6).

**Definition 13.6 (per-input cost).** For one accepted structural input:

`cost(input) = parse + identity lookup + Σ_ops cost_op(Δ) + commit / groupSize`

with `cost_op` from Fact 11.1, identity lookup `O(1)` on the binding set, and `commit` one store batch per group (Ch. 11).

## 13.3 Cost theorems

**Theorem 13.1 (join cost is output-sensitive).** Let `a` and `b` be indexed by the join key, `d_a` and `d_b` the maximal numbers of rows of the integrals sharing a key. One incremental join step on changes `Δa, Δb` costs `|Δa| + |Δb|` index lookups plus the number of produced rows, and the number of produced rows is at most `|Δa|·d_b + |Δb|·d_a + |Δa|·|Δb|`.
Status: **proved**, conditional on the bilinear incrementalization rule of [budiu2023] (T5, Ch. 6): `(a ⋈ b)^Δ = Δa ⋈ Δb + I(a) ⋈ Δb + Δa ⋈ I(b)`.

*Proof.* Each of the three terms is a join between a delta and an indexed set. A hash or ordered index finds the matching rows of the other side with one lookup per delta row, and enumerating the matches costs their number. For a delta row `y` with key `κ`, the number of matches in `I(a)` is `deg_a(κ) ≤ d_a`. Summing gives the stated bounds. ∎

A change that touches a hub of degree `d` costs `Θ(d)` produced rows (Fact 11.2), which is why H14 tests the proportionality.

**Proposition 13.2 (group commit amortization).** Let a store batch of `n` revisions cost `C(n) = α + β·n`, with `α` the per-batch overhead and `β` the per-revision payload cost, for `n ≤ n_max = min(100, ⌊2 MB / revision size⌋)` (S1, Ch. 11). Then the cost per revision `β + α/n` decreases strictly in `n` and is smallest at `n_max`. The first revision of a group waits for the group to fill or a flush deadline, so group size trades latency for cost.
Status: **model** (by arithmetic). `α, β` are hypotheses, measured in E1 and E2.

**Theorem 13.3 (tandem pipeline).** Consider stages `1 … m` (acceptance, evaluation, commit, each projection hop) with service rates `μ_i` records per second. With bounded queues that block their producers, the sustained throughput is at most `min_i μ_i`. With unbounded queues, a stage with arrival rate `λ_i > μ_i` accumulates backlog at rate at least `λ_i − μ_i`. A projection that processes its inputs sequentially (ADR-017) has `μ ≤ 1 / s̄` where `s̄` is its mean service time per record or group.
Status: **proved**.

*Proof.* Flow conservation: the backlog of stage `i` changes at `λ_i − (departure rate) ≥ λ_i − μ_i`. With blocking queues a producer cannot exceed the consumer's departure rate, so every stage runs at most at the slowest rate. Sequential processing serves one record or group at a time, so the departure rate is at most the reciprocal of the mean service time. ∎

Consequence for H1: 5,000 records per second through one sequential evaluator needs a mean service time of at most 200 µs per record, commit amortization included (arithmetic on the target). Parallelism comes from many graphs (sharding by tenant and cluster, Ch. 11), not from within one projection (gap P-a).

**Theorem 13.4 (latency budget composition).** Let `L = X₁ + … + X_d` with `P(X_j > q_j) ≤ ε_j`. Then `P(L > q₁ + … + q_d) ≤ ε₁ + … + ε_d`. Hence, for a stack of `d` hops, if every hop meets its budget `q` at quantile `1 − 0.01/d`, then `d·q` is an end-to-end budget at p99, and if every hop meets `q` only at p99, then `d·q` holds only at the `1 − 0.01·d` quantile.
Status: **proved**.

*Proof.* If `L > Σ q_j` then at least one `X_j > q_j` (otherwise the sum is at most `Σ q_j`). The union bound gives the claim. ∎

B4 reports the per-hop histograms and the end-to-end percentile directly. Theorem 13.4 says which per-hop quantile a stack depth requires.

**Claim 13.5 (hot read cost).** For the hot layout of Ch. 11 (11.9): dense handles from an intern table, one column per property in fixed segments, adjacency as a base CSR plus an append-only delta log per vertex, each entry with a revision interval.
- (a) A read of one property of an element by its handle at the published base costs two array accesses, a segment and an offset. From an id it costs one intern lookup first: a hash of the id and an expected constant number of probes in a table of bounded load.
- (b) A read at a pinned revision inspects in addition the delta entries of that element that are not yet merged into the base: `O(1 + ℓ_k)` for `ℓ_k` such entries.
- (c) A one-hop adjacency scan of a vertex `v` at a pinned revision visits `deg_base(v) + L_v` entries, each tested against the pinned revision in constant time, where `deg_base(v)` is the length of the vertex's slice of the base CSR and `L_v` the length of its delta log. With the delta log kept below a merge threshold `θ` (a parameter), that is `O(deg(v) + θ)`.
Status: (a) to (c) are **proved** as counts on the layout of 11.9, restated. They are statements about a design. That the engine's hot graph has this layout, and what the constants are, is a hypothesis of gate 1c, settled by measurement and not by proof: benchmark **B6** (point reads by handle and by id at 10⁴, 10⁵ and 10⁶ elements, where the time per read must not grow with the size; one-hop scans over vertex degrees from 1 to 10⁵ and delta logs from 0 to `θ`, where the time must be linear in `deg_base(v) + L_v`). The first form said that a hot point read is `O(1)` without conditions; (a) and (b) say for which read. Counterexample to the first form: an element edited `ℓ` times since the last merge, read at a revision before those edits, costs `ℓ` entry tests, which no constant bounds unless a merge threshold applies per element.

*Proof.* (a) A handle is a dense integer (interning, 11.9). A column lives in fixed segments of 64 K elements, so the value of handle `i` is at segment `⌊i / 2¹⁶⌋`, offset `i mod 2¹⁶`: two accesses. The intern table maps an id to its handle by hashing; with bounded load the expected number of probes is constant, and hashing reads the id once. (b) Base arrays are immutable once published, and a reader at revision `r` filters the delta entries by `from ≤ r < to` (11.9): the entries of the element are inspected once each. (c) The base CSR is sorted by `(vertex, traversal direction, type)`, so the edges of `v` are one contiguous slice, found by two offsets; each entry is tested once. The delta log of `v` is scanned once. ∎

## 13.4 Hypotheses

All values are hypotheses unless stated. H1 to H10 are the targets of [R02] section 5.1. H11 to H14 complete the metrics of Def. 13.2. "Refuted if" follows the rule of 13.6.

| Id | Target (tier 1) | Benchmark | Refuted if | Measured (one run, Ch. 14) |
|---|---|---|---|---|
| H1 structural ingest | ≥ 5,000 accepted input records/s per graph space. 50 revisions/s steady. A 2,000 revisions/s burst of 10 min drains within 10 min after it ends | **B1** replay lab inputs at 1×, 10×, 100× speed: acceptance rate, revisions/s, backlog drain time | sustained rate below 5,000 records/s, or drain longer than 10 min | about 67,000 records/s on the real capture (243,457 records in 3.6 s, an early engine slice that read fewer record kinds, `engine/PERF.md`), in memory. Estate run: 3.6 M flow rows/s including row generation. Burst and drain not measured. The lab transport delivers about 125 events/s per throughput unit (Ch. 10, 10.6) |
| H2 telemetry ingest | ≥ 200,000 samples/s per node into the hot ring. Aggregated flow records ≥ 4,000/s into ADX | **B2** synthetic series generator with lab-measured shapes: append rate, ring memory, ADX ingestion lag | either rate below target | 7.84 M samples/s into the in-memory ring. ADX part not built |
| H3 commit latency | p99 ≤ 20 ms from evaluated commit record to durable commit record | **B3** group-commit sweep on a real Cosmos account (shares E1, E2). The master log is on provisioned throughput because the documented point-write latency SLA is below 10 ms (provisioned) against an SLO below 30 ms (serverless) | p99 above 20 ms at any group size that sustains H1 | memory p99 0.079 ms, SQLite p99 4.31 ms. Cosmos not measured (emulator only, no latency model) |
| H4 projection latency per hop | p99 ≤ 1 s from master commit to derived commit for linear projections. ≤ 5 s for join projections with touched degrees ≤ 1,000 | **B4** chain of 3 stacked projections over B1 input: per-hop histograms and the end-to-end percentile (Theorem 13.4) | per-hop p99 above target | p99 62.3 ms for a two-graph chain at 5,000 pairs. At 240,000 pairs the first window closes in 9.0 s and windows 1 to 3 in 1.5 to 1.7 s each (4 windows, one run), above 1 s. No 3-hop chain |
| H5 source to graph | p99 ≤ 2 s from engine receipt to master revision. Source delays are excluded (ARG changes up to 5 minutes, Activity Log 3 to 20 minutes, Fact 0.1) | **B5** timestamps at adapter receipt and at commit | p99 above 2 s | `supported` for the engine part on memory and SQLite; Cosmos not measured in ten runs (13.7) |
| H6 hot reads | point read ≤ 1 µs. One-hop scan ≤ 0.5 µs + 5 ns per neighbour. 3-hop neighbourhood of 10,000 elements ≤ 20 ms | **B6** BenchmarkDotNet microbenchmarks on the hot graph at the Ch. 11 size ([benchmarkdotnet]) | any median above target | point read 250 to 410 ns and one-hop scan 230 to 270 ns at 6 neighbours with the second hot-graph layout (two runs); 20 ns and 109 ns with the reference layout. 3-hop and the 2.25 M relationship size not measured |
| H7 durable reads | p99 ≤ 10 ms point read from the Cosmos `state` container. One-hop scan of 100 neighbours ≤ 20 ms | **B7** read load test against `state` | p99 above target | no status for a real account; two labels on the Cosmos emulator (13.7) |
| H8 element view | structure at `r` joined with 1 hour of 4 series for 10,000 elements ≤ 100 ms from the hot plane | **B8** Def. 7.9 queries inside the hot horizon | above 100 ms | `supported` for the hot plane at the head revision (13.7) |
| H9 memory | hot graph ≤ 400 MB for 750,000 entities and 2.5 M relationships (estate 2.25 M plus headroom), excluding the telemetry ring and the lookback state of `Series` operators (Ch. 11, 11.8) | **B9** managed heap after load, at tier factors 1, 4, 16 (E15) | above 400 MB at tier 1 | **337 MB** for the hot graph of 750,000 entities and 2.5 M relationships (second layout, one run). The reference layout took 2,395 MB. Whole-engine heap on the 240,000-pair estate: 2.5 GB after structure, 5.4 GB at the end, outside the scope of H9 |
| H10 cold start | load a seal and replay 1 h of log in ≤ 60 s | **B10** kill and restart the engine during B1 | above 60 s | `supported` for restart from a SQLite journal after a clean stop, with and without a state seal (13.7) |
| H11 edit replay (new) | replay time of a configuration edit grows linearly in the total number of retained inputs of the affected projection, with a measured slope. A numeric tier-1 target is set after the first measurement | **B11** configuration edits at 1×, 4×, 16× retained inputs (gap P-f; depends on Ch. 9) | growth faster than linear in the retained inputs | built as full replay (Ch. 9, 9.5), not measured |
| H12 telemetry to property (new) | p99 ≤ 1 s from the closing logical-time input to the derived commit for a threshold crossing operator. The target equals H4's linear hop because a crossing operator is linear on closed records | **B12** threshold projection over lab latency series, with and without hysteresis (E16) | p99 above 1 s | p99 62.3 ms at 5,000 synthetic series (the same close as H4); 1.5 to 1.7 s per close at 240,000 pairs, above 1 s in one run |
| H13 materialization lag (new) | p99 of commit-to-watermark ≤ 5 s at steady load (parameter chosen here; R02 states "seconds") | **B13** = E3 | p99 above 5 s | not applicable: SQLite and the Cosmos store materialize inside the commit (watermark equals head, D10) |
| H14 join cost against hub degree (new) | per-change time grows linearly in matched degree (Theorem 13.1), with slope within a factor of 2 over degrees 10 to 100,000 | **B14** = E14, synthetic hubs and the first ten real projections | slope outside the factor, or super-linear growth | `Join` is built, its cost is not measured |

Comparison benchmarks (no target, ratios are reported with confidence intervals):

| Id | Benchmark | Purpose |
|---|---|---|
| B15 | Nexmark queries ([nexmark]) for the operator subset Sattle supports (filter, map, join, group), run on Sattle and on Feldera and Materialize | position the incremental core against incremental view engines |
| B16 | LDBC Social Network Benchmark interactive update stream ([erling2015]) mapped to the hot graph, run against Memgraph and Neo4j | position the hot graph against graph stores |

Neither comparison is a claim. No statement of superiority is made before the results exist and the comparison is fair on hardware and configuration.

## 13.5 How the contract limits of [R01] 4.2 are treated

| Gap | Issue | Treatment in the paper | Tested by |
|---|---|---|---|
| P-a | strictly sequential processing per projection caps throughput, stacked projections add latency | accepted. Theorem 13.3, Theorem 13.4, shard by `(tenant, cluster)` | H1, H4 |
| P-b | patches carry full before and after states, so a one-property change costs `O(entity size)` twice | the logical model keeps full states (guards need `b`, Ch. 4). The physical encoding may store property-level differences if decoding yields the same canonical states. Telemetry never enters this path (Theorem 7.1) | B1 sweeps entity size |
| P-c | one evaluation request per input, per-input commit | evaluate in batches and commit in groups with revision boundaries preserved (Proposition 13.2, Ch. 11). Batch invariance is T6 (Ch. 6) | H1, H3 |
| P-d | per-element history keeps up to `N` original payloads | the evidence head holds input ids (Ch. 11). Bytes stay in the input log under its retention (Ch. 9) | H9 |
| P-e | global relationship uniqueness needs a cross-partition reservation | not needed. Theorem 11.8 | E12 |
| P-f | configuration edits pause the projection and replay retained inputs | cost model `O(N · elements)`, H11 | B11 |
| P-g | string ids and polymorphic JSON at every boundary | interning and columns inside the engine (Ch. 11, 11.9). Strings and JSON at the boundary only | H6, H9 |

## 13.6 Method

- **Determinism of the harness.** Fixed seeds for generators, input from the recorded lab capture where possible, published harness and result files in the repository.
- **Runs.** A warm-up phase, then at least 10 independent runs per configuration. Report median, p50, p99 and the 95% confidence interval of the median across runs, and the variance.
- **Decision rule.** A latency or size target is *supported* iff the reported statistic is at or below the target in every run, *refuted* iff the median run is above the target, otherwise *inconclusive*. A throughput target is supported iff the lower bound of the 95% interval is at or above the target.
- **Scope of a status.** The rule above says nothing about the machine, and Def. 13.4 names a reference that none of the runs so far used. A status is therefore stated for a *configuration*: a store, a machine and the recorded load. The rule is applied to the runs of one configuration, and the status carries its name ("`supported` on SQLite on the lab laptop under load"). It is the status of the hypothesis itself only where the hypothesis names no store (H5 and H10 name none; H7 names the Cosmos `state` container, H3 names Cosmos). For an upper-bound latency target, load on the machine can only raise latencies, so a pass under load holds a fortiori for that configuration, while a failure under load is weak evidence. A `refuted` on the emulator is reported as such and does not refute the hypothesis for a real account. A machine with no other load was not available for any of the ten-run figures, and the threat stays in Ch. 14 (14.9).
- **Environment record.** Def. 13.4 plus engine commit, .NET version, Cosmos and ADX settings, and the workload parameters actually used.
- **Microbenchmarks** use BenchmarkDotNet. System benchmarks use the harness and the lab capture.
- **Separation of causes.** B3 runs against a real Cosmos account (the emulator has no RU accounting, Ch. 11). Hot-plane benchmarks run with the store stubbed, so that store effects do not enter H4, H6, H8.

## 13.7 Results

Source: the engine's benchmark notes (figures of set B at evaluation 6, each with its command; set B is runs of the project that the authors did not repeat, Ch. 14, 14.1), set A's ten-run output (at evaluation 4 copied unchanged to `paper/evidence/perf-*-orch.txt`, at evaluation 5 to `perf-h8-orch-turn8.txt`, `perf-h10-*-orch-turn8.txt` and `cosmos-cap-orch-turn8.txt`) and this paper's own ten runs of the same harness on clean exports (`bench b5`, `b8`, `b10`; `perf-*-paper.txt` at evaluation 4, `perf-*-head4-paper.txt` at evaluation 5, `perf-*-head6-paper.txt` at evaluation 6); the discussion is in Ch. 14 (14.7). H7 was not repeated in set A or by this paper after evaluation 4 (no emulator was available to this paper). Machine: Apple M1 Max, 10 cores, 64 GB, macOS, .NET 10.0.14, Release build, in-memory and SQLite stores and, for the Cosmos figures (those of sets A and B runs only), the vNext emulator. One run per figure except H5, H7, H8 and H10 (ten runs). Load average: set A 25 to 106 at evaluation 4 and 25 to 75 at evaluation 5; this paper 27 to 91 at evaluation 4, 16 to 70 at evaluation 5 (239 during three restarts) and 16 to 82 at evaluation 6; set B 14 to 60 at evaluation 6 (recorded in the output files and in `engine/PERF.md`). **No run was on a machine with no other load.** The reference machine for the ten-run measurements is the lab laptop when no other work runs on it (a project decision), which is not the Azure reference of Def. 13.4; Def. 13.4 stays the target for Cosmos figures.

| Id | Status under the rule of 13.6 | Measured |
|---|---|---|
| H1 | met in one run, not `supported` | about 67,000 records/s on the real capture; 3.6 M flow rows/s at the estate size |
| H2 | met in one run, not `supported` | 7.84 M samples/s |
| H3 | not tested (Cosmos not built); met locally | memory p99 0.079 ms, SQLite p99 4.31 ms |
| H4 | met in one run for two graphs at 5,000 pairs, not met in one run at 240,000 pairs; not a 3-hop stack | p99 62.3 ms; 1.5 to 1.7 s per close at the estate size, 9.0 s for the first |
| H6 | met in one run for point and one-hop reads | 250 to 410 ns, 230 to 270 ns (second layout) |
| H9 | met in one run for the hot graph, not `supported` | 337 MB against 400 MB. Whole-engine heap 5.4 GB at the estate size, not covered by H9 |
| H12 | as H4 | p99 62.3 ms at 5,000 series; 1.5 to 1.7 s at 240,000 |
| H5 | **`supported`** for the engine part on memory and on SQLite (lab laptop, under load): every run of every ten-run set at the last three measured engine commits is at or below 2,000 ms. `hypothesis` for the Cosmos store, which Def. 13.4 names | p99 of the records that make a master revision: 1.11 ms in memory and 23.5 ms on SQLite (this paper, evaluation 6). The worst run of any set is 25.9 ms, a factor of about 77 below the target. One single record took 1.75 s in set B's SQLite set: a p99 target does not bound one record. Cosmos: 0.11 to 0.13 s per revision, a mean on the emulator and not a p99 |
| H7 | **No status for the hypothesis**, which names the Cosmos `state` container of a real account. On the emulator the two ten-run sets get different labels: **`refuted`** in set A's set at evaluation 4 and **`inconclusive`** in set B's set at evaluation 6 | point read p99 22.5 ms in the first set and 6.55 ms with a worst run of 31 ms in the second, against 10 ms. The read path is the same in both, so the emulator figure follows the load on the machine it shares. SQLite: 0.05 ms, which is not a test of H7 |
| H8 | **`supported`** for the hot plane at the head revision, in memory (lab laptop, under load): every run of every set is at or below 100 ms since the window scan stopped allocating. `inconclusive` for the code before that | median 29.5 ms, range 16.7 to 41.1 (this paper, evaluation 6). The worst run of any set is 49.4 ms, a factor of 2 below the target. Before: median 73.5 ms with one run of ten at 305 ms, in which the garbage collector ran |
| H10 | **`supported`** for restart from a SQLite journal after a clean stop, with a state seal and without, at 1.2 h and 4.1 h of repeated capture, in every set. `hypothesis` for a kill during ingestion, for Cosmos and for new data of that length | with a seal 7.35 s at 1.2 h and 7.42 s at 4.1 h; without 7.11 s and 17.4 s (this paper, evaluation 6). With a seal the cost does not grow between the two histories; without one it grows with the journal; at 1.2 h a seal does not pay. Journals of the two real captures: 6 to 8 s. The worst run of any set is 39.8 s. Not the defined path in three respects: the stop is clean, the history is one capture repeated, and a seal is valid only for the build that wrote it |
| H11, H13, H14, B15, B16 | not measured or not applicable (the `Join` of H14 is built and unmeasured; H13 does not apply to the SQLite and Cosmos stores of the prototype, which materialize in the commit call) | none |
| Q14 | answered | interpreter 17.0 M rows/s against a hand-fused loop 47.5 M rows/s (2.8 times), 1.8 bytes per row, window close p50 29.2 ms and p99 62.3 ms |

The figures of every set, the workloads and the caveats of H5, H7, H8 and H10 are in Ch. 14 (14.7) and in `paper/evidence/perf-*`; the engine commits that were measured are evaluation 4, evaluation 5 and evaluation 6, and the benchmarks were not repeated at evaluation 7 or evaluation 9. Three hypotheses are `supported`, each for a named configuration: H5 for the engine part on memory and SQLite, H8 for the hot plane at the head revision, and H10 for restart from SQLite after a clean stop. H7 has two labels on the emulator from two sets and none for a real account. Nothing in this chapter says anything about the reference hardware of Def. 13.4, about the Cosmos store on a real account, or about a machine with no other load.

## 13.8 Delta to iteration 0.1

- ADR-093 is supplemented by H1 to H14. The oracle and `VERIFICATION.md` remain correctness evidence only.
- `EvaluationInput` carries one input per request. A batched evaluation input is required (P-c).
- `RetainedInput.OriginalBytes` per element is replaced by input ids on the element and bytes in the input log (P-d).
- Numeric targets name hardware and store configuration (Def. 13.4). The research text names a hardware class only (R02 section 5); this paper adds the rule that the SKU is fixed at the first run.

## 13.9 Deviations found by the prototype

Ids refer to Ch. 14 (14.8).

- **D14 (method).** The benchmark uses Stopwatch and GC counters, not BenchmarkDotNet, and each figure is one run (ten for H5, H7, H8, H10) on an Apple M1 Max, not the reference hardware of Def. 13.4 (`engine/PERF.md`). The ten-run figures were taken at a load average of 14 to 115 (239 during one set of three restarts), so latencies are pessimistic and the spread is wide; no machine with no other load was available. The statuses of 13.7 follow the rule of 13.6 with its scope clause.
- **Workload.** The synthetic workload has 1,000 pods, 5,000 pod pairs and 100 services. The reference estate has about 240,000 flow pairs.
- **The health model in H5 and H10 at evaluation 6.** The benchmarks load the tracked definitions and not the local space overlay that `replay` and `serve` apply to a real capture. Since evaluation 6 the tracked space binds the health model to invented names, so on the real capture the health topology and state graphs are empty in a benchmark and the signals are 3 and not 12 (`paper/evidence/bench-overlay-head6.txt`: the same capture replayed without and with the overlay). At evaluation 5 the tracked space carried the capture's names and the benchmarks ran the whole model. `engine/PERF.md` describes the workload of H5 and H10 as the newest real capture without saying this. The H5 and H10 figures at evaluation 6 are therefore for the master graph, the service tree, deployment health, the report and an almost empty health model, which is one more reason not to compare them with the earlier set; the statuses do not depend on it, because the earlier set ran the whole model and is inside the targets too.
- **H9.** Met in one run by the second hot-graph layout, 337 MB against 400 MB. Differences from the design of Ch. 11 (11.9): adjacency is a linked list per endpoint and not a CSR with a delta log, so removal is `O(degree)`; ids are strings at the API, so reads materialize elements (250 to 410 ns). Whole-engine memory at the estate size is 5.4 GB.
- **Estate run.** One run of `sattle bench estate`: 150,000 pods, 30,000 services, 300,000 Azure resources, 240,000 active pod pairs, 1 M flow rows per 60 s window, 4 windows. Not a distribution, no p99.
- **H4 and H12.** Measured by one `Advance` that closes windows of the master graph and commits the derived graph, not by a three-hop stack. At the estate size the close is 1.5 to 1.7 s for windows 1 to 3, above the 1 s target; the plan still keeps up with a 60 s window on one thread. The health model of Ch. 7 (7.5) is a stack of four projections on the lab capture, but its latency was not measured per hop.

## 13.10 Obligations

| Id | Statement | Discharged by |
|---|---|---|
| O13.1 | Theorem 13.1 on the engine: matched-degree proportionality | B14, with T5 (Ch. 6) |
| O13.2 | Claim 13.5 | B6 |
| O13.3 | `α, β` of Proposition 13.2 | E1, E2 |
| O13.4 | Each hypothesis receives a status in Ch. 14 | benchmark runs |
| O13.5 | H5, H7, H8, H10 repeated on a machine with no other load (the lab laptop with no other work running), Cosmos figures on a real account in the engine's region, H10 with a kill during ingestion and with a seal on Cosmos | the harness of `engine/PERF.md`; set B sends the output |
