# Engine wire protocol (authoritative)

The contract between `sattle serve` and the canvas. Everything is JSON over HTTP and one WebSocket. Version `3` (sections marked **v3** are new or changed since 2). The canvas implements against this file; where the engine differs, the engine is wrong.

## Conventions
- Base URL `http://127.0.0.1:5080` (`--port`). All bodies UTF-8 JSON. Errors are `{ "error": { "code", "message", ... } }` with an HTTP status.
- **Revisions** are positions in a per-graph log. On the wire they are decimal strings (`"17"`), never JSON numbers. `"0"` is the empty genesis state.
- **Instants** are RFC 3339 UTC strings with 0 to 9 fractional digits (`"2026-11-10T10:41:00Z"`). **Durations and counts that can exceed 2^53** are decimal strings. Telemetry values are JSON numbers (doubles) for display; exact decimals are in `valueText`.
- **Graph ids**: `catalog` (the definitions), plus the graphs of the active space (`master`, `serviceTree`, `capabilities`, ...). One tenant per server.
- **Element ids** of data graphs are opaque. Element ids of `catalog` are definition ids (`d:acme.svc/projection:serviceDeps/op:deps#0`).
- **Shapes** of snapshots, patches, ontologies, descriptors and graph values are the shapes in `brainstorm/contracts/schemas` (`EntityState`, `RelationshipState`, `GraphPatch`, `GraphDescriptor`, `OntologyDefinition`) with revisions as strings in `{ "value": "17" }`. They are what the canvas `StaticSource` reads today. The kernel ontology exports its `Any` value kind (`Literal.value`) as `text`. Type and property ids in them are names (`Pod`, `flows`), qualified `module.name` when two modules use the same name.
- The server answers CORS preflights and sends `Access-Control-Allow-Origin: *` (it binds to 127.0.0.1 only).
- Reads are consistent per request: the engine holds one lock per request. Writes are serialized.

## Space and graphs
`GET /space`
```json
{ "space": "operations", "protocol": "3",
  "catalogRevision": "10", "kernelDigest": "sha256:...",
  "graphs": [
    { "id": "catalog", "role": "catalog", "head": "10", "ontology": "sattle.kernel", "inputs": [], "direction": "readonly", "editable": false },
    { "id": "master", "role": "master", "head": "146", "ontology": "acme.ops", "inputs": [], "direction": "mixed", "editable": true,
      "descriptor": { }, "projection": "ingest" },
    { "id": "serviceTree", "role": "projection", "head": "44", "ontology": "acme.svc", "inputs": ["master"], "direction": "forward", "editable": false, "projection": "serviceDeps" }
  ],
  "input": { "mode": "replay|live", "logicalTime": "2026-11-10T10:41:00Z", "rows": "243457" } }
```
- `direction` is `forward`, `lens`, `mixed` (a projection with `readonly` and `bijective` or `lens` rules) or `readonly`. `editable` is true when some rule accepts edits.
- `GET /graphs/{g}/descriptor` returns the `GraphDescriptor` at the head.

### Definitions are graphs
`catalog` is served like any data graph, with the kernel as its ontology. A canvas that renders and diffs data graphs renders and diffs definitions with the same code.
- `GET /graphs/catalog/ontology` returns the kernel ontology (`EntityType`, `RelationshipType`, `Constraint`, `Port`, operators, `contains`, `wire`, ...; names as in research 03 section 2).
- `GET /graphs/catalog/snapshot` returns every definition node as an entity (type `Module`, `EntityType`, `Projection`, `Match`, ...) and every definition edge as a relationship (type `contains`, `ofType`, `wire`, ...). Relationship ids are `<source id>|<edge type>|<target id>`. `contains` carries `slot` and `ordinal`.
- `GET /graphs/catalog/patches?after=r`: one patch per catalog revision. Loading a module and committing an edit are ordinary patches.
- `GET /definitions/{module}` returns the canonical printed text. `GET /definitions` lists modules: `[{ "name", "version", "digest", "revision" }]`.

## Reads
- `GET /graphs/{g}/snapshot[?revision=r]` returns `{ "descriptor", "extent": { "entities": [], "relationships": [] } }` at `r` (default head). Older than the retained patches (Version 3.8) answers `410 Gap`; clients must handle it. Revisions older than the retained history answer `410 { "error": { "code": "Gap", "lastAvailable": "5" } }`.
- `GET /graphs/{g}/ontology`, `GET /graphs/{g}/patches?after=r[&limit=n]` (NDJSON, one `GraphPatch` per line, `to` = `after+1 ...`).
- `GET /graphs/{g}/meters` returns the telemetry declarations the definitions imply, for the canvas channel mapping:
```json
{ "meters": [ { "id": "flows", "target": "edge", "label": "flows", "unit": "", "temporality": "delta",
                "combine": "sum", "periodSeconds": 60, "min": 0, "max": 812.0, "scale": "sqrt", "warn": null } ],
  "thresholds": [ { "graph": "serviceTree", "operator": "health", "meter": "dropRatio", "above": 0.05, "enterAfter": 3, "below": 0.02, "exitAfter": 3, "state": "status", "high": "degraded", "low": "ok" } ],
  "defaults": { "edge": { "width": "flows", "flow": "flows", "state": "dropRatio" }, "node": { "ring": "flowsOut", "size": "flowsOut", "state": "flowsOut" } } }
```
`warn` is the `above` threshold of a `Threshold` on that meter, from the definitions. `min`/`max` are observed over the retained windows.

## Telemetry
Telemetry is keyed by **graph, element id, property id, window start instant**. It never creates revisions and is never indexed by revision. Derived graphs carry their own telemetry.
- Range read: `GET /graphs/{g}/telemetry?from=<instant>&to=<instant>[&property=<id>][&element=<id>][&format=series|batches]`. `from` inclusive, `to` exclusive, on window starts. Defaults: all retained.
  - `format=series` (default):
```json
{ "graph": "master", "from": "...", "to": "...",
  "series": [ { "element": "r:ab12", "target": "edge", "property": "flows", "periodSeconds": 60,
                "completeness": "Complete",
                "points": [ { "windowStart": "2026-11-10T10:41:00Z", "value": 7, "valueText": "7" } ] } ] }
```
  `completeness` is `Complete` or `Partial(<reason>)` (`sourceLoss` after a Hubble `LostEvent`). Per window partial marks are in `partialWindows: [{windowStart, reason, count}]`.
  - `format=batches` returns NDJSON of the canvas `TelemetryBatch` shape, sparse (`ids` present), one line per (window, property): `{ "graph", "tick", "t", "windowStart", "target", "property", "ids", "values" }`. `tick` is the ordinal of the window among retained windows. **Ticks are not revisions.**
- Live push: see WebSocket `telemetry` messages. A message carries one window of one property and the changed elements only. A revised (late) window is pushed again with the new values. A window that was retracted (its last supporting row went: a late retraction inside the horizon, a rule change, an identity move) is pushed with the element under `removed: [ids]` of the batch, next to `ids` and `values` (which may then be empty); a client drops the value of that element, property and window.

## Lineage
`GET /graphs/{g}/lineage?element=<id>` for a derived or master element:
```json
{ "graph": "serviceTree", "element": "r:cd34", "kind": "relationship",
  "projection": "serviceDeps", "sink": "emit.5", "rule": null, "direction": "forward",
  "operators": ["deps#0", "deps", "emit.5"],
  "sources": [ { "graph": "master", "element": "r:ef56", "kind": "relationship", "role": "c" },
               { "graph": "master", "element": "e:12ab", "kind": "entity", "role": "a" } ],
  "sourceCount": "2", "complete": true,
  "groupKey": null }
```
- `sources` are the input-graph elements bound by the row that last contributed the element (pattern variables of the `Match`), up to 16; `sourceCount` is the total. `role` is the pattern variable.
- For elements emitted from an aggregation (`Group`), `sources` holds the elements named by the group key if they are element variables, otherwise it is empty, `complete` is false and `groupKey` holds the key values. For the master graph, `sources` is empty and `inputs` names the source row types: `{ "sources": [], "inputs": ["PodRow"] }`.
- `404 { "error": { "code": "UnknownElement" } }` when the element is not in the graph at the head.

## Checking and editing definitions
Definitions are text (`.sattle`), one module per call.

