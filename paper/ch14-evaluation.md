# Ch. 14 Evaluation

**Question.** What did running the prototype show about the claims of Chapters 1 to 13, on what data, by whom, and what does it not show?

**What you can state at the end.** Historical runs built health models from two real recordings and produced matching final digests across the tested batch sizes. Those recordings are unavailable to an independent reader. The original statement inventory and three supported performance statuses describe the draft and its measured configurations, not universal guarantees. The later manual audit found additional proof conditions and gaps; current-code reruns are reported separately in 14.11. Tests do not replace proofs, and readable diagrams do not establish reader comprehension.

This chapter uses all earlier chapters.

**Running example.** The shop of the earlier chapters is invented. The real test cluster ran a demo workload of the same shape: `frontend`, `api` and `db`, a generator of load, and an `intruder` that the network policy denies. The synthetic estate used by the tests copies the shop: cluster `synth`, namespace `shop`, services `web`, `api` and `db`.

## 14.0 Results by gate

| Gate | Where | What the results amount to |
|---|---|---|
| 1a theory | 14.5, 14.6 | Historical classifications and tests exist. The tested real replays agreed across five batch sizes; the manual audit adds missing premises and unresolved gaps (Ch. 15, 15.3) |
| 1b flexible and expressive | 14.3 | A list of what was built. No result of this chapter is stated or tested as a claim of expressiveness |
| 1c efficient | 14.7, 14.9, 14.11 | Three historical hypotheses were supported for named configurations. The local million-node rerun exceeded the CPU and peak-RSS budgets. Nine steady closes leave long-run p99 open (13.1, 14.11) |
| 2a health model from real Azure data | 14.2, 14.4 | The pipeline runs end to end on real data and computes a health model. Early evidence: one workload, a cluster that no longer exists, and live examples on a new test cluster in which a failed pod and a pod whose calls are dropped each reach the model and are found as its origin (Ch. 7, 7.4) |
| 2b understandable by people | 14.4, 14.6, Ch. 12 | Lineage and the views of the viewer exist and are checked by scripts. No person outside the project has read the model |
| 2c always live | 14.6, 14.7 | Restart and resume are tested against a fake source. One live run on Event Hubs, killed twice and resumed, ended in the digest that processing its recording gives (Ch. 10, 10.6). From a source to the master graph 4 s for a change of replicas, to the health model 77 to 90 s for a failing pod, each measured once |

## 14.1 Method and evidence

**Kinds of evidence.** A claim has one of these levels. A higher level does not replace a lower one.

| Kind | Meaning |
|---|---|
| A | a proof in the paper |
| B | a seeded random test: a generator of random cases from fixed seeds, 8 groups of 500 cases, 4,000 cases per property |
| C | an example test, on small inputs |
| D | a benchmark |
| E | a run on a real recording, or another run by the authors |

**Provenance of historical runs.** The authors ran experiments at nine successive software states. From the tenth state onward they mainly read code and other contributors' results; appendix A.9 records the exceptions. A later number in that log does not mean every experiment was rerun.

| Set | Who ran it | What the authors checked |
|---|---|---|
| Authors' runs | the paper's authors, on a clean recorded state | the experiments themselves |
| A | the project before recording a software state, including local-emulator checks | output read, not independently rerun |
| B | engine developers alongside their code | reported results, not independently rerun |

The 22 November review reruns in 14.11 form a separate current-code evaluation. They do not replace or retroactively verify these sets.

**Rule of decision** for hypotheses (Ch. 13, 13.5): a target of time or size is *supported* iff the statistic is at or below the target in every one of at least ten runs, *refuted* iff the median run is above it, and *inconclusive* otherwise; a status holds for one configuration. A figure from fewer than ten runs is reported as met in one run, not met in one run, or not measured.

## 14.2 The test environment and the recordings

