# Ch. 14 Evaluation

**Question.** What did running the prototype show about the claims of Chapters 1 to 13, on what data, by whom, and what does it not show?

**What you can state at the end.** The whole pipeline runs on two real recordings from a test cluster, from the records of every source to a health model, and gives one state digest per recording whatever the size of the batches. Every numbered statement of the paper has a status: proved, proved relative to a cited result or a named premise, or tested only; tests support a proof and never replace it. Three performance hypotheses are supported, each for one named configuration on a laptop under load. Everything else is met in one run, not met, or not measured, and is listed as open. The evaluation is partial: one machine, two recordings from a cluster that no longer exists, mostly one run per figure.

This chapter uses all earlier chapters.

**Running example.** The shop of the earlier chapters is invented. The real test cluster ran a demo workload of the same shape: `frontend`, `api` and `db`, a generator of load, and an `intruder` that the network policy denies. The synthetic estate used by the tests copies the shop: cluster `synth`, namespace `shop`, services `web`, `api` and `db`.

## 14.0 Results by gate

| Gate | Where | What the results amount to |
|---|---|---|
| 1a theory | 14.5, 14.6 | Every statement has a status and a row of test evidence. Determinism of replay (Theorem 6.3) and batch invariance (Theorem 6.2) hold on both real recordings at five batch sizes, at the states of the software measured in 14.5 |
| 1b flexible and expressive | 14.3 | A list of what was built. No result of this chapter is stated or tested as a claim of expressiveness |
| 1c efficient | 14.7, 14.9 | Three hypotheses are supported for named configurations at the scale of the recordings. The memory of the whole engine and the time to close a window at the size of the estate are above target. Nothing ran at 1 million master nodes, and no budget per element exists to compare with |
| 2a health model from real Azure data | 14.2, 14.4 | The pipeline runs end to end on real data and computes a health model. Early evidence: one workload, a cluster that no longer exists |
| 2b understandable by people | 14.4, 14.6, Ch. 12 | Lineage and the views of the viewer exist and are checked by scripts. No person outside the project has read the model |
| 2c always live | 14.6, 14.7 | Restart and resume are tested; the live reader only against a stand-in. Nothing is measured from a source to the view |

## 14.1 Method and evidence

**Kinds of evidence.** A claim has one of these levels. A higher level does not replace a lower one.

| Kind | Meaning |
|---|---|
| A | a proof in the paper |
| B | a seeded random test: a generator of random cases from fixed seeds, 8 groups of 500 cases, 4,000 cases per property |
| C | an example test, on small inputs |
| D | a benchmark |
| E | a run on a real recording, or another run by the authors |

**Three sets of runs.** Every figure comes from one of three sets of runs, and says which when it is not the first.
- *The authors' runs*: made by the authors on a clean copy of a recorded state of the software.
- *Set A*: the full runs of tests and checks that the project makes before it records a state of the software, including the tests of the cloud database on its local emulator. The authors read their output and did not repeat them.
- *Set B*: runs by the developers of the engine, reported with their code, not repeated by the authors.
The authors' runs took place at nine successive states of the software; from the tenth state on, the authors read the code and the figures are set A's. The log of the twelve evaluations, with the files of each, is in the appendix (A.9).

**Rule of decision** for hypotheses (Ch. 13, 13.5): a target of time or size is *supported* iff the statistic is at or below the target in every one of at least ten runs, *refuted* iff the median run is above it, and *inconclusive* otherwise; a status holds for one configuration. A figure from fewer than ten runs is reported as met in one run, not met in one run, or not measured.

## 14.2 The test environment and the recordings

- **Cluster.** A managed Kubernetes cluster in Azure (AKS, Kubernetes 1.34), with the network observer and a managed measurement system. It no longer exists: its group of resources was deleted on 2026-11-10, not by this project, as set A reports. Both recordings were taken before that. No further recording can be taken.
- **Recording.** A script records, per source, the envelopes of Def. 10.2: the Azure resource list, Kubernetes list and watch, flows of the network observer, a measurement system, Azure measurements, Azure's log of management actions, the network logs of Azure, and the copies sent through Event Hubs. A forwarder sends the same envelopes to Event Hubs for the live input (Ch. 10, 10.6). Setting up and removing the environment took 3 min 14 s and 2 min 23 s once, as its developers measured; the authors cannot repeat it.
- **The two recordings.** The *95-minute recording* (5,679 s): 245,345 records, among them 235,858 flows of the network observer over 725 s and 522 records of network logs with 175,265 tuples. The *17-minute recording* (1,017 s): 160,300 records, from every source. Both hold real names and are kept outside the project's repository. The names of the real workload are given to the definitions by a local file next to the recordings; the shared definitions bind invented names.
- **Fixtures.** Samples of the recordings with every real name replaced, kept in the repository, which the tests process. And the **synthetic estate**: 548 records from every source the engine reads, written by a program with invented names only, so that every operator has data. It is also the default content of the viewer (Ch. 12).
- **Size.** The test estate is small. After the 95-minute recording the master graph has 15,154 entities, of which 14,622 are events kept as elements for 24 hours of logical time (4,135 of them evidence elements with a subject, the rest audit events) (Ch. 9, 9.6); the other 532 are the estate itself. The reference estate of Ch. 11 has 750,000 entities. The recordings show that the pipeline works end to end, not that it scales.

