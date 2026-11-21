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
- The allocation per record includes about 11 KB of the bench's own measurement: two string keys per operator call, made while `TrackAllocations` is on (found 2026-11-18, see "allocation per record: the step"). The engine's own figure was measured at 10^4 and 10^5 only: 47.0 and 46.1 KB per record.
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

### Efficiency stage 2, allocation per record: the flush of the telemetry (#115, 2026-11-18)
Theory. Premise B1: a window is written by exactly one row, and then the store's value is its whole support, so nothing is kept for it in the sink. A step's rows for one window are few: one, or one out and one in for a revision. A window needs its rows of the step netted by own id and value, and its stored value; a short list held for the whole flush serves both. The rows of a window are chained in arrival order (first, last, and the next of each row, in one pooled array), so the windows settle in the order their first row came, as before. The stored value is one lookup: the telemetry port gets `ValueOf(g, window)`, the twin of `AuxOf`, where a `ReadWindow` of one window made two arrays, a list and a record. A dictionary is made only for a support the sink keeps, one that is not one row of weight one. The text of a conflict subject (`window e p ws`) was made for every window; a window has a conflict only while its support is kept (a conflict needs two values, and a support of two values is kept, and the settle that drops the support clears the conflict), so the subject is made only for a kept support, a conflict, or a window a merge moved. Windows written under two own ids keep their path. The samples reach `Append` as a span of the list, not as a copy.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 32.6 / 26.7 KB | 31.8 / 26.2 KB |
| flush of the telemetry | 575 / 113 MB | 5,402 / 1,038 MB |
| telemetry append and read | 61 / 41 MB | 608 / 401 MB |
| live bytes | 252 / 252 MB | 1,546 / 1,547 MB |

- What remains in the flush of the telemetry, 1.3 KB per record at 10^5: the step's dictionary of windows, the samples list and the working lists, made per flush.
- Before is the step patch. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5. No state is added, the seal format is unchanged. One new method on the telemetry port.

