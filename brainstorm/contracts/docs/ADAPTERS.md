# Source adapter contracts

The examples are synthetic source-native fixtures and expected mapping specifications. No vendor parser or API call is implemented. The package does not claim those adapters pass upstream compatibility tests.

| Source | Transport/data boundary | Required interpretation |
|---|---|---|
| Hubble / Cilium | GetFlows protobuf or its documented JSON encoding; distinguish flows and protocol/loss messages | Map observed endpoints and traffic into configured identities and directed observed relationships. Scope endpoint identity by cluster and namespace when only names are available. IP reuse is not timeless identity evidence. Lost events are explicit incompleteness, not inferred deletion. |
| Azure Resource Graph | Native query response pages plus query digest, scope, scan ID, pagination/truncation controls | Preserve IDs and provider types. A resource returned by a query is an observation. Completion of pagination is not strong point-in-time consistency. Delete-by-absence needs a separately declared reconciliation policy; query filters, partial scopes and eventual indexing prohibit an unconditional rule. |
| Kubernetes | events.k8s.io/v1 Event resources separately from ADDED/MODIFIED/DELETED/BOOKMARK/ERROR watch messages | Event warnings are observations about objects, not Kubernetes state changes themselves. ResourceVersion is an opaque resume token. UID and cluster scope can identify a pod; a deployment aggregation uses a different domain. DELETED withdraws the source's contribution; another source may still support the entity. Expired watch history requires relisting. |
| Azure Network Watcher | Versioned VNet flow-log JSON and explicit legacy NSG format binding | Parse tuple layout by flowLogVersion. Do not use one universal positional parser. Direction and source/target are distinct concepts. Flow counters belong to telemetry unless the projection derives graph data such as health. Prefer VNet flow logs; NSG retirement is a source compatibility concern. |
| OpenTelemetry | OTLP traces, logs and metrics, protobuf or OTLP JSON; retain resource attributes, scope and schema URL | A resource supplies identity context, not a guaranteed cross-source canonical identity. Span relationships require trace/parent context; metrics require temporality-aware interpretation. Counters do not automatically change topology. Model service.instance.id separately from service.name grouping. |

Source declarations bind format versions and supported message kinds. Unsupported versions return typed rejections. Metadata such as source, tenant, receipt time and queue position is sidecar input context. The native payload need not contain a Sattle envelope.

GraphObservation contains a stable source contribution key and candidates. A contribution can be replaced or withdrawn without deleting other support for the same resulting graph fact. Identity allocation and conflict resolution remain owned by the engine/projection contracts.

Hubble and Kubernetes watch are naturally streaming; ARG is naturally paged; Network Watcher files are naturally batched; OTLP exports can contain multiple signal records. They all fit IAsyncEnumerable<NativeInput<TNative>>. The number of transport messages is not a graph mutation count.

The fixtures in mapping-scenarios.yaml specify choices made by the example projection. They do not assert that an Unhealthy event always means degraded health, that a network flow proves a business dependency, or that an IP address is a permanent identity.
