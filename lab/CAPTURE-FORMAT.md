# Capture format

For adapter authors. A capture is `captures/<UTC timestamp>/<source dir>/*.ndjson`: one JSON object per line, one envelope per record. Payloads are the source's own JSON, untouched. Nothing here is parsed or renamed by the capture tooling.

## Envelope
```json
{"v":1,"source":"hubble","capturedAt":"2026-11-10T10:41:53.120Z","seq":17,"key":"hubble/<uuid>","meta":{},"payload":{}}
```
- `v`: envelope version, 1.
- `source`: producer, see the table below.
- `capturedAt`: UTC wall clock when the capture process received the record. Not the event time. Event time is inside the payload.
- `seq`: 0-based arrival index inside one wrapper process. Orders records of one file only. Files written by several processes (`kubernetes/watch.*`) have one sequence each.
- `key`: stable delivery key, `<source>/<natural id>`. Same record delivered twice gives the same key. Natural ids are listed per source. Where the record has none, the key is `<source>/sha256:<hash of payload and meta.hashSalt>`.
- `meta`: capture-side context (query, page, resource version). Absent when there is none. Never part of the source's data.
- `payload`: the native record.
- Records that are not JSON become `{"text": "..."}`.

## Sources
| `source` | Directory and files | One record is | Native shape | Natural key |
|---|---|---|---|---|
| `arg` | `arg/resources.ndjson`, `arg/resourcecontainers.ndjson` | one row | Azure Resource Graph, API `2022-10-01`, `resultFormat=objectArray`: `id`, `name`, `type` (lowercase), `location`, `resourceGroup`, `subscriptionId`, `tags`, `properties`, ... | `<meta.query>/<id>` |
| `arg-page` | `arg/pages.ndjson` | one page boundary | `{request, response}`: the request body and the response without `data` (`count`, `totalRecords`, `$skipToken`, `resultTruncated`) | hash |
| `kubernetes-list` | `kubernetes/list.ndjson` | one object from a list call | The Kubernetes object as returned by `kubectl get --raw <path>`. Items in lists carry no `kind` and `apiVersion`, so they are in `meta.list.kind` (for example `PodList`) and `meta.list.apiVersion`. `meta.list.resourceVersion` is the list's consistent snapshot. | `<list kind>/<uid>/<resourceVersion>` |
| `kubernetes-watch` | `kubernetes/watch.<resource>.ndjson` | one watch event | Kubernetes `WatchEvent`: `{type: ADDED\|MODIFIED\|DELETED\|BOOKMARK, object}`. `meta.startResourceVersion` is the list version the watch resumed from. | `<type>/<kind>/<uid>/<resourceVersion>` |
| `hubble` | `hubble/flows.ndjson` | one response of `GetFlows` | Hubble `observe -o jsonpb`: `{flow, node_name, time}`, or `{lost_events, node_name, time}` | `flow.uuid`, `lost:<node>:<time>` |
| `prometheus` | `prometheus/range.ndjson` | one range query | Prometheus HTTP API `/api/v1/query_range` response of the Azure Monitor workspace: `{status, data:{resultType:"matrix", result:[{metric, values:[[ts, "v"]...]}]}}`. `meta` has `name`, `query`, `start`, `end` (unix seconds), `step`. | hash of payload and name |
| `azure-metrics` | `azure-metrics/{aks,loadbalancer,eventhubs}.ndjson` | one Azure Monitor metrics response for up to 20 metrics | `az monitor metrics list` JSON: `{cost, interval, namespace, resourceregion, timespan, value:[{name, unit, timeseries:[{data:[{timeStamp, average, total, maximum}]}]}]}`. `meta` has `resource`, `resourceId`, `interval`, `start`, `end`, `metrics`. | hash |
| `activity-log` | `activity-log/backfill.ndjson` | one Activity Log event | Management API `EventData` as printed by `az monitor activity-log list`: camelCase, `operationName.value`, `status.value`, `caller`, `claims`, `eventDataId`, `eventTimestamp`, `submissionTimestamp` | `eventDataId` |
| `vnet-flow-log` | `vnet-flow-logs/records.ndjson` | one element of `records` of a `PT1H.json` blob | Network Watcher VNet flow log, see the section below. `meta.blob` is the blob path. | hash of the record |
| `eventhub:<hub>` | `eventhub/<hub>.ndjson` | one Event Hubs event | `payload` is the event body parsed as JSON. See below. | `eventhub:<hub>/<partition>/<sequenceNumber>` |