`POST /check`
```json
{ "module": "acme.svc", "text": "module acme.svc @ 1.0.0; ..." }
```
No state changes. Response `200`:
```json
{ "ok": false, "baseRevision": "10",
  "diagnostics": [ { "code": "UnknownField", "severity": "error", "message": "...", "line": 31, "column": 14,
                     "element": "d:acme.svc/projection:serviceDeps/op:depTelemetry/assign:dropRatio" } ],
  "patch": { "...GraphPatch on graph catalog from baseRevision to baseRevision+1, null when not ok..." },
  "affected": { "modules": ["acme.svc", "acme.space"], "graphs": ["serviceTree"] } }
```
- `line` and `column` are 1-based positions in `text` (0 when the diagnostic is about an element without text position). `element` is a catalog element id when known, else `null`. Codes are those of `sattle check` (`Syntax`, `UnknownReference`, `UnknownField`, `Set`, `K1`..`K11`, `DirectionOverclaim`, `P2`, `S2`, `ImportPinMismatch`, ...).
- The patch is the difference between the current catalog and the catalog with `text` replacing the module (dependent modules re-sealed: their release digests and import pins change, so they appear in the patch). It is a normal `GraphPatch`; the canvas diffs it with its definition-graph machinery.

`POST /definitions/{module}`
```json
{ "text": "...", "expectedRevision": "10" }
```
Commits the catalog patch and re-activates the space as a configuration edit (ADR-064 to 071): the previous output graphs are kept as the base, the retained inputs are replayed once in order through the new plan, and **only the final difference per graph is emitted** as one patch per changed graph. Response:
- `200 { "catalogRevision": "11", "activation": { "graphs": [ { "id": "serviceTree", "from": "44", "to": "45", "edits": "12" } ], "replayedRows": "243457", "milliseconds": "3100" } }`. The same facts are pushed on the WebSocket (`catalog` patch, graph patches, `activation`).
- `409 { "error": { "code": "StaleRevision", "actual": "11" } }` when `expectedRevision` is not the catalog head. Nothing changes.
- `422 { "error": { "code": "Invalid", "diagnostics": [ ... as /check ... ] } }`. Nothing changes.
- `422 { "error": { "code": "ActivationRejected", "message": "..." } }` when the new definitions do not activate (for example a source with no adapter) or the retained inputs no longer cover the replay (`InputsNotRetained`). Nothing changes.
- `PUT`-style creation of a new module uses the same call.
Canvas `evaluate(definition)` = `POST /check` for the diagnostics and diff, then `POST /definitions/{module}` to run it; `GET /graphs/{g}/snapshot` afterwards is the output.

## Edits to data graphs (bidirectional)
`POST /graphs/{g}/edits`
```json
{ "baseRevisions": { "capabilities": "4", "master": "9" },
  "edits": [
    { "kind": "entity", "id": "e:aa", "before": { "...EntityState..." }, "after": { "...EntityState..." } },
    { "kind": "entity", "id": null, "before": null, "after": { "id": { "value": "e:bb" }, "type": { "value": "BizCapability" }, "properties": [] },
      "key": { "domain": "capability", "components": { "id": "payments" } } },
    { "kind": "relationship", "id": "r:cc", "before": null, "after": { "...RelationshipState..." } } ] }
```
- Edits target the internal graph only. A chain ends at the master graph's authored partition (rows from the `sattle.authoring` source); no edit reaches Azure or Kubernetes. Elements and properties owned by `readonly` rules and all telemetry reject.
- `baseRevisions` names the base revision of every graph on the edit path (the edited graph, each intermediate graph, the master). Any stale base rejects.
- A created entity names its key (`key`); the server derives its id the way ingestion does and rejects a different id.
- The server computes the backward proposal, applies it to a staged copy, evaluates forward, and accepts only if the staged view equals the edited view (PutGet). Accepted edits become recorded input rows; normal forward ingestion commits them and the patches arrive on the feed like any other.
- `200 { "accepted": true, "revisions": { "master": "10", "capabilities": "5" } }`.
- Rejection: `422 { "error": { "code": <code>, "message", "graph", "element", "rule" } }` with `code` one of
  - `NotEditable`: the graph is produced by a `forward` projection;
  - `ReadOnlyByRule`: the element or property is owned by a `readonly` rule (`rule` names it);
  - `TelemetryNotEditable`;
  - `NotAccepted`: the rule's `accept` list does not allow this kind of edit;
  - `StaleBase` (`409`, with `actual`);
  - `PutGetViolation`: forward evaluation of the proposal does not reproduce the edited view; `element` names the first difference;
  - `AmbiguousKey`, `ConstraintViolation` (key, cardinality, acyclic, enum), `KeyMismatch`, `BadEdit`.
  Nothing is accepted or committed on rejection.
- `GET /graphs/{g}/editability` returns `{ "graph", "rules": [ { "name", "class", "accept": ["create","delete","update(name)"], "reason": null, "owns": ["BizCapability", "BizCapability.name"] } ] }` so the canvas can disable controls before the user tries.

## WebSocket `/ws`
One connection, many subscriptions. Client messages:
```json
{ "op": "subscribe", "graph": "master", "after": "120" }
{ "op": "subscribe", "graph": "serviceTree" }
{ "op": "subscribe", "graph": "serviceTree", "telemetry": false }
{ "op": "unsubscribe", "graph": "master" }
{ "op": "ping" }
```
- The server sends `{ "type": "hello", "protocol": "3", "space": { ... as GET /space ... } }` on connect.
- `subscribe` without `after` (or with `after` equal to `"-1"`): the server sends `{ "type": "snapshot", "graph", "snapshot": { descriptor, extent } }` at some revision `r`, then every patch after `r`, in order, without gaps, then follows live. With `after = "k"`: the missed patches `k+1 ...` then live, or, when `k` is older than the retained history, `{ "type": "gap", "graph", "lastAvailable": "5" }` followed by a snapshot as above. A client ahead of the head gets `{ "type": "error", "code": "AheadOfHead" }`.
- Server messages (all carry `graph` where applicable):
  - `{ "type": "snapshot", "graph", "snapshot" }`
  - `{ "type": "patch", "graph", "patch" }` one per revision, strictly consecutive per graph per subscription
  - `{ "type": "gap", "graph", "lastAvailable" }`
  - `{ "type": "telemetry", "graph", "batch": { sparse TelemetryBatch with windowStart } }` unless `telemetry:false`
  - `{ "type": "activation", "catalogRevision", "graphs": [ { "id", "from", "to" } ] }` after a definition commit; patches of that activation precede it. Clients refetch telemetry after it.
  - `{ "type": "time", "logicalTime": "<instant>", "rows": "<count>" }` at most once per second: the engine's logical frontier and input row count.
  - `{ "type": "end" }` when a replay input ends (live inputs never end).
  - `{ "type": "error", "code", "message" }`
- Ordering: for one graph, patches are delivered in revision order. Telemetry and patches of different graphs are interleaved in engine order.
- Backpressure: a subscription that falls behind by more than 10,000 messages is closed with `{ "type": "error", "code": "SlowConsumer" }`; the client resubscribes with `after`.

## Input modes
- `replay CAPTURE`: the recorded capture is fed at `--speed`. `GET /space` shows `input.mode = "replay"`.
- `serve --live`: Event Hubs input (`--live`, see README). `input.mode = "live"`, `input.hubs`, `input.checkpoint`. Logical time advances from the recorded `capturedAt` of the events, exactly as replay does, so replaying the recorded stream gives the same state digest. `GET /health` returns `{ "mode", "done", "digest", "graphs": [ { "name", "head" } ], "lag": { "events", "seconds" } }`.

## Not in the protocol yet
Cosmos-backed history beyond process memory (history older than the retained patches answers `Gap`), authentication (the server binds to 127.0.0.1), multi-tenant.

---

# Version 3: editor, drafts, refactoring, push and caching

Design stance: a **redux style** client. The server owns the committed state and emits one ordered stream of events with a sequence number. A client reducer folds events into local state (`state[graph] = apply(patch)`), keeps its own 99-step undo stack of inverse patches, and caches by content: a resource addressed by a content digest (`/releases/{digest}`) is immutable, the others are revalidated with their content tag (Version 3.9). The server has no per-client undo state beyond the draft overlay.

