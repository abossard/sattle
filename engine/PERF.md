# Performance

Measured, not claimed. Each section names the command that produces its numbers; the first sections were measured in turn 2 on one machine, once per figure unless a range is given, and are reproduced by `sattle bench` and `sattle bench estate`. Targets are the hypotheses of research 02 section 5.1 and open question Q14 of research 03.

## Machine and build
- Apple M1 Max, 10 cores, 64 GB, macOS, .NET SDK 10.0.403, `dotnet build -c Release`, in-process engine, no other load.
- Single writer thread: the engine processes inputs sequentially, as the design requires (ADR-017).
- Run: `dotnet src/Sattle.Cli/bin/Release/net11.0/sattle.dll bench` (defaults: 10 M rows, 1,000 pods, 100 services). `--trace-alloc` adds bytes per operator.

## Method (Q14)
- Workload: Example 1 `observedCalls` (filter on present endpoints, 60 s window, group by pod pair with `count` and `sum(if(...))`, emit `Calls` with telemetry) plus the whole Example 2 chain (`Match`, `Series`, `Rollup`, `Map`, `Threshold`, emits) on the derived graph. Synthetic flows, 1,000 pods with 5 peers each (5,000 pod pairs), 2 percent dropped.
- Rows are built once as columnar batches of 50,000 rows with interned codes. Only the `Engine.Ingest` calls are timed (Stopwatch), so adapter JSON parsing and row generation are excluded. Rows per second is rows divided by the summed time of those calls.
- Allocation: `GC.GetAllocatedBytesForCurrentThread` around the same calls, divided by rows.
- Window close: one `Engine.Advance` per 60 s window (100 windows, 100,000 rows each). The call includes emitting the closed groups, identity lookups, telemetry append, the derived graph's `Series`, `Rollup`, `Threshold` and its commit. p50, p99 and max over the 100 calls.
- Reference loop: a hand-written fused loop for the same query over the same batches with a `Dictionary` accumulator (40 passes over the 4 pooled batches). It stands in for what compiled delegates could reach on this layout.
- Commit: 2,000 patches of 50 entity creations through the storage port, in memory and on a SQLite file (WAL, `synchronous=NORMAL`).
- Reads: 5 M point reads and 1 M one-hop scans on the hot `Graph` after the run.
- Memory: managed heap delta (`GC.GetTotalMemory(true)`) after building 750,000 entities and 2.5 M relationships in a `Graph`.
- No benchmarking library: Stopwatch and GC counters, one fewer dependency (BenchmarkDotNet is the better tool for micro results).

## Results against targets

| Id | Target (hypothesis) | Measured | Verdict |
|---|---|---|---|
| Q14 rows per second | none stated; a useful throughput | 17.0 M flow rows/s through the plan (10 M rows in 0.59 s of ingest calls) | meets any plausible need (lab estate is about 330 rows/s) |
| Q14 allocations per row | none stated | 1.8 bytes per row (18.4 MB in total, 11.7 MB of it in the windowed group `pairs`, per `--trace-alloc`) | window tables are pooled and pre-sized; it was 22 bytes per row before pooling |
| Q14 window close | p99 latency | p50 29 ms, p99 62 ms, max 156 ms per close of 5,000 pairs (master and derived graph together) | fine for 60 s windows; the allocation is 17.8 MB per close |
| Q14 interpreter vs compiled | does the vectorized interpreter suffice | interpreter 17.0 M rows/s, hand-fused loop 47.5 M rows/s (2.8 times). No IL or delegate compiler was built, so no third number | interpreter suffices at this scale; the gap is the headroom for a compiler |
| H1 structural ingest | at least 5,000 accepted records/s | 243,457 capture records in 3.6 s end to end including JSON parsing = 67,000 records/s on the real capture (flows, pods, resources, metrics); structural revisions: 146 in that run | met |
| H2 telemetry ingest | at least 200,000 samples/s into the ring | 7.8 M samples/s `Append` into the in-memory ring | met |
| H3 commit latency | p99 at most 20 ms (Cosmos target) | in memory p99 0.08 ms, SQLite p99 4.3 ms, max 8.5 ms for 50-edit patches | met locally; Cosmos not built |
| H4 projection latency | p99 at most 1 s master commit to derived commit | covered by the window-close figure: master and derived graph in one call, p99 62 ms | met |
| H6 point read | at most 1 us | 250 to 410 ns in two runs (was 20 ns on the first layout: an element is now materialized from columns on every read) | met |
| H6 one-hop scan | at most 0.5 us plus 5 ns per neighbour | 230 to 270 ns for 6 neighbours (was 109 ns) | met |
| H9 memory | at most 400 MB for 750,000 entities and 2.5 M relationships (hot graph, no telemetry) | 337 MB in the hot layout (was 2,395 MB in the reference layout, kept as `ReferenceGraph` in the tests) | met for the graph structure; see the estate run for the whole engine |
| H5, H7, H8, H10 | latency from receipt, durable read, element view, cold start | see "H5, H7, H8, H10" below (each with its command) | see below |

## Reading the numbers
- The hot path is a vectorized interpreter over interned columns with selection vectors. Linear operators share column arrays and allocate nothing per row. The group table is open addressing over fixed-width long keys, allocation free once sized and recycled across windows.
- Window close is the expensive step, not ingest: it creates relationship elements, resolves identities, appends telemetry and runs the derived graph. It scales with groups closed, not rows ingested.
- H9 is met by the second layout of `Graph` (`Graph.cs`): ids interned once into a UTF-8 byte pool with an open-addressing index, one column per attribute (type, props reference, endpoints), properties as compact bytes with in-place update, intrusive singly linked adjacency lists per endpoint, and an open-addressing uniqueness index over (source, target, type) that stores only handles. 337 MB for 750,000 entities and 2.5 M relationships, about 100 bytes per element. The API still takes and returns string ids and records; elements are materialized on read, which is why reads cost 250 to 410 ns instead of 20 ns.
- Deviation from research 02 section 5.2: adjacency is a linked list per endpoint, not a CSR with a per-vertex delta log. Removal is O(degree). The reference layout is kept as `ReferenceGraph` and a differential test (`HotGraphTests`) compares every operation on random edit sequences.
- The synthetic workload has 5,000 pair series. A 240,000-edge estate (research 02 section 2.3) was not run. Window close time grows with the number of pairs closed per window.
- Real capture: `sattle replay CAPTURE` prints the telemetry sample counts of the master graph and the Hubble flow count; the `flows` and `drops` summed in the master graph equal an independent count of the capture with `jq` (the flows of the capture between two named pods, and the `DROPPED` verdicts).

## Large estate (research 02 section 2.3)
`sattle bench estate`: 150,000 pods, 30,000 services, 300,000 Azure resources (480,000 master entities after structure), 240,000 active pod pairs, 1 M flow rows per 60 s window, 4 windows. One run.
- Structure load 6.7 s (pods, services, endpoints), resources 4.0 s.
- Window 0 closes in 9.0 s (creates about 200,000 `Calls` relationships, resolves their identities and derives the service tree: 149,000 `DependsOn` relationships). Windows 1 to 3 close in 1.5 to 1.7 s each (240,000 pair groups, telemetry append, `Series`, `Rollup`, `Threshold`, derived commit).
- Flow ingest including row generation 3.6 M rows/s.
- Whole-engine memory is **not** at the 400 MB target: managed heap 2.5 GB after structure and 5.4 GB at the end (3.6 M telemetry windows). The hot graphs are a small part. The rest is the in-memory store (element versions, adjacency versions, every patch kept for `Follow`), the support-count layers of the emit sinks, lineage records, the identity catalog and the interner. Moving those off the object heap (SQLite store, compact layers) is not done.
- A window close at this size is 1.5 s against a 60 s window, so the plan keeps up with the estate on one thread; the first window and memory are the open problems.

## Large estate with the health tree (engine turn 14)
`sattle bench estate --windows 3` (defaults: 150,000 pods, 30,000 services, 300,000 Azure resources, 240,000 active pod pairs, 1 M flow rows per window) now also ingests one signal rule and one CPU reading per resource and window (300,000 `Meter` series; the first 1 % of the resources run hot from window 1), so the tree graphs carry 451,006 elements (100 subscriptions, 900 resource groups, 300,000 resources, 1 cluster, 5 namespaces, 150,000 pods), 300,000 signals; `healthTree` has 751,006 entities and 750,905 relationships. `--no-tree` leaves the readings and the rule out (the tree graphs are still in the space). `--trace-alloc` prints time and allocation per operator for the structure and resource loads. One run each, the machine shared with other sessions.
- Two quadratic costs found and fixed by this run: the touch check of `Traverse` (6.8 s of 8.8 s at 10,000 resources, now 0.1 s; it walked the patch for every start) and the identity merge of the output layers (`Remap` scanned every element per merge, 66 s for 20,000 pods, now 0.3 s; it keeps an index from an endpoint identity to its relationships).
- Structure (pods, services, endpoints) 50 s with the tree (36 s with `--no-tree`), resources 90 s (49 s).
- Window close with the readings: window 0 97 s (everything is derived once: 952,724 master relationships, the service tree, the tree), windows 1 and 2 10.3 s and 9.6 s against a 60 s window. Without the readings: 10.8 s, 2.5 s, 2.3 s. So the readings (300,000 meter rows per window through `Meter`, `MeterOf`, `treeSignals`, the roll-up and the tree) cost about 7.5 s per window, the flows and the rest 2.3 s. The plan keeps up on one thread; window 0 does not (it is a load, not a steady window).
- Memory is the limit: managed heap 15.4 GB after the structure and the resources (15.6 GB with `--no-tree`: the readings are not what holds it), 32.8 GB at the end with the readings (3.6 M telemetry windows), 19.8 GB without. At 24,000 master entities the tree graphs (`treeStructure`, `treeRollup`, `healthTree`) add 0.9 GB to a base of 0.38 GB (measured by taking graphs out of a copy of the space), about 45 KB per tree element against about 15 KB per master entity. What holds it is the per-element state of the engine, not a column store: the in-memory store (versions of every element and adjacency, every patch kept for `Follow`), the contribution layers of the emit sinks, lineage records, the identity catalog, and the operator state of `Traverse` (reach and its signature strings per start). Not profiled further (no heap profiler on the machine); moving the store and the layers off the object heap is the open work, as in the section above.

## Baseline for the efficiency concept
Measured, not optimized: the code is the working tree at the end of engine turn 14 plus the measurement code (`Census.cs`, `Bench.Phases.cs`, `--census`, `--trace-alloc` on `replay` and `bench estate`, live-bytes lines). Raw outputs are in `engine/out/` (gitignored), file names below. Machine: Apple M1 Max, 10 cores, 64 GB, .NET 10, Release build, **shared with other sessions: load average 15 to 225 during these runs**, so every time is pessimistic and noisy (the first window of the same 100,000 run took 11.3 s, 19.1 s and 12.8 s in three runs). Byte figures do not depend on the load. One run per figure unless stated. Cosmos is not involved anywhere in this section (no emulator was started; every test time below is without it).

### How bytes are measured
- `Census.cs` walks the object graph of the engine with reflection (shared lookups first, then the store, the state of each graph, the output layers of each graph by field, the operator state by operator kind) and adds the layout size of every object once, for the first structure that reaches it. Callbacks are not followed. Coverage against the live bytes (what a forced compacting collection promotes): 98 % on the capture, 99 to 100 % on every estate run. Flag: `--census` on `replay` and `bench estate`.
- `GC.GetTotalMemory(true)`, which the older lines in this file use ("managed heap"), reads higher than the live bytes on the same heap and the ratio is not constant (8.8 GB against 5.2 GB at 100,000 nodes with the tree, 433 MB against 248 MB on the capture, 53.7 GB against 49.4 GB at 1,000,000), and the working-set line of the old runs was a stale reading. The new lines say "live bytes". The 15.4 GB and 32.8 GB of the 480,000 run above are `GetTotalMemory` figures, not live bytes; by the linear scaling below the live bytes of that run were about 24 GB at the end. The runs of this section are the ones to quote.
- "Master nodes" below are the structure nodes of the estate (pods + services + Azure resources). The readings add 62.5 % more entities (`Meter`) to the master.

### Size ladder (`bench estate --windows 3`; sizes keep the ratio 5 pods : 1 service : 10 resources; pairs = 0.5 per node, flow rows per window = 2 per node)
| master nodes | variant | live bytes at the end | per master node | structure + resources load | windows 0, 1, 2 close | peak RSS | file |
|---|---|---|---|---|---|---|---|
| newest capture (50,266 entities) | without the five tree graphs | 218 MB | 4.3 KB | replay 160,300 records in 7.6 s | 16 advances | | `capture-notree.txt` |
| same | with the tree graphs (no rules, all Unknown) | 248 MB | 4.9 KB | 11.5 s | | | `capture-tree.txt` |
| 25,000 | without tree graphs, without readings | 306 MB | 12.2 KB | 2.3 s | | | `estate-25k-notree.txt` |
| 25,000 | tree graphs and readings | 1,380 MB | 55.2 KB | 3.9 s | | | `estate-25k-tree.txt` |
| 100,000 | without tree graphs, without readings | 1,038 MB | 10.4 KB | 13 s | 2.2, 0.4, 0.8 s | | `estate-100k-notree.txt` |
| 100,000 | tree graphs, no readings | 3,552 MB | 35.5 KB | 20 s | max 2.1 s, p50 0.34 s | | `estate-100k-treegraphs-noreadings.txt` |
| 100,000 | tree graphs and readings | 5,188 MB | 51.9 KB | 26 s | 12.8, 0.8, 1.5 s | | `estate-100k-tree.txt` |
| 250,000 | tree graphs and readings | 12,540 MB | 50.2 KB | 52 s | 30.5, 2.8, 2.8 s | 15.1 GB | `estate-250k-tree.txt` |
| 1,000,000 | without tree graphs, without readings | 9,625 MB | 9.6 KB | 56 s | 23.8, 5.1, 5.2 s | 14.3 GB | `estate-1m-notree.txt` |
| 1,000,000 | tree graphs and readings | **49,386 MB** | 49.4 KB | 267 s | 173.6, 25.7, 25.5 s | 31.5 GB (see below) | `estate-1m-tree.txt` |
- Memory is linear in the nodes (49 to 55 KB per node with the tree and readings from 25,000 to 1,000,000; about 10 KB without). The total is linear in the measured range (the per-structure split was taken at 100,000 only; the 25,000 census has the same shape).
- The 1,000,000 run with the tree **fits in 64 GB only barely**: 49.4 GB live, 53.7 GB by `GetTotalMemory`, and a peak resident set of 31.5 GB because macOS compressed the rest; GC pause 27 % of the wall time (182 s of 675 s), 241 GB allocated in total. It leaves no room for a second process. The run was guarded to be killed at 50 GB RSS, which was not reached. Without the tree graphs and the readings the same estate is 9.6 GB.
- Commands: `dotnet engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll bench estate --trace-alloc --census --windows 3 --pods 31250 --services 6250 --resources 62500 --pairs 50000 --per-window 208000` (100,000; add `--no-tree` to leave the readings and the rule out; 1,000,000: `--pods 312500 --services 62500 --resources 625000 --pairs 500000 --per-window 1000000`, without `--census`: the walk needs a visited set of about 600 million objects). "Without the tree graphs": copy `engine/definitions`, delete the five `graph` lines `treeStructure`, `treeSignals`, `treeRollup`, `healthTree`, `healthReport` from the copy of `acme.space.sattle`, run from the directory that holds the copy (`bench` finds `definitions` upward from the working directory). Capture: `sattle replay CAPTURE --trace-alloc --census`, the second variant with `--defs COPY --overlay COPY_OF_lab/captures/space-overlay.sattle` without the five lines.

