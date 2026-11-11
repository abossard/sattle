# 04. Compute and memory efficiency, persistence and liveness: a concept to build from

Status: concept, 11 November 2026. Nothing here is built. Measured numbers come from `engine/PERF.md`, `engine/README.md`, the paper (Ch. 6, 7, 9, 13) and a read-only analysis of the newest capture (`lab/captures/20261110T140542Z`). Numbers derived here are marked **budget**, **layout** or **assumption**. External claims link to a source that was opened. Sections 1.2, 1.3 and 3 use the engine session's measured baseline (`engine/PERF.md`, "Baseline for the efficiency concept"; live bytes after a forced compacting collection, a reflection census covering 98 to 100% of them; machine shared, so times are pessimistic, bytes exact). That baseline is X1. Companions: research 02 (storage and scale), research 05 (expressiveness, cost fragments; agreed boundary in Section 5).

User decisions already taken: the design point is **10⁶ master nodes** with their edges, telemetry and projections on one 16 GB, 8-core machine; persistence is **store-agnostic** (no vendor is the design centre); efficiency must **not cost expressiveness** (all operator kinds, parameters, direction classes, definitions as graphs, new ontologies without an engine change); the later prototype must be **always live**. On 2026-11-11 the user also accepted D2 to D9 as recommended (Section 11): .NET 10 with the state restructured, the Section 2 live budgets, the history of D4, exact structure, identities and counts, the event plane, a synchronous local journal with group commit, local files plus an object-storage tier (SQLite for tests), and adapted rows as journal content.

## 0. Findings that drive the design

1. **The graph is not the cost; everything around it is.** Measured live bytes are linear in master nodes: 9.6 KB per node without the tree graphs and 49.4 KB with them and their meter readings; 10⁶ nodes with the tree take 49.4 GB (`PERF.md` baseline). At 100,000 nodes the hot graph state is 5% of it; operator state is 42%, the store's in-memory copies 25%, sink layers 20%. The budget is about 2.2 KB per master node all-in with one hour of telemetry hot, so the gap is about 24×.
2. **Only the ordered input log and the definitions are truth.** Every other structure is a function of them (Theorem 6.3). Each one is a cache that must pay for itself, either by keeping evaluation at `O(|Δ|)` or by shortening recovery. Persistence follows: the journal is the only thing that must be durable before an input is acknowledged; commits and snapshots are written asynchronously.
3. **Events are not graph nodes.** 49,663 of 50,154 master entities in the newest capture are evidence events (Ch. 9, 9.6). A third plane, the event log, takes them out of identity, revisions, support counts and lineage. Projections still read them as rows.
4. **Live at 10⁶ needs window close at about 1 µs per series.** Measured at 10⁶ nodes with the tree: 25.5 s per steady window for ≈ 1.1 M series (≈ 23 µs per series), 174 s for the first window, and 27% of wall time in garbage collection. Correct, not live. At budget the steady close is 2.7 s on one core for 2.7 M series, 0.7 s on four.

## 1. Targets as budgets

### 1.1 Information content (first principles)