- **Cluster.** A managed Kubernetes cluster in Azure (AKS, Kubernetes 1.34), with the network observer and a managed measurement system. It no longer exists: its group of resources was deleted on 2026-11-10, not by this project, as set A reports. Both recordings were taken before that. No further recording can be taken.
- **Recording.** A script records, per source, the envelopes of Def. 10.2: the Azure resource list, Kubernetes list and watch, flows of the network observer, a measurement system, Azure measurements, Azure's log of management actions, the network logs of Azure, and the copies sent through Event Hubs. A forwarder sends the same envelopes to Event Hubs for the live input (Ch. 10, 10.6). Setting up and removing the environment took 3 min 14 s and 2 min 23 s once, as its developers measured; the authors cannot repeat it.
- **The two recordings.** The *95-minute recording* (5,679 s): 245,345 records, among them 235,858 flows of the network observer over 725 s and 522 records of network logs with 175,265 tuples. The *17-minute recording* (1,017 s): 160,300 records, from every source. Both hold real names and are kept outside the project's repository. The names of the real workload are given to the definitions by a local file next to the recordings; the shared definitions bind invented names.
- **Fixtures.** Earlier evaluations used scrubbed samples of the recordings. The reviewed checkout contains their builders but not those capture-derived inputs; tests requiring them skip. The bundled **synthetic estate** uses invented names and exercises the supported sources. Its historical 548-record count is not the current fixture size: the review replay consumed 2,266 records. It is the viewer's default example (Ch. 12).
- **Size.** The test estate is small. After the 95-minute recording the master graph has 15,154 entities, of which 14,622 are events kept as elements for 24 hours of logical time (4,135 of them evidence elements with a subject, the rest audit events) (Ch. 9, 9.6); the other 532 are the estate itself. The reference estate of Ch. 11 has 750,000 entities. The recordings show that the pipeline works end to end, not that it scales.

## 14.3 What was tested

- **Engine.** Fifteen modules of definitions, loaded from text and checked with no error and no warning. Adapters for every source of 14.2. 17 of the 18 operator kinds (Ch. 5; `Delta` is missing), aggregates per context, parameters, levels written with joins, rules `lens` and `bijective` with backward edits (Ch. 8), identity merge with a survivor that does not depend on the order (Ch. 1), references for missing ends (Ch. 3). Stores in memory, on SQLite and on Cosmos DB (the last against the emulator only), the journal and state seals (Ch. 11). A live input that resumes from the journal (Ch. 10; its loop tested with a fake source, and run once on Event Hubs). The examples: the workload model (Ch. 7), deployment health and its report (Ch. 2, 12), the service and capability levels (Ch. 5).
- **Viewer** (Ch. 12): views of data, space, ontology and documents, the editor of projections, colors from value roles, highlight across levels, stubs, folding of supporting elements.
- **Test counts.** At the last state, from set A: 670 of 670 tests of the engine in every category, and 149 of 149 cases of the comparison with the independent implementation (14.6), all 21 tests of formerly known defects passing. At the ninth state, the authors: 579 tests without the cloud emulator (463 by default, 96 slow, 20 on the recordings), no failure; set A reports 598 of 598 with the emulator at that state. Viewer: the authors ran 643 tests, no failure; set A reports 672 at a later state.

What is not built is listed in Ch. 15 (15.2).

## 14.4 End to end on the real recordings

One run of each recording by the authors, at the last state of the engine, with the defaults and the local names. Times are single runs on a shared laptop, not performance figures.

