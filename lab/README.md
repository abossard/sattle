# Sattle Azure lab

Produces real captured data for the engine: recorded NDJSON files and a live Event Hubs stream, from an AKS cluster with Azure CNI powered by Cilium.

## Prerequisites
- `az` and `azd` logged in (`azd auth login`; Owner on the subscription), `az` extensions `aks-preview`, `resource-graph`; `kubectl`, `hubble`, `jq`, `node` 24
- An existing AKS cluster with managed Prometheus. Its names are not in the repo: set them once in the azd environment (`SATTLE_AKS_NAME`, `SATTLE_AKS_RG`, see below); the hooks stop with a message naming the value if one is missing.
- `cd capture && npm install`

## Deploy and tear down (azd)
The azd project root is `lab/` (everything the lab owns stays in one folder). Run all azd commands in `lab/`.
```bash
azd env new sattle --subscription <subscription id> --location <region>
```
```bash
azd env set SATTLE_AKS_RG <aks-resource-group>
azd env set SATTLE_AKS_NAME <aks-name>
```
```bash
azd up
```
```bash
azd down
```
- `azd up` runs `hooks/preprovision.sh` (looks up the AKS-managed VNet and subnet), deploys `infra/main.bicep`, then runs `hooks/postprovision.sh`. `azd provision --preview` shows the what-if.
- Bicep owns only `rg-sattle-lab`: Event Hubs, storage, the private endpoint and DNS zone for flow log reads, role assignments. `azd down` deletes the resource groups of its deployment, so nothing outside the lab resource group is allowed in that template.
- Additions to existing resources are made in `hooks/postprovision.sh` and removed in `hooks/postdown.sh`: Activity Log diagnostic setting, AKS diagnostic setting, VNet flow log in `NetworkWatcherRG`, ACNS observability on the cluster (only undone if the lab switched it on, tracked in `SATTLE_ACNS_BY_LAB`), and the `sattle-demo` namespace.
- `azd down` prints the resources it will delete and asks. Check that only `rg-sattle-lab` is listed. Never use `--force` or `--purge`.
- The lab kubeconfig is `lab/.kube/config`. Every script sets `KUBECONFIG` to it, so your current context is never touched.
- The azd environment lives in `lab/.azure/` (gitignored by `lab/.gitignore`). The capture tooling reads it with `azd env get-values`.