### Retained bytes per structure (100,000 master nodes, census; MB)
| structure | no tree graphs, no readings | tree graphs | tree graphs + readings | bytes per master node (last column) |
|---|---|---|---|---|
| store: patches kept for `Follow` (`Commits`) | 76 | 252 | 480 | 4,800 |
| store: element versions | 52 | 164 | 226 | 2,260 |
| store: adjacency versions | 33 | 115 | 162 | 1,620 |
| store: second copy of the hot graph (`Head`) | 48 | 159 | 282 | 2,820 |
| store: identity bindings (copy of the catalog's) | 47 | 83 | 141 | 1,410 |
| hot graph state (columnar, all graphs) | 46 | 150 | 267 | 2,670 |
| sink: contribution layers (support per element) | 66 | 294 | 384 | 3,840 |
| sink: lineage records | 43 | 160 | 209 | 2,090 |
| sink: support counts and group members of group emits | 34 | 197 | 254 | 2,540 |
| sink: endpoint index `relsAt` (turn 14, a cache) | 19 | 86 | 129 | 1,290 |
| sink: other (touched sets, telemetry buffer) | 17 | 26 | 46 | 460 |
| operator state `JoinOp` (hash indexes) | 106 | 553 | 674 | 6,740 |
| operator state `GroupOp` (accumulators, ordered sets) | 3 | 343 | 514 | 5,140 |
| operator state `TraverseOp` (reach, signature strings) | 0 | 359 | 359 | 3,590 |
| operator state `SeriesOp` | 93 | 93 | 283 | 2,830 |
| operator state, all others (Emit*, Filter, Map, Threshold, WindowedGroup, Rollup, Match, Union) | 130 | 210 | 327 | 3,270 |
| telemetry windows (ring in the in-memory telemetry store) | 111 | 111 | 216 | 2,160 |
| interner | 85 | 130 | 159 | 1,590 |
| identity catalog | 11 | 21 | 42 | 420 |
| engine rest, statics | 14 | 14 | 14 | 140 |
| **live bytes** | **1,038** | **3,552** | **5,188** | **51,880** |
Source: `out/estate-100k-notree.txt`, `out/estate-100k-treegraphs-noreadings.txt`, `out/estate-100k-tree.txt` (each also has the top 40 types by size and the table per graph). The journal and the served-history buffers are not in memory in these runs (`bench estate` has no host; the `replay` run attaches the canvas export, 18 MB on the capture).
- What holds the memory (tree and readings, 100,000): the store's copies are 1.29 GB (25 %), the operator state 2.16 GB (42 %), the sink layers 1.02 GB (20 %), the hot graph itself 0.27 GB (5 %), telemetry 0.22 GB, interner 0.16 GB. Per element of its graph (state, store, sink layers, operators of that graph): master 2.2 KB (362,000 entities and relationships), `serviceTree` 7.9 KB (37,300), `treeSignals` 5.4 KB (62,500), `treeStructure` 2.5 KB (189,400), `treeRollup` 3.4 KB (189,400), `healthTree` 6.6 KB (314,400); shared lookups (interner, identities, telemetry, statics) 0.43 GB.
- Typical objects: `String` 1.2 M objects and 150 MB in the store alone (ids and property texts as separate objects), `KeyValuePair<String,Value>[]` and `TextValue` (every property of every version is an object), `Edit` and `Version` per element version, `Contribution` and `LineageInfo` with a `List<LineageSource>` per element, one `Dictionary<String,Int64>` per support-count group (541,162 of them at 100,000).
- Telemetry: 755,548 windows at the end of the 100,000 run; bytes per window: the telemetry store 290 B, plus `SeriesOp` and `ThresholdOp` state of about 1 KB per series window and 0.5 KB per threshold key.
- Object counts (census): 63.8 M objects with the tree and readings (638 per master node), 11.0 M without (110 per node). The capture has 2.7 M objects for 50,266 master entities (54 per entity).

### Allocation and GC on the hot path
- Capture replay (`sattle bench phases --overlay lab/captures/space-overlay.sattle`, `out/phases-capture.txt`; 160,300 records, 704,027 rows, 4,029 ingests, 9.3 s): allocated 9.13 GB, 57 KB per record, 13 KB per row; GC pause 0.35 s (4 %). By phase: reading and parsing the JSON lines 3.7 s (40 %) and 4.5 GB (28 KB per record), the adapters (records to rows) 2.5 s (27 %) and 1.4 GB (8.5 KB per record), engine ingest 3.0 s (32 %) and 3.2 GB (20 KB per record), window advance 0.06 s. Attaching the canvas export changed nothing measurable (`out/phases-capture-export.txt`). Setup per host: catalog load 4.2 s (parse, check, lower, type 19 modules) and 0.35 s to build the plans of 14 graphs, every time a space is built.
- `replay latest --trace-alloc` (`out/capture-tree.txt`) allocates 14.9 GB in the same run because of the operator tracing and the report; use the phases figures for the cost of the path.
- Estate, one 60 s window at 100,000 nodes (208,000 flow rows plus 62,500 readings), window 1 (`out/estate-100k-tree.txt`): operators 817 ms and 518 MB allocated (1.9 KB per row); without tree graphs and readings (`out/estate-100k-notree.txt`) 351 ms and 152 MB (0.7 KB per row). Whole runs: 26.1 GB allocated, pause 16.4 s of 77 s (21 %) with the tree at 100,000; 9.9 GB, 2.1 s of 17 s (12.6 %) without; 1,000,000 with the tree 241 GB, 181.6 s of 675 s (27 %), gen2 collections 26 (every one walks tens of GB).
- Collections at 100,000 with the tree: gen0 101, gen1 34, gen2 21.

### Where the time goes (single thread, `--trace-alloc`; process CPU is wall time plus 5 to 35 % GC threads)
- Capture replay (phases above), operator kinds in ms of the 3.0 s of engine time: `MatchOp` 751 (145,267 calls), flush, apply and store commit 387, `EmitEntityOp` 335 (233,000 calls), `TraverseOp` 181, `WindowedGroupOp` 122, `EmitRelOp` 109, `FilterOp` 87, `MapOp` 80, `SeriesOp` 75, `UnionOp` 74, `JoinOp` 73, `RetainOp` 68. By graph: master operators 563 and commit 297; `healthTree` 360 + 40; `treeStructure` 186; every other graph under 150 each.
- Window 0 against the later windows (100,000, `out/estate-100k-tree.txt`): window 0 is the initial derivation of 1.4 M elements in 14 graphs: flush, apply and store commit 6.4 s (32 patches, every derived graph applies and stores its first patch of 100,000 to 300,000 edits), `MatchOp` 3.5 s (every `match` of every projection scans its input once), `EmitEntityOp` 0.6 s, `EmitRelOp` 0.6 s, `GroupOp` 0.6 s, `SeriesOp` 0.4 s. Windows 1 and 2: flush, apply and commit 0.36 s, `SeriesOp` 0.22 s, the rest under 0.07 s each. At 480,000 nodes (older runs, without this table) the first window was 9 s and the later ones 1.5 s on the same pattern; with the tree and readings 97 s and 10 s.
- Structure load at 100,000 (`out/estate-100k-tree.txt`, "structure load" and "resources load" tables): `MatchOp` 2.5 s, store commit 2.3 s, `TraverseOp` 0.6 s, `EmitRelOp` 0.5 s, `EmitEntityOp` 0.4 s; the resources load: store commit 3.5 s, `MatchOp` 2.4 s, `EmitEntityOp` 1.3 s, `TraverseOp` 1.0 s. A third of every load is committing patches into the in-memory store and applying them to the hot graph.
- Inherently parallel (by the structure of the plan, not measured): the 14 derived graphs read only the master and each other along `in` ports, so graphs that do not depend on one another (the five tree graphs, `serviceTree`, `deploymentHealth`, the health chain) can evaluate in parallel after each master commit; `Group`, `WindowedGroup`, `Series`, `Rollup`, `Threshold` and `Join` are keyed and partition by key; parsing and the adapters (67 % of the capture replay) are per record and parallel across files; the telemetry append is per graph. Not parallel: the master commit (one revision order), the identity catalog (one writer), the interner, window closes in epoch order.

### State required by the semantics, caches, and state kept for a feature (judgment from the code and the census; nothing was dropped to test it)
| state | MB at 100,000 with tree | class | reason |
|---|---|---|---|
| hot graph state (arrays) | 267 | required | the current result |
| sink contribution layers and support counts | 640 | required | a retraction needs the contributions an element is made of; without them an element cannot be removed or merged correctly |
| operator state: Join, Group, Series, Rollup, Threshold, Retain, Traverse reach | 2,160 | required in principle, rebuildable from the inputs | incremental maintenance; the signature strings of `TraverseOp` and the `Dictionary` per group are representation, not semantics |
| telemetry windows | 216 | required while inside the retention (`retain`, late horizon) | revisable windows; beyond the horizon they are history |
| identity catalog | 42 | required | ids must be stable across restarts (it is in the journal) |
| interner | 159 | a lookup | strings to codes; could be partial or per graph |
| store `Head` | 282 | duplicate | a second copy of the hot graph for reads through the store port |
| store `Versions`, `Adjacency` | 388 | feature (`AsOf`, history reads) | nothing in the results needs them |
| store `Commits` | 480 | feature (`Follow`, served history, restart verification) | patches can be regenerated from the journal; the host caps what it serves (`--retain-patches`) but the store keeps all |
| store identity bindings (copy) | 141 | duplicate of the identity catalog | the journal keeps them too |
| lineage records | 209 | feature | the backward and forward lineage routes; could be computed on demand by replaying the element's derivation |
| `relsAt` index | 129 | cache | exists to make an identity merge cheap (turn 14 fix) |
| canvas export, served history, event ring, drafts, preview inputs | 18 on the capture | feature | not measured at estate size (no host in `bench estate`) |
Required by the semantics (hot graph, layers, operator state, telemetry inside retention, identities): about 3.3 GB of the 5.2 GB; duplicates and features: about 1.9 GB (37 %), of which the store alone is 1.29 GB.

### Persistence on SQLite (`sattle bench b10 --hours 1 --seal-every 2000 --runs 3 --db out/p-1h.db`, `out/persist-1h.txt`; the newest capture repeated four times shifted in time to 1.23 h of logical time, load average 45 to 120)
- File 751 MB for 2,422,295 input rows, 5,435 journal entries, 3,093 revisions in 14 graphs, 2 seals. Pages by table (`sqlite3 out/p-1h.db "select name, sum(pgsize) from dbstat group by name"`): journal 580 MB (77 %), seal 56 MB, element_version 44 MB, commit 41 MB, identity_binding 11 MB (+7 MB index), adjacency 3 MB, other indexes 13 MB.
- Per journal entry 107 KB (a chunk of at most 2,000 rows); per input row 240 bytes; per revision (the `commit` table) 13 KB; one seal 28 MB in the file (the last one reported as 39.6 MB), taken in 3.2 s on average, independent of the history (the served patches and the event ring are capped).
- Per day of capture at this capture's rate (704,027 rows per 1,017 s, 60 M rows and 13.6 M records per day): journal 14.4 GB; element versions, commits, bindings and indexes 119 MB in 1.23 h, about 2.3 GB a day (they grow with the changes, not the rows); seals 28 MB each in the file, one per 2,000 journal entries: 53 a day (1.5 GB) by the entry rate of the later passes, 172 a day (4.8 GB) by the entry rate of the first pass (4,045 entries in 0.28 h against 463 per later pass: chunking differs), so it is a setting (`--seal-every`), not a property of the data. In all about 18 to 21 GB a day with nothing expired. The journal holds the raw input rows and is the largest and the only unbounded part.
- Restart (`bench b10`, same command, three runs): 11.2, 13.7 and 14.4 s at 1 h of history with a seal (state load 3.1 to 3.5 s, tail replay 4.9 to 7.0 s, 1,435 entries after the seal); the turn 9 runs were 7.5 s at 1 h and 8.3 s at 4 h with the same shape (about 4 s of either is the catalog load). With a seal the time does not grow with the history; without one it is linear (16.2 s at 4 h in turn 9). Not rerun at 4 h or 24 h in this turn.

### Raw size of the newest capture (`lab/captures/20261110T140542Z`, 1,017 s)
| source | files | bytes | records |
|---|---|---|---|
| hubble | 1 | 628 MB | 146,759 |
| eventhub (mirrors of other sources are skipped by replay; `sattle-activity` 1,637 and `sattle-aks` 3,016 records are consumed) | | 550 MB | 47,055 lines |
| vnet-flow-logs | | 28 MB | 1,148 |
| activity-log | | 24 MB | 5,000 |
| kubernetes | | 6.9 MB | 2,337 |
| prometheus | | 3.0 MB | 16 |
| arg | | 1.2 MB | 390 |
| azure-metrics | | 0.4 MB | 20 |
Rows after the adapters: 704,027 (hubble flows 146,758, flow logs 382,527, audit 121,772, prometheus metrics 38,980, activity 5,000, resource metrics 3,900 and smaller sources). A seal at this size: 39.6 MB compressed for 50,266 master entities and 8,305 relationships, about 790 bytes per master node for all 14 graphs, the served patches and the event ring.

### Test suite (`dotnet test --logger trx`, Debug build, tests run in parallel inside the process, load average 36 to 225 at the start and end of the default run, 21 at the end of the full one; `out/tests-default.trx`, `out/tests-full.trx`; durations are wall time per test under that contention, so the sums exceed the wall time)
| run | tests | wall time | summed test time |
|---|---|---|---|
| default (everything except Slow and Capture) | 534 | 13 min 16 s | 5,614 s |
| Slow (101 tests) | 101 | | 8,369 s |
| Capture (20 tests, real captures present) | 20 | | 3,012 s |
| full (`--settings tests/Sattle.Tests/full.runsettings`) | 655 | 31 min 56 s | 16,618 s |
Time per test class in the default run (seconds summed): LiftTests 797 (35 tests), LanguageTests 613 (29), RestartSmokeTests 550 (4), ServeReplayTests 360 (8), HealthTreeTests 355 (31), ReferencesTests 342 (6), RetentionTests 317 (7), HealthTests 283 (3), ProjectionEdgesTests 251 (33), EstateTests 225 (29), HostTests 215 (7), LineageTests 198 (7), EtagTests 183 (4), ProtocolFixTests 172 (17), ReplayTests 147 (14), DeployHealthTests 144 (6), BatchInvarianceTests 122 (13), DocumentTests 76, PaletteAggregateTests 63, EngineTests 55, RollupTemporalityTests 55, AlgebraTests 38 (85 tests), OperatorTests 22 (25), HotGraphTests 10 (10). Slow classes (seconds summed): SealTests 1,718 (9), LiveResumeTests 1,607 (7), LiveCrashTests 1,031 (13), RestartTests 893 (8), LensTests 836 (16), RefactorTests 759 (12), DraftTests 605 (11), RetentionOfHistoryTests 472 (6), LiveReproducibilityTests 283 (5), SnapshotTests 125 (5). Capture classes: ReplayTests 946 (4), RetentionTests 709 (6), BatchInvarianceTests 559 (6), ServeReplayTests 496 (1), LiftTests 188 (1), ReferencesTests 65 (1), HealthTests 50 (1).
Ten slowest tests in the default run: RestartSmokeTests (journal and seal restarts) 171, 155, 127 and 97 s; ServeReplayTests (late horizon restart; serving the lab fixtures) 150 and 146 s; HealthTests (conformance and determinism; roll-up) 114 and 105 s; LanguageTests (import pins) 111 s; EtagTests 98 s. In the full run: serving the newest real capture 496 s, `ReplayTests` chunk size 100000 on the real captures 431 s, `SealTests` on SQLite 380 s, `LiveResumeTests` 342, 337, 271 s.
The reason, from the measurements above: every test builds a `SpaceHost` or an `Engine` from the definitions, and that costs 4.2 s of catalog load (parse, check, lower and type 19 modules) and 0.35 s of plan building in Release, several times that in Debug; most tests then drive a small world. The Slow and Capture tests replay the real captures (9 s each in Release, more in Debug), several times (chunk sizes, restarts, seals, resumes). No test time comes from the Cosmos emulator (`SATTLE_COSMOS_EMULATOR` was not set).

### Validity of the baseline (code review 06 and 07, checked by running)
- Phantom relationships (`experiments/V1-phantom`, `out/V1-phantom-*.txt`): `bench estate` builds 62,500 relationships at 100,000 nodes that point to entities that do not exist (31,250 `RunsOn` to 1 absent target, 31,250 `ControlledBy` to 5 absent targets) and sets `ready` = false on every pod, because the row builder marks unset optional pod columns present with code 0 (`Operators.cs:58`, `Columnar.cs:59`, `Synthetic.cs:22`). With those columns marked absent: master relationships 199,021 to 136,521; live bytes with the tree graphs and readings 5,188 to 5,086 MB (-2 %), after the structure 3,096 to 3,002 MB; without the tree graphs 582 to 483 MB after the structure (-17 %) and 1,409 to 1,286 MB at the end (-9 %); window closes unchanged. So the ladder rows with the tree are about 2 to 3 % too high and the rows without the tree graphs 10 to 17 %; the master-side per-structure rows of `estate-100k-notree` carry the phantom share (46 % of its master relationships).
- `bench b10 --hours` above the capture span (`experiments/V2-b10-passes`, `out/V2-b10-passes.txt`): only `CapturedNs` is shifted, event times are not; from pass 2 on 349,961 rows per pass (flows, flow logs, metrics, resource metrics) are dropped as late (late horizon 3), resources, pods and endpoints repeat as updates of the same ids, and the master telemetry holds the same 16 window starts after pass 4 as after pass 1. The "1 h" and "4 h" restart times and the per-hour file sizes of H10 and of the persistence section measure a journal of repeated rows, not hours of history; the 240 bytes per input row stays valid per row.
- Look-back (`out/estate-100k-lookback30m-40w.txt`: 100,000 nodes, 40 windows, every `24h` of the definitions replaced by `30m`): the window close grows from about 1.2 s (windows 1 to 10) to about 2.4 s once the look-back is full (windows 31 to 39) and stays there; live bytes 3.1 GB after the structure, 5.2 GB at 3 windows, 8.2 GB at 40 (about 80 MB per window, 10.1 M telemetry windows). The 3-window figures above therefore understate the steady state about two-fold in close time and, extrapolated linearly, a 24 h look-back at 100,000 nodes would need about 115 GB for the windows alone (not runnable on 64 GB; extrapolation, not measured).

### Validity re-measured on the fixed engine (Release built 2026-11-11 20:12 from 9a6533b plus the bench changes; commands in the file names under `out/`)
Changes of the engine since the baseline that bear on these numbers (from engine-core): every first write of a telemetry window reads the store once (support counting), Threshold keeps one carried state per key instead of replaying the look-back, Retain holds a weighted multiset; the row builder marks unwritten columns absent and `bench estate` marks the flow columns it does not write absent.
- V1, estate without phantoms (`out/estate-100k-tree-v1.txt`, 100,000 nodes, tree graphs and readings, 3 windows, `--census`): dangling relationships of the master 0 after the structure and at the end of 136,521 (old tree 199,021 relationships, 62,500 phantom); live bytes after the structure 2,977 MB (old 3,096), at the end **5,097 MB** (old 5,188, -1.8 %); census coverage 100 %, 62.6 M objects (old 63.8 M). Per structure the differences are in the master-side rows: `JoinOp` 674 to 640, store `Commits` 480 to 467, `Versions` 226 to 204, `Adjacency` 162 to 151, lineage 209 to 194, interner 159 to 152; `GroupOp`, `TraverseOp`, `SeriesOp`, telemetry, supports unchanged. Window closes 9.4, 0.9, 1.3 s (old 11.4 to 12.8, 0.8 to 0.9, 1.5 to 1.9 s). The ladder in the baseline section stays valid within 2 to 3 % with the tree and readings; the rows without the tree graphs are about 10 to 17 % too high.
- V2, steady state (`out/estate-100k-lookback30m-45w-v2.txt`, `--look-back 30m --windows 45 --census`; 30 windows fill the look-back, 15 run full; the definitions load without error): **window close p50 2,037 ms, max 2,283 ms** with the look-back full (windows 30 to 44; 0.9 to 1.4 s in windows 1 to 2, 1.9 s in window 29, window 0 9.8 s); live bytes 7,463 MB when the look-back filled, 8,040 MB at the end: **+7.7 % in 15 windows, about 38 MB (0.5 %) per window**, so not flat within 5 %; census at the end 8,040 MB, coverage 100 %, 94.1 M objects. Close time stops growing after the fill (it doubled from 1.2 s to 2.0 s while filling); memory keeps growing at about 38 MB per window (the baseline run with phantoms and a 40-window run: 80 MB per window while filling, 8.2 GB at the end).
- V3, restart with real history (`out/persist-v3-*.txt`; `bench b10 --hours H --seal-every 2000 --runs 3`, later passes now replay only the telemetry sources with event times shifted; SQLite files `out/p3-1h.db` 760 MB, `out/p3-4h.db` 2,033 MB): restart with a seal **8.0 s at 1 h (7.9 to 8.3), 9.7 s at 4 h (9.7 to 10.0)**; without a seal **7.5 s at 1 h (7.5 to 11.3), 15.8 s at 4 h (15.7 to 19.2)**, load average 11 to 34; state load 2.7 to 3.0 s, tail replay 2.8 s (871 entries) and 4.4 s (1,346 entries). Journal entries 4,871 (4 passes, 1.23 h) and 7,346 (13 passes, 4.08 h): later passes add 275 entries instead of 4,045. File growth from 1 h to 4 h about 450 MB per logical hour (10.7 GB a day), against about 610 MB per logical hour in the old repeated-rows run. The two checks first set for this run (late rows constant and the master constant) were mis-specified, see below: late rows dropped still grow by about 227,900 per pass (230,888 in pass 1, 914,570 after 4 passes, 2,965,616 after 13), and the master is not constant: 48,104 entities and 5,731 relationships after pass 1, 48,025 and 5,652 after pass 4, 47,761 and 5,388 after pass 13 (about 26 entities fewer per pass). Both are explained, not defects of the run (engine-core, confirmed by a rerun with evidence counts, `out/persist-v3b-1h.txt`): pass 1 itself drops about 230,000 rows (history older than the late horizon of 3 windows) and every later pass drops the same rows again because their times move with the pass; the master shrinks because `retain on time for 24h` withdraws Activity Log events as the passes move the frontier: entities 48,104, 48,092, 48,055, 48,025 per pass (-12, -37, -30) and ActivityEvent 5,181, 5,169, 5,132, 5,102 (-12, -37, -30); AuditEvent (42,244) and WarningEvent (173) constant, relationships fall by the same 79 (their `Concerns` edges). So the V3 figures stand as measured.

### V2 again on the committed head (5c1450c; telemetry retained by the logical frontier, `--look-back D` sets the telemetry retention to 2 D; `out/estate-100k-lookback30m-75w-v4.txt`)
`--look-back 30m --windows 75 --census`, 100,000 nodes, tree graphs and readings (60 windows fill the retention, 15 steady), load average about 130 during the run (times pessimistic): steady state (windows 60 to 74): **window close p50 2,380 ms, max 3,848 ms; live bytes 6,838 MB when the retention filled, 6,841 MB at the end (+0.04 %, flat)**; before the fix the same measurement grew 7,463 to 8,040 MB (+7.7 % in 15 windows, 38 MB per window). The two censuses (window 59 and the end) differ by 3 MB. Against the old run's census at the end (`estate-100k-lookback30m-45w-v2.txt`): telemetry 1,726 to 684 MB, `RollupOp` 325 to 163 MB, everything else equal within 3 MB (`SeriesOp` 1,249 MB, `JoinOp` 640, `GroupOp` 514, store `Commits` 475, contributions 395, `TraverseOp` 359). `GC.GetTotalMemory` at the end 7,351 MB (old run 14,082 MB), so the garbage in the heap is gone as well. Allocation is higher: 341.7 GB over the 75 windows (about 4.6 GB per window against 2.2 GB per window in the old 45-window run), 2,417 gen0 collections, GC pause 7 % of 327 s. The live bytes at window 30 were not printed by this run.

## S0 experiments
Measurements for the efficiency concept (`paper/research/04-compute-and-memory-efficiency.md`, Appendix A); programs in `engine/experiments/<ID>-<name>/` (outside `Sattle.slnx`), raw results in `engine/out/experiments/<ID>.txt` (gitignored). Nothing in `src/` changes behaviour. Machine and conditions as in the baseline section (shared, load average 15 to 225).

### X3: what the event plane saves (PASS on the newest capture)
Evidence rules (`observedActivityEvents`, `observedWarningEvents`, `observedAudit`) with `|> where false` before their `retain` (accepted by the checker), everything else unchanged. Commands in `out/experiments/X3.txt`: `sattle check --defs out/experiments/X3-defs --update-pins`, then `sattle replay CAPTURE [--defs out/experiments/X3-defs] --census`.
| | newest capture base | events off | older capture base | events off |
|---|---|---|---|---|
| master entities | 50,266 | 603 (-98.8 %) | 15,895 | 532 (-96.7 %) |
| live bytes | 248 MB | 95 MB (-61.7 %) | 140 MB | 82 MB (-41.4 %) |
| identities minted | 53,380 | 2,148 | 18,344 | 1,960 |
- Criterion (at least 90 % fewer master entities and 50 % less heap on the newest capture): met. The older capture has fewer events and misses 50 % on heap (the fixed part is a larger share there).
- What shrank (newest, MB, base to off): store `Commits` 29 to 2, `Head` 16 to 1, versions 12 to 1, bindings 18 to 1, hot graph 15 to 1, contribution layers 15 to 2, lineage 8 to 1, `RetainOp` 23 to 0. What did not: the interner (31 to 21 MB: the adapters intern every string of every row, also rows the definitions drop, so it scales with the raw input, not with the graph), the canvas export (18 to 14), `MatchOp` 16, `WindowedGroupOp` 8, `TraverseOp`, `JoinOp`, `SeriesOp`: independent of events. Stage S3 needs a lifetime for interned strings per source or plane, or the adapters must not intern what no rule keeps.

### X11: flow pre-aggregation (PASS)
Program `experiments/X11-flow-aggregator` (reads `hubble/flows.ndjson` of the newest capture, aggregates as `observedCalls` does, compares with `replay latest --out`). Command and full output: `out/experiments/X11.txt`.
- Configuration 1 (the engine rule: all four names present, 60 s windows on flow time, key pod pair, `flows` and `drops`) equals the engine's `Calls` telemetry: 466 of 466 pair-windows of the 137 relationships whose pods resolve, and the per-window totals of all 245 `Calls` relationships in 8 of 8 windows (117,931 flows, 322 drops). 662 pair-windows, 107 pairs per window median (133 max).
- Aggregation without parsing: 17 ns per flow for configuration 1, 11 ns for configuration 2 (median of 5, struct array, `Dictionary` of tuples; limit 50). Parsing the JSON lines: 2.8 s for 146,759 records (19 microseconds per record, `System.Text.Json`).
- Configuration 2 (requests only, workload pair, destination port): 284 keys for 117,931 flows with names, 415 times fewer (limit 100); 82 workload pairs, per window median 50 (max 52). The concept's figure of 220 workload pairs is not reproduced with the known-name filter of `observedCalls`; 60,924 request flows have all four names (the concept counts 82,893 without that filter).
- Finding for the definitions: `observedCalls` does not drop replies. 57,007 of the 117,931 flows are replies, and 118 of the 245 pod pairs exist only as replies (an edge from the callee to the caller). Whether the rule should drop them is a question for the definitions and the user, not changed here.

### P1: local journal with group commit (FAIL on latency, PASS on recovery, macOS)
Program `experiments/P1-journal` (one paced producer, one writer thread, frames of u32 length + u32 CRC32 + u64 sequence + 100-byte payload, group deadline T after the first record of the group, flush, then acknowledge). Grid and crash runs, commands and every cell: `out/experiments/P1.txt`. Apple M1 Max internal SSD, macOS 27.2; the machine was shared (load average 20 to 165 during the cells), so latencies are pessimistic; a Linux VM with local NVMe was not available and was not run.
| T | 1,000/s | 10,000/s | 30,000/s | 100,000/s |
|---|---|---|---|---|
| 1 ms, p50 / p99 | 8.8 / 81.6 ms | 7.8 / 27.9 | 7.9 / 27.4 | 7.8 / 16.5 |
| 2 ms | 7.5 / 15.0 | 7.7 / 15.9 | 7.9 / 30.9 | 8.0 / 20.7 |
| 5 ms | 7.7 / 14.6 | 7.6 / 18.9 | 7.8 / 16.4 | 8.1 / 26.8 |
| 10 ms | 10.1 / 16.9 | 10.2 / 18.9 | 10.4 / 19.5 | 10.5 / 17.9 |
- Criterion (p99 acceptance latency at most 10 ms at 30,000 records/s with T at most 5 ms): not met in any cell; the smallest p99 is 14.6 ms. The cause is the flush, not the grouping: a 4 KB append plus flush alone costs p50 5.1 to 6.2 ms and p99 10.7 to 12.8 ms here (`Flush(true)` and `fcntl(F_FULLFSYNC)` cost the same, both about 5 ms; the sustained rate is 185 flushes per second), and the group deadline T adds to it. Section 7.7 needs revising for this machine, not the decision: acceptance on a laptop-class SSD is about T + 5 ms at p50 and 15 ms and more at p99.
- Throughput is not the limit: 100,000 records/s acknowledged in every cell with 95 to 190 flushes per second (at 100,000 records/s a group at T = 5 ms is about 500 records).
- Crash: 20 of 20 `SIGKILL` runs (30,000 records/s, T = 5 ms, killed after 2 to 8 s) recovered with every acknowledged record present, none duplicated, no gaps; a process kill leaves no torn tail (the page cache survives it), so power-loss torn writes are not exercised by this test.

### X4: fixed-point and compressed telemetry (PASS, with a caveat on series length)
Program `experiments/X4-telemetry-blocks` (in-process replay of the newest capture and an estate workload of 100,000 master nodes with 3 windows, every closed window read from `InMemoryTelemetryStore.Dump`; 1,140,251 values in 379,979 series). Command, per-property tables and result: `out/experiments/X4.txt`.
- Exactness: every input aggregate (`flows`, `drops`, `changes`, `warnings`, `newFlows`, `deniedFlows`, `flowsOut`) is an exact integer (scale 0) in every graph on both sources; no value fits no scale. Derived values are not: means, rates and ratios are repeating decimals of 28 digits (`dropRate`, `reading` on the capture: 264 of 4,629 values; `dropRatio` 7,401 of 74,048 and `flowRate` 49,576 of 74,048 on the estate service tree). The rule that follows: store sum and count, derive means, rates and ratios at read time.
- Size per window (input aggregates, weighted): zigzag-varint deltas of the fixed-point values 1.70 B on the capture, 1.01 B on the estate (limit 2 B per value); Gorilla XOR 4.5 and 4.9 B; `decimal` 16 B. Delta varint is the better encoding here, XOR is worse because the series are short and the first value costs 64 bits.
- Append: 1.6 ns per value for delta varint, 10 ns for Gorilla (limit 30 ns), single thread.
- Caveat: series are 3.0 to 3.4 windows long on average (the capture spans 17 minutes, the estate run 3 windows), and the estate counts are synthetic; no capture has day-long series, so the per-window cost of long series is not measured.

### X10: journal of adapted rows (digest equal, size PASS, speed 2.96x against the 3x criterion)
Program `experiments/X10-row-journal` (records the batches and controls a replay of the newest capture feeds the engine, writes dictionary-coded segments, decodes them into a fresh engine). Format, command and full output: `out/experiments/X10.txt`.
- The state digest from the segments equals the digest of the envelope replay (4,029 batches, 704,027 rows, 16 advances). Batches carry weights (the Kubernetes adapter emits retractions); the format has a weights flag.
- Size: 31.8 B per row uncompressed, **11.2 B per row with Brotli** (limit 40; ZLib 11.6, Brotli fastest 12.2): 7.9 MB for a capture whose consumed envelopes are at least 708 MB, about 90 times smaller. Rows with free text cost more (audit 21, activity 35, events 39, resources 74 B proportional); flows, flow logs and metrics 5 to 9 B.
- Time: 2.03 s from segments against 6.00 s from envelopes (median of 5, load average 53): 2.96 times, 1 % under the criterion; the remaining 2 s is mostly the engine's own ingest and window work, which a journal does not remove (the baseline phase table puts the engine at about a third of the envelope replay).

### X2: lineage recomputed instead of stored (PASS for supports and group members; the procedure was changed, see below)
Program `experiments/X2-lineage`; commands, census rows and full outputs: `out/experiments/X2.txt`.
- Cost of storing (census, bytes): newest capture lineage records 8.3 MB, supports 1.8 MB, group refs 0.1 MB (together 4 % of 248 MB); older capture 3.9, 1.4, 0.1 MB; estate 100,000 without tree graphs 43 + 28 + 6 MB (7.5 % of 1,038 MB), with tree graphs and readings `Lineage` 209 MB, `Supports` 197 MB, `GroupMembers` 39 MB, `GroupRefs` 19 MB (9 % of 5,188 MB).
- Deviation from Appendix A.1: a per-element evaluation of a sink's input relation on the final state needs a change in `src/` (the plan only evaluates incrementally). Instead the capture is evaluated from scratch with a different history: engine A is the reference replay (4,029 batches), engine B gets the same recorded rows with all batches of one source between two advances merged into one (48 batches, sources in fixed order, almost no retractions), both on one interner so handles compare. The stored lineage of A and B is compared for every derived element. No per-element latency is measured.
- Result: `Supports` (the complete lineage of an element) equal for 5,168 of 5,168 elements (newest capture) and 3,868 of 3,868 (older); the sample of 1,000 (at least 50 or all per graph with lineage, then uniform, seed 20261111): 1,000 of 1,000 on both; `GroupMembers` equal; state digests of A and B equal. So lineage is a function of the input rows and the definitions and does not depend on the history of batches and retractions; stage S2 may drop the stored copy once an on-demand walk reproduces it (its latency is the S2 criterion).
- Finding: the one-row `Lineage` records and `GroupRefs` are not removed when an element is retracted (52 of 68 differing records on the newest capture, 8 of 29 on the older, belong to elements no longer in the graph; 6 and 5 stale `GroupRefs`): history-dependent leftovers, harmless for results, a leak in memory.
- Finding: feeding the recorded rows in 48 merged batches took 1.3 s against about 3 s of engine time in the 4,029-batch replay (identical digest): per-batch overhead may be about half of the engine time; not isolated.

## S1 step (a): the head-only default store (2026-11-12)
Steady-state run at 100,000 master nodes: `bench estate --look-back 30m --windows 75 --census --pods 31250 --services 6250 --resources 62500 --pairs 50000 --per-window 208000`, tree graphs and readings, the census at the end. Before: the committed engine 6a69582 (Release build, full-history in-memory store); after: the working tree with `HeadOnlyGraphStore` as the default (an optimized build of its own). Raw outputs `out/s1/before-6a69582.txt`, `out/s1/after-headonly-tail.txt` (gitignored). Load average 30 to 50, so the times are noisy; bytes do not depend on load.

| census row (MB) | before | after |
|---|---|---|
| `store.graph.Commits` (every patch) | 475.3 | (gone) |
| `store.graph.Head` (a second copy of every graph) | 277.3 | (gone) |
| `store.graph.Versions` (every element version) | 205.0 | (gone) |
| `store.graph.Bindings` (unused copy of the identity bindings) | 168.4 | (gone) |
| `store.graph.Adjacency` (adjacency intervals) | 153.2 | (gone) |
| `store.graph.Tail` (the last commits, at most 4,096 edits per graph) | | 7.7 |
| `graph.sink.elems` | 394.8 | 494.1 (objects the commit log shared with the sink's contributions are now counted here) |
| live bytes at the end (forced compacting collection) | **9,760** | **8,588 (-1,172, -12.0 %)** |
| live bytes when the retention filled (window 59) | 9,698 | 8,528 |

- The design's target was minus 1.29 GB, the five store rows. The measured drop is 1,172 MB. The difference is the 99 MB of the commit log that the sink's contributions also hold, which stay alive.
- The census attributed the engine's own graphs (about 245 MB) to the store in this run, because it walked the store first; it now walks the graph state first.
- Window close p50 5,402 ms before, 4,538 ms after (load-dependent).
- State digests of the estate, `lab/fixtures` and both captures at chunk sizes 1, 50 and 100,000: all 12 unchanged.

## Core steps 1 and 2: the table structures, and the sink table in place of the contribution objects (2026-11-13)
Three numbers per step:
- the census at 100,000 nodes (the run of "S1 step (a)", live bytes after a forced compacting collection, per master entity of 162,501);
- `sattle replay` of the newest capture at chunk 1 (wall time, and bytes allocated by the operators with `--trace-alloc`), the committed build and this one alternating, two runs each;
- lines of runtime code added and removed.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| Before (committed engine, c9c43ab to 2cd0c9a) | 8,588 MB (52,849 B) | 30.7 s and 30.3 s, 38.5 GB | |
| Step 1: segments, code dictionary, table, arrangement (not used by the engine yet) | unchanged | unchanged | +385, -0 |
| Step 2: the sink table | 8,173 MB (50,295 B), -415 MB | 31.4 s and 32.2 s, 38.6 GB | +108, -46 |

- The chunk-1 runs alternated at a load average of 35 to 60 (the machine was shared): step 2 is about 1.3 s slower (4 %), operator time 12.1 s before and 12.5 s after. A row is found by walking the fragments of its element, and a retracted relationship fragment checks whether the element still names the endpoint.
- Step 2's census rows. The contribution objects (`elems` 494 MB, `relsAt` 123 MB, `touchedSet` 17 MB, `touched` 3.5 MB, together 637 MB) became 220 MB:
  - `fragments` 106 MB: each fragment once, as bytes, with its text in UTF-8;
  - `contributions` 61 MB: the table of element, fragment and weight, found through its arrangement by element (no index of its own);
  - `ends` 34 MB: relationships by endpoint, for merges, a row only while a fragment names the endpoint;
  - `kinds` and `dirty` 8.5 MB each, `touched` 3.5 MB.
- Against the design's budget for the sink table (about 16 B per contribution, 27 MB for 1.66 million): 61 MB is about 37 B per contribution (weight 8 B, element and fragment 8 B, the arrangement's next and previous rows 8 B, its index, and the last segment of each table only partly used). The fragments are the properties of every element a second time beside the graph's own copy; the graph layout step (5) removes that copy, and the dictionaries (step 6) the text.
- Allocation at chunk 1 does not change: the emit operators still build a property map per row (3.1 GB of the 38.6 GB), and the fragment is encoded from it. Writing the fragment straight from the columns belongs to step 3.
- The 12 state digests are unchanged. `Sattle.Differential` every category 312 of 312; the seal, restart, live-resume, process-kill and structure tests 63 of 63; the default run 265 of 265 and 680 passed, 2 skipped. All in the tree.

## Core step 3, grouping: the state of Group, windowed Group and Rollup in tables (2026-11-13)
The three operators share one container (`KeyTable`), so the step is one change in two parts. Same three numbers as above; "before" is core step 2.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| Before (core step 2) | 8,173 MB (50,295 B) | | |
| 3a: per key one table row with its count, sums and means; no copy of the values emitted last | 7,544 MB (46,423 B), -629 MB | 26.4 s and 34.2 s against 26.1 s and 29.0 s (load 35, noisy) | +127, -191 |
| 3b: the multisets of min, max, last and countDistinct as tables | 6,978 MB (42,941 B), -566 MB | 24.4, 24.5 and 24.9 s against 24.8, 24.2 and 24.7 s for the build before the core steps (load 20 to 35); 38.6 GB against 38.5 GB | +104, -93 |
| Step 3 grouping in all | **-1,195 MB** | equal within the noise | +184, -237 |

- Census rows: Rollup 1,188 MB, then 707 MB (3a), then 352 MB (3b). Group 613, 493, 283 MB. Windowed Group 63, 34, 34 MB. Together 1,864 MB before and 669 MB after.
- 3a: a key's aggregate is retracted when a step first touches the key, before the step's rows change it, so the values emitted last are not stored. A key with no rows left gives its row back at once (before, a compaction every 1,024 emptied keys).
- 3b: a multiset is rows of (key's row, aggregate, value) with a weight, found through the arrangement by (key's row, aggregate); the extreme is found by walking those rows. A decimal is keyed by its number whatever its scale (2.5 and 2.50 are one entry, which keeps the digits that came first, as before).
- Operator time at chunk 1 is 9.3 to 9.6 s against 9.1 to 9.2 s. Allocation does not change: the windowed Group still allocates 11.7 GB at chunk 1, because every call builds a new output batch of 64 rows per column whether or not it writes a row. That is the next thing to remove, in the operators, not in their state.
- Proofs: the 12 state digests unchanged; `Sattle.Differential` every category 312 of 312; the seal, restart, live-resume, process-kill, operator and grouping tests 164 of 164; the default run 265 of 265 and 680 passed, 2 skipped. All in the tree, on the state of 3b (3a had the same proofs on its own state).

## Results table, step 1: every table has one index on its whole key (2026-11-13)
Code review 2 measured what the table's second way to find a row cost: a row was found by walking the rows that share its first key column, so 8,000 relationships at one node took 4.95 s against 0.03 s at distinct nodes. Now a row is always found through one index on its whole key, and an arrangement by some key columns serves only the callers that want every row of one value. The hash of the index mixes all bits of a key (60,000 keys that are multiples of 65,536: 32.5 s before, as fast as sequential keys now).

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| Before (core step 3 and the host rewrite) | 6,978 MB (42,941 B) | 24.6, 23.6 and 23.7 s for the build before the core steps | |
| One index per table | 7,064 MB (43,470 B), +86 MB | 23.4, 25.6 and 23.5 s (load 45 to 55); 39.3 GB against 39.1 GB | +51, −56 |

- The price, as expected: about 8 bytes per row for the index a table did not have. The sink's contributions 61 to 84 MB, its endpoint table 34 to 36 MB, Rollup 352 to 369 MB, Group 283 to 301 MB.
- The state digests equal those of the build before this step at every chunk size. The digests of the two recordings changed earlier, with the definitions change of 79a3cce (a present but empty text counts as missing, as in KQL); the estate and `lab/fixtures` kept theirs.
- Proofs: `Sattle.Differential` every category 312 of 312; the seal, restart, snapshot, operator, grouping, lineage and structure groups 238 of 238; the default run 265 of 265 and 701 passed, 2 skipped. Seal format 4.

## Results table, step 2: what a sink keeps about an element is compared as a value and freed when no row uses it (2026-11-13)
Code review 2 found two defects in the values a sink keeps (what a rule says about an element: its type, endpoints and properties). They were compared byte by byte with a number's scale among the bytes, so 2.5 retracted as 2.50 cancelled nothing and the element stayed (defect 20). And a value was never freed: every value a property ever had stayed in memory. Now a value is written in one form (a number without its scale, every text and name as the engine's number for it), each table row counts as one use of its value, and a value no row uses is gone; when more bytes are free than in use, the rest are copied together. A sink whose elements all went holds nothing.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| One index per table (step 1) | 7,064 MB (43,470 B) | 23.4, 25.6 and 23.5 s; 39.3 GB | |
| Values compared as values, counted | 7,025 MB (43,230 B), −39 MB | 24.9, 24.4 and 24.1 s (load 18 to 20; the build before the core steps in the same minutes 23.6 s); 39.2 GB | +153, −57 |