## v3: one ordered event stream
`{ "op": "subscribe", "graphs": ["master","catalog"], "drafts": true, "after": "1204" }`; `graphs` defaults to all, `drafts` to false, `after` to absent. Replaces the v2 per-graph subscription (one subscription per connection; subscribing again replaces it).
- Every event except `time` carries `seq`, a server-wide decimal string that increases by one per event. The server keeps the last 100,000 events.
- No `after`: the server sends one `snapshot` per subscribed graph, each with `"seq"` = the sequence number the snapshot is current as of, then every event with a larger `seq` that matches the filter, in order.
- With `after`: events with `seq > after`. If `after` is older than the retained events: `{ "type": "gap", "firstSeq": "900" }` and then snapshots as above. If `after` is ahead of the server: `{ "type": "error", "code": "AheadOfHead" }`.
- Events: `snapshot`, `patch {graph, patch}` (consecutive revisions per graph), `telemetry {graph, batch}`, `activation {catalogRevision, graphs}`, `draft {draft, revision, ok, diagnostics}` (after each change of a subscribed-to draft's workspace, only with `drafts: true`), `end`, `error`. `time` is unsequenced.
- Reducer contract: apply `patch` when `patch.from` equals the local head of `graph`; otherwise resubscribe with `after = lastSeq`.
- Slow consumers (more than 10,000 queued) are closed with `SlowConsumer`; resubscribe with `after`.

## v3: caching
- **Digest-addressed resources** (`GET /releases/{digest}`, `GET /releases/{digest}/text`, `GET /releases/diff?from=&to=`) answer with `Cache-Control: public, max-age=31536000, immutable` and a strong `ETag` that is a content digest (see "Version 3.9"). **Revision-addressed resources** (`GET /graphs/{g}/snapshot?revision=r`, `GET /graphs/{g}/patches?after=a&to=b`, `GET /graphs/{g}/document?revision=r`) are `no-cache` and revalidated by the content tag: a revision number is unique only within one run.
- **Head resources** (`GET /space`, `GET /graphs/{g}/snapshot` without `revision`, `GET /graphs/{g}/patches?after=a`, `GET /definitions`, `GET /graphs/{g}/meters`) answer with `Cache-Control: no-cache` and a strong `ETag` that is the digest of the body (see "Version 3.9"); `If-None-Match` answers `304` when it matches. Snapshots without `revision` also carry `Content-Location: /graphs/{g}/snapshot?revision=r` and `X-Revision: r`, so a client can cache them under the immutable URL.
- Typical client: fetch `GET /space`, fetch snapshots by `Content-Location` (browser cache), subscribe with `after = seq` from the snapshot response header `X-Seq`, fold events.

## v3: palette from the kernel
`GET /palette` (`no-cache`, revalidated by its content tag) describes everything the editor can place, derived from the kernel table and the engine's support list, never hard-coded in the UI:
```json
{ "kernelDigest": "sha256:...", "version": "3",
  "operators": [ { "kind": "Window", "supported": true, "role": "linear", "container": "Projection", "slot": "op",
      "ports": [ { "name": "in", "direction": "in", "kind": "rows" }, { "name": "out", "direction": "out", "kind": "rows" } ],
      "params": [ { "name": "timestampField", "kind": "Text", "required": true },
                  { "name": "width", "kind": "Integer", "required": true, "unit": "ns" },
                  { "name": "late", "kind": "Text", "required": true, "values": ["ignore","revise","reject"] } ],
      "slots": [ ],
      "strategy": "linear: window assignment", "sink": false, "telemetry": false } ],
  "ruleClasses": [ { "name": "lens", "params": ["putput"], "policies": ["accept","on create","on delete"] } ],
  "aggregates": [ { "name": "min", "supported": true, "inputs": "numeric|instant", "output": "same" } ],
  "functions": [ { "name": "Divide", "signature": "(n, m) -> Decimal", "role": "scalar", "inverse": null, "supported": true } ],
  "valueKinds": ["Text","Integer","Decimal","Boolean","Instant","Entity","List"],
  "idScheme": "d:<module>/<slot>:<name>/..., relationship id <source>|<edge>|<target>, unnamed ordered child slot:#n" }
```
`supported: false` entries are kernel kinds the engine cannot evaluate yet; the editor shows them disabled with the reason in `unsupportedReason`.
`POST /palette/skeleton { "kind": "Window", "container": "d:acme.ops/projection:ingest", "name": "w1", "params": { "width": "60000000000" } }` returns a `GraphPatch` on the catalog that creates the node with its kernel-fixed ports (K9) and defaults, ready to apply to a draft.

## v3: drafts
A draft is an overlay on the catalog at a base revision. State lives on the server; the browser may keep a copy.
- `POST /drafts { "base": "12" }` (base defaults to the head) returns `{ "draft": "d1", "baseRevision": "12", "revision": "0" }`. `GET /drafts` lists drafts, `GET /drafts/{id}` returns the draft state, `DELETE /drafts/{id}` discards it.
- `GET /drafts/{id}/snapshot` and `GET /drafts/{id}/patches?after=r` serve the draft's catalog graph like `catalog` (same shapes; the draft's own revision counter, starting at `"0"` = the base).
- `POST /drafts/{id}/patches { "patch": <GraphPatch on catalog>, "expectedRevision": "3" }` applies a structural edit. The patch is a normal `GraphPatch` (guards = before states) with `from` = the draft revision. It may be a user edit or the inverse of an earlier one: **undo and redo are inverse patches applied to the draft**, never a rewind of any log; the engine accepts any patch whose guards hold. Response: `{ "revision": "4", "ok": true, "diagnostics": [...], "modules": { "acme.svc": { "text": "...canonical text...", "changed": true } } }`. A guard failure answers `409 { "error": { "code": "GuardFailed", "element": "<id>" } }`; a stale `expectedRevision` answers `409 StaleRevision`; nothing changes.
- `POST /drafts/{id}/check` re-checks without changing anything and returns the same `{ ok, diagnostics, modules }`. Diagnostics are by **definition element id** (`element`), with `module`, `code`, `severity`, `message`, and `line`/`column` in the canonical text of that module (`modules[m].text`). A patch that makes the graph unprintable (missing required slot or reference) yields code `Unprintable` with the element id.
- `POST /drafts/{id}/commit { "expectedCatalogRevision": "12" }` prints every changed module canonically, checks them together, and activates exactly like `POST /definitions/{module}` (replay of retained inputs, one final-diff patch per graph). Response as there, plus `"modules": ["acme.svc"]`. Errors as there (`StaleRevision`, `Invalid` with diagnostics, `ActivationRejected`).
- Text and graph stay two views of one definition graph: `GET /drafts/{id}/text/{module}` returns the canonical text; `POST /check` and `POST /definitions/{module}` take text and are equivalent to a patch from the current graph to the parsed text.
- Drafts survive until discarded or the server restarts. A draft whose base is not the head is still committable if its guards hold against the head (a rebased commit): the server computes the patch from the head to the draft's final graph for the modules the draft touched and answers `409 StaleRevision` when a touched module changed in between.

## v3: releases and versions
Every committed catalog revision that changes a module creates a release identified by the module's digest.
- `GET /releases[?module=acme.svc]` returns `[ { "module", "version", "digest", "catalogRevision", "supersedes": "<digest|null>" } ]`, oldest first.
- `GET /releases/{digest}` returns the module's definition facts at that release as `{ descriptor, extent }` (content-addressed, immutable). `GET /releases/{digest}/text` returns the canonical text.
- `GET /releases/diff?from={digestA}&to={digestB}` returns the graph diff as a `GraphPatch` on the catalog (before states from A, after states from B; ids are definition ids so renames show as property changes).
- `POST /drafts/{id}/restore { "digest": "sha256:..." }` applies the patch from the draft's current facts of that module to the release's facts, so the draft equals the release for that module (an ordinary draft patch, undoable by its inverse).