| Item | 95-minute recording | 17-minute recording |
|---|---|---|
| Records processed | 245,345 in 1,636 batches, 14 advances of logical time, 5.4 s | 160,300 in 4,029 batches, 16 advances, 7.3 s |
| Not consumed, rejected | 5, 0 | 5, 1 (a record of the network observer that is neither a flow nor a loss message) |
| Master graph | version 674, 15,154 entities, 4,650 relationships | version 1,907, 48,201 entities, 5,828 relationships |
| Entity types of the master | 4,132 activity events, 10,487 audit events, 361 Azure resources, 22 deployments, 76 meters, 2 machines, 56 pods, 15 services, 3 warnings | 5,181, 42,244, 385, 22, 112, 3, 66, 15, 173 |
| Measurements of the master | 9,047 samples over 7,834 windows of elements, no partial window | 9,090 over 7,942, none partial |
| Service level | version 44: 15 services in 4 groups, 14 lifted dependencies | version 27: the same |
| Report document (Ch. 12) | version 39, 26 blocks | version 23, the same 26 blocks |
| Health model of the earlier definitions, replaced by the workload model of Ch. 7, 7.4 | version 36: 9 components all healthy, 9 signals, 119 evidence elements | version 48: 11 components, 12 signals, 124 evidence elements; 3 services and the workload unhealthy, the other 7 healthy |
| Deployment health (Ch. 2) | 22 workloads, 38 `Talks`; 1 workload and 2 `Talks` unhealthy | 22, 36; 1 workload unhealthy, no `Talks` |
| Capability level | empty: capabilities are typed in by people, and no source recorded any | empty, for the same reason; on the synthetic estate 4 capabilities, 2 `Under`, 2 lifted `DependsOn` |
| References (Def. 3.14) | master 452, all absent (427 Azure resources, 24 pods, 1 machine); derived graphs none | master 590, all absent (563, 26, 1); derived graphs none |
| Identities | 17,109 created, 80 aliased, 35 merged, no conflict | 50,599 created, 81 aliased, 43 merged, no conflict |
| Runtime faults and conflicts | none of any kind; 169 rows skipped by emits because a key was absent | none; 125 skipped |

The health rows were taken with the earlier health model. Its replacement, the workload model (Ch. 7, 7.4), builds the structure and the dependencies between services on both recordings with no dangling edge; with no signal rule typed in for them, every state there is `Unknown`. The authors have not run the workload model themselves.

An earlier check by the engine's developers, on an earlier state: the `flows` and `drops` measurements summed in the master graph (199,379 and 512) equal an independent count of the recording, and 199,379 of the 235,858 flows are between two named pods. No independent count of the evidence elements was made.

**What this shows.** The pipeline of Ch. 10 and Ch. 5 runs end to end on real data from every source: adapters produce rows; the ingestion projection resolves identity and emits facts and evidence elements; flow counts are measurements on `Calls` relationships (Ch. 11, A1); derived graphs roll them up with a status with hysteresis; a stack of four projections computed the earlier health model, which found failing services on the second recording; a separate ontology computes deployment health; and none of it creates a version per sample (674 versions of the master for 235,858 flows and 175,265 tuples). **What it does not show.** Scale; the Cosmos store on a real recording or account; `sync` rules; the live input; a stack of lenses; a capability level on real data; and that the unhealthy states match the real condition of the cluster.

## 14.5 Determinism and batch invariance

**Replay determinism (Theorem 6.3).** Two runs over the fixtures give the same state digest and, for every graph, equal patches. Two tests process every real recording at batch sizes 1, 50 and 100,000 and compare the state digest, the number of identity conflicts (0) and the digest of every graph; the authors ran them at five states of the software, all passing.

**Batch invariance (Theorem 6.2).**
- A test with batch sizes 1, 2, 3, 7, 50, 100,000 and 2,000: equal state digests, and the integrated output equals the final graph.
- Targeted tests of premises B1 and B2 (Ch. 6, 6.3) pass, and each fails when its fix is taken out; the authors repeated that check.
- How the premises were found is told in Ch. 6 (6.6). Since the survivor of a merge became the smallest id of any key (Proposition 1.8), each recording has **one digest at five batch sizes** (the default, 1, 50, 777 and 100,000), the same number of merges at every size (35 and 43) and no conflict, at five states of the software. At the last state the authors processed each recording once at the default size; set B reports the same digest at 1, 50 and 100,000, which nobody repeated. The cut into batches changes how many ids are created before the merge (16,722 at size 1 against 17,564 at 100,000 on the first recording), and the merge makes the result the same. The report document has one digest at batch sizes 1 and 100,000, although the graph has 60 and 39 versions.
- **Reading.** Theorem 6.2 holds on the graphs, measurements and documents of both recordings under five batch sizes. B1 and B2 are covered by targeted tests and by the digests of the recordings. B3 holds on these two inputs, on the merge examples of the tests, on one synthetic input in two orders of arrival at four batch sizes, and on generated inputs: five seeds, each with 20 random orders of arrival of keys of two domains and three cuts of each order. B3 is a tested premise.

