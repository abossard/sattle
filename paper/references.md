# References

Stable keys, used as `[key]` in the chapters. "Checked" says where the source was verified: **R01** and **R02** are the research files (opened on 10 November 2026), **opened** means opened for this paper on 10 November 2026. A source that neither research file nor this paper opened is not cited.

## Internal documents

| Key | Document |
|---|---|
| R01 | [research/01-content-review.md](research/01-content-review.md) |
| R02 | [research/02-master-graph-storage-and-scale.md](research/02-master-graph-storage-and-scale.md) |
| R03 | [research/03-projection-language-and-metagraph.md](research/03-projection-language-and-metagraph.md) |
| ARCH | [ARCHITECTURE.md](../brainstorm/contracts/docs/ARCHITECTURE.md) |
| DEC | [DECISIONS.md](../brainstorm/contracts/docs/DECISIONS.md) |
| ADP | [ADAPTERS.md](../brainstorm/contracts/docs/ADAPTERS.md) |
| VER | [VERIFICATION.md](../brainstorm/contracts/docs/VERIFICATION.md) |
| ADRs | `brainstorm/dist/adrs.json` (authoritative ADR source in this repository) |
| ENG | [engine/README.md](../engine/README.md): what exists, deviations, not done (read at evaluation 7) |
| PROTO | [engine/PROTOCOL.md](../engine/PROTOCOL.md): wire protocol, version 3 with 3.1 to 3.11 (read at evaluation 7) |
| PERF | [engine/PERF.md](../engine/PERF.md): engine benchmarks, one run per figure, ten runs for H5, H7, H8, H10 (loaded machine) |
| CAN | [canvas/README.md](../canvas/README.md) and [canvas/PERF.md](../canvas/PERF.md): canvas, protocol notes, frame-rate measurements |
| CAP | [lab/CAPTURE-FORMAT.md](../lab/CAPTURE-FORMAT.md) and [lab/README.md](../lab/README.md): envelope, sources, flow logs, forwarder capacity |
| EV | [evidence/](evidence/): outputs of the re-runs of this paper. Fourth evaluation at evaluation 4: `tests-head3.txt`, `check-head3.txt`, `replay-head3-*.txt`, `replay-chunks-head3.txt`, `health-head3.txt`, `document-head3.txt`, `restart-head3.txt`, `retention-ages.txt`, and the unchanged copies of the orchestrator's ten-run output `perf-h5-memory-orch.txt`, `perf-h5-sqlite-orch.txt`, `perf-h7-orch.txt`, `perf-h8-orch.txt`, `perf-h10-orch.txt`. Third evaluation at evaluation 3: `tests-head2.txt`, `check-head2.txt`, `replay-head2-*.txt`, `replay-chunks-merge.txt`, `health-head2.txt`, `document-head.txt`. First evaluation at evaluation 1: tests, check, replays, the chunk-50 investigation, the health sensitivity run. Second evaluation at evaluation 2: `tests-head.txt`, `check-head.txt`, `replay-head-*.txt`, `replay-chunks-head.txt`, `chunk-1-defect.txt`, `health-head.txt`, `deployment-health-head.txt`. Fifth evaluation at evaluation 5 and the canvas of evaluation 5: `check-head4.txt`, `tests-head4.txt`, `replay-head4-*.txt`, `replay-chunks-head4.txt`, `health-head4.txt`, `document-head4.txt`, `restart-head4.txt`, `restart-seal-prefix-head4.txt`, `restart-real-head4.txt`, `retention-ages-head4.txt`, `estate-head4.txt`, `walkthroughs-head4.txt`, `a11y-head4.txt`, `document-live-head4.txt`, this paper's ten-run output `perf-h5-memory-head4-paper.txt`, `perf-h5-sqlite-head4-paper.txt`, `perf-h8-head4-paper.txt`, `perf-h10-{1h,4h}-{seal,noseal}-head4-paper.txt`, `perf-h10-4h-seal38000-head4-paper.txt`, and the unchanged copies of the orchestrator's `perf-h8-orch-turn8.txt`, `perf-h10-1h-orch-turn8.txt`, `perf-h10-4h-orch-turn8.txt`, `perf-h10-1h-seal-orch-turn8.txt`, `perf-h10-4h-seal-orch-turn8.txt` and `cosmos-cap-orch-turn8.txt`. Sixth evaluation at evaluation 6, the canvas of evaluation 6 and the lab of evaluation 6: `check-head6.txt`, `tests-head6.txt`, `replay-head6-*.txt`, `replay-chunks-head6.txt`, `health-head6.txt`, `document-head6.txt`, `restart-head6.txt`, `restart-seal-prefix-head6.txt`, `restart-real-head6.txt`, `retention-ages-head6.txt`, `estate-head6.txt`, `etag-head6.txt`, `bench-overlay-head6.txt`, `mutation-head6.txt`, `walkthroughs-head6.txt`, `a11y-head6.txt`, `document-live-head6.txt`, and this paper's ten-run output `perf-h5-memory-head6-paper.txt`, `perf-h5-sqlite-head6-paper.txt`, `perf-h8-head6-paper.txt`, `perf-h10-{1h,4h}-{seal,noseal}-head6-paper.txt`. Seventh evaluation at evaluation 7: `check-head7.txt`, `tests-head7.txt`, `replay-head7-*.txt`, `replay-chunks-head7.txt`, `health-head7.txt`, `estate-head7.txt`. Eighth evaluation, the canvas of evaluation 8 against the engine of evaluation 7: `canvas-tests-head8.txt`, `walkthroughs-head8.txt`, `a11y-head8.txt`, `demo-estate-head8.txt`, `document-live-head8.txt`. Ninth evaluation at evaluation 9: `tests-head9.txt`, `check-head9.txt`, `replay-head9-*.txt`, `replay-chunks-head9.txt`, `estate-head9.txt`, `references-head9.txt`, `canvas-head9.txt`. Twelfth evaluation at evaluation 12: `replay-head16-105738.txt`, `replay-head16-140542.txt`, `health-head16.txt`. |