- The sink's values 106 to 67 MB: a text is now 1 to 4 bytes where it was its UTF-8 bytes, and the second table of property names is gone. The contributions (84 MB) and the endpoint table (36 MB) are unchanged.
- What the census cannot show, since it measures one steady state: on a long run where properties keep changing, the old store grew without bound. `A_sink_holds_a_value_only_while_a_row_uses_it`: one element whose property takes 20,000 values holds one value at the end; 20,000 elements that came and went hold none.
- The state digests equal those of step 1 at every chunk size (estate, `lab/fixtures`, both recordings; 12 of 12).
- Proofs: `A_sink_cancels_a_contribution_with_its_retraction_written_at_another_decimal_scale` (5 cases) and `Rows_that_differ_only_in_decimal_scale_leave_nothing_when_they_are_retracted` (plain, distinct and join, in one chunk and row by row; all 6 fail on step 1); `Values_are_kept_once_while_they_are_held_and_gone_when_released` against a model. `Sattle.Differential` every category 312 of 312; the default run 715 passed, 2 skipped; the KQL tests 138 of 138. Seal format 4 (a seal of another build is refused by its build id).

## Results table, step 3: the results of a projection in three modules (2026-11-13)
What a projection contributes to its graph was one class with 23 public members and four public dictionaries that the lineage reader and the census read. It is now three modules that each own their state: which elements exist (`ElementLayer`), where they come from (`LineageLayer`, asked through `Of(element)`), and which rows support each telemetry window (`WindowSupport`). `OutputBuilder` stays as the one thing an operator holds, with 13 members that pass on. The code moved as it was; the state kept is the same.

