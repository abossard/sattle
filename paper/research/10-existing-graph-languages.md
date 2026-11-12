# 10. Which existing language projections should be written in

Research note, 12 November 2026. Nothing here is built. The rewrites in [`10-rewrites/`](10-rewrites/) are written, and the KQL ones are type-checked; none of them has been run.

## Summary

**Recommendation: write projections in KQL, the Kusto Query Language, using its graph models.** KQL is the query language of Azure Data Explorer, Log Analytics, Azure Resource Graph, Microsoft Sentinel and Fabric. A Kusto graph model is a named graph built by a list of steps, each a KQL query that adds nodes or edges, with a schema for its node and edge types. That is already the shape of a projection: the schema is the ontology, the steps are what today's `emit` statements do.

Why KQL and not the graph standard GQL:
- **It has a mature .NET parser.** Microsoft's `Kusto.Language` (source under Apache-2.0; the NuGet package under Microsoft's license terms for the Azure Data Explorer SDK, which allow using it in applications one builds; on NuGet since 2019, 66 versions, 12.4.1 released July 2026) parses KQL, checks names and types against a schema the host supplies, and offers completion. All six rewritten projections pass its checks against the master graph's ontology: 60 node and edge steps, each with the `let` statements it uses. GQL has no .NET parser from its owners, only an ANTLR grammar (a grammar file from which parser code can be generated) that its maintainers describe as tested by spot checks.
- **It covers time.** Windows, sums per window, filling empty windows and state machines over ordered rows are ordinary KQL. No graph standard has anything for time, and the health model is mostly about time.
- **People who work in Azure already read it.**

What we add to KQL, as four library functions and one statement form, each in the style KQL already has:
1. The step statements `nodes <Type> on <id> = <query>` and `edges <Type> from <a> to <b> = <query>`. They are a readable text form of the graph model's own `AddNodes` and `AddEdges` steps.
2. `identity(domain, key, ...)` for nodes whose identity comes from an identity domain.
3. `telemetry(graph, lookback)`, a table of a graph's telemetry by element and window.
4. `hysteresis(...)`, a threshold that must hold for a number of windows. It is written in plain KQL in the library, so Kusto's tools check every call to it; the engine maintains it incrementally.
5. Optional edit settings on a step (`with (edit = ...)`) for editable views.

What goes away from today's syntax: `lift` and its clauses, `series`, `rollup`, `threshold` as statements, `traverse`, `fixpoint`, the three kinds of `emit`, key rules, and the `|>` pipe form with `select` and `group by`. Today's six projections use 28 constructs a reader must learn from us. The KQL versions use 9 additions on top of standard KQL. They are 341 lines against 450 today.

**The path:** the existing `.sattle` definitions keep working. A KQL front end is built that produces the same graph of operators the engine runs today. It replaces today's syntax only when each rewritten projection gives the same results, with the same digests, in the same engine tests.

**The main risks:**
- KQL belongs to Microsoft and has no formal standard. Its exact meaning is its documentation plus the behaviour of Kusto.
- Some KQL constructs cannot be maintained incrementally or are not deterministic, so the engine accepts a stated subset (a profile) and rejects the rest with a message.
- Several of our ontology names are KQL keywords and must be quoted (`['kind']`).

Second choice: GQL with a `CONSTRUCT` statement borrowed from the research language G-CORE. It reads well and its rewrites are the shortest (245 lines). But it needs more additions, three of them deep (time, a statement that returns a graph, identity across graphs), and it has no .NET parser.

---

## 1. What stays fixed, and what the question is

- **Fixed:** the engine and its graph of operators (incremental, deterministic, with history), the master graph, ontologies, identities, and that a projection takes graphs and gives a graph.
- **The question:** only the language a person writes and reads.
- **Two facts about the engine shape the answer.**
  - It keeps every result up to date as data changes, by working on differences (incremental maintenance). So a language construct is usable only if its operator can be maintained that way.
  - It must give the same result for the same recorded input. So a construct that picks an arbitrary row or reads the wall clock cannot be used.

## 2. The candidates

Every fact below comes from the specification, the project's repository, its releases or its package registry, as listed in the sources. "Graph out" means a query can produce a graph that the next query reads, which is what lets projections stack.