| Item | Information | Bits | Basis |
|---|---|---|---|
| Entity handle | one of `N` nodes | `log₂ N` (17 at 10⁵, 20 at 10⁶) | dense numbering |
| Global entity id | SHA-256 prefix | 128 stored | `e:` + 26 base32 characters (engine README, "Deviations") |
| Entity type | ≤ a few hundred types | ≤ 8 | ontology size |
| Categorical property value | small domains | 2 to 12 | dictionary of distinct values |
| Name-like property | mostly unique text, ≈ 30 characters | ≈ 240, once per node | resource and pod names |
| Relationship | endpoints, type | `2·log₂ N + 8` | its id is a hash of key rule and endpoints (README) |
| Telemetry window aggregate | count, sum, min, max on a fixed grid | timestamps implicit; values ≈ 8 to 16 bits after XOR coding | Gorilla: 1.37 bytes per point on average ([Pelkonen et al., PVLDB 8(12) 2015](https://www.vldb.org/pvldb/vol8/p1816-teller.pdf)) |
| Hubble flow, request direction | 5-tuple, two identities, verdict, time | ≈ 300 | the wire record is ≈ 4.2 KB of JSON (613 MB for 146,759 records in the capture) |

A master node with six categorical properties, one name and 3.3 relationships carries **40 to 60 bytes of information**. The budgets allow about 10 times that for indexes, alignment and update slack. Today's engine uses about 200 times that without the tree graphs and about 1,000 times with them.

### 1.2 Unit budgets and the measured gap

Measured column: `engine/PERF.md`, "Baseline for the efficiency concept" (100,000 master nodes with the tree graphs and readings unless stated; per master node = bytes divided by the 100,000 structure nodes).

| Unit | Budget | Measured | Gap |
|---|---|---|---|
| Whole engine per master node, tree graphs and readings [V1, V2] | ≤ 2,200 B (1.4) | 51,900 B at 10⁵, 49,400 B at 10⁶ | ≈ 24× |
| Whole engine per master node, no tree graphs [V1, V2] | ≤ 1,300 B | 10,400 B at 10⁵, 9,600 B at 10⁶ | ≈ 8× |
| Master graph per element (its state, store, sink layers and operators) [V1] | ≤ 120 B | 2,200 B (362,000 master entities and relationships) | ≈ 18× |
| Derived graph per element (same scope) [V1] | ≤ 70 B | 2,500 (`treeStructure`) to 7,900 B (`serviceTree`); `healthTree` 6,600 B | 35 to 110× |
| Hot graph state per element, all graphs | ≤ 40 B | ≈ 230 B (267 MB for ≈ 1.15 M elements) | ≈ 6× |
| Bytes per property value | ≤ 4 in a column plus the amortized dictionary | a `TextValue` and a `KeyValuePair<String,Value>[]` slot per property of every version (census types) | ≈ 10× (layout) |
| Bytes per closed telemetry window (4 aggregates) [V2] | ≤ 8 | 290 B in the telemetry store, plus ≈ 1 KB of `SeriesOp` state per series window and ≈ 0.5 KB of `ThresholdOp` state per key | ≈ 36× store alone |
| Bytes per open window accumulator | ≤ 32 | 16-byte `decimal` per value, a `SortedList` per series (`Stores.cs`) | ≈ 5× (layout) |
| Identity catalog per master node | ≤ 34 B | 420 B, plus a 1,410 B copy in the store | ≈ 12×, 54× with the copy |
| Interner per master node | ≤ 50 B (name pool and dictionaries) | 1,590 B | ≈ 30× |
| Bytes per event, event plane, on disk | ≤ 64 | an evidence element with identity, edge, support, lineage and journal JSON | ≫ 100× |
| Bytes per journaled input row | ≤ 40 adapted (≤ 8 compressed for telemetry rows) | 240 B per input row, 107 KB per journal entry (SQLite, envelope JSON) | ≈ 6× |
| ns per adapted flow row through aggregation | ≤ 50 | 59 interpreter (17.0 M rows/s), 21 hand-fused (`PERF.md` Q14) | met |
| ns per record end to end, parsing included | ≤ 2,000 (binary or pre-aggregated input) | ≈ 58,000 on a loaded machine (160,300 records in 9.3 s: JSON 40%, adapters 27%, engine 32%) | ≈ 29× |
| Allocation per record end to end | ≤ 1 KB | 57 KB (28 KB JSON, 8.5 KB adapters, 20 KB engine) | ≈ 57× |
| ns per telemetry sample append | ≤ 30 | 128 (`PERF.md` H2) | ≈ 4× |
| ns per closed series at window close, whole chain [V1, V2] | ≤ 1,000 | ≈ 23,000 at 10⁶ (25.5 s per steady window for 500,000 pair series and 625,000 readings) | ≈ 23× |
| First window (initial derivation) at 10⁶ [V1] | ≤ 20 s | 174 s | ≈ 9× |
| Garbage collection share of wall time [V1, V2] | ≤ 5% | 27% at 10⁶, 21% at 10⁵ (with the tree) | ≈ 5× |

**Validity of the baseline** (code review, `paper/research/06-code-review-early-note.md`, `07-code-review-findings.md`; the engine session is checking all three). Rows marked in the tables of 1.2 and 1.3:
- **V1, phantom relationships.** A row builder marks every column present, so unset columns read as present with code 0 (`Operators.cs:58`, `Columnar.cs:59`). `bench estate` never sets four pod columns and the VM `parentId`: every synthetic pod gets `RunsOn` and `ControlledBy` to a dangling phantom id (about 300,000 relationships), and all VMs sit in a join state they should not be in. Per-element costs, derived element counts (the 7.9 per master node of 1.3 included), `JoinOp` and store sizes at the estate are inflated by an unknown share. The capture figures (218 and 248 MB) come from real rows and are not affected by this.
- **V2, three windows.** The estate ran 3 to 4 windows. Costs that grow with the look-back are missing: `Series` expiry walks every element and retained window on each advance, `Threshold` replays every retained window per touched key, `Rollup` adds a slot per key and window that is never freed (1,440 per key per day), and the telemetry change log keeps up to 4 M records per graph. Steady-state numbers for these rows will be higher, not lower.
- **V3, restart history.** `bench b10 --hours` above the capture span shifts only the capture time; from the second pass ARG and authoring rows reach multiplicity 2, 3 and flows are dropped as late. Restart times "at 4 h" and the persistence growth from the repeated capture are not real history.
Until the engine session reports, budgets and gaps keep these numbers as upper bounds for V1 rows and lower bounds for V2 rows.

### 1.3 Where the measured bytes go

Census at 100,000 master nodes with the tree graphs and readings (5,188 MB live). Budget per master node is the layout of Sections 4 and 5 at the same mix.

| Structure | MB | B per master node | Budget B per master node | Gap | Decision (Section 3) |
|---|---|---|---|---|---|
| Store: patches kept for `Follow` (`Commits`) [V1] | 480 | 4,800 | ≤ 100 (capped tail) | 48× | commit log on disk |
| Store: element versions [V1] | 226 | 2,260 | 0 | removed | history from seal plus log |
| Store: adjacency versions [V1] | 162 | 1,620 | 0 | removed | as above |
| Store: second copy of the hot graph (`Head`) | 282 | 2,820 | 0 | removed | one head per graph |
| Store: identity bindings copy | 141 | 1,410 | 0 | removed | the catalog is the one copy |
| Hot graph state, all graphs | 267 | 2,670 | ≤ 460 | 6× | columns (4.2, 4.3) |
| Sink: contribution layers | 384 | 3,840 | ≤ 65 with support counts | 59× together with the next row | contribution rows (4.8) |
| Sink: support counts and group members of group emits | 254 | 2,540 | (in the row above) | | |
| Sink: lineage records | 209 | 2,090 | 0 | removed | on demand (3.2) |
| Sink: `relsAt` | 129 | 1,290 | 0 | removed | head adjacency |
| Sink: other (touched sets, telemetry buffer) | 46 | 460 | ≤ 20 | 23× | pooled |
| Operator `JoinOp` (hash indexes) [V1] | 674 | 6,740 | ≤ 100 | 67× | shared arrangements, 16 B per indexed row |
| Operator `GroupOp` (accumulators, ordered sets) [V2] | 514 | 5,140 | ≤ 64 | 80× | struct tables, arena multisets |
| Operator `TraverseOp` (reach, signature strings) | 359 | 3,590 | ≤ 13 | 280× | C-tree |
| Operator `SeriesOp` [V2] | 283 | 2,830 | ≤ 65 | 44× | read telemetry blocks; keep only the lookback downstream needs |
| Operators, all others [V2] | 327 | 3,270 | ≤ 60 | 55× | struct tables |
| Telemetry windows (store ring) [V2] | 216 | 2,160 | ≤ 1,060 (86 open + 970 for 60 min closed) | 2× | compressed blocks (4.5) |
| Interner | 159 | 1,590 | ≤ 50 | 32× | split (4.1) |
| Identity catalog | 42 | 420 | ≤ 34 | 12× | struct table (4.9) |
| Engine rest, statics | 14 | 140 | 140 | | |
| **Total** | **5,188** | **51,880** | **≈ 2,200** | **≈ 24×** | |

Where the measurement contradicts the estimates of the first version of this document:
- **Operator state is the largest share (42%), not the sink strings.** `JoinOp` alone is 674 MB (13%), the largest single structure: its hash indexes hold copies of input rows. The earlier layout accounting put the sink's string row keys first; lineage is 4% and support plus contribution layers 12%.
- **`Series` holds state.** About 1 KB per series window, 283 MB in all; Section 5 had listed it with no own state. The `lift` default `within 24h` keeps up to a day of windows per series. Budget and design now say: `Series` reads closed telemetry blocks (60 minutes hot, older from disk) and keeps only the lookback that its downstream operators need (a `Threshold` with `for 3` needs 3 windows, `rate` needs 2).
- **Derived elements per master node are about 7.9, not 1.6.** The earlier mix counted tree entities only; the estate has 793,000 derived entities and relationships for 100,000 master nodes. The 10⁶ budget of 1.4 is revised accordingly (≈ 2.2 KB per node instead of 1.6 KB).
- **The older totals were not live bytes.** The 15 KB per master entity and 45 KB per tree element of the first version came from `GC.GetTotalMemory`, which reads high; live bytes are 9.6 to 10.4 KB per node without the tree graphs and 49 to 55 KB with them.
- **The record path, not the operators, dominates the capture replay.** JSON parsing and adapters take 67% of the time and 36.5 KB of the 57 KB allocated per record; X10 (journal of rows) and X11 (pre-aggregation) matter more for the capture path than operator tuning.
- **Persistence matches the order of magnitude.** On SQLite at the capture's rate the journal grows 14.4 GB per day and everything 18 to 21 GB per day; the capture's flows, flow logs and audit rows are not pre-aggregated.
- **Restart is within budget already.** 11.2 to 14.4 s at 1 h with a seal under load (budget ≤ 30 s).
- **Contribution layers are required, not only counts.** The engine session's judgment: a retraction or merge must know which contributions make an element. Section 4.8 now keeps contributions as rows (element, sink, property tuple, count) rather than a bare count.

### 1.4 The ladder: 10⁵ and 10⁶ in full

Mix per master node, from the research 02 scenario (750,000 entities, 2.5 M relationships, 2 M telemetry series, 240,000 active pairs) and the estate tree (751,006 tree entities for 480,000 master entities): 3.3 relationships, 6 properties, 2.7 telemetry series, 7.9 derived elements (entities and relationships, measured on the estate), 1.2 identity keys, 0.32 active flow pairs per minute. Events are excluded (event plane). These are **assumptions**, scaled linearly.

Per-structure budget (layout of Section 3), hot memory:

| Structure | Formula per master node | 10⁵ | 10⁶ |
|---|---|---|---|
| Entity id digests and index | 26 B | 2.6 MB | 26 MB |
| Type, flags, since | 8 B | 0.8 MB | 8 MB |
| Property columns and name pool | 6 × 4 B + 32 B | 5.6 MB | 56 MB |
| Relationships: table, uniqueness, CSR both ways | 3.3 × 25 B | 8.3 MB | 83 MB |
| Identity catalog | 1.2 × 28 B | 3.4 MB | 34 MB |
| Derived graphs (records, columns, contribution rows, edges) | 7.9 × 32 B | 25 MB | 250 MB |
| Operator state (Section 5, per kind) | ≈ 300 B | 30 MB | 300 MB |
| Engine rest, statics (measured) | 140 B | 14 MB | 140 MB |
| **Structural subtotal** | ≈ 900 B | **90 MB** | **900 MB** |
| Telemetry open windows | 2.7 × 32 B | 8.6 MB | 86 MB |
| Telemetry closed windows, 60 min hot | 2.7 × 60 × 6 B | 97 MB | 970 MB |
| Event plane hot tail (last 16 per subject, 10% of nodes are subjects) | 0.1 × 16 × 64 B | 10 MB | 100 MB |
| Served patch tail, buffers | capped | 20 MB | 100 MB |
| **Steady total** | ≈ 2.2 KB | **220 MB** | **2.2 GB** |
| Peak: re-activation runs a second engine, seal buffers, bulk load | ≈ 2× steady | 0.45 GB | **4.4 GB** |

CPU at 10⁶ (budget; assumption: 90 structural revisions per second, 1.5 telemetry input rows per node per minute, 2.7 M series closing per window):

| Work | Rate | Budget per unit | Core share |
|---|---|---|---|
| Structural inputs through ≈ 6 projection hops | 90/s × 4 edits | 6 × 1 µs per edit | < 1% |
| Telemetry input rows | 25,000/s | 50 ns | 0.1% |
| Window close, all series | 2.7 M per 60 s | 1 µs per series | 2.7 s per window = 4.5% of one core |
| Journal append (sync) and asynchronous writes | Section 7 | fsync per group | < 5% |

- **Why one 16 GB, 8-core machine is right for 10⁶.** Steady memory is 2.2 GB (14%), peak 4.4 GB (28%). The rest is page cache for the journal and history, previews, lineage requests and collector headroom. CPU is far below one core at budget; the other cores take parsing, persistence, seals, reads and the bulk load. The margin is deliberate: re-activation doubles state, and a live system must stay live while it catches up.
- **Raw flows stay outside the engine.** JSON parsing at ≈ 15 µs per record caps one core at ≈ 67,000 records/s. At the measured 190 flows per node per second, 10⁶ master nodes (≈ 6,700 Kubernetes nodes) produce ≈ 1.3 M flows/s. Pre-aggregation collapses flows by more than two orders of magnitude: with the engine's own name filter, 117,931 named flows become 284 request keys (415 times fewer) and 82 workload pairs, about 50 active per window (X11); a first read-only count without that filter gave 82,893 request flows in 220 pairs. Pre-aggregation per cluster before the transport is part of the design (research 02, Rule A4); measured aggregation cost is 11 to 17 ns per flow after parsing (budget 50 ns), and parsing JSON is 19 µs per record, which is why it happens at the cluster and not in the engine.
- **Measured flow rate** (newest capture, 3 nodes): 34,600 flows per minute in steady minutes, about 190 per node per second; one system-pool node carried 116,322 of 146,759 flows.

### 1.5 What 10⁷ would need

Nothing in the 10⁶ design may block it. What changes:
- **Memory.** 10⁷ × 2.2 KB = 22 GB. A 15-minute hot telemetry horizon (closed windows 2.4 GB instead of 9.7 GB) brings it to ≈ 14 GB, which leaves no headroom on 16 GB. So 10⁷ needs one of: a 32 GB machine, a split into two graph spaces, or property columns that no projection reads moved to disk (≈ 0.6 GB) plus native memory for all large columns (4.2) and the identity catalog's cold part on disk (4.9).
- **CPU.** Window close is 27 s per window at 1 µs per series, so a live 5 s close needs key-partitioned window close on ≥ 6 cores (4.4, X12) or ≈ 0.2 µs per series (a fused kernel, 4.1).
- **Persistence.** Journal growth ≈ 260 GB per day at full resolution (Section 7.2): object-storage tiering and downsampling become mandatory, not optional.
- **Not changed.** `u32` handles (4.29 × 10⁹), the layouts, the operator fragments, the port. 10⁸ is many graph spaces on many machines: each graph space is an independent single writer.

## 2. What "live" means, in numbers, at 10⁶

Live is three latencies, each with an engine share and a share fixed by the source or by the declared semantics. Engine shares are **budgets**.

| Path | From | To | Engine budget (p99) | Fixed by semantics or source |
|---|---|---|---|---|
| Structural | source event received by the engine (Kubernetes watch, Resource Graph change, forwarder record) | changed health state committed in `healthTree` | ≤ 1 s (master revision ≤ 100 ms, then ≈ 6 hops) | source delivery: Kubernetes watch about a second; Resource Graph change records within 5 min ([resource changes](https://learn.microsoft.com/en-us/azure/governance/resource-graph/how-to/get-resource-changes)); Activity Log 3 to 20 min ([activity log](https://learn.microsoft.com/en-us/azure/azure-monitor/essentials/activity-log)) |
| Telemetry | logical close of a window (frontier passes window end plus lateness) | health state committed | ≤ 5 s (close of all 2.7 M series ≤ 3 s, chain ≤ 2 s) | window width `w`, lateness `L`, hysteresis `n_in` windows: a breach shows after at most `w + L + n_in·w` plus the engine share; with `w = 60 s`, `L = 30 s`, `n_in = 3` that is 4 min 30 s + 5 s |
| View | commit | open view updated (WebSocket push) | ≤ 500 ms | network to the browser |

End to end for a Hubble-observed drop burst: about 4.5 minutes at the default parameters, of which the engine owns at most 5.5 s. The parameters are data in the definitions (`state ... for 3`), so the user trades sensitivity for stability without an engine change.

Measured today (`PERF.md` H5): master revision p99 1.46 ms in memory and 22.4 ms on SQLite per record that made a revision. Window close at 10⁶ nodes with the tree: 25.5 s per steady window for ≈ 1.1 M series (≈ 60 s at the 2.7 M series of the budget mix), so the telemetry path is not live today; the gap is the 23× of 1.2.

**Under load.**
- The single writer is a queue. Its p99 waiting time stays small only at low utilization; the budget keeps steady utilization of the evaluation core below 50% (at budget it is about 6%).
- Bounded queues with blocking producers (4.6) keep memory bounded; the engine publishes its lag (`now − logical frontier`) on the protocol so a view can show "behind by X s" instead of silently aging.
- A bulk load (window 0, a definition re-activation) runs as a batch recompute (4.5) on a second engine; the live engine keeps serving until the new one catches up and is swapped in at a revision boundary.

**After a restart.** Live again within 30 s at 10⁶ (budget): load the latest seal (≈ 2.2 GB from local disk), replay the journal tail since it (≤ 5 minutes of input at a 5-minute seal interval: ≈ 7.5 M telemetry rows × 50 ns plus ≈ 27,000 structural inputs × 100 µs ≈ 3 s), then catch up the source backlog of the downtime at ≥ 20 times real time. During recovery the last seal is served read-only with its revision and a staleness flag. Measured today (`PERF.md` H10 and the baseline, newest capture repeated): 7.5 s with a seal at 1 h of history and 8.3 s at 4 h, flat in history (the 4 h figure is under question, V3 of 1.2); 11.2 to 14.4 s at 1 h on a heavily loaded machine; 16.2 s without a seal at 4 h, linear in history. The seal is about 790 bytes per master node.

## 3. What must exist at all

"Truth" cannot be rebuilt; "required cache" is a function of truth that incremental evaluation needs to stay `O(|Δ|)`; "index" can be shared or rebuilt; "convenience" is computed on demand or kept on disk.

Measured column: MB at 100,000 master nodes with the tree graphs and readings (`PERF.md` baseline census); "n/m" means not in memory in that run or not measured.

| State | Class | MB measured | Decision |
|---|---|---|---|
| Journal of ordered inputs with source positions and logical-time controls | truth | on disk (SQLite: 240 B per input row) | durable before acknowledgement; adapted columnar rows (7.1) |
| Definitions, catalog releases | truth | n/m (catalog load 4.2 s per host) | durable, small |
| Current state of each graph (head) | required cache | 267 | hot, columnar (4.2 to 4.4); in every seal |
| Identity bindings and forwarding | required cache (a function of inputs, Theorem 1.5) | 42 | hot, append-only; in every seal |
| Interner | index | 159 | split into id digests, per-property value dictionaries and a name pool; nothing for row identity; adapters intern only demanded columns, with a lifetime per source or plane (X3) |
| Uniqueness index `(source, target, type)` | index | in the head | inside the relationship table hash |
| `relsAt` (identity to its relationships, for merges) | index | 129 | replaced by the head adjacency |
| Element and adjacency versions (`InMemoryGraphStore`) | convenience | 388 | removed from memory; history comes from seal plus commit log on disk |
| Every committed patch, for `Follow` | convenience | 480 | commit log on disk; memory keeps a capped tail (exists: `--retain-patches`) |
| Second `Graph` per graph inside the store | duplicate | 282 | removed |
| Identity bindings copy inside the store | duplicate | 141 | removed |
| Emit-sink contribution layers and support counts | required cache | 638 | contribution rows (4.8) |
| `Supports`, `GroupMembers`, `GroupRefs`, `Lineage` | convenience | 209 for lineage records (group members are in the row above) | removed; lineage on demand (3.2) |
| Operator state (`Join`, `Group`, `Distinct`, `Threshold`, `Window`, `Retain`, `Series`) | required cache | 1,798 (`Join` 674, `Group` 514, `Series` 283, others 327) | pooled struct tables; shared arrangements; `Series` keeps only the lookback it needs (Section 5) |
| `Traverse` reach sets and signature strings | required cache, wrong shape | 359 | ancestor aggregation for `hierarchy` relationships, bitmaps otherwise (Section 5) |
| Telemetry windows | required cache (open, recent), history (older) | 216 | open hot; closed compressed, 60 min hot, then disk |
| Retained input rows of the host (up to 40 M rows) | convenience | n/m (no host in `bench estate`) | read from the journal on disk |
| Served history, event ring, canvas export | convenience | 18 on the capture | capped tails (exist) |
| Drafts, previews | convenience | n/m | on demand, bounded |
| Lens complement (bidirectional rules) | required cache for lens rules | n/m | per owned view element, ≤ 16 B plus kept values; in seals |
| Evidence elements in the master | modelling choice | 98% of master entities on the newest capture | event plane (3.1) |

Measured split (engine session's judgment, which this document shares): required by the semantics about 3.3 GB of 5.2 GB (hot graph, contribution layers, operator state, telemetry inside retention, identities); duplicates and features about 1.9 GB (37%), of which the store's copies are 1.29 GB. The required part is still about 17 times its budget (≈ 190 MB at 100,000 nodes): the gap is representation, not semantics.

### 3.1 Events: a third plane

- **Observation.** Evidence elements are 98% of the master in the newest capture. At the capture's audit rate (42,244 in 1,017 s) a 24-hour retention holds ≈ 3.6 M `AuditEvent` elements (Ch. 9, 9.6). Each mints an identity binding and gets revisions, a support count, lineage and an edge.
- **Not the graph plane.** An event never changes after it happens, so revisions, patches, merges and retractions carry no information for it; its identity is its delivery key.
- **Not the telemetry plane.** Telemetry is numeric with monoid roll-ups (Theorem 7.2). An event carries text that a person reads one by one.
- **Event plane.** `E_g = {(subject k, time τ, kind, seq, payload columns)}`, append-only, partitioned by time, indexed by subject. Read: `events(k, [τ₁, τ₂), kinds)`. Projections consume events as ordinary rows: counts per window (telemetry), "last `N` per subject" or "open warnings" as bounded evidence elements in a derived graph such as the health tree. Retention drops whole time partitions.
- **Expressiveness is unchanged.** Rules that emitted evidence elements still can, into derived graphs; only the master ingestion definitions move events to the event plane. No operator, rule class or language form is removed.
- **Theorems.** Theorem 7.1 (plane separation) extends verbatim to events. Theorem 6.3 is unchanged (events are inputs). Definition 9.12 and Proposition 9.11 hold for evidence elements materialized by projections. Theorem 1.5's binding table stops growing with the event rate. State digests change once: a separate event-plane digest is added and the captures are re-baselined once, reviewed (stage S4).
- **Size.** ≈ 40 to 64 bytes per event on disk (subject handle 4, time 8, kind 1, sequence 8, dictionary-coded payload).

### 3.2 Lineage on demand

- Bag semantics annotates each derived tuple with a natural number; why-provenance (lineage) is a different semiring annotation over the same query ([Green, Karvounarakis, Tannen, PODS 2007](https://web.cs.ucdavis.edu/~green/papers/pods07.pdf)). The engine keeps the counting annotation already (support counts); storing the lineage annotation for every element is what `Supports` and `GroupMembers` cost today.
- Concept: keep counts only. A lineage request for element `x` walks the operator chain backward over arrangements that exist anyway (`Join` indexes, `Group` input arrangements, head graphs at the request's frontier). Cost is proportional to the supporting rows of `x`, paid on request; a past revision replays from the nearest seal.
- `GET /lineage` keeps its content; X2 checks equality with the recorded lineage on every capture before the recorded form is removed.

## 4. Data layout

Principles: one dense integer handle per element per graph space; no object per row, version or value; fixed-width columns in pooled segments; strings only in dictionaries; history on disk. Columns are created at activation from the ontology, so a new ontology, property or source needs no engine change.

### 4.1 Identifiers and dictionaries
- **Handles.** `u32` per element (entities and relationships share the space, as the interner does today); reused only after a seal.
- **Global ids.** 16-byte digest per entity in a handle-indexed array plus an open-addressing index digest to handle (≈ 10 B per entry at 0.8 load); `e:` text rendered at the boundary. Relationship ids are a hash of `[tenant, module, key rule, source, target]` (README), so they are not stored: the index holds the handle and compares by recomputing the hash.
- **Value dictionaries.** One per (graph, property), codes `u8`/`u16`/`u32` per dictionary. Arrow's dictionary encoding uses signed integer indices of 8 to 64 bits ([Arrow columnar format](https://arrow.apache.org/docs/format/Columnar.html)), so seals and pages can use Arrow without re-encoding.
- **Name pool.** UTF-8 bytes with a `u32` offset per value; no `string` object until the boundary.

### 4.2 Entities and properties
- Per graph: `type: u16[]`, `alive` bitmap, `since: u32[]` (revision relative to the last seal).
- Per (type, property) a fixed-width column (`u32` codes, `i64` integers and instants, `i64` fixed-point decimals, bitmaps for booleans) with a presence bitmap; rarely set properties in a sparse column.
- Exact decimals: the paper requires exact arithmetic (Rule 7.1). `i64` fixed-point with a scale from the property's unit, overflow checked, and a `decimal` side table for values out of range.
- Columns are segments of 64 K entries allocated once and reused. Arrays of 85,000 bytes or more go to the large object heap, collected only with generation 2 and not compacted by default; pools of large objects are the documented remedy ([LOH](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap), [ArrayPool](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)). At 10⁶ managed pooled segments suffice; native segments (`NativeMemory.AlignedAlloc`, [NativeMemory](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory)) are the 10⁷ step, behind the same segment type.

### 4.3 Relationships and adjacency
- Relationship table: `src u32`, `dst u32`, `type u16`, `alive` bit, property columns: 12 bytes plus properties.
- Uniqueness `(src, dst, type)`: open addressing storing handles only (as `Graph.cs` does).
- Adjacency: CSR per direction over relationship handles sorted by `(vertex, type)` (8 bytes per relationship for both directions), plus a per-vertex append log of changes with creation and invalidation revisions, as LiveGraph's transactional edge log keeps adjacency scans purely sequential under concurrent updates ([LiveGraph, PVLDB 13(7) 2020](https://www.vldb.org/pvldb/vol13/p1020-zhu.pdf)). Logs merge into a new CSR past a threshold (Claim 13.5); Teseo's sparse arrays are the alternative if merges show in tails ([Teseo, PVLDB 14(6) 2021](https://www.vldb.org/pvldb/vol14/p1053-leo.pdf)).
- Compression is unnecessary at 10⁶ (8 MB of CSR at 3.3 relationships per node). For cold structure, gap encoding reached about 3 bits per link on a web graph of 1 G links ([WebGraph, WWW 2004](https://vigna.di.unimi.it/ftp/papers/WebGraphI.pdf)).

### 4.4 Versions without a copy per version
- The head is the only in-memory state of a graph. A commit's patch (before and after of each edited element) is needed anyway for subscribers; it goes to the commit log on disk; memory keeps a capped tail.
- Reads of older revisions: nearest seal plus commit log (Theorem 6.4), cost proportional to revisions replayed, bounded by the seal interval.
- Head readers that need a stable view get copy-on-write of touched 64 K segments only.

### 4.5 Telemetry
- **Open windows**: per (element, property) a 32-byte slot `count, sum, min, max` (`i64`, fixed-point); window start implicit on the grid.
- **Closed windows**: per-series blocks with implicit timestamps (aligned grid, Def. 7.5) and values as zigzag varints of the delta to the previous window, as `i64` integers: X4 measured 1.0 to 1.7 bytes per value against 4.5 to 4.9 for Gorilla-style XOR ([Gorilla](https://www.vldb.org/pvldb/vol8/p1816-teller.pdf)) and 16 for `decimal`, and 1.6 ns per append. The series were 3 windows long; XOR is re-measured on day-long series (V) before it is ruled out for long gauges. Only input aggregates are stored (all were exact integers at scale 0); means, rates and ratios are derived at read time from sums and counts.
- **Revisable windows** within the late horizon rewrite only the newest open blocks; older blocks are immutable and move to disk after 60 minutes (10⁶).

### 4.6 Events
Time-partitioned columnar segments: subject handle, time, kind code, sequence, dictionary-coded payload. The hot segment has a subject index; older segments carry a subject bitmap. Roaring bitmaps split the 32-bit space into 2¹⁶ chunks with array or bitmap containers ([Chambi et al., SPE 2016](https://arxiv.org/abs/1402.6407)).

### 4.7 Operator state
See Section 5 (per operator kind).

### 4.8 Contributions and support counts
A retraction or an identity merge must know which contributions make an element (the engine session's judgment, `PERF.md` baseline), so a bare count per element is not enough. Each sink keeps a struct table of contribution rows `(element u32, sink u16, property tuple ref u32, count i32)`, 12 to 16 bytes; the property tuple lives once in the sink's output columns, keyed by its hash, not as a copied `Props` per contribution. The element's state is the winning tuple by the declared rule. Counts are the `ℕ` annotation of Green et al. ([PODS 2007](https://web.cs.ucdavis.edu/~green/papers/pods07.pdf)); retraction is subtraction, existence is count > 0 (Theorem 6.6 limits this to acyclic derivations, which unrolled `Fixpoint` gives).

### 4.9 Identity catalog
Key: domain code plus a 128-bit hash of the canonical components; value: handle. Open addressing, ≈ 28 B per entry at 0.8 load; forwarding in a sparse hash. Append-only (Theorem 1.5). 34 MB at 10⁶. For 10⁷ and beyond, the cold part moves to disk: FASTER's hybrid log spans memory and storage with in-place updates of the hot set and reached up to 160 M operations per second on one machine ([Chandramouli et al., SIGMOD 2018](https://www.microsoft.com/en-us/research/uploads/prod/2018/03/faster-sigmod18.pdf)); its C# repository is no longer maintained and points to Garnet, whose storage engine Tsavorite is a FASTER fork ([FASTER repository](https://github.com/microsoft/FASTER)). A sorted run with a bloom filter is the simple alternative (X8).

## 5. Operators: budgets per kind and cost fragments

Expressiveness is a constraint: every operator kind, parameter, rule class and definition form stays. The fragments below are **cost classes**, not restrictions; a definition outside them runs with declared bounds and work budgets (`WorkBudget`, `BudgetExhausted`), and the planner prints its class (`check --plan`). Boundary agreed with research 05 (`projection-lang`): cost of an incremental step is input rows plus produced rows (Theorem 13.1 is output-sensitive), state is bytes per entry of the indexed integrated input, and telemetry operators are budgeted per window close.

DBSP classes underneath: linear operators need no state and are their own incremental version; bilinear joins need both integrals; `distinct` is `O(|Δ|)` with its integral as state ([DBSP, PVLDB 16(7) 2023](https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf), Theorems 3.3, 3.4, Proposition 4.7).

| Operator kind | Fragment | Work per step | State | Bytes per state entry (budget) | Time budget |
|---|---|---|---|---|---|
| `Filter`, `Map` | C (linear) | `O(|Δ|)` | none | 0 | ≤ 10 ns per row (selection vectors exist) |
| `Union` | C | `O(|Δ|)` | none | 0 | ≤ 5 ns per row |
| `Values` | C | once | none | 0 | n/a |
| `Window` (assignment) | C | `O(|Δ|)` | open windows (with `Group`) | see `Group` | ≤ 10 ns per row |
| `Match` node pattern | C | `O(|Δ|)` | the input graph | 0 own | ≤ 20 ns per row |
| `Match` fixed-length edge pattern | C (bilinear) | `O(|Δ| + |Δout|)`, `|Δout| ≤ |Δ|·degree` | the input graph's adjacency (shared) | 0 own | ≤ 50 ns per produced row |
| `Join` | C (bilinear) | `O(|Δa| + |Δb| + |Δout|)` | both sides indexed by key | 16 | ≤ 50 ns per lookup and per produced row |
| `Group` / `Rollup` with `count`, `sum`, `mean`, `weightedMean`, `any`, `all` | C (per key) | `O(|Δ|)` per touched key; per window `O(active keys)` | accumulator per key; `any`/`all` as two counters | 16 + 8 per aggregate | ≤ 30 ns per row, ≤ 200 ns per closed key |
| `Group` / `Rollup` with `min`, `max`, `last` | C+ | `O(log m)` per update, `m` rows of the key; no recompute on retraction (multiset kept) | sorted run per key in an arena | 12 per member | ≤ 100 ns per row |
| `Group` `countDistinct`, `Distinct` | C+ | `O(1)` amortized | count per distinct value | 12 | ≤ 50 ns per row |
| `Series` | C (per window) | per closed window of the input | only the lookback its downstream operators need (a `Threshold` with `for 3` needs 3 windows, `rate` 2); older windows are read from telemetry blocks (60 min hot, then disk). Measured today ≈ 1 KB per series window, because the `lift` default `within 24h` keeps up to a day | ≤ 8 per kept window | ≤ 100 ns per point read |
| `Threshold` | C (per key) | `O(1)` per key and window | state machine per key | 8 to 16 | ≤ 50 ns per key and window |
| `Retain` | C | `O(|Δ|)`; expiry `O(expired)` via a time-ordered queue | held rows | 12 | ≤ 30 ns per row |
| `EmitEntity`, `EmitRelationship` | C | `O(|Δ|)` | support count per element and sink | 4 to 8 | ≤ 100 ns per element change |
| `Link` (identity) | C | `O(1)` amortized; a merge costs `O(degree of the loser)` | identity catalog | 28 per key | ≤ 100 ns per resolution |
| `Lift` | C | lowered to `Match`, `Group`, `Join`, `Filter`, `Series`, `Rollup`, `Map`, `Threshold`, emits | as lowered | as lowered | sum of lowered |
| `Traverse` over a relationship declared `hierarchy`, decomposable aggregate (worst, max with a multiset, sum, count) | C-tree | `O(depth)` per change (ancestor aggregation) | parent array plus accumulator per node | 4 + accumulator | ≤ 1 µs per change at the tree's depth (≈ 8 for the estate tree; the checker admits `Traverse` depth up to 64) |
| `Traverse` general, depth `d ≤ 64` (checker limit, `Checker.cs`) | outside (bounded) | up to `fanout^d` per touched start | reach per start as Roaring bitmaps | data dependent | declared bound; budget per start |
| `Fixpoint` (bounded unrolling `k ≤ 64`, checker limit) | outside (bounded) | `k` × step cost; nested fixpoints multiply: `∏ kᵢ` × innermost step (two nested `k = 64` levels cover 65 × 65 edges, research 05) | `∏ kᵢ` × relation | product of levels × body | declared `k` per level, budget = product over nesting; `maxFacts` enforced. The step accepts any operator, so a body can be C+ (for example `Group` with a filter on its count) or non-monotone; its class is the worst class in the body times the unrolling |

What falls outside and what it costs: general `Traverse` and `Fixpoint` keep their full semantics; their cost depends on graph shape (fan-out, depth, `k`, nesting), so they get a declared bound and a work budget (for nested fixpoints the product of the levels' `k`), and a definition that relies on them at 10⁶ is measured per definition (X6, X13). The tag-and-group difference construction of research 05 is C with `sum` tags and C+ with `max` tags. The health roll-up, the main user of `Traverse` today, moves to C-tree because `Contains` is declared `hierarchy` and checked to have at most one parent.

**Shared arrangements.** Projections that read the same input relation share its index; shared arrangements gave order-of-magnitude gains in response time and resources for concurrent incremental queries ([McSherry et al., PVLDB 13(10) 2020](https://www.vldb.org/pvldb/vol13/p1793-mcsherry.pdf)). In Sattle the head graph is the arrangement of every `Match`, and a `let` relation read by several statements is arranged once per key; the planner sees all readers at activation.

**Direction classes.** `lens` and `bijective` rules keep their semantics; their backward path evaluates on a staged engine that activates only the graphs it needs (README). Budget: complement ≤ 16 B per owned view element plus kept values; a backward edit ≤ 1 s at 10⁶ (staged evaluation of the affected rows only).

## 6. Execution model

### 6.1 Batch and vectorized evaluation
- Keep the vectorized interpreter over columns with selection vectors (17 M rows/s; a hand-fused loop reaches 47.5 M, `PERF.md` Q14). Vector-at-a-time execution avoids the per-tuple interpretation overhead that kept tuple-at-a-time engines one to two orders of magnitude slower on TPC-H ([Boncz, Zukowski, Nes, CIDR 2005](https://www.cidrdb.org/cidr2005/papers/P19.pdf)); DuckDB uses the same vectorized interpreted model in-process ([Raasveldt and Mühleisen, SIGMOD 2019](https://mytherin.github.io/papers/2019-duckdbdemo.pdf)).
- Kernels over `Span<T>` slices (no copies, [spans](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/)) with `Vector128<T>` where hot ([SIMD](https://learn.microsoft.com/en-us/dotnet/standard/simd)). A fused kernel generator is the 2.8× headroom, not needed at 10⁶.

### 6.2 Allocation-free hot path
- No object per row, row key, version or value; row identity is an integer tuple hashed in place.
- Window close reuses its tables (allocation fell from 22 to 1.8 bytes per row when group tables were pooled, `PERF.md` Q14); the same for emits, telemetry and operator state.
- Allocation budget: ≤ 1 byte per ingested row and ≤ 64 bytes per closed series (`--trace-alloc` checks it).

### 6.3 Memory management
- Large state in pooled segments (managed at 10⁶, native at 10⁷); the collector's work scales with the objects it traces from roots ([GC fundamentals](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals)).
- Server GC with dynamic adaptation (DATAS, default since .NET 9 for Server GC), a heap hard limit, and `ConserveMemory` ([GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)).
- Evidence: element views allocated 148 MB each and one run in ten paused 200 to 440 ms until `ScanWindow` stopped allocating (`PERF.md` H8).

### 6.4 Parallelism under single writer and determinism
Safe because each is a function of the recorded order (Theorem 6.3) and output edits have a canonical order (Theorem 4.4):
- graph spaces in parallel (separate writers);
- adapters and parsing in parallel (pure functions), reassembled in recorded order;
- projections pipelined; siblings on separate cores (Theorem 13.3 bounds throughput by the slowest stage);
- key-partitioned `Group`, `Join`, `Distinct`, `Threshold` and window close inside one step, merged into canonical edit order (open question Q16 of research 03; X12);
- seals, compaction and CSR merges on immutable segments in the background.
Not safe: two writers on one graph; any arrival-time tie-break across partitions.

### 6.5 Incremental versus recompute
- A join step costs `|Δa| + |Δb|` lookups plus produced rows (Theorem 13.1); recomputation costs the input. Break-even when `|Δ|·d̄ ≈ |DB|·c_scan/c_lookup`; with a random lookup ≈ 10 sequential scan steps (**assumption**, X9), recompute wins above ≈ 10% of the input changing.
- Window 0 of the estate (97 s against 10 s steady, `PERF.md`) is that case. Policy per operator and step: if `|Δ| > θ·|DB|`, build state by sort and bulk insert, compute in batch, emit the difference. Same result (Theorem 6.1), different strategy.

### 6.6 Backpressure
Bounded channels between source, acceptance, evaluation, persistence and followers, producers waiting when full ([channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)). Structural inputs and events are never dropped; the telemetry ring may drop oldest under overload and marks the window `Partial`. Consumption from the transport stops when acceptance is full.

## 7. Persistence (store-agnostic)

### 7.1 What is persisted

| Data | Class | Written | Size at 10⁶ | Growth per day at 10⁶ (assumption: 90 structural inputs/s, 1.5 telemetry rows per node per minute, 820 events/s) |
|---|---|---|---|---|
| Journal: structural input rows, adapted, with source positions and the interleaving order | truth | synchronously, group commit | n/a | 90/s × 100 B ≈ 0.8 GB (X10 measured 11.2 B per row on average with Brotli, 21 to 74 B for rows with free text, so this is an upper bound) |
| Journal: telemetry input rows (pre-aggregated), or closed-window records (Def. 7.7) | truth | synchronously, as adapted rows (D7, D9) | n/a | 2.2 G rows × 8 B compressed ≈ 17 GB (52 GB uncompressed) |
| Event plane segments | truth (inputs) | with the journal | n/a | 71 M events × 64 B ≈ 4.5 GB (20 clusters at the capture's audit rate) |
| Definitions, catalog releases | truth | synchronously on commit of a release | < 1 MB | per release |
| Commit log (patches per graph, frontiers) | derived (Theorem 6.3) | asynchronously | n/a | 90/s × 4 edits × 120 B ≈ 3.7 GB |
| Seals (whole engine state at a journal position) | derived | asynchronously, every 5 min | ≈ 2.2 GB (measured today ≈ 790 B per master node on the capture) | 2 kept locally, 1 per day archived ≈ 2.2 GB |
| Closed telemetry blocks older than the hot horizon | derived (from the journal) or truth (if raw rows are not kept) | asynchronously | n/a | 2.7 M series × 1,440 windows × 6 B ≈ 23 GB at 60 s resolution |
| Identity bindings | derived (in seals) | with seals | 34 MB | ≈ 3 MB (100,000 new keys) |

- At 10⁶, persisted volume is dominated by telemetry (journal rows or closed blocks, ≈ 20 to 25 GB per day at 60 s resolution), not by the graph. Thirty days at full resolution is ≈ 0.8 TB; downsampling after 7 days to 5-minute windows cuts the remainder by 5. At 10⁵ everything is a tenth.
- **Positions-only journal (not chosen; D9 decided adapted rows).** If a source retains its payloads (Event Hubs keeps events 1 hour to 7 days on Standard, up to 90 days on Premium and Dedicated, [Event Hubs features](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-features)), the journal can record only the interleaving (source, partition, sequence ranges) and fetch payloads from the source on replay within retention, archiving payloads before it expires. It cuts synchronous journal bytes by two orders of magnitude and couples replay to the source's retention. Kept as a later option, measured by P3.

### 7.2 The storage port: the only seam

The engine's ports stay (`IGraphStore`: `Head`, `Commit`, `Read`, `ScanAdjacency`, `Follow`, `Seal`; `IJournalStore`; `ITelemetryStore`; `Stores.cs`). Graph reads at the head are served from memory, so no backend is on the read hot path. Underneath, a backend offers five primitives:

| Primitive | Contract |
|---|---|
| B1 `Append(stream, expectedNext, records)` | atomic all-or-nothing for the batch; succeeds only if the stream's next position equals `expectedNext` (compare-and-append), else `Conflict(actualNext)` |
| B2 `Read(stream, from, max)` | records in position order from `from`, contiguous, or `Gap` |
| B3 `PutImmutable(key, bytes)`, `Get(key)`, `GetRange(key, offset, length)` | write-once objects (seal pages, segments); reads may be served from caches |
| B4 durability | an acknowledged `Append` or `PutImmutable` survives the failure class the backend declares (process, machine, zone) |
| B5 (optional) point and range reads by element key at a revision | only for history queries without a seal replay; the engine can always answer from B2 and B3 |

Streams: one journal per graph space, one commit log per graph, one manifest per graph space (seals, segments, positions). A lease with an epoch for the single writer is a B1 append to the manifest; every record carries the writer's epoch.

**When a backend is weaker:**
- **No multi-record atomicity.** Write chunks first (idempotent, unreferenced garbage on failure), then one commit record that names them; the commit record is the single commit point. This is the Delta Lake protocol: a transaction commits when the next log record is created, and only one client may create it ([Armbrust et al., PVLDB 13(12) 2020](https://www.vldb.org/pvldb/vol13/p3411-armbrust.pdf)), and the research 02 protocol for document stores.
- **No compare-and-append, but put-if-absent.** Name records by position (`log/00000000000000000042`) and create with put-if-absent: Azure Blob `If-None-Match: *` and S3 conditional writes both fail an existing key with 412 ([Azure conditional headers](https://learn.microsoft.com/en-us/rest/api/storageservices/specifying-conditional-headers-for-blob-service-operations), [S3 conditional writes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html)). An Iceberg-style catalog does the same with a check-and-put of a metadata pointer ([Iceberg spec](https://iceberg.apache.org/spec/)).
- **Neither.** An external lease or coordination service serializes appends; Delta Lake used a separate coordination service for S3 before it had put-if-absent ([Armbrust et al.](https://www.vldb.org/pvldb/vol13/p3411-armbrust.pdf)).
- **No ordering.** Positions are in the record names or bodies; readers order and detect gaps by contiguity.
- **Eventual consistency.** The commit point must be read strongly (conditional write or strongly consistent read); immutable objects can lag and are retried. S3 provides strong read-after-write consistency for PUT and DELETE ([S3 consistency](https://docs.aws.amazon.com/AmazonS3/latest/userguide/Welcome.html)).
- **No durable acknowledgement, or a loss window.** Delay acceptance receipts until durable, or rely on source retention: the last durable journal entry names source positions, and the engine re-reads the source from there. PostgreSQL's asynchronous commit loses at most three times `wal_writer_delay` (200 ms default) of commits, never consistency ([asynchronous commit](https://www.postgresql.org/docs/current/wal-async-commit.html), [WAL settings](https://www.postgresql.org/docs/current/runtime-config-wal.html)).
- **Record size limits.** Chunk; the commit record names the chunks.
- **Limited retention (log services).** The log holds only the tail; seals and archived segments go to a B3 store.

### 7.3 Backend classes

| Class | B1 / B2 / B3 / B4 mapping | Cost per commit | Cost per restart | Where it stops |
|---|---|---|---|---|
| **Local files** (append-only segment files, manifest, memory-mapped seals) | single owner process; length-prefixed records with checksums, torn tail truncated on recovery; `fsync` per group; seals as files read through memory maps ([memory-mapped files](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files)) | one write plus one `fsync` per group commit | map seal, decode, replay tail | one machine: no failover, disk size; pair with object storage for the machine-loss case |
| **Embedded store** (SQLite in WAL mode, an LSM such as RocksDB) | B1 as a transaction with the position as key; SQLite allows one write transaction at a time ([transactions](https://www.sqlite.org/lang_transaction.html)); RocksDB `WriteBatch` is atomic and groups concurrent writes into one WAL write with one `fsync` when `sync` is set ([basic operations](https://github.com/facebook/rocksdb/wiki/Basic-Operations), [WAL performance](https://github.com/facebook/rocksdb/wiki/WAL-Performance)) | measured SQLite p99 4.3 ms for 50-edit patches (`PERF.md` H3) | as local files; SQLite journal replay measured (`PERF.md` H10) | one writer per file; one machine |
| **Object storage** (immutable segments plus a manifest) | B1 by put-if-absent per position (412 on conflict); B3 native; S3 strong read-after-write | one PUT per group at network latency (**to measure**, P2); a single block blob targets up to 3,000 requests per second ([Blob scalability](https://learn.microsoft.com/en-us/azure/storage/blobs/scalability-targets)) | GET manifest, parallel range GETs of seal pages | per-commit latency: use for sealed segments, history and followers, not for synchronous acceptance |
| **Relational database** (PostgreSQL, SQL Server, …) | B1 as a transaction with a unique `(stream, position)`; group commit via `commit_delay` ([WAL settings](https://www.postgresql.org/docs/current/runtime-config-wal.html)) | one round trip per group | seal from B3, tail by range query | one primary's write rate; cost per instance |
| **Document database** (Cosmos DB and similar) | B1 by single-partition batch plus a conditional commit record (research 02, 4.4) | one round trip per group plus one request per touched partition if a read model is kept (32 requests per revision measured on an emulator, `PERF.md`) | seal from B3, tail by query | per-partition throughput and request cost |
| **Log service** (Event Hubs, Kafka-like) | B2 native: ordered sequence numbers per partition ([Event Hubs features](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-features)); no compare-and-append documented for Event Hubs, so the single writer needs an external fence and readers deduplicate by delivery key; no B3 | one append per group | read from the seal's position | retention (≤ 7 days Standard, ≤ 90 days Premium); no point reads; pairs with B3 |

**Default.** Local files on local NVMe for the journal (synchronous, group commit) and seals; asynchronous upload of sealed segments, seals and the commit log to object storage under a manifest with put-if-absent. SQLite stays the embedded reference backend for tests and development (it exists). Relational, document and log backends are adapters behind the same port.

### 7.4 How it scales
- **Partitioning.** By tenant and graph space; each has its own streams and one writer.
- **Single writer with a lease.** Lease record with an epoch, renewed by conditional write; every record carries the epoch; a stale epoch's append fails at B1.
- **Group commit.** Proposition 13.2: cost per revision `β + α/n` falls with group size `n`; the first revision of a group waits for the group or a deadline (≤ 5 ms for acceptance).
- **Seals and compaction.** A seal every 5 minutes or N entries bounds restart; journal segments older than the oldest retained seal move to the archive tier.
- **Tiering.** Local NVMe holds 1 to 7 days; object storage holds the rest; telemetry downsampled after 7 days.
- **Read replicas.** Followers tail the commit log and hold heads only (cheap), or replay the journal as full engines (deterministic, Theorem 6.3) for read scaling and hot standby.
- **Recovery time.** `T(restart) = T(load seal) + entries since seal × c(replay)`, independent of total history with seals (`PERF.md` H10: 7.5 s at 1 h, 8.3 s at 4 h, the latter under question, V3; linear without a seal).

### 7.5 Keeping persistence off the hot path

| Write | Mode | Latency cost | Memory cost |
|---|---|---|---|
| Journal append of accepted input | synchronous to the local journal, group commit ≤ 5 ms (D7). The flush gates the **source checkpoint** (the acknowledgement to the transport), not evaluation: an accepted input is evaluated as soon as it is in the journal's buffer; if the machine dies before the flush, the transport delivers it again and deterministic replay (Theorem 6.3) gives the same state | none on the live path; checkpoint lag = T + flush time: P1 measured flush p50 5 to 6 ms, p99 11 to 13 ms on the macOS SSD, so acceptance p99 15 to 31 ms there (budget in 7.7) | write buffer ≤ 64 MB |
| Commit log | asynchronous | none on evaluation | queue ≤ 64 MB, then backpressure |
| Seal | asynchronous, from immutable or copy-on-write segments | none on evaluation; one copy of touched segments | ≤ one seal's worth of touched segments |
| Closed telemetry blocks, event segments | asynchronous | none | per-segment buffers |
| Upload to object storage | asynchronous, after local durability | none | none extra |

### 7.6 Conformance and qualification of a backend

The storage contract tests (`StoreConformanceTests`, parametrized over memory, SQLite and Cosmos), `LiveCrashTests`, and the restart and seal tests are the start. A backend must also pass:
- compare-and-append under two concurrent writers (one wins, no forked positions), fencing by epoch;
- torn-write and crash at every step (chunk written, commit record written, acknowledgement sent), with recovery to a consistent prefix;
- ordered read after reopen, gap detection, idempotent retry of the same batch;
- durability after acknowledgement for the declared failure class (process kill; machine loss simulated by discarding unsynced writes);
- chunking above record size limits; delayed visibility of immutable objects (eventual consistency simulation);
- seal round trip with equal state digest.

Qualification measurements: commit p50 and p99 against group size, sustained commits per second, bytes per entry, restart time at 1 h, 24 h and 7 days of history, follower lag, cost per day at 10⁶.

### 7.7 Budgets for persistence at 10⁶

| Quantity | Budget |
|---|---|
| Acceptance (source checkpoint) latency added by durability | p99 ≤ 10 ms on server NVMe (to measure on Linux); ≤ 35 ms on a laptop-class SSD, where P1 measured 14.6 to 30.9 ms p99 at 30,000 records/s. Not on the live path (7.5) |
| Sustained journal throughput | ≥ 30,000 rows/s (≥ 10× the 10⁶ rate) |
| Restart to live | ≤ 30 s |
| Local disk for 7 days | ≤ 200 GB |
| Seal size | ≤ 2.5 GB, written in ≤ 10 s without pausing evaluation |
| Follower lag | p99 ≤ 2 s |

## 8. Alternatives

| Option | What it is | Verdict | Cost against the 655 tests and the protocol |
|---|---|---|---|
| **A. .NET 10, state restructured** | same front end; state as struct tables, pooled segments, support counts, columnar telemetry and events, journal of rows, store-agnostic persistence | **Chosen (D2).** The hot graph and interpreter already meet their budgets in .NET; the gap is state shape. ≈ 3,000 of 15,229 lines change (`Output.cs`, `Stores.cs`, telemetry store, operator state, `Identity.cs` storage, journal) | all tests stay; digests are the oracle at every stage except the event-plane change (one reviewed re-baseline) |
| B. Native core (Rust or C++) behind the protocol | rewrite runtime, operators, identity, stores | Not now: a second language and a boundary for a gap that is not the runtime's. Revisit if X7 shows collector tails above budget after A | protocol tests stay; runtime unit tests rewritten |
| C1. Differential dataflow (Rust) | data-parallel framework responding to input changes, compiled to timely dataflow ([repository](https://github.com/TimelyDataflow/differential-dataflow)) | operator semantics fit; identity minting and merge, revisions and patches, telemetry windows with late horizons, lineage and lenses would be rebuilt on top | as B plus a semantic mapping |
| C2. Feldera (DBSP) | incremental SQL engine, Rust, MIT, spills to disk ([repository](https://github.com/feldera/feldera)) | same theory as the paper; the language would compile to SQL, and identity merge, `retain` by logical frontier and graph emits do not map cleanly. A reference for operator costs | full re-platform |
| C3. DuckDB or DataFusion | in-process vectorized analytical engines ([DuckDB](https://mytherin.github.io/papers/2019-duckdbdemo.pdf), [DataFusion](https://github.com/apache/datafusion)) | batch, not incremental; useful for ad-hoc queries over journal segments, closed telemetry and the event plane | none on the hot path; additive |
| C4. Embedded graph store | Memgraph, Neo4j and similar | rejected in research 02 | n/a |
| **D. Hybrid** | A for the hot path, C3 over cold columnar files | **Recommended second step** | additive |

## 9. Experiments, each a day

Read-only on the existing engine, or small throwaway programs outside `engine/`. Data: `lab/captures/*`, `sattle bench estate`.

| Id | Question | Measure | Data | Decides |
|---|---|---|---|---|
| X1 | Where do the bytes per master node go? | done: reflection census in `PERF.md` baseline (no gcdump available) | newest capture, estate at 25,000 to 10⁶ | ordered stages S1 to S4 by measured share (Section 10) |
| X2 | Lineage recomputed instead of stored? | backward walk for 1,000 random elements vs recorded `Supports`; heap with `Supports` and `GroupMembers` emptied | captures, estate | remove recorded lineage if equal on 100% and p99 ≤ 50 ms |
| X3 | What does the event plane save? | definitions copy with evidence rules emitting counts only: master entities, bindings, heap, replay time | captures | the saving of the decided event plane (D6); below 90% fewer master entities or 50% less heap, find what else scales with events |
| X4 | Fixed-point, compressed telemetry | `i64` fixed-point vs `decimal`, XOR-coded closed blocks: bytes per window, ns per append, exactness | capture series from `replay --out` | `i64` if every value round-trips; block format if ≤ 8 B per window |
| X5 | Support counts as columns | 10⁶ elements, 3 sinks, random contribute and retract; columns vs `Contribution` lists | synthetic | confirm ≤ 16 B per element, ≤ 100 ns per update |
| X6 | Tree roll-up (C-tree) | ancestor aggregation vs `Traverse` depth 16 (as the health tree uses it), memory and ns per changed signal | estate tree (451,006 elements) | replace for `hierarchy` if ≥ 10× less memory and equal states |
| X7 | Collector tails | window close and `bench b8` with Server GC plus DATAS, with and without a hard limit; then pooled segments | estate | whether option B is revisited (p99 pause > 50 ms after A) |
| X8 | Cold identity and structure | 10⁷ keys: open addressing vs sorted run with bloom vs SQLite; gap-encoded adjacency bits per link | synthetic, estate | the 10⁷ spill format |
| X9 | Incremental vs recompute | `Join`, `Group` with `|Δ|/|DB|` from 0.1% to 100% | synthetic | θ of 6.5 |
| X10 | Journal of rows | adapted row batches vs envelope JSON: bytes per record, replay time | newest capture | journal format |
| X11 | Flow pre-aggregation | request direction (`is_reply`), workload pair keys, ns per flow, pairs per minute; engine `Calls` telemetry vs aggregated input | newest capture | pre-aggregation contract |
| X12 | Key-partitioned determinism | window close partitioned 4 ways, canonical merge; digests at chunk sizes 1, 50, 100,000 | estate, captures | parallel window close |
| X13 | General `Traverse` and `Fixpoint` cost at 10⁶ | a definition with depth 4 reach, one with `Fixpoint` k = 8, and one with two nested fixpoints (k = 8 each) on the estate scaled to 10⁶: work per change, state | synthetic | default bounds and budgets for the outside fragment, including the nesting product |
| L1 | Live latency | timestamps at receipt, master commit, `healthTree` commit, WebSocket push; steady and during a 10× burst | newest capture at speed, estate | whether Section 2 budgets hold |
| L2 | Restart to live | kill at random points; time to serve current state, time to zero lag | estate at 10⁶ | seal interval |
| P1 | Local journal | group commit latency and throughput with `fsync` on local NVMe, group deadline 1 to 10 ms | synthetic rows | the group deadline and segment format of the D7 journal |
| P2 | Object storage commit point | put-if-absent latency p50/p99, conflicts under two writers, request rate on one prefix | Azure Blob test container | object storage as the async tier only, or also for commits |
| P3 | Positions-only journal | journal bytes and replay time with payloads fetched from Event Hubs vs stored | live hub of the lab | whether positions-only becomes a later option (D9 chose adapted rows) |
| P4 | Seal at 10⁶ | write time without pausing evaluation, size, mapped load time | estate at 10⁶ | seal format and interval |
| P5 | Followers | commit-log follower and journal-replay follower lag and memory | estate | read scaling mode |
| P6 | Backend conformance | the suite of 7.6 on local files and SQLite | n/a | qualification of the default backends |

## 10. Staged plan

| Stage | Builds | Proves | Kept / replaced |
|---|---|---|---|
| S0a | make the oracle and the measurements valid (Section 12.4): fix the reached defects that change state, unset columns absent, restart bench with real history, steady-state estate (≥ one full look-back of windows), an independent reference evaluator for operator tests | equal digests at chunk sizes 1, 50, 100,000 after the fixes; steady-state baseline rerun (V1 to V3 resolved) | defects fixed in place; `ReferenceGraph` grows into a reference evaluator |
| S0 | done: X1 (baseline), X2, X3, X4, X10, X11, P1 (results in Section 13) | event plane saves 98.8% of master entities and 61.7% of bytes; lineage is a function of input; telemetry 1 to 1.7 B per value; journal 11.2 B per row with an equal digest; pre-aggregation 415×; journal flush 5 to 13 ms on macOS | nothing changes |
| S1 | store without in-memory copies: no second `Head`, no element or adjacency versions, no identity copy, commit log on local disk with a capped tail for `Follow`; history reads from seal plus log | 100,000 nodes with the tree: 5,188 MB → ≤ 3,900 MB (−1.29 GB, 25%); equal digests on all captures and the full suite | `InMemoryGraphStore` history replaced; port unchanged |
| S2 | layout primitives (handles, digests, dictionaries, pooled segments, struct hash tables) and operator state on them: `Join` on shared arrangements, `Group` struct tables with arena multisets, C-tree for `hierarchy` roll-ups, `Series` with needed lookback over telemetry blocks, bulk-load path | operator state at 100,000: 2,157 MB → ≤ 30 MB; window close ≤ 1 µs per series (≤ 2.7 s at 10⁶); first window ≤ 20 s at 10⁶ | operator internals; plans unchanged |
| S3 | sink layers: contribution rows (4.8), lineage on demand (and the `Lineage`/`GroupRefs` leak gone), `relsAt` removed; hot graph columns and interner split (4.1 to 4.3), adapters interning only demanded columns; live input coalesced per few milliseconds (Section 13) | sink layers at 100,000: 1,023 MB → ≤ 10 MB; hot graph ≤ 46 MB; interner ≤ 5 MB; X2 equality holds | `Output.cs` state, `Graph` internals (API kept, differential tests against `ReferenceGraph`) |
| S4 | telemetry blocks with fixed-point; event plane with its own digest | ≤ 8 B per closed window; newest capture master ≈ 500 entities; one reviewed re-baseline; 100,000 nodes with the tree ≤ 220 MB, 10⁶ ≤ 2.2 GB | telemetry store, evidence rules in definitions |
| S5 | persistence: local-file journal and seals behind the port, async object-storage tier, conformance suite of 7.6, followers | Section 7.7 budgets; restart to live ≤ 30 s | journal format (versioned); SQLite backend kept |
| S6 | parallel adapters, sibling projections, key-partitioned window close | close at 10⁶ ≤ 1 s on 4 cores with equal digests; L1 within Section 2 budgets under a 10× burst | none |

Kept throughout: the language, checker, catalog and definitions; the protocol (`PROTOCOL.md`); the storage port; the 655 tests; state digests of the S0a engine (not of today's engine) and the reference evaluator as the oracle (one reviewed re-baseline in S4).

## 11. Decisions

All decided by the user; D1 earlier, D2 to D9 on 2026-11-11 as recommended.

| # | Question | Decided | Alternatives not taken |
|---|---|---|---|
| D1 | Scale target | 10⁶ master nodes on one 16 GB, 8-core machine; 10⁷ kept unblocked (1.5) | 10⁷ or 10⁸ per machine |
| D2 | Language and runtime | .NET 10 with the state restructured (option A); revisited only if X7 shows collector tails above budget after A | native core; an existing engine |
| D3 | Live latency | engine share p99 ≤ 1 s structural, ≤ 5 s after window close, ≤ 0.5 s to the view, restart to live ≤ 30 s; `w = 60 s`, `L = 30 s`, hysteresis as data (Section 2) | tighter or looser budgets |
| D4 | History | journal rows 30 days (7 local, the rest in object storage), seals daily; telemetry 60 min hot, 7 days at 60 s, then 5-minute windows to 30 days; events 24 h hot partition, 30 days on disk | raw envelopes forever; rows only |
| D5 | What may be approximate | structure, identities and counts exact; quantiles by sketches; telemetry dropped under overload only with `Partial` | nothing approximate; approximate counts |
| D6 | Where events live | event plane, bounded evidence elements in derived graphs | graph plane with retention; telemetry counts only |
| D7 | Durability point of an accepted input | synchronous local journal, group commit ≤ 5 ms; asynchronous replication to object storage | asynchronous with refill from source retention |
| D8 | Default backend | local files plus an object-storage tier; SQLite for tests; other classes as adapters behind the port | embedded SQLite, a database or a log service as default |
| D9 | Journal content | adapted rows | positions only (payloads in the source; later option, P3); raw envelopes |

## 12. Reconciliation with the code review

Source: `paper/research/07-code-review-findings.md` (section 1 and verdict) and `06-code-review-early-note.md`.

### 12.1 Its changes 3 and 4 against the stages

| Review change | Same as | Differences that the plan adopts |
|---|---|---|
| 3. One packed, weighted row arrangement with typed columns and eviction, shared by `Join`, `Distinct`, `Series`, `Retain`, `Traverse` and `Threshold`, with an explicit save and load per operator | S2 (operator state on shared arrangements) | **One** primitive, not a struct table per operator kind: one equality (typed, canonical decimals), one consolidation and one retraction rule. It is a correctness change as much as a memory change, because the eight keying variants are where the order-dependent results come from. Eviction is part of the primitive. Explicit per-operator state codecs replace the reflective snapshot, and the seal digest covers operator state (today it covers graphs and telemetry only, so lost operator state passes). |
| 4. One copy per element: head-only default store, working in-place property updates, compact-graph handles as the currency on the hot path, pruned output tables, tree roll-up rewritten towards ancestors or as a fold in one graph | S1 (head-only store), S3 (contribution rows, lineage on demand, hot graph columns), and the C-tree part of S2 | Handles as the currency between layers is the central point: today the object-form `Element` crosses every layer, so each element is held 3 to 6 times and the compact graph is bypassed on every read. The plan had this only implicitly ("no object per row"). The tree roll-up also needs a definitions change (one graph instead of `treeStructure`, `treeRollup` and `healthTree` as three copies). |

**Order.** Unchanged for S1 to S4, with one stage in front: S0a (12.4). S1 (head-only store) stays first: it is independent of the row primitive and removes 25% at once. The primitive of change 3 is S2 because every stateful operator, and later the sink's contribution rows (S3), are built on it. Handle currency starts in S2 (operator outputs as handle rows) and is completed in S3 (sinks and graph reads).

### 12.2 What the concept missed

| Missed | What the review found | Added to the concept |
|---|---|---|
| Append-only state without eviction | interner, id pool, props pool (the in-place branch of `Graph.Apply` is dead, so every property update appends a block), `KeyTable` without removal (`Rollup`: 1,440 slots per key per day), output tables never pruned, store history, telemetry change log up to 4 M records per graph | Rule E: every structure names its eviction trigger (element retraction, window past the late horizon, seal). Handles and pool slots are reclaimed at seals. Test: a 24-hour run at constant estate has flat live bytes (L3 below). |
| No-op deltas never consolidated | an irrelevant property change emits retract plus insert for every match and flows through the whole plan (`Match.cs:124-133`) | Consolidate at every operator boundary (sum weights of equal rows, drop zeros: Z-set addition), and push projections down: a `Match` reads only demanded columns, so a change to an undemanded property stops at the source. The checker's leaking `demands` set (`Catalog.cs:170`) must be fixed first, or the pushdown passes extra columns. |
| Three-fold materialise-and-compare per epoch | each changed element materialised and compared at flush, graph check and store; window close re-resolves unchanged elements; store commit and the host's JSON synchronous in the loop (`Engine.cs:281-357`, `Output.cs:239-267`) | Edits are produced once, as handle-level differences of the winning contribution, only for elements whose winning tuple changed. Persistence and serialisation leave the loop (7.5). The review names the re-resolution as the probable main constant of the per-series window close cost, so S3 carries part of the window-close target, not only S2. |
| Expression evaluation ignoring the selection vector | all physical rows are evaluated; `id(...)` mints and persists identities for rows a `Filter` rejected (`Identity.cs:165`, `Exprs.cs:107-110`) | Kernels evaluate selected rows only; functions with effects (`id`) run after selection. This is a defect first (12.4) and a cost second. |
| Tree roll-up to descendants | `reach` traverses from every element to its descendants: all pairs, `n·depth` state, and three copies of the tree (`acme.treehealth.sattle:64`) | C-tree in Section 5 is the engine side. The definitions side: traverse towards ancestors (same pairs, per-start state bounded by depth) or a fold over `hierarchy`, in one graph. Expressiveness is unchanged; the definitions change. |
| Stateful telemetry operators that replay | `Series` expiry walks all elements and windows per advance; `Threshold` replays all retained windows per touched key; telemetry writes without support counts | `Series` expiry `O(expired)` by a time-ordered queue; `Threshold` as an incremental state machine whose state is the run counter, not a replay; telemetry window values weighted (retractable), not last-writer-wins. |

### 12.3 What in the plan was wrong

- **"State digests of today's engine are the oracle at every stage."** Wrong as written. The engine has defects reached today that change state (12.4), and the test suite takes most expected values from the engine itself (review, verdict on tests). The oracle is the S0a engine plus an independent reference evaluator for operators (incremental output equals batch evaluation, Theorem 6.1).
- **Per-kind struct tables in S2.** Replaced by one shared weighted row primitive (12.1).
- **Budgets measured on the estate as given.** V1 inflates and V2 understates; the stage targets of Section 10 are provisional until the steady-state rerun of S0a.
- **`Threshold` and `Series` budgeted as cheap.** Correct as a target, but today's implementations replay; the budget assumed the design, and the review shows the code does not have it yet.
- **Seal correctness assumed.** The reflective snapshot skips fields (`StateSnapshot.cs:32`) and its digest does not cover operator state; restart budgets (Section 2) assumed a correct seal.

### 12.4 Defects to fix before the engine can serve as the oracle

Reached today and changing the state or the digest that the rebuild is compared against (fix each with one test, in S0a):

| Defect (review location) | Why it blocks the oracle |
|---|---|
| `Threshold` hysteresis recomputed from `initial` over held windows; a key flips to "off" when its entering windows age out (`Operators.cs:643`) | wrong state in any run longer than the look-back, exactly the steady-state runs S0a needs |
| `id(...)` evaluated for rows a `Filter` rejected; identities minted and persisted (`Identity.cs:165`, `Exprs.cs:107-110`) | the identity catalog and every id-derived digest differ from a selection-respecting rebuild |
| Text `<`, `>` and the `last` tie-break compare interner codes, not text (`Exprs.cs:119`) | results depend on first-seen order of strings; the S3 interner split changes codes, so the rebuild would differ for the wrong reason |
| Telemetry writes without support counts; window deletions never published (`Output.cs:295-306`, `Host.cs:490`) | telemetry state depends on write order; the digest covers telemetry |
| Unset columns marked present (`Operators.cs:58`, `Columnar.cs:59`) | phantom relationships in every synthetic benchmark (V1); any adapter that leaves a column unset is affected the same way |
| Reflective snapshot skips `Layout` fields; seal restore of a live space falls back to a full replay silently (`StateSnapshot.cs:32`) | restart measurements and S5 rely on a correct seal |
| Checker `demands` never cleared between projections (`Catalog.cs:170`) | extra columns in every stateful operator: inflates the baseline and changes arrangement shapes |
| `serve` interns from two threads without the lock (`Server.cs:182`) | nondeterministic codes in `serve`; L1 (live latency) runs in `serve` |

Latent, but a correct rebuild would differ from the oracle on them, so decide the intended semantics and pin it with a test before S2: decimal keys by scale (2.5 and 2.50 do not join, `Operators.cs:753`); `countDistinct` next to another aggregate cancels nothing (`Operators.cs:435`); `Retain` removes the whole held entry on any retraction (`Operators.cs:1109`); `Series` stores windows older than the look-back and retracts rows never inserted (`Output.cs:94-95`); `Traverse` misses changes at exactly max depth (`Operators.cs:948`).

Not on the oracle path (fix on their own schedule): the subscriber counter, CORS and request guards, draft preview base, threshold parameters exported as 0, refactoring of `via` ports.

### 12.5 Additions to the experiments

| Id | Question | Pass |
|---|---|---|
| L3 | Is memory flat at constant estate over time? | 24 h of logical time at constant 100,000 nodes after S0a: live bytes at 24 h within 5% of those at 2 h |
| V | Do the baseline rows marked V1 to V3 hold after S0a? | rerun of `bench estate` (steady state, at least one full look-back) and `bench b10` with real history; Sections 1.2 and 1.3 updated with the new figures |

## 13. S0 results and what they change

Source: `engine/PERF.md`, section "S0 experiments"; raw output in `engine/out/experiments/<ID>.txt`, programs in `engine/experiments/`. Machine shared (load average 15 to 225), so times are pessimistic.

| Id | Result | Criterion | Verdict |
|---|---|---|---|
| X3 event plane | newest capture: master entities 50,266 → 603 (−98.8%), live bytes 248 → 95 MB (−61.7%), identities minted 53,380 → 2,148; older capture: −96.7% entities, −41.4% bytes | ≥ 90% entities and ≥ 50% bytes on the newest capture | pass (the older capture has fewer events, so its fixed part weighs more) |
| X11 pre-aggregation | engine rule reproduced exactly (466 of 466 pair-windows, 8 of 8 window totals); 11 to 17 ns per flow after parsing; parsing 19 µs per record; requests only by workload pair: 284 keys for 117,931 flows (415×), 82 workload pairs | exact equality, ≤ 50 ns, ≥ 100× | pass |
| P1 local journal | flush alone p50 5.1 to 6.2 ms, p99 10.7 to 12.8 ms (`Flush(true)` and `F_FULLFSYNC` cost the same); best acceptance p99 14.6 ms; 100,000 records/s acknowledged in every cell; 20 of 20 kills recovered with nothing lost or duplicated | p99 ≤ 10 ms at 30,000/s with T ≤ 5 ms; 20 of 20 recoveries | fail on latency (macOS SSD; Linux NVMe not run), pass on throughput and recovery |
| X4 telemetry | every input aggregate an exact integer (scale 0); derived means, rates, ratios are 28-digit repeating decimals; delta varint 1.0 to 1.7 B per value, Gorilla XOR 4.5 to 4.9 B; append 1.6 ns | input aggregates exact; ≤ 2 B per value; ≤ 30 ns | pass, on series of only 3 windows |
| X10 row journal | digest equal to the envelope replay; 11.2 B per row with Brotli (31.8 B uncompressed), about 90 times smaller than the envelopes; replay 2.96× faster | equal digest; ≤ 40 B per row; ≥ 3× | size and digest pass; speed misses by 1% because the remaining 2 s is the engine's own ingest, which no journal format removes |
| X2 lineage | `Supports` and `GroupMembers` identical between a 4,029-batch replay and a 48-batch evaluation for every derived element (5,168 and 3,868) and the sample of 1,000; stored lineage is 4% of bytes on the capture, 9% on the estate with the tree | equal content | pass, with a changed procedure: the per-element evaluation of Appendix A.1 needs a `src/` change, so two batch histories were compared instead; no per-element latency was measured |

What changes in the concept:
- **The durability point gates the source checkpoint, not evaluation** (7.5, 7.7). P1 shows a flush costs 5 to 13 ms on a laptop-class SSD whatever the group size, so acceptance p99 under 10 ms is not reachable there. The live path does not need it: evaluation runs on the buffered journal entry, and the transport's checkpoint advances only after the flush, so a crash re-delivers the input and replay reproduces the state. D7 stands. The 10 ms budget stays for server NVMe, unmeasured; 35 ms is the laptop budget.
- **Telemetry encoding is delta varint over integers, not Gorilla XOR** (4.5), and only input aggregates are stored; means, rates and ratios are derived at read time. This also resolves the exactness question of Rule 7.1 for stored values. Day-long series are still unmeasured (V).
- **Journal format: dictionary-coded row segments, Brotli** (7.1). At 11.2 B per row the capture's rate (60 M rows per day) needs about 0.7 GB per day instead of 14.4 GB on SQLite with envelope JSON. Replay from rows is bounded by the engine (2 of 6 s), so the next replay gain is in the engine, not the journal.
- **The interner scales with raw input, not with the graph** (X3: 31 → 21 MB when events are off). The adapters intern every string of every row, including rows that no rule keeps. Stage S3 adds: adapters intern only demanded columns (the same pushdown as 12.2), and interned strings get a lifetime per source or plane.
- **Per-batch overhead may be half the engine time** (X2: the same rows in 48 merged batches took 1.3 s against about 3 s in 4,029 batches, equal digest). Live input arriving within a few milliseconds is coalesced into one batch before evaluation; batch invariance (Theorem 6.2) makes this safe for state. It gives up revision boundaries between the coalesced inputs, which is acceptable for live input and is never done for recorded replay with `--chunk`.
- **Lineage is a function of the input and the definitions** (X2), so storing it is a cache. Two leaks were found: `Lineage` records and `GroupRefs` are not removed when an element is retracted. On-demand lineage in S3 needs its latency criterion (≤ 50 ms p99 per request) measured on the S3 walk.
- **Pre-aggregation numbers corrected** (1.4): 82 workload pairs and 415× with the engine's name filter, not 220 and 200×.
- **Definitions question for the user.** `observedCalls` counts reply flows: 57,007 of 117,931 named flows are replies, and 118 of the 245 `Calls` relationships exist only as replies (an edge from the callee back to the caller). The dependency direction of the service tree and the health roll-up inherits that. Whether the rule should drop replies is a decision about the definitions, not the engine.

## Appendix A. S0 procedures and pass criteria

Common rules for every experiment below.
- **Where** (agreed with the engine session). Each experiment is a standalone console project in `engine/experiments/<ID>-<name>/`, not part of `Sattle.slnx`, with a `ProjectReference` to `engine/src/*` only where it needs engine types. No change to `src/` for S0: an experiment that needs an internal uses reflection from its own project, and the engine session asks before touching `src/`. Run order: X3, X11, P1, X4, X10, X2; each result is summarized in `engine/PERF.md`, section "S0 experiments", as it finishes. Results go to `engine/out/experiments/<ID>.txt`; the first lines name the engine commit (`git rev-parse HEAD` plus "dirty" if the working tree differs), the machine, the .NET version and the exact command.
- **CLI.** `sattle` means the Release CLI built once by the engine session: `dotnet build -c Release src/Sattle.Cli && cp -R src/Sattle.Cli/bin/Release/net10.0 out/cli`, then `dotnet out/cli/sattle.dll ...` from `engine/`. Run only when `engine-build-status.md` says the Release binaries are free.
- **Data.** "Newest capture" is `latest` (`lab/captures/20261110T140542Z` at the time of writing: 160,300 records). "Older capture" is `lab/captures/20261110T105738Z`. The overlay `lab/captures/space-overlay.sattle` is applied automatically.
- **Repeats.** Timing figures are the median of 5 runs on an otherwise idle machine, with the load average written next to them; memory figures come from one run after a forced compacting collection (as the existing census prints).
- **Reporting.** Every experiment states pass or fail against its criterion, with the numbers. A fail is a result, not a reason to stop: write what was measured and which assumption of this document it contradicts.

### A.1 X2: lineage recomputed instead of stored

Question: is the lineage the engine stores (`OutputBuilder.Supports`, `GroupMembers`, `GroupRefs`, `Lineage`) recomputable from the input and the definitions with equal content, and what does storing it cost?

Procedure.
1. Cost of storing: `sattle replay latest --census` and `sattle replay lab/captures/20261110T105738Z --census`. Record the census rows `graph.sink.<Supports>k__BackingField`, `graph.sink.<Lineage>k__BackingField`, `graph.sink.<GroupRefs>k__BackingField` and any `GroupMembers` row: objects, MB, bytes per master node. Repeat with `sattle bench estate --windows 3 --census` if the bench accepts `--census`; otherwise note that it does not.
2. Recorded lineage: `sattle replay latest --out out/experiments/X2-recorded`; the lineage files written by `replay --out` (forward and reverse, `LineageTests`) are the reference.
3. Recomputation (program `X2-lineage`): replay the same capture in-process through the engine API. Pick 1,000 elements uniformly at random with a fixed seed (`20261111`) from the derived graphs that have recorded lineage (all graphs except `master`), stratified so that every graph with lineage contributes at least 50 or all of its elements. For each element `x`, compute its supporting rows by evaluating the sink's input relation on the final state and keeping the rows whose emitted element id is `x` (a naive full evaluation is allowed here: this experiment checks content, not speed). Express each row exactly as `OutputBuilder.RowKey` does (role, kind, graph port, handle). The sink's column bindings and its input relation are internal: reach them by reflection from the experiment project.
4. Compare the multiset of recomputed rows with the recorded `Supports[x]` (and, for elements emitted from a `Group`, with `GroupMembers[GroupRefs[x]]`).
5. Time each recomputation; report p50, p99 and the maximum.

Pass. Recomputed lineage equals the recorded lineage for 1,000 of 1,000 elements on both captures. Latency is reported only; the 50 ms p99 target applies to the indexed backward walk built in stage S3, not to the naive evaluation of step 3.

Decides. Equal on all: stage S3 removes stored lineage and keeps support counts. Any difference: list the elements, the graph and the rule; stored lineage stays until the cause is understood.

### A.2 X3: what the event plane saves

Question: how much of the engine's state scales with events, measured by removing evidence elements from the master without changing anything else?

Procedure.
1. Baseline: `sattle replay latest --census > out/experiments/X3-base.txt`; the same for the older capture.
2. Copy the definitions: `cp -R definitions out/experiments/X3-defs`.
3. In `out/experiments/X3-defs/acme.ops.sattle`, in the rules `observedActivityEvents`, `observedWarningEvents` and `observedAudit`, insert `|> where false` before `|> retain on time for 24h` (three places: `allOperations`, the `events` filter, `audit`). If the checker rejects a constant predicate, use `|> retain on time for 1ns` instead and say so in the result. Do not delete rules: downstream graphs keep their bindings and still check.
4. `sattle check --defs out/experiments/X3-defs --update-pins`; it must report no errors.
5. `sattle replay latest --defs out/experiments/X3-defs --census > out/experiments/X3-events-off.txt`; the same for the older capture.
6. From both outputs, record: master entities and relationships (the `graph master` line), entities by type, the `identities:` line (minted, aliased, merged), the census total (objects, MB, GC-reported MB), the census rows `store.graphs`, `interner`, `op:RetainOp`, `op:MatchOp`, `graph.sink.elems`, lineage rows, `identities`, and the replay time from the first line.

Pass. Master entities fall by at least 90% and the GC-reported heap by at least 50% on the newest capture.

Decides. The event plane (D6) is decided; this confirms its saving. Below either threshold: name the census rows that did not shrink and whether they scale with events (they become stage S4 work).

### A.3 X4: fixed-point and compressed telemetry

Question: can every stored telemetry aggregate be an exact `i64` fixed-point value, and how many bytes does a closed window take when compressed per series?

Procedure.
1. Source of values: the engine's telemetry store after a replay, read as `decimal` through the public `InMemoryTelemetryStore.Dump(key)` (decimals per element, property and window). Not the canvas export: `*.telemetry.ndjson` carries JSON numbers printed from doubles.
2. Program `X4-telemetry-blocks`: replay the newest capture and `sattle bench estate --windows 3`'s workload in-process; collect every `(graph, element, property, windowStart, value)`.
3. Exactness: per (graph, property) find the smallest scale `s ≤ 18` such that every value times `10^s` is an integer and fits in `i64`. Report per property: `s`, the number of values, and the values that fit no `s` (with examples). Classify each property as an input aggregate (`count`, `sum`, `min`, `max`, `last`) or a derived value (`mean`, ratios, `rate`, `weightedMean`).
4. Compression: per series, encode the sequence of windows in window order as `i64` fixed-point at the property's `s`: timestamps implicit (window index delta, a varint only where windows are missing), values as zigzag varint of the delta to the previous value, and separately as XOR of consecutive bit patterns with leading and trailing zero counts (Gorilla style). Report average bytes per window per property for both encodings, and the total.
5. Speed: append rate in ns per value for the better encoding, single thread, after warm-up.

Pass.
- Every input aggregate is exact at some `s ≤ 18`. Derived values may fail; they are then recomputed at read time from stored sums and counts, never stored. The engine's own rule then becomes "store sum and count, derive means".
- Compressed size ≤ 8 bytes per window for the four aggregates `count, sum, min, max` of one series (or ≤ 2 bytes per stored value), averaged over all series of the newest capture and of the estate.
- Append ≤ 30 ns per value.

Decides. Pass: stage S4 uses `i64` fixed-point and the better encoding. Exactness fails for an input aggregate: that property keeps `decimal` in a side table, and the property is named in the result.

### A.4 X10: journal of adapted rows

Question: how large is a journal of adapted, dictionary-coded row batches compared with envelope JSON, and does replaying it give the same state?

Procedure.
1. Envelope size: per source directory of the newest capture, total bytes and records (`du -b` per file, records from `SUMMARY.json`).
2. Program `X10-row-journal`: read the capture through the engine's capture reader and adapters in recorded order, exactly as `sattle replay latest` does (same chunking, same logical-time advances). Write every row batch to a segment file: per batch a header (source, row contract, row count, adapter version, the source positions of the batch and the advance controls in between), then per column the values: interned text as `u32` codes into a per-segment dictionary written once, integers and instants as zigzag varints of deltas, decimals as their canonical string in a dictionary when they repeat, else as scaled `i64`, presence as a bitmap. Compress each segment of ≥ 1 MB with a general-purpose codec from the .NET base library (Brotli or ZLib); report both uncompressed and compressed sizes.
3. Replay from the segments: decode batches into `Batch` objects (through the public `RowWriter`) and feed them to the engine with the public `Engine.Ingest` and `Engine.Advance` in the same order with the same advances; print the state digest.
4. Time: wall time of the replay from segments against `sattle replay latest` (median of 5).

Pass.
- State digest from the segments equals the digest of `sattle replay latest` for the same capture and definitions.
- Average compressed size ≤ 40 bytes per adapted row over the whole capture, reported per row contract (`flows`, `flowLogs`, `audit`, `metrics` and the rest).
- Replay from segments at least 3× faster than from envelopes.

Decides. Pass: the journal format of stage S5 is this segment format (versioned header). Digest differs: the adapters are not a pure function of the envelope, a determinism defect to fix before any journal change.

### A.5 X11: flow pre-aggregation

Question: what does per-cluster pre-aggregation of Hubble flows cost per flow and save, and does it reproduce the engine's `Calls` telemetry?

Procedure.
1. Engine reference: `sattle replay latest --out out/experiments/X11-engine`; from `master.telemetry.ndjson` and the final master snapshot, sum `flows` and `drops` per `Calls` relationship (source pod, target pod) per window.
2. Program `X11-flow-aggregator`: read `lab/captures/20261110T140542Z/hubble/flows.ndjson`. For each record with a flow, apply the engine rule `observedCalls` exactly (`acme.ops.sattle` lines 101 to 113): both pod names and namespaces present, 60 s windows on flow time, group by cluster, source namespace and pod, destination namespace and pod, window start; `flows = count`, `drops = count of verdict DROPPED`. Then run a second configuration that also drops `is_reply == true` and keys by workload (`workloads[0].name` or the pod name when absent) and destination port.
3. Measure ns per flow with JSON parsing and without it (parse once into a struct array, then time the aggregation alone).
4. Report: flows in, pairs per window (median, max), pairs in total, for both configurations; the reduction factor.

Pass.
- Configuration 1 equals the engine's `Calls` sums exactly for every pair and window.
- Aggregation without parsing ≤ 50 ns per flow.
- Configuration 2 reduces records by at least 100× (the read-only analysis of this document found 82,893 request-direction flows in 220 workload pairs).

Decides. The pre-aggregation contract (its key and its filter). Note for the definitions, found while writing this: `observedCalls` does not filter reply flows, so a reply from `db` to `api` is counted as a `Calls` edge from the `db` pod to the `api` pod. Configuration 1 versus 2 shows how many reversed edges that creates; whether the rule should drop replies is a definitions question for the engine session and the user.

### A.6 P1: local journal with group commit

Question: what acceptance latency and throughput does a synchronous local journal with group commit reach, and does it recover cleanly after a kill?

Procedure.
1. Program `P1-journal`: one writer thread owns an append-only segment file. Producers enqueue records of 100 bytes into a bounded channel; the writer takes everything queued, waits until the group deadline `T` since the first record of the group, writes all records as length-prefixed frames with a CRC32 each, then flushes to stable storage (`FileStream.Flush(flushToDisk: true)`; on macOS also run a variant with `F_FULLFSYNC` through `fcntl` if reachable, and name which call each run used), then acknowledges every record of the group.
2. Grid: `T ∈ {1, 2, 5, 10} ms` × offered load `{1,000, 10,000, 30,000, 100,000}` records/s, 60 s per cell after 5 s warm-up.
3. Report per cell: acknowledged records/s, fsyncs/s, acceptance latency (enqueue to acknowledgement) p50, p99, max.
4. Crash: at 30,000 records/s and `T = 5 ms`, kill the process with `SIGKILL` at a random time, 20 times. On restart, scan the file, truncate at the first frame with a bad length or CRC, and check that every acknowledged record (the producer logs acknowledged sequence numbers to a separate file, flushed) is present and that no record appears twice.
5. Machine: the engine machine (Apple M1 Max, internal SSD); state that the result is macOS, and repeat on a Linux VM with local NVMe when one is available.

Pass.
- At 30,000 records/s (10× the 10⁶ rate of Section 7.7) with `T ≤ 5 ms`: p99 acceptance latency ≤ 10 ms.
- Crash runs: 20 of 20 recover with every acknowledged record present and none duplicated.

Decides. The group deadline and frame format of the D7 journal (stage S5). Fail on latency: report the smallest `T` and largest load that meet 10 ms; Section 7.7 is then revised, not the decision.