## Live stream (Event Hubs)
Run `capture/eh-forward.mjs` (needs `kubectl`, `hubble`, `az` logged in; Entra only). It runs unattended and writes to the hubs below.

| Hub | Written by | Partitions | Body |
|---|---|---|---|
| `sattle-ingest` | `eh-forward.mjs` | 1 | one envelope per event, as UTF-8 JSON text |
| `sattle-hubble` | `eh-forward.mjs --mirror-hubble` | 2 (only partition 0 is used) | the `hubble` envelopes of `sattle-ingest`, nothing else. Empty unless the forwarder runs with `--mirror-hubble`. |
| `sattle-activity` | Azure diagnostic setting | 2 | `{"records":[...]}`, Activity Log in diagnostic-settings schema (`category`, `operationName` string, `time`, `resultType`, `identity`, `properties`, `correlationId`, `callerIpAddress`, `resourceId`). Differs from the camelCase management API `EventData` in `activity-log/`. One event carries several records. |
| `sattle-aks` | Azure diagnostic setting | 2 | `{"records":[...]}`, categories `kube-audit-admin` and `cluster-autoscaler`. `properties.log` is the Kubernetes audit event as a JSON string. Delivery lags the cluster by minutes. |

- Namespace `evhns-sattle-*` in `rg-sattle-lab`, Standard, 1 TU (about 1 MB/s in, 2 MB/s out), retention 24 h on `sattle-ingest` (the platform may report 7 days on the Azure-written hubs). Entra auth only: roles Azure Event Hubs Data Receiver and Sender.
- Consumer groups per hub: `$Default`, `sattle-capture` (used by `eh-consume.mjs`), `sattle-engine` (reserved for the engine; present once the pending Bicep deploy ran, use `$Default` until then). One reader per group: Event Hubs allows 5 readers per group and partition, and an engine should own its group.
- Ordering: `sattle-ingest` has one partition, so `meta.sequenceNumber` is a total order over everything the forwarder routed: Hubble flows, Kubernetes list and watch records, metrics polls and forwarder markers. The envelope `seq` inside the body is the same order (global per forwarder process, restarts at 0). Azure-written hubs are ordered per partition only.
- Delivery: at least once. A failed batch is retried whole, so a record can appear twice. Deduplicate on `key` (Hubble `flow.uuid`, Kubernetes `<type>/<kind>/<uid>/<resourceVersion>`). Do not rely on `capturedAt` to order events: use `meta.sequenceNumber` and, for event time, the payload.
- Capacity (measured, 1 TU, one sequential sender per hub): a Hubble flow envelope is about 4.4 KB. Clean runs: 86 flows/s unmirrored for 14 min with a relay restart (RSS at most 101 MB, queue near 0), and 62 flows/s mirrored for 8 min. Saturated: 107 flows/s mirrored (hour-long run, 36k events dropped, about 9 % of events duplicated by retries) and 286 flows/s mirrored (5 min, 16k dropped events reported, RSS 244 MB, queue held at 64 MB). In saturation the delivered rate stays near 125 events/s, about 0.55 MB/s, well under the nominal 1 MB/s. Plan for 125 events/s total across hubs: about 125 flows/s unmirrored or 60 flows/s mirrored. Past that the forwarder queues, then drops with a `dropped` marker (the marker count is a lower bound while the hub is overloaded).
- Higher rates, recommendation only: keep mirroring off; raise throughput units (auto-inflate, each TU adds 1 MB/s) before anything else; pipeline several concurrent sends only if ordering is not needed (the single partition requires one in-flight send); filter at the source with `HUBBLE_FILTER` (for example `--verdict DROPPED`, one namespace, or `--not --protocol udp`); aggregate flows per connection and interval in the engine's edge as a derived source, not in this forwarder, which keeps records native.
- Event Hubs `enqueuedTimeUtc` is the broker's clock, `capturedAt` is the forwarder's receive time. Hubble `flow.time` and Kubernetes `metadata` timestamps are the event times.