### Efficiency stage 2, allocation per record: the operators (#115, 2026-11-18)
Where the operators allocated at 10^4, measured part by part inside them: `MatchOp` 372 MB (an array of (state, sign) per edit and seed 122, the candidate edges 81, entity reads 73, the old view's dictionaries per call 50); `EmitEntityOp` 207 MB, of which minting identities 167 (about 4 KB per new key); `EmitRelOp` 157 MB, of which relationship ids 108 (1.8 KB per new pair); `SeriesOp` 122 MB, of which grouping the step's window changes into a new dictionary of lists per call 60.

Theory. An id is the hash of a canonical text; the text is a function of the key and is needed only while it is hashed. It is built in a buffer of the thread that is reused, encoded and hashed without a copy, and the id is the one string made; the hex of the hash and the components are made only for a binding record, not for an own id. Same text, same hash: ids are unchanged. Match seeds an edit's before and after state with two calls, not an array; the old view indexes the patch's relationships by their ends at the first walk that needs them; a walk reads the edges at an entity directly instead of decoding each id and looking it up again, and does not copy them (the graph does not change during a call). A Series needs, per window the step changed, each read's value before the step, which is its first change: a map from window to a slot and two flat arrays. Calls do not overlap, so one such working state per thread serves every Series, sized by the largest step. Map's output is a view of its input's rows with its own columns, one per operator, refilled each call and valid until it runs again (the rule of `EmptyOutput`, #39).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 26.7 / 21.0 KB | 26.2 / 20.6 KB |
| operators | 14.2 / 8.5 KB | 13.6 / 8.0 KB |
| `MatchOp` | 372 / 239 MB | 3,678 / 2,346 MB |
| `EmitEntityOp` | 207 / 83 MB | 2,125 / 878 MB |
| `EmitRelOp` | 157 / 51 MB | 1,578 / 518 MB |
| `SeriesOp` | 122 / 43 MB | 1,162 / 413 MB |
| `LinkOp` | 38 / 23 MB | 361 / 211 MB |
| live bytes | 252 / 253 MB | 1,547 / 1,553 MB |

- Cost moved: the Series working state stays between calls, 6.6 MB at 10^5 (66 B per master node). Letting it go after a large step made the next large step grow it again: 830 MB more allocation at 10^5.
- What remains in the operators at 10^5, KB per record: `MatchOp` 2.9 and `TraverseOp` 0.9 (elements read from the graph as objects: an id string and decoded properties per read), `EmitEntityOp` 1.1 and `EmitRelOp` 0.6 (mostly the element and lineage layers), `SeriesOp` 0.5, `MapOp` 0.3 (expression scratch).
- Before is the telemetry flush patch. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5. No state is added, the seal format is unchanged.

### Efficiency stage 2, hot graph id tables: an element's id is a code (#115, 2026-11-18)
Theory. Per graph, two id tables (entities, relationships) gave each id a dense handle, by which the columns and adjacency lists are kept. A table held every id the graph had as UTF-8 bytes (28 B for an id), its offset, length and hash (12 B) and an index at a load of at most 1/2 (8 B): about 72 B per id with slack, 426 and 447 B per master node at 10^4. A put needs the handle of an id and of a relationship's endpoints, a read by id the same lookup, a read of an element its id back. None needs the bytes in the graph: every element id of an engine's graph is an old code of the engine's interner, which never frees it, and the pool already keeps its texts there. Now a table keeps per handle the id's code in the graph's text table (4 B) and an index from code to handle at a load of at most 1/2 (8 B). A lookup by id finds the text's code without adding it (`TryFind`, the one method the text table gains), then probes an integer. Handles are still kept for the life of the graph; the churn of ids is small (at 10^4, 640 of 11,281 relationship ids of treeStructure are not live).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 252 / 246 MB | 1,546 / 1,486 MB |
| per master node | 25.2 / 24.6 KB | 15.5 / 14.9 KB |
| entity ids per master node | 426 / 114 B | 418 / 106 B |
| relationship ids per master node | 447 / 105 B | 388 / 99 B |
| allocated per input record | 58.2 / 57.0 KB | 57.5 / 56.5 KB |

- Cost moved: a read by id asks the interner first (a hash of the characters and a compare with its bytes), and the id of a handle is the interner's string (from its cache, else made once), where the id table decoded UTF-8 on every read: allocation per record falls by 1.2 KB at 10^4 and 1.1 KB at 10^5. The thread's CPU time at 10^5 rises by about 2 % (main 60.9 and 62.0 s, after 62.9 and 63.5 s, two runs each, alternated, load 49 to 133), most of it in the advance (window closes, derived commits), where reads by id now go through the engine-wide interner instead of a table per graph.
- The interner's live texts do not change (127,466 at 10^4, 1,212,925 at 10^5).
- Before is main at 4c58129 (with the interner, telemetry and pool patches). Times are measured under load and not comparable; bytes are. Digests unchanged. The seal format is unchanged: a seal writes a graph as its elements.

### Efficiency stage 2, a graph compares an element in codes (#115, 2026-11-18)
`Graph.Holds(id, type, source, target, props)` says whether the graph has exactly this element, all given as interner codes and the props as a block from `PropBlockWriter` (the one writer of the pool's encoding). True means equal; false means different or not decidable in codes (a decimal of another scale), and the caller compares elements as before. It makes no string and no element, so the element flush can skip the elements a flush does not change (at 10^4, 130,482 of 262,881 touched elements). For this, an element's type and its property keys are interner codes too, as its texts already were; the two string tables per graph go. Cost: a key is a 2 to 3 byte code instead of a 1 byte number of the graph's own, so the pool grows from 240 to 276 B per master node at 10^4 (live 246 to 247 MB). Digest and the interner's live texts unchanged at 10^4. The flush itself is changed in its own patch.

### Efficiency stage 2, sink kinds and touched elements: a bit per code (#115, 2026-11-18)
Theory. Per graph, the element layer of the sink kept a byte per interner code for an element's kind (entity or relationship, from its first fragment) and another for whether it was touched since the last flush, in segments of 65,536 codes. Both are indexed by code, and the codes of one graph's elements are spread over the engine's whole code range, so every graph allocated about a byte and an eighth per code of the engine: 153 B per master node each at 10^5, for 1.21 M live codes. A change needs, per element, whether the sink has seen it, whether it is a relationship and whether it is in the list of touched elements: three yes-or-no answers. Now they are three sets of one bit per code (`Bits`, in segments of 65,536 bits): the elements seen, the relationships among them, and the touched ones (the list of touched elements keeps their order). Nothing else changes. Seal format 10 to 11.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 247 / 244 MB | 1,488 / 1,463 MB |
| per master node | 24.7 / 24.4 KB | 14.9 / 14.6 KB |
| kinds and touched per master node | 354 / about 60 B | 306 / about 51 B |
| allocated per input record | 56.9 / 56.8 KB | 56.3 / 56.3 KB |

- After: the seen and the touched sets take 20 B per master node each at 10^4 and 17 B at 10^5; the relationships take at most as much (below the census's 40 largest structures).
- Before is main at 4c58129 with the id table and `Graph.Holds` patches. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`); the interner's live texts unchanged (127,466 and 1,212,925).

### Efficiency stage 2, lineage: a handle is a 4-byte code (#115, 2026-11-18)
Theory. Lineage keeps the rows that hold an element up: per emit (element, group key, a handle per element column), per `Group` (group key, member handles), each a Z-set arranged by element or by key. Every column took 8 bytes, with a word of absent bits per row. An answer needs, per element, its rows and the handles in them. A handle is an interner code, never negative, so it takes 4 bytes and marks its own absence as -1, and the absent bits go, as in the joins. The element is a handle too. A group key part keeps 8 bytes: it may be an integer. The weight, the index on the whole row and the arrangement stay. Seal format 11 to 12.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 244 / 241 MB | 1,463 / 1,442 MB |
| per master node | 24.4 / 24.1 KB | 14.6 / 14.4 KB |
| lineage of emits per master node | 614 / 412 B | 461 / 302 B |
| lineage of groups per master node | 250 / 191 B | 203 / 155 B |
| allocated per input record | 56.8 / 56.8 KB | 56.3 / 56.2 KB |

- Before is the kinds patch. Digests and the interner's live texts unchanged, as for the kinds.

### Efficiency stage 2, allocation per record: the flush of the elements (#115, 2026-11-18)
At 10^4 a flush resolved 262,881 touched elements and gave 132,399 edits: about half the touched elements resolve to what the graph already holds. For each, the flush made the id string, decoded every fragment into strings and values, built lists, a dictionary, the props and an element, and compared it with an element the graph materialized.

Theory. The sink's fragments and the graph's prop blocks are canonical forms in codes of the one interner: a text and an entity id are the same code, a key is its name's code, props are in the order of the key texts. When the positive create fragments of an element agree on type and endpoints and no key gets two values, its resolved state is a function of those codes. So whether it changed is decided in codes: the merged props are written as a block and the graph is asked whether it holds that element (`Graph.Holds`). Equal bytes are equal values; unequal bytes may still be equal values (a decimal stored at another scale), and then the full resolution decides. An element the graph holds gives no edit and is skipped, so the other edits keep their order. The same merge in codes builds the element of an edit: one string per id, type, endpoint and key, the values read once. Fragments that disagree, a key with two values or a big integer take the old resolution, which notes the conflict. What the old path did besides the edit is kept: an element or property conflict is cleared only while one exists (the conflict set counts them), and a relationship takes the shortcut only while no tuple is contested in the flush (an element missing from the flush's states counts as not moved, as an unchanged one did). A graph made without the engine's interner has a text table of its own and does not take the shortcut (`Graph.Uses`); the engine's graphs keep the interner through a seal and restore (a snapshot loads into the graphs of the fresh engine).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 19.2 / 12.9 KB | 19.1 / 12.5 KB |
| flush of the elements | 7.5 / 1.2 KB | 7.9 / 1.3 KB |
| flush of the elements, MB | 612 / 100 | 6,482 / 1,052 |
| live bytes | 245 / 245 MB | 1,470 / 1,470 MB |

- At 10^4, 124,714 of the 130,482 touched elements with no edit take the shortcut; the other 5,768 go through the full resolution and give no edit (not split further: an element that resolves to nothing and that the graph lacks is one such case). No element or property conflict and no contested tuple occur in the bench.
- `PropBlockWriter.Decimal` writes a decimal without an array.
- Before is main 416b561 with the operators, id tables, sink bitmaps and `Graph.Holds` patches. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5. No state is added, the seal format is unchanged.

### Efficiency stage 2, sink: the endpoints of a relationship come from its graph (#115, 2026-11-18)
Theory. A merge of two identities moves every relationship whose fragments name the loser. The sink kept a row (endpoint, relationship) for each endpoint a fragment names, about one row per relationship endpoint: 116,524 rows at 10^4, 43 B each with the weight, the index and the arrangement. The graph of the sink already links each relationship it holds to both its endpoints, and only the sink's flush writes that graph. So the row is needed only where the graph does not hold the relationship at that endpoint: held back by another with the same (source, target, type), only updated, its create taken back, or not flushed yet. A row of an emit still adds the endpoint row; the flush drops it for each relationship the graph will hold there and restores it for one the graph will not. A merge reads the graph's adjacency at the loser and the rows left. Seal format 12 to 13.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 242 / 239 MB | 1,449 / 1,419 MB |
| endpoint rows per master node | 497 / 139 B | 422 / 120 B |
| allocated per input record | 12.87 / 12.85 KB | 12.44 / 12.44 KB |

- What stays is mostly the capacity the table reached within a step: its columns do not shrink when rows go.
- Cost: the flush reads each relationship's fragments once more, and a new relationship's endpoint rows are written and removed in one step. At 10^5 the flush of the elements took 3.8 s before and 4.4 s after (one run each, load about 30), allocating 1,052 and 1,090 MB; the engine ingest thread CPU was 32.2 and 31.6 s.
- Before is main 416b561 with the operators, id tables, `Graph.Holds`, sink bitmaps, lineage and element flush patches. Digests and the interner's live texts unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`; 127,466 and 1,212,925).

### Efficiency stage 2, sink: an element with one create is kept by its graph (#115, 2026-11-18)
Theory. At 10^4, 68 % of the elements a sink knows have one row, a create, and the graph holds exactly what it says: Resolve of one fragment is that fragment, and only the sink's flush writes its graph. The sink kept that row (49 B), its fragment (61 B plus about 18 B of bytes) and its weight. Now it keeps the sink and the weight in a table of such elements (`ones`); the graph has the fragment. An element enters at the flush after the one that wrote it, when the graph's element written back as a fragment is byte for byte the stored one (a big integer never is). A row that arrives for it is kept as usual beside it; a retraction and the same row again cancel there and leave the element as it is. Before anything reads its rows (a flush that resolves it, a contested (source, target, type) that may drop it, a merge) its fragment is written back from the graph into the rows. Emptying a store of values or a column that is already at its smallest size now clears it in place: a sink whose elements all left the rows emptied its values at every re-emission and allocated a new 64 KB page each time. Seal format 13 to 14.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 239 / 236 MB | 1,419 / 1,384 MB |
| contributions, values and `ones` per master node | 1,625 / 1,387 B | 1,483 / 1,109 B |
| allocated per input record | 12.85 / 12.84 KB | 12.43 / 12.51 KB |

- The rows and values do not shrink in proportion: their columns keep the capacity the largest step reached, before its elements entered `ones`. `ones` takes 253 B per master node at 10^5.
- Cost: an element enters with one more read of the graph, and leaves with another one when it changes. At 10^5 the flush of the elements took 4.7 s before and 7.6 to 7.8 s after (load 30 to 46).
- Later, the entry check could go through `Graph.Holds`: the fragment written as a block (`Copy`, as the flush's shortcut does) compared in place, instead of the graph's element written back as a fragment and compared. A flush that takes the shortcut has already made that comparison, so its element could enter at once, without waiting for the next flush. That needs the round trip block → canonical → block to be the identity, shown by a test over every kind of value. The read back on change stays: it is the fragment itself that is missing then.
- Before is the endpoints patch. Digests and the interner's live texts unchanged, as for the endpoints.

### Efficiency stage 2, joins: an input read by several joins is stored once (#115, 2026-11-18)
Theory. A join keeps the rows of both its inputs, arranged by its key. At 10^5 the 84 joins kept 140.8 MB of rows, and 41.7 MB of it was an input that another join of the same graph kept too (one input of the workload structure was kept five times). A join whose key parts are columns of its input stores just the input's rows, so the joins of a graph that read one input now share one table of its rows, with one arrangement per distinct key, and one `DecimalCodes` per graph. A step joins each batch with the other side's rows as they were before the step, and the two batches with each other; then the last reader of each input in plan order adds the batch. The result does not depend on where in the plan the readers are. Before, a join added its left batch before joining its right batch, which a second reader of a shared side cannot do. Seal format 14 to 15.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 236 / 233 MB | 1,384 / 1,346 MB |
| joins per master node | 2,307 / 1,967 B | 1,795 / 1,410 B |
| allocated per input record | 12.85 / 12.78 KB | 12.51 / 12.42 KB |
| joins, ms and MB allocated at 10^5 | | 1,302 and 326 / 1,113 and 252 |

- A self-join in the projection language reads its input on one side and a renamed copy on the other, so one table is never both sides of one join. The step handles that case all the same.
- Before is the one-create elements patch. Digests and the interner's live texts unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`; 127,466 and 1,212,925). Load average 80 to 120 during the runs.

### Efficiency stage 2, allocation per record: Match (#115, 2026-11-18)
At 10^4, Match read 210,337 entities from the graph, each made with all its properties, and walked 122,574 relationships, each made with its id, ends, type and properties, of which 19,682 (16 %) fit the pattern's edge type. Every call also made two views and an array of bindings.

Theory. A walk from a bound entity takes the relationships at it whose type is a subtype of the edge's type. The type is a column of the graph, so the walk tests it first and makes only the relationships that fit: the same candidates in the same order. A node bound from the graph is used for its id, its type and the properties the pattern reads. So it binds by id and type (`Graph.EntityType`), and a read takes its one property from the graph when the row is emitted (`Graph.EntityProp`, which decodes that value only). A node the old view has from the patch is the entity the patch made, as before. Calls do not overlap, so the views and the bindings are one working state per thread, refilled by each call and holding no graph, patch or element between calls. Same bindings, same rows.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 12.9 / 11.7 KB | 12.4 / 11.2 KB |
| Match | 166 / 73 MB | 1,750 / 705 MB |
| live bytes | 242 / 242 MB | 1,449 / 1,449 MB |

- What remains in Match at 10^4: the walks' enumerators and the relationships that fit, the props of a relationship the pattern reads (made with the relationship), the rows emitted.
- `OutEdges` and `InEdges` take the type test (Match is their only caller).
- Before is main 9f822ab with the sink and element flush patches. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5. No state is added (the working state is per thread, not sealed); the seal format is unchanged.

### Efficiency stage 2, allocation per record: the flush of the telemetry, its working state (#115, 2026-11-18)
At 10^4 a flush of the telemetry (44,666 of them) made a dictionary of the step's windows, a list of them and a list of samples each time: 40.5 MB for the windows and 59.8 MB for the growth of the sample list, of 113 MB. A step writes about one row per window (199,232 rows, 199,082 windows).

Theory. The windows of a step and its samples are needed only while the step is flushed and until its caller has appended the samples to the store, which it does before the next flush on the thread. Flushes do not overlap, so one working state per thread serves every flush: the windows by key, their rows as a chain, the samples. It is cleared, not made, at each flush, and keeps the size of the largest step. Same windows in the same order, same samples.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 12.9 / 11.7 KB | 12.4 / 11.4 KB |
| flush of the telemetry | 113 / 18 MB | 1,038 / 173 MB |
| live bytes | 242 / 245 MB | 1,449 / 1,473 MB |

- Cost moved: the working state stays between flushes, 23 MB at 10^5 (235 B per master node: the samples 13.6 MB, the windows 9.2 MB), sized by the largest steps (the advances). Letting it go after a flush of more than 16,384 windows gave back the allocation: 1,024 MB at 10^5.
- What remains: the settle of a window written by more than one row (its support kept as a dictionary) and the conflict subjects of shared or moved windows.
- Before is main 9f822ab with the sink and element flush patches. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5. The working state is per thread and not sealed; the seal format is unchanged.

### Efficiency stage 2, allocation per record: minting ids (#115, 2026-11-18)
At 10^4 the emits' identities allocated 43.8 MB, of which 36.4 MB for minting 33,092 new keys (1.1 KB each); a relationship id was made as a string before it was interned.

Theory. A binding record needs the hash as hex, the id and the components as JSON; the components are built in the thread's key buffer (the hash of the key is taken by then), so only the final text is made. An own id and a relationship id are needed as interner codes only: their characters are written into the stack and interned from there (`Interner.Intern` of a span finds a known text without a string). Same texts, same hashes: ids unchanged.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 12.9 / 12.6 KB | 12.4 / 12.2 KB |
| `EmitEntityOp` | 79 / 65 MB | 848 / 706 MB |
| `EmitRelOp` | 47 / 42 MB | 493 / 441 MB |
| live bytes | 242 / 242 MB | 1,449 / 1,449 MB |

- What remains per minted key, about 700 B: the binding record (hash as hex 152 B, the id, the components as JSON, the record) and the growth of the key index. The default store (`HeadOnlyGraphStore`) does not keep binding records, so a record made only when a store keeps it would save about 400 B more per key; that changes the store interface and is not part of this change.
- In `EmitEntityOp` at 10^4 after this: identity 29.7 MB, contributions to the element layer 21 MB, lineage 6.2 MB, set expressions 5.4 MB.
- Before is main 9f822ab with the sink and element flush patches. Times are measured under load and not comparable; bytes are. Digests unchanged at 10^4 and 10^5. No state is added; the seal format is unchanged.

### Efficiency stage 2, allocation per record: Traverse (#115, 2026-11-18)
At 10^4 a walk made every entity it reached with all its properties (74,101 entities, 21 MB), and every reach before the patch was made the same way (6.2 MB), to compare the two and to emit rows.

Theory. A walk compares what a start reached before and after this step's patch. An entity the patch did not edit is the same before and after, so it gives a row the same values and needs no comparison and no object: it is held by its id code, and a row emitted for it reads its properties from the graph (`Graph.EntityProp`). Only the entities the patch edited are made, before and after, as before. Same reach, same rows.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 11.7 / 11.6 KB | 11.2 / 11.0 KB |
| `TraverseOp` | 69 / 57 MB | 617 / 499 MB |
| `TraverseOp` time, ms (two runs each, alternated) | 643, 575 / 419, 385 | |
| live bytes | 242 / 242 MB | 1,449 / 1,449 MB |

- What remains at 10^4: the walk (the neighbours' ids as strings, 7.6 MB), the tables of stored rows and depths, the rows emitted.
- Before is main 9f822ab with the sink, element flush and Match patches (this builds on `Graph.EntityProp`). Times are measured under load; the alternated runs are the comparison. Digests unchanged at 10^4 and 10^5. No state is added; the seal format is unchanged.
### Efficiency stage 2, traverse: a stored row holds codes in 4 bytes, a large step's working state does not stay (#115, 2026-11-18)
At 10^5 the two traverses of the health tree (one over Contains, one over Rolls, the same tree) each kept 106,437 input rows of two element columns in 8 bytes per column and an absent word (7.3 MB), 424,820 reach rows (23.0 MB), and working dictionaries at the size of their largest step: a walk from the root of the tree reaches 75,431 elements, so the entities before and after, the visited ids and the frontier stayed at that size between steps (measured with a temporary diagnostic, not in the patch).

Theory. A traverse stores its input rows as a join does: a column of codes (a text, an element, a decimal as its number) takes 4 bytes and is -1 when absent, a number takes 8 bytes and its absence is a bit in a word of 31 bits. The working state is needed only within a call; a structure that grew past 4,096 entries is let go at the end of the call, as the join's index of a large step is. Same rows, same reach, same output. Seal format 15 to 16.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 236 / 234 MB | 1,370 / 1,350 MB |
| traverse per master node | 1,453 / 1,228 B | 998 / 804 B |
| allocated per input record | 10.31 / 10.32 KB | 9.93 / 9.94 KB |
| traverse, ms and MB allocated | 349 and 57 / 301 and 56 | 5,562 and 499 / 3,829 and 509 |

- Cost moved: a step that walks from the root again allocates its working state again, 10 MB more at 10^5 (0.1 %).
- What remains at 10^5: the reach table, 55 B per row for 12 B of data (the weight, the index on the whole key, the arrangements by start and by element), and the operator's output batch, kept at the size of its largest step (the census's 8-byte arrays less the tables' weights: about 22 MB).
- Before is main 7ab0046 with the ends, ones, joins, Match, telemetry flush state and Traverse allocation patches. Digests and the interner's live texts unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`; 127,466 and 1,212,925). Times are measured under load (load average 80 to 150) and not comparable; bytes are.

### Efficiency stage 2, segments: a column grows in 4,096 entries, not 65,536 (#115, 2026-11-18)
At 10^5 the operators' tables and the sinks' tables hold about 10^5 rows each, and every column of a table grew in segments of 65,536 entries: a table of 135,051 rows (the row set of the health tree's rolled-up state) held room for 196,608 in every column, its arrangements and its weights. (Footprints from a temporary diagnostic of the group operators, not in the patch.)

Theory. The segment is the allocation unit of every column (operator state, graph tables, sink tables, the interner). A smaller segment leaves less room unused: at most 4,095 entries per column instead of 65,535. A read is the same shift and mask. A large column has 16 times as many segment arrays (a column of 10^6 entries 245), and each array of 32 KB or less is allocated in the young generation rather than the large object heap. Same values in the same places. Seal format 16 to 17.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 234 / 226 MB | 1,350 / 1,280 MB |
| allocated per input record | 10.32 / 9.88 KB | 9.94 / 9.60 KB |
| groups per master node | 1,837 / 1,739 B | 1,564 / 1,375 B |
| windowed groups per master node | 1,220 / 1,220 B | 930 / 760 B |
| joins per master node | 1,967 / 1,811 B | 1,410 / 1,296 B |
| sinks (contributions, lineage, values, ones, ends) per master node | | 1,686 / 1,515 B |

- GC pause at 10^5 0.69 / 0.77 s.
- Before is the traverse rows patch. Digests and the interner's live texts unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`; 127,466 and 1,212,925). Times are measured under load and not comparable; bytes are.

### Efficiency stage 2, working arrays: a large step's batches and expression columns go to a shared pool after the step (#115, 2026-11-18)
Every operator keeps its output batch and every compiled expression its column from call to call (#39), so a call allocates nothing for a change that fits. They grew to the largest call each one ever saw (the first load, an advance that closes every window) and stayed at that size, though between steps they hold nothing: the engine reads an output only within its step. At 10^5 that was 363 MB of 1,350 MB live, counted with a temporary diagnostic (not in the patch) over every operator's output batch, its expressions' columns and the input columns a reused view still showed.

Theory. The working arrays live for one step. One that grows past its first size (64 rows) while a step runs takes a power-of-two array from a pool on the engine's thread; when the step's operators are done, it is cut back to 64 rows and its array goes back to the pool, and a reused view lets go of the batch it showed. The next steps take their arrays from the pool, so the arrays of a large change are shared by all operators instead of each keeping its own largest call; an array no step took for 256 steps is dropped. A literal's column is filled on every call, since its array may have served another expression since. Same rows, same output; no state and no seal change.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 234 / 200 MB | 1,350 / 1,131 MB |
| operators' working arrays (diagnostic) | 61.0 / 3.2 MB | 362.7 / 3.6 MB |
| allocated per input record | 10.08 / 9.52 KB | 9.69 / 9.41 KB |
| peak resident set | 592 / 526 MB | 2,231 / 2,031 MB |

- The allocation falls because operators share the pool: a step whose batches are larger than those of the step before takes arrays another operator let go instead of growing its own. Bounds tried at 10^4 (live, allocated): a cut past 4,096 rows without a pool 224 and 826 MB; past 1,024 rows without a pool 199 and 975 MB; with the pool, a cut past 64 to 1,024 rows and a drop after 16 to 256 steps 200 to 201 and 778 to 866 MB, after 512 steps 208 and 787 MB, after 1,024 steps 213 and 822 MB. 64 rows and 256 steps was the lowest allocation.
- What the pool holds after the replay is not attributed by the census (coverage 99 to 96 % at 10^5, about 44 MB): the arrays the last 256 steps let go.
- Cost: a cut column is given a new array of 64 rows (24 MB at 10^5 in the step dispatch).
- Before is main fa0be02 with the ones, joins, Match, telemetry flush state, mint, traverse and traverse rows patches. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load (load average 30 to 60) and not comparable; bytes are.

### Efficiency stage 2, binding records: the store receives a binding as codes and makes its texts only if it persists it (#115, 2026-11-18)
Every key the identity catalog binds made its record as text when it was minted: the key's SHA-256 as 64 hex characters, the id as a string before it was interned, the components as JSON, and a record object. At 10^5 that is 371,730 records (330,480 minted keys, 27,525 aliased, 13,725 merges). The default store keeps only the head and dropped them unread; the engine also copied the pending list per master commit.

Theory. A binding is a fact the catalog already holds as codes: a domain, the key's canonical component codes, and the identity's code (for a merge, the loser's and the survivor's). The engine hands the store those codes; the record's texts (domain, key hash, entity, components) are made when a store reads them, with the catalog's own key hash, the function that mints the id. The SQLite, Cosmos and in-memory stores read them as before and write the same rows; the head-only store never reads them. The id is made in a stack buffer and interned from its characters. The store receives the catalog's pending list for the duration of the commit; after it, the list is emptied, and a list grown past 4,096 by a large step is let go. Same records, same output; no seal change.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 9.52 / 9.35 KB | 9.41 / 9.27 KB |
| allocated in the emits, link and store commit | 118 / 103 MB | 1,277 / 1,167 MB |
| live bytes | 200 / 200 MB | 1,131 / 1,131 MB |

- About 380 B less per binding at 10^4 and 300 B at 10^5, after the pending list's regrowth.
- The pending list holds bindings as values (64 B each instead of an 8-byte reference), so it is let go after a step that grew it past 4,096. Measured at 10^5: keeping it costs 3.6 MB live (36 B per master node) and saves 48 MB of allocation (7,518 MB instead of 7,566 MB); letting it go past 65,536 never happens on these captures.
- Before is the working-arrays section's after. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load and not comparable; bytes are.

### Efficiency stage 2, tables: a column of 8 bytes takes 4 per entry until a value needs 8 (#115, 2026-11-18)
At 10^5 the row sets of `arg_max` in the groups held 938,030 rows in 73.5 MB, 78 B per row, and the key rows of the groups 91.9 MB (footprints from a temporary diagnostic of the group operators, not in the patch). The values in them are mostly small integers: the ranks that order an `arg_max`, the counts and severities it keeps, the counts and sums of the key rows. Every Integer took an 8-byte column, and every row an 8-byte weight, mostly 1.

Theory. A table column that may need 8 bytes, and the weight of a row, take 4 bytes per entry until a value needs 8; then the column is copied to 8 bytes once and stays so. A column of 4 bytes (codes) still refuses a value that needs 8. Nothing a caller reads changes: a read returns the same 64-bit value, the index hashes values, not their storage, and a snapshot writes the column in the form it has. This holds for every table (group key rows and row sets, multisets, joins, traverse, sink tables, lineage). A column of instants or decimal halves takes the copy at its first value. Seal format 17 to 18.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 226 / 217 MB | 1,275 / 1,207 MB |
| allocated per input record | 9.63 / 9.46 KB | 9.35 / 9.45 KB |
| groups per master node | 1,739 / 1,470 B | 1,375 / 1,161 B |
| windowed groups per master node | 1,220 / 982 B | 760 / 617 B |
| joins per master node | 1,811 / 1,700 B | 1,296 / 1,208 B |
| traverse per master node | 1,057 / 1,011 B | 767 / 724 B |
| sinks (contributions, lineage, ones, ends) per master node | 1,500 / 1,292 B | 1,190 / 1,026 B |
| row sets of the groups | 9.2 / 7.5 MB | 73.5 / 59.6 MB |
| key rows of the groups | 13.5 / 9.9 MB | 91.9 / 68.4 MB |

- Example: `healthTree/rolledAll`, `arg_max(rank, healthy, degraded, unhealthy, monitored, severity) by key`, 135,051 rows at 10^5: 104 B per row before, 72 B now. `master/sent_3` (windowed, ordered by an instant): 74 B to 69 B.
- Allocation at 10^5 is 1 % higher, at 10^4 2 % lower; not traced further (a column copied to 8 bytes allocates its 8-byte segments once). GC pause at 10^5 0.92 / 0.91 s; peak resident set 2,187 / 2,053 MB.
- Before is the segments patch. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load and not comparable; bytes are.
- The row sets' remaining cost is their structure, not their values: per row a whole-row index entry (8 to 16 B), the arrangement by key (8 B and its index), the key slot and the bits of which values are present. Not changed here.

### Efficiency stage 2, identities: a key holds its component codes in 4 bytes (#115, 2026-11-18)
The identity catalog keeps two maps from a key to an id: the bindings (358,005 keys at 10^5) and the own ids (one per key the telemetry sinks read). A key was its domain's index and four components in 8 bytes each, 40 bytes; an entry of the map 56 bytes.

Theory. A canonical component is a text code of the interner, which is 4 bytes, as the id it maps to is. So a key holds its components in 4 bytes: 20 bytes, 32 bytes per entry. Same keys, same hash and equality over the same codes, same ids; the catalog is not sealed, so no seal change.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 193 / 192 MB | 1,061 / 1,041 MB |
| identities per master node | 355 / 219 B | 501 / 302 B |
| allocated per input record | 8.90 / 8.87 KB | 8.93 / 8.86 KB |

- The census now has a root for the pool of working arrays (thread statics of a generic class, which the statics root missed): 5.3 MB at 10^4 and 32.0 MB at 10^5 (320 B per master node). Coverage at 10^5 is 99 %.
- Before is the binding records section's after, on main 493fbbc with the segments patch. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load and not comparable; bytes are.

### Efficiency stage 2, allocation per record: the working sets of a flush and of a patch check (#115, 2026-11-18)
At 10^4 the flush of the elements and the apply of the patch allocated 111 and 58 MB. Measured in parts with a temporary diagnostic (not in the patch): 18 MB went to the three dictionaries each flush made (what each touched element resolves to, the tuples claimed, the tuples whose claimants changed; 44,666 flushes), and the patch check's set of seen keys and map of added tuples regrew by doubling in every patch (202,367 edits in 12,226 patches). The check's guard, making the graph's element again to compare with the edit's before-state, is not where it goes: only 12,621 edits have a before-state.

Theory. A flush's dictionaries are needed only while the flush runs; flushes of a graph do not overlap, so its layer keeps one set of them, cleared, not made, at each flush, and let go after a flush that grew them past 4,096 (as a traverse's working state). The check's sets hold one entry per edit, and the number of edits is known when the check starts: they are made at that size, so they never regrow. The flush's list of edits is made at the size of what it resolved. Same edits in the same order.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 8.87 / 8.53 KB | 8.86 / 8.51 KB |
| flush of the elements | 111 / 90 MB | 1,230 / 1,025 MB |
| patch and apply | 58 / 51 MB | 599 / 518 MB |
| live bytes | 192 / 192 MB | 1,041 / 1,041 MB |

- What remains in the flush at 10^4: resolving the elements an edit needs (53 MB: the elements, their props and values), the touched relationships' holders (about 23 MB: the id strings, a holder made as a relationship), the before-states read for the edits (17 MB). In the apply, the puts (40 MB: the graph's arrays and props pool growing).
- Before is the identities section's after. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load (load average 40 to 110) and not comparable; bytes are. Nothing is sealed; the seal format is unchanged.

### Efficiency stage 2, groups: the rows of an `arg_max` are reached through their key (#115, 2026-11-18)
Theory. A group keeps, per key and per `arg_max` or `arg_min`, every distinct input row of the key, since the next winner comes from the key's other rows when the winning row goes. A change needs the key's rows: to add its weight to the identical row, and to find the winner, which walks all of them on every change. The rows were a table with an index on the whole row and an arrangement by key with its own index, two hash indexes (8 to 16 B per row each) to find what a walk of the key's one or two rows finds. Now the key's slot holds its first row and each row the next one, and the table of rows has no key columns and so no index (`Table.Insert`, `Table.AddAt`). A change walks the key's rows, as the winner does. Same rows, same winners. Seal format 18 to 19.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 184 / 181 MB | 987 / 963 MB |
| row sets | 7.5 / 4.7 MB (745 / 467 B per master node) | 59.6 / 34.7 MB (596 / 346 B per master node) |
| groups per master node | 927 / 731 B | 738 / 551 B |
| windowed groups per master node | 856 / 774 B | 532 / 470 B |
| allocated per input record | 8.74 / 8.68 KB | 8.84 / 8.67 KB |

- What stays per row: its values, the bits of which members have one, the weight, the next row (4 B), and per key its first row (4 B).
- Before is main 493fbbc with the segments, working arrays, binding records and argmax tables patches. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load and not comparable; bytes are.

### Efficiency stage 2, groups: a key's row keeps no flag and no count that the rest of it gives (#115, 2026-11-18)
Theory. A group's key row held a flag of whether the key has a row in the output, and a `count()` its own accumulator. Both are functions of the key's count: a group has a row in the output iff its count was positive at the end of the last step, a windowed group iff its window is closed and its count positive, a roll-up iff its count is positive and no marker record of no data takes its place; and `count()` is the count. A step reads the flag before it changes the count, so the count still gives it. Now neither is stored. Same output. Seal format 19 to 20.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 181 / 179 MB | 963 / 952 MB |
| key rows | 9.9 / 8.3 MB (990 / 826 B per master node) | 68.4 / 57.5 MB (684 / 574 B per master node) |
| groups per master node | 731 / 682 B | 551 / 511 B |
| windowed groups per master node | 774 / 666 B | 470 / 406 B |
| roll-ups per master node | 131 / 120 B | 82 / 75 B |
| allocated per input record | 8.68 / 8.61 KB | 8.67 / 8.65 KB |

- Accumulators of `last` are reserved but never written, and a column never written takes no memory: nothing to gain there.
- Before is the row sets patch. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`).

### Efficiency stage 2, allocation per record: the window changes of a step and its commit report (#115, 2026-11-18)
At 10^4 reading what a step changed in the telemetry allocated 41 MB in 44,666 steps, and the commit report 12 MB, 270 B per step, nearly all of them steps without a changed conflict. A window change was an object of 80 B behind a reference in a list that grew by doubling, each step started a new log, and each step ordered its empty set of changed conflicts into a new list.

Theory. A window change is a value (a key and two optional numbers), so it is a struct of 64 B held in the log itself. An append makes room for its whole batch at once, so a step's log is made at its size, not grown by doubling. The plane's one reader keeps what it reads (the graph delta, the commit event, an export), so a read takes the log; a step with no change reads the shared empty list and leaves the log in place. A step with no changed conflict reports the shared empty list. Same changes in the same order.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 8.27 / 8.02 KB | 8.24 / 8.00 KB |
| telemetry append and read | 41 / 32 MB | 401 / 313 MB |
| commit event | 12 / 2 MB | 120 / 18 MB |
| live bytes | 179 / 179 MB | 937 / 937 MB |

- Tried and dropped: one log per plane, kept and cleared, each read copied out as an array of its count. It saved 16 MB, not 20, and the log kept the size of the largest step (1.8 MB live at 10^4). Letting such a log go past 4,096 entries made the advance steps regrow it, 13 MB more than before.
- Before is the groups key rows section's after (seal 20). Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Bytes only: the runs were measured under load (load average 37 to 56), so no times are given. Nothing is sealed; the seal format is unchanged.

### Efficiency stage 2, allocation per record: the texts and properties of a resolved element (#115, 2026-11-18)
At 10^4 the flush resolved elements for 53 MB. Two copies in it carried nothing: each text read from a fragment or a graph block became a new `TextValue` around a string the interner had already made (18 MB of them at 10^4, each scanned again for lone surrogates), and the properties, merged in key order, were copied once more by `Props.Of` to be sorted (26 MB of property arrays in all).

Theory. A text value is a function of its code. While the interner caches a code's string it keeps that string's value in the same slot and gives it out again (`Interner.Text`; a graph's own text table makes a new one, by the interface's default). A slot that takes another code, or a freed code, lets go of the value, so the values held are at most the cache's 16,384. Resolve hands the merged properties, which are in the order of their key texts, to `Props.FromSorted`, now public, which checks the order and takes the array without a copy. Same elements and values.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per input record | 8.02 / 7.72 KB | 8.00 / 7.70 KB |
| flush of the elements | 90 / 74 MB | 928 / 770 MB |
| `TextValue` (sampled by type) | 20 / 5 MB | 231 / 52 MB |
| property arrays (sampled by type) | 30 / 19 MB | 289 / 198 MB |
| live interner | 9.1 / 9.3 MB | 73.9 / 74.0 MB |
| live bytes | 179 / 179 MB | 937 / 938 MB |

- Not changed: 41 MB of strings at 10^4 are misses of the interner's string cache (flush 10.7 MB, Match 6.2, Emit 9.9, Traverse 5.6). A cache of 65,536 halves them for 5 MB more live at 10^4. That is a trade of live bytes for allocation; the cause is the round trip from code to string and back (Resolve makes elements of strings, `Graph.Put` interns them again).
- Before is the previous section's after. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Bytes only, measured under load. Nothing is sealed; the seal format is unchanged.

### The second run at 10^6, after efficiency stage 2 (#115, 2026-11-18)
Release build of fd42618, the same input as the first run (8,159,533 records, 1,000,006 master nodes), `sattle bench phases --capture DIR`, then again with `--census`. Machine as before; load average 58 to 94 during both runs, and other sessions' builds overlapped the first minutes of the first run, so times are pessimistic; bytes are not affected. Limits of Ch. 13, 13.1.

| | first run (f9e3121) | second run, timed | second run, census | limit |
|---|---|---|---|---|
| live after a compacting collection | 19,955 MB, 19.95 KB per master node | | 9,035 MB, 9.04 KB per master node | 8 GB |
| peak resident set | 15.3 / 16.1 GB | 9.7 GB | 11.1 GB (12.8 GB with the census walk) | 12 GB |
| wall | 677 / 590 s | 563 s | 533 s | |
| process CPU per record | 91 / 87 µs | 67 µs | 65 µs | 37 µs |
| allocated per record (engine, adapters and parsing) | 58.6 KB (about 11 KB the bench's own) | 8.3 KB | 8.3 KB | proportional to changes |
| GC pause | 5 / 4 % | 1 % | 1 % | 5 % |
| 12 window advances | 219 / 170 s | 190 s | 177 s | 5 s each (p99), #119 |

- Live memory is 13 % over the limit; peak, GC pause and allocation are within; CPU per record is about 1.8 times the limit. A Graph names memo (a CPU regression of 12 to 15 % from 9f822ab) and the choice of GC mode (server GC took about twice the CPU of workstation GC on a smaller run) are the next steps for CPU.
- Retained bytes per master node by structure (census, 15.8 M objects, coverage 100 %, walk 11 s): telemetry store 1,031; joins 937; interner 781; traverse 600; groups 548; sink contributions 480; statics 390 (a 218 MB sample array); windowed groups 364; sink values 325; thresholds 311 (4.7 M tree nodes); graph byte pool 302.
- Raw outputs: `tmp/orchestrator/n1m-b-phases.txt` and `n1m-b-census.txt` (not in git).

### Window close: the time from a window's close to committed health state (#119, 2026-11-18)
`sattle bench phases --capture DIR` now times each window close: from the first progress record the replay sends before an advance to the end of the advance, when every graph has committed (the replay sends progress only there). Per close it prints the frontier, wall, thread CPU, allocation, collection pauses, the telemetry window changes the graphs committed, the series held after it and the four graphs that took longest; then p50, p99 and max over all closes and over the closes after the first. It also prints the graph table and the state digest on every run, not only with `--census`. The generated estates (`bench gen-estate`) run 12 closes: two before any telemetry, the first close with telemetry, nine steady.

| | 10^4 | 10^5 |
|---|---|---|
| series held at the end | 20,023 | 199,964 |
| window changes per steady close | 19,878 to 19,914 | 198,623 to 198,960 |
| steady close, wall p50 / max | 73 / 95 ms | 668 / 900 ms |
| steady close, thread CPU median | 70 ms | 622 ms |
| first close with telemetry, wall (thread CPU) | 564 (462) ms | 7,841 (6,390) ms |

- The first close with telemetry is a load: the derived graphs create their structure from the first windows (at 10^5 workloadStructure 1.7 s, master 1.5 s, healthTree 1.3 s, treeSignals 0.9 s) and it allocates 1.2 GB. A live space has it once; a restart from a seal does not repeat it. The steady closes are what the limit of 13.1 reads.
- Linear in the series: about 6.2 s per steady close at 10^6 from these figures, over the 5 s limit.
- Load average 75 to 90 on 10 cores during these runs; wall is noisy, thread CPU less so. Digests `sha256:2654c25f...bfbc3` (10^4), `sha256:0ab80689...41140` (10^5).

### Window close: a closed window's row for an element its graph keeps touches nothing (#119, 2026-11-18)
Theory. A windowed group never retracts a closed window: its state goes when the window passes the allowed lateness, with no row. So the support of an element its emit creates is the number of windows that wrote it, and every close adds one row per series to an element that exists since the key's first window. For an element of `ones` that row cost a write-back of the fragment from the graph, a resolve, the comparison with the graph, a settle of its endpoints, and at the next flush a second read of the graph to collapse it again, at every close, while the graph did not change (the patches of the steady closes take 5 ms in all at 10^5). An element resolves from its positive fragments only (Def. 6.4). A row whose fragment is the one the graph holds for an element of `ones`, with a weight that leaves the element's weight positive, changes none of them: its weight goes to `ones`, and the element is not touched. The check reads the graph the last flush wrote, as the entry to `ones` does. Any other row is kept and touches as before. Same edits, same elements. Seal format 20 to 21 (the layer keeps that graph and a buffer).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| steady close, thread CPU median | 70 / 55 ms | 622 / 445 ms |
| steady close, wall p50 / max | 73 / 95, 56 / 91 ms | 668 / 900, 506 / 591 ms |
| first close with telemetry, wall (thread CPU) | 564 (462) / 473 (433) ms | 7,841 (6,390) / 7,105 (6,161) ms |
| this thread, whole replay | | 43.0 / 39.5 s |
| allocated per input record | 7.87 / 7.84 KB | 7.72 / 7.69 KB |

- At 10^5 before, the flush of the elements took 193 ms of each steady close and the emits 235 ms.
- Linear to 10^6: about 4.5 s per steady close, under the 5 s limit with little room.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Load 75 to 90.

### Efficiency stage 2, statics: a large flush's telemetry working state does not stay (#115, 2026-11-18)
At 10^6 the statics held 390 B per master node, 218 MB of it one `Sample[]`: the working state of the telemetry flush on the engine's thread (the windows of the step by key, their order, and the samples) was kept at the size of the largest step, the first window close of the run. At 10^5 it was 23 of the 30 MB of statics.

Theory. A flush past 4,096 windows or samples lets the thread's working state go; the step that called it still holds the samples until it has appended them, and the next flush starts small. A flush of a large step sizes its working state once from its row count instead of growing it by doubling. Same samples in the same order. The seal format is unchanged (the state is the thread's, not the engine's).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 179 / 177 MB | 934 / 910 MB |
| statics per master node | 409 / 193 B | 303 / 69 B |
| allocated per input record | 7.73 / 8.07 KB | 7.70 / 8.07 KB |
| flush telemetry, MB allocated | 18 / 47 | 173 / 475 |

- Cost moved: every window close of a large estate allocates its flush state again, 4.5 % (10^4) and 4.8 % (10^5) more allocation per record; GC pause 0.59 / 0.49 s at 10^5. Without the sizing once it was 13.5 % (8.75 KB per record at 10^5).
- Before is main c57264e. Digests unchanged (estate `sha256:32bcad1f...a422`, lab fixtures `sha256:4a1560a5...ffd0`, 10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load (load average 100 to 300) and not comparable; bytes are.

### Efficiency stage 2, telemetry store: a window's start in 4 bytes and its value in 8 (#115, 2026-11-18)
At 10^5 the telemetry store held 1,030 B per master node: 199,964 series of about 16 windows each, every start a long (8 B) and every value a decimal (16 B), in arrays grown by doubling.

Theory. A series keeps a window start as an int number of window widths from the first start the series had, and a value as its mantissa and scale in one long (the mantissa below 2^58). A start off that grid or more than 2^31 widths away turns the series' starts to longs once, a value that does not pack (a wider mantissa, a negative zero) its values to decimals once, so every start and value reads back exactly as written, sign and scale too. Nothing a caller reads changes: the same windows in the same order. Seal format 20 to 21 (a snapshot writes the series in the form it has).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 177 / 174 MB | 910 / 878 MB |
| telemetry per master node | 1,041 / 714 B | 1,030 / 703 B |
| allocated per input record | 8.07 / 8.01 KB | 8.07 / 8.00 KB |
| peak resident set | 515 / 455 MB | 1,880 / 1,798 MB |

- What remains at 10^5: values 29 MB, starts 19 MB, and the 199,964 series objects 14 MB (72 B each); the arrays carry the slack of growth by doubling.
- A series of hop windows (starts on the hop, not on the width) keeps plain starts.
- Before is the statics patch on main c57264e. Digests unchanged (estate, lab fixtures, 10^4 and 10^5 as in the statics section). Times are measured under load and not comparable; bytes are.

### Window close: a Series reads a changed window from the step's changes (#119, 2026-11-18)
Theory. Step B of a Series joins the windows its read graph changed in this step against its rows: per changed window it needs the values of its reads before the step and after it. The change of a read carries both (`Old`, `New`), and the last change of a read in the step holds what the store holds now. So when the step changed every read of the window, the steady case (a closed window brings each read once), the values after the step are the step's own changes, and the store read per changed window (a lookup under the store's lock, the element array, the visit, the point array) is not needed. A window with a read the step did not change still reads the store. The values before and after are kept in two buffers per thread that the next call overwrites, not in new arrays. Same rows, same order.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per steady close | 8.2 / 7.0 MB | 98.1 / 84.4 MB |
| SeriesOp, allocated in the nine steady closes | 18 / 5 MB | |

- Thread CPU is not separable from the load (250 to 460 on 10 cores during these runs); the three changes of this day together are in the last section.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: an identity component that keeps its text is its own canonical code (#119, 2026-11-18)
Theory. An emit evaluates the identity of every row: per component the canonical code, then the catalog's handle and own id. The canonical code of a component was cached per operator in 4,096 slots, so at 10^4 and above nearly every series missed it each close and read its text, canonicalized it and interned it again (about 1 µs a miss under load). The interner gives a text one code while the code is old (kept), so under a canonicalizer that leaves every text as it is (`Exact`, and the default), the canonical code of an old code is that code. It is taken as it is, with no read of the text, no intern and no cache. A young code (a text new in this batch, which the interner may free) goes the old way, which also makes it old.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per steady close | 7.0 / 6.8 MB | 84.4 / 67.2 MB |
| component codes taken as they are, nine steady closes | | 3,042,805 |
| misses left (`LowerInvariant`), nine steady closes | 11,422 | 196,901 |

- At 10^4 before, every miss was a code whose canonical code is itself (61,010 of 435,541 reads), 81 % of them under `Exact`.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: an advance reads the store for leaving windows only once the look-back reaches a written one (#119, 2026-11-18)
Theory. At an advance a Series retracts its records of the windows that leave the look-back, [old edge, new edge). It read the store for every element it holds a row of, to find them. A Series retracts only records it wrote, and every record goes through one write, so it keeps the earliest window start it wrote: while the new edge is at or before it, no written window leaves, and the advance reads nothing. In the generated estates no window leaves the look-back (30 minutes and one day, against 12 minutes of capture), so every advance read the store once per element for nothing: 638,334 reads in the nine steady closes at 10^5. In a long steady run a 30-minute Series reads again once the look-back reaches its first window, as before. Seal format 21 to 22 (the Series keeps that start).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| allocated per steady close | 6.8 / 6.0 MB | 67.2 / 61.4 MB |

The three changes of this day together, against the previous section's after (the `ones` row):

| | 10^4 before / after | 10^5 before / after, two pairs |
|---|---|---|
| steady close, thread CPU median | 60 / 56 ms | 462 / 334 ms, 549 / 425 ms |
| steady close, wall p50 / max | 62 / 70, 57 / 77 ms | 570 / 1,694, 364 / 511 ms |
| allocated per steady close | 8.2 / 6.0 MB | 98.1 / 61.4 MB |
| first close with telemetry, thread CPU | 469 / 468 ms | 6,697 / 6,341 ms |

- Thread CPU falls 23 to 28 % at 10^5. Linear to 10^6 from 334 to 425 ms: 4.6 to 5.9 s per steady close. The machine ran at load 250 to 460 on 10 cores (other sessions' test runs), so the figures are pessimistic; a quiet run decides it.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`).

### Efficiency stage 2, interner: an element id is stored in 19 bytes, a text's place in 4 (#115, 2026-11-18)
At 10^5 the interner held 748 B per master node. Most of its texts are element ids (`e:` or `r:` and 26 base32 characters, 70 % of the texts at 10^4), stored at one byte per character, 28 B; and every code kept its place as an 8-byte page and offset.

Theory. A text with the shape of an id as `KeyText.Id` mints it (a letter, a colon, 26 base32 characters) is stored packed: the letter, then 5 bits per character in three words, 19 B instead of 28. A text has exactly one form (a packed id, one byte per character, or two), so equal texts have equal bytes; the form is part of the hash and of the comparison, and a text has one code as before. The length word holds the stored bytes and the form, and a place is an int, page << 16 | offset. Codes, young and old, what `Get` returns and the seal format are unchanged (a seal writes the interner as its texts).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 179 / 177 MB | 934 / 921 MB |
| interner per master node | 976 / 844 B | 748 / 618 B |
| allocated per input record | | 7.73 / 7.71 KB |

- Cost moved: an intern packs an id before it hashes, and `Get` of a packed id builds its 28 characters from the three words. On the 10^5 replay with workstation GC (instruction counts stable to about 2 %) the process ran 427 and 442 G instructions against 418 to 437 G before, 2 to 6 % more. A first version that packed bit by bit cost 17 %. Thread CPU is not separable from the load (300 to 560 on 10 cores).
- Limit: 65,536 pages of 64 KB, 4 GB of stored text; a text of 64 KB or more is a page of its own, and the interner throws past the last page. At 10^5 the texts take 509 pages (33 MB); linear to 10^6 about 5,100, 8 % of the limit. One text is at most 256 MB (the length word holds its bytes shifted by 3).
- Before is main 5e86f36; the interner is the same in the statics, telemetry and #119 changes this applies on. Digests unchanged (estate, lab fixtures, 10^4, 10^5 `sha256:0ab80689...41140`). Times are measured under load and not comparable; bytes are.

### Efficiency stage 2, plan: a join on the start of a traverse runs below it (#115, 2026-11-18)
At 10^5 the hidden rows of the health tree walked from every one of its 106,437 elements over `Contains` (256 B per master node in that traverse), and a join then kept the walks from the few roots a "hide" curation names (143 B per master node in that join).

Theory. `PlanCompiler.JoinsBelowTraverses`: for start rows S, a traverse T, a map M and rows R, a join on a key that reads only the start rows' columns, Join(M(T(S)), R) on k(s) = k'(r), is M'(T(Join(S, R) on k(s) = k'(r))), where M' is M with R's columns carried through. T emits, per start row and per element its start element reaches, the start row's columns with that element, so a key of the start row pairs the same rows with the same weights before the walk as after it; a start row that joins two rows of R is walked twice, as its reach was joined twice. Incrementally a row of R that comes is a start row that comes. Only the plan the engine runs changes: the definitions, and the reference evaluator of the differential tests, keep the plan as written, and the worlds `join on the start of a traverse` (left and right) check one against the other under every cut. Applied when the traverse and the map have no other reader. A traced preview shows the plan as written; the rewrite applies only to untraced runs (`EngineOptions.TraceGraphs`). Seal format 23 to 24 (the health tree's operators change).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 179 / 175 MB | 934 / 899 MB |
| traverse per master node | 749 / 431 B | 514 / 263 B |
| joins per master node | 1,265 / 1,154 B | 856 / 757 B |
| allocated per input record | 7.74 / 7.49 KB | 7.73 / 7.46 KB |

