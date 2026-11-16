# Sattle

Sattle turns what a cloud system reports into graphs that explain it, and keeps them up to date as the input changes. The words are defined once in the [glossary](paper/glossary.md).

```mermaid
flowchart TB
  subgraph IN["Semi-structured data in"]
    SRC["sources<br/>Kubernetes, Hubble, Azure,<br/>Prometheus, authoring"]
    ENV["envelopes<br/>capture files or Event Hub"]
    AD["adapters<br/>Adapters.cs"]
    ROWS["typed rows<br/>sattle.source.*.sattle"]
    SRC --> ENV --> AD --> ROWS
  end
  subgraph DEF["Definitions"]
    ONT["ontology modules .sattle<br/>domain knowledge,<br/>identity domains"]
    KQL["projections .kql<br/>rules in KQL"]
    SP["graph space<br/>acme.space.sattle"]
  end
  ENG["engine<br/>Engine.cs, Operators.cs, Identity.cs<br/>computes on changes only"]
  G["graphs<br/>master, serviceTree,<br/>workloadStructure, workload, ..."]
  HOST["host and server<br/>Host.cs, Server.cs<br/>protocol v3: HTTP history, /ws stream"]
  CV["canvas<br/>EngineSource, store, views"]
  ROWS --> ENG
  DEF --> ENG
  ENG -->|"one patch per changed graph"| G
  G -->|"input of the next projection"| ENG
  G --> HOST -->|"patch events"| CV
  ENG -.->|"replay --out: static files"| CV
```

- Identity domains of the ontology modules make one entity of what several sources report about one thing.
- The same input gives the same graphs, whatever the batching. Envelope format: [CAPTURE-FORMAT.md](lab/CAPTURE-FORMAT.md).

One change, from the cluster to the screen:

```mermaid
sequenceDiagram
  autonumber
  participant K as cluster<br/>Kubernetes API, Hubble
  participant F as forwarder<br/>eh-forward.mjs
  participant H as hub<br/>Event Hub sattle-ingest
  participant L as live input<br/>LiveInput.cs, Live.cs
  participant A as adapters<br/>Adapters.cs
  participant P as projections<br/>Engine.cs, Operators.cs
  participant R as revision commit<br/>Engine.Committed, Host.OnCommit
  participant W as WebSocket<br/>Server.cs /ws
  participant C as canvas<br/>engine.ts, engine-stream.ts
  K->>F: watch event: a pod changed
  F->>H: envelope as the event body
  L->>H: Read(after): PartitionReceiver.ReceiveBatchAsync
  H-->>L: LiveEvent(text, position)
  L->>L: SpaceHost.LiveEnvelope: journal, record stream.ndjson
  L->>A: Replay.Process: adapter.Consume(envelope)
  A-->>L: typed rows
  L->>P: LiveFlush (every 2 s or when idle): rows, Advance(λ)
  P->>P: each projection turns changed rows into changed elements
  P->>R: net edits of each graph
  R->>R: revision n+1 of the graph
  R->>W: Publish {type: "patch", graph, patch}
  W->>C: message
  C->>C: transition, emit, dispatch feed/patch, redraw
  Note over K,L: a recording takes the hub's place: serve CAPTURE --speed N, or replay of stream.ndjson
```


This repository is research in progress: a white paper, a working engine, a canvas, and a lab that captured real Azure data. [Status](#status) says what is proven and what is not.

![The graph space: the master graph and the graphs derived from it](docs/screenshots/graph-space.png)

## Getting started

You need the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) and Node.js 22 or newer. Every step runs on the synthetic estate that ships with the repository, an invented Kubernetes and Azure workload with five failures on a 24 minute timeline. No cloud account is needed. Build once:

```bash
dotnet build engine/src/Sattle.Cli -c Release
```

```bash
npm --prefix canvas install
```

Steps 1 and 2 in one command, with the walkthrough printed: `scripts/demo-replay.sh`.

### 1. Start the engine