### What the forwarder puts in the stream
| `source` in the envelope | Payload | Notes |
|---|---|---|
| `hubble` | `{flow, node_name, time}` or `{lost_events, ...}` | Only namespaces `sattle-demo` and `demo` (override `HUBBLE_FILTER`). |
| `kubernetes-list` | one object, no `kind` in items | `meta.list.{kind,apiVersion,resourceVersion}`, `meta.relist` is `start` or `expired`. Always between `relist-start` and `relist-end` markers for the same `meta.path`. |
| `kubernetes-watch` | `{type, object}` | Kinds: namespaces, nodes, pods, services, events, deployments, replicasets, endpointslices. `meta.path`, `meta.startResourceVersion`. |
| `prometheus` | range query response | Polled every 60 s over the last 120 s, step 30 s. Overlapping windows are normal. `meta` as in the table above. |
| `azure-metrics` | metrics response | Polled every 300 s over the last 600 s, for the AKS cluster, its load balancer and the Event Hubs namespace. |
| `sattle-forwarder` | marker `{at, event, ...}` | See below. |

Startup is a relist of every kind (`relist-start`, items, `relist-end`), then watches. A consumer that begins reading at `latest` has no state: it must read from the `start` marker of a relist, or from the beginning of the retained stream, to get a complete picture.

### Markers (`source: sattle-forwarder`)
- `start`, `stop {reason, counts}`: the process began or ended cleanly (`SIGINT`, `SIGTERM` or duration). No `stop` after a `start` means a crash: treat all state as possibly stale and expect a fresh `start` with relists.
- `relist-start {path, reason}` and `relist-end {path, reason, resourceVersion, count}`: a full list of one kind follows between them. `reason` is `start` or `expired`. Objects of that kind not seen between them no longer exist.
- `watch-expired {path, lastResourceVersion}`: the server dropped the resourceVersion (HTTP 410), a relist follows. Watch events between the last one and the relist are lost, the relist repairs them.
- `watch-error {path, code, stderr}`: a watch died and is retried from its last resourceVersion with backoff. No events are lost.
- `hubble-reconnect {exitCode, ranSeconds, retryInMs}`: the relay port-forward or CLI died. After reconnecting the forwarder replays what the relay still holds from the gap start (`--since`), so duplicates are possible (dedupe on `key`) and flows older than the relay's ring buffer are lost. Flow loss on the node side is reported by Hubble as `lost_events` records.
- `poll-failed {poll, code}`: a metrics poll failed, the next interval retries.
- `dropped {count, reason, from, to}`: `reason` is `queue-full` or `non-json`. For `queue-full`, Event Hubs was unreachable long enough that the bounded queue (64 MB of event text per hub, `QUEUE_MAX_MB`) overflowed and the oldest `count` events were dropped. The marker stands in the stream where those events would have been, and `from` and `to` are the times of the oldest and the newest dropped event, so the windows it marks incomplete are the right ones. Each hub gets its own marker (a loss on `sattle-hubble` is marked on that hub). Treat state as stale and wait for the next relist (`start` of a new process) or restart the forwarder. For `non-json`, `count` lines from a child process were not JSON and were skipped.
- `stats {uptimeSeconds, ingest:{sent,queued,dropped}, hubble:{...}, counts, rssMB}` every 60 s.

### Failure behaviour
- Hubble relay or CLI dies: restarted with backoff 1 s doubling to 30 s, marker `hubble-reconnect`, replay from the gap.
- Watch ends (server timeout every 5 min is normal) or errors: resumed from the last seen `resourceVersion`. On expiry: `watch-expired`, relist.
- Event Hubs send fails: retried in order (events a partly failed send already delivered are not sent again), queue bounded, oldest dropped with a `dropped` marker at their place.
- `SIGTERM`/`SIGINT`: children are stopped, queues drained for up to 15 s, a `stop` marker is written, clients closed. Events still queued at exit are reported and the exit status is 1.
- The forwarder does not persist state. A restart starts a new run with relists.