Contract and oracle file references (`Model.cs`, `Ports.cs`, `ChangeAlgebra.cs`, `Program.cs`, `ReferenceModel.cs`, `validate_schemas.py`) use the path legend of R01.

## Papers, standards and benchmarks

| Key | Reference | Link | Checked |
|---|---|---|---|
| rfc8785 | A. Rundgren. *JSON Canonicalization Scheme (JCS)*. RFC 8785, June 2020. | <https://www.rfc-editor.org/rfc/rfc8785> | opened (text read: member sorting section 3.2.3, I-JSON number constraint, recommendation to use strings for longer integers) |
| angles2018 | R. Angles. *The Property Graph Database Model*. AMW 2018, CEUR-WS vol. 2100, paper 26. | <https://ceur-ws.org/Vol-2100/paper26.pdf> | opened (title and existence). The statement that edge endpoints must be nodes is taken from R01 section 2.1 |
| budiu2023 | M. Budiu, T. Chajed, F. McSherry, L. Ryzhyk, V. Tannen. *DBSP: Automatic Incremental View Maintenance for Rich Query Languages*. PVLDB 16(7), 2023. DOI 10.14778/3587136.3587137. | <https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf> | R01 (replaces dead link G6), R02 |
| budiu2025 | Extended version. VLDB Journal 34, 2025. DOI 10.1007/s00778-025-00922-y. | | R01 (bibliographic data only) |
| mcsherry2013 | F. McSherry, D. Murray, R. Isaacs, M. Isard. *Differential dataflow*. CIDR 2013. | <https://www.cidrdb.org/cidr2013/Papers/CIDR13_Paper111.pdf> | R02 |
| mcsherry2020 | F. McSherry et al. *Shared Arrangements: practical inter-query sharing for streaming dataflows*. PVLDB 13(10), 2020. | <https://www.vldb.org/pvldb/vol13/p1793-mcsherry.pdf> | R02 |
| kulkarni2012 | K. Kulkarni, J.-E. Michels. *Temporal features in SQL:2011*. SIGMOD Record 41(3), 2012. | <https://sigmodrecord.org/publications/sigmodRecord/1209/pdfs/07.industry.kulkarni.pdf> | R02 |
| schneider1990 | F. B. Schneider. *Implementing fault-tolerant services using the state machine approach: a tutorial*. ACM Computing Surveys 22(4), 1990. | <https://www.cs.cornell.edu/fbs/publications/SMSurvey.pdf> | R02 |
| mimram2013 | S. Mimram, C. Di Giusto. *A categorical theory of patches*. MFPS 2013. | <https://arxiv.org/abs/1311.3903> | R02 |
| shapiro2011 | M. Shapiro, N. Preguiça, C. Baquero, M. Zawirski. *Conflict-Free Replicated Data Types*. SSS 2011, LNCS. DOI 10.1007/978-3-642-24550-3_29. | <https://doi.org/10.1007/978-3-642-24550-3_29> | opened (Crossref: title, authors, year, venue) |
| gonzalez2012 | J. Gonzalez et al. *PowerGraph: Distributed Graph-Parallel Computation on Natural Graphs*. OSDI 2012. | <https://www.usenix.org/system/files/conference/osdi12/osdi12-final-167.pdf> | R02 |
| stanton2012 | I. Stanton, G. Kliot. *Streaming Graph Partitioning for Large Distributed Graphs*. MSR-TR 2011, KDD 2012. | <https://www.microsoft.com/en-us/research/wp-content/uploads/2011/11/Streaming_Partitioning_MSR_TR_ver3.pdf> | R02 |
| berkholz2017 | C. Berkholz, J. Keppeler, N. Schweikardt. *Answering Conjunctive Queries under Updates*. 2017. | <https://arxiv.org/abs/1702.06370> | R02 |
| henzinger2015 | M. Henzinger et al. *Unifying and Strengthening Hardness for Dynamic Problems via the Online Matrix-Vector Multiplication Conjecture*. 2015. | <https://arxiv.org/abs/1511.06773> | R02 |
| pelkonen2015 | T. Pelkonen et al. *Gorilla: A Fast, Scalable, In-Memory Time Series Database*. PVLDB 8(12), 2015. | <https://www.vldb.org/pvldb/vol8/p1816-teller.pdf> | R02 |
| boncz2005 | P. Boncz, M. Zukowski, N. Nes. *MonetDB/X100: Hyper-Pipelining Query Execution*. CIDR 2005. | <https://www.cidrdb.org/cidr2005/papers/P19.pdf> | R02 |
| zhu2020 | X. Zhu et al. *LiveGraph: A Transactional Graph Storage System with Purely Sequential Adjacency List Scans*. PVLDB 13(7), 2020. | <https://www.vldb.org/pvldb/vol13/p1020-zhu.pdf> | R02 |
| deleo2021 | D. De Leo, P. Boncz. *Teseo and the Analysis of Structural Dynamic Graphs*. PVLDB 14(6), 2021. | <https://www.vldb.org/pvldb/vol14/p1053-leo.pdf> | R02 |
| erling2015 | O. Erling et al. *The LDBC Social Network Benchmark: Interactive Workload*. SIGMOD 2015. DOI 10.1145/2723372.2742786. | <https://doi.org/10.1145/2723372.2742786> | opened (Crossref: title, authors, year, venue) |
| nexmark | Apache Beam. *Nexmark benchmark suite*. | <https://beam.apache.org/documentation/sdks/java/testing/nexmark/> | opened (page exists, describes the suite) |
| benchmarkdotnet | BenchmarkDotNet. | <https://benchmarkdotnet.org/> | opened (page exists). No claim about it is made beyond naming the tool |

