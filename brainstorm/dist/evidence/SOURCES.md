# Research sources and design consequences

Reviewed 9 November 2026. Links identify primary documentation or original research. Source behavior is used to inform the contract, not treated as proof that Sattle implements it.

| ID | Source | Design consequence |
|---|---|---|
| O1 | [John Ousterhout, Stanford CS190 design discussion](https://web.stanford.edu/~ouster/cgi-bin/cs190-winter21/lecture.php?topic=bookReview) | Deep execution modules, information hiding, different abstraction per layer; avoid exposing every pipeline phase. |
| G1 | [Anjorin, Leblebici, Schürr: 20 Years of Triple Graph Grammars](https://eceasst.org/index.php/eceasst/article/view/2202) | Explicit source/target correspondence and rule-specific consistency obligations. |
| G2 | [Foster et al.: Combinators for Bidirectional Tree Transformations](https://www.cis.upenn.edu/~bcpierce/papers/lenses-full.pdf) | Retained complement and declared round-trip laws; arbitrary lossy transformations are not automatically lenses. |
| G3 | [Cypher patterns](https://neo4j.com/docs/cypher-manual/current/patterns/) | Typed graph pattern and path vocabulary; no claim of full Cypher compatibility. |
| G4 | [Quine standing queries](https://quine.io/learn/standing-queries/standing-queries/) | Incremental matching and positive/negative result changes. Standalone snapshots and streaming changes must agree. |
| G5 | [Memgraph streams](https://memgraph.com/docs/data-migration/data-streams) | Separate source transport, transform and transactional acknowledgement; batching does not imply exactly-once processing. |
| G6 | [DBSP paper](https://docs.feldera.com/assets/files/vldb23-1bfe30b29f95168c8e1f427fccfc6da2.pdf) | Finite-support integer-weighted collections underpin addition and retraction. |
| G7 | [Feldera: database computations on Z-sets](https://www.feldera.com/blog/database-computations-on-z-sets) | Algebraic deltas and valid graph states have different domains. |
| C1 | [C# async streams](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/generate-consume-asynchronous-stream) | IAsyncEnumerable and cancellation at operational boundaries. |
| I1 | [Hubble internals](https://docs.cilium.io/en/stable/internals/hubble/) and [flow protobuf](https://github.com/cilium/cilium/blob/main/api/v1/flow/flow.proto) | Native flow messages and loss controls; network observation does not prove desired dependency. |
| I2 | [ARG paging](https://learn.microsoft.com/en-us/azure/governance/resource-graph/concepts/paging-results) and [overview](https://learn.microsoft.com/en-us/azure/governance/resource-graph/overview) | Page completion and eventual consistency do not establish an authoritative point-in-time inventory. |
| I3 | [Kubernetes API concepts](https://kubernetes.io/docs/reference/using-api/api-concepts/) | Event objects differ from resource watch changes; opaque resourceVersion, list/watch handoff and expired histories need distinct handling. |
| I4 | [Network Watcher VNet flow logs](https://learn.microsoft.com/en-us/azure/network-watcher/vnet-flow-logs-overview) and [NSG migration](https://learn.microsoft.com/en-us/azure/network-watcher/nsg-flow-logs-migrate) | Prefer VNet flow-log contracts; parse tuple layout by format version. Legacy NSG support is explicit. |
| I5 | [OTel resource](https://opentelemetry.io/docs/specs/otel/resource/) and [telemetry schemas](https://opentelemetry.io/docs/specs/otel/schemas/) | Keep resource identity context and schema URL; distinguish traces, logs and metric temporality. |

Project evidence: current health-engine-architecture-decisions.md version 109 read in the conversation; site source in sattle.zip, including foundations.js, identity.js and platform-model.js. Site examples are illustrative and not a production engine. Live site: https://health-graph-atlas-ab.ablazeblanket1.chatgpt.site/ .