## VNet flow logs
- Storage: container `insights-logs-flowlogflowevent`, one blob per hour and NIC: `flowLogResourceID=/<SUBSCRIPTION ID>_NETWORKWATCHERRG/<WATCHER>_<FLOW LOG NAME>/y=YYYY/m=MM/d=DD/h=HH/m=00/macAddress=<12 hex>/PT1H.json` (upper case). Each blob is `{"records":[...]}`; the capture writes one envelope per element.
- Record: `{time, flowLogGUID, macAddress, category:"FlowLogFlowEvent", flowLogResourceID, targetResourceID, flowLogVersion, operationName:"FlowLogFlowEvent", flowRecords:{flows:[{aclID, flowGroups:[{rule, flowTuples:["..."]}]}]}}`.
  - `flowLogVersion` is 4 in the records (the flow log is configured as format version 2, the state-aware format).
  - `aclID`: the NSG resource id, or a GUID for platform rules. `rule`: `DefaultRule_*`, `UserRule_*` or `PlatformRule`.
  - `targetResourceID`: the VNet. `macAddress`: the NIC the record was captured on (12 bare hex digits).
- Tuple: a comma separated string with 13 fields, for example `1794306078061,10.1.2.3,10.43.245.105,35119,53,17,I,E,NX,1,177,1,270`.

| # | Field | Values |
|---|---|---|
| 0 | timestamp | Unix epoch milliseconds, event time |
| 1, 2 | source IP, destination IP | IPv4 |
| 3, 4 | source port, destination port | |
| 5 | protocol | IANA number: `6` TCP, `17` UDP |
| 6 | direction | `I` inbound, `O` outbound (relative to the NIC) |
| 7 | flow state | `B` begin, `C` continue (periodic), `E` end, `D` denied by a rule |
| 8 | encryption | `NX` not encrypted, `NX_LOCAL_DST`, others for VNet encryption |
| 9, 10 | packets and bytes, source to destination | `0` for `B` and `D` |
| 11, 12 | packets and bytes, destination to source | `0` for `B` and `D` |

- Observed in the first capture (a lab cluster, 2 h 11 min): 522 records, 175,265 tuples, from 10:19:47Z to 12:30:48Z; 85,491 `B`, 85,476 `E`, 4,170 `C`, 128 `D`; 130k UDP (DNS), 45k TCP; `NX_LOCAL_DST` in 6 tuples. Records come from 2 NICs (the 2 nodes).
- Ordering: records inside one blob are in increasing `time`. Tuples inside a record are not sorted by timestamp, sort by field 0. Flows are not grouped by connection: join `B`, `C` and `E` on the 5-tuple (source, destination, ports, protocol) yourself.
- Delay: `record.time` is the time the platform wrote the record, a median of 4 s (3 to 19 s) after the newest tuple in it. A record covers about one minute and tuples older than that can still appear in a later record. The newest record is typically 1 to 2 minutes behind wall clock.
- Re-reading: a blob is appended to for the whole hour, so a later capture contains the same records again. Deduplicate on `meta.blob` plus `payload.time`. `key` is a content hash and is equal for identical records.
- Addresses: pod IPs (the overlay pod CIDR, most tuples) and node IPs both appear as source or destination, plus some public addresses. Join with the Kubernetes list (`status.podIP`) or Hubble for pod identity.
- Fixture: `fixtures/vnet-flow-logs/records.ndjson`, records cut to 3 flows, 3 groups and 5 tuples, denied tuples first.