## Source systems (Ch. 0)

| Key | Reference | Link | Checked |
|---|---|---|---|
| arg-paging | Azure Resource Graph, paging results | <https://learn.microsoft.com/en-us/azure/governance/resource-graph/concepts/paging-results> | R01 (I2) |
| arg-overview | Azure Resource Graph overview | <https://learn.microsoft.com/en-us/azure/governance/resource-graph/overview> | R01 (I2) |
| arg-throttling | ARG guidance for throttled requests | <https://learn.microsoft.com/en-us/azure/governance/resource-graph/concepts/guidance-for-throttled-requests> | R02 |
| arg-changes | ARG resource changes | <https://learn.microsoft.com/en-us/azure/governance/resource-graph/how-to/get-resource-changes> | R02 |
| activity-log | Azure Monitor Activity Log | <https://learn.microsoft.com/en-us/azure/azure-monitor/essentials/activity-log> | R02 |
| k8s-api-concepts | Kubernetes API concepts | <https://kubernetes.io/docs/reference/using-api/api-concepts/> | R01 (I3, current content) |
| k8s-apiserver | kube-apiserver reference (`--event-ttl`) | <https://kubernetes.io/docs/reference/command-line-tools-reference/kube-apiserver/> | R02 |
| k8s-large | Kubernetes, considerations for large clusters | <https://kubernetes.io/docs/setup/best-practices/cluster-large/> | R02 |
| hubble-internals | Cilium, Hubble internals | <https://docs.cilium.io/en/stable/internals/hubble/> | R01 (I1) |
| hubble-flow-proto | Cilium `flow.proto` | <https://github.com/cilium/cilium/blob/main/api/v1/flow/flow.proto> | R01 (I1) |
| hubble-export | Hubble export configuration | <https://docs.cilium.io/en/stable/observability/hubble/configuration/export/> | R02 |
| vnet-flowlogs | Network Watcher VNet flow logs overview | <https://learn.microsoft.com/en-us/azure/network-watcher/vnet-flow-logs-overview> | R01 (I4), R02 |
| nsg-migrate | Network Watcher NSG flow logs migration | <https://learn.microsoft.com/en-us/azure/network-watcher/nsg-flow-logs-migrate> | R01 (I4) |
| otel-resource | OpenTelemetry resource specification | <https://opentelemetry.io/docs/specs/otel/resource/> | R01 (I5) |
| otel-schemas | OpenTelemetry telemetry schemas | <https://opentelemetry.io/docs/specs/otel/schemas/> | R01 (I5) |