## v3: preview
`POST /drafts/{id}/preview { "scope": { "maxRows": "500000" }, "graphs": ["serviceTree"], "include": ["counts","diff","snapshots","telemetry"], "limit": "5000" }`
Evaluates the draft without committing or activating. A fresh engine with its own in-memory stores replays the retained inputs (the first `maxRows` input rows in order, default 500,000, hard cap 5,000,000; **the scope is stated in the response**); the live engine, its stores and its history are untouched.
```json
{ "scope": { "rows": "243457", "totalRows": "243457", "complete": true, "logicalTime": "2026-11-10T10:41:00Z" },
  "diagnostics": [ ],
  "graphs": [ { "id": "serviceTree", "counts": { "entities": "19", "relationships": "29", "telemetryWindows": "182" }, "digest": "sha256:...",
                "diff": { "...GraphPatch from the current output to the preview output..." },
                "snapshot": { "...": "present only with include snapshots; truncated flag when over limit" },
                "telemetry": [ "...TelemetryBatch (as in the feed)..." ],
                "truncated": false } ],
  "identity": { "preview": { "minted": "52266", "aliased": "81", "merged": "43", "conflicts": "0" },
                "live":    { "minted": "52266", "aliased": "81", "merged": "43", "conflicts": "0" },
                "comparable": true, "difference": { "minted": "0", "aliased": "0", "merged": "0", "conflicts": "0" } },
  "milliseconds": "840" }
```
`identity` is the identity effect of the preview: identities are resolved once per space, so the counters are the previewed space's (`preview`), next to the live space's (`live`). `minted` are new identities, `aliased` keys bound to an existing identity by a `Link`, `merged` two identities joined by a `Link` or by late evidence, `conflicts` links refused (different entity types). `difference` is preview minus live and is present only when `comparable` (the preview saw every retained input, as for `diff`); a draft that adds a `Link` shows up as more `aliased` or `merged`. Integers are strings.
`diff` is present only when `scope.complete` is true (the preview saw all retained inputs, so it is comparable with the live outputs); otherwise `null`. New graphs diff against empty; removed graphs are listed with `removed: true`. Invalid drafts answer `422 Invalid` with diagnostics and nothing evaluated.

## v3: refactoring
Pure functions on the definition text of a draft, returned as draft patches (nothing is applied until the client applies the returned patch to the draft). All three are limited to projections of one module.
- `POST /drafts/{id}/refactor { "kind": "extract", "module": "acme.biz", "projection": "capabilityView", "select": ["tree"], "name": "capabilityTree" }`: moves the selected statements (let chains by name, sinks by name, whole rules by name) into a new projection `capabilityTree` with its own output graph `<graph>_capabilityTree` in the graph space, copies the ports it uses, and binds the new graph with the parent's arguments. The parent keeps everything else. Selection names in the canonical text: top-level lets by name, top-level sinks as `emit.N` (N counts the `emit` statements in canonical order), whole rules by name. Lets used only by the selection move with it. An update sink moves only together with the create sink of its type, and an ingestion projection cannot be split (a second graph must derive from the master).
- `{ "kind": "inline", "module", "projection": "capabilityTree", "into": "capabilityView" }`: the reverse; the graph is removed from the space.
- `{ "kind": "moveRule", "module", "from": "p1", "to": "p2", "rule": "observedPods" }`: moves a rule (and the lets only it uses) between two existing projections.
- Response `{ "patch": <GraphPatch>, "modules": { "<m>": { "text": "..." } }, "preserves": { "outputs": "union" } }` or `422 { "error": { "code": "<reason>", "element": "<op or rule>", "message" } }` with codes `SharedOperator` (a kept statement consumes a moved one), `DependsOnUnmoved`, `UpdateWithoutCreate`, `SplitRule`, `IngestionProjection`, `CrossModule`, `UnknownSelection`, `NotAProjection`.
- **Preservation** (tested on the fixtures): the union of the output graphs (elements with ids and states, and telemetry windows) after the refactoring equals the output graph before it. Relationship ids are derived from the output ontology and key rule, not from the projection id, so moving a rule between projections of one output ontology keeps ids.

## v3: errors and misc
- `POST /check`, `POST /definitions/{module}` unchanged (text based). `GET /health` also returns `"seq"`.
- New error codes: `GuardFailed`, `Unprintable`, `UnknownDraft`, `UnknownRelease`, `SharedOperator`, `DependsOnUnmoved`, `UpdateWithoutCreate`, `SplitRule`, `IngestionProjection`, `CrossModule`, `UnknownSelection`, `NotAProjection`, `ScopeTooLarge`.

# Version 3.1: corrections (engine turn 4)
Same protocol number; these fix mismatches the canvas found in version 3.
- `GET /space` and the `hello` message carry `seq` (decimal string): the event sequence the response is consistent with, so a client can subscribe with `after = seq`.
- `POST /drafts/{id}/restore` answers `{ patch, revision, ok, diagnostics, modules }`: the applied patch is returned (an empty patch when the draft already equals the release).
- `POST /palette/skeleton` takes an optional `"draft": "<id>"`; with it the patch starts at that draft's revision, without it at the catalog head. `POST /drafts/{id}/skeleton { kind, container, name, params }` is the same call with the draft implied.
- Skeleton patches create a placeholder for every required slot whose child has a default: `Expr` slots get a `Literal` (`true` for Filter and Join, otherwise empty text), `Assign` slots an assignment `x` with a literal value, `Aggregate` slots a `count` named `n`, `FieldRef` slots the path `x.y`. Slots whose child needs a choice (Match nodes, `IdOf` of Link, Fixpoint relation, Values rows) stay empty; printing then fails with `Unprintable` naming the operator.
- `Unprintable` always names the element: a wired operator or sink with a missing slot or reference is reported with its own id (the Filter without predicate case), not the module.
- Committing a module whose version changed (`module m @ 1.1.0;`, by `POST /definitions/{m}` or a draft commit) rewrites the `import m @ old` lines of its dependents to the new version and re-seals their pins, in the same revision. Dependents appear in the `modules` list of a draft commit and in the activation patch.
- `GET /graphs/catalog/meters` answers `{ meters: [], thresholds: [], defaults: {} }` instead of 404.
- Cache headers: `GET /graphs/{g}/ontology` is `no-cache` with a content-digest ETag, as is `GET /graphs/{g}/telemetry`; `GET /health` is `no-store` (it is liveness and changes with every event).
- **Dangling relationships** (not a bug, now visible; Version 3.11 says for each endpoint what it is and why it is missing): a relationship may name an endpoint entity that is not in the graph (ADR-084). Causes: a Hubble flow names a pod before the pod row arrives or after it left, a source cut that lacks the pod rows (`lab/fixtures` has 2 pods and flows between 11 others), a signal for a component outside the model's scope. Each graph entry of `GET /space` except `catalog` carries `dangling` (decimal string): the number of relationships with a missing endpoint at the head. A client draws them as pending stubs or skips them; they resolve by themselves when the entity appears, and the patch stream then needs no correction.