**What this does not cover.** Interleavings of independent sources beyond the shuffled arrivals of the tests of live reproduction (up to 30 places, three seeds, on the fixtures; one more on the newest recording). Repeating the same run many times on a real recording. Other platforms. A Cosmos run on a real recording beyond the emulator (Ch. 11, 11.6). Live runs processed again from their recordings on the real reader beyond the one example of Ch. 10 (Proposition 10.7 is tested against a stand-in). The state digest of a repeated recording is the same with and without seals.

## 14.6 Statements against tests

The inventory gives the draft's original proof and test classifications. The later manual audit reviewed every numbered proof and found further conditions and gaps; see the revised statements and Ch. 15, 15.3. Do not infer proof validity from a label or a passing test. The tests are of three kinds.

- **Seeded random tests.** The Chapter 4 algebra tests draw compatible chains. The Chapter 3 admission test runs 4,000 seeded histories of six proposals without filtering them through the engine. A finite map checks guards against the original state, then checks relationship uniqueness over the entire proposed graph. It compares application and commit separately, including the effective-edit rule, refusal atomicity and every retained state. The oracle shares the typed element and value representations. A fixed boundary test caught both a deliberately accepted stale removal and a deliberately rejected legal relationship replacement in separate runs. Three fixtures pin the UTF-8 graph encoding, including non-BMP key order and a mixed entity, ontology and relationship graph.
- **From-scratch comparison.** The second evaluator shares the catalog, typed plan and canonical encoding with the engine. It recomputes the results independently of the incremental runtime. The harness compares generated logs in the scenario catalog at single-row and largest batches, single split positions, a seeded partition and selected within-batch reversals. This tests Theorems 6.1 and 6.2 per kind of operator. For each of five identity seeds, the first generated arrival order is also run through two views that group by the merged pod. Their counts and their sums through a union and two joins match the reference under three batch cuts. [Identity tests](../engine/tests/Sattle.Differential/DifferentialTests.cs)
- **Example tests** on small inputs and on the two recordings.

The appendix (A.8) lists which test exercises which statement.

## 14.7 Performance against the hypotheses of Ch. 13

Ch. 13 (13.6) gives the status of each hypothesis. This section adds the detail of the ten-run sets, the transport and the viewer. Setup as in Ch. 13: one laptop (Apple M1 Max, 10 cores, 64 GB), always with other work running.