One behaviour changes (code review 2, defect 22, the part in the results): on a merge of two identities the lineage of the id that lost moves to the survivor, as its rows do. Before, an entity that a link forwarded to another id had no lineage: 1 pod in 5 on the estate.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| Values compared as values, counted (step 2) | 7,025 MB (43,230 B) | 24.9, 24.4 and 24.1 s (the build before the core steps in the same minutes 23.6 s); 39.2 GB | |
| Three modules | 7,025 MB (43,230 B), every census row as before | 36.8, 35.3 and 38.2 s on a busier machine (the build before the core steps in the same minutes 36.2 s); 39.2 GB | +626, −532 |

- The census names the rows by module now (`graph.sink.elements.contributions`, `graph.sink.lineage.supports`, `graph.sink.windows.soleOwn`).
- The state digests equal those of step 2 at every chunk size (estate, `lab/fixtures`, both recordings; 12 of 12).
- Proofs: `An_entity_that_a_link_forwards_to_another_id_keeps_its_lineage` (2 cases) and `Every_pod_of_the_master_has_a_lineage_also_one_that_a_link_forwarded_to_another_id`, all three failing before. `Sattle.Differential` every category 314 of 314; the default run 716 passed, 2 skipped; the KQL tests 138 of 138. Seal format 4.
- Left as it is, for a later step: the lineage is kept as text rows in dictionaries (supports 196 MB, last-row records 194 MB, group members 39 MB, group references 19 MB at 100,000 nodes), and the last-row record and the group reference of an element are not removed when the element goes (found in X2). Behind `Of(element)` both can change without touching a caller.