## Version 3.1: operators, parameters, kernel changes
- **Kernel changes** (the kernel digest changes, clients that key the palette by `kernelDigest` refetch): `Aggregate` gets an optional slot `order` (the ordering expression of `last`), `Traverse` a property `target` (the name of the reached element column), `Fixpoint.relation` is optional (the relation type is inferred from the seed), `Port.kind` may be `param` and a `Port` may have a `valueKind` edge, `Binding` has an optional property `value` (a constant). Definition graphs gain nodes accordingly; patches on them need nothing new.
- **Operators now `supported` in `GET /palette`**: `Join` (ports `left`, `right`, slot `on`), `Union`, `Distinct`, `Traverse` (ports `graph`, `in`, `out`; params `direction`, `minDepth`, `maxDepth`, `start`, `target`; edge `ofType` to the relationship type), `Fixpoint` (ports `seed`, `out`; `maxIterations`; the step operators are siblings in the same projection, wired from the fixpoint's `out` port, and an `over` edge points to the port that carries the step result). Aggregates `min`, `max`, `mean`, `any`, `all`, `last` are `supported`. Still `supported: false`: `Values`, `Delta`, `weightedMean`, rule class `sync`.
- **Text syntax** (canonical printing is exact): `let j = join a, b on <expr>;` (equalities between a left and a right expression, other conjuncts are a residual filter; both inputs are data rows with distinct column names), `let u = union a, b;`, `|> distinct`, `|> traverse g via Rel out|in|either depth 0 .. 8 from c as d` (adds the columns `d`, `d_depth` and the demanded read columns `d.prop`), `let r = fixpoint seed via <pipeline that reads r> max 8;`, aggregates `mean(x)`, `any(b)`, `all(b)`, `last(value, order)`.
- **Parameters**: a projection declares `in name: param Text;` (Text, Integer, Decimal or Boolean) and reads it as a constant in expressions; a space binds it with a literal, `graph g = m.p(master: master, name: "value");`. The projection is checked again with the values per binding. Parameters are ordinary definition facts (`Port` with kind `param`, `Binding` with `value`), so the editor creates and edits them with the same patches.
- **Refactoring** copies the parameters the moved statements use and binds them in the space. The guarantee stays "the union of the output graphs is preserved": a graph that reads the parent graph is not rewired and sees only what the parent kept.

## Version 3.2: value roles (presentation hints as ontology data)
The engine knows nothing about health or colors. A property type may declare a semantic **role** per value. The kernel stores it, the ontology endpoint serves it, no engine code reads it. A client colors by the role of the property value it finds on a node or an edge.
- **Roles**: `ok`, `warning`, `danger`, `unknown`, `neutral` (kernel enum `ValueRole.role`). What a role looks like is the client's business.
- **`.sattle` syntax**: after the type and the other modifiers of a property, `roles { <value>: <role>, ... }`, values are string, integer, decimal or boolean literals:
  `state: Text roles { "Healthy": ok, "Degraded": warning, "Unhealthy": danger, "Unknown": unknown };`
  It works on properties of entities, relationships and `extend` blocks. The printer writes the entries in the order of the canonical id segment of the value (text first, then booleans, decimals, integers).
- **Definition graph**: a node of the new kernel type `ValueRole` (properties `value`, `role`) contained in slot `presentation` of its `PropertyType`, id `<property id>/presentation:<canonical value>`. Draft patches create and delete it like any other definition node; `GET /palette` lists the type, its slot and the role enum (`valueRoles`).
- **Ontology endpoint** (`GET /graphs/{g}/ontology`): every entry of `properties` gets `"presentation": [ { "value": <value as in snapshots, e.g. {"kind":"text","value":"Healthy"}>, "role": "ok" } ]`; the key is absent when the property declares no roles. Properties extended onto other types carry the same list.
- **Use**: a node (or edge) has the property `state` with value `"Unhealthy"`; the ontology says that value has role `danger`; the client paints it with its danger color. A value without a role is neutral. Roles apply to data properties, including ones computed by a projection (a Deployment's `state`, an edge's `state`), so a client needs no knowledge of how the value was computed.

## Version 3.2: palette describes declarations
`GET /palette` gains, all derived from the kernel table (nothing for a client to hard-code): `declarations` (one entry per module slot: `entity`, `rel`, `extend`, `identity`, `equivalence`, `rows`, `projection`, `space`, `constraint`; each with `slot`, `type`, `container`, `params` as for operators, `slots`); the `constraint` entry also lists `kinds` (`key`, `enum`, `range`, `pattern`, `cardinality`, `acyclic`: the constrained target, the properties it uses, its text form); `rowTypes` (the field type with its params and the usable value kinds), `equivalencePolicies` (modes, params), `rules` (rule params and slots, `onDelete` values, the edit `policies` with their text), `projectionPorts` (`rows`, `graph`, `param` with the text form), `valueRoles` and `valueRoleSlot` (where a `ValueRole` node goes). Refactoring answers also carry `diagnostics`: the warnings of the refactored text (for example "source of Under is emitted by no create emit of this projection" after extracting a lens rule that only emits relationships; the endpoint types are created by another projection, which is allowed and resolves when they appear). `UpdateWithoutCreate` carries `include`: the statements that would have to move (or stay) too. A patch with several edits of one element (remove then create of the same id) is folded into one edit; errors that were raw .NET text are `Unprintable` with the declaration's element id or `BadPatch` (422), and the server never answers with an empty 500 body (`Internal`).

## Version 3.3: loss evidence and late data
- `partialWindows[].reason` (and `completeness: Partial(<reason>)`) now also takes `forwarderDropped`, `hubbleReconnect`, `pollFailed` (forwarder markers of `lab/CAPTURE-FORMAT.md`); several reasons in one window are joined with `+`. `count` is the number of lost events when the marker gives one, 0 otherwise.
- A row for a window beyond the late horizon (three widths by default) is dropped and counted in the late-dropped total instead of reviving the window. `sattle replay --late-horizon WINDOWS` sets the horizon (default 1,000,000 for a finite replay).
- `GET /palette`: `Values` and `weightedMean` are `supported`; `Delta` and rule class `sync` stay `supported: false` with `unsupportedReason`.
- The ontology of `sattle.infra` and `sattle.azure` gains evidence types (`WarningEvent`, `AuditEvent`, `ActivityEvent` and the relationships `About`, `Concerns`), and `acme.health` gains `Evidence` and `EvidenceFor`; no wire change.

## Version 3.4: document projections
A document is a derived graph whose output ontology is `sattle.doc`: its elements are `Block` entities. Nothing about documents is a new kernel kind; any projection can produce one, and the engine has one generic renderer.
- **Ontology `sattle.doc`**: `Block { kind, order, text?, level?, a?, b?, c?, d?, n? }` with the identity domain `block(rule, key)`; the block id derives from the rule and the source key (never from position). `kind` is `heading` (`level` 1 to 6, `text`), `paragraph` (`text`), `item` (`text`), `stat` (`text` is the label, `n` the value), `header` and `row` (cells `a`, `b`, `c` and `d`, or `n` instead of `d` when present; consecutive `header` and `row` blocks form one table, the `header` first). `order` is a text sort key: blocks are ordered by `order` (ordinal), then by id, so the order is total.
- **`GET /graphs/{g}/document`** answers the rendered document of a document graph at the head, or at `?revision=r`. `format=markdown` (default, `text/markdown`) or `format=json` (`{ revision, digest, blocks: [ { id, kind, order, ... } ] }`). Headers: `X-Revision`, `X-Document-Digest` (`sha256:` of the rendered Markdown bytes), `ETag` is the content digest of the body, as for every route (Version 3.9). The response is `no-cache` (with `revision` it is revalidated by the content tag; without it the head response also carries `Content-Location` of the revision URL). A graph that is not a document answers `404 NotADocument`.
- **Rendering is a function of the snapshot**: LF line endings, one final newline, text from graph data is escaped (backslash before ``\ ` * _ { } [ ] < > ( ) # + - . ! | ~``; line breaks become spaces), so data cannot create structure. The digest of a revision is the digest of these bytes, so equal graph states give equal documents.
- `GET /space`: a graph entry of a document graph carries `"document": true`.

## Version 3.5: aggregate contexts, retention, restart
- **`GET /palette` aggregates are true per context.** Each entry of `aggregates` is `{ "name", "supported", "contexts": [ { "operator", "inputs", "output", "requires" } ] }`. `operator` is `Group` (aggregates data columns) or `Rollup` (aggregates telemetry values). An aggregate is allowed only in the contexts listed; in any other it is a typed error at that position (`Aggregate` or `Unsupported`), so a client can disable it per operator. Today: `count` (Group, Rollup), `countDistinct` (Group), `sum`, `min`, `max` (Group over numeric data; Rollup over numeric telemetry that is not cumulative), `mean` (Group; Rollup over a gauge only, `requires: "gauge"`), `weightedMean`, `any`, `all`, `last` (Group only). `supported` is true when at least one context is listed. The kernel digest changed (`Retain` below), so cached palettes revalidate.
- **Operator `Retain`** (`|> retain on time for 24h`, ports `in` and `out`, params `timestampField` (Text, a required data Instant column of the input) and `age` (Integer, nanoseconds, positive), linear strategy "per-row buffer by event time"). Rows pass through unchanged. A row is withdrawn (the same row with weight -1) when the logical frontier reaches its time plus the age, so every element and relationship derived only from it is retracted by the usual support counts; a row that is already that old on arrival never passes; a withdrawal of a row that expired meanwhile is absorbed. Retained state is a function of the frontier, so it is batch invariant. The input must be event rows, not windowed rows. Retention applies to evidence rules in `acme.ops` (24 hours of logical time); a retracted element is a `patch` like any other, no new message.
- **Restart.** `sattle serve --store sqlite=PATH` and `--store cosmos=ENDPOINT[,db=NAME]` keep the journal of the space (input batches, logical-time advances, losses, definition commits, the end marker; sequence numbers consumed by drafts) next to the commit log, written before the engine sees an input. Started on a store that has a journal, `serve` needs no capture and no definitions directory: it replays the journal, recomputes every commit and compares it with the stored one (a difference refuses the start), and serves the same `seq`, heads, state digest and `/space` as before the stop. A subscription that resumes with `after = N` gets the events after `N` from the rebuilt ring (the events are regenerated with their sequence numbers; `gap` only if the ring is shorter than the request, as before). Not stored: drafts (a draft is a workspace of one session; its sequence numbers are kept so later numbers do not move), the telemetry ring beyond what replay recomputes, subscriptions. A capture is not resumed: a journal that ends before the end marker is closed on start (`end` event). Definition commits survive: releases are the catalog history rebuilt from the journal.

## Version 3.6: numeric parameters say what they measure
- Every Integer and Decimal parameter in `GET /palette` (operator params, declaration params, constraint params) has `quantity` and `unit`. `quantity` is `duration` (an integer of nanoseconds on the wire, `unit: "ns"`, written in the text as `<integer><ms|s|m|h>`: `literal` and `example: "24h"` are given so an editor can offer `24h` and send nanoseconds), `count` (`unit` says what is counted: `windows`, `iterations`, `facts`, `hops`, `retained inputs per element`), `value` (in the unit of the thresholded value or the constrained property) or `ratio` (a fraction from 0 to 1). Durations today: `Window.width`, `Series.within`, `Retain.age`, `PropertyType.period`. A test fails when a numeric parameter has no quantity.

## Version 3.7: seals, source positions
- **Seals.** A durable store keeps, besides the journal, seals: the state of the whole space after the first `position` journal entries (a compressed snapshot, with a description: sealed state digest, catalog digest, build, generation, the definition texts). `serve --store X` takes one every `--seal-every` entries (default 5000). A restart loads the latest seal, compares its digests with what it restored, and replays only the entries after it, comparing each recomputed commit with the stored one as before. Nothing in the wire changes: `seq`, heads, `/space` and the event ring are what they were. A seal written by another build, with other definitions than the catalog it was sealed with, or with a digest that does not match, is not used and the journal is replayed from its start (a message on stderr says why). The retained input rows needed to re-activate a changed definition are read back from the journal on demand.
- **Source positions.** `GET /space` `input` has `position` for a live run: the position of the source (`{"partition","sequenceNumber"}` for Event Hubs) after the last envelope the space has seen. It is journaled with each envelope, so a live run killed and restarted from its store continues after it.

## Version 3.8: retention of served history and events, value roles of the health model
- **Retention.** The host keeps the last `RetainPatches` patches of every graph (`serve --retain-patches N`, default 2,000, 0 keeps all) and the last `RetainEvents` events of the ordered stream (`--retain-events N`, default 20,000). Revision numbers, heads, `seq`, state and digests do not change; the oldest are dropped in one cut when the retained ones are 10 percent over the declared number.
  - `GET /graphs/{g}/patches?after=r` and `GET /graphs/{g}/snapshot?revision=r` (and `/document?revision=r`) with `r` below the first retained revision answer **`410`** `{ "error": { "code": "Gap", "message": "...", "graph", "lastAvailable": "<first revision still available>" } }`. A client that gets it takes the snapshot at the head and continues from there. A revision at or after `lastAvailable` reads as before (the state is the head state walked back through the inverses of the retained patches).
  - A subscription that resumes with `after = seq` older than the oldest retained event gets `{ "type": "gap", "firstSeq" }` and then snapshots, as already documented; one that resumes inside the retained events gets exactly the events it missed.
  - The catalog history (definition revisions and releases) is not capped.
- **Value roles on the health model.** `GET /graphs/health/ontology` (and `healthState`, `healthTopology`, `healthSignals`) serve `presentation` for `Component.state` (`Healthy` ok, `Degraded` warning, `Unhealthy` danger, `Unknown` unknown) and for `Signal.degraded` (`off` ok, `on` warning) and `Signal.unhealthy` (`off` ok, `on` danger), as for `acme.deployhealth` (Version 3.2). The relationships of the model have no state property and carry none.
- **Preview identity effects.** `POST /drafts/{id}/preview` answers `identity` (see "v3: preview").

## Version 3.9: every ETag is a digest of the content it names
- **The rule.** The `ETag` of a response is `"` + the first 32 hex digits of SHA-256 over the media type, a newline and the body bytes + `"`. Nothing else goes into it: not a graph name, not a revision, not a catalog revision, not a sequence number. Those are unique only within one run of one store (two runs with different definitions reach the same catalog revision, and a browser that held `"onto-health-16"` from one run was told `304` by another and kept a stale ontology), so none of them can identify content across runs. Two responses with equal bytes have equal tags, in any run and after a restart from a store; two responses with different bytes never share one. The tag is a strong validator.
- **Applies to** every cached or revision-addressed route: `GET /space`, `/graphs/{g}/snapshot` (head and `?revision=`), `/graphs/{g}/patches`, `/graphs/{g}/ontology`, `/graphs/{g}/document`, `/graphs/{g}/meters`, `/graphs/{g}/telemetry`, `/definitions`, `/palette`, `/releases`, `/releases/{digest}`, `/releases/{digest}/text`, `/releases/diff`. The skeleton routes are `POST` and not cached. `GET /health` is `no-store`.
- **`Cache-Control`.** Only resources addressed by a content digest in their URL (`/releases/{digest}`, `/releases/{digest}/text`, `/releases/diff?from=&to=`) are `public, max-age=31536000, immutable`. Everything else is `no-cache` and is revalidated by its content tag: a revision number in a URL (`snapshot?revision=17`, `patches?after=a&to=b`, `document?revision=r`) does not identify content across runs, so a year-long cache of it would be the same defect as the tag. `GET /palette` is `no-cache` too (it changes with the kernel of the build). Earlier text in this document that calls revision-addressed responses `immutable` describes within-run behavior only and is superseded.
- **Revalidation.** `If-None-Match` with a tag the server does not know (a tag from another run, or a stale one) answers `200` with the body and the current tag. A matching tag answers `304`. The server remembers, per resource and while nothing has changed in the run, the tag it computed, so a matching revalidation does not rebuild the body; when something may have changed it builds the body and compares digests, so the answer is `304` only if the bytes are the same.

## Version 3.10: lifted edges, authored capabilities
- **`capabilities`** (`acme.biz.capabilityView`) has a second input `tree` (the service tree) in the space binding (`space.json`, `projections[].inputs`). It carries the dependencies of the service level one level up: relationship type `acme.biz.DependsOn` (`BizCapability` to `BizCapability`) with properties `callStatus` (`ok` or `degraded`), and the telemetry `callFlows`, `callDrops` (delta, sum, 60 s) and `callDropRatio` (gauge, max, 60 s). Semantics: for every service dependency `a -> b` whose ends both realize a capability, the edge `image(a) -> image(b)` unless both images are one capability; parallel edges are one edge with summed `callFlows` and `callDrops`, `callDropRatio` recomputed from the sums and `callStatus` derived from it (above 0.05 for 3 windows degraded, below 0.02 for 3 windows ok); a service without a capability and a retired capability give no edge. The names differ from the service level (`flows`, `drops`, `dropRatio`, `status`) so that no property name becomes ambiguous and qualified in the existing graphs.
- **Capture envelope source `authoring`**: capabilities and the services that realize them, see README "Edge lifting". `lab/CAPTURE-FORMAT.md` does not describe it yet.
- **`replay --out DIR`** (unchanged format, `manifest.json` lists the graphs): per graph `NAME.ontology.json`, `NAME.snapshot.json` (revision 0), `NAME.final.snapshot.json` (the extent at the end, entities and relationships), `NAME.patches.json` and `.ndjson`, `NAME.telemetry.ndjson`, and `space.json`.
- **Overlays by capture name**: `replay` and `serve` of a capture directory named `fixtures` bind the space with `engine/overlays/fixtures.space.sattle` (workload arguments of that cluster); of a capture under `lab/captures` with the gitignored `lab/captures/space-overlay.sattle`; `--overlay` overrides both.

## Version 3.11: references, complete lineage, serving a capture
- **Serving a capture is a replay.** `serve CAPTURE` and `replay CAPTURE` use the same late horizon (1,000,000 windows) and end in the same state digest; `--late-horizon N` sets it for both. Only `serve --live` keeps 3 (a live source must bound its state). The horizon is stored in the journal header and in every seal, so a restart computes with the horizon the run had. A recorded input with old timestamps (Activity Log backfill, Prometheus range queries) is dropped as late under horizon 3: on the newest real capture 192,095 rows, which turned 4 Unhealthy components into 9 Healthy and 2 Unknown.
- **References.** A relationship may name an endpoint that is not an entity of the graph (`dangling` in `GET /space` counts those relationships). Every snapshot (`GET /graphs/{g}/snapshot`, also `?revision=`, and the `NAME.final.snapshot.json` of `replay --out`) now has `extent.references`, one entry per such endpoint: `{ "id", "state", "graph", "entityType", "identity": { "domain", "key": { component: value } }, "uses": [ { "relationshipType", "end": "source"|"target", "count" } ] }`. `state` is `inOtherGraph` (the entity is in `graph`, the master first then the others in order: draw it or resolve it there; read at the head) or `absent` (no graph holds it, `graph` is null). `identity` is the key the id was minted from (domain, the identified type and the components), so a viewer can draw a labelled stub for an `absent` reference. Each graph entry of `GET /space` has `danglingBy`: `[ { "relationshipType", "end", "entityType", "state", "graph", "relationships", "endpoints" } ]` (empty when `dangling` is 0; a relationship with both ends missing counts once in `dangling` and once per end here).
- **Causes seen** (newest real capture, master): events about Azure resources that Resource Graph never listed (`Concerns` to `AzureResource`: 6,420 relationships to 563 resources), pods that flows name and Kubernetes never listed (`Calls` 140 to 14 pods), pods that warning events name and were never listed (`About` 71 to 16 pods). All are `absent`. The `health` graph had 187 `EvidenceFor` edges (52 % of its relationships) to components that are not in the model, Activity Log operations on clusters and machines of other resource groups; the projection now takes evidence only for components of the model, so `health` has none.
- **Lineage is complete for lifted elements.** `GET /graphs/{g}/lineage?element=ID[&limit=N][&after=CURSOR]` lists, for an element emitted from rows that bind elements of a `Match` (every row-level `emit`), the sources of every row that supports the element, not the last one: a lifted edge names every lower edge, both end nodes and every image it stands for (`sources[]` of `{ graph, element, kind, role }`, sorted by role then element, `sourceCount` of distinct sources, `contributions` the number of supporting rows, `complete: true`). `limit` (default 100) and `after` page the list; `next` is the cursor of the next page (null on the last). An element emitted from an aggregate (`Group`, `Rollup`) has only the elements of its group key (`complete: false`); the library projections emit nodes and edges row by row and put counts on top by `emit update`, so the nodes and edges themselves are complete.
- **Reverse lineage.** `GET /graphs/{g}/lineage/reverse?element=ID[&limit=N][&after=CURSOR]` for an element of `g`: `{ graph, element, kind, partOf: [ { graph, element, kind, role, sink, rule } ], count, next, incompleteIn: [graph] }`, the elements of other graphs that name it as a source. `incompleteIn` lists the graphs that read `g` through an aggregate (only group-key elements known there). `404 UnknownElement` for an element not in `g`.
- **`replay --out DIR`** writes what the host serves: per graph `NAME.lineage.ndjson` (one forward record per element, complete, the body of the route above) and `NAME.lineage.reverse.ndjson` (one reverse record per element that is part of something), and for a document graph `NAME.document.md` (the rendered Markdown) and `NAME.document.json` (`format=json`). `manifest.json` names them per graph (`lineage`, `lineageReverse`, `document`, `documentJson`).

## Version 3.12: references alone, meters in the static space, lineage of groups, the image of a lift
- **References alone.** `GET /graphs/{g}/references[?revision=r][&limit=N][&after=ID]` answers `{ graph, revision, references: [ ... ], count, next }`: the entries of `extent.references` (Version 3.11) of the graph at the revision (default head), sorted by id, `limit` (default 1000) per page after the id `after`, `next` the id to continue from (null on the last page). `GET /graphs/{g}/snapshot?references=false` leaves `extent.references` out. Revision routes are `no-cache` and revalidated by the content tag, as before.
- **`replay --out DIR`** also writes `NAME.references.ndjson` (one reference per line, the same entries) and `NAME.meters.json` for every graph; `manifest.json` names them (`references`, `meters`). `NAME.meters.json` is the body of `GET /graphs/{g}/meters`: `{ "meters": [ { "id", "target": "node"|"edge", "label", "unit", "temporality": "delta"|"gauge", "combine", "periodSeconds", "min", "max", "scale", "warn" } ], "thresholds": [ { "graph", "operator", "meter", "above", "enterAfter", "below", "exitAfter", "state", "high", "low" } ], "defaults": {} }`. A meter is a telemetry property of the graph's ontology (`id` is the property name as the telemetry files use it), `max` the largest value in the graph (for scaling), `warn` the `above` of a threshold on it. Telemetry itself is `NAME.telemetry.ndjson` (batches of `{ graph, tick, t, target, property, ids, values }`).
- **Lineage of group emits.** An element emitted from a `Group` (without windows) names every input row of its group: the elements those rows bind (a lifted edge written by `group by` names every lower edge and every node), complete and paged like a row-level emit, forward and reverse. What is still partial: elements emitted from a windowed `Group`, `Rollup`, `Threshold` or `Series` (the elements of the group key only, `complete: false`), and from rows that were reduced to scalar columns by `select` before the emit.
- **The image of a lift is a function.** A lift maps each lower node to one image. `Realizes` (service to capability) is declared `constraint cardinality Realizes per source 0 .. 1` (an edit that adds a second one is rejected; `Conformance.Check` reports it for data that has one), and the capability lift in `acme.biz` states the policy for data that has two anyway: a service with more than one image is not lifted, so no call is counted for two capabilities. `Backs` (pod to service) is not declared a function: a pod that backs two services counts for both in the service tree (each image gets the full count; sums over the images exceed the lower total).

## Version 3.13: the `Lift` operator
- **Kernel.** New operator type `Lift` (parameters `within` Integer nanoseconds, `images` Text `reject|each` optional, `selfLoops` Text `drop|keep`) and node type `LiftState` (`target`, `of`, `above`, `enterAfter`, `high`, `below`, `exitAfter`, `low`, `initial`: the parameters of a `Threshold`, plus the property it sets). Ports `edges` (graph, in), `map` (graph, in), `out` (rows, out: the relation of the lifted windows). Slots of `Lift`: `carry`, `derive`, `keep` (ordered `Assign`: `target` is the property or column, its `value` a `FieldRef`, an expression for `derive`), `state` (ordered `LiftState`), `when` (`Expr`, at most one). Edges: `ofType` (the relationship type lifted to), `liftEdge` (the lower relationship type), `liftVia` (the mapping type); `governs` may point from a readonly `Rule` to a `Lift`. New scalar function `EntityId` (`Entity -> Text`: the id of an element, to match the same element in two graphs). The kernel digest and `kernelDigest` of `GET /palette` and `GET /space` changed.
- **Palette.** `GET /palette` lists `Lift` with `role: "lift"`, `supported: true`, its ports, parameters (`within` carries `quantity: duration`, `images` is optional: without it the mapping must be a function), slots and a strategy text, and `EntityId` among the functions. `POST /palette/skeleton` for kind `Lift` gives the operator and its three ports; the statement is not complete until the three relationship types and the wires of `edges` and `map` are set (a draft with an unfinished Lift answers an `Unprintable` diagnostic naming the node).
- **Text.** `lift NAME as REL from GRAPH EDGETYPE via GRAPH MAPPING [when e] [carry p = q, ...] [keep c = src.p, ...] [derive p = e, ...] [state p of m above A for N then "x" below B for M then "y" initial "z"] [images reject|each] [selfloops keep] [within D];` in a forward projection or a readonly rule. The printer prints the clauses that differ from the defaults (images unsaid when the mapping is a function, `selfloops drop` and `within 24h` unsaid), in the order `when`, `carry`, `keep`, `derive`, `state`, `images`, `selfloops`, `within`. A draft patch that sets a property of the `Lift` or of a `LiftState` node (`within`, `selfLoops`, `images`, `above`, `exitAfter`, ...) prints back as that clause and is checked like any edit; its preview is the preview of the operators it lowers to.
- **Semantics** (README, "Edge lifting"): for every edge of `EDGETYPE` whose two ends each have exactly one image through `MAPPING` (images restricted by `when`; with `images each` a node may have several), the relationship `REL` between the images, unless the images are one element (`selfloops keep` keeps them); one element per pair of images, keyed `NAME`; the carried telemetry is combined per window with the combine rule declared on the property of `REL`, the derived properties are computed from the sums per window, the state is a threshold over one of them with hysteresis.
- **Lowering and lineage.** The planner (the checker, when the projection is checked) replaces the Lift node, in a copy of the graph, by the operators it stands for; the node itself stays in the catalog and in the definition facts. The generated operators are named `NAME__edges`, `NAME__images`, `NAME__lifted`, `NAME__windows`, `NAME` (the relation), the emits `NAME.emit.1` to `NAME.emit.N` (the sink names of lineage). `GET /graphs/{g}/lineage` of a lifted edge lists every lower edge and node with the roles `edge`, `source`, `target`, `sourceMapped`, `targetMapped`, `sourceImage`, `targetImage` (Version 3.11 and 3.12 apply: complete, paged, with the reverse route). A diagnostic of a generated operator is reported with the message prefix `lift NAME: ` and the focus of the Lift node. `sattle check --plan` lists the statement and its operators.
- **Refactoring.** `extract`, `inline` and `moveRule` treat a lift statement like a `let` that reads only graph ports (a lift alone cannot leave the statements that read its relation behind: `SharedOperator`). `moveRule` also binds a port that the target projection did not have in the graph space like the source's port of that name.

## Version 3.14: the health tree
- **Hierarchy marker.** `RelationshipType` has an optional `hierarchy` Boolean (text: `relationship Contains (A) -> (B) hierarchy;`). The ontology endpoint (`GET /graphs/{g}/ontology`) gives every relationship type `hierarchy: true|false`; a viewer draws a graph that has a hierarchy relationship type as a tree (parent = source, child = target; roots have no parent). The kernel digest changed.
- **Per-row threshold parameters.** `threshold v by {...} above E for N then "x" below E for N then "y" initial "z" as s`: each of the four numbers may be an expression over numeric data columns of the row instead of a literal; the latest row of a key sets them. In the definition graph the slots `aboveBy`, `belowBy`, `enterBy`, `exitBy` of `Threshold` hold the expressions (a literal stays a property). The palette lists them as slots of `Threshold`.
- **Authoring record kinds** (source `authoring`, payload `kind` plus `id`): `signalRule` (`type`, `meter`, `above`, `enterAfter`, `below`, `exitAfter`, `severity` 1 or 2, `invert`), `policy` (`id` = element key or kind, `mode` worst|share|ignore, `share` for share), `curation` (`action` group|hide|move|critical, `target`, `value` for group and move). A malformed record is rejected and counted. The master has entities `SignalRule`, `RollupPolicy`, `Curation` for them and `Meter` (`key`, `meter`, telemetry `reading`, `MeterOf` to an Azure resource, node or pod) for every metric series. Kubernetes and Prometheus records may carry `cluster` in the envelope meta (a capture of several clusters).
- **The tree graph `healthTree`** (ontology `acme.tree`). Property names are unique in the catalog so that no wire name of an existing graph changes. `Element`: `nodeKey`, `nodeKind` (subscription|resourceGroup|resource|cluster|namespace|workload|pod|node), `nodeName`, `ownSeverity` (the worst own signal: 0 healthy, 1 degraded, 2 unhealthy, -1 none), `ownState`, `rolledSeverity` (after the roll-up policy), `rolledState` (Healthy|Degraded|Unhealthy|Unknown, value roles ok, warning, danger, unknown), `healthyCount`, `degradedCount`, `unhealthyCount`, `monitored` (the elements at and below it that carry signals), `critical`, `hidden`, `curatedBy` (the authored record that curates it), `rollupMode` (worst|share|ignore), `rollupShare`. `Contains (Element) -> (Element)` is the hierarchy, one parent per element; `Rolls` the edges the roll-up follows (graph `treeRollup`). `Signal` (`signalElement`, `signalRule`, `signalSeverity`, `signalState` on|off, `signalAbove`, `signalBelow`, `signalMeter`, `signalEnterAfter`, `signalExitAfter`, `signalInverted`, telemetry `signalValue` per 60 s window) with `Measures (Signal) -> (Element)`, in `healthTree`, so a viewer finds the signals of a node by its `Measures` edges and draws the readings against the thresholds. Evidence: `Evidence` (`evidenceId`, `evidenceKind` warning|activity, `evidenceTime`, `evidenceSummary`, `evidenceDetail`) with `EvidenceFor (Evidence) -> (Element)`, never a child. Authored records in the master: `SignalRule` (`ruleId`, `ruleType`, `ruleMeter`, `ruleAbove`, `ruleEnter`, `ruleBelow`, `ruleExit`, `ruleSeverity`, `ruleInvert`), `RollupPolicy` (`policyTarget`, `policyMode`, `policyShare`), `Curation` (`curId`, `curAction`, `curTarget`, `curValue`), `Meter` (`meterKey`, `meter`, telemetry `reading`). Other graphs: `treeStructure`, `treeSignals`, `treeRollup`, `healthReport` (a document graph). Element keys: `sub/<sub>`, `rg/<sub>/<rg>`, the lower-case Azure resource id for resources and for a cluster that is an AKS resource, `cluster/<label>` for a cluster without one, `ns/<label>/<namespace>`, `wl/<label>/<namespace>/<name>`, `pod/<label>/<namespace>/<name>`, `node/<label>/<name>`, `grp/<name>` for a curated group.
- **`treeState` in the ontology.** For every entity type that has a property with a `treerole`, `GET /graphs/{g}/ontology` has `treeState` next to `presentation`: `{ own, rolledUp, severity, kind, curatedBy, critical, hidden, mode, share, counts: { Healthy, Degraded, Unhealthy } }`, each the wire name of the property that plays the role (`counts` the properties that count the elements in each state). The roles come from `treerole` modifiers in the definition (`nodeKind: Text treerole kind;`).
- **Limits.** The lineage of an element of `treeStructure` stops there (its elements are derived through scalar rows); the lineage of an evidence element names the master event. The roll-up policy `share` is over the monitored elements below an element.

## Version 3.15 (engine stage S0a: defects of the code review)
- **Origins.** A request that carries an `Origin` header is served only when the origin is this machine (`localhost`, `127.0.0.1`, `[::1]`, any port) or was named with `serve --allow-origin ORIGIN[,ORIGIN]`; the response then carries `Access-Control-Allow-Origin: <that origin>` and `Vary: Origin`. Any other origin gets `403 { "code": "ForbiddenOrigin" }` on every route, reads, writes and `/ws` alike. Requests without an `Origin` (the CLI, curl) are served as before. Before: `Access-Control-Allow-Origin: *` on every route.
- **Errors.** A body that is not JSON is `400 BadRequest` (before: a bare 500). `404 UnknownGraph` is answered only for a `/graphs/{g}/...` route whose graph does not exist; another missing key is no longer reported as an unknown graph.
- **Telemetry push.** `telemetry` batches may carry `removed: [ids]` (see "Live push"). Before, a retracted window was never pushed.
- **Subscriptions.** `SlowConsumer` closes a subscription that has 10,000 messages written and not read (before: after 10,000 messages in total).
- **`GET /graphs/{g}/meters`, `thresholds`.** A threshold parameter that is read from the row (`threshold v by {...} above above for enterAfter ...`) is `null` in `above`, `enterAfter`, `below`, `exitAfter`, and `byRow: { "above": "<column>", ... }` names the column it is read from (before: the literal 0). `warn` of a meter is `null` when its threshold's `above` is read from the row.
- **Drafts.** `check`, `preview` and `refactor` report what the draft changed against its base revision (as `commit` always did), not against the head: a commit by someone else no longer shows as a change of the draft. `preview` evaluates with the late horizon of the serving engine.
