# Build the demo health graph in Sattle

[Trace a project flow in Canvas](http://localhost:5173/#guide=core-portable-flows) · [Open the CORE health mirror](http://localhost:5173/#guide=core-health) · [Inspect ontology gaps](http://localhost:5173/#guide=core-ontology-gaps)

## Listen for metrics

```sh
./scripts/health-mirror.sh curate-flows --offline
./scripts/health-mirror.sh prepare-project
./scripts/health-mirror.sh listen-diagnostics "$EH_NAMESPACE" "$METRIC_HUB"
```

Azure Monitor publishes `AllMetrics` from each demo Container App to the regional hub. The listener records the partition and sequence, then sends each selected metric to the running graph. Its UTC window comes from the event’s `time` and `timeGrain`.

In a second terminal, run `./scripts/health-mirror.sh verify-diagnostics` to trace live graph values to receipt files. [Open the live readings in Canvas](http://localhost:5173/#guide=core-app-telemetry).

Provision new apps with the [diagnostic setting](projects/ahm-model-demo/container-app-diagnostics.bicep). After inventory refresh, the naming rules assign the app to a class. Unclassified apps appear in `ontologyGaps`.

To replay saved deliveries offline, use `./scripts/health-mirror.sh serve-project` after `prepare-project`.

In a second terminal, start Canvas.

```sh
npm --prefix canvas ci
npm --prefix canvas run dev
```

## Compare with the captured Azure Health Model

```sh
./scripts/health-mirror.sh prepare
./scripts/health-mirror.sh verify all
./scripts/health-mirror.sh verify-rules --draft
./scripts/health-mirror.sh compare-discovery
```

| Check | What to look for |
| --- | --- |
| `verify all` | Entity states, links and signal readings at each Azure checkpoint |
| `verify-rules --draft` | Unmapped and ambiguous names after editing a rule in a temporary draft |
| `compare-discovery` | Each authored flow and its deployed apps, compared with the Azure model after replay |

## Model a user flow

1. Open [the project ontology](projects/ahm-model-demo/flows.json). Find **Run a Task**.
2. Follow a step to its required app class. The naming rules map deployed app names to classes.
3. Apply the file to the local inventory and rebuild the graph.

```sh
./scripts/health-mirror.sh curate-flows --offline
./scripts/health-mirror.sh prepare-project
./scripts/health-mirror.sh compare-discovery
```

Restart the project listener. In Canvas, open **Model user flows in the ontology** and select a step to see its evidence.

## Read metrics and Kusto results

| Source | Measurement | Canvas graph |
| --- | --- | --- |
| Event Hubs diagnostic metrics | CPU, memory, response time and restarts in the interval Azure emitted | `appTelemetry` |
| Log Analytics | HTTP outcomes by route and dependency spans within a request | `workloadFlows`, `appTelemetry` |
| Deployed signal definitions | Configured metric and KQL values, thresholds and evaluated states | `signals` |

```sh
./scripts/health-mirror.sh curate-flows --print-kql routes
./scripts/health-mirror.sh curate-flows --hours 72
./scripts/health-mirror.sh prepare-project
```

Edit [metric settings](projects/ahm-model-demo/metrics.json), the [request query](projects/ahm-model-demo/request-routes.kql) or the [dependency query](projects/ahm-model-demo/request-dependencies.kql). The collector fills in the [resource and time filters](projects/ahm-model-demo/request-scope.kql) from the local deployment inventory. An authenticated Azure CLI is required for a fresh capture.

## Capture this deployment again

Set `MODEL_ID` to the resource ID from the health model’s Azure overview.

```sh
export MODEL_ID="$(jq -r .model.sourceId lab/health-mirror/fixture/model.json)"
export RESOURCE_GROUP_ID="${MODEL_ID%/providers/*}"
./scripts/health-mirror.sh capture "$MODEL_ID"
./scripts/health-mirror.sh inventory "$RESOURCE_GROUP_ID"
```

Capture again after Azure evaluates the signals. If the model configuration changes, use a new `MIRROR_FIXTURE` and `MIRROR_WORK` directory for that version.

```sh
./scripts/health-mirror.sh capture-network "$RAW_CHECKPOINT_DIR" 72
./scripts/health-mirror.sh import-flows "$VNET_ID" "$FLOW_BLOB"
```

`RAW_CHECKPOINT_DIR` is a directory under the fixture’s `raw/`. Flow imports require a downloaded Network Watcher blob from the same VNet. The shell aliases its addresses before publication.

## Replay an archived capture through a separate hub

Set `EH_NAMESPACE` and `EH_HUB` to a dedicated single-partition hub. Stop the local replay server before using its port for the listener.

```sh
npm --prefix lab/capture ci
./scripts/health-mirror.sh listen "$EH_NAMESPACE" "$EH_HUB"
```

After the listener prints `from latest`, run the following in a second terminal.

```sh
./scripts/health-mirror.sh publish "$EH_NAMESPACE" "$EH_HUB"
./scripts/health-mirror.sh verify-stream
./scripts/health-mirror.sh watch "$MODEL_ID" "$EH_NAMESPACE" "$EH_HUB"
```

The inventory watcher refreshes resource metadata every five minutes. Platform metrics enter through the dedicated diagnostic listener.

## Edit the model

| Artifact | What it controls |
| --- | --- |
| `definitions/mirror.module.gkql.json` | Types, identities, labels and graph inputs |
| `definitions/mirror.health.gkql.json` | Archived Azure configuration and child-state rollups |
| `definitions/mirror.projectHealth.gkql.json` | Editable CPU and memory thresholds for the independent project |
| `definitions/mirror.workloadFlows.gkql.json` | Flow, component and app relationships |
| `projects/ahm-model-demo/flows.json` | Project vocabulary, user flows, steps and required app classes |
| `fixture/flow-ontology.json` | Project ontology applied to the captured resource group |
| `definitions/mirror.ontologyGaps.gkql.json` | Unmapped and ambiguous apps |

The user-flow projection uses deployment facts and the project ontology. The separate health mirror uses Azure’s configured signal and rollup rules. The verifier reads Azure’s evaluations from `reference.json`. Captures, reports and edited drafts stay local.