## Results table, step 4: `min` and `max` no longer walk the values of a group on every read (2026-11-14)
A group keeps the values of each `min` and `max` in a table, one entry per distinct value. Every read of the aggregate walked all entries of the group: twice per step that touched the group, more with the check for "no value". Now a group with one value keeps nothing more, since that value is the extreme. A group with more values has the extreme in its own row; a new value that beats it takes its place, and the values are walked only when the extreme itself is retracted.

A first variant kept the extreme in the row of every group. It cost 33 MB at 100,000 nodes (7,058 MB), because the column was never allocated before, and bought nothing there: the roll-ups of the estate have one value per window. The variant above writes the column only for groups with more than one value.

A defect found on the way: a `min` or `max` and a `countDistinct` of one group filed their values under the same number, so the two mixed. `max` beside a `countDistinct` could give a value that was never in its column. Each aggregate now files its values under its own accumulator.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| Three modules (step 3) | 7,025 MB (43,230 B) | 36.8, 35.3 and 38.2 s on a busy machine (the build before the core steps in the same minutes 36.2 s); 39.2 GB | |
| `min` and `max` without the walk | 7,025 MB (43,230 B), Rollup 369 MB and Group 301 MB as before | 24.7, 24.8 and 25.5 s (the build before the core steps in the same minutes 25.4 s); 39.2 GB | +69, −35 |

- Where it shows: 300 steps of one row each at a group of 1,000,000 values took 0.19 and 0.22 s before and 0.05 and 0.07 s now, the same as at a group of 100 values (measured with a test kept out of the suite, since a time ratio of 4 is too small to assert). The recordings and the estate have few values per group, so their times do not move.
- The state digests equal those of step 3 at every chunk size (estate, `lab/fixtures`, both recordings; 12 of 12).
- Proofs: `Min_and_max_equal_the_extremes_of_the_rows_a_group_has_after_every_step` (5 cases against a model, failing on two deliberately broken variants of the new code) and `An_extreme_and_a_count_of_distinct_values_in_one_group_do_not_mix_their_values` (3 cases, the `max` case failing before). `Sattle.Differential` every category 314 of 314; the default run 724 passed, 2 skipped; the KQL tests 142 of 142. Seal format 4.
- Not covered by a test: when a group falls back to one value, its row's extreme is cleared so that an empty group gives its row back. Leaving the clearing out changes no output, only memory.

## Results table, step 5: the `last` aggregates of one row (2026-11-14)
An addition, for `arg_max` in KQL: several `last` aggregates of one group can be marked as one row (`last(value, order) row name`). They then take their values from the same input row, the one with the greatest order; a tie goes to the greatest values of the kept columns in the order of the aggregates' names. Without the mark each `last` picks its own winner, so two columns of one output row could come from two input rows. A group keeps one entry per distinct input row of a mark: the order and every kept column. A projection without the mark keeps nothing more and means what it meant.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| `min` and `max` without the walk (step 4) | 7,025 MB (43,230 B) | 24.7, 24.8 and 25.5 s (the build before the core steps in the same minutes 25.4 s); 39.2 GB | |
| The `last` aggregates of one row | 7,025 MB (43,230 B), Rollup 369 MB and Group 301 MB as before | 24.2, 24.9 and 24.2 s (the build before the core steps in the same minutes 24.7 s); 39.2 GB | +93, −6 (language and kernel +25, −8) |

- No definition of the estate or the recordings uses the mark yet, so these numbers say only that the addition costs nothing where it is not used.
- Cost where it is used: a marked group's rows are walked once per step and group to find the winner (the answer is kept for all aggregates of the mark within the step). A group with many rows and frequent changes pays for the walk; keeping the winner in the group's row, as step 4 does for `min` and `max`, is the remedy if a measurement asks for it.
- The state digests equal those of step 4 at every chunk size (estate, `lab/fixtures`, both recordings; 12 of 12).
- Proofs: `The_last_aggregates_of_one_row_take_their_values_from_one_input_row` (marked and unmarked, a tie, a winner without a value, a retraction of the winner), `The_row_mark_of_a_last_is_printed_and_checked` (3 cases), and the differential world "last of one row" against the reference evaluator. `Sattle.Differential` every category 324 of 324; the default run 734 passed, 2 skipped; the KQL tests 166 of 166. The kernel digest changes (a new optional property of `Aggregate`); seal format 4 stays.

## Results table, step 6: lineage through `select` and `where` (2026-11-14)
An element says which elements it is made of. That was lost whenever a `select` stood in the way: between the match and a group (the group had no members), between a group and the emit (the element did not know its group), or between the match and the emit. A projection written in KQL always has such a `select`, so the count blocks of its reports named nothing. Now the plan compiler works out, per step, which columns say where a row comes from: the elements it was matched from and the group it is a result of. A `where` keeps them. A `select` hands on those it names and carries the others behind its own columns, as references to the same column data. A group and an emit read them; a join, a union, a distinct or a window ends what was carried. One calculation over the plan replaces the two that looked only at a sink's direct input.

| | Live bytes at the end (per master entity) | Chunk 1: wall, operators allocated | Runtime lines |
|---|---|---|---|
| The `last` aggregates of one row (step 5) | 7,025 MB (43,230 B) | 24.2, 24.9 and 24.2 s (the build before the core steps in the same minutes 24.7 s); 39.2 GB | |
| Lineage through `select` and `where` | 7,038 MB (43,310 B): group members 51.8 MB (38.8 MB before), every other row as before | 27.9, 26.8 and 28.0 s on a busier machine (the build before the core steps 25.6 s after them); then in turn without the allocation trace 22.8, 20.7 and 19.7 s against 21.1, 22.1 and 19.1 s; 39.2 GB | +46, −25 |

- The 13 MB are the members of groups that stand behind a `select` and had none before. The replay of the recording makes the same operator calls and allocates the same per operator kind as step 5, so the first three times differ by the load of the machine; the second set, taken in turn, shows no difference.
- What changes in answers, on the estate of the tests with today's text: `deploymentReport` 4 of 6 elements name sources (2 before), `healthReport` 10 of 12 (4 before), `workloadStructure` 259 sources in all (227 before). With the KQL text: `deploymentReport` 4 of 6 (0 before), `healthReport` 10 of 12 (0 before). The KQL text names the edges of a pattern, so its elements now also name the relationships the pattern walked; today's text leaves them unnamed.
- The state digests equal those of step 5 at every chunk size (estate, `lab/fixtures`, both recordings; 12 of 12).
- Proofs: `An_element_names_what_it_is_made_of_through_select_and_where` (5 plan shapes, each with a retraction; 4 failing before) and the two cases of `Review2KqlLineageTests`, failing before (2 of 6 and 4 of 12 elements). `Sattle.Differential` every category 324 of 324; the default run 739 passed, 2 skipped; the KQL tests 168 of 168 with the two test changes of the KQL front end. Seal format 4.
- Rule that follows for every operator: a batch may hold columns behind those its layout names, and a reader goes by the layout. `Window` and `Join` took the number of columns from the batch and now take it from the layout.
- Left as it is: an element emitted from the rows of two groups names the rows of one; a windowed group, a roll-up, a threshold and a series name the elements of the group key only.