### Measured, and what a second `azd up` needs
- `azd up` on the existing cluster: 3 min 14 s in total (Bicep deployment 2 min 5 s, the rest is the hooks incl. ACNS when it is off). `azd down`: 2 min 23 s in total (the Bicep deployment record shows 3 s, the resource group delete and `postdown.sh` take the rest).
- After `azd down`, measured against a baseline taken before: the AKS resource group (73 resources), the node resource group (21) and `NetworkWatcherRG` (3 left, the lab's flow log gone) are unchanged, ACNS is off and the cluster is `Succeeded`, no lab diagnostic setting, flow log, namespace, private endpoint, DNS zone or role assignment is left.
- Left behind on purpose or by the platform (harmless, not lab resources): subscription deployment records `sattle-<timestamp>` (history only), a soft-deleted storage account `stsattle*`, and ACNS leftovers in `kube-system` (secrets `hubble-server-certs`, `hubble-relay-server-certs`, `hubble-relay-client-certs` and one completed pod `hubble-generate-certs-*`). `advancedNetworking` reads `enabled: false` where it read `null` before the lab.
- A second `azd up` needs nothing new: the azd environment in `lab/.azure/` is kept, `preprovision.sh` looks up the VNet again, `postprovision.sh` re-enables ACNS (because `SATTLE_ACNS_BY_LAB` is `false` again) and reapplies the demo and diagnostic settings, and the Hubble certificates are regenerated. Flow log blobs start empty and appear after a few minutes. Resource names are derived from the subscription, so they repeat; recreating the storage account name after deletion was not tested.
- A reused `lab/.kube/config` is overwritten by `get-credentials` in `postprovision.sh`. `captures/` and the scrub salt are not touched by azd.

## What the lab creates
- Resource group `rg-sattle-lab` (in the azd location), tagged `project=sattle` and `azd-env-name`
- Event Hubs namespace (Standard, 1 TU, local auth off, Entra RBAC) with hubs `sattle-activity`, `sattle-aks`, `sattle-hubble`, `sattle-ingest` (1 partition, one ordered stream), consumer groups `sattle-capture` and `sattle-engine`
- Storage account for VNet flow logs (3 day lifecycle), private endpoint and private DNS zone for it
- Role assignments for the signed-in user: Event Hubs Data Sender and Receiver, Storage Blob Data Reader
- Hooks: Activity Log and AKS (`kube-audit-admin`, `cluster-autoscaler`) diagnostic settings to their hubs, VNet flow log `fl-sattle-aks-vnet` (format v2, 3 day retention), ACNS observability (runs Hubble relay), namespace `sattle-demo` (frontend, api, db, loadgen, and an intruder that network policy denies)

## Capture and stream
```bash
capture/capture.sh 420
```
- Writes `captures/<UTC timestamp>/<source>/*.ndjson` and `SUMMARY.json` with record counts. `captures/` is gitignored.
- During the 420 s window: Kubernetes watch, Hubble follow, a forwarder publishing Hubble flows and Kubernetes watch events to `sattle-ingest` (Hubble also to `sattle-hubble`, `--mirror-hubble`), consumers writing every received event to `eventhub/*.ndjson`, and `demo/churn.sh` creating change events.
- After the window: Prometheus range queries, Azure platform metrics, Activity Log backfill, flow log blobs.
- Single sources can be run alone: `capture/sources/<name>.sh <outdir> [args]`.
- Format of every record: see [CAPTURE-FORMAT.md](CAPTURE-FORMAT.md).

## Fixtures
```bash
fixtures/build.sh captures/<timestamp>
```
- Needs: a capture directory from `capture/capture.sh`, `node` 24. No Azure access and no `az`/`azd` login.
- Writes: `fixtures/<source>/*.ndjson`, a small scrubbed sample of every source (same layout and envelope as the capture). It builds in a temporary directory, verifies, and only then replaces the old files. The salt for the aliases is created once in `captures/.scrub-salt`.
- Also writes `fixtures/space-arguments.json`: the five arguments the engine's space needs for this data (`workloadNamespace`, `clusterLabel`, `aksName`, `nodeResourceGroup`, `hubResourceGroup`), each as the alias the fixture files carry. The engine reads it when it replays `lab/fixtures`; without it the tracked, invented arguments apply. An argument the capture does not show is left out.
- Never writes: anything outside `fixtures/` and `captures/`, and no real value. The files are git-ignored (`lab/fixtures/**/*.ndjson`, `space-arguments.json`); do not commit them.
- Verify is the last step and fails the build, printing counts only, when anything the capture itself holds as identifying (every id, address and e-mail written anywhere in it, resource, group and node names, people behind claims, tag and claim values) is still in the output.
- Record selection (the first records in capture order, so a rebuild from the same capture gives the same files): Resource Graph resources one per type, up to 10; resource containers two per type, up to 4; ARG pages 5; Kubernetes list one per list kind, up to 25; Kubernetes watch one per event type and kind, merged into one file; Hubble flows two per verdict, protocol and direction, up to 14; Prometheus one per query, up to 16; Azure metrics the first record of each file; Activity Log one per operation, up to 10; VNet flow logs two allowed and two denied; Event Hubs three per source, up to 8. The table is `SAMPLE` in `fixtures/scrub.mjs`.
- Scrub rules and what is kept: see CAPTURE-FORMAT.md, section Fixtures.
- Without Azure: the public tests and the default canvas view use the synthetic estate (`engine/tests/Sattle.Tests/fixtures/build-estate.py`, owned by the engine). `lab/fixtures` is only for people who captured their own data.
- Tests (scrubber, hooks with stand-ins for `az` and `azd`, forwarder sink and consumer stop rule; no Azure needed): `cd capture && npm test`.

## From a fresh clone to fixtures
Run in `lab/`, with the prerequisites above.
1. Deploy the lab. About 3 minutes. From then on Event Hubs costs about 0.03 USD per hour, storage cents per day, ACNS per node hour.
   ```bash
   azd env new sattle --subscription <subscription id> --location <region>
   azd env set SATTLE_AKS_RG <aks-resource-group>
   azd env set SATTLE_AKS_NAME <aks-name>
   azd up
   ```
2. Capture and build the fixtures in one command. About 12 minutes: a 420 s window, the pulls, then about 2 minutes for the scrub and its verify.
   ```bash
   ./capture-to-fixtures.sh 420
   ```
   It is `capture/capture.sh 420` followed by `fixtures/build.sh captures/<timestamp>` on exactly that capture; the two can also be run one after the other, and `build.sh` alone on any older capture. If a stream died during the capture it stops before the build and prints the `build.sh` command to use the incomplete capture anyway.
3. Run the engine's tests that replay the fixtures (they are skipped on a clone without them). They assert properties that hold for any capture (no rejected record, rows of every source, determinism, batch invariance, retention, serve equals replay), not values of one capture. The second command prints the replay summary.
   ```bash
   cd ../engine && dotnet test --settings tests/fixtures.runsettings
   ```
   ```bash
   cd ../engine && dotnet run --project src/Sattle.Cli -- replay ../lab/fixtures
   ```
4. Tear down. About 2.5 minutes. Check that the list `azd down` shows holds only the lab's resource group.
   ```bash
   azd down
   ```

What stays on your machine, and why none of it is committed (all git-ignored):
- `captures/<timestamp>/` (about 1 GB): the raw capture, with real subscription, tenant, resource, user and address values.
- `captures/.scrub-salt`: the secret the aliases are derived from. With it an alias could be linked back to a real value.
- `fixtures/**/*.ndjson`: scrubbed, but derived from one real environment. Its shape (which resource types, how many nodes, which operations ran when) still describes that environment, and a scrub rule can miss a field no one has seen yet. The public tests use the synthetic estate instead.
- `fixtures/space-arguments.json`: the aliases of this capture's cluster and groups for the engine's space. It only fits the fixtures next to it.
- `.azure/` and `.kube/config`: the azd environment and the lab's kubeconfig.

One lab per subscription: the hooks own one Activity Log diagnostic setting and one flow log by fixed names. `preprovision.sh` stops when the lab's resource group already exists and was not created by this azd environment, or when the Activity Log setting already feeds another lab.

## Cost
- ACNS: per node hour for as long as it is enabled on the cluster. Not removed by default.
- Event Hubs Standard, 1 TU: about 0.03 USD per hour while the namespace exists.
- Storage and flow logs: cents per day at this size. Activity Log and AKS audit logs add ingress to Event Hubs.
- Demo workload: a few millicores on existing nodes.
- Run `azd down` when done.

## Flow log blobs
- Tenant policy forces `publicNetworkAccess: Disabled` and no shared keys on storage accounts, so a laptop cannot read the blobs. The flow log is writing anyway (storage `Ingress` metric shows about 0.5 MB per 5 minutes).
- Path: a private endpoint for blob in the AKS subnet plus a private DNS zone linked to the AKS VNet (`infra/modules/flow-reader.bicep`). `capture/sources/flowlogs.sh` starts a short-lived pod `flowlog-reader` in `sattle-demo`, sends your own Entra token to it on stdin, streams the blobs out through `kubectl exec`, and deletes the pod. No identity or role is added to the cluster.
- Alternative not chosen: Traffic Analytics into a Log Analytics workspace. It queries from the laptop with no private network, but returns aggregated `NTANetAnalytics` rows 10 to 60 minutes late, not the native flow tuples.
- Side effect: the private endpoint adds a network interface to the AKS-managed subnet. `azd down` removes it with the resource group.

## Live stream for the engine
```bash
cd capture && node eh-forward.mjs --namespace <eventhub namespace> --kubeconfig ../.kube/config
```
- Runs until `SIGTERM`. Reads Hubble through its own relay port-forward, so it can run next to `capture.sh`.
- Hubs, ordering, markers and failure behaviour: see CAPTURE-FORMAT.md, section Live stream.
- Knobs: `--mirror-hubble`, `HUBBLE_FILTER`, `PROM_POLL_SECONDS`, `AZURE_POLL_SECONDS`, `QUEUE_MAX_MB`, `--no-hubble`, `--no-metrics`.