```bash
dotnet engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll serve engine/tests/Sattle.Tests/fixtures/estate --cluster synth --port 5080 --speed 20
```

- Wait for the line `serving http://127.0.0.1:5080`. From then on it replays the estate's records at 20 times their speed (the 24 minutes take about 72 seconds) and serves every graph as it changes.

### 2. Start the canvas

```bash
npm --prefix canvas run dev
```

### 3. Follow a failure down to its cause

- Open http://localhost:5173/#source=engine&view=data. The Data graph view shows one graph at a time; pick it in the "Graph" box.
- `master`: everything the sources reported, one entity per thing. `serviceTree`: the services and who calls whom, lifted from the calls between pods. `workloadStructure`: what the workload is made of and what each part depends on, with evidence. `workload`: its health.
- In `workload`, the graph follows the replay: the cache is overloaded at 10:05, the database server is down at 10:11, the storage account stops reporting at the end. Drag the timeline under the graph to any of them.

![The workload graph during the replay: the cache overloaded at 10:05](docs/screenshots/timeline-replay.png)

- Drag the timeline to 10:11 (revision 11) and open the Tree tab. It opens on the path to what is wrong: the database server `pg-shop` carries the origin mark, and every row above it says through which part it is affected.
- Click a row. The same entity is highlighted in every graph, and the Inspector shows it in the master and the other graphs and what it was derived from.

### 4. The other views

- http://localhost:5173/#view=space: the graph space, every graph side by side and which ones are built from which.
- http://localhost:5173/#view=ontology: the ontology modules, their types and relationships, drawn as the graph they are stored as.
- http://localhost:5173/#view=editor: the projection editor, a projection's KQL text with its steps, outline and wires.

### Read how a graph is defined

Projections are written in KQL, the query language of Azure Data Explorer and Log Analytics. Read them in this order:

1. [`acme.svc.serviceDeps.kql`](engine/definitions/acme.svc.serviceDeps.kql): calls between pods become dependencies between services (graph `serviceTree`).
2. [`acme.workload.workloadStructure.kql`](engine/definitions/acme.workload.workloadStructure.kql): what a workload is made of and what each part depends on, with evidence.
3. [`acme.workload.workloadHealth.kql`](engine/definitions/acme.workload.workloadHealth.kql): health from signals, rolled up to the workload (graph `workload`).

The space that binds them to graphs is [`acme.space.sattle`](engine/definitions/acme.space.sattle); the ontology they write is [`acme.workload.sattle`](engine/definitions/acme.workload.sattle).

### Change a projection and see the effect

```bash
dotnet engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll replay engine/tests/Sattle.Tests/fixtures/estate --cluster synth
```

- It prints the elements of each graph and a state digest.
- Change a rule in one of the `.kql` files and run it again. The counts and the digest show what changed. An error names the line in the `.kql` file.
- The Projection editor does the same on a running engine: it stores the text in a draft, checks it, previews the rows after each step, and commits.

### Learn why it works

- [paper/README.md](paper/README.md), then chapters [0](paper/ch00-introduction.md) (the problem), [3](paper/ch03-graphs-and-revisions.md) (graphs and revisions), [5](paper/ch05-projection-language.md) (projections) and [7](paper/ch07-telemetry-plane.md) (telemetry and health). The [glossary](paper/glossary.md) explains every term.
- [Research note 10](paper/research/10-existing-graph-languages.md): why projections are written in KQL.

### Run the tests

```bash
dotnet test engine/Sattle.slnx
```

```bash
dotnet test engine/tests/Sattle.Kql.Tests
```

```bash
npm --prefix canvas test
```

## Contents