## Azure and platform documentation (Ch. 11, 13)

All read in R02 (10 November 2026).

| Key | Page |
|---|---|
| cosmos-limits | <https://learn.microsoft.com/en-us/azure/cosmos-db/concepts-limits> |
| cosmos-batch | <https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch> |
| cosmos-occ | <https://learn.microsoft.com/en-us/azure/cosmos-db/database-transactions-optimistic-concurrency> |
| cosmos-changefeed | <https://learn.microsoft.com/en-us/azure/cosmos-db/change-feed> |
| cosmos-changefeed-modes | <https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/change-feed-modes> |
| cosmos-changefeed-processor | <https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/change-feed-processor> |
| cosmos-hpk | <https://learn.microsoft.com/en-us/azure/cosmos-db/hierarchical-partition-keys> |
| cosmos-partitioning | <https://learn.microsoft.com/en-us/azure/cosmos-db/partitioning-overview> |
| cosmos-index-policy | <https://learn.microsoft.com/en-us/azure/cosmos-db/index-policy> |
| cosmos-serverless | <https://learn.microsoft.com/en-us/azure/cosmos-db/throughput-serverless> |
| cosmos-emulator | <https://learn.microsoft.com/en-us/azure/cosmos-db/emulator-linux> |
| adx-ingestion | <https://learn.microsoft.com/en-us/azure/data-explorer/ingest-data-overview> |
| adx-streaming | <https://learn.microsoft.com/en-us/azure/data-explorer/ingest-data-streaming> |
| adx-eventhub | <https://learn.microsoft.com/en-us/azure/data-explorer/ingest-data-event-hub-overview> |
| adx-cursors | <https://learn.microsoft.com/en-us/kusto/management/database-cursor> |
| adx-retention | <https://learn.microsoft.com/en-us/kusto/management/retention-policy> |
| adx-cache | <https://learn.microsoft.com/en-us/kusto/management/cache-policy> |
| adx-matviews | <https://learn.microsoft.com/en-us/kusto/management/materialized-views/materialized-view-overview> |
| adx-timeseries | <https://learn.microsoft.com/en-us/kusto/query/time-series-analysis> |
| azmon-prom | <https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/prometheus-metrics-overview> |
| azmon-metrics | <https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/data-platform-metrics> |
| azmon-limits | <https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/service-limits#prometheus-metrics> |
| azmon-ingest-limits | <https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/azure-monitor-workspace-monitor-ingest-limits> |
| aks-quotas | <https://learn.microsoft.com/en-us/azure/aks/quotas-skus-regions> |
| aks-cilium | <https://learn.microsoft.com/en-us/azure/aks/azure-cni-powered-by-cilium> |
| arm-limits | <https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits> |
| eventhubs-quotas | <https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-quotas> |
| eventhubs-scalability | <https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-scalability> |
| sqlite-wal | <https://www.sqlite.org/wal.html> |
| arrow-columnar | <https://arrow.apache.org/docs/format/Columnar.html> |
| arrow-dotnet | <https://github.com/apache/arrow-dotnet> |
| arrow-status | <https://arrow.apache.org/docs/status.html> |
| dotnet-loh | <https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap> |
| dotnet-arraypool | <https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1> |
| dotnet-spans | <https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/> |
| dotnet-simd | <https://learn.microsoft.com/en-us/dotnet/standard/simd> |
| dotnet-tensor | <https://learn.microsoft.com/en-us/dotnet/api/system.numerics.tensors.tensorprimitives> |
| dotnet-frozen | <https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary-2> |
| dotnet-channels | <https://learn.microsoft.com/en-us/dotnet/core/extensions/channels> |