- **H5, from source to graph.** 160,300 records per run, 10,362 of them making a version of the master, ten runs. The authors' last set: p99 over all records 0.60 ms in memory (range over runs 0.57 to 0.69) and 2.00 ms on SQLite (1.85 to 2.17); over the records that make a version 1.11 ms (0.93 to 2.81) and 23.5 ms (20.2 to 25.9). The largest single record 148.5 ms. Set B at the same state: 0.62, 1.46, 2.00 and 22.4 ms, and one record of one run on SQLite at 1.75 s. Earlier sets lie in the same ranges.
- **H7, durable reads.** Ten runs of 1,000 reads of one element and 200 scans of neighbours. Set A: SQLite p99 0.05 ms for one element and 0.18 ms for a scan of 100; on the emulator 22.5 ms (6.5 to 32.8 over runs), median of reads 4.66 ms, scans 25.3 ms. Set B later: 6.55 ms (5.94 to 31.0) and 11.9 ms (10.5 to 32.6). The path of the read is the same in both sets, so the emulator's times follow the load of the machine.
- **H8, view of an element.** 10,000 elements, 4 series, 60 windows, 2.4 million points, at the head. The authors' last set: median 29.5 ms (16.7 to 41.1); set B, 30 runs, 17.5 ms (15.6 to 49.4); earlier sets 34.6 and 26.0 ms. No collection of unused memory in any run; 3 MB allocated per view. In each set the first runs are the slow ones, which suggests a warm-up inside the benchmark; this was not checked.
- **H10, restart.** From a SQLite journal of a repeated recording after a clean stop, with 5,435 entries at 1.2 hours and 9,602 at 4.1 hours. The authors' last set: with a seal every 2,000 entries 7.35 s (7.17 to 7.49) and 7.42 s (7.27 to 7.64), ten restarts each; without a seal 7.11 s (6.71 to 10.8) and 17.4 s (16.0 to 19.9), three each. A seal is 36.0 and 34.8 MB. Set B: 7.52 and 8.33 s with a seal, 6.64 and 16.2 s without. An earlier set on another workload is within the target too, its worst run 39.8 s without a seal. The journals of the real recordings restart in 6.2 s (1,652 entries) and 7.2 to 8.2 s (4,047 entries).
- **Transport.** The forwarder delivers about 125 events per second on one throughput unit (Ch. 10, 10.6), so live input in the test environment is limited by the transport, not by the engine.
- **The viewer**, measured by its developers in Chrome on a synthetic estate of about 99,500 nodes and 288,000 edges: moving the view at 44.7 to 60 frames per second in runs of 5 s, picking an element in about 1.4 ms, applying a patch in about 0.1 ms. With measurements on every element (1,062,871 values per tick) the page kept up with 60 ticks per second, about 64 million values per second, at 53.4 to 60 frames per second while moving. These are figures of the viewer on synthetic data and say nothing about the engine.

## 14.8 Where the prototype differs from the design

Each chapter ends with what the prototype does differently. Taken together, the differences that matter most for the claims are: `Delta`, `sync` rules and the page seal are not built; the language has no recursion over rows; the history of an element and the carry policy of a definition edit are not built; the age of retention of measurements is a setting of the running engine, not a declaration (Def. 7.13); a document has no truncation; and the Cosmos store has never run against a real account. Ch. 15 (15.2) lists every limit in one table.

## 14.9 Threats to validity

**Construct.**
- The hypotheses name Azure hardware and Cosmos DB. The measurements are on a laptop with stores in memory and on SQLite and, for Cosmos, the emulator, which charges 1 per request and is not a model of a real account's times. A pass here is not a pass there, and H3, H7 and H13 cannot be confirmed without a real account.
- H4 and H12 were measured with one advance of logical time over two graphs, not over a stack of at least three projections.
- The synthetic workload has 5,000 pairs; the reference estate about 240,000.

**Internal.**
- Mostly one run per figure. The ten-run sets report median, minimum, maximum and an interval. **No set had a machine without other load**: the authors report load averages between 14 and 115, and 239 during one restart set. A status is scoped to that configuration (Ch. 13, 13.5). It cannot be transferred to an idle or different machine merely by assuming load always makes the result slower. The emulator is also not the target store.
- The random tests are by the same authors as the proofs. Their generators are seeded and test the engine's own types. The independent implementation is a check that does not share the step-by-step strategy.
- Several statements were checked on fixtures and on real recordings, not on randomly generated projections.
- How the premise B3 was found rests on comparing exported graphs, not on instrumenting the engine. Proposition 1.8 states the minimum-id rule for compatible keys across distinctly named domains, using allocation and forwarding only. The evidence for order independence covers one synthetic input, generated arrivals on five seeds and two recordings. Concurrent merges across projections remain open. The causes behind B1 and B2 were found by set B. For a time the suite had no targeted tests of B1 and B2, and only the tests on the recordings guarded them.
- From the tenth state of the software on, the figures at the current state are set A's: the people who direct the work also verify it, and the authors' check is a reading of code and tests.
- Viewer and engine were evaluated at different times. What the viewer shows on the served newest recording was seen in a browser at the state of this revision (589 stubs, the workload model with two folded evidence elements and every state `Unknown`), and earlier in set A with the earlier health model (143 folded evidence elements, the propagated failure). No script asserts it.
- The runs on real recordings depend on a local file of names that only one machine has; the authors used a copy that matches the shared definitions, and the state digests confirm it.