- The join of the start rows stores the start rows (every element, by its key), so the joins fall by 99 B at 10^5, not by the 143 B of the old join.
- The reach of the rolled-up severity (`counts`) joins on the reached element's key, not on a start column, and is unchanged.
- Before is main 5e86f36. Digests unchanged (estate `sha256:32bcad1f...a422`, lab fixtures `sha256:4a1560a5...ffd0`, 10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...1140`). Times are measured under load (load average 300 to 400) and not comparable; bytes are.

### Efficiency stage 2, joins: a row alone with its value in an arrangement keeps no links (#115, 2026-11-18)
At 10^5 the joins held 757 B per master node, 73 MB: columns 25.0, the index on the whole row 15.7, the links of the arrangements 14.7, the arrangements' indexes 11.1, weights 6.6. An arrangement chains the rows of one value through a next and a previous row number, 8 B per row, and 7.6 MB of those links were in arrangements where every value has one row (the join on a key that names one element, as most joins of the health tree and the workload structure do), so every link was 0.

Theory. A link that was never written reads 0 (none): a new segment is zero, and an unlink clears its row's links. So linking a row that is alone with its value writes nothing; only a row that joins a value with rows writes its next and the previous of the row it goes before. An arrangement whose values are one row each keeps its index and no links. Same rows, same order of a value's rows. Traverse and the sinks use the same arrangement. The seal format is unchanged (a seal writes the rows, not the arrangements).

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| live bytes | 168 / 167 MB | 835 / 826 MB |
| joins per master node | 1,154 / 1,059 B | 757 / 682 B |
| traverse per master node | 431 / 420 B | 263 / 254 B |
| allocated per input record | 7.63 / 7.60 KB | 7.28 / 7.27 KB |

- At 10^5 the links of the joins go from 14.7 to 7.2 MB; what is left is in arrangements with several rows per value (the reach of the health tree's `counts`, 424,820 rows over 106,437 values).
- At 10^5 live is now 8.26 KB per master node. Projected to 10^6 with the ratios of the second 10^6 run (same code at both scales), about 7.8 KB per structure, or 8.0 KB with that run's overall ratio of 0.97.
- Before is main 4afb044 with the interner and the join below a traverse. Digests unchanged (estate `sha256:32bcad1f...a422`, lab fixtures `sha256:4a1560a5...ffd0`, 10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...41140`). Times are measured under load (load average 250 to 380) and not comparable; bytes are.