## 14.3 What was tested

- **Engine.** Fifteen modules of definitions, loaded from text and checked with no error and no warning. Adapters for every source of 14.2. 19 of the 20 operator kinds (Ch. 5; `Delta` is missing), aggregates per context, parameters, levels written with `lift`, rules `lens` and `bijective` with backward edits (Ch. 8), identity merge with a survivor that does not depend on the order (Ch. 1), references for missing ends (Ch. 3). Stores in memory, on SQLite and on Cosmos DB (the last against the emulator only), the journal and state seals (Ch. 11). A live input that resumes from the journal (Ch. 10; not run, its loop tested with a fake source). The examples: the workload model (Ch. 7), deployment health and its report (Ch. 2, 12), the service and capability levels (Ch. 5).
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
- **Reading.** Theorem 6.2 holds on the graphs, measurements and documents of both recordings under five batch sizes. B1 and B2 are covered by targeted tests and by the digests of the recordings. B3 holds on these two inputs, on the merge examples of the tests, and on one synthetic input in two orders of arrival at four batch sizes. **No generator produces random orders of arrival of keys**, so B3 is a tested premise, not a generally tested one.

**What this does not cover.** Interleavings of independent sources beyond the shuffled arrivals of the tests of live reproduction (up to 40 places, three seeds, on the fixtures; one more on the newest recording). Repeating the same run many times on a real recording. Other platforms. A Cosmos run on a real recording beyond the emulator (Ch. 11, 11.6). A live run processed again from its recording on the real reader (Proposition 10.7 is tested against a stand-in). The state digest of a repeated recording is the same with and without seals.

## 14.6 Statements against tests

Every numbered statement of the paper has a status: proved, proved relative to a cited theorem, proved under a named premise, or tested only. The inventory gives each one. Tests can support a proof, never replace it. They are of three kinds.

- **Seeded random tests.** A generator produces random cases from fixed seeds, 4,000 cases per property, so a run can be repeated exactly. They cover the algebra of changes and the versions of a graph (Ch. 3, 4), with one caveat: the generator keeps only changes that the prototype itself accepts, so a wrong rejection cannot be seen.
- **Comparison with an independent implementation.** A second, simple program computes every result from scratch, from the definitions of this paper. The prototype's step-by-step result is compared with it on generated inputs, in 32 small worlds, under every way of cutting the input into batches and with reordering inside a batch. This tests Theorems 6.1 and 6.2 per kind of operator. `Lift` and `Fixpoint` are not covered independently, because the simple program uses the prototype's own translation of them. A `Group` keyed by an identity that merges has no case (Ch. 6, 6.6).
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

Each chapter ends with what the prototype does differently. Taken together, the differences that matter most for the claims are: `Delta`, `sync` rules and the page seal are not built; `Fixpoint` is a bounded unrolling; the history of an element and the carry policy of a definition edit are not built; the age of retention of measurements is a setting of the running engine, not a declaration (Def. 7.13); a document has no truncation; and the Cosmos store has never run against a real account. Ch. 15 (15.2) lists every limit in one table.

## 14.9 Threats to validity

**Construct.**
- The hypotheses name Azure hardware and Cosmos DB. The measurements are on a laptop with stores in memory and on SQLite and, for Cosmos, the emulator, which charges 1 per request and is not a model of a real account's times. A pass here is not a pass there, and H3, H7 and H13 cannot be confirmed without a real account.
- H4 and H12 were measured with one advance of logical time over two graphs, not over a stack of at least three projections.
- The synthetic workload has 5,000 pairs; the reference estate about 240,000.

**Internal.**
- Mostly one run per figure. The ten-run sets report median, minimum, maximum and an interval. **No set of runs had a machine without other load**: the load average was between 14 and 115, and 239 during one set of three restarts. A status is therefore scoped to that load (Ch. 13, 13.5). For an upper bound on time, load can only raise times, so a pass under load holds all the more, and a failure under load is weak evidence; the one failure, H7 on the emulator, is also on a store that is not the target.
- The random tests are by the same authors as the proofs. Their generators are seeded and test the engine's own types. The independent implementation is a check that does not share the step-by-step strategy, but it shares the translation of `Lift` and `Fixpoint`.
- Several statements were checked on fixtures and on real recordings, not on randomly generated projections.
- How the premise B3 was found rests on comparing exported graphs, not on instrumenting the engine. That the survivor rule removes the dependence on order in general is Proposition 1.8; its tests are one synthetic input and two recordings. The causes behind B1 and B2 were found by set B. For a time the suite had no targeted tests of B1 and B2, and only the tests on the recordings guarded them.
- From the tenth state of the software on, the figures at the current state are set A's: the people who direct the work also verify it, and the authors' check is a reading of code and tests.
- Viewer and engine were evaluated at different times; what the viewer shows on the served newest recording (589 stubs, 143 folded evidence elements, the propagated failure) was seen in a browser in set A and is not asserted by a script.
- The runs on real recordings depend on a local file of names that only one machine has; the authors used a copy that matches the shared definitions, and the state digests confirm it.