**External.**
- Two recordings from one cluster with a demo workload and a generator of load. They are not a representative estate (a few hundred entities besides evidence). They have no loss messages, no markers of the forwarder in their direct sources, and no reuse of pod names. The network logs cover 2 hours of one cluster.
- The real recordings stay local, and the cluster no longer exists. A third party can repeat the runs on the fixtures and on the synthetic estate, and the synthetic benchmarks, but not the figures of the real recordings, nor the ten-run figures of H5 and H10 (their workload is the newest recording), nor anything that needs Azure.
- No comparison with other incremental engines or graph databases was run, so the paper makes no claim of relative performance.

**Conclusions.**
- Statuses use the rule of Ch. 13 as written, with its scope. A single run stays a single run, whether far from its target (H9) or far inside it (H2, H6). The three supported statuses are inside their targets by a factor of about 77 for H5 on SQLite (records that make a version), about 2 for H8 on its worst run, and about 5.7 for H10 with a seal on its worst run and 1.5 without, all under load, and each is for one configuration. H8 is the closest to its target. H7 has two labels on the emulator from two sets, which says that the emulator's figure is not a property of the design.
- Evidence that the engine matches the paper is limited to the listed tests. Unbuilt features have no implementation evidence; their model claims still require valid proofs and explicit premises. The manual audit found gaps that example tests cannot discharge (Ch. 15, 15.3). The health states on the second recording show the authored roll-up working, not that the real cluster was unhealthy.

## 14.10 What is open

Grouped by gate. The remaining expressiveness and million-node performance obligations are in Ch. 15 (15.2). Later node/record budgets and measurements exist; a complete set of per-edge and per-measurement budgets does not.