### Window close: one catalog lookup per row gives the identity and the key's own id (#119, 2026-11-18)
Theory. An emit that writes telemetry evaluates the identity of every row twice in the catalog: the identity through the bindings (then `Rep` through the forwarding records), and the key's own id through a second table by the same key. Both tables hold an entry per key, so each row paid two lookups in a large table (1,965,821 rows in the twelve closes at 10^5; 491 ms for both under load 460, by the sub-timers of the previous section). A binding now holds both: the identity and the key's own id, filled on first use; a key that `Resolve` mints has its own id as its identity. One lookup per row, and `Rep` returns at once while no identities merged. The second table goes: a bound key holds 4 bytes more, a key whose own id is read holds about 36 bytes less.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| steady close, thread CPU median | 42 / 74 ms | 441 / 453 ms |
| allocated per steady close | 8.9 / 8.9 MB | 90.2 / 90.2 MB |

- One pair at load 460 to 530: thread CPU at that load moves by more than the change. The last section of the day has the pairs that decide it.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: a Series visits only the changes of the properties it reads (#119, 2026-11-18)
Theory. Each Series scanned every window change of the graph it reads in a step and kept those of its reads: 7,939,330 changes scanned for 2,566,110 kept in the nine steady closes at 10^5. The graph delta of a step now indexes its changes by property once, on the first read (two integer arrays, 8 bytes per change), and a Series visits only the changes of its reads, merged in the order of the step: the same windows in the same order. The engine made one delta per graph that reads a stepped graph, so the index would have been built once per reader; it now makes one per stepped graph, shared by all its readers.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| steady close, thread CPU median | 74 / 32 ms | 453 / 373 ms |
| allocated per steady close | 8.9 / 9.0 MB | 90.2 / 91.6 MB |

- One pair at load 460 to 600. The allocation is the index: 94.4 MB per close with one delta per reader, 91.6 MB with the delta shared.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: the `ones` check compares a row with the graph's element without copying it (#119, 2026-11-18)
Theory. The check of the `ones` row (a row of an element in `ones` with the fragment the graph holds goes to `ones`, no touch) costs, split by sub-timers at 10^5 (twelve closes, wall under load 541, 1,042,419 checks): the graph's read of the element 768 ms (handle index, element arrays, props block), the lookup in `ones` 429 ms, writing the graph's element in the canonical form about 90 ms, copying the row 126 ms, comparing 48 ms. The lookups are two thirds; the re-encoding suspected before is about 5 %. The copy goes: the row's bytes stay in their buffer while the graph's element is written into a second one (the two swap), so the check copies nothing. The lookups stay, since the row must be compared with what the graph holds; one fewer would need the graph's handle kept in `ones` (4 bytes per element), not done.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| steady close, thread CPU median | 32 / 43 ms | 373 / 372 ms |
| allocated per steady close | 9.1 / 9.2 MB | 94.4 / 94.4 MB |

The three changes of this section and the two before it together, against the advance section's after, at load 375 to 700 on 10 cores:

| | 10^4 before / after | 10^5 before / after, four pairs and one more before |
|---|---|---|
| steady close, thread CPU median | 42 / 47 ms | 441, 351, 366, 350, 374 / 350, 356, 350, 358 ms |
| steady close, thread CPU least | 33 / 44 ms | 407, 346, 336, 330, 364 / 335, 331, 338, 335 ms |
| steady close, wall p50 / max | 151 / 229, 58 / 63 ms | 726 / 1,020, 440 / 687, 510 / 691 against 540 / 582, 396 / 433, 470 / 584 ms |
| allocated per steady close | 8.9 / 9.0 MB | 90.2 / 91.6 MB |
| first close with telemetry, thread CPU | 681 / 455 ms | 6,336 to 7,565 / 6,084 to 6,600 ms |

- Thread CPU at 10^5: median of the medians 366 to 353 ms (4 %), the least close 330 to 407 against 331 to 338 ms. That is within the noise of this load; the sub-timers put the removed work at about 6 % of the thread CPU of the twelve closes. Linear to 10^6 from 353 ms: about 4.9 s. A quiet run decides it.
- The figures of this section (one pair, before without the shared delta) are at load 525 to 690. Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: a new window is learned and written with one store lookup (#119, 2026-11-18)
Theory. Each series gets one new window per close, written by one row. The flush of the telemetry settled it by reading the store (the series, then the window, under the store's lock), found nothing, made a sample and kept it in a list as long as the step's windows; after the graph's commit the engine appended the list, and the append looked up the series and the window again. Split by sub-timers at 10^5 (nine steady closes, wall at load 137, 1.59 M windows): flush 928 ms, of which the index of the step's windows 151 and settling 677, the store read in it 379; then append and read 382 ms. The telemetry path was 27 % of the steady close, the largest part of it.

Now a window that one row of weight one writes, with no support kept, goes to the store in one call that inserts it when the store does not hold it and says so (`AppendNew`); only when the store holds it does the flush read the value and settle as before. The flush writes the samples of each window as it settles, so the plane's change log holds the same changes in the same order; they are now written before the graph's commit instead of after it, and nothing reads the plane in between. The list of samples is gone. A log that fills one window at a time is made at the size of the last read, so a steady close does not grow it by doubling.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| steady close, thread CPU median | 47 / 33 ms | 368, 389 / 371, 344 ms |
| steady close, thread CPU least | 42 / 29 ms | 335, 370 / 347, 330 ms |
| allocated per steady close | 9.0 / 7.2 MB | 91.7 / 73.4 MB |
| telemetry flush and append, MB allocated in twelve closes | 72 / 56 | 728 / 563 |

- Allocation per steady close at 10^5 drops by 18.3 MB (20 %): the samples list (about 105 bytes per window) and the log's growth. Exact, the same in every run.
- Thread CPU is within the noise of load 230 to 330 on 10 cores (one pair at 10^4, two at 10^5, interleaved). Instructions retired of the whole process vary by 4 % between runs of one binary at 10^4, too much to see this. The removed work is one series lookup and one search of its windows per window: at 10^6 about 2 M per close, where those lookups miss the caches.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: a text that is already canonical keeps its code (#119, 2026-11-18)
Theory. An identity component under `LowerInvariant` is canonicalized per row through a cache of 4,096 slots per component. The keys of the series are many more than that, so at 10^5 about 19,000 per steady close miss it, and every miss was a text that is already lower case (175,010 misses in nine closes, all of them the same text after canonicalizing). A miss read the text, lowered it, and interned the result: a hash of the whole text and a comparison, to find the code it started from. When the canonicalizer hands back the text itself (it does when nothing changes and the text is ASCII), an old code is the one code of its text, so it is its own canonical code; a young code, or a text that changed, is interned as before.

| | 10^5, sub-timer before / after (nine steady closes, wall) |
|---|---|
| the misses of the `LowerInvariant` cache | 103 / 36 ms |

- About 2 % of a steady close at 10^5. At 10^6 the cache misses nearly every key, so the share stays the same per key.
- Both sections of this day against the previous one's before, two pairs at load 88 to 138: steady close thread CPU median 324 / 309 and 324 / 287 ms, least close 303 / 288 and 303 / 264 ms, allocated 91.7 / 73.4 MB. At 10^4: 47 / 47 ms, 9.0 / 7.2 MB.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: a row held as one finds its `ones` row once (#119, 2026-11-18)
Theory. The row of a closed window for an element of `ones` found the element's row, compared the row with the graph's element, and then added its weight with `Add`, which hashed the key and probed the index a second time. The weight now goes to the row already found. The same holds where an element of `ones` gets its fragment back.

| | 10^4 | 10^5 |
|---|---|---|
| steady close, thread CPU median, before / after (two pairs, interleaved) | 41 / 40, 40 / 39 ms | 272 / 261, 283 / 281 ms |
| allocated per steady close | 7.2 / 7.2 MB | 73.4 / 73.4 MB |

- One index probe less per held row: 926,000 rows in nine steady closes at 10^5. Within the noise at load 31 to 44.
- Before is the previous section. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: the store writes a new window without lookups that find nothing new (#119, 2026-11-18)
Theory. `AppendNew` writes a window the plane does not hold. For a sample without a pair it removed the window from `Aux`, a table with one entry per window of `mean` and `last`. `Aux` holds only windows the plane holds (a delete, a retention and a move remove theirs), so that removal never found anything. It is gone. Every call of the store also found its plane by hashing the tenant and graph texts; the plane last looked up is now kept and taken when both texts are the same objects, which they are for every write of one flush.

| | 10^4 | 10^5 |
|---|---|---|
| steady close, thread CPU median, before / after (two pairs, interleaved) | 43 / 42, 41 / 40 ms | 294 / 279, 267 / 261 ms |
| allocated per steady close | 7.2 / 7.2 MB | 73.4 / 73.4 MB |

- Load 93 to 45. Each window a flush writes saves one probe of a large table and one hash of two texts: 1.59 million windows in nine steady closes at 10^5.
- Before is the section before the previous one. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Nothing sealed changes.

### Window close: a threshold finds a key's state without making its text, and keeps its windows in a sorted list (#119, 2026-11-18)
Theory. A threshold keeps one state per key, by the key's text: its numbers joined by commas. Every row made that text with `string.Join` over the numbers, which allocated an enumerator, one text per number and the joined text. The text is now written into a buffer on the stack and the state found through the dictionary's lookup by characters; the text is made only for a new key, the same text as before. A key's windows were a sorted dictionary: an object per window, and an enumerator stack allocated by every walk over them and by every look at the oldest one. They are now a sorted list, two arrays walked by index. The order of the windows and every step of the state machine are the same.

| | 10^4 | 10^5 |
|---|---|---|
| allocated per steady close, before / after | 7.2 / 6.3 MB | 73.4 / 64.9 MB |
| threshold, in the advances (two pairs) | | 208 / 138, 225 / 179 ms |
| steady close, thread CPU median (two pairs, interleaved) | 39 / 38, 41 / 37 ms | 254 / 252, 262 / 250 ms |
| threshold state retained, bytes per master node | | 312 / 265 |

- Load 57 to 23. Live bytes after a forced compacting collection at 10^5: 827 to 822 MB.
- Before is the section three back. Digests unchanged (`sha256:2654c25f...bfbc3`, `sha256:0ab80689...41140`). Seal format 25: a threshold's windows are sealed as a sorted list.

### Efficiency stage 2, time: a match asks whether a type fits a component with one hash of the type (#115, 2026-11-18)
A sampling profile of the 10^5 replay (workstation collector, the per-step timers of `bench phases` off) puts 22 % of the replaying thread in the match: for every changed element and every component of its pattern, the match asks whether the element's type is the component's type or a subtype of it. The answer was cached under the pair (type, component type), and a pair of strings hashes with the runtime's randomized string hash: 7.9 % of the thread was that hash, four fifths of it under the match.

Theory. The component fixes the type it asks about, so the cache moves into the component: one table per component, keyed by the type alone, which hashes once with the ordinal string hash. Same questions, same answers, so the same matches in the same order. No state is sealed; the tables are filled on first use as before.

| | 10^4 before / after | 10^5 before / after |
|---|---|---|
| thread CPU of the replay | 4.80 / 4.42 s | 39.2 / 37.2 s |
| instructions retired (process) | 121.8 / 124.0 G | 453.5 / 445.5 G |
| match, ms of the kind table | 712 / 453 | 8,238 / 6,093 |

- Means of four runs at 10^4 and two at 10^5, `bench phases` as shipped, workstation collector. At 10^4 the instruction count moves within the run to run spread (119 to 126 G), where loading the catalog and compiling dominate.
- In the profile the match goes from 22 % to 16 % of the replaying thread, and the randomized string hash from 7.9 % to 1.3 %.
- The per-step timers of `bench phases` cost about 11 % of the instructions at 10^5 (449 G with them, 400 G without), and the runs at 10^6 measured with them on.
- Before is the verification clone of main 5deb56f with the join below a traverse, the Ch. 6 paragraph and the arrangement links. Digests unchanged (10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...41140`). Load average 80 to 140; thread CPU is comparable within a pair, wall time is not.

### Efficiency stage 2, digests: a graph's digest and the state digest are hashed in pieces (#115, 2026-11-18)
The run at 10^6 replayed in full and then failed at its last line: the digest of a graph wrote enc(G) (Def. 3.5) into one string, and the master graph's passed 2^31 characters, the most one string can hold. The state digest wrote every graph's digest and every telemetry window into one string as well.

Theory. SHA-256 reads its input in order and needs none of it twice, so the text can be hashed as it is written. Both digests write the same characters in the same order into a buffer. Once the buffer passes 64 Ki characters it is encoded to UTF-8 through one stateful encoder, which carries a surrogate pair split across a cut, and appended to an incremental hash. The bytes hashed are the bytes of the whole text, so every digest is unchanged. `Encode` stays for the tests that compare encodings and writes through the same code.

| | 10^5 before / after |
|---|---|
| allocated by the state digest | 4,286 / 1,160 MB |
| time of the state digest (thread, under load) | 4.9 / 3.8 s |

- What is left is the order. A graph's elements are made and sorted by key string, and a graph's telemetry windows are sorted by element and property text, each held whole while its graph is hashed. That's 1,160 MB allocated at 10^5, so about 10 times that at 10^6. It runs once, at the end of a replay.
- Before is main f87f1ba. Digests unchanged (estate `sha256:32bcad1f...a422`, lab fixtures `sha256:4a1560a5...ffd0`, 10^4 `sha256:2654c25f...bfbc3`, 10^5 `sha256:0ab80689...41140`).

### Efficiency stage 2, digests: a set digest beside the digest of the written form (#115, 2026-11-18)
Research note 15: two graphs are equal when they hold the same set of written pairs (key text, canonical JSON of the state), so a digest that needs no order can replace the sort. This step adds that digest beside today's one and checks that the two agree; the printed digest does not change yet.

Theory. AdHash (Bellare and Micciancio 1997): the digest of a set of items is the sum, mod 2^256, of their SHA-256 values, in four lanes of 64 bits. A graph's set digest sums its written pairs in storage order, one element at a time, and hashes the sum once with SHA-256. The state's set digest sums, per graph, its pairs and its telemetry windows (the lines of the state digest after `w|`), tags each graph's sum with the graph's name, and sums the graphs' digests. Removing an item subtracts what adding it added, so a graph's digest can later be kept per change. That step is deferred while time per record is over the limit. Against chance, two different sets collide with negligible probability. The known weakness of AdHash at 256 bits needs inputs chosen by an attacker, and our use has none.

Agreement on equal versus not equal, old against new:
- random graphs, their clones, the states of chains, and patches applied in two orders (`Def_3_5_set_digest_agrees_with_the_digest_of_the_written_form`);
- every differential world, after every batch of four logs cut three ways, per graph and for the state (`The_set_digests_agree_with_the_digests_of_the_written_form`);
- the saved states (table): six distinct states, six distinct digests both ways, and the 10^4 estate reached by `replay` and by `bench phases` is equal both ways.

| state | digest of the written form | set digest |
|---|---|---|
| estate | `sha256:32bcad1f...a422` | `sha256:bd5d5efd...bb3f` |
| lab fixtures | `sha256:4a1560a5...ffd0` | `sha256:62343165...2acf` |
| capture 20261110T105738Z | `sha256:de8fe8a8...4520` | `sha256:ab85f141...4774` |
| capture 20261110T140542Z | `sha256:bd7dd356...cb13` | `sha256:3bff3e78...1b66` |
| 10^4 | `sha256:2654c25f...bfbc3` | `sha256:6445eac7...4c42` |
| 10^5 | `sha256:0ab80689...41140` | `sha256:dc2b3192...2598` |

| state digest at 10^5 (thread, under load) | written form, streamed | set digest |
|---|---|---|
| thread CPU | 3.12 s | 4.82 s |
| allocated | 1,160 MB | 888 MB |
| held at once | each graph's elements, sorted | one element; each graph's windows as one list from the store |

- The set digest costs one SHA-256 per item, 1.5 µs per item at 10^5 (3.16 million items: 1.18 million elements and 1.99 million windows), against 1.0 µs for the streamed written form with its sort. It holds no sorted list of elements. What remains whole is the store's list of a graph's windows, 32 B per window.
- Measured on main f87f1ba with the streamed digests; on main 8ae372a both digests of every saved state are the same. Digests of the written form unchanged.

### Efficiency stage 2, digests: the set digest is the digest (#115, 2026-11-18)
Research note 15, step 3. The digest of a graph and the state digest are now the set digests of the step before: AdHash of the written pairs, and for the state, per graph, of its pairs and telemetry windows under the graph's name. The written form `Encode` stays as the export and reading form and is no longer hashed. The streamed digest of the written form, the side-by-side set digest and the agreement tests are gone: they compared the old digest with the new one, and the old one no longer exists. `replay` and `bench phases` print one `state digest` again.

Theory. Two graphs are equal when they hold the same set of written pairs (Def. 3.4), so a digest that adds up one hash per pair decides equality without an order. The digest no longer sorts the elements and holds one pair at a time; what remains whole is the store's list of a graph's telemetry windows. A graph's digest could be kept per change; that is deferred while time per record is over the limit.

Every recorded digest changes once (old is the digest of the written form, new the set digest):

| recorded | old | new |
|---|---|---|
| estate (replay, and pinned `estate`) | `sha256:32bcad1f...a422` | `sha256:bd5d5efd...bb3f` |
| lab fixtures | `sha256:4a1560a5...ffd0` | `sha256:62343165...2acf` |
| capture 20261110T105738Z | `sha256:de8fe8a8...4520` | `sha256:ab85f141...4774` |
| capture 20261110T140542Z | `sha256:bd7dd356...cb13` | `sha256:3bff3e78...1b66` |
| 10^4 | `sha256:2654c25f...bfbc3` | `sha256:6445eac7...4c42` |
| 10^5 | `sha256:0ab80689...41140` | `sha256:dc2b3192...2598` |
| pinned `deploy-health` | `sha256:a9fa8922...3536` | `sha256:e79fc08a...27ff` |
| pinned `deploy-health minReady=1` | `sha256:51412d33...0658` | `sha256:ed9cf316...62f8` |
| pinned `estate-wide` | `sha256:debc3276...a8cc` | `sha256:f6c39f00...a06f` |
| kernel (`Kernel.CompiledDigest`) | `sha256:dd5bb1df...0ce6` | `sha256:98fa72a0...5155` |
| the empty graph | `sha256:4f53cda1...b945` (SHA-256 of `[]`) | `sha256:7f9c9e31...6ce9` (SHA-256 of 33 zero bytes) |

- The estate, the lab fixtures, both captures and 10^4 give, with this build (`replay`), exactly the set digests recorded in the step before. The 10^5 value is the set digest of the step before (`bench phases`), the same code; it was not run again on this build.
- Pins re-recorded with `SATTLE_PIN=1`; the kernel constant with `Kernel.ComputeDigest()`. The kernel digest changes because the kernel's digest is its graph's digest.
- Seal format 24 stays: a seal's data is unchanged. A seal of an earlier build names the old kernel digest and is refused as written by another build, so the journal is replayed.
- Tests: `Def_3_5_digest_does_not_depend_on_storage_order` (1, 2 and 4,000 entities stored in two orders, with text outside the BMP: equal written forms, equal digests, and for more than one entity a different storage order); `Digest_of_the_empty_graph_is_sha256_of_a_zero_byte_and_the_zero_sum`. The reference graph of the hot-graph tests computes its digest as the set digest too, so the hot graph is still compared with it after every patch.
- Cost at 10^5: the state digest costs what the set digest cost in the step before (4.82 s thread CPU, 888 MB allocated, against 3.12 s and 1,160 MB for the streamed written form). It is computed once at the end of a run and at a seal, not per record.
- Before is main 393f8e1 with the set digest and two #119 patches.

## First populated window and steady closes on the scale ladder (2026-11-19)

The capped run of `a5ca9da` on November 18 advanced twice with zero telemetry series. Advance 2 then created 1,985,732 series in 62.282 s, including 1.368 s of GC pauses. The previous summary skipped only advance 0 and included this initial population in its steady sample. Across the nine closes that followed, the median was 3.750 s and the maximum was 4.356 s. The all-advance median was 3.721 s and the maximum was 62.282 s. These measurements start with an empty engine. Restart from a stored seal and journal has a separate benchmark.

`bench phases` now reports all advances, those before telemetry, the first close with changed windows or resident series, and those after that first populated close. Regular source-driven closes with no changes still count. The final synthetic advance counts toward the steady sample when it commits a change. An empty synthetic advance appears in its own group. Percentiles use nearest rank. With nine steady observations, the observed p99 equals the sample maximum.

Use `bench phases --timing-only` to bypass the per-record, per-ingest and per-operator diagnostic counters. The replay still measures each close from the first source progress record through the window advance and downstream commits. The report gives writer CPU, whole-process CPU, all-thread allocation, GC pauses and the peak resident set through replay. The process CPU includes garbage collection and any worker threads. Graph reporting, the final state digest and the forced compaction used to measure live bytes happen outside the replay timing.

One serial Release replay at each scale used the engine at `6a7108c`, the new benchmark, .NET SDK 11.0.100, server GC and a 12 GiB managed-heap cap on the shared M1 Max. Each ten-minute estate had one initial structure load. Run these commands from the repository root.

```sh
dotnet build engine/src/Sattle.Cli -c Release
DOTNET_GCHeapHardLimit=0x300000000 dotnet engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll bench phases --timing-only --capture tmp/issue-49/gen/n100k
DOTNET_GCHeapHardLimit=0x300000000 dotnet engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll bench phases --timing-only --capture tmp/orchestrator/n1m
```

Use `bench gen-estate --nodes 100000 --minutes 10 --out DIR` and `bench gen-estate --nodes 1000000 --minutes 10 --out DIR` to regenerate the two inputs in separate empty directories.

| Measurement | 100,011 master nodes | 1,000,006 master nodes |
| --- | ---: | ---: |
| Records replayed | 816,075 | 8,159,533 |
| Replay wall time | 28.81 s | 334.48 s |
| Writer CPU per record, including parsing | 32.65 µs | 38.61 µs |
| Whole-process CPU per record, including parsing | 39.77 µs | 43.72 µs |
| Peak RSS through replay | 1,717 MB | 9,215 MB (8.58 GiB) |
| GC pause time during replay | 0.44 s | 5.06 s (1.51%) |
| First populated close, after two empty advances | 4.697 s | 69.819 s |
| Steady closes observed | 9 | 9 |
| Steady p50 / maximum | 0.261 / 0.337 s | 4.080 / 4.531 s |
| Final state digest, abbreviated | `dc2b3192…2598` | `5b266957…77f5` |

The million-node end state matched the November 18 digest. It held 7,691,409,016 live managed bytes after forced compaction, or 7.69 KB per master node. The resident high-water mark through graph reporting and the end-state digest was 10,415 MB (9.70 GiB). In the measured build the diagnostic graph table ran before the digest. The timing-only command now samples the digest and retained heap before producing that table. The raw reports are local files `engine/out/20261119-n100k-timing-only.txt` and `engine/out/20261119-n1m-timing-only.txt`.

The million-node run started at a one-minute system load average of 62.76. Both CPU measures exceed 37 µs per record. The November 18 run enabled detailed counters and reported about 44 µs of writer CPU per record. These runs differ in instrumentation and machine load. The first populated close still cost 69.819 s with the counters off, including 1.427 s of GC pauses. Nine later observations within 5 s leave the long-run p99 open.

To check a longer series of closes, `bench gen-estate --nodes 10000 --minutes 110 --out DIR` produced 9,995 master nodes, initialized once, and 762,998 records. One capped `bench phases --timing-only --capture DIR` replay of that input took 13.43 s, with a one-minute load average of 106.96 at the start on the shared host. Two advances preceded telemetry. Initial population took 518 ms. Across 109 consecutive later closes the median was 36 ms, the observed p99 was 117 ms and the maximum was 208 ms. This is evidence at 9,995 nodes. The local report is `engine/out/20261119-n10k-110min-timing-only.txt`.

### Commit visibility in the parallel graph prototype

The parallel-waves patch for #119 passed 19 ordering and restart cases and 394 default differential cases. A subscriber that reads a different graph during a commit exposed a discrepancy. In a single `Engine.Advance(0)` the engine publishes `deploymentReport` before `healthReport`. During the first callback, the canvas snapshot of `healthReport` on the parallel prototype already contained two elements whose edits had yet to be published. The serial engine exposed its previously published state. The focused reproduction is `dotnet test engine/tests/Sattle.Tests -c Release --filter FullyQualifiedName~CommitVisibilityTests`. The serial engine is the baseline for the measurements above. The implementation below preserves that callback boundary.

### Parallel preparation of graph matches

The engine evaluates graphs and operators in order. Inside a `MatchOp`, workers can scan ranges of a committed upstream patch. The writer holds the graph and its text codes fixed until the workers join. Each range owns its bindings, decoding cache and output rows. Workers share a read-only index of old relationship endpoints when a match needs the graph before the patch.

The writer consumes ranges in pattern-component order, then canonical edit order. Each range visits an edit's old value before its new one. The writer copies the signed rows and their presence bits into the operator's output before running the next operator. Only the writer applies downstream graph edits, writes telemetry and revisions, and calls commit subscribers. It rethrows worker failures in range order before committing this graph. Earlier graph commits remain visible. Range buffers and worker counters are transient. Seals and journal restart use the published engine state.

Workers can prepare a patch from an engine graph that uses its interner. A progress record or window advance needs at least 1,024 edits to qualify. An ingest needs 8,192. Both serial and parallel matches check which types occur before and after the edits. Each pattern component scans only its kind of element and skips the scan if none of those types can match.

The engine processes one group of ranges at a time. Each range covers up to 8,192 edits. The private row arrays have a 1 MiB budget per active range, separate from the final operator output and shared index. If a range produces enough matches to exhaust the budget, the writer reruns it into its final position. The writer scans small patches and very wide output layouts itself. The engine clears its reusable worker text caches after each match.

The worker limit defaults to one and includes the writer. Set `SATTLE_PREPARATION_WORKERS` for the CLI and host, or set `EngineOptions.PreparationWorkers` when embedding the engine. The engine caps the limit at the runtime's available processor count. For a replay comparison in the repository root, build once and use the same process settings and capture for each worker limit.

```sh
dotnet build engine/src/Sattle.Cli -c Release
cli=engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll
capture=tmp/issue-49/gen/n100k
DOTNET_GCHeapHardLimit=0x300000000 dotnet "$cli" bench phases --timing-only --graph-workers 1 --capture "$capture"
DOTNET_GCHeapHardLimit=0x300000000 dotnet "$cli" bench phases --timing-only --graph-workers 4 --capture "$capture"
```

#### 100,011 master nodes

With the original 1,024-edit ingest threshold, four workers prepared 15,807 ranges and allocated 390–394 MB more than one worker. The higher threshold, type filtering and reusable caches reduced this to 210 ranges and 55–57 MB. The first populated close accounted for 162 ranges. The nine steady closes qualified for none.

One diagnostic pair applied input through `--until 2026-01-01T10:02:01Z` with per-operator timers on, during another million-node replay. The match operators took 0.865 s of the 4.002 s first close with four workers. With one worker they took 2.156 s of 6.500 s. Both prefix digests were `f8b8f75e…0429`.

The full 816,075-record replay created 198,624 series on its first populated close. Each row is one fresh process on the shared M1 Max with server GC, the 12 GiB heap cap and per-operator timers off. Other team tests paused for the four runs. Starting load averages were 74, 48, 41 and 55. All digests matched `dc2b3192…2598`.

| Worker limit | First populated wall / writer CPU / process CPU, s | Nine steady closes p50 / max, ms | Replay wall, s | Replay writer / process CPU, s | Replay allocated / peak RSS, MB |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 3.844 / 3.625 / 4.581 | 237 / 288 | 24.86 | 23.82 / 29.21 | 5,742 / 1,670 |
| 4 | 3.431 / 3.111 / 5.140 | 282 / 325 | 26.39 | 24.85 / 31.67 | 5,799 / 1,646 |
| 4 | 3.561 / 3.092 / 5.186 | 355 / 404 | 31.84 | 27.43 / 35.25 | 5,797 / 1,605 |
| 1 | 4.319 / 3.970 / 5.016 | 293 / 428 | 27.57 | 25.34 / 30.71 | 5,742 / 1,596 |

Four workers shortened initial population by 10.7% and 17.6% against the adjacent one-worker runs. They completed 103 and 102 first-close ranges off the writer. Each worker run replayed six ranges on the writer across the full capture. Initial population allocated 1,145 MB with four workers and 1,098 MB with one. After compaction, all four processes held 795.3–795.4 MB of managed memory. Both four-worker processes took longer to replay the full capture. A fresh copy of the original engine had taken 8.017 s on initial population and 50.87 s on the whole replay at a starting load of 487.

#### 1,000,006 master nodes

Set `capture=tmp/orchestrator/n1m` for the same commands. This ten-minute capture contains 8,159,533 records and creates 1,985,732 series on the third advance. The original serial row used the working engine frozen before this change. The other two used the same new build.

| Engine / worker limit | First populated wall / writer CPU / process CPU, s | Nine steady closes p50 / max, s | Replay wall, s | Replay writer / process CPU, s | Replay allocated / peak RSS, MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Original serial | 106.060 / 75.448 / 88.427 | 4.795 / 8.828 | 415.82 | 345.07 / 389.94 | 60,889 / 11,479 |
| New, 4 | 64.295 / 46.951 / 75.720 | 6.220 / 6.491 | 447.54 | 328.73 / 387.88 | 61,363 / 11,497 |
| New, 1 | 62.071 / 57.543 / 70.223 | 4.195 / 5.170 | 353.38 | 314.41 / 358.06 | 61,300 / 11,445 |

The starting load averages were 454, 52 and 50, in table order. The two runs on the new build ran consecutively. Engine and durable-store tests overlapped the four-worker replay. The durable-store tests finished partway through the one-worker replay. The original engine had also recorded 69.819 s on the same first close earlier that day at a load of 63. The original-to-new difference includes machine load and changes to the serial match algorithm.

In this full-capture pair, four workers cut writer CPU on the first populated close by 10.592 s. Wall time increased by 2.224 s and process CPU by 5.497 s. The close allocated 11,405 MB with four workers and 11,348 MB with one. GC paused for 2.404 s and 1.307 s respectively. Workers completed 1,075 of the first close's 1,513 ranges off the writer. Four later closes prepared 54 ranges, 17 off the writer. Across the full replay, the engine retried 65 ranges on the writer. All three runs had identical counts and revision heads in all 13 graphs and the same digest, `5b266957…77f5`. After compaction, both new runs held 7.670 GB. The full replay used 43.88 µs of process CPU per record with one worker and 47.54 µs with four, against the 37 µs budget.

After the test suites and other replay checks finished, three fresh processes applied the same million-node capture through `--until 2026-01-01T10:02:01Z`. Each created 1,985,732 series on the close at 10:01 UTC. All three prefix digests were `41984359…eae2`.

| Worker limit, in run order | Starting load | Close wall, s | Writer / process CPU, s | Allocated, MB | GC pause, s | Ranges total / off writer |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 27.69 | 50.443 | 44.204 / 72.185 | 11,406 | 1.063 | 1,513 / 1,078 |
| 2 | 35.01 | 98.096 | 53.464 / 79.620 | 11,384 | 2.477 | 1,513 / 725 |
| 1 | 202.81 | 64.181 | 59.898 / 71.714 | 11,347 | 1.286 | 0 / 0 |

Four workers shortened this close by 21.4% against one worker, with similar process CPU. The full-replay comparison gave the opposite wall-time ordering. During the prefix series the one-minute host load ranged from 21 to 253. Each limit ran once per workload. The size and repeatability of a million-node benefit remain open.

A 110-minute replay at 9,995 nodes had 109 steady closes. With four workers, the first populated close took 264 ms and the steady median, p99 and maximum were 22, 46 and 78 ms. With one worker, those values were 364 ms and 22, 53 and 55 ms. All 88 ranges in the four-worker replay occurred on the first populated close. Both runs matched the recorded `a67711e7…26cd` digest. At one million nodes only nine steady closes were observed per full run, and both new runs exceeded 5 s. The 5 s p99 in #119 remains open.

### Counting stored telemetry windows

The graph summary and draft preview used `Dump(graph).Count`, which sorted the series and materialized every window. `WindowCount` sums the per-series counts under the existing store lock into a 64-bit integer. A .NET 11.0.100 Release measurement populated 100,000 series with ten consecutive 60-second windows per series. Both methods returned 1,000,000. Each was warmed on a separate one-window graph, followed by a forced GC before each measured read. In ABBA order, the two `WindowCount` reads allocated zero bytes on the calling thread and took 1.06 and 0.83 ms. The two `Dump(graph).Count` reads each allocated 89,909,792 bytes and took 172.93 and 69.12 ms. These are timings for the count operation.

### Testing a type index for graph matching

A match scans the upstream patch in canonical edit order for each seed of its pattern. Two prototypes shared work across readers of a patch with at least 128 edits. One built a list of edits per concrete type, including both types of a retyped element. The other kept only the distinct types and skipped seeds that could match none of them. Both deferred building the index until a second scan.

The 100,000-node capture contains 816,075 records. Each pair used the same Release CLI, server GC with a 12 GiB heap cap, and `bench phases --timing-only`. Only the runtime assembly changed. The nine steady closes followed the first populated window of 198,624 series.

| Run | First populated wall / writer CPU, s | Steady wall median / max, ms | Writer CPU, µs/record | Allocated, MB |
|---|---:|---:|---:|---:|
| Control for edit lists | 5.637 / 4.605 | 292 / 363 | 34.29 | 5,706 |
| Edit lists | 5.195 / 4.264 | 362 / 531 | 34.25 | 5,722 |
| Control for distinct types | 9.459 / 5.078 | 447 / 1,027 | 38.61 | 5,701 |
| Distinct types | 10.403 / 5.047 | 553 / 851 | 38.79 | 5,705 |

All four runs produced the recorded state digest `dc2b3192…2598`. Starting one-minute load averages were 57, 47, 122 and 161 on the 10-core host. A second comparison reversed the order and stopped applying input at `--until 2026-01-01T10:02:01Z`. The first populated close took 5.498 s writer CPU with distinct types and 5.377 s with the control. Both prefix digests were `f8b8f75e…0429`.

The edit lists saved 0.341 s of writer CPU on the first populated close in one pair, added 16 MB of allocations across the replay and yielded nearly identical CPU per record. The distinct-type filter gave inconsistent results across the two orders. These measurements support retaining the existing ordered scan.

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