## Operators stream: each keeps one output batch and fixed working buffers (2026-11-17, #39)
Every operator call built a new output batch, 64 rows of every column and the weights, even when it wrote no row, and the expressions rebuilt their presence arrays and argument arrays on every call. The rule now (decided by Andre on #39): **an operator's output batch belongs to the operator and is valid until it runs again; a reader that keeps rows copies them**, as a compiled expression's column already was. Each operator empties and refills one batch; the aggregating operators evaluate into working arrays they keep; an expression keeps its presence array and its argument array. A row of a reused batch starts zero and present, as in a new batch. A Debug build poisons the rows of an emptied batch, so a reader that kept them fails the tests; it found one writer that relied on new rows being zero (the roll-up writes its no-data column only on marker rows). Nothing else changes what is computed.

`sattle replay` of the newest recording at chunk 1 with `--trace-alloc`. The allocation trace counted its own bookkeeping (the dictionary keys it made and the growth of the step's output map, about 400 B per call) as the operator's; it now reads the counter before either, and both builds below carry that fix. At chunk 1 a call carries at most one input row, so bytes per call bound bytes per input row.

| | Windowed group allocated (per call, 1,277,240 calls) | Operators allocated (13 kinds) | Operator time (13 kinds, one run each) | Peak resident | Runtime lines |
|---|---|---|---|---|---|
| Before (main at #107) | 5,998 MB (4,696 B) | 24.9 GB | 8.3 s | 793 MB | |
| Streaming operators | 61 MB (48 B) | 4.1 GB | 4.8 s | 781 MB | +83, −49 |

- Per kind, before and after, MB: join 7,617 and 321, retain 2,925 and 59, match 2,328 and 394, series 1,865 and 55, filter 764 and 15, windowed group 5,998 and 61; union 2,054 and below the 14 kinds the trace lists. Unchanged: the emits (408, 106), link 1,222 and traverse 1,589 to 1,405, which make objects per row (property maps, paths); they are the next step.
- What the windowed group still allocates is state: rows of new keys and a key table per new window (897 windows, 5.5 KB each, 4.9 MB). Counted inside the operator: about 15 MB in all, 12 B per call; the rest of the 61 MB is the granularity of the counter.
- Peak memory hardly moves: what was removed was short-lived. Peak is the kept state of the graphs.
- Wall time: the machine ran at a load average of 50 to 250 (other sessions), so single times say little: 23.9 to 61.4 s before and 21.0 to 41.2 s after over the alternating runs.
- The state digests are unchanged (estate, `lab/fixtures`, both recordings).

## Memory cost of the week's foundations (#108, 2026-11-17)
Theory. A step may keep what the next change needs, keyed by what exists now; it must not keep what grows with the input, and a change must cost what it touches, not what the estate holds. Three places broke that rule, and each is fixed here:
- **Selects** matched every change against every Service and Deployment of the estate and compared the rows of a namespace pairwise. Label selection is local to a namespace, so the state is now kept per namespace (a reduce keyed by cluster and namespace): a Service change is matched against the Deployments of its namespace only, a Deployment change against its Services, and each (Service, Deployment) row carries a count of the object pairs that give it. A row is written when its count leaves 0 and taken back when it returns to 0; an object delivered again unchanged writes nothing.
- **Dropped rows** were written with the engine's interner, which only grows: every text of a row no source binds stayed in memory for the life of the space. Those rows are never read, so their writers now keep no text table: the rows are counted and their texts are not kept.
- **The inputs kept for a definition commit** were held in memory up to 40 million rows even when the space has a journal, which holds the same inputs. A space with a journal now keeps none and reads them from the journal (the path a seal restart already took); a space without a journal keeps them up to the cap as before.

Per foundation: the structure that holds its state, its cost per change, and what it retains.

| Foundation | State (structure) | Per change: time, allocation | Retained per element |
|---|---|---|---|
| Live input, dedup window | `Queue<string>` and `HashSet<string>` of the last 20,000 envelope keys | one hash lookup and insert; the key's JSON is parsed for the lookup | about 150 B per key (a key of about 48 characters, a set entry, a queue slot): about 3 MB for the full window, constant |
| Live input, journal and recording | the journal store (SQLite), the recording file | one journal append and one file append per envelope; nothing in memory | none in memory |
| Live input, retry position and lag | one position text per source; the lag is one record after processing | one dictionary write | one entry per source |
| Live input, inputs kept for a definition commit | `List<EngineInput>` of row batches (before: always, up to 40 M rows; now: only without a journal) | one list append | 8 B per column per row (4 to 11 columns: 32 to 88 B per row), so 1.3 to 3.5 GB at the cap before the fix for a space with a journal; now 0 |
| Selects | per namespace: Services and Deployments by key, and a count per (Service name, Deployment name) | before: every Service and Deployment of the estate, then a pairwise comparison of the namespace's rows; now: the other kind of the same namespace, one label check each | a label array per Service and Deployment, two dictionary entries, one count per pair |
| Roll-up marker and origin | one boolean on the relationship type in the catalog; the origin is read by the canvas | none | none per element |
| Projection sources | none after lowering: the lowered operators are catalog elements the plans need, the file texts are the definitions the host keeps for a restart | per catalog build only | none per projection beyond the catalog |
| Dropped-row counts | `Dictionary<string, long>` by adapter and row type, one row writer per unbound type | before: the row's texts interned for good; now: counted, the texts not kept | one entry and one writer per unbound type |

Measured on the estate of the tests (73 master nodes, 2,266 records) and capture 20261110T140542Z (48,287 master entities, 160,300 records), Release builds of each commit in a clone, `sattle bench phases --capture` (allocation per record and per row after setup) and `sattle replay --census` (bytes retained per structure after a forced compacting collection). The capture before #104 replays with the space overlay without its two `selections` lines. One run per figure; the machine was shared (load average up to 581 at the end), so times are noisy and byte figures are not.

| Build | Estate: adapters, B per record allocated / B per master node retained | Estate: replay (writers, dropped counts), B per node | Capture: adapters, B per record allocated | Capture: allocated per input row (whole path) | Capture: adapters / replay / interner retained | Capture: live bytes |
|---|---|---|---|---|---|---|
| before the roll-up marker (b831eef) | 6,893 / 400 | 1,362 | 10,007 | 16,455 B | 0.3 / 0.1 / 33.8 MB | 237 MB |
| roll-up marker (82db158; measured at 99f0ee8, which also holds the live input changes of #102 and #105) | 6,893 / 400 | 1,362 | 10,025 | 16,292 B | 0.3 / 0.1 / 33.8 MB | 236 MB |
| projection sources (measured at e150797) | 6,893 / 400 | 1,362 | 10,025 | 16,291 B | 0.3 / 0.1 / 33.8 MB | 236 MB |
| Selects (f3b5e81) | 6,955 / 428 | 1,437 | 10,032 | 16,433 B | 0.3 / 0.1 / 33.8 MB | 236 MB |
| dropped-row counts (8564071, measured at 4fa7f8c) | 6,955 / 428 | 1,439 | 10,030 | 16,432 B | 0.3 / 0.1 / 33.8 MB | 236 MB |
| streaming operators (#39, 955c66f) | 4,284 / 428 | 337 | 10,031 | 12,858 B | 0.3 / below 0.1 / 33.8 MB | 239 MB |
| this change, on 955c66f | 4,281 / 436 | 337 | 10,026 | 12,857 B | 0.3 / below 0.1 / 33.8 MB | 239 MB |

- The roll-up marker and the projection sources cost nothing measurable per element, as the structure says. Selects costs 28 B per master node in the adapter on the estate (its 3 Services and 2 Deployments) and 75 B in the replay (the `SelectsRow` writer); on the capture 1 B per node. The dropped-row counts cost 2 B per node on the estate. This change adds 8 B per node on the estate (the per-namespace index).
- The rows of #39 and of this change are measured on 955c66f, whose batch builders reuse their arrays: that, not this change, moves the estate's adapter and replay figures. The allocation trace's fix in #39 changes which operator a byte is counted to, not the totals above, which are the thread's own counter around each phase; the adapter phase runs outside the engine and is not traced.
- At scale the old matching is the cost. A synthetic capture of 20 namespaces, each with 100 Services and 100 Deployments, delivered twice (8,000 records), on 955c66f, alternating: the adapters took 42.1 and 46.1 s and allocated 40.8 GB (5.1 MB per record) before; 0.21 and 0.26 s and 40 MB (5.0 KB per record) after. The label check now compares the pairs in place: as a `Contains` on the label array it boxed every pair it compared.
- The engine's ingest of the same 8,000 records allocates 15.2 GB (1.9 MB per Kubernetes record) with and without this change. That is the cost of the operators on many Services and Deployments, not of Selects, and belongs to the efficiency concept (#3, #49).
- State digests are unchanged by this change: estate `sha256:32bcad1fe77cdfb8d85119b9238d9e1cebb3aa063c2554eab6ce922c52ffa422`, capture 20261110T140542Z with the overlay `sha256:bd7dd35664dec61df5868c9d35c8bc97620ecc5fa7670b2b9439d15f074ccb13`.
- Proofs: `The_texts_of_dropped_rows_stay_out_of_the_interner` (fails before: the text is interned), `Selections_of_several_Services_and_Deployments_follow_each_object_in_its_namespace` and the Selects, dropped-row, forwarder and restart tests as before. On 955c66f with this change: `Sattle.Differential` 241 of 241, X12 16 of 16, the KQL tests 229 of 229, the default run of `Sattle.Tests` 674 passed and 2 skipped, and the slow restart, seal, live-crash and forwarder tests 63 of 63. A seal of a live space holds one text table: the writers of unbound rows hold none, which a first variant with a second, emptied table broke.
- Left as it is, by measurement: a live envelope's JSON is parsed four times (its key and its envelope, once to decide on duplicates and again when its journal entry is applied); the parse costs 28 KB per record on the capture, so the live path pays about twice that. It does not grow with the input.

## Streaming record reading: one parse per record, texts straight into rows (#45, 2026-11-17)
Theory. A record is read once, as the UTF-8 bytes it is stored in, into one token index, and everyone who reads it reads that index: the duplicate check, the dedup window and the adapter on the live path, the adapter on a replay. Texts go from the record's bytes into the interner without a string; only a text the interner does not have yet becomes one, so what a record allocates is the rows it writes and the new values it brings. The 1.9 MB per Kubernetes record of #108 is not reading: on that synthetic capture reading costs 2.6 KB and the adapters 5.0 KB per record, and `TraverseOp` (8.6 GB) and `JoinOp` (3.3 GB) of the graph `healthTree` make the rest (#110, #3, #49).

| Structure | Per record: time | Per record: bytes | Retained |
|---|---|---|---|
| line buffer: one byte array per open capture file, rented, grown to the longest line, reused | one vectorized search for the line end | 0 | the longest line per open file |
| token index: `JsonDocument` over the line's bytes (no copy), its table of 12 B per token rented and returned when the next line of the file is read | one tokenizing pass | 72 (the document object); with the envelope and its source text, 217 per Hubble flow | none |
| field lookup (`TryGetProperty`, paths as `params ReadOnlySpan<string>`) | linear in the members of the searched object | 0 | none |
| text into a row: the value's UTF-8 decoded into a stack buffer of 256 characters, looked up in the interner by span (longer or escaped values: a string) | one hash lookup | 0 for a known text, one string for a new one | the interner, which grows with new values only |
| AKS `properties.log`: unescaped into a rented buffer and indexed there | one unescape and one tokenizing pass | 0 | none |
| VNet flow tuple: split in place into 13 ranges | one scan | 0 | none |
| live: `LiveEnvelope` hands its parsed envelope to the apply of its own journal entry; a restart parses each journaled envelope once | one parse instead of four | the key, when the envelope is consumed | the 20,000-key dedup window, unchanged |
| replay writers keyed by (adapter, row type) | one lookup | 0 (before: the key text per row) | one writer per row type |
| whether a graph reads source progress: computed when the engine's graphs are built | none | 0 (before: a walk over every graph's operators per record, about 1.2 KB) | one boolean |

Rejected: a `Utf8JsonReader` state machine per adapter. A forward pass over a Hubble flow costs 10.6 µs and 0 B, the pooled index 14.4 µs and 72 B. JSON member order is not fixed (a watch event may put `kind` after `spec`, and `is_reply` decides which end of a flow is the caller), so a forward reader keeps what it read until it knows what it means, which is an index written by hand per adapter: ten adapters as state machines for about 4 µs per record. `JsonDocument` is the read-only DOM of System.Text.Json, held here as a flat token table over the bytes with no object per node; that departs from "no DOM parse" in the issue's done-when, on these numbers.

`sattle bench phases` of the estate (`--cluster synth`) and of capture 20261110T140542Z (with the space overlay), Release builds of 9211a7f and of this change, alternating, two runs each, load average 35 to 145.

| | Estate: read, B per record | Estate: adapters, B per record | Capture: read, B per record (µs per record) | Capture: adapters, B per record (s) | Capture: allocated in all |
|---|---|---|---|---|---|
| before (9211a7f) | 2,809 | 4,220 | 28,318 (19 to 26) | 9,993 (4.3 to 6.2) | 9,762 MB |
| this change | 523 | 2,465 | 271 (15 to 18) | 1,797 (2.6 to 2.9) | 3,952 MB |

- By source on the capture (adapters, B per record, before and after): Hubble flows 1,977 and 184 (146,759 records; what is left is the row batch the engine takes), AKS audit 245,326 and 15,248 (new audit ids and the rows), VNet flow logs 398,075 and 129,955 (the rows of about 350 tuples per record), Kubernetes 2,544 and 1,070. The Hubble adapter's time fell from 4.1 s to 0.4 s: most of it was the walk over the operators per record.
- Live path, 20,000 Hubble envelopes through `LiveEnvelope` with the journal in memory (engine included), two runs each: 106 to 113 µs and 38,960 B per envelope before; 99 to 100 µs and 15,480 B after.
- `RecordReadingTests`: the lines of a file with LF, CRLF, a byte order mark, no final newline, empty lines and a line longer than the buffer give the envelopes of their texts; texts with escapes, beyond ASCII, empty and longer than the stack buffer are written as their values; hosts as `HostOf` gives them; an audit event inside a string with its escapes; reading and consuming a flow of known texts allocates under 1 KB per record (3,227 B before, which fails it).
- State digests unchanged: estate `sha256:32bcad1fe77cdfb8d85119b9238d9e1cebb3aa063c2554eab6ce922c52ffa422`, `lab/fixtures` `sha256:4a1560a5b4df685d764a5a491fa008594b36f583bfd4a0df5d8ae09128b6ffd0`, 20261110T105738Z `sha256:de8fe8a84f6567ff4a2630571a49ca671d4741e745a1a9d44779d6d5a2aa4520` and 20261110T140542Z `sha256:bd7dd35664dec61df5868c9d35c8bc97620ecc5fa7670b2b9439d15f074ccb13` (both with the overlay).

## Scale ladder for gate 1c, phase 1: 10^4 and 10^5 master nodes (#49, 2026-11-17)
**Input.** `sattle bench gen-estate --nodes N --out DIR` writes a capture in the format of the test estate, scaled by its workload namespace. Per namespace it holds the Services web, api and database, 5 pods, 2 Deployments, service accounts and EndpointSlices, and 10 Azure resources (cache, storage account, PostgreSQL, API Management and a backend, 3 private endpoints, a managed identity and a role assignment), so 18 master nodes. There is one Kubernetes node and VM per 30 pods, in one cluster and one subscription. Per minute it writes one flow per active pod pair (0.24 per master node, so 240,000 at 10^6), four endpoint flows per namespace, one Azure Monitor point per platform resource, and Prometheus samples per node. Scenarios A to C (cache, database, crash loop) run in one namespace of every hundred, with the estate's signal rules. The first namespace is `shop`, so the tracked space binds the workload model to it. The input is deterministic and is not in git (`tmp/`). 10^4: 81,598 records (40 MB); 10^5: 816,075 records (402 MB); 10 minutes each.

**Harness.** `sattle bench phases --capture DIR --census` adds three things to the phases of the replay: the thread's CPU per phase (`CLOCK_THREAD_CPUTIME_ID`), the peak resident set (`ru_maxrss`), and the census at the end, per master node (pods, Services and Azure resources). A bug is fixed: the closing flush and advance were counted to the engine but not to the processing total, so the adapters read negative.

Release build of 1b89b48 plus the harness, in a clone. Load average 140 to 300 (other sessions). Byte figures do not depend on the load; times are pessimistic.

| | 10^4 (9,995 master nodes) | 10^5 (100,011) | budget (research 04, 1.2) |
|---|---|---|---|
| wall, thread CPU, process CPU | 23.2 s, 21.4 s, 25.5 s | 822 s, 646 s, 731 s | |
| engine ingest, wall and CPU | 20.2 s, 18.8 s | 800 s, 627 s | |
| allocated per input row (whole path) | 531 KB | 637 KB | 1 KB per record |
| same, without `LinkOp` and `TraverseOp` | 57 KB | 56 KB | |
| GC pause | 0.7 s (3 %) | 44.7 s (5 %) | 5 % |
| peak resident set | 963 MB | 4,879 MB | |
| live bytes at the end | 422 MB | 3,191 MB | |
| per master node | 42.2 KB | **31.9 KB** | 2.2 KB |

**Retained bytes per master node by structure** (census, coverage 98 and 100 %; the top 14):
| structure | 10^4 | 10^5 | what it is |
|---|---|---|---|
| `SeriesOp` | 6,042 | 5,939 | a `SortedDictionary<long, decimal?[]>` per element, one tree node and one array per window: 3.79 M windows, about 116 B each for the node and the array |
| `TraverseOp` | 6,179 | 5,835 | the reach of every start as a `List<Entity>`, a copy of each reached entity with its properties as strings and `TextValue`s |
| `JoinOp` | 3,596 | 2,925 | the tables of both sides |
| `GroupOp` | 2,836 | 2,578 | accumulator tables |
| telemetry store | 1,854 | 1,828 | a `(decimal, long)[]` per series, and the change log |
| interner | 1,771 | 1,643 | 1.24 M strings at 10^5, 12 per master node |
| `WindowedGroupOp` | 1,742 | 1,346 | |
| hot graph byte pool | 1,386 | 1,180 | |
| sink contributions | 867 | 834 | |
| `RollupOp` | 828 | 702 | |
| `ThresholdOp` | 617 | 603 | a sorted tree of windows per key |
| `MatchOp` | 2,764 | 571 | mostly fixed: an ontology view per operator (20 MB at 200 nodes) |
| identity catalog | 570 | 507 | |
| engine rest, statics | 2,170 | 217 | fixed (21.7 MB) |

Per graph at 10^5, MB: `healthTree` 1,031; shared (interner, telemetry, identities, statics) 420; `workloadStructure` 375; `master` 307; `workload` 264; `treeStructure` 191; `treeSignals` 183; `serviceTree` 170; `treeRollup` 160. `workloadStructure` and `workload` produce 86 elements (the namespace `shop`) and hold 639 MB, 6.4 KB per master node.

**Work by operator, 10^4 and 10^5** (ms and MB allocated; the growth for ten times the nodes):
| operator | 10^4 | 10^5 | growth of the allocation |
|---|---|---|---|
| `TraverseOp` (almost all `healthTree`) | 3,150 ms, 1,404 MB | 487,306 ms, 130,853 MB | 93 times: quadratic |
| `LinkOp` (identity merges) | 12,997 ms, 41,037 MB | 203,917 ms, 390,577 MB | 9.5 times: linear, about 8.8 MB per call |
| `JoinOp` | 686 ms, 78 MB | 66,126 ms, 544 MB | time 96 times (downstream of the reach churn) |
| all others | about 4.8 s, 1.6 GB | about 36 s, 13 GB | about linear |

**What blocks 10^6.** Linear structures would put about 32 KB per master node, so 32 GB live, against the 2.2 KB budget (a gap of 14.5 times). The traversal is quadratic: 487 s at 10^5 would be about 13 h at 10^6. The merges would allocate about 3.9 TB. The theory notes of the hogs are on #49, one per hog, before any fix.

### Hog 5: the workload views read only what the workload has (#49, 2026-11-17)
Theory. A view of one namespace should keep state for that namespace. Two reads came before the filter that narrows them to it. In `workload`, the meters of every Azure resource and node went through the joins with the rules, the `Series`, the roll-up of `ComponentSignal` and the hysteresis; only `measured` joined them with the workload's components. In `workloadStructure`, `destinationOf` resolved every Destination of the estate, not only those the workload's pods send to. Now the meter reads join `comps` before `windowsOf`, and `destinations` comes from `sent`. No engine change.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 422 / 403 MB | 3,191 / 3,031 MB |
| per master node | 42.2 / 40.3 KB | 31.9 / 30.3 KB |
| `workload` | 36.7 / 24.3 MB | 264.2 / 149.8 MB |
| `workloadStructure` | 53.1 / 51.3 MB | 374.9 / 350.8 MB |
| telemetry store | 18.5 / 16.5 MB | 182.9 / 162.9 MB |
| allocated | 47,588 / 47,496 MB | 571,251 / 570,471 MB |

- Times are not comparable: load average 128 to 300 during all four runs, and another project was rendering during the after-runs. Bytes are valid under load.
- What stays is the `Series` state: a `Series` keeps the windows of every element of the read graph, whatever its other side joins. The three `Series` of `workloadStructure` alone hold 20 MB at 10^4. That is hog 4, and no definitions change reaches it.
- Digests:
  - estate unchanged (`sha256:32bcad1f...a422`), deploy-health unchanged;
  - estate-wide moves from `sha256:825fead7...a533` to `sha256:debc3276...8cc`.
- Why estate-wide moves: its graphs are identical (`workload` 20 entities, 20 relationships). Its `workload` telemetry falls from 146 to 56 windows, and 9 fewer identities are minted (246 to 237). Before, `extend nodes ComponentSignal` wrote reading windows for the signals of meters outside the workload. Those signal elements are never created, so the windows had no element. The estate has no such meters.
- The `acme.workload` pin in `acme.space` moves.

### Hog 4: a `Series` keeps the windows of the elements it joined (#49, 2026-11-17)
Theory. A `Series` joins the structural rows with the closed windows of the read graph, keyed by element. The telemetry store already indexes those windows by element. The `Series` kept the windows of every element of the read graph, whether or not a row joined it. Now it keeps the windows of the elements with a row only, as emitted, so it can retract them at the look-back edge after the store has dropped them by retention. When an element gets its first row, its windows of the look-back are read from the store. The read graph runs earlier in the same step, so the store holds exactly the window changes the `Series` has received. When the last row goes, the windows go. Per joined element the windows are a `SortedList<long, decimal?[]>`: about 72 B per window (the values array, 8 B start, 8 B reference) instead of about 116 B (tree node and array). Seal format 6.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 403 / 359 MB | 3,031 / 2,596 MB |
| per master node | 40.3 / 35.9 KB | 30.3 / 26.0 KB |
| `Series` state | 58.0 / 14.2 MB | 572.2 / 134.8 MB |
| `Series` per master node | 5,808 / 1,420 B | 5,721 / 1,348 B |
| windows a `Series` holds | 378,758 / 71,048 | 3,789,028 / 708,718 |
| `workload` | 24.3 / 12.9 MB | 149.8 / 35.4 MB |
| `workloadStructure` | 51.3 / 31.1 MB | 350.8 / 149.0 MB |
| `Series` allocated | 298 / 132 MB | 2,920 / 1,261 MB |
| peak resident set | 901 / 875 MB | 4,613 / 3,852 MB |

- Before is hog 5's after-run. Both runs have hog 5's definitions.
- Times are not comparable: the load average at the start was 128 and 72 before, and 609 and 568 after. Bytes and allocations are valid under load. The `Series` operators' own time still fell (10^5: 2,658 to 2,188 ms), the store reads included.
- What the `Series` still holds at 10^5: the windows of the views that join the whole estate, and its structural rows (the `Int64[]` and `Decimal[]` per row).
- The telemetry store grows by 2.3 MB at 10^5: two 24 B wrappers (`Keys`, `Values`) that a `SortedList` caches once its scan touches a series. That is about one object per master node.
- Digests: unchanged. estate, deploy-health, estate-wide and the lab fixtures equal the pins with hog 5.

### The run at 10^6 (#49, 2026-11-17)
Release build of f9e3121, which includes hogs 1 to 5, in a clone. Input from `sattle bench gen-estate --nodes 1000000`: 55,046 namespaces, 1,000,006 master nodes (275,230 pods, 165,138 Services, 559,638 Azure resources), 9,175 Kubernetes nodes, 240,001 active pod pairs and 220,184 endpoint flows per minute, 10 minutes: 8,159,533 records, 3.9 GB. Command: `sattle bench phases --capture DIR`, then again with `--census`. Machine: Apple M1 Max, 10 cores, 64 GB. Other sessions were paused. Short jobs of another project still ran, with a load average of 20 to 90 during the runs. So the machine was quiet, not idle, and it is not the reference hardware of Def. 13.4.

| | run 1 | run 2 (`--census`) | budget (research 04, 1.2) |
|---|---|---|---|
| load average at start | 54 | 20 | |
| wall | 677 s | 590 s | |
| engine ingest, wall | 424 s | 391 s | |
| engine advance (12 window closes), wall | 219 s | 170 s | |
| process CPU | 746 s | 711 s | |
| allocated per input record (whole path) | 58.6 KB | 58.6 KB | 1 KB |
| GC pause | 37 s (5 %) | 22 s (4 %) | 5 % |
| peak resident set | 15.3 GB | 16.1 GB | |
| live bytes at the end | | 19,955 MB | |
| per master node | | **19.95 KB** | 2.2 KB |

- From 10^5 to 10^6, live bytes per master node fell from 26.0 KB to 19.95 KB, and the replay grows about linearly with the input: 55 s at 10^5 (hogs 1 to 3) against 590 to 677 s at 10^6. Before the hogs, 10^6 was estimated at about 13 h and 32 GB.
- The gap to the budget is about 9 times for memory and 59 times for allocation per record.
- Retained bytes per master node by structure (census, 42.7 M objects, coverage 100 %, walk 30 s): `JoinOp` 2,585; `GroupOp` 2,311; interner 1,863 (12.1 M strings); telemetry store 1,657; hot graph byte pool 1,485; `SeriesOp` 1,307; `WindowedGroupOp` 1,099; `TraverseOp` 987; sink contributions 772; sink values 629; identities 452; lineage sinks 422; entity and relationship tables 410 and 386; sink ends 386; `MatchOp` 329; `ThresholdOp` 325 (2.9 M tree nodes); telemetry windows of the sinks 302; `EmitRelOp` 279; `MapOp` 244.
- Work by operator, run 1 (ms, MB allocated): flush, apply and store commit 131,907 ms; `TraverseOp` 92,663, 9,204; `MatchOp` 88,271, 40,257; `EmitRelOp` 44,067, 14,147; `EmitEntityOp` 40,843, 18,901; `SeriesOp` 19,881, 12,072; `GroupOp` 18,315, 3,274; `JoinOp` 16,668, 3,714; `LinkOp` 3,577, 3,525. By graph, `healthTree` costs most (operators 134 s, commit 21 s), then `master` (49 s, 65 s) and `workloadStructure` (53 s).
- Raw outputs: `tmp/orchestrator/n1m-phases.txt` and `n1m-census.txt` (not in git).

### Efficiency stage 2, joins: a stored row holds what a change needs, in the width it needs (#115, 2026-11-17)
Theory. A join keeps the rows of each side as a Z-set arranged by the join key. A row of one side needs the rows of the other side with the same key and all their columns, and its own side's row to add its weight to. A stored row was the join key, every column and a word of absent bits, 8 bytes each. Three things in it carry nothing: a join key part that is a column of the row (`on $left.dkey == $right.ekey`, the common case) was stored twice; a column of codes (a text, an element, a relationship, a decimal as its number) is an `int` and took 8 bytes; and a column of codes is never negative, so it can mark its own absence as -1. Now the arrangement reads a key part that is a column from that column, codes take 4 bytes, and only numbers (integer, instant, boolean) have an absent bit, in 4-byte words. The weight, the index on the whole row and the arrangement stay: without them a step would be proportional to a key's rows. Seal format 6 to 7.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 313 / 301 MB | 2,116 / 2,019 MB |
| per master node | 31.3 / 30.1 KB | 21.2 / 20.2 KB |
| `JoinOp` per master node | 3,519 / 2,307 B | 2,771 / 1,795 B |
| allocated per input record | 60.9 / 60.6 KB | 58.8 / 58.5 KB |

- Example: the reach that `healthTree/counts` joins, 42,458 rows of (`rkey`, `dkey`) at 10^4, stored 32 B of values per row, now 8.
- What stays per stored row is mostly overhead: weight 8 B, index 8 to 16 B, arrangement 8 B and its index.
- Before is f50d6fc. Release builds in clones; times are measured under load (load average 80 to 190, another worker's tests running) and are not comparable. Bytes are.
- Digests unchanged: the generated estate at 10^4 (`sha256:2654c25f...bfbc3`) and 10^5 (`sha256:0ab80689...1140`), and the pinned scenario digests (estate, deploy-health, estate-wide) and the lab fixtures.

### Efficiency stage 2, groups: an `arg_max` row stores integers and codes as themselves (#115, 2026-11-17)
Theory. A group keeps per key a row of accumulators and, for `arg_max` and `arg_min` (`last` aggregates), every distinct input row of the key, since the next winner comes from the key's other rows when the winning row goes. That row stored every value as a decimal: two 8-byte halves as its key (so 2.5 and 2.50 are one value) and two more for its digits, 32 B per value, also for integers and codes. Now only decimals keep that form; an integer is one 8-byte column, a code one 4-byte column, and the bits of which members have a value take 4 bytes up to 31 members. The key columns of a group (also windowed groups and roll-ups) take 4 bytes for a text, an element or a decimal (its number's code). Nothing kept changes; it is stored in fewer bytes. Seal format 7 to 8.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 301 / 286 MB | 2,019 / 1,874 MB |
| per master node | 30.1 / 28.6 KB | 20.2 / 18.7 KB |
| `GroupOp` per master node | 2,836 / 1,837 B | 2,578 / 1,564 B |
| `WindowedGroupOp` per master node | 1,742 / 1,220 B | 1,346 / 930 B |
| allocated per input record | 60.6 / 60.0 KB | 58.5 / 58.3 KB |

- Example: `healthTree/rolledAll`, `arg_max(rank, healthy, degraded, unhealthy, monitored, severity) by key`, two rows per key: 204 B per row before, 56 B now.
- Before is the joins patch. Times are measured under load and not comparable; bytes are.
- Digests unchanged, as for the joins.

### Efficiency stage 2, interner: a text is its bytes, not a string object (#115, 2026-11-17)
Theory. At 10^4 the interner holds 127,466 texts: 89,074 element ids (`e:` and `r:` with 26 base32 characters), 19,553 paths, the rest names, numbers and labels; at 10^6 about 12 per master node. Every one is live: an element id lives as long as its element, and its code is the handle in sinks, lineage, telemetry and operator tables. So the texts stay and their form changes. A text cost a .NET string (80 B for an id), a dictionary entry with its bucket, a list slot and a young flag, about 134 to 154 B. A lookup needs a hash and a comparison with the stored text; `Get` needs the characters. Now the texts are bytes in pages of 64 KB (one byte per character when every character is below 256, else two), and per code its place (8 B), its length with the young mark (4 B) and its hash (4 B); one open-addressing index at a load of at most 3/4. Freed texts leave dead bytes, and the pages are copied together when more bytes are dead than live, as `Values` does. Codes, young and old, the reuse of the smallest free code and the seal are unchanged.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 286 / 278 MB | 1,874 / 1,786 MB |
| per master node | 28.6 / 27.8 KB | 18.7 / 17.9 KB |
| interner per master node | 1,752 / 1,007 B | 1,624 / 745 B |
| bytes per text | 137 / 79 | 134 / 61 |
| allocated per input record | 60.0 / 60.5 KB | 58.3 / 59.3 KB |

- Cost moved: `Get` makes a string. Most calls are the sinks resolving element ids into the elements of a patch. A direct-mapped cache of the last 16,384 strings (fixed, about 1.5 MB) keeps repeats; at 10^4 a cache of 4,096 cost 0.75 KB per record more, one of 65,536 0.2 KB and 6 MB. At 10^5 allocation rises by 1.0 KB per record (1.8 %).
- At 10^4 the per-code columns are in segments of 65,536 codes, so their slack is large; at 10^5 a text costs 61 B, and about the same is expected at 10^6 (about 740 B per master node instead of 1,863).
- A lookup by span allocates nothing: reading a flow of known texts costs 337 B per record, as before this change (`RecordReadingTests`). The test of whether a text needs two bytes per character is a loop; `ContainsAnyExceptInRange` allocated 96 B a call, 1,108 B per record in that test.
- Not solved: the +1.0 KB per record at 10^5 is `Get`, mostly in the advance phase (17.7 KB per record against 16.9). Its callers that need a string are the sinks building the elements of a patch (`ElementLayer`: ids, types, property keys, text values), the identity catalog and text functions (`Concat`, `Trim`, `ToLower`). The patch and the hot graph take strings, so the edge is there.
- Before is the groups patch. Times are measured under load and not comparable; bytes are.
- Digests unchanged, as for the joins.

### Efficiency stage 2, telemetry store and Series: one copy of a window (#115, 2026-11-17)
Theory. A Series kept, per element with a row, the windows of its look-back with the values of its reads: a copy of what the store holds, kept to retract a window when it leaves the look-back. Now the store is the one copy. A window change needs the record before and after: after is the store now (the read graph steps first), before is the store with the first old value of each changed read put back, which the change carries. Changed windows join the rows before the step, then changed rows join the windows after it (`Δ(R ⋈ W) = R_old ⋈ ΔW + ΔR ⋈ W_new`), so an element's first row needs no special case. At an advance the windows that leave the look-back are read from the store, so retention now runs after every graph of the epoch stepped, not after each graph: before, the read graph could drop a window before the Series retracted it. Every window a Series can need starts at or after `frontier - within`, and `within <= retention`, so the store holds it; the store after the epoch is the same as before. The store keeps a series as its starts and values in two arrays and its width (it was a `SortedList` with two wrappers and a second dictionary for the width). The input id of a window goes: it was compared only together with an equal value, and then no change is logged either way. The change log is released as it is read (one reader per plane, its graph, right after its step); it held the last step's changes and handed out a copy. A Series row stores each column once, in `long` or `decimal` by its type (it held both arrays at full width). Seal format 8 to 9.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 278 / 263 MB | 1,786 / 1,636 MB |
| per master node | 27.8 / 26.3 KB | 17.9 / 16.4 KB |
| telemetry store per master node | 1,674 / 1,041 B | 1,652 / 1,030 B |
| Series per master node | 1,420 / 538 B | 1,348 / 473 B |
| store bytes per window | 84 / 52 | 83 / 52 |
| allocated per input record | 60.5 / 60.2 KB | 59.3 / 59.1 KB |

- Windows: 198,968 at 10^4 and 1,987,323 at 10^5, about 10 per series.
- What a store window costs now: 8 B start and 16 B value, with the slack of doubling arrays; per series an object of 48 B and a dictionary entry. Next candidates: values as integers where a series holds only integers, and a column per plane instead of an object per series.
- Cost moved: a Series reads the store for a new row and at an advance, where it read its copy. SeriesOp time at 10^5 1,039 ms before, 1,609 ms after, both under load (another worker's tests ran): not comparable.
- Before is the interner patch. Digests unchanged at 10^4 and 10^5.

### Efficiency stage 2, hot graph byte pool: a text is a code, a block is reused (#115, 2026-11-17)
Theory. Per graph, the properties of every element are a block in one byte array: count, then per property its key number, a tag and the value. A text was its UTF-8 bytes, a decimal its 16 bytes. At 10^4 the 13 graphs held 13.9 MB of pool (1,386 B per master node), 7.8 MB of it in live blocks, of which 6.6 MB were texts. These texts are all in the engine's interner already (the sinks keep an element's fragments as old interner codes, and an old code is never freed), and the same tree keys are in three graphs. An update wrote a new block, because removing the before state forgot the old one: dead bytes grew with the updates, not with the state. Capacity doubled, so up to half was unused. A read needs the values back and a put needs to store them; neither needs the text bytes in the graph. Now a text or an entity id in a block is its code in the graph's text table: the engine's interner for the graphs of an engine, a table of the graph's own otherwise (catalog, reference model, tests). A decimal whose mantissa fits 64 bits is a scale-and-sign byte and a varint, bit for bit. A removed element keeps its block, and its next put reuses it when the props fit; when more bytes are dead than live (and more than 64 KB), the live blocks are copied together.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 278 / 267 MB | 1,786 / 1,696 MB |
| per master node | 27.8 / 26.7 KB | 17.9 / 17.0 KB |
| byte pool per master node | 1,386 / 240 B | 1,180 / 273 B |
| interner per master node | 1,010 / 1,067 B | 742 / 753 B |
| allocated per input record | 60.7 / 58.8 KB | 59.3 / 57.7 KB |

- At 10^5 the live blocks are 19.0 MB of 27.3 MB capacity; the rest is the slack of doubling. Dead bytes are at most what one graph's removals leave before the next copy (serviceTree 0.19 MB).
- Cost moved: a text value is read through the interner's `Get` (its cache of 16,384 strings now holds graph texts too: the interner grows by 57 B per master node at 10^4 and 11 B at 10^5, a fixed amount). A put interns each text, a hash and a compare where it encoded UTF-8 before. Allocation per record falls by 1.6 KB: no UTF-8 arrays.
- The interner's live texts do not change (127,466 at 10^4, 1,212,925 at 10^5): no text was kept for a graph alone.
- One new concept: `ITextTable` (intern a text, get a text), implemented by the interner and by the graph's existing id table.
- Before is the interner patch. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5 and on the lab fixtures. The seal format is unchanged: a seal writes a graph as its elements.

### Efficiency stage 2, allocation per record: the step (#115, 2026-11-18)
Where the 57.5 KB per record at 10^5 went, measured part by part the way operators are (`GC.GetAllocatedBytesForCurrentThread` around each, KB per record, 10^4 in brackets): a dictionary of inputs per operator call 11.3 (11.4); the bench's own keys, two string concatenations per operator call 11.4 (11.5); a dictionary of outputs per graph step 2.9 (3.0); operators 13.6 (14.2); flush of the elements 8.2 (7.8); flush of the telemetry 6.6 (6.9); patch, apply, store commit, telemetry append and read, commit event 2.0 (2.1); epochs and the interner's collect 0.5 (0.4); reading the capture and the adapters 0.9 (1.0).

Theory. A step of a graph runs its operators in plan order. Each operator needs the batch on each of its ports, and the step needs each operator's output until its last reader ran. Both are fixed by the plan, so they need no new object per call: one view of inputs per operator and one table of outputs per graph, made once from the plan and refilled by every step. The views hold batches only while the step runs and let go of them at its end, so no batch lives longer than before (an operator's output batch belongs to it until it runs again, #39). The ports are walked as an array: an enumerator through `IReadOnlyDictionary` is an object. The keys of the measurement are a function of the plan too and are made once per graph; until now every figure of allocation per record in this file held 11.4 KB of the harness, which a run without `TrackAllocations` never paid. The parts after the operators have a row each in `bench phases` (flush elements, flush telemetry, patch and apply, store commit, telemetry append and read, commit event, step dispatch); the row `(flush, apply, store commit)`, which measured time only, goes.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 58.5 / 32.6 KB | 57.5 / 31.8 KB |
| engine ingest per record | 40.4 / 14.5 KB | 39.5 / 13.7 KB |
| engine advance per record | 17.2 / 17.1 KB | 17.1 / 17.1 KB |
| step dispatch | 14.4 / 0.0 KB | 14.2 / 0.0 KB |
| live bytes | 252 / 252 MB | 1,546 / 1,546 MB |

- What remains at 10^5, KB per record: operators 13.6 (`MatchOp` 4.5, `EmitEntityOp` 2.6, `EmitRelOp` 1.9, `SeriesOp` 1.4, `TraverseOp` 0.9), flush of the elements 8.2, flush of the telemetry 6.6, patch and apply 1.1, telemetry append and read 0.7, reading and adapters 0.9.
- Before is the stack of joins, groups, interner, telemetry and pool. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 (`sha256:2654c25f...bfbc3`) and 10^5 (`sha256:0ab80689...1140`). The seal format is unchanged: the working state of a step is not state of the engine between steps.

## H5, H7, H8, H10 (ten runs each unless stated)
Measured in turn 9 on an Apple M1 Max, 10 cores, 64 GB, macOS 27.2.0, .NET 10.0.14, Release build copied out of the build tree, engine commit of the working tree at that time. The machine was shared with other sessions: load average 14 to 60 during these runs, so latencies are pessimistic. Decision rule of paper 13.6: a latency target is supported only if every run is at or below it. The workload of H5 and H10 is the newest real capture (`sattle replay latest` prints its record count and capture span). Build once: `dotnet build -c Release src/Sattle.Cli && cp -R src/Sattle.Cli/bin/Release/net10.0 out/cli`, then run the commands as `dotnet out/cli/sattle.dll ...`.

**H5 source to graph**: `sattle bench b5 --store memory` and `sattle bench b5 --store sqlite`. One capture record at a time through the adapter, row building, `Ingest` and the master commit; time spent in window closes the record triggered is taken out and reported separately. It is service time at maximum replay speed in one process, without source delays, network or Event Hubs.

| Store | p99 of all records (target 2 s) | p99 of the records that made a master revision | window close p99 |
|---|---|---|---|
| memory | median 0.620 ms (0.585 to 0.660) | 1.46 ms (1.08 to 2.73) | 3.8 ms (2.0 to 24.1) |
| SQLite (WAL, `synchronous=NORMAL`) | median 1.998 ms (1.985 to 2.060) | 22.4 ms (20.8 to 25.7) | 31.7 ms (26.7 to 85.3) |

Against a 2 s target for the p99 both are supported. The largest single record in one SQLite run was 1.75 s (median of the ten maxima 82 ms); a p99 does not constrain it. Cosmos was not measured: it needs a real account in the engine's region (`sattle bench b5` has no Cosmos mode; the replay on the emulator is under "Cosmos requests").

**H7 durable reads**: `sattle bench b7` and `sattle bench b7 --cosmos http://localhost:8081` with the emulator (`docker run -d --name sattle-cosmos-emulator -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview --protocol http`). 1,000 point reads and 200 one-hop scans of 100 neighbours per run, 2,000 entities.

| Store | point read p99 (target 10 ms) | one-hop scan of 100 p99 (target 20 ms) |
|---|---|---|
| memory | 0.003 ms | 0.024 ms |
| SQLite | 0.044 ms (0.030 to 0.054) | 0.179 ms (0.116 to 3.894) |
| Cosmos DB emulator (vNext, http, gateway, same machine) | 6.55 ms (5.94 to 31.0), p50 4.47 ms | 11.9 ms (10.5 to 32.6), p50 8.90 ms |

The emulator medians are under the targets and its worst run is not (31 ms), so by the rule the emulator result is inconclusive; and the emulator is not the target (a container on the same machine, no RU accounting: a charge of 1 per request). A point read is one request; a one-hop scan is a query. A real account in the engine's region and steady load are needed for a p99 that means something.

**H8 element view**: `sattle bench b8 --runs 30`. The structure of 10,000 entities at the head joined with 60 windows of 4 series (2.4 million points) from the telemetry ring, one call. Median 17.5 ms (15.6 to 49.4), every run under the 100 ms target: supported. History: the old read path allocated 148 MB per view (2.4 million `SeriesPoint` objects); one run in ten took 300 ms or more because the garbage collector ran in it (gen0 and gen1 collections with 200 to 440 ms pauses, in the same run numbers in three sessions). `ITelemetryStore.ScanWindow` visits windows without allocating (3 MB per view, no collection in any run) and the outliers are gone (`bench b8` prints allocation, collections and pause per run).

**H10 cold start by restart**: `sattle bench b10 --hours N --seal-every 2000 --runs 10` builds a SQLite journal and log of the newest capture repeated with shifted time until the logical span reaches N hours (the Activity Log and Event Hubs records carry absolute event times and are not repeated: repeating them would add the same events again), takes seals, then restarts ten times; `--no-seal` restarts the same file replaying the whole journal (3 runs). A restart loads the latest seal, checks its state and catalog digests, replays the entries after it and compares every recomputed commit with the stored one.

| History | With a seal (10 runs) | Without (3 runs) | Seals | Last seal | State load | Entries replayed after the seal |
|---|---|---|---|---|---|---|
| 1 h | median 7.52 s (7.33 to 7.91) | 6.64 s (6.53 to 10.4) | 2, 2.1 s each | 36.1 MB | 2.4 s | 1,435 (tail 3.4 to 3.6 s) |
| 4 h | median 8.33 s (7.76 to 10.1) | 16.2 s (15.9 to 20.3) | 4, 2.3 s each | 35.2 MB | 2.2 to 2.4 s | 1,602 (tail 3.9 to 4.1 s) |

With a seal the cost no longer grows with history: the sealed state is the same size at 1 h and at 4 h (36.1 and 35.2 MB, 2.4 and 2.2 s to load) because the served patches (2,000 per graph) and the event ring (20,000) are capped (`--retain-patches`, `--retain-events`) and evidence is retained for 24 h. The 0.8 s between the two is the tail: 1,602 entries replayed after the last seal at 4 h against 1,435 at 1 h, at about 2.5 ms an entry. Without a seal the cost is linear in history and passes the sealed restart at about 2 h; at 1 h replaying the journal is faster than loading the seal. Before the caps, the same measurement at 4 h loaded a seal of 45.5 MB in 3.7 to 5.0 s (the 1 h seal was 38.7 MB in 2.4 s); its growth was the repeated activity events held by `Retain` (463,000 more buffered rows between the 1 h and the 4 h seal in the seal's object counts), which the benchmark no longer repeats; the patch history and the ring were capped in the same turn, so the effect of the caps alone was not separated out.

## Cosmos requests
Command: `sattle replay latest --store cosmos=http://localhost:8081,db=NAME` against the emulator (the state digest it prints is the memory store's, `sattle replay latest`). The commit protocol writes one transactional batch per touched partition, reads nothing before writing (the mirror remembers the revision each element's version began), writes identity bindings in one batch per domain partition and commit, the watermark every 64 commits, and runs the partition batches of a commit 16 at a time. Newest capture, 2,099 revisions: requests commit 2,099, follow 26, identity 2,159, materialize 62,745, meta 97, read 67, in all 67,193 (32 per revision); before the batching (turn 7) 195,669 (93 per revision), with 52,307 identity, 78,239 materialize, 60,513 read; wall time 276 s against 1,607 s. The remaining materialize requests are one per touched element partition (about 1.06 per element at head): a transactional batch cannot span partitions. The emulator reports a charge of 1 per request, so no RU figure exists.

## Tests
Commands: `time dotnet test` (the default run: everything except `Category=Capture` and `Category=Slow`), `time dotnet test --settings tests/Sattle.Tests/full.runsettings` (every test, the real captures present included), and with `SATTLE_COSMOS_EMULATOR=http://localhost:8081` set the storage, restart, seal and live-resume tests also run on Cosmos. Turn 12 (a loaded machine, so read the counts rather than the times): the default run is 463 tests in 3 m 37 s (445 s of CPU); the full run without the emulator is 578 tests in 13 m 13 s, with the real captures present and the local space overlay in place. Earlier counts (397 and 517 at turn 9) are stale. The benchmarks (`sattle bench ...`) load the tracked `acme.space`, which binds the invented estate, without the local overlay `lab/captures/space-overlay.sattle`: on a real capture their health chain is the one of an unbound space (nearly empty), so the numbers measure the master, the service tree and the capability level, not the real health model.