- [Getting started](#getting-started)
- [The graph space](#the-graph-space)
- [Portfolio](#portfolio)
- [The white paper](#the-white-paper)
- [The engine](#the-engine)
- [The canvas](#the-canvas)
- [The lab](#the-lab)
- [Status](#status)
- [Earlier iteration](#earlier-iteration)

## The graph space

The 13 graphs of [`acme.space.sattle`](engine/definitions/acme.space.sattle). Each arrow into a graph is a projection, labelled `module.projection`.

```mermaid
flowchart LR
  ROWS["rows of the sources"] -->|"ops.ingest"| master
  AUTH["authored rows<br/>capabilities, signal thresholds,<br/>policies, curations"] -->|"ops.authored"| authored
  master -->|"svc.serviceDeps"| serviceTree
  serviceTree -->|"biz.capabilityView"| capabilities
  authored --> capabilities
  master -->|"wl.workloadStructure"| workloadStructure
  master -->|"wl.workloadHealth"| workload
  workloadStructure --> workload
  authored --> workload
  master -->|"dh.deploymentHealth"| deploymentHealth
  deploymentHealth -->|"rpt.deploymentReport"| deploymentReport
  master -->|"tr, th, trep"| TREE["treeStructure, treeSignals, treeRollup,<br/>healthTree, healthReport<br/>(estate tree, paused)"]
  authored --> TREE
```

- Health is not built into the engine: it is the ontology module [`acme.workload`](engine/definitions/acme.workload.sattle) and two projections, [`workloadStructure`](engine/definitions/acme.workload.workloadStructure.kql) and [`workloadHealth`](engine/definitions/acme.workload.workloadHealth.kql).
- Edge lifting (an edge between two pods becomes an edge between their services) is a join: [`serviceDeps`](engine/definitions/acme.svc.serviceDeps.kql), theory in [Ch. 5](paper/ch05-projection-language.md).

## Portfolio

Screenshots of the running engine on the synthetic estate ([generator](engine/tests/Sattle.Tests/fixtures/build-estate.py)); no real names.

### 1. The master graph

![Master graph](docs/screenshots/master-graph.png)

- Everything the sources reported, one entity per thing: [`acme.ops.ingest.kql`](engine/definitions/acme.ops.ingest.kql) under [`sattle.infra`](engine/definitions/sattle.infra.sattle) and [`sattle.azure`](engine/definitions/sattle.azure.sattle). Theory: [Ch. 1](paper/ch01-values-and-identity.md), [Ch. 3](paper/ch03-graphs-and-revisions.md).

### 2. Services

![The service graph serviceTree](docs/screenshots/service-tree.png)

- Calls between pods lifted to dependencies between services, flows summed per pair: [`serviceDeps`](engine/definitions/acme.svc.serviceDeps.kql).

### 3. Capabilities

![Capabilities](docs/screenshots/capabilities.png)

- Service dependencies lifted again to the capabilities the services realize: [`capabilityView`](engine/definitions/acme.biz.capabilityView.kql).

### 4. What a workload is made of and depends on

![Workload structure](docs/screenshots/workload-structure.png)

- Composition top to bottom, dependencies to the right, communication dotted; every dependency carries its evidence: [`workloadStructure`](engine/definitions/acme.workload.workloadStructure.kql), tests in [`WorkloadTests.cs`](engine/tests/Sattle.Tests/WorkloadTests.cs).

### 5. Its health, and which part is to blame

![Workload health at 10:11, the database server down](docs/screenshots/workload-health.png)

- The path from the workload to the origin (`⚑`, the database server) in red; a state is the worst over what a part contains and depends on: [`workloadHealth`](engine/definitions/acme.workload.workloadHealth.kql). Colors are value roles of the ontology. Theory: [Ch. 7](paper/ch07-telemetry-plane.md).

![The same state as a tree of causes](docs/screenshots/workload-tree.png)

- The same graph drawn by its hierarchy, with the Inspector's lineage of the origin: [`tree-model.ts`](canvas/src/app/tree-model.ts), [`TreeView.tsx`](canvas/src/ui/TreeView.tsx).

### 6. A document is a graph too

![Report document](docs/screenshots/report-document.png)

- A graph of [`sattle.doc`](engine/definitions/sattle.doc.sattle) blocks rendered to Markdown with a digest per revision: [`deploymentReport`](engine/definitions/acme.report.deploymentReport.kql), [`Documents.cs`](engine/src/Sattle.Runtime/Documents.cs).

### 7. Editing a projection

![The projection editor on workloadHealth: the KQL text and its steps](docs/screenshots/editor-kql.png)

- KQL text with three notations (Steps, Outline, Wires) that edit the same text, in a draft the engine checks and commits: [`EditorView.tsx`](canvas/src/ui/EditorView.tsx), [`kql-edit.ts`](canvas/src/editor/kql-edit.ts), tests in [`kql-references.test.tsx`](canvas/tests/editors/kql-references.test.tsx), protocol in [PROTOCOL.md](engine/PROTOCOL.md#checking-and-editing-definitions).

### 8. Ontology modules are graphs

![Ontology view](docs/screenshots/ontology.png)

- Types, relationships and identity domains, drawn as the graph they are stored as. Theory: [Ch. 2](paper/ch02-ontologies-as-type-graphs.md).

## The white paper

Start at [paper/README.md](paper/README.md). It states the research plan and what is closed.

| Chapter | Topic |
| --- | --- |
| [0](paper/ch00-introduction.md) | Introduction and research plan |
| [1](paper/ch01-values-and-identity.md) | Values and identity |
| [2](paper/ch02-ontologies-as-type-graphs.md) | Ontologies as type graphs |
| [3](paper/ch03-graphs-and-revisions.md) | Graphs and revisions |
| [4](paper/ch04-change-algebra.md) | Change algebra |
| [5](paper/ch05-projection-language.md) | The projection language |
| [6](paper/ch06-semantics-and-incrementality.md) | Semantics and incrementality |
| [7](paper/ch07-telemetry-plane.md) | The telemetry plane |
| [8](paper/ch08-sync-direction-and-bidirectional-consistency.md) | Direction and bidirectional consistency |
| [9](paper/ch09-evolution-evidence-provenance.md) | Evolution, evidence, provenance |
| [10](paper/ch10-sources-and-adapters.md) | Sources and adapters |
| [11](paper/ch11-storage-and-scale.md) | Storage and scale |
| [12](paper/ch12-outputs-documents-and-canvas.md) | Outputs: documents and canvas |
| [13](paper/ch13-performance.md) | Performance |
| [14](paper/ch14-evaluation.md) | Evaluation |
| [15](paper/ch15-related-work-and-limitations.md) | Related work and limitations |

- Reference pages: [glossary](paper/glossary.md), [notation](paper/notation.md), [references](paper/references.md).
- [Proof inventory](paper/inventory.md): every numbered statement with its status and what tests it.

Research notes behind the paper:

- [Storage and scale](paper/research/02-master-graph-storage-and-scale.md)
- [Projection language and meta-graph](paper/research/03-projection-language-and-metagraph.md)
- [Compute and memory efficiency, persistence, liveness](paper/research/04-compute-and-memory-efficiency.md): budgets per node and edge, a staged plan, experiments.
- [Expressiveness and flexibility](paper/research/05-expressiveness-and-flexibility.md): what the language can and cannot express, with a [capability suite](paper/research/05-suite).
- [Code review findings](paper/research/07-code-review-findings.md): an independent review of engine, tests, canvas and lab.

## The engine

.NET 11. Start at [engine/README.md](engine/README.md).

| Part | Where |
| --- | --- |
| Value algebra, compact graph, kernel table | [`Sattle.Core`](engine/src/Sattle.Core): [`Algebra.cs`](engine/src/Sattle.Core/Algebra.cs), [`Graph.cs`](engine/src/Sattle.Core/Graph.cs), [`Kernel.cs`](engine/src/Sattle.Core/Kernel.cs) |
| Language: parser, checker, lowering, printer | [`Sattle.Language`](engine/src/Sattle.Language): [`Syntax.cs`](engine/src/Sattle.Language/Syntax.cs), [`Checker.cs`](engine/src/Sattle.Language/Checker.cs), [`Lowering.cs`](engine/src/Sattle.Language/Lowering.cs), [`Printer.cs`](engine/src/Sattle.Language/Printer.cs) |
| Runtime: operators, output, identity, journal, drafts | [`Sattle.Runtime`](engine/src/Sattle.Runtime): [`Operators.cs`](engine/src/Sattle.Runtime/Operators.cs), [`Output.cs`](engine/src/Sattle.Runtime/Output.cs), [`Identity.cs`](engine/src/Sattle.Runtime/Identity.cs), [`Journal.cs`](engine/src/Sattle.Runtime/Journal.cs), [`Drafts.cs`](engine/src/Sattle.Runtime/Drafts.cs), [`LineageReader.cs`](engine/src/Sattle.Runtime/LineageReader.cs) |
| Command line and server | [`Sattle.Cli`](engine/src/Sattle.Cli): [`Program.cs`](engine/src/Sattle.Cli/Program.cs), [`Server.cs`](engine/src/Sattle.Cli/Server.cs) |
| Wire protocol | [PROTOCOL.md](engine/PROTOCOL.md): [reads](engine/PROTOCOL.md#reads), [telemetry](engine/PROTOCOL.md#telemetry), [lineage](engine/PROTOCOL.md#lineage), [editing definitions](engine/PROTOCOL.md#checking-and-editing-definitions) |
| Measurements | [PERF.md](engine/PERF.md) |

Definitions in [`engine/definitions`](engine/definitions), all plain text and all loaded as graphs:

- Ontology modules (`.sattle`): [`sattle.infra`](engine/definitions/sattle.infra.sattle), [`sattle.azure`](engine/definitions/sattle.azure.sattle), [`sattle.doc`](engine/definitions/sattle.doc.sattle), and one per example: [`acme.ops`](engine/definitions/acme.ops.sattle), [`acme.svc`](engine/definitions/acme.svc.sattle), [`acme.biz`](engine/definitions/acme.biz.sattle), [`acme.deployhealth`](engine/definitions/acme.deployhealth.sattle), [`acme.workload`](engine/definitions/acme.workload.sattle), [`acme.report`](engine/definitions/acme.report.sattle).
- Row types of the sources: [Kubernetes](engine/definitions/sattle.source.k8s.sattle), [Hubble flows](engine/definitions/sattle.source.hubble.sattle), [Prometheus](engine/definitions/sattle.source.prometheus.sattle), [Azure](engine/definitions/sattle.source.azure.sattle).
- Projections (`.kql`), one file each: [`ingest`](engine/definitions/acme.ops.ingest.kql) (the master) and [`authored`](engine/definitions/acme.ops.authored.kql), [`serviceDeps`](engine/definitions/acme.svc.serviceDeps.kql), [`capabilityView`](engine/definitions/acme.biz.capabilityView.kql), [`deploymentHealth`](engine/definitions/acme.deployhealth.deploymentHealth.kql), [`workloadStructure`](engine/definitions/acme.workload.workloadStructure.kql), [`workloadHealth`](engine/definitions/acme.workload.workloadHealth.kql), [`deploymentReport`](engine/definitions/acme.report.deploymentReport.kql), and the paused estate tree (`acme.tree*`).
- The graph space that binds them into 13 graphs: [`acme.space`](engine/definitions/acme.space.sattle).

How it is tested:

- An independent **reference evaluator** that computes every projection from scratch in the simplest possible way and shares no code with the runtime: [`Sattle.Reference`](engine/tests/Sattle.Reference) ([`ReferenceEvaluator.cs`](engine/tests/Sattle.Reference/ReferenceEvaluator.cs), [`ZSet.cs`](engine/tests/Sattle.Reference/ZSet.cs)).
- A **differential harness** that compares the engine's incremental result with the reference on generated inputs, under every batch split and reordering: [`Sattle.Differential`](engine/tests/Sattle.Differential) ([`Worlds.cs`](engine/tests/Sattle.Differential/Worlds.cs), [`Differential.cs`](engine/tests/Sattle.Differential/Differential.cs)).
- The main suite: [`Sattle.Tests`](engine/tests/Sattle.Tests).

## The canvas

React 19, Fluent UI v9, Zustand, WebGL. Start at [canvas/README.md](canvas/README.md).

- Graph core and rendering: [`core/graph.ts`](canvas/src/core/graph.ts), [`render/renderer.ts`](canvas/src/render/renderer.ts).
- State as actions and a reducer: [`state/reducer.ts`](canvas/src/state/reducer.ts).
- Graph sources: the live engine over one resumable event stream ([`source/engine.ts`](canvas/src/source/engine.ts)) and static files written by the engine ([`source/static.ts`](canvas/src/source/static.ts)).
- Views: Data graph (with the tabs Graph, [Tree](canvas/src/ui/TreeView.tsx) and [Document](canvas/src/ui/DocumentView.tsx)), Graph space, [Projection editor](canvas/src/ui/EditorView.tsx), Ontology.
- Color by declared value roles: [`color-by.ts`](canvas/src/app/color-by.ts).
- Accessibility checks in a real browser: [`a11y-cdp.mjs`](canvas/scripts/a11y-cdp.mjs).

## The lab

An `azd` project that added capture to an existing AKS cluster and removed it again. Start at [lab/README.md](lab/README.md).

- Infrastructure: [`azure.yaml`](lab/azure.yaml), [`main.bicep`](lab/infra/main.bicep).
- Capture format: [CAPTURE-FORMAT.md](lab/CAPTURE-FORMAT.md). Live forwarder to Event Hubs: [`eh-forward.mjs`](lab/capture/eh-forward.mjs).
- Captures and the fixtures derived from them are not in the repository. [`build.sh`](lab/fixtures/build.sh) builds scrubbed fixtures from a capture with [`scrub.mjs`](lab/fixtures/scrub.mjs).
- What the repository does carry is the synthetic estate, written by a [generator](engine/tests/Sattle.Tests/fixtures/build-estate.py). The canvas's default graph source and the public tests use it.

## Status

The work follows two phases. Phase 1 proves the theory, shows the language is flexible and expressive, and makes the engine efficient. Phase 2 is a health model on real Azure data that a person can read and that is always live.

What holds today:

- The engine's incremental result equals the independent reference evaluator in every generated case of the differential harness, under every batch split and reordering.
- The proof inventory has no statement left as a sketch or open. Some proofs rest on named premises about the engine, and most were checked by reading only.
- The language covers relational algebra with aggregation, graph patterns, bounded recursion, windows with thresholds, and edge lifting.
- A master graph of about 50,000 nodes was built from a real 17 minute capture of one cluster, and a health model with roll-up was derived from it.

What does not hold yet:

- **Efficiency.** The engine holds tens of kilobytes per master node. The target is about 1.6 KB at one million nodes on one machine. The plan is in [research note 04](paper/research/04-compute-and-memory-efficiency.md).
- **Expressiveness gaps.** Unbounded reachability, comparison of a window with an earlier one, and "no data for N windows" cannot be expressed. See [research note 05](paper/research/05-expressiveness-and-flexibility.md).
- **Always live.** The engine follows the lab cluster live through Event Hubs since 2026-11-16 ([#70](https://github.com/abossard/sattle/issues/70): a scaled deployment turned Unhealthy in 34 s and back in 4 s, two hard kills resumed from the store). The source cluster runs again ([`source-cluster.sh`](lab/source-cluster.sh)).
- **Persistence.** A local journal with snapshots works. A store-agnostic design that scales is a concept, not built.
- **Known defects** in the canvas and the lab from the [code review](paper/research/07-code-review-findings.md) are not all fixed.

## Earlier iteration

The first iteration was a design site with C# contracts and a test-only reference model. It is kept under [`brainstorm/`](brainstorm/README.md).

- [21-step animated guide](brainstorm/dist/index.html)
- [C# contracts and schemas](brainstorm/contracts)
- [Architecture](brainstorm/contracts/docs/ARCHITECTURE.md)
- Live site: https://health-graph-atlas-ab.ablazeblanket1.chatgpt.site