The store evaluation of Ch. 11 section 11.4 summarizes verdicts. The documentation pages behind each verdict (PostgreSQL, Neo4j, Memgraph, Fabric graph, Cosmos Gremlin, Kusto graph) are listed in R02 section 4.1 and are not repeated here, because no number or claim of the chapters rests on them alone.

## Sources for Ch. 2 to 15

"Checked": **R01**, **R03** (opened by the research on 10 November 2026) or **Crossref** (title, authors, year, venue read from the Crossref record on 10 November 2026 for this paper). Content claims about R03 sources rest on R03 section 1.

| Key | Reference | Link | Checked |
|---|---|---|---|
| pgschema | R. Angles et al. *PG-Schema*. PACMMOD, 2023. | <https://arxiv.org/abs/2211.10962> | R03 |
| pgkeys2021 | R. Angles et al. *PG-Keys: Keys for Property Graphs*. SIGMOD 2021. DOI 10.1145/3448016.3457561. | <https://doi.org/10.1145/3448016.3457561> | Crossref |
| gql | ISO/IEC 39075:2024 *Database languages, GQL*. Publication facts from gqlstandards.org (the ISO page returned 403). | <https://www.gqlstandards.org/> | R03 |
| opencypher | openCypher. | <https://opencypher.org/> | R03 |
| cypher-patterns | Neo4j, Cypher patterns. | <https://neo4j.com/docs/cypher-manual/current/patterns/> | R01 (G3), R03 |
| rdfs | W3C. *RDF 1.1 Schema*. | <https://www.w3.org/TR/rdf11-schema/> | R03 |
| owl2 | W3C. *OWL 2 Overview*. | <https://www.w3.org/TR/owl2-overview/> | R03 |
| shacl | W3C. *Shapes Constraint Language (SHACL)*. | <https://www.w3.org/TR/shacl/> | R03 |
| mof | OMG. *Meta Object Facility 2.5.1*. | <https://www.omg.org/spec/MOF/> | R03 |
| emf | Eclipse Modeling Framework. | <https://eclipse.dev/emf/> | R03 |
| graphql | GraphQL specification, October 2021. | <https://spec.graphql.org/October2021/> | R03 |
| k8s-crd | Kubernetes, custom resource definitions and their versioning. | <https://kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definitions/> | R03 |
| backstage | Backstage software catalog, system model and descriptor format. | <https://backstage.io/docs/features/software-catalog/system-model> | R01, R03 |
| smartscape | Dynatrace Smartscape. | <https://docs.dynatrace.com/docs/analyze-explore-automate/smartscape> | R01, R03 |
| otel-metrics | OpenTelemetry metrics data model. | <https://opentelemetry.io/docs/specs/otel/metrics/data-model/> | R03 |
| foster2007 | J. N. Foster et al. *Combinators for bidirectional tree transformations*. ACM TOPLAS 29(3), 2007. | <https://www.cis.upenn.edu/~bcpierce/papers/lenses-full.pdf> | R01 (G2), R03 |
| hofmann2011 | M. Hofmann, B. Pierce, D. Wagner. *Symmetric lenses*. POPL 2011. DOI 10.1145/1926385.1926428. | <https://www.cis.upenn.edu/~bcpierce/papers/symmetric.pdf> | R03, Crossref |
| diskin2011 | Z. Diskin, Y. Xiong, K. Czarnecki. *From state- to delta-based bidirectional model transformations*. Journal of Object Technology, 2011. | <https://www.jot.fm/issues/issue_2011_01/article6.pdf> | R03 |
| bancilhon1981 | F. Bancilhon, N. Spyratos. *Update semantics of relational views*. ACM TODS, 1981. DOI 10.1145/319628.319634. | <https://doi.org/10.1145/319628.319634> | Crossref |
| anjorin2016 | A. Anjorin, E. Leblebici, A. Schürr. *20 Years of Triple Graph Grammars*. ECEASST 73, 2016. | <https://eceasst.org/index.php/eceasst/article/view/2202> | R01 (G1), R03 |
| hermann2013 | F. Hermann et al. SoSyM, 2013. On conditions for correctness and completeness of TGG-derived operations (title not recorded in R01). | | R01 (tgg-correct) |
| barkowsky2024 | M. Barkowsky, H. Giese. SoSyM 24, online November 2024. On version-aware TGGs and multi-version pattern matching (R01: title truncated). | | R01 (tgg-version) |
| gottmann2016 | S. Gottmann et al. BX 2016, CEUR-WS 1571. On multi-view consistency (title not recorded in R01). | | R01 (multi-view) |
| hidaka2010 | S. Hidaka et al. *Bidirectionalizing graph transformations*. ICFP 2010. DOI 10.1145/1863543.1863573. | <https://doi.org/10.1145/1863543.1863573> | R01, Crossref |
| miltner2018 | A. Miltner et al. POPL 2018. On bijective lenses (title not recorded in R01). | | R01 (bijective-lenses) |
| wermelinger2002 | M. Wermelinger, J. L. Fiadeiro. Science of Computer Programming 44(2), 2002. On graph transformation for software architecture reconfiguration (title not recorded in R01). | | R01 (software-arch) |
| mahfoudh2015 | M. Mahfoudh et al. Knowledge-Based Systems 73, 2015. DOI 10.1016/j.knosys.2014.10.007. On ontology change (title not recorded in R01). | <https://doi.org/10.1016/j.knosys.2014.10.007> | R01 |
| spivak2012 | D. Spivak. *Functorial data migration*. Information and Computation, 2012. DOI 10.1016/j.ic.2012.05.001. | <https://doi.org/10.1016/j.ic.2012.05.001> | Crossref |
| fagin2005 | R. Fagin, P. Kolaitis, R. Miller, L. Popa. *Data exchange: semantics and query answering*. Theoretical Computer Science, 2005. DOI 10.1016/j.tcs.2004.10.033. | <https://doi.org/10.1016/j.tcs.2004.10.033> | Crossref |
| green2007 | T. Green, G. Karvounarakis, V. Tannen. *Provenance semirings*. PODS 2007. DOI 10.1145/1265530.1265535. | <https://doi.org/10.1145/1265530.1265535> | Crossref |
| gupta1993 | A. Gupta, I. Mumick, V. Subrahmanian. *Maintaining views incrementally*. SIGMOD 1993. DOI 10.1145/170035.170066. | <https://doi.org/10.1145/170035.170066> | Crossref |
| murray2013 | D. Murray et al. *Naiad: a timely dataflow system*. SOSP 2013. DOI 10.1145/2517349.2522738. | <https://doi.org/10.1145/2517349.2522738> | Crossref |
| chandy1985 | K. M. Chandy, L. Lamport. *Distributed snapshots*. ACM TOCS, 1985. DOI 10.1145/214451.214456. | <https://doi.org/10.1145/214451.214456> | Crossref |
| akidau2015 | T. Akidau et al. *The Dataflow Model*. PVLDB, 2015. | | R01 (dataflow) |
| david2016 | I. Dávid, I. Ráth, D. Varró. SoSyM 17, online 2016. On streaming model transformations (title not recorded in R01). | | R01 (stream-model) |
| steer2020 | B. Steer, F. Cuadrado, R. Clegg. Raphtory: streaming analysis of distributed temporal graphs. Future Generation Computer Systems 102, 2020. | | R01 |

## Not yet cited (named in R01 section 2.5, not opened)

Each must be opened before it is cited: Abiteboul, Hull and Vianu *Foundations of Databases* (R03 opened chapter 15 for stratified negation only; no claim of the chapters rests on it), SQL/PGQ, RDF-star, algebraic graph transformation (Ehrig et al.), patch theory (Jacobson), Stevens on bx in QVT, bitemporal data (Snodgrass), entity resolution (Fellegi and Sunter), streaming graph surveys (Besta et al.), SHACL appendices beyond the R03 summary.
