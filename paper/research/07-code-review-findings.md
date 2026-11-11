# Code review (max) of everything on local main that is not on origin/main, plus the uncommitted engine tree

Review only. Nothing built, run, edited or committed. Method: the `code-review` skill at max, once per area. 47 finder reports (10 angles per area, line scans split by file group), then one verifier vote per candidate (CONFIRMED / PLAUSIBLE / REFUTED), then a gap sweep per area. Reviewer subagents ran on Opus at high effort. "Reached" says whether a shipped definition, fixture or normal use triggers it today. Line numbers are from the working tree (engine) and HEAD (canvas).

Related: `06-code-review-early-note.md` (benchmark validity).

## 1. engine/src

### Correctness, reached today

- `Sattle.Runtime/Host.cs:542` (reader `Sattle.Cli/Server.cs:158`). `Subscription.Queued` is only ever incremented. Every WebSocket subscriber is closed as SlowConsumer on its 10,001st message, even when it drains everything. CONFIRMED.
- `Sattle.Runtime/Operators.cs:643` (with `:683`, `:689`, `Output.cs:113`). Threshold recomputes its state from `initial` over the windows still held. Series retracts windows at the look-back. A key that entered "on" and then stays inside the hysteresis band flips to "off" when the entering windows are 24 h old, although no value went below `below`. Example: `acme.healthmodel.sattle:97`, above 0.05 for 2, below 0.02 for 2, values 0.06, 0.06, then 0.03. CONFIRMED, any run longer than the look-back.
- `Sattle.Runtime/Identity.cs:165` (with `Exprs.cs:107-110`, `Engine.cs:340`). Expressions run over all physical rows and ignore the selection vector. For `id(...)` this has side effects: identities are minted and persisted as bindings for rows a Filter rejected. `acme.healthmodel.sattle:28-46`: every AzureResource of a batch gets a `health.component` identity. CONFIRMED.
- `Sattle.Runtime/StateSnapshot.cs:32` (with `Replay.cs:118`, `Columnar.cs:138-151`). The reflective snapshot skips `Layout` fields. After a seal restore of a live space the `RowWriter`s have a null Layout: the first tail entry throws, `Host.cs:337` catches it and silently replays the whole journal. With an empty tail the next live envelope throws after it was journaled. LiveResumeTests pass through the fallback (they never assert `RestoredFromSeal`). CONFIRMED.
- `Sattle.Runtime/Output.cs:295-306` and `Host.cs:490`. Telemetry writes keep no support count (a later positive overrides, a retraction deletes only on equal value), and the host filters `New is not null`, so window deletions (24 h expiry, rule change, identity move) are never published. Subscribers keep stale windows. CONFIRMED.
- `Sattle.Cli/Server.cs:182` (with `Replay.cs:118`, `Columnar.cs:142`, `Drafts.cs:294`, `Edits.cs:305`). In `serve` the capture replay runs on a background thread and interns into the host's shared `Interner` without the host lock, while preview, edit and commit handlers intern under the lock. A plain Dictionary plus List written from two threads: lost codes, one string with two codes, or a corrupt dictionary. CONFIRMED mechanism, timing dependent.
- `Sattle.Runtime/Exprs.cs:119` (checker `Checker.cs:1024`, `:582`; `Operators.cs:250`, `:361`). `<`, `>` on Text and the tie-break of `last(value, order)` compare interner codes (first-seen order), not text. `acme.tree.sattle:67/110/112` reach it when two curation records on one target tie on rank: the parent then depends on which string was interned first. CONFIRMED. Text `<` in a filter is latent.
- `Sattle.Runtime/Synthetic.cs:22`, `Operators.cs:58`, `Columnar.cs:59`, `Sattle.Cli/Bench.cs:75-76`. A row builder marks every column present. Unset columns read as present code 0. See the early note: `bench estate` measures about 300k phantom relationships. CONFIRMED.
- `Sattle.Language/Catalog.cs:170` (`Checker.cs:54`, `:638`). One checker is reused for all projections of a module and never clears `demands`. A Match reads properties demanded by a same-named operator of another projection (`acme.treehealth`: treeRollup's `els` reads `e.nodeName` per row and never uses it; `acme.healthmodel`: `comps` reads `c.own`). Extra columns per row in every stateful operator downstream. CONFIRMED.
- `Sattle.Language/Refactor.cs:40`. A moved or extracted rule collects the port after `from` but not the one after `via` of a lift. Extracting only `lifted` from `acme.biz` capabilityView yields text that fails with "'master' is not a graph port". CONFIRMED, no test.
- `Sattle.Runtime/Export.cs:296` (uncommitted feature). Thresholds whose parameters are expressions lower to literal 0. The meters document lists every tree signal with above 0, enterAfter 0, below 0, exitAfter 0. CONFIRMED.
- `Sattle.Runtime/Drafts.cs:283` (`:126`, `:264`, `:294`, commit `:148`). Preview, check and refactor diff the draft against head, commit against the draft's base: after another commit the preview shows that commit reverted. Preview also runs with late horizon 3 while `serve` of a capture runs with 1,000,000 (`Program.cs:295`). CONFIRMED.
- `Sattle.Cli/Server.cs:59` (`:64`, `:104-126`, `:45`). `Access-Control-Allow-Origin: *` on every route including the mutating POSTs: a foreign page in the user's browser can read `/space` and commit definitions or edits to a local engine. Bodies are parsed outside the error guard (malformed JSON gives a bare 500). Every KeyNotFoundException becomes 404 UnknownGraph. CONFIRMED.

### Correctness, latent (no shipped definition or fixture reaches it)

- `Host.cs:353-358` (`:886`, `:331`). After a seal restore, inputs are rebuilt from journal kinds `r`, `a`, `l`. Live input is journaled only as `e` and `F`. A definition commit then re-activates with no live rows and the diff deletes the live state. Today masked by the StateSnapshot defect above. CONFIRMED.
- `Adapters.cs:385` (`:397`, `:340`, `:217`, `:486`; `Host.cs:239-243`). A value of the wrong JSON type (authoring rule with `"above":"80"`) throws out of the adapter. Replay stops. In live mode the envelope is journaled before it is applied, so every restart throws again and the space cannot start. CONFIRMED.
- `Operators.cs:435` (`:423`, `:279`). `countDistinct` takes its index from the `dist` counter but stores its last emitted value in `LastL`, laid out by the `l` counter. `aggregate { n: count(), d: countDistinct(x) }`: the retraction row carries n = distinct count and cancels nothing. Shipped uses have a single countDistinct. CONFIRMED.
- `Operators.cs:460` (`:535`, `:599`, `:671`, `:373`; `Columnar.cs:55`; `Checker.cs:544`, `:570`). A Decimal group, rollup or threshold key, or `countDistinct` over a Decimal, type-checks and then throws IndexOutOfRangeException on the first row, with operator state half updated. CONFIRMED.
- `Operators.cs:753`, `:818`, `:837`, `:927`. Join, Distinct and Traverse key decimals by `decimal.GetBits` (scale included): 2.5 and 2.50 do not join, Distinct keeps both. CONFIRMED.
- `Operators.cs:948`. Traverse breaks at max depth before adding the frontier to `Touched`: elements at exactly max depth never trigger a recompute (stale reads, a late entity never appears). Shipped bounds are 0..12 and 0..16. CONFIRMED.
- `Operators.cs:649`, `:675-681`; `Checker.cs:428-429`; `Adapters.cs:384-386` (uncommitted). Data-driven thresholds skip the checker's `above > below` and `enterAfter >= 1` checks and nothing validates them at run time: an authored rule with enterAfter 0 is on at the first window.
- `Output.cs:94-95`, `:110-113`. Series stores a window change older than the look-back without emitting it and retracts it at the next advance: rows retracted that were never inserted. Plausible in replay (late horizon 1,000,000) and after an identity move.
- `Adapters.cs:106` (`:102`, `:127`, `:167`, uncommitted). Kubernetes adapter state is keyed by uid and path without the cluster, and retractions are stamped with the current envelope's cluster: with two clusters a relist-end of one retracts the other's pods with rows that cancel nothing.
- `Operators.cs:1109`. Retain removes the whole held entry on any retraction, whatever the weights.
- `Engine.cs:113`. A windowed Group finds its Window by width: two windows of the same width with different late policies collide.
- Language checker, all CONFIRMED and latent: `Checker.cs:1094` (`if` drops the condition's role, so telemetry leaks into data properties, filters and keys); `Checker.cs:966` with `Typing.cs:141` (`null` is typed required and fits any kind); `Lowering.cs:499` (steps after a fixpoint's `max N` are dropped silently); `Typing.cs:46` with `Checker.cs:337/407/433/617` (duplicate output names throw ArgumentException out of the catalog load: bare CLI message, 500 on the server, no file or line); `Checker.cs:76/658` (`List<Entity>`, `List<Any>` throw); `Lowering.cs:240` with `Kernel.cs:258` (same-id declarations merge silently: two key constraints become one composite key); `Checker.cs:654` (`Ref<T>` target never enforced); `Checker.cs:1143` (a projection cannot be instantiated twice in a space); `Printer.cs:84` against `Syntax.cs:50` (escapes do not round-trip; `above (-x)` prints unparsable).
- `Edits.cs:73` (qualified type names from the export are rejected by edit decoding), `Edits.cs:242` (lens create hard-codes a `status` = "retired" property; without it a 500), `Drafts.cs:70` (two edits on one key merged without the guard chain), `Server.cs:33` (revision snapshots revalidate to 304 although their body depends on other graphs at head), `Host.cs:507` against `Export.cs:215` (live and exported telemetry ticks are numbered differently; the canvas places frames by time, so no visible effect today), `Engine.cs:334-343` (state applied and pending bindings cleared before the store commit, no rollback).
- Refuted: WindowedGroup windowEnd mismatch (`Operators.cs:548`).

### Memory and compute (all CONFIRMED unless noted)

Where the measured numbers come from, reconstructed from the code (not measured by me):

- About 15 KB per master entity: master path about 5 KB (compact graph state plus the store's second graph 0.6, store versions and edits 0.4, identity binding with a 64-char hex hash and a components JSON string 0.5, output layer with lineage and endpoint index 1, interner and identity strings 1, about one relationship 1.5), then re-emission into 3 to 4 derived graphs at about 2.5 KB of layers each plus 0.5 to 1 KB of join and group rows.
- About 45 KB per health-tree element: traverse reach 5 to 7 KB, joins 3 to 4 KB, sorted-dictionary aggregates about 3 KB, three graphs each holding the element with its edge, store versions, contributions, supports and lineage at 5 to 7 KB each, signals and series 3 to 5 KB.
- About 800 B per telemetry window: one value is held 4 to 6 times (telemetry store, the change log, Series windows, the Series arrangement at full row width, Threshold windows, and again in the next graph).

Structures:

- `Stores.cs:156-200`. The default store (`Engine.cs:12`, `Host.cs:32`) keeps per graph a second full `Graph`, every element version, adjacency intervals, every patch and the bindings. Production code calls only `Head` and `Commit`. `Read`, `ScanAdjacency`, `Seal`, `OpenSnapshot` are called from tests and the latency bench only, `Bindings(GraphKey)` by nobody. A write-only history copy of every graph, in the baseline.
- `Sattle.Core/Graph.cs:338` (`:375`, `:385`, `:191-195`). `Apply` removes before it puts, `Remove` zeroes the props offset, so the in-place reuse branch is dead: every property update appends a block that is never reclaimed. `IdPool` never frees ids. PERF.md:41 claims in-place update.
- `Operators.cs:144-257`, `:602`, `:462`. `KeyTable` has no removal. Rollup keys slots by (key, windowStart): 1,440 slots per key per day, forever. Group keeps slots of emptied groups. The plan's memory bound (`Engine.cs:147`) says the opposite.
- `Operators.cs:905-1022` (Traverse). Per start: a list of freshly materialised `Entity` objects for every reached element, a `HashSet<string>` of touched ids, a signature string over all reached ids and read values. Every epoch sorts all starts and tests each against the patch (`:1008-1011`), even for a telemetry-only epoch. A changed reach is retracted and re-inserted whole (`:1012-1018`), while the plan text says only the difference is emitted.
- `Output.cs:132-354`. Per emitted element: an element state with a contribution list (props again), a lineage record, supports as a nested dictionary of row keys built as strings, group member strings, an endpoint index (`relsAt`) that is filled for every graph though only the master is ever remapped. None is pruned when an element goes (`:266`).
- `Operators.cs:788-861`, `:735`, `:896`, `:1054`. Join, Traverse, Distinct and Retain store each row as `Cell[]` (bool, long, decimal: 32 B per cell) plus a key of 4 longs per cell plus key and entry objects plus an inner dictionary per join key. About 560 B for a 7-column row against 56 B packed. Each join side holds its whole input. Nothing is shared between operators reading the same input.
- `Operators.cs:244-255`. Each min, max, last and countDistinct allocates a sorted dictionary per group slot with a comparer per instance.
- `Sattle.Core/Values.cs:12`, `Model.cs:9`. Values are heap records (an Integer boxes a BigInteger). The object-form `Element` is the currency between layers, so the compact graph is bypassed: every read materialises strings and values (`Graph.cs:204`), and output layer, store versions, patches and operators each hold their own object copy.
- `Stores.cs:523`, `:560-563`. Every telemetry append scans all series for retention and removes from the front of a sorted list. The change log keeps up to 4,000,000 records per graph although its only reader reads right after appending.
- `Output.cs:108-110`. Series expiry copies all elements and walks all retained windows of each on every Advance: elements times windows per advance, whatever expired.
- `Operators.cs:641-658`, `:673`. Threshold replays every retained window per touched key and allocates a string key per row.
- `Output.cs:239-267`, `:339-354`. A window close re-resolves elements whose state did not change (list, sort, materialise the before state, deep equals, no edit). Probably the main constant of the 18.5 microseconds per series.
- `Match.cs:124-133`, `Operators.cs:470`, `:610`. No-op deltas are never consolidated: an irrelevant property change emits retract plus insert for every match and flows through the whole plan.
- `Engine.cs:281-357`. Per epoch every graph runs every operator, each changed element is materialised and compared three times (flush, graph check, store), the store commit and the host's JSON for every patch are synchronous inside the loop.
- `Host.cs:443`. All input batches are retained in memory up to 40M rows, next to the journal in durable mode. `Journal.cs:109`: the stored log loaded for recovery is never released. `Host.cs:486-533`, `Export.cs:19`: served history keeps patches and JSON text.

Structure and honesty:

- No shared row or multiset primitive. Row keys and consolidation exist about eight ways (`Operators.cs:735`, `:790`, `:898`, `:673`; `Output.cs:137`, `:292`; Series and Retain by linear search), each with its own equality (absent cells, decimal scale) and its own retraction rule. Several use compare-and-remove where weights are needed (Threshold, FlushTelemetry). This is where the order-dependence bugs come from.
- `Host.cs` with `Drafts.cs` and `Edits.cs` (about 1,650 lines, one class, one lock): journal, seals, live input, drafts, edits, subscriptions, reads. The journal is decoded in three places. Flags (`suppress`, `recovering`, `retained`, `inputsInJournal`) carry the semantics.
- `StateSnapshot.cs`: reflection over private fields, skipping by type. The seal digest covers graphs and telemetry only, so lost operator state passes.
- Looks complete but is not: `Engine.cs:129-147` (fused chains computed and printed, never used; the memory bound sentence), strategy strings that contradict the code, the kernel's `Delta` operator, `constraint key` parsed and never enforced, 21 `_ = x;` discards hiding unused parameters, `ITelemetryStore` with one implementation and four downcasts.
- Sound: `Sattle.Core` (algebra, values, kernel table, the struct-of-arrays graph), the columnar batch, Filter, Map, Window, the windowed group and `KeyTable` itself, the parser, lowering and printer as a split.

## 2. engine/tests and engine/definitions

Tests (all CONFIRMED unless noted):

- `HostTests.cs:116` (`:147-155`, `:184`). Under the comment that patches replay to the final graph there is a loop with an empty body. The event-stream test asserts over lists that may be empty. Default run.
- `Gen.cs:62` with `HotGraphTests.cs:22` and `ReferenceGraph.cs:47-70`. The generator keeps only patches the hot graph's own `Check` accepts, so the oracle comparison is null against null. The reference graph's `Check` and `Encode` are copies of the engine's. A wrong rejection is retried silently.
- `StoreTests.cs:246`, `:264` and 9 more methods (`ReplayTests.cs:93`, `:106`, `HealthTests.cs:54`, `LiftTests.cs:74`, `LiveReproducibilityTests.cs:86`, `ServeReplayTests.cs:53`, `ReferencesTests.cs:124`, placeholder rows in BatchInvariance and Retention). They return early when a capture, overlay or emulator is missing and report passed. The README says skipped.
- `fast.runsettings:4`. The default run excludes 11 classes (Draft, Lens, LiveReproducibility, ForwarderMarker, LiveCrash, Refactor, LiveResume, RetentionOfHistory, Restart, Snapshot, Seal). `HealthTreeTests` has no Slow trait and replays the wide estate 43 times, with nothing shared.
- `HealthTests.cs:44` (uncommitted), `EstateTests.cs:110`. No test checks healthTree, healthReport, treeRollup or treeSignals against their ontology. The "every graph has entities" theory lists 9 of the 14 graphs.
- `LensTests.cs:144`. GetPut sends only edits with before equal to after. `Edits.cs:114` drops them before any lens evaluation. `:162` asserts `Equals(false, false)`.
- `SealTests.cs:97-98`, `:137`, `RestartTests.cs:131`. The foreign-build seal case also carries a bad digest, so the build check is not isolated. No test changes the content of a stored commit, so `DurableView.SameChange` is never exercised on a mismatch.
- `LiveReproducibilityTests.cs:27-44`. The test writes the stream itself and drives the replay. `LiveInput`, `SpaceHost.LiveEnvelope` and `FileRecording` are never replayed. `Live.cs:51` would count a torn last line as an envelope (plausible).
- `HealthTreeTests.cs:283` (`IndexOf` of a missing row is -1, so "worst first" passes), `:202` (Unknown mapped to Healthy before comparing).
- `IdentityMergeTests.cs:33` in 588a256: assertions loosened in the commit that changed `Identity.cs`.
- `ProjectionEdgesTests.cs:165`, `Repo.cs:43`. Import pins are masked before comparing and updated on every load. The fixture copies of the space file carry pins that differ from the tracked one.
- Cannot fail: `ValueTests.cs:96`, `KernelTests.cs:16`, `StoreTests.cs:308`, `OperatorTests.cs:263` (one row).
- Coverage by behaviour: no operator test splits batches or reorders retract and insert inside a batch. Threshold, Window, Series, Rollup, Retain and Lift have no small direct test. Most expected values come from the engine under another configuration. Many checker rules have no negative test. 190 facts, 101 theories.
- Refuted: BatchInvariance chunk 100000 row as a gap, LiftTests discarded parameter as a gap.

Definitions (all CONFIRMED):

- `acme.ops.sattle:318` with `acme.tree.sattle:156`, `acme.healthmodel.sattle:106`. The warnings meter has a window only when a warning occurred. `below 0` can never hold, so one warning keeps a pod Degraded until the window is 24 h old, then the signal is retracted and the pod is Unknown, never Healthy. A pod without warnings has no signal and is not monitored.
- `acme.tree.sattle:117` with `Export.cs:33` (uncommitted). acme.tree declares Signal, Measures, Evidence, EvidenceFor, which acme.health declares too. The export then qualifies them: the existing health graphs change their wire type names, and the canvas stand-in (`canvas/src/source/tree-standin.ts:10`) no longer matches.
- `acme.treehealth.sattle:64`. `reach` traverses to descendants from every element. Traversing to ancestors gives the same pairs with per-start state bounded by depth and a new leaf costing a handful of rows. `:26`, `:41-43`, `:79`: three full copies of the tree (treeStructure, treeRollup, healthTree), signals and their series twice.
- `acme.ops.sattle:291`. Every metric sample is windowed and stored twice (named property and Meter reading), pod warnings too.
- `acme.healthmodel.sattle:21`. The workload model filters on namespace, not on cluster: two clusters with the same namespace are mixed. Latent in fixtures.
- `acme.tree.sattle:64`. The cluster join needs a Resource Graph row for the node's machine. AKS scale-set nodes have none (the README says so): on such a cluster the cluster is two unconnected elements. Latent in fixtures.
- Intended and documented, but worth knowing: share roll-up dilutes a node's own failure so a node can be Degraded under Unhealthy ancestors (`acme.treehealth.sattle:74`), sums over lifted edges count a flow once per image (`acme.svc.sattle:62`).
- Language: no outer join (the rank trick, five `last` for one left join), `union` of two inputs only, no reusable rule (three verbatim meter pipelines, seven signal blocks, six window-mean rules).

## 3. canvas/src and canvas/scripts (HEAD)

- `source/engine.ts:244-246`. A telemetry batch older than the newest window seen for the graph is dropped. The protocol pushes revised windows again. The user keeps the old value until reload. Reached (rules use `late revise`).
- `app/data-pane.ts:328` (`:201`). The patch path writes visibility for a new slot past the end of a typed array sized at load. The new node and its edges are not drawn and search skips them until a full refresh. Reached on the first growing live patch.
- `state/effects.ts:75` with `:87`, `source/engine.ts:255-269`. Any resync (a gap, another client committing a new graph) reloads once per graph, and each reload drops the editor session: the author's draft is orphaned on the engine without notice. Abandoned loads keep delivering patches into the new workspace.
- `app/tree-model.ts:51`. Severity mixes the engine's 0..2 with a role scale 1..5: Unknown (3) sorts above Unhealthy (2) and is named as the driver. Reached on the shipped health graph.
- `ui/EditorView.tsx:113`. Blur cancels the pending 700 ms debounce: typing in the Text tab and clicking Commit within 0.7 s loses the edit.
- `editor/commands.ts:199-200`. New child ids come from the child count: after deleting a middle row every add collides and the engine rejects it.
- `ui/EditorView.tsx:374-418`. The root key handler swallows Enter on buttons and dialog buttons, and Delete or Backspace on a focused button deletes the selected operators.
- `app/data-pane.ts:417`. Flag writes drop the reference bit: after a hover a stub (an endpoint the graph does not hold) is drawn as a real entity.
- `app/workspace.ts:113`. A live patch moves a graph to now while the shared timeline is scrubbed back, and overrides the highlight mode.
- `editor/session.ts:96-108` (restore of an equal release, then the next edit is reported failed), `:55-59` (commands not serialised: double undo undoes once).
- `ui/editor/NewProjection.tsx:38` (graph name is the first letter of the name), `state/editor-effects.ts:39` (the Editor tab on the demo source spins forever, the error is overwritten), `ui/Filters.tsx:157` (a threshold typed with a decimal comma becomes NaN: everything is drawn at warning).
- `core/telemetry.ts:67` (a reused slot inherits the deleted element's value; no data reads as the minimum, so not as unknown), `core/time-axis.ts:24` (a graph with more patches than windows puts revisions at seconds 1..n against epoch windows).
- `source/engine.ts:233-237` (a failed gap fill leaves the graph frozen while the status says live; latent), `:251-257` (ontology and roles are not re-read after an activation).
- `source/simulated.ts`, `synthetic.ts` (no dispose: three workers and an interval leak per reset).
- `state/effects.ts:86` (telemetry and layout ticks bump the graph version four times a second: tree model, lists and counts recompute over the whole graph; PERF.md:150 says they do not), `ui/Inspector.tsx:58` (quadratic in a node's degree, per render), `ui/EditorView.tsx:211` (the preview pane is rebuilt on every editor render).
- `scripts/a11y-cdp.mjs:43`, `:263` (the focus-ring check always passes, skipped checks exit 0), `scripts/perf-cdp.mjs` (no thresholds).
- Dead: `core/projection` (about 800 of 828 lines reachable from tests only), `GraphSource.evaluate`, `DefinitionService.check/commit`, the bezier wire mode in the renderer, about 200 lines of unused members.
- Tests: `canvas/tests` is 3,409 lines of test code (the rest is JSON data). 218 plain tests, 106 parametrized. Tree UI tests run on a hand-written source. No shared contract test across the source implementations.
- Sound: `core/graph.ts`, `diff.ts`, `model.ts`, `wire.ts`, the reducer, `tree-model` and `color-by` as pure modules, render and layout (no correctness defect found there).

## 4. lab

See the appended section at the end of this file (verification finished last).

## 4. lab (appended)

No identifier value is repeated here: file, line and field only. All CONFIRMED by a verifier unless noted.

Before any push (none of these commits is on origin):

- History. `lab/capture/lib.sh:6-8` in commit bbb778e holds three hard-coded defaults (two resource group names, one cluster name). Later commits removed them, the commit stays in `origin/main..HEAD`. A reviewer also reports the resource group name in the fixtures as they were before the rescrub commit 085cf33 (`lab/fixtures/hubble/flows.ndjson`, `kubernetes/list.ndjson`, `kubernetes/watch.ndjson`, `eventhub/sattle-ingest.ndjson`). I checked the shape of the lib.sh lines myself with the values masked, not the fixtures.
- Tracked fixtures at HEAD still carry values the scrub does not cover: 32-hex ids without dashes in `internalId` (`lab/fixtures/arg/resources.ndjson:7-8`), `roleAssignmentId` (`lab/fixtures/eventhub/sattle-activity.ndjson:1-3`), node `machineID` (`lab/fixtures/kubernetes/list.ndjson:2`, `watch.ndjson:16`); a free-text tag value that looks like an organisation code (`lab/fixtures/arg/resourcecontainers.ndjson:1`, `tags`); a short prefix kept verbatim in a resource group alias that looks like initials (`resourcecontainers.ndjson:3`, `resources.ndjson:7-10`); token claims `rh`, `xms_ftd`, `xms_tcdt` (`lab/fixtures/activity-log/backfill.ndjson:1-10`, low confidence on what they contain). `scrub.mjs verify` reuses the scrub's own detectors (`:16`, `:190-193`), so it cannot see any of this. Whether these values are sensitive is the owner's call.

Scrub:

- `lab/fixtures/scrub.mjs:78-100`. Names are harvested from any ARM-like path segment and replaced as case-insensitive substrings over the serialised line, keys included. Tracked fixtures are damaged: a watch event type, ARG resource types, JSON keys (for example the probe threshold key), the operation `write`, every Azure metric name but one, Prometheus metric names. Effect on the engine: the four Azure metric rules of `acme.ops.sattle` (`:177`, `:224`, `:236`, `:248`) never match on `lab/fixtures`, and the tests that replay them assert only counts above zero.
- `scrub.mjs:68`, `:100`. Literal aliases hash the matched text in its original case while matching ignores case: one subscription has two aliases (167 lower-case, 136 upper-case occurrences). Activity rows from the diagnostic hub no longer join to the same resource from the management API. Subscription and tenant ids become non-GUIDs although `CAPTURE-FORMAT.md:120` says GUIDs map to GUIDs.
- `scrub.mjs:209`. The main guard compares `argv[1]` with a URL pathname: in a checkout path with a space `build` and `verify` do nothing and exit 0, after `build.sh:18` already deleted the fixtures (not separately verified).
- No test exists for the scrubber, the only pure core in `lab/`.

Capture and its contract with the engine:

- `engine/src/Sattle.Runtime/Host.cs:234` with `lab/capture/lib/envelope.mjs:17` and `Adapters.cs:103-144`. Live input drops an envelope whose key was seen (20,000 key window). A relist after a forwarder restart or a watch expiry repeats unchanged objects with the same key (kind, uid, resourceVersion): they are dropped as duplicates, never reach the relist's seen set, and relist-end then retracts every unchanged pod, service, node, deployment and slice. Live mode, normal use, no test.
- `Adapters.cs:334` against `lab/capture/prometheus-queries.json:6-17`. The adapter keeps only the `instance` label: samples of queries that aggregate it away are dropped without a count, all kube-state-metrics series collapse into one meter per metric attached to a node that does not exist, series split by direction or reason are averaged.
- `acme.ops.sattle:138-142` with `Adapters.cs:425`, `:516`. The same Activity Log event arrives through the management backfill and the diagnostic hub under different ids and is counted twice on real captures.
- `Adapters.cs:263`. Hubble writes `num_events_lost` (uint64) as a JSON string. The adapter accepts a number only: a real loss record counts 0. The test feeds a number.
- `lab/capture/capture.sh:32`. `wait` on several pids returns the last one's status (the churn job): a churn failure aborts the capture before the pulls and the summary, a dead watch or consumer is not noticed. `sources/kubernetes.sh:33`: one kubectl watch per kind, stderr discarded, no resume.
- `lab/capture/eh-consume.mjs:47`. The consumer ends only after 10 s of quiet: with a forwarder running next to the capture (as the README allows) the capture never ends.
- `lab/capture/lib/sink.mjs:23-30`, `eh-forward.mjs:101-127`. Dropped events are the oldest but the marker is appended behind the queue with the current time, so the wrong windows are marked partial. A partly failed send re-queues batches the hub already accepted. On stop the queue and the stop marker can be lost with exit 0. Mirror hub drops are booked on the ingest stream. `eh-forward.mjs:43-44`: a non-JSON line is dropped without a marker (reviewer finding, not separately verified).
- Two capture stacks do the same job differently: shell watchers to files (23 kinds, no relist) and the Node forwarder (8 kinds, relist).

Hooks and infra:

- `lab/hooks/preprovision.sh:12-16`. `azd env set X "$(az ...)"` does not abort when `az` fails: empty values are stored and provisioning fails later with an unrelated error.
- `lab/capture/sources/flowlogs.sh:21`, `:25`. The user's storage token is passed on curl's command line inside a pod, against the script's own comment at `:4`. The image is referenced by tag (`flowlog-reader.yaml:13`).
- `lab/hooks/postdown.sh:3`, `:15`. No `-e`, ends in `exit 0`: with an expired login every undo step fails and `azd down` reports success while billed features and a subscription diagnostic setting stay. `lab/infra/main.bicep:21-23`: names derive from the subscription only, two azd environments share the group, and an override to an existing group would be deleted by `azd down` (latent).
- Sound: `lib/envelope.mjs`, `wrap.mjs`, the forwarder's structure (bounded sinks, supervisor, markers), the Bicep (Entra-only access, one resource group).

### Lab addendum: history (checked by the review session itself, values never printed)

- The two default names of `lab/capture/lib.sh:6-7` at bbb778e (a resource group name and a cluster name) occur in 35 of the unpushed commits, bbb778e through 6f784c9: under `lab/capture`, `lab/infra`, `lab/README.md`, `lab/teardown.sh`, `lab/hooks`, from 4f53d4a also under `engine/definitions`, from 9d0fff1 also under `engine/tests`. Tracked files at HEAD: none. Working tree: none. A push of `main` as it is publishes them. Removing them needs a history rewrite or a squash, which is the owner's decision.
- Sweep additions, not separately verified: diagnostic settings and the flow log have fixed names per subscription and cluster, so two azd environments overwrite each other and either teardown removes both (`lab/hooks/postprovision.sh:9`, `:13`, `:18`); the live Azure metrics poll (300 s cadence, 600 s range) against a late horizon of 3 windows drops the completed half of each poll (`lab/capture/eh-forward.mjs:94`); scrub literals come from the current login and azd environment, not from the capture being scrubbed (`lab/fixtures/build.sh:8`); role assignments hard-code principal type User (`lab/infra/modules/lab.bicep:59`); base64 values are not decoded by the scrub (`lab/fixtures/arg/pages.ndjson:1-4`, low sensitivity).

### Tests and definitions addendum (sweep, not separately verified)

- `engine/tests/Sattle.Tests/RollUpPropagationTests.cs:17`, `HealthTreeTests.cs:177`, `:193`. The expected end states hold only because the warnings signal never exits. Fixing that defect fails these tests: they pin the bug.
- `engine/definitions/acme.tree.sattle:46`. Resource Graph container rows for resource groups pass the filter and become resource elements. The builders never produce such rows. On a real capture each resource group appears twice.
- `engine/definitions/acme.healthmodel.sattle:122`, `:132`, `:143`, `:153`, `:165`. Five of the seven signal pipelines (memory, VIP, server errors, denied flows, node drop rate) get no data in any fixture and no test names them.
- `engine/definitions/sattle.infra.sattle:37`. The pod name identity (cluster, namespace, name) is not unique over time. The link at confidence 1.0 merges a recreated StatefulSet pod with its predecessor for good.
- `RetentionTests.cs:99` (the chunk 100000 row compares a cached result with itself), `DocumentTests.cs:27` (expected digest computed with the same helper), `EvidenceAdapterTests.cs:40` (claims case-insensitivity, both inputs have the same case).

### engine/src addendum (gap sweep)

- `engine/src/Sattle.Runtime/Host.cs:923` with `Sattle.Cli/Program.cs:93-106`, `:311`. Checked by the review session by reading. `ReadDefinitions` puts the overlay's text under the tracked file's path. With `--write-definitions` a commit writes every source entry back to its path. So `serve <capture under lab/captures> --write-definitions` (which applies the gitignored space overlay with the real workload arguments on its own) followed by the first definition commit from the canvas overwrites the tracked `engine/definitions/acme.space.sattle` with the overlay text. The doc comment at `Program.cs:93` states the opposite. This is one way real names reach tracked definition files (see the lab addendum on history).
- Not separately verified: `Program.cs:239` (`replay` attaches the canvas export without `--out` and so keeps every patch and window change for the whole run, in the census as "canvas export"); `Host.cs:520` (the telemetry period lookup rebuilds an ontology view per window and property on every commit event); `Server.cs:178` (a restored host started without `--live` writes an end marker into a live space's journal); `Drafts.cs:83-90` (each draft clones the catalog graph, no expiry; the canvas orphans one per resync); `Exprs.cs:211` (`if` evaluates both branches over all rows, so a guarded division still counts division-by-zero faults: the "runtime faults" figure of a replay is inflated by `acme.svc.sattle:57` and `acme.biz.sattle:55`).

### canvas addendum (gap sweep, not separately verified)

- `canvas/src/ui/Elements.tsx:40`. Rows are capped at 300 in slot order before the sortable grid sees them: sorting by state sorts only that subset, so Unhealthy elements outside the first 300 slots never show.
- `canvas/src/app/data-pane.ts:246`, `:266`. On the canvas a health role is a colour only (danger edges are dashed, as are aggregate and stub edges). At overview zoom labels with the role glyph are culled.
- `canvas/src/app/telemetry-hub.ts:118` with `render/renderer.ts:516`. Warning is decided on 8-bit quantised values in two different ways (bytes on the GPU, dequantised values in the Inspector): near the threshold the canvas, the Inspector and the engine disagree.
- `canvas/src/editor/expr.ts:14-38`. The editor's expression parser rejects syntax the engine prints (`a ++ b`, duration literals).
- `canvas/src/core/graph.ts:81-88`. Edges parked while the stub cap (5,000) was reached are dropped from the pending list when their endpoint arrives and stay unresolved.
- `canvas/src/state/reducer.ts:28`. The rejected counter is capped at 200 and cleared by a resync. `canvas/src/app/history.ts:37`: a live session keeps every patch, every viewed document revision and every rejection. `canvas/src/ui/DocumentView.tsx:42-56`: a stale document shows while switching between two document graphs at the same revision number.

## Verdict per area

engine/src
- Sound to build on: `Sattle.Core` (algebra, values, kernel table, the struct-of-arrays graph), the parser, lowering and printer, the columnar batch, the linear operators, `KeyTable`. The design is coherent and follows the paper. It is not filler code: few stubs, comments mostly true.
- Not sound: the state of the stateful operators (Traverse, Threshold, Series, the row stores of Join and Distinct), the output layer, the default store, the host, the snapshot.
- Worst structural problems: (1) the object-form element is the currency between layers, so each element is held 3 to 6 times as objects and the compact graph is bypassed; (2) no shared weighted row primitive, so every operator has its own keys, equality and retraction rule, which is where the order-dependent results come from; (3) state is append-only almost everywhere (interner, id pool, props pool, key tables, output tables, store history); (4) one host class with one lock and flags for journal, seals, live input, drafts and serving.
- Cheaper to rewrite than to repair: the in-memory store, Traverse (or a fold over the hierarchy in its place), Threshold, the output layer's lineage and support tables, row storage of Join, Series, Distinct and Retain as one packed arrangement, the reflective snapshot as explicit per-operator state codecs, the host's journal and input handling.
- As a semantic reference for an efficient design it is usable only after the defects that are reached today are fixed (hysteresis, identity minting, text ordering, telemetry deletions).

engine/tests and definitions
- The suite protects less than its size suggests: 11 classes are outside the default run, 13 cases pass without running, several central assertions cannot fail, and expected values mostly come from the engine itself. There is no independent oracle for operators or definitions.
- It would protect a change of data layout in part: algebra, snapshot round trip, batch invariance and the lift comparison do not depend on layout. About 40 sites read the telemetry store and row writers directly and would have to change with it.
- Cheaper to rewrite: `HealthTreeTests` (43 full replays, pins a known defect), the reference graph as a real reference evaluator, the per-file replay helpers as one harness.
- Definitions: `acme.svc`, `acme.biz`, `acme.deployhealth` are sound. `acme.ops` and `acme.healthmodel` are paste-expanded. The tree roll-up (all pairs to descendants, three graphs) is the main cost driver of the 45 KB and is cheaper to rewrite than to tune.

canvas
- Sound: `core/graph`, `diff`, `model`, `wire`, the reducer, the pure models, render and layout.
- Not sound: the engine source (five per-graph maps kept consistent by callback order), the data pane (646 lines, parallel slot arrays), workspace and editor lifecycle (no generation on events, no cancellation), the editor view.
- 17 confirmed defects a user can hit today, several of them the worst kind for this tool: a wrong or stale state shown, a lost edit.
- Cheaper to rewrite: the engine source as a small per-graph state machine, the data pane's visibility and scene sync as one tested calculation. Delete: `core/projection` (about 800 lines only tests reach).

lab
- Sound: the Node core (envelope, sink, supervisor, watch) and the Bicep.
- Not sound: the scrubber (text substitution, circular verify, no test) and the fixtures it produced, the shell orchestration (failures hidden).
- Rewrite: the scrubber as a field-aware walk over parsed JSON with case-normalised aliases, an independent verify and tests.

## The five changes to make first

1. Before any push: decide what to do about the 35 commits that carry the lab's names, fix the definition write-back of overlays (`Host.cs:923`), rewrite the scrubber and regenerate the fixtures.
2. Make the measurements valid before the efficiency concept relies on them: unset columns must be absent (`Operators.cs:58`), fix the restart bench passes, and measure at steady state (at least one full look-back of windows), where Series expiry, Threshold replay, Rollup slots and the telemetry log dominate.
3. One packed, weighted row arrangement with typed columns and eviction, shared by Join, Distinct, Series, Retain, Traverse and Threshold, with an explicit save and load per operator. It removes the eight keying variants, the order-dependent results, the decimal key defects, most per-row memory and the reflective snapshot.
4. One copy per element: a head-only default store, working in-place property updates, handles of the compact graph as the currency on the hot path, pruned output tables, and the tree roll-up rewritten (towards ancestors or as a fold, in one graph).
5. Fix the defects reached today with one test each, and give the suite an oracle: the subscriber counter, hysteresis, identity minting under a selection, seal restore of live spaces, telemetry deletions, the interner lock, text ordering; operator tests with retraction and batch splits; real skips; in the canvas the dropped revised windows, the invisible new nodes and the draft lost on resync.