## Ordering and loss
- `hubble`: ordered by Hubble Relay's merge across nodes, not strictly by time. Within a node, ring buffer order. The capture starts with `--last 3000` per node (history), then follows. The ring buffer is finite. Events dropped by the node are reported as `lost_events` records (none seen in the first capture). Flow `uuid` is unique per flow event.
- `kubernetes-list` then `kubernetes-watch`: the watch starts from each list's `resourceVersion`, so nothing is missed between them. `resourceVersion` is opaque, only comparable per resource kind. `BOOKMARK` events carry only a `resourceVersion`. Watches end after the capture window; a watch the server ends earlier resumes from the last `resourceVersion` it delivered (`meta.startResourceVersion` says where each stretch started). A watch that fails or whose version has expired is reported and makes the capture exit non-zero: the file capture has no relist, so that kind has a hole. Kubernetes `Event` objects are captured as kind `events`.
- `activity-log` backfill: newest first, bounded by `ACTIVITY_MAX` (5000), so a busy subscription is truncated at the old end.
- `prometheus`: values every `step` seconds, gaps where a series did not report. Metric names of Hubble appear only after scraping started.
- Event Hubs: see the live stream section. `eh-consume.mjs` reads to the point each partition had reached when the window closed, then stops (so a forwarder running next to it does not keep it alive); 10 s of quiet and a cap of 2 minutes are the fallbacks.
- `vnet-flow-log`: see the VNet flow logs section.

## Versions
- Kubernetes 1.34. Cilium as shipped by AKS with ACNS observability (Hubble relay, mTLS). Hubble CLI `hubble` from Homebrew. Azure Resource Graph API `2022-10-01`. Event Hubs data plane via `@azure/event-hubs` 6.

## Fixtures
- `fixtures/<same layout>`: a small sample per source (a few records per kind of record), scrubbed by `fixtures/scrub.mjs`. Same envelope. `managedFields` is dropped from Kubernetes objects and Prometheus results are cut to 3 series.
- The scrub is a walk over parsed JSON with a rule per field (`fixtures/scrub/rules.mjs`): keys are never touched, and neither are vocabularies (resource types, operation names, metric names, kinds). Only values change.
- Scrubbing is deterministic under a secret salt kept in `captures/.scrub-salt`: the same real value gets the same alias in every source and in every spelling (aliases are lower case), so references still join. An alias keeps the shape of its value:
  - GUIDs map to GUIDs, and the same 32 digits without dashes map to the same alias without dashes (role assignment ids, internal ids, machine ids, principal ids). IPv4 map to `10.x.y.z` (private) or `198.18.0.0/15` (public). IPv6 map to `fd00::/16`. MACs map to `02:...`.
  - Emails map to `user-<hash>@example.com`. Token claims map to `redacted-<hash>` unless they are public vocabulary or an id (those keep the shape of their value). Tag values and custom role names map to `redacted-<hash>`.
  - Resource group and resource names map to `res-<hash>`, in resource ids, in name fields and in free text, as whole words. Nothing of the real name is kept; `mc_<hash>` marks an AKS node resource group. The names come from the capture itself: the name positions of the resource ids in it, Resource Graph names, node names, the cluster name as Cilium writes it on flows, and the people behind claims and callers. A name is also found after an escape such as `:2F` (Activity Log job ids write `/`, `.` and `-` as `:2F`, `:2E`, `:2D`). A name that is also an ordinary word (a VM called `memory`) is replaced where it stands as a name in a resource id or a name field, and nowhere else. AKS API server host names map to `<hash>.hcp.<region>.azmk8s.io`.
  - Unchanged: loopback and link-local addresses, the Azure wire server `168.63.129.16`, regions, generic names such as `kubernetes` and `default`, and the lab's own names (`sattle-*` hubs, `sattle-demo`).
  - `key` fields that are content hashes are not recomputed. They are opaque.
- `fixtures/space-arguments.json` (written by the build, git-ignored): one JSON object with the arguments of the engine's space for this data, `workloadNamespace` (namespace of the Service in the Kubernetes list), `clusterLabel` (cluster name on the Hubble flows), `aksName` (name of the managed cluster resource), `nodeResourceGroup` (the `mc_...` group in the resource ids), `hubResourceGroup` (group of the Event Hubs namespace). Each value is the alias as the fixture files carry it; an argument the capture does not show is left out.
- `fixtures/build.sh <capture dir> [output dir]` rebuilds and then runs `scrub.mjs verify`. Verify is independent of the scrub: it builds its own list from the capture (every id, address and e-mail written anywhere, resource names, people, tag and claim values) and fails when any of it is in the fixtures. It prints counts only, never the values. `node --test lab/tests/` runs the tests of the scrubber, the hooks and the capture tooling.