| Gate | Open item | Bears on |
|---|---|---|
| 1a | identity merge across projections that run at the same time; Def. 7.6 stated on logical time | Theorems 1.7, 6.2, 7.3; Proposition 1.8 |
| 1a | the history of an element, the carry policy, rebuild, provenance semirings, the cost of a definition edit (H11) | Ch. 9 |
| 1a | Composition of two editable lenses, if a definition needs it. The current checker rejects a lens over another lens and a lens over `Join`. The engine supports one lens over a bijective rule (Ch. 8, 8.5). [Checker decision](https://github.com/abossard/sattle/issues/116#issuecomment-6962758051) | Theorem 8.3 |
| 1a | Checking the premises of a proposed refactoring. Tests cover extract, inline and move on generated inputs and counterexamples that violate R3 or R4. [Refactoring tests](../engine/tests/Sattle.Differential/RefactoringTests.cs) | Proposition 5.7 |
| 1a | For levels, checking at ingestion that each item maps to at most one group, testing composition beyond two levels, and exercising capabilities in a real recording. Generated graphs already cover lifting for the two shipped projection shapes (Ch. 15, 15.2). | Ch. 5, 5.8 |
| 1a | an independent check of the canonical encoder; digests on other platforms; reuse of pod names; merge across projections | Ch. 1, 2, 10 |
| 1b | aggregates of `Rollup` over a gauge and over a running total evaluated on data; `last` over a gauge built or removed from Def. 5.6 | Ch. 5 |
| 1b | `sync` rules; `Delta`; a measured cost of `Join` | Ch. 6, 8; H14 |
| 1c | the Cosmos store on a real account: times, cost of the 32 requests per version, the path for large commits, the lease | Ch. 11; H3, H5, H7 |
| 1c | the ten-run figures on a machine without other load and on the reference machine; ten runs for the other hypotheses; H5 and H10 with the whole health model; H10 with a kill during input, with new data, and on Cosmos | every status of Ch. 13 |
| 1c, 2c | the length of history at which a seal starts to pay (between 1.2 and 4.1 hours here); failure during a journal write under power loss (Ch. 15, 15.2); damage to a seal that keeps its digests; a seal another build can read; repeated live resume and replay on a real stream | H10; Propositions 10.7, 10.8, 11.9, 11.10 |
| 1c | time at the size of the estate: closing a window takes 1.5 to 1.7 s against 1 s; the whole engine holds 5.4 GB | H4, H12; Ch. 11 |
| 1c | retention of evidence: the memory saved in bytes, the choice of age, delivery twice and withdrawal by a source tested for evidence elements | Ch. 9, 9.6 |
| 2a | health model: edges between Azure resources, `Unknown` with a reason, a message for chains beyond the bound of depth | Ch. 7, 7.4 |
| 2b | truncation of documents; escaping as a property; the digest the viewer shows compared with the engine's at every version; a replica compared end to end on a real recording and on a feed that is still arriving; `rev_of`; editing data views in the viewer; a screen reader; other browsers; a person outside the project reading the model | Ch. 12 |
| 2b | lineage through windowed aggregates, thresholds and series | Ch. 9, 9.5 |
| 2c | live runs on a real stream processed again from their recordings with the same horizon, beyond one example; the markers of loss of the forwarder on a real stream; H5 on the live path; more than one partition and more than one throughput unit; the delay from a source to the view as a distribution, not single examples | Ch. 10 |

## 14.11 Review reruns, 22 November 2026

The review used revision `54bbb6213d28801f41338061319f47a6b7d4b909` before text edits: Ubuntu x64, .NET 11, two logical cores and about 7.5 GiB total memory, shared with other work. Viewer checks used Node.js 22 from the Canvas directory. The artifact records commands and results in `evidence/review-2026-11-22.txt`.

All default and slow engine suites completed, including the reference comparisons. Capture-dependent tests skipped because the inputs were absent; the two local-emulator conformance tests were rerun and passed. The 60 viewer test files passed across the full run and a targeted rerun after correcting the working directory. Contract, lab and guide checks also passed.

The synthetic estate gave one final digest at batch sizes 1, 50 and 100,000, with 73 entities and 69 relationships in the master graph. This reproduces a current-fixture result, not the unpublished recordings.

Performance was not uniformly within target. The exact hot-graph benchmark held 750,000 entities and approximately 2.5 million relationships in 387 MB. The 30-run element-view benchmark had a 53.0 ms median but a 154.0 ms maximum: under the every-run rule, it is **inconclusive**, not supported. The local-emulator read benchmark exceeded both targets and is **refuted for that bounded emulator configuration**, not for Cosmos in Azure. Sealed synthetic restart was within 60 s in all ten runs; unsealed restart had a 51.1 s median and an 89.2 s maximum and is inconclusive.

The 10,000-node long-run generator produced 109 steady closes, p99 137 ms. The 100,000-node generator produced nine steady closes, maximum 1.128 s; that is not a long-run p99 result. The Linux host could not run the million-node workload.

A second run used the generated million-node input on a shared 64 GB M1 Max, one worker, the in-memory store and a 12 GiB managed-heap limit. At revision `dc8270b` the engine replayed 8,159,533 records in 333.52 s. The digest matched the published abbreviation. Peak resident memory was 12.336 GB and process CPU was 41.88 µs/record, both above the budgets of 12 GB and 37 µs. The first populated close took 65.235 s. The nine later closes had a median of 4.190 s and a maximum of 4.552 s. Nine observations do not establish a long-run p99. The evidence file records the command and full digest.

On the corrected engine, all ten capture tests passed using the two private recordings and the local overlay. For each recording the suite compared the graph and telemetry digests and integrated patches at chunk sizes 1, 50 and 100,000. The 95-minute and 17-minute recordings produced 15,241 and 48,287 master entities, respectively, versus the historical 15,154 and 48,201 in 14.5. Both had zero identity conflicts, with 35 and 43 merges. The comparison across cuts holds within this build. The recordings are unavailable to a reader starting from the public repository. The follow-up used no live stream or Azure account.