| Language | Standard, age | Activity | .NET | Patterns | Paths | Graph out | Time windows | Schema |
|---|---|---|---|---|---|---|---|---|
| **GQL** (ISO/IEC 39075) | ISO standard, published April 2024 | grammar repository last changed December 2025 | no package from the owners; an ANTLR grammar (Apache-2.0) | yes, `MATCH (a:Pod)-[:Calls]->(b)` | yes, quantified paths `->{1,16}` | no: queries return tables; `INSERT` writes into a graph | no | graph types |
| **openCypher** (Neo4j's Cypher, opened) | open specification since 2015, moving towards GQL | release 2024.3 in March 2026 | Neo4j.Driver (a client that sends queries to a Neo4j server) | yes | yes | no: `CREATE` and `MERGE` write into the graph | no | none in the language |
| **SQL/PGQ** (ISO/IEC 9075-16) | ISO standard, 2023 | DuckPGQ, a research extension of DuckDB, active | none | yes, inside `GRAPH_TABLE` | yes | no: the result is a table | through SQL | property graph over tables |
| **Gremlin** (Apache TinkerPop) | Apache project since 2015 | 3.8.2 in September 2026, 4.0 in beta | Gremlin.Net (Apache-2.0), a client; the engine is Java | step by step, not declarative | yes | `subgraph()` returns a graph only on the Java side | no | none |
| **SPARQL** (W3C) with dotNetRDF | W3C Recommendation 2013; 1.2 is a working draft | dotNetRDF 3.5.2 in June 2026 | dotNetRDF (MIT): parser and in-memory engine | yes, over triples | yes, property paths | **yes**: `CONSTRUCT` returns a graph | no | RDFS, SHACL |
| **Datalog** (Soufflé, Differential Datalog, Feldera) | no common standard | Soufflé 2.5 (March 2025); Differential Datalog archived; Feldera (incremental SQL) active | none mature | as rules | yes, recursion | relations in, relations out | no | relations |
| **KQL** with graph semantics | Microsoft language, no ISO standard; Kusto.Language public since 2019 | Kusto.Language 12.4.1 in July 2026, repository active this week | **Kusto.Language** (source Apache-2.0, NuGet package under Microsoft's SDK license terms): parser, type checker, completion; **monaco-kusto** (MIT): browser editor | yes, `graph-match (a)-[c]->(b)` | yes, `-[e*1..16]->`, shortest paths, components | **graph models**: named graphs built by node and edge steps, with a schema | **yes**: `bin`, `summarize`, `make-series` with a fill value, `scan` | graph model schema |

Notes per candidate:
- **GQL.** The international standard for graph queries. Its pattern language is the one everybody converges on, and its linear statements (`MATCH`, `FILTER`, `LET`, `RETURN ... GROUP BY`, `NEXT` to continue) are clean. A query returns a table. The only way to produce a graph is `INSERT`, a write into an existing graph, not a definition of one. The ISO page refused access, so the publication facts come from gqlstandards.org, which says 17 April 2024 while opencypher.org says 11 April.
- **openCypher.** The repository says it is maintained by Neo4j staff in a personal capacity and evolves by adding GQL features. Neo4j's own Cypher supports "the majority of mandatory GQL features". Writing a graph is imperative (`CREATE`, `MERGE` change the graph in place). Turning it into a definition the engine maintains would change what those words mean.
- **SQL/PGQ.** Graph patterns inside SQL; the result is a table. The only implementation found is DuckPGQ, which calls itself a research project and work in progress. No .NET.
- **Gremlin.** A step-by-step traversal language (go out, filter, repeat) rather than a description of a result. Gremlin.Net builds traversals and sends them to a server; there is no engine in .NET. It is a poor match for a graph of operators that is maintained incrementally.
- **SPARQL with dotNetRDF.** The only candidate with a mature .NET query engine, and the only standard whose queries return graphs (`CONSTRUCT`), so queries compose. The data model is triples, and properties on edges need RDF-star or the RDF 1.2 triple terms, which are still drafts. Nothing for time. Few people in Azure write it.
- **Datalog.** The closest in meaning to the engine: relations in, relations out, recursion, and engines that maintain results incrementally exist (Differential Datalog, Feldera's DBSP-based SQL). But there is no common dialect, Differential Datalog is archived, and nothing exists for .NET beyond small community packages.
- **KQL.** Used across Azure Data Explorer, Fabric, Azure Monitor, Sentinel, Azure Resource Graph and Defender. Its graph operators build a graph from tables (`make-graph`) and match patterns in it (`graph-match`, with paths of bounded length). Persistent **graph models** define a named graph by steps with a schema; a snapshot is that graph at one point in time and is not refreshed automatically, which is exactly the part our engine adds. Azure Resource Graph supports only a subset of KQL without the graph operators. The documentation does not say whether the graph operators are preview or generally available.
- **Others with a .NET implementation.** T-SQL graph tables with `MATCH` (SQL Server 2017 and later) with Microsoft's T-SQL parser ScriptDom (MIT); GraphQL with Hot Chocolate; LINQ. None of them returns a graph or covers time. LINQ also needs compiling, which rules out loading projections as data. Small community packages for Cypher, GQL or Datalog exist on NuGet, none above about 25,000 downloads and none from the language owners.

**The two best:** KQL (parser, types, editor, time, graph models) and GQL (the standard pattern language). Both are rewritten below.

## 3. How each construct maps to the engine's operators

| What a projection does | Today | KQL | GQL | Operator in the engine | Kept up to date by differences? |
|---|---|---|---|---|---|
| find a pattern | `match (a: Pod)-[:Calls]->(b)` | `graph("master") \| graph-match (a)-[c]->(b) where labels(c) has "Calls"` | `MATCH (a:Pod)-[:Calls]->(b)` | Match | yes |
| filter, compute | `where`, `select` | `where`, `project`, `extend` | `FILTER`, `LET`, `RETURN` | Filter, Map | yes |
| join | `join a, b on x == y` | `join kind=inner (b) on x` | shared variables, `CALL { }` | Join | yes |
| join keeping unmatched rows | (zeros and ones, then a sum) | `join kind=leftouter` | `OPTIONAL MATCH` | Join plus a difference (lowered, no new operator) | yes |
| union, distinct, constant rows | `union`, `distinct`, `values` | `union`, `distinct`, `datatable` | `UNION ALL`, `DISTINCT`, list literals | Union, Distinct, Values | yes |
| group and aggregate | `group by {..} aggregate {..}` | `summarize ... by` (`count`, `sum`, `countif`, `max`, `arg_max`) | `RETURN ... GROUP BY` | Group | yes |
| paths of bounded length | `traverse ... depth 1 .. 16` | `-[a*1..16]->` with `all(a, ...)` | `-[:Affects]->{1,16}` | Traverse | yes, when only the two ends are kept |
| parameters | `in x: param Integer = 0` | `declare query_parameters(x:long = 0)` | `$x` | parameter | yes |
| a node or edge of the result | `emit entity ... identity ... set` | `nodes T on id = query` (graph model step) | `CONSTRUCT` (G-CORE) | emits | yes |
| telemetry per window | `series ... within`, `rollup` | `telemetry(...)` then `summarize ... by windowStart` | `SERIES(...)`, `AT` | Series, Rollup | yes |
| threshold over windows | `threshold v ... above .. for ..` | `invoke hysteresis(...)` | `HYSTERESIS(...)` | Threshold | yes |
| edges between groups from edges between members | `lift` | joins and `summarize` | `CONSTRUCT` with grouping | Match, Join, Group, emits | yes |

The engine has no outer join today. A left outer join is an inner join plus the rows of the left side whose key has no match, and that difference is already expressible by grouping (research 05). So KQL's `leftouter` and GQL's `OPTIONAL MATCH` need a lowering, not a new operator.

**The profile: what the engine refuses in KQL, and why.**
- **The wall clock:** `now()` and `ago()` outside the library. Time is the engine's logical time, which advances only by recorded inputs.
- **Arbitrary choices:** `take_any` (the documentation calls it indeterministic), `take` and `limit` without a full order, and `sample`.
- **Estimates:** `dcount` (HyperLogLog, which "might return a result that varies between executions"). Distinct counts are written `distinct ... | summarize count()`.
- **`join` without `kind`:** the default, `innerunique`, removes duplicate rows on the left, which is rarely what a projection means. Every join states its kind.
- **General `scan`, `evaluate` plugins and external data.** The one state machine allowed is `hysteresis`.
- **Paths whose intermediate edges are projected.** These enumerate every path, which cannot be kept up to date cheaply. Projecting only the two ends of a path is reachability, which the engine maintains.

Kusto made the same choice for its own incremental feature: a materialized view accepts only an aggregation query and keeps the result up to date by combining a materialized part with the new rows.

## 4. The six projections, rewritten

The full texts are in [`10-rewrites/`](10-rewrites/): `service-tree`, `capabilities`, `deployment-health`, `deployment-report`, `workload-structure`, `workload-health`, each as `.kql` and `.gql`, plus `library.kql`, the four functions of the KQL form.

**Checked how.**
- **KQL:** every statement of every `.kql` file was parsed and type-checked with Kusto.Language 12.4.1, against graph models for the master graph and the input graphs and against the library functions. The result was 60 node and edge steps, each checked together with the `let` statements it uses, and 0 errors. The program is in `10-rewrites/check/`:

```bash
dotnet run --project paper/research/10-rewrites/check -- paper/research/10-rewrites
```

  The checks found real errors on the way: our ontology property `kind` is a KQL keyword and must be written `['kind']` (`order` too), and a mistyped property name is reported. They do not check that a node type named in `labels(x) has "..."` exists, and nothing was run.
- **GQL:** not machine-checked. No .NET GQL parser exists, and generating one from the ANTLR grammar needs a tool that is not installed here. The texts follow the grammar file of opengql/grammar, plus the additions of section 5.

**Counts.** Lines without blank lines and comments. "Own constructs" counts the statements and clauses a reader must learn from us: for today's text all of them, for KQL and GQL only the additions on top of the standard language. Scalar functions are not counted in any column.

| Projection | Lines today | KQL | GQL | Characters today | KQL | GQL | Own constructs today | KQL additions | GQL additions |
|---|---|---|---|---|---|---|---|---|---|
| service tree | 30 | 34 | 29 | 1,485 | 1,804 | 1,513 | 14 | 6 | 6 |
| capabilities (on the service tree) | 31 | 29 | 26 | 1,246 | 1,699 | 1,282 | 15 | 6 | 8 |
| deployment health | 27 | 22 | 15 | 1,630 | 1,105 | 849 | 17 | 3 | 2 |
| report document | 20 | 18 | 13 | 1,114 | 1,118 | 782 | 11 | 3 | 3 |
| workload structure | 195 | 167 | 107 | 16,367 | 11,356 | 8,014 | 21 | 5 | 7 |
| workload health | 147 | 71 | 55 | 10,587 | 4,359 | 3,243 | 24 | 8 | 9 |
| **all six** | **450** | **341** | **245** | **32,429** | **21,441** | **15,683** | **28 distinct** | **9 distinct** | **10 distinct** |

The KQL library adds 27 lines, written once.

Reading the numbers:
- The service tree gets longer in KQL (30 to 34 lines) and the capabilities stay about the same. Today's `lift` hides the joins from pods to services; in KQL they are written out, and every type test is `where labels(x) has "T"`.
- The workload projections get much shorter in both, for three reasons:
  - both languages have outer joins and `union` over different shapes;
  - KQL has local functions (`let svcKey = (...) { ... }`) and graph model steps that take the node type from a column, so one step re-emits every kind of component where today eight blocks do;
  - GQL's `CONSTRUCT` writes several nodes and edges in one pattern.
- GQL is the shortest, but part of that is the additions doing work that is not built anywhere, for example a `CONSTRUCT (c)` that copies a node with its types and properties.

### The service tree, three ways

Today (`engine/definitions/acme.svc.sattle`, the projection only):

```text
projection serviceDeps view forward output acme.svc {
  in master: graph ops;
  retain 4;
  let services = match master (s: infra.Service);
  emit entity ServiceNode from services identity s
    set { cluster: s.cluster, namespace: s.namespace, name: s.name };
  let backed = match master (p: infra.Pod)-[:infra.Backs]->(s: infra.Service)
    |> group by { s: s } aggregate { pods: countDistinct(p) };
  emit update entity ServiceNode from backed identity s set { pods: pods };
  emit entity ServiceGroup from services
    identity serviceGroup(cluster: s.cluster, namespace: s.namespace)
    set { cluster: s.cluster, namespace: s.namespace };
  let groups = services
    |> group by { cluster: s.cluster, namespace: s.namespace } aggregate { services: countDistinct(s) };
  emit update entity ServiceGroup from groups
    identity serviceGroup(cluster: cluster, namespace: namespace)
    set { services: services };
  emit relationship Contains from services key contains:
    serviceGroup(cluster: s.cluster, namespace: s.namespace) -> s;
  lift dependsOn as DependsOn from master infra.Calls via master infra.Backs
    keep cluster = src.cluster, namespace = src.namespace
    derive dropRatio = if(flows > 0, drops / flows, 0.0), flowRate = rate(flows)
    state status of dropRatio above 0.05 for 3 then "degraded" below 0.02 for 3 then "ok" initial "ok"
    images each;
  let serviceOut = dependsOn |> rollup by { s: src } combine { flowsOut: sum(flows) };
  emit update entity ServiceNode from serviceOut identity s set { flowsOut: flowsOut };
  let groupOut = dependsOn |> rollup by { cluster: cluster, namespace: namespace } combine { flowsOut: sum(flows) };
  emit update entity ServiceGroup from groupOut
    identity serviceGroup(cluster: cluster, namespace: namespace) set { flowsOut: flowsOut };
}
```

KQL (`10-rewrites/service-tree.kql`):

```kusto
graph serviceTree : acme.svc;

let services = graph("master")
    | graph-match (s) where labels(s) has "Service"
      project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;
let backing = graph("master")
    | graph-match (p)-[b]->(s) where labels(b) has "Backs"
      project pod = p.id, service = s.id;
// a call between two pods is a call between the services they back; a pod that backs two services counts for both
let calls = graph("master")
    | graph-match (a)-[c]->(b) where labels(c) has "Calls"
      project call = c.id, srcPod = a.id, dstPod = b.id
    | join kind=inner (backing | project srcPod = pod, src = service) on srcPod
    | join kind=inner (backing | project dstPod = pod, dst = service) on dstPod
    | where src != dst
    | join kind=inner (services | project src = service, cluster, namespace) on src;
let flows = calls
    | join kind=inner (telemetry("master", 24h) | where property in ("flows", "drops") | project call = id, windowStart, property, value) on call
    | summarize flows = sumif(value, property == "flows"), drops = sumif(value, property == "drops") by src, dst, cluster, namespace, windowStart;

nodes ServiceNode on service = services;
nodes ServiceNode on service = backing | distinct pod, service | summarize pods = count() by service;
nodes ServiceGroup on group = services
    | summarize services = count() by cluster, namespace
    | extend group = identity("serviceGroup", cluster, namespace);
edges Contains from group to service = services | extend group = identity("serviceGroup", cluster, namespace);
edges DependsOn from src to dst = calls | distinct src, dst;
edges DependsOn from src to dst = flows
    | project src, dst, windowStart, flows, drops, dropRatio = iff(flows > 0, drops / flows, 0.0), flowRate = flows / 60.0;
edges DependsOn from src to dst = flows
    | project src, dst, windowStart, value = iff(flows > 0, drops / flows, 0.0), above = 0.05, enterAfter = 3, below = 0.02, exitAfter = 3
    | invoke hysteresis("degraded", "ok", "ok")
    | project src, dst, status = state;
nodes ServiceNode on src = flows | summarize flowsOut = sum(flows) by src, windowStart;
nodes ServiceGroup on group = flows
    | summarize flowsOut = sum(flows) by cluster, namespace, windowStart
    | extend group = identity("serviceGroup", cluster, namespace);
```

GQL with our additions (`10-rewrites/service-tree.gql`):

```text
CREATE GRAPH serviceTree TYPED acme.svc AS {
  USE master
  MATCH (s:Service)
  OPTIONAL MATCH (p:Pod)-[:Backs]->(s)
  RETURN s, COUNT(DISTINCT p) AS pods GROUP BY s
  NEXT CONSTRUCT (s:ServiceNode {cluster: s.cluster, namespace: s.namespace, name: s.name, pods: pods});

  USE master
  MATCH (s:Service)
  CONSTRUCT (g:ServiceGroup KEY serviceGroup(s.cluster, s.namespace) {cluster: s.cluster, namespace: s.namespace})-[:Contains]->(s)
  RETURN s.cluster AS cluster, s.namespace AS namespace, COUNT(DISTINCT s) AS services GROUP BY cluster, namespace
  NEXT CONSTRUCT (g:ServiceGroup KEY serviceGroup(cluster, namespace) {services: services});

  // a call between two pods is a call between the services they back; a pod that backs two services counts for both
  USE master
  MATCH (a:Pod)-[c:Calls]->(b:Pod), (a)-[:Backs]->(s:Service), (b)-[:Backs]->(t:Service)
  FILTER s <> t
  CONSTRUCT (s)-[:DependsOn]->(t)
  FOR w IN SERIES(c, 'flows', 'drops' WITHIN DURATION 'PT24H')
  RETURN s, t, w.windowStart AS ws, SUM(w.flows) AS flows, SUM(w.drops) AS drops GROUP BY s, t, ws
  NEXT LET dropRatio = CASE WHEN flows > 0 THEN drops / flows ELSE 0.0 END
  CONSTRUCT (s)-[:DependsOn {flows: flows, drops: drops, dropRatio: dropRatio, flowRate: flows / 60.0} AT ws]->(t),
            (s {flowsOut: flows} AT ws)
  RETURN s, t, HYSTERESIS(dropRatio ORDER BY ws ABOVE 0.05 FOR 3 BELOW 0.02 FOR 3 THEN 'degraded' ELSE 'ok' INITIAL 'ok') AS status GROUP BY s, t
  NEXT CONSTRUCT (s)-[:DependsOn {status: status}]->(t);
  ...
}
```

### The deployment health model, three ways

Today the "Deployment without a ready pod still has a row" rule needs a union of zeros and ones followed by a sum. Both standard languages have outer joins.

Today (excerpt):

```text
  let zeros = deployments |> select { c: d.cluster, ns: d.namespace, nm: d.name, n: 0 };
  let ones = readyPods |> select { c: d.cluster, ns: d.namespace, nm: d.name, n: 1 };
  let counts = union zeros, ones;
  let ready = counts |> group by { c: c, ns: ns, nm: nm } aggregate { ready: sum(n) };
  let status = ready |> select { c: c, ns: ns, nm: nm, ready: ready, state: if(ready > minReady, "Healthy", "Unhealthy") };
```

KQL:

```kusto
let status = deployments
    | join kind=leftouter (graph("master")
        | graph-match (p)-[c]->(d) where labels(c) has "ControlledBy"
          project deployment = d.id, ready = coalesce(p.ready, false)
        | summarize readyPods = countif(ready) by deployment) on deployment
    | project deployment, readyPods = coalesce(readyPods, 0)
    | extend state = iff(readyPods > minReady, "Healthy", "Unhealthy");
```

GQL:

```text
  USE master
  MATCH (d:Deployment)
  OPTIONAL MATCH (p:Pod)-[:ControlledBy]->(d) FILTER COALESCE(p.ready, FALSE)
  RETURN d, COUNT(p) AS readyPods GROUP BY d
  NEXT LET state = CASE WHEN readyPods > $minReady THEN 'Healthy' ELSE 'Unhealthy' END
  CONSTRUCT (d:Workload {cluster: d.cluster, namespace: d.namespace, name: d.name, replicas: d.replicas, readyPods: readyPods, state: state});
```

### The roll-up of the workload health model, three ways

The worst own severity over a component and everything that affects it, up to 16 edges away.

Today:

```text
  let reach = comps |> traverse structure via Affects out depth 0 .. 16 from c as d |> select { rk: c.key, dk2: d.key };
  let reachedOwn = join reach, owns on dk2 == k;
  let worst = reachedOwn |> group by { k: rk } aggregate { w: max(own) };
  let unrated = comps |> select { k: c.key, w: 0 - 1 };
  let rated = union unrated, worst;
  let levels = rated |> group by { k: k } aggregate { w: max(w) };
```

KQL:

```kusto
let reach = union
    (comps | project rk = key, dk = key),
    (graph("structure") | graph-match (c)-[a*1..16]->(d) where all(a, labels(a) has "Affects") project rk = c.key, dk = d.key);
let levels = union (comps | project k = key, w = -1),
                   (reach | join kind=inner (owns | project dk = k, own) on dk | summarize w = max(own) by k = rk)
    | summarize w = max(w) by k;
```

GQL:

```text
  USE structure
  MATCH (c:Component)-[:Affects]->{0,16}(x:Component)
  OPTIONAL CALL (x) { FOR o IN TABLE owns FILTER o.k = x.key RETURN MAX(o.own) AS xo }
  OPTIONAL CALL (c) { FOR o IN TABLE owns FILTER o.k = c.key RETURN MAX(o.own) AS own }
  RETURN c, own, MAX(COALESCE(xo, -1)) AS w GROUP BY c, own
```

The roll-up needs no addition in either language: a bounded path pattern plus an aggregate is the roll-up.

## 5. What does not fit, and the smallest additions

| Need | KQL addition | GQL addition | Where another product made the same addition |
|---|---|---|---|
| **A named result graph with an ontology** | `graph serviceTree : acme.svc;` plus `nodes` and `edges` steps, a text form of a Kusto graph model (schema plus `AddNodes` and `AddEdges` steps, with a node type from a column via `LabelsColumn`) | `CREATE GRAPH serviceTree TYPED acme.svc AS { ... }` and `CONSTRUCT` | Kusto graph models; G-CORE, where every query returns a graph and `CONSTRUCT` builds it |
| **Identity** of nodes that are not elements of an input graph | `identity("serviceGroup", cluster, namespace)` | `KEY serviceGroup(cluster, namespace)` | Kusto merges nodes with the same id within a graph (`make-graph`, graph model steps); G-CORE groups constructed nodes by values; PG-Keys (research 03) declare keys in the schema |
| **The same element in several graphs** (shared identity across projections) | an element id read in one graph matches the same id in another: joins on `id` | `CALL (x) { USE master MATCH (x)-... }` on a node from another graph | no language has this; the engine already has it (ids are timeless and shared) |
| **Telemetry per window** | `telemetry(graph, lookback)` returns `id, property, windowStart, value`; `telemetry_filled(..., ttl, fill)` adds the fill value and "no data"; windows over raw rows are standard `bin()` | `FOR w IN SERIES(c, 'flows' WITHIN DURATION 'PT24H' [FILL 0] [NODATA AFTER ...])` and `AT windowStart` on a constructed property | KQL's own time series: `make-series` with `default=` for empty bins |
| **Threshold with a duration** (hysteresis) | `invoke hysteresis(high, low, initial[, nodata])`; thresholds as columns, so a rule row can carry them; the library writes it in KQL with `scan`, and Kusto.Language checks that text | `HYSTERESIS(value ORDER BY ws ABOVE .. FOR .. BELOW .. FOR .. THEN .. ELSE .. INITIAL ..)` | Prometheus alerting rules: `for` holds an alert pending until the condition has held for a duration, and `keep_firing_for` keeps it after the condition clears, to prevent flapping |
| **Roll-up** | none: `graph-match` with `-[a*1..16]->` and `summarize` | none: `->{0,16}` and `GROUP BY` | |
| **Edges between groups from edges between members** (today's `lift`) | none: joins and `summarize` | none: `CONSTRUCT` with grouping | G-CORE's grouping in `CONSTRUCT` |
| **Edits flowing back** (editable views) | `with (edit = "...", createDefaults = "...", deleteSets = "...")` on a step | `EDITABLE CREATE, DELETE, UPDATE name ON CREATE ... ON DELETE ...` | no graph language; SQL's updatable views are the nearest relative (not checked here) |
| **Identities that merge later** (one pod known by uid and by name) | stays in the ingestion of the master graph, not in these projections | same | no language has it |

Three differences in meaning that the KQL profile must state:
- **Edges.** A Kusto graph model keeps duplicate edges as separate edges. The engine keeps one edge per type and pair of ends. The profile defines `edges` steps with the engine's rule.
- **Conflicts.** When two steps set one property of one node differently, a Kusto graph model lets the last step win. The engine reports the conflict and leaves the property out. The profile keeps the engine's rule, which does not depend on the order of steps.
- **Types.** `labels()` in Kusto returns the labels a node was given. The engine's types have supertypes (`WorkloadService` is a `Component`). The profile defines `labels(x) has "Component"` to be true for every subtype.

## 6. Recommendation

**One language: KQL, with the graph model as the shape of a projection.**

What we reuse:
- **Kusto.Language** for parsing, name and type checking, error positions and completion. The host supplies the schema: graph models built from our ontologies, and the library functions. Verified by the check of the 60 steps above.
- **monaco-kusto** for the canvas editor: completion, errors and hovers in the browser, with a schema loaded from the catalog.
- **Microsoft's KQL documentation** for everything except the four functions and the step form.

What we add: the step form (`graph`, `nodes`, `edges`, optional `with (...)`), `identity()`, `telemetry()` and `telemetry_filled()`, `hysteresis()`, and the profile of refused constructs (section 3). The ontology files stay as they are. They carry more than a graph model schema does: identity domains, constraints, telemetry declarations, value roles.

What of today's syntax goes away: `lift` and its clauses, `series`, `rollup`, `combine`, `threshold` as a statement, `traverse`, `fixpoint`, `values`, the three `emit` forms and key rules, `select`, `group by ... aggregate`, and the `|>` pipe.

**Staged path, each stage done only when its test passes:**

| Stage | What | Done when |
|---|---|---|
| 1 | A KQL front end in the engine: parse and check with Kusto.Language against the catalog, then lower to the same graph of operators the engine runs today | each of the six rewritten projections gives the same state digest as today's text on the synthetic estate and on the recorded captures, in the existing engine tests |
| 2 | Both front ends side by side; every new projection in KQL | all definitions converted, the whole engine test suite green with unchanged digests |
| 3 | The canvas editor on monaco-kusto, with the schema from the catalog | an author writes a projection with completion and the engine's errors in place |
| 4 | Today's projection syntax retired; the ontology format stays | no `.sattle` projection left |

An optional check in stage 1: run the batch form of each KQL projection on a small Azure Data Explorer cluster with the same recorded data, and compare the tables with the engine's results. That catches places where our meaning of a KQL construct differs from Kusto's.

**Risks.**
- **Ownership.** KQL is Microsoft's and has no formal specification. Kusto.Language's source is Apache-2.0 and can be forked (the NuGet package itself comes under Microsoft's SDK license terms), but the language evolves where Microsoft takes it. Mitigation: the profile pins the subset we use, and the comparison against Kusto finds drift.
- **Different meaning in places.** `join` defaults to `innerunique`; `count_distinct` is marked preview; `dcount` and `take_any` are not deterministic; graph models keep duplicate edges. The profile refuses or redefines each one and says so in its error messages.
- **Paths.** `graph-match` with variable-length edges enumerates paths. The engine maintains only reachability, so projecting the edges of a path is refused.
- **Keywords.** Ontology names that are KQL keywords (`kind`, `order`) must be quoted. Renaming them in the ontologies avoids this.
- **More text for small projections.** Joins that `lift` hid are written out, so the service tree grows from 30 to 34 lines, and type tests are long (`where labels(x) has "Service"`).
- **The rewrites have not been run.** They are type-checked; whether they give today's results is what stage 1 tests.

## 7. What could not be verified

- The ISO pages for GQL and SQL/PGQ refused access. Publication facts come from gqlstandards.org and opencypher.org, which disagree on the day (17 or 11 April 2024).
- The GQL rewrites were not parsed. Iterating a named table with `FOR x IN TABLE t` is allowed by the grammar file, but its meaning was not checked against the standard's text.
- Whether a graph model step may itself read another graph with `graph()`, which is how projections would stack inside Kusto. The documentation does not say. Our engine does it either way.
- Whether KQL's graph operators are preview or generally available. The documentation does not say.
- That `graph-match` may be followed by `summarize` and `join`. The documentation shows no example, though Kusto.Language accepts it. Nothing was run on a Kusto cluster.
- The list of aggregations allowed in materialized views, and the identity rule for new nodes in G-CORE's `CONSTRUCT`.
- Whether ScriptDom parses T-SQL graph `MATCH`.

## 8. What stage 1 showed

Stage 1 built the KQL front end and tested it on the six projections. A front end is the part of the engine that reads a definition text and turns it into the engine's operators. Kusto.Language reads and type-checks the text; our code translates Kusto's reading of it into the same operators today's text gives. Nothing in the engine changed.

### The result

- **The same graphs.** Each of the six projections, written in KQL, gives the same graph as today's text when the synthetic estate is replayed: every node, every edge, every property and every telemetry window. The same holds on both recorded captures from the lab.
- **The same operators.** A test takes today's version of a projection out of the engine's definitions, puts in what the KQL front end wrote, prints it back to today's text with the engine's own printer, loads it, and finds exactly the operators the front end wrote. So the engine really runs the KQL version.
- **Fast enough to run with every change.** The tests on the estate take about 25 seconds.

### The size

- The front end is about 880 lines of our code: 100 that read the text and build the description of the graphs Kusto checks against, and 780 that translate into operators. The library adds 28 lines written in KQL. Parsing and type checking are Kusto.Language's.
- The six projections take 394 lines of KQL against 450 lines today.

| Projection | Lines today | Lines in KQL |
|---|---|---|
| service tree | 30 | 34 |
| capabilities (without its edit rules) | 31 | 27 |
| deployment health | 27 | 21 |
| deployment report | 20 | 14 |
| workload structure | 195 | 192 |
| workload health | 147 | 106 |

### What the front end accepts

Sixty-two constructs, every one plain KQL except the ten additions below.
- **Statements (10):** the graph line, query parameters, a let of a table, of a value function and of a table function, the four step forms (`nodes`, `edges`, `extend nodes`, `extend edges`), and the `key` setting.
- **Table operators (14):** `graph-match` (patterns of nodes and edges, `labels(x) has "T"`, `where`, `project`), paths of several edges `-[p*0..16]->` with `all(p, labels(p) has "T")`, `where`, `project`, `extend`, `distinct`, `summarize`, `join kind=inner`, `union`, `datatable`, `invoke` of a table function, `invoke hysteresis`, a join with `telemetry` and a join with `telemetry_filled`.
- **Aggregations (10):** `count`, `countif`, `sum`, `min`, `max`, `avg`, `arg_max`, and over telemetry `sumif`, `minif`, `maxif`.
- **Functions (14):** `iff`, `case`, `coalesce`, `strcat`, `tolower`, `not`, `isnotnull`, `isnull`, `toreal`, `rate`, `identity`, `labels`, `all`, and value functions of one's own.
- **Operators (14):** `==`, `!=`, `<`, `<=`, `>`, `>=`, `and`, `or`, `+`, `-`, `*`, `/`, the minus sign and `in`.

### What it refuses, and what it says

The engine keeps every result up to date as data changes, and gives the same result for the same recorded input. What cannot be kept up to date, or would not give the same result twice, is refused with a message that says what to write instead. The messages call the accepted part of KQL "the profile".

| Written | The message |
|---|---|
| `take`, `sample` | take and sample pick arbitrary rows, so the result would depend on the order rows arrive in: filter or summarize instead |
| `sort`, `order by`, `top` | rows of a graph have no order: summarize with min, max or arg_max instead |
| `scan` | a general scan is not kept up to date: write a threshold with invoke hysteresis(...) |
| `evaluate` | plugins are not in the profile |
| `join` without a kind | state the kind of every join: join kind=inner (KQL's default, innerunique, drops duplicate rows of the left side) |
| `join kind=leftouter` and the other kinds | join kind=leftouter is not in the profile: write union and summarize, or kind=inner |
| `dcount` | dcount is an estimate that may vary between runs: write distinct ... \| summarize n = count() |
| `count_distinct` | count_distinct is a preview function: write distinct ... \| summarize n = count() |
| `take_any` | take_any picks an arbitrary value: write min, max or arg_max |
| `now()`, `ago()` | the wall clock is not in a projection: time is the windows of the telemetry |
| the edges of a path in `project` | the edges of a path are not kept: every path would be listed, which is not kept up to date; project the two ends |
| `nodes on id labels col` | a step names its type ...: the engine writes one type per step, so a type taken from a column needs one step per type |
| `with (edit = ...)` | editable views come in a later stage of the KQL front end: this stage refuses with (edit = ...) |
| another setting in `with` | 'x' is not a setting of a step: the one setting is key = "name" |
| `union` of tables with different columns | the tables of a union have the same columns: project each to ... |
| a step that makes a type but leaves out a required property | a step that makes T sets every required property, and p is not set: set it, or write extend nodes to add properties to nodes another step makes |
| `extend nodes` when no step makes the type | no step makes T: extend nodes adds properties to nodes another step of this projection makes |
| two steps that make edges of one type with one key | two steps make T edges with the key 'k': give one its own key with (key = "..."), or write extend edges |
| a column the type has no property for | T has no property 'c': project it away |
| several telemetry properties used before summarizing | summarize the telemetry rows of several properties by their window before projecting them: summarize v = sumif(value, property == "p") by ..., windowStart |
| any other operator or function | x is not in the KQL profile, or f() is not a function of the profile |

### The ten additions, each with an example

1. **The graph line** names the projection and the ontology of its result: `graph serviceDeps : acme.svc;`
2. **`nodes`** makes nodes from the rows of a query: `nodes ServiceNode on service = services;`
3. **`edges`** makes edges: `edges DependsOn from src to dst = calls | distinct src, dst;`
4. **`extend nodes` and `extend edges`** add properties to what another step makes, in KQL's own word for adding columns: `extend nodes ServiceNode on service = backing | distinct pod, service | summarize pods = count() by service;`
5. **`with (key = "...")`** names the key of an edge, which is part of its identity: `edges Contains from s to d with (key = "serviceDeployment") = placed | project s, d;`
6. **`identity(domain, ...)`** gives the identity of a node that is in no input graph: `project group = identity("serviceGroup", cluster, namespace)`
7. **`telemetry(graph, lookback)`** is the telemetry of a graph, one row per element, property and window: `join kind=inner (telemetry("master", 24h) | where property in ("flows", "drops") | project call = id, windowStart, property, value) on call`
8. **`telemetry_filled(graph, lookback, timeToLive, fill)`** fills an empty window and marks a silent element as "no data": `telemetry_filled("master", 30m, 5m, 0.0)`
9. **`invoke hysteresis(high, low, initial[, noData])`** is a threshold that must hold for a number of windows: `invoke hysteresis("degraded", "ok", "ok")`
10. **`rate(x)`** is a value per second over the period of its property: `flowRate = rate(flows)`. KQL has no units, so no KQL expression can give a value the unit "per second" that the property declares.

Of the nine additions in section 5, the step whose type comes from a column (`nodes on id labels col`) is not in stage 1; `extend` took its place in the count.

### Where our meaning differs from Kusto's documented one

1. One edge per type, key and pair of ends. Kusto keeps duplicate edges. Two keys on the same ends and type compete; one edge wins and takes the other's telemetry.
2. When two steps set one property differently, the engine reports a conflict and sets no value. Kusto takes the last step.
3. Dividing two integers is exact. KQL divides integers to an integer.
4. `labels(x) has "T"` also holds for every subtype of T.
5. `telemetry(g, 24h)` looks back from the newest window of the recorded input, not from the wall clock.
6. When `arg_max` has ties, the engine decides which row wins. Kusto returns any of the tied rows.
7. A `union` needs the same columns. Kusto fills missing columns with nulls.

### Rules the engine adds

These are checks with a message, not different results: a step that makes a type sets all its required properties; two steps that make edges of one type need different keys; every column of a step is a property of its type. One rule removes a difference that is only in typing: `toreal` of a value the engine already holds as a decimal is that value (in KQL every telemetry value is real).

### Repetition without new words

KQL's own functions with a table parameter, `let f = (T:(a:string, ...), p:string) { T | ... };`, are written out where they are called. In the workload health, the three blocks that read meters and warning events against the rules became two functions, and the three thresholds one call each: 111 lines and 12,603 characters became 106 lines and 12,146 characters. In the workload structure, the six blocks of evidence became three functions, and six steps that made evidence nodes became one step over a `union`: 194 lines and 19,898 characters became 192 lines and 18,531 characters. This is what research 08 wanted generators for, with nothing invented. The many key names of the evidence edges stay, because each key is part of an edge's identity; whether a key can be derived from the step instead of written by hand is open for the last stage.

### What today's text hid

`lift` carries every telemetry property the lifted and the lower relationship share, without naming it. In the workload structure, `lift talks` gives the edges `flows` and `drops` over 24 hours, though the text names neither. The KQL text says it as a step of its own.

### What could not be checked

- Whether each KQL text gives the same tables in Kusto itself. Nothing was run on a Kusto cluster; the seven differences above are where results would differ.
- That the source of Kusto.Language on GitHub is the 12.4.1 package. The repository has no tags, and its version comes from the build.
- Whether the package's license lets a release of ours include it. Its redistribution section names no files. A release is not in scope; building from the source would be under the source's license.
- Editable views. They come in a later stage.

## Sources

Opened on 12 November 2026.
- KQL: [graph semantics overview](https://learn.microsoft.com/en-us/kusto/query/graph-semantics-overview), [make-graph](https://learn.microsoft.com/en-us/kusto/query/make-graph-operator), [graph-match](https://learn.microsoft.com/en-us/kusto/query/graph-match-operator), [labels()](https://learn.microsoft.com/en-us/kusto/query/labels-graph-function), [graph-to-table](https://learn.microsoft.com/en-us/kusto/query/graph-to-table-operator), [graph-shortest-paths](https://learn.microsoft.com/en-us/kusto/query/graph-shortest-paths-operator), [graph-mark-components](https://learn.microsoft.com/en-us/kusto/query/graph-mark-components-operator), [persistent graphs](https://learn.microsoft.com/en-us/kusto/management/graph/graph-persistent-overview), [graph models](https://learn.microsoft.com/en-us/kusto/management/graph/graph-model-overview), [create or alter graph model](https://learn.microsoft.com/en-us/kusto/management/graph/graph-model-create-or-alter), [graph snapshots](https://learn.microsoft.com/en-us/kusto/management/graph/graph-snapshot-overview), [graph()](https://learn.microsoft.com/en-us/kusto/query/graph-function), [scan](https://learn.microsoft.com/en-us/kusto/query/scan-operator), [make-series](https://learn.microsoft.com/en-us/kusto/query/make-series-operator), [bin()](https://learn.microsoft.com/en-us/kusto/query/bin-function), [materialized views](https://learn.microsoft.com/en-us/kusto/management/materialized-views/materialized-view-overview), [join](https://learn.microsoft.com/en-us/kusto/query/join-operator), [dcount](https://learn.microsoft.com/en-us/kusto/query/dcount-aggregation-function), [count_distinct](https://learn.microsoft.com/en-us/kusto/query/count-distinct-aggregation-function), [take_any](https://learn.microsoft.com/en-us/kusto/query/take-any-aggregation-function), [invoke](https://learn.microsoft.com/en-us/kusto/query/invoke-operator), [KQL overview](https://learn.microsoft.com/en-us/kusto/query/), [Azure Resource Graph query language](https://learn.microsoft.com/en-us/azure/governance/resource-graph/concepts/query-language).
- Kusto.Language: [repository](https://github.com/microsoft/Kusto-Query-Language) and its `src/Kusto.Language/readme.md`; NuGet `Microsoft.Azure.Kusto.Language` (versions and dates from the NuGet registration API); [monaco-kusto](https://github.com/Azure/monaco-kusto). The local package 12.4.1 was used for the checks.
- GQL: [gqlstandards.org](https://www.gqlstandards.org/), [opengql/grammar](https://github.com/opengql/grammar) (`GQL.g4` read). [ISO page](https://www.iso.org/standard/76120.html): 403.
- openCypher: [opencypher.org](https://opencypher.org/), [repository](https://github.com/opencypher/openCypher); [Neo4j GQL conformance](https://neo4j.com/docs/cypher-manual/current/appendix/gql-conformance/).
- SQL/PGQ: [DuckPGQ](https://duckpgq.org/), [repository](https://github.com/cwida/duckpgq-extension) (metadata). [ISO page](https://www.iso.org/standard/79473.html): 403.
- G-CORE: [arXiv 1712.01550](https://arxiv.org/abs/1712.01550) (partly read).
- Gremlin: [TinkerPop](https://tinkerpop.apache.org/), [reference: Gremlin.Net](https://tinkerpop.apache.org/docs/current/reference/#gremlin-dotnet), [subgraph step](https://tinkerpop.apache.org/docs/current/reference/#subgraph-step); repository tags.
- SPARQL: [SPARQL 1.1 Query](https://www.w3.org/TR/sparql11-query/), [SPARQL 1.2 Query (draft)](https://www.w3.org/TR/sparql12-query/); [dotNetRDF](https://dotnetrdf.org/), [repository](https://github.com/dotnetrdf/dotnetrdf).
- Datalog: [Soufflé](https://souffle-lang.github.io/), [releases](https://github.com/souffle-lang/souffle/releases); [Differential Datalog](https://github.com/vmware-archive/differential-datalog) (archived); [Feldera](https://docs.feldera.com/).
- T-SQL graph: [SQL Graph overview](https://learn.microsoft.com/en-us/sql/relational-databases/graphs/sql-graph-overview); [SqlScriptDOM](https://github.com/microsoft/SqlScriptDOM).
- Prometheus: [alerting rules](https://prometheus.io/docs/prometheus/latest/configuration/alerting_rules/).
- Package numbers: NuGet search and registration APIs; repository facts: GitHub API (license, creation, last push, releases), 12 November 2026.
- Not opened: the Flink SQL window and `MATCH_RECOGNIZE` pages (404 at the addresses tried).