**External.**
- Two recordings from one cluster with a demo workload and a generator of load. They are not a representative estate (a few hundred entities besides evidence). They have no loss messages, no markers of the forwarder in their direct sources, and no reuse of pod names. The network logs cover 2 hours of one cluster.
- The real recordings stay local, and the cluster no longer exists. A third party can repeat the runs on the fixtures and on the synthetic estate, and the synthetic benchmarks, but not the figures of the real recordings, nor the ten-run figures of H5 and H10 (their workload is the newest recording), nor anything that needs Azure.
- No comparison with other incremental engines or graph databases was run, so the paper makes no claim of relative performance.

**Conclusions.**
- Statuses use the rule of Ch. 13 as written, with its scope. A single run stays a single run, whether far from its target (H9) or far inside it (H2, H6). The three supported statuses are inside their targets by a factor of about 77 for H5 on SQLite (records that make a version), about 2 for H8 on its worst run, and about 5.7 for H10 with a seal on its worst run and 1.5 without, all under load, and each is for one configuration. H8 is the closest to its target. H7 has two labels on the emulator from two sets, which says that the emulator's figure is not a property of the design.
- Evidence that the engine matches the paper is limited to the listed tests. Where there is no test (`sync` rules, a stack of lenses, provenance semirings, `Delta`, `Fixpoint` on cyclic input), the claims stand as proofs only. Several statements rest on proofs with example tests or none: Proposition 1.8, Proposition 5.7, Proposition 5.9, Propositions 7.7 and 7.8, Proposition 9.11, Propositions 10.7 and 10.8, Propositions 11.9 and 11.10, Proposition 12.10, and the premises B1 to B3 of Theorem 6.2. The health states on the second recording show the roll-up working; they do not show that the cluster was unhealthy.

## 14.10 What is open

Sorted by gate. The two items that define gates 1b and 1c, a stated claim of expressiveness and budgets at 1 million master nodes, are in Ch. 15 (15.2).

| Gate | Open item | Bears on |
|---|---|---|
| 1a | a generator of orders of arrival of keys for premise B3; identity merge across projections that run at the same time; Def. 7.6 stated on logical time | Theorems 1.7, 6.2, 7.3; Proposition 1.8 |
| 1a | the history of an element, the carry policy, rebuild, provenance semirings, the cost of a definition edit (H11) | Ch. 9 |
| 1a | a stack of lenses; a lens over `Join` | Theorem 8.3 |
| 1a | refactoring on other modules and on generated projections | Proposition 5.7 |
| 1a | levels: the image checked to be a function when data arrive; lifting and its lowering on generated graphs and over more than two levels; capabilities in a real recording | Ch. 5, 5.8 |
| 1a | an independent check of the canonical encoder; digests on other platforms; reuse of pod names; merge across projections | Ch. 1, 2, 10 |
| 1b | aggregates of `Rollup` over a gauge and over a running total evaluated on data; `weightedMean` and `last` over a gauge built or removed from Def. 5.6 | Ch. 5 |
| 1b | `sync` rules; `Delta`; `Fixpoint` on cyclic input with the condition F5 checked; a measured cost of `Join` | Ch. 6, 8; H14 |
| 1c | the Cosmos store on a real account: times, cost of the 32 requests per version, the path for large commits, the lease | Ch. 11; H3, H5, H7 |
| 1c | the ten-run figures on a machine without other load and on the reference machine; ten runs for the other hypotheses; H5 and H10 with the whole health model; H10 with a kill during input, with new data, and on Cosmos | every status of Ch. 13 |
| 1c, 2c | the length of history at which a seal starts to pay (between 1.2 and 4.1 hours here); a journal cut inside an entry; damage to a seal that keeps its digests; a seal another build can read; live resume and live reproduction on a real stream | H10; Propositions 10.7, 10.8, 11.9, 11.10 |
| 1c | time at the size of the estate: closing a window takes 1.5 to 1.7 s against 1 s; the whole engine holds 5.4 GB | H4, H12; Ch. 11 |
| 1c | retention of evidence: the memory saved in bytes, the choice of age, delivery twice and withdrawal by a source tested for evidence elements | Ch. 9, 9.6 |
| 2a | health model: edges between Azure resources, `Unknown` with a reason, a message for chains beyond the bound of depth | Ch. 7, 7.4 |
| 2b | truncation of documents; escaping as a property; the digest the viewer shows compared with the engine's at every version; a replica digest end to end; `rev_of`; editing data views in the viewer; a screen reader; other browsers; a person outside the project reading the model | Ch. 12 |
| 2b | lineage through windowed aggregates, thresholds and series | Ch. 9, 9.5 |
| 2c | the live input run on a real stream and processed again from its recording with the same horizon; markers of the forwarder on a real stream; H5 on the live path; more than one throughput unit | Ch. 10 |
