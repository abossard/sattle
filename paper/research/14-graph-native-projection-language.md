# 14. A projection language that is itself a graph

Research note, 14 November 2026. Exploration only: nothing here is designed, decided or built, and the KQL work of notes 10 and 11 continues unchanged. The same content, with diagrams and two small interactive examples, is published as a page: https://claude.ai/artifact/WXZsaw6t8FgcK9Kd8BpY1q (private to its owner until shared).

The question, in the user's words, shortened: KQL feels made for tables. Could Sattle have its own graph projection language, like XSLT, that is a graph and not text, and that runs as map and reduce on parts of a graph without ever holding the whole? Are there already such languages?

Terms used here:
- **Projection**: a rule set that takes graphs and gives a graph.
- **Map**: a step without memory. **Reduce**: a step whose memory is filed under a key, so it can be split by key across threads or machines.
- **Change**: a row with weight +1 (now true) or -1 (no longer true). Working on changes is called incremental.
- **Locality**: how much of the graph one rule may look at.
- **Confluence**: the result does not depend on the order in which rules are applied.

## Summary

1. **No existing language has all four properties**: (G) the program is a graph, (O) the output is a graph, (P) it runs in parts split by a key, (I) it runs on changes. Of 35 languages and tools read for this note, none has all four in full. Every pair exists.
2. **The closest four**, and what each lacks:
   - Henshin compiled to Apache Giraph (2014): G, O and P. It computes from scratch on every run, and it is a research result.
   - VIATRA with IncQuery-D: O and I, with patterns kept up to date. Patterns are text, and the distributed version was a prototype.
   - ATL: G and O, and the nearest relative of XSLT for graphs. P and I were each shown in a separate prototype, never together.
   - Differential dataflow and DBSP: P and I. The program is a graph of operators built in code, and it works on tables of rows.
3. **Sattle's engine already supplies O, P and I.** The open part is G: what a person writes and looks at. Three things that go by the name "the program is a graph" are different projects (section 1.2), and two of them are already true inside the engine: definitions are served as a graph, and every projection is a graph of operators.
4. **The central tension is tree against graph.** XSLT's recursive descent is cheap because a tree has one parent per node, a part is a subtree, and going down ends. A graph has none of the three. Each known way to get them back gives up something: follow only a declared hierarchy, limit what a rule sees to a fixed distance, or allow only rules that add and never take back.
5. **Known results put a price on each corner** (section 4): rules without recursion see a fixed distance and can run on a part plus a border; reachability has no such distance and costs rounds; patterns with a cycle cannot be split by one key; only monotone rules run without waiting; in-place rewriting cannot be checked for ending or for order-independence in general.
6. **The user's answers** (section 6) settle three axes: visual editing is required and the graph is the truth; recursion is bounded depth along any edges; KQL lends names only and nothing is converted from KQL text. His working name for the graph form is gkql.
7. **Query verbs as nodes** (section 7): every KQL verb has a form as a node and lowers to a map or a keyed reduce. What changes is around the verbs: a pipeline becomes a graph without loops, join sides need names, a pattern is a graph inside a node, variables become connections.
8. **"A graph form makes visual editing easy" is half true** (section 8). Parsing disappears and every edit is safe. Layout, readability at scale, comparison and review, keyboard editing and a text form were each built separately in every node-based tool read.
9. **Eight decisions** remain open (section 10). None is taken here. The page stores his marks on 27 terms and on the decisions, readable in the next round.

## 1. Problem space

### 1.1 What a projection must carry
- **Graph in, graph out.** So projections stack.
- **Identity.** Every element of the result needs an id that is the same on every run and on every machine.
- **Lineage.** For each element of the result, the elements it was made from.
- **Time.** Windows, thresholds that must hold for a duration, and silence. No graph language has words for time; KQL has (note 10). A new language would have to bring them or borrow them.

### 1.2 "The program is a graph" has three readings
| Reading | What the author sees | Examples | In Sattle today |
|---|---|---|---|
| Rule graph | a small pattern in the vocabulary of the data, and what to build for each match | graph grammars, ATL, GROOVE | planned as a view: the "By example" editor of note 11 writes a KQL pattern from picked elements |
| Dataflow graph | operators wired together, rows flowing along the wires | Blender Geometry Nodes, Node-RED, differential dataflow | the engine's operator graph; the "Wires" view of note 11 draws it |
| Syntax graph | a text whose sentences are stored as nodes | XSLT (a stylesheet is XML), SPIN, SHACL rules, ATL transformations as models | the catalog: every definition is served as a graph with the kernel as its ontology |

The readings differ in who can read the program and in which tools come for free. SPIN and SHACL show that the third does not give the first: their rules are stored as graph data, and people still write them as text.

### 1.3 Why XSLT has it easy
An XSLT template says: for a node like this, write that, then send the children back to be matched. Three properties of a tree carry it:
- **One parent.** Each node is reached once, by one path.
- **A part is a subtree.** Cut anywhere and the piece below is complete.
- **Descent ends.**

A graph has shared nodes, edges leaving every part, and cycles. The known ways back:
- **A tree inside the graph.** Recurse only along edges the ontology declares a hierarchy. Sattle's ontologies already mark such edges.
- **A fixed distance.** A rule sees at most k steps from where it matched. A part plus a border of width k is then enough.
- **One edge at a time.** UnQL's structural recursion applies a function to every edge on its own and glues the results as the edges were connected. It is defined on graphs with cycles and runs in bulk and in parallel. It has no identities.
- **Only adding.** Rules that never take back reach one answer in any order (Datalog).

### 1.4 The forces
| Force | The question it asks | Pulls against |
|---|---|---|
| Locality | how much may one rule see | expressiveness: roll-ups and reachability want to see far |
| Recursion | may a rule apply to its own result | partitioning: every round is an exchange between parts |
| Partitioning by key | where does each piece of state live | patterns that span several keys, which need a join per shared node |
| Incremental change | can a step turn an input change into an output change | steps that need everything so far (negation, counts, recursion with removals) |
| Streaming | can a result be given before the input is complete | non-monotone rules, which must know a key is complete |
| Confluence | does order matter | in-place rewriting |
| Termination | does it end | general recursion and in-place rewriting |
| Identity of created elements | is an id a function of what it was made from | fresh ids, the default of classic graph rewriting |
| Composability | does every rule set return a graph a next one can read | languages whose queries return tables |
| Program as graph | can the engine store, show, compare and project its own programs | text tools: comments, layout, review by diff, existing parsers |
| Learnability | can someone who does not program read and change a rule | large rule sets, which are easier to search and compare as text |

### 1.5 The concrete target: a health model
The user named the output a projection should produce: an Azure Monitor health model (resource type Microsoft.CloudHealth/healthmodels). The description below is the orchestrator's survey of his models, passed on at his request, with generic names only. It was not checked against the service's documentation for this note.

The target graph:
- **Entities:** a root named after the model, user flows, system components, resources.
- **Relationships:** parent to child. A child may have several parents, so the graph is a DAG, not a tree. A relationship cannot be changed, only removed and created.
- **Signals** on an entity, each with a degraded and an unhealthy rule (an operator and a threshold; unhealthy is checked first), read from metrics, log queries, PromQL or pushed reports.
- **Roll-up** on each parent over its children: `WorstOf`, `MaxNotHealthy` (a count or a share of children against a degraded and an unhealthy threshold) or `Average`. Each child has an impact on its parent: `Standard`, `Limited` (one level milder) or `Suppressed`.
- **States:** Healthy, Degraded, Unhealthy, Unknown.
- **Size:** 7 to 30 entities and 0 to 25 signals per model, all written by hand.

Pain points he has today: a resource shared by two flows is copied per flow, so the model drifts from the estate; `WorstOf` ignores Unknown, so a parent looks healthy over children nobody measures; leaves without signals stay Unknown; an update replaces the whole resource; a delete loses the history; discovered entities form their own tree outside the roll-up.

What this target adds to the problem space:
- **It is a projection's output graph.** Flows and components are authored facts, resources are observed, signals bind elements to telemetry.
- **Identity is the cure for the copies.** A shared resource is one element with one id and several parents. A rule whose target gives the resource an identity computed from the observed resource cannot produce two copies. This is the strongest concrete case for "identity as a function of the match" (section 3.1).
- **The roll-up is a reduce keyed by parent.** `WorstOf` is the maximum of the children's severities; the impact is a map on each child's value before the reduce. `MaxNotHealthy` is a count compared with a threshold. A count is not monotone, so it needs to know that a parent's children are complete (section 4, CALM). Unknown has to be a value of its own, or the roll-up repeats today's fault.
- **Depth is small and bounded.** Root, flow, component, resource is three to four levels. Rolling up level by level is recursion along parent edges to a bounded depth, which is what the user asked for. The output being a DAG and not a tree means XSLT-style descent would visit a shared child once per parent; a reduce keyed by parent does not have that problem.
- **The model is small, the estate is not.** "Without the whole graph" matters for the observed graph a model is derived from, and for many models at once, not for one model of 30 entities.
- **Two ways to deliver, both open:**
  - *Emit the model.* Each change of the output graph becomes a create, update or delete call. The engine's +1 and -1 changes map onto that. The service's rules bite here: an update replaces the whole resource, a changed relationship is a delete and a create, and a delete loses history. So changes must be combined per entity, and stable ids matter even more.
  - *Compute health in Sattle and push it.* The model in Azure keeps entities with pushed reports as their only signals; thresholds and roll-up are computed by the engine. Azure's own roll-up and its faults are bypassed; its history and alerts still work on what is pushed.
  - Nothing is decided between the two.

## 2. Prior art

Ratings: yes, partly (or only in a research prototype), no. They are this note's reading of each tool's documents.

| Family | Language or tool | Program is a graph | Graph out | Runs in parts | Runs on changes |
|---|---|---|---|---|---|
| Trees first | XSLT 3.0 | partly | partly | partly | no |
| Trees first | Attribute grammars | no | partly | partly | yes |
| Trees first | Stratego | no | partly | no | no |
| Trees first | UnQL and UnCAL | no | yes | yes | partly |
| Graph rewriting | Algebraic graph rewriting (the theory) | yes | yes | partly | no |
| Graph rewriting | AGG | yes | yes | no | no |
| Graph rewriting | GROOVE | yes | yes | no | no |
| Graph rewriting | GP 2 | partly | yes | no | no |
| Graph rewriting | GrGen.NET | no | yes | partly | no |
| Graph rewriting | PROGRES | partly | yes | no | no |
| Graph rewriting | Henshin | yes | yes | yes | no |
| Graph rewriting | Interaction nets | yes | yes | yes | no |
| Graph rewriting | Triple graph grammars | yes | yes | no | yes |
| Model transformation | ATL | yes | yes | partly | partly |
| Model transformation | QVT | yes | yes | no | partly |
| Model transformation | VIATRA and IncQuery | partly | yes | partly | yes |
| Queries that return graphs | SPARQL CONSTRUCT | no | yes | partly | no |
| Queries that return graphs | SPIN and SHACL rules | yes | yes | no | partly |
| Queries that return graphs | G-CORE | no | yes | no | no |
| Queries that return graphs | Cypher and GQL | no | no | partly | no |
| Queries that return graphs | KQL with graph models | no | partly | yes | no |
| Queries that return graphs | Gremlin | partly | partly | yes | no |
| Queries that return graphs | CQL (categorical) | partly | yes | no | no |
| Queries that return graphs | GSQL accumulators | no | partly | yes | no |
| Dataflow on changes, split by key | Datalog | no | partly | yes | yes |
| Dataflow on changes, split by key | Differential dataflow | partly | partly | yes | yes |
| Dataflow on changes, split by key | DBSP and Feldera | partly | partly | yes | yes |
| Dataflow on changes, split by key | Materialize | no | partly | yes | yes |
| Dataflow on changes, split by key | Pregel and GraphX | no | yes | yes | no |
| Programs drawn as nodes and wires | Blender Geometry Nodes | yes | no | partly | partly |
| Programs drawn as nodes and wires | Node-RED | yes | no | no | no |
| Programs drawn as nodes and wires | Unreal Blueprints | yes | no | no | no |
| Programs drawn as nodes and wires | Enso | yes | no | no | partly |
| Programs drawn as nodes and wires | Apache NiFi | yes | no | yes | no |
| Programs drawn as nodes and wires | JetBrains MPS | partly | no | no | no |

### Trees first

**XSLT 3.0.** Templates that match a node of an XML tree and write output, then hand the children back to be matched (apply-templates). Version 3.0 adds a streaming mode that never holds the whole document.
- Why these ratings: A stylesheet is itself XML, a tree, so XSLT can transform XSLT. Output is a tree. Streaming avoids the whole input but is one pass on one machine, not split by key. Nothing is kept up to date.
- What Sattle can learn: Two things. The template with dispatch is the shape you like. And streaming is a checked promise: every template in a streaming mode must be provably computable in one pass, and the checker says so before anything runs. Sattle could check 'this rule needs only a part' in the same way.
- Source: [XSLT 3.0, W3C Recommendation, 8 June 2017](https://www.w3.org/TR/xslt-30/)

**Attribute grammars.** Rules that compute values on the nodes of a tree. Some values flow up from children (a roll-up), some flow down from the parent (context).
- Why these ratings: Rules are text. The result is the same tree with values on it. Evaluation order comes from which value needs which, so subtrees are independent. Keeping values current after an edit was solved in 1983 with optimal cost.
- What Sattle can learn: A health roll-up is a value flowing up; a policy inherited from a parent is a value flowing down. The dependency analysis says exactly what to recompute after one change.
- Source: [Knuth, Semantics of context-free languages, 1968](https://doi.org/10.1007/BF01692511), [Reps, Teitelbaum, Demers, Incremental context-dependent analysis for language-based editors, 1983](https://research.cs.wisc.edu/wpis/abstracts/toplas83.abs.html)

**Stratego.** Rewrite rules on trees, with the order of application written separately as a strategy ('top down', 'repeat until stuck').
- Why these ratings: Text; trees in, trees out; one machine; batch.
- What Sattle can learn: Keeping 'what to change' apart from 'where and in which order' is a clean split. It exists because most rule sets are neither order-independent nor sure to end.
- Source: [Stratego reference, Spoofax](https://www.spoofax.dev/references/stratego/)

**UnQL and UnCAL.** A query language for graphs built on one idea: structural recursion. A function is applied to every edge on its own, and the results are glued together the way the edges were connected.
- Why these ratings: Programs are written as terms. Graphs in, graphs out, cycles allowed. Because every edge is handled alone, the whole thing runs in bulk and in parallel, and was later compiled to Pregel. A two-way version exists (GRoundTram); keeping results current is not documented.
- What Sattle can learn: The closest theory to 'recursively apply a pattern without ever having the whole graph'. The price is what a rule may see: one edge at a time. It has no identities; two graphs are equal when they behave the same.
- Source: [Buneman, Fernandez, Suciu, UnQL: a query language and algebra for semistructured data based on structural recursion, 2000](https://doi.org/10.1007/s007780050084), [Tung, Hu, Towards systematic parallelization of graph transformations over Pregel, 2017](https://doi.org/10.1007/s10766-016-0418-5)

### Graph rewriting

**Algebraic graph rewriting (the theory).** The mathematics behind graph grammars. A rule is a left graph, a right graph and what they share. Applying it replaces a copy of the left by the right, in place.
- Why these ratings: A rule is graphs by definition. Two applications that do not overlap may run in either order or together (a theorem), which is a basis for running in parts. Nothing about changes.
- What Sattle can learn: The vocabulary for order-independence: find all pairs of rules that can overlap in conflict and show each pair still ends in the same graph. Whether rules always stop cannot be decided in general.
- Source: [Ehrig, Ehrig, Prange, Taentzer, Fundamentals of Algebraic Graph Transformation, 2006](https://doi.org/10.1007/3-540-31188-2), [Plump, Confluence of graph transformation revisited](https://eprints.whiterose.ac.uk/195071/1/Plump.ECEASST.10.pdf)

**AGG.** A workbench from TU Berlin for typed graph rewriting with checks.
- Why these ratings: Rules are edited as graphs. One machine, in memory, batch. Little activity.
- What Sattle can learn: It ships the conflict analysis as a menu item. It also shows the limit: the analysis is per pair of rules and slows with every rule added.
- Source: [AGG, TU Berlin](https://user.tu-berlin.de/o.runge/AGG/WWW/agg.html)

**GROOVE.** A tool that draws each rule as one graph, with colour for what is matched, created and deleted, and explores every graph the rules can reach.
- Why these ratings: Rules are graphs. One machine, batch. Active (release 7.6).
- What Sattle can learn: The single-graph rule notation is the most compact drawing of a rule there is. Worth a look before inventing one.
- Source: [GROOVE manual, University of Twente](https://groove.cs.utwente.nl/manual_introduction.html)

**GP 2.** A small graph programming language from York: rules with a left and right graph, composed with a few control words.
- Why these ratings: Rules are graphs in meaning but written as text and compiled to C.
- What Sattle can learn: Rules that start from a marked 'root' node match in constant time. A marked starting point is what makes matching local.
- Source: [GP 2, University of York](https://github.com/UoYCS-plasma/GP2)

**GrGen.NET.** A fast graph rewriting system for .NET. Rules in a text language, compiled to .NET assemblies.
- Why these ratings: Text rules. Works on one in-memory graph. Matching can use several cores. Active (release 8.1, May 2026).
- What Sattle can learn: The only mature rewriting engine on Sattle's platform. It plans the search order for each pattern from statistics of the data, like a database.
- Source: [GrGen.NET](https://grgen.de/)

**PROGRES.** The 1990s ancestor from Aachen: rules drawn as graphs plus a programmed order with backtracking.
- Why these ratings: Visual rules over a graph database. Ended in 2006.
- What Sattle can learn: It needed backtracking so that authors did not have to prove order-independence by hand. That cost is built into in-place rewriting.
- Source: [Ehrig, Ehrig, Prange, Taentzer, Fundamentals of Algebraic Graph Transformation, 2006](https://doi.org/10.1007/3-540-31188-2)

**Henshin.** The Eclipse graph transformation language. Rules and their composition are stored as models.
- Why these ratings: Rules are models, so graphs. In 2014 its rules were compiled to Apache Giraph and run on a cluster in rounds. Always from scratch. Last release 2022.
- What Sattle can learn: The nearest existing thing to three of your four wishes. Its lesson: a drawn rule can be compiled to a round-based, split-by-vertex program automatically. Its gap is change.
- Source: [Henshin, Eclipse](https://projects.eclipse.org/projects/modeling.emft.henshin), [Krause, Tichy, Giese, Implementing graph transformations in the bulk synchronous parallel model, 2014](https://doi.org/10.1007/978-3-642-54804-8_23)

**Interaction nets.** A model of computation where the program and the data are one graph, and every rule rewrites exactly two connected nodes.
- Why these ratings: Everything is a graph. Because a rule touches only one connected pair, any two rewrites are independent. The result never depends on order and all rewrites can run at once. Runtimes are research prototypes.
- What Sattle can learn: Order-independence and parallelism come for free if a rule may see almost nothing. It is the far end of the trade between what a rule sees and what the engine can promise.
- Source: [Lafont, Interaction nets, 1990](https://en.wikipedia.org/wiki/Interaction_nets), [HVM, Higher Order Company](https://github.com/HigherOrderCO/HVM)

**Triple graph grammars.** Rules that grow a source graph, a target graph and a graph of links between them at once. One rule set gives forward, backward and keep-in-step translation.
- Why these ratings: Rules are graphs. The eMoflon tool kept results current with an incremental matcher. Its triple-graph part was discontinued in 2024.
- What Sattle can learn: The link graph is lineage made first class: 'what was built from this' is a lookup. Sattle's paper already plans its two-way rules on this model.
- Source: [eMoflon::IBeX](https://emoflon.org/ibex/)

### Model transformation

**ATL.** The most used model-to-model transformation language. A matched rule says: for every source element like this, create these target elements. References between targets are resolved through a trace kept by the engine.
- Why these ratings: A transformation is itself a model, and transformations that read or write transformations are routine ('higher-order'). Prototypes ran it on MapReduce (2015) and on changes (2010), separately.
- What Sattle can learn: The graph answer to XSLT. The trace does the job of apply-templates: a rule refers to 'whatever was made from that element' without naming the rule that made it. Because it only builds, order never matters.
- Source: [ATL, Eclipse](https://eclipse.dev/atl/), [Tisi and others, On the use of higher-order model transformations, 2009](https://doi.org/10.1007/978-3-642-02674-4_3), [Jouault, Tisi, Towards incremental execution of ATL transformations, 2010](https://doi.org/10.1007/978-3-642-13688-7_9), [Benelallam and others, Distributed model-to-model transformation with ATL on MapReduce, 2015](https://doi.org/10.1145/2814251.2814258)

**QVT.** The standard for model transformation from the OMG: a relational, a core and an imperative language.
- Why these ratings: Defined as models. The Eclipse implementation of the relational part is still marked partial, ten years after the standard.
- What Sattle can learn: A warning about cost. A committee with industry backing specified a graph transformation language and never got a complete implementation of its declarative part.
- Source: [OMG, MOF Query/View/Transformation 1.3, 2016](https://www.omg.org/spec/QVT/1.3)

**VIATRA and IncQuery.** Graph patterns over models whose matches are kept current as the model changes, and transformations that fire on those changes.
- Why these ratings: Patterns are text, stored as models. Matches are maintained by a network that remembers partial matches (Rete). A distributed version, IncQuery-D, was a 2014 prototype that spread the network, not the graph.
- What Sattle can learn: Proof that graph patterns kept up to date scale to real models. The method is the same as Sattle's: a pattern of k edges becomes joins that react to a change on either side.
- Source: [VIATRA, Eclipse](https://eclipse.dev/viatra/), [Szarnyas and others, IncQuery-D: a distributed incremental model query framework in the cloud, 2014](https://doi.org/10.1007/978-3-319-11653-2_40), [Rete algorithm (Forgy 1982)](https://en.wikipedia.org/wiki/Rete_algorithm)

### Queries that return graphs

**SPARQL CONSTRUCT.** The query form of the web's graph query language that returns a graph instead of a table.
- Why these ratings: Text. Graph out, so queries chain. Stores split the work internally; the standard says nothing about changes.
- What Sattle can learn: Graph-out as a plain template: write the shape of the result with variables from the pattern. No aggregation inside the template is its limit.
- Source: [SPARQL 1.1 Query Language, section 16.2 CONSTRUCT](https://www.w3.org/TR/sparql11-query/#construct)

**SPIN and SHACL rules.** Standards that store rules as graph data next to the data they apply to. SPIN stores a whole query as nodes; SHACL rules attach to shapes.
- Why these ratings: The rule is a graph in the data's own format, so it is stored, queried and versioned with it. Some stores keep derived facts current. No standard way to run in parts.
- What Sattle can learn: The standards body did 'the program is a graph' fifteen years ago. People still write the rules as text; SHACL keeps the query as a string inside the graph. Storing a program as a graph and reading it as a graph are different wishes.
- Source: [SPIN overview, W3C Member Submission, 2011](https://www.w3.org/submissions/spin-overview/), [SHACL Advanced Features, W3C Working Group Note, 2017](https://www.w3.org/TR/shacl-af/), [SHACL 1.2 Inference Rules, W3C Working Draft, November 2026](https://www.w3.org/TR/shacl12-inference-rules/)

**G-CORE.** A research language from 2018 that set out the rule 'graphs in, graphs out' for graph queries.
- Why these ratings: Text. Graph out by design. No implementation of note.
- What Sattle can learn: New nodes get their identity from the values they are grouped by. That is Sattle's identity function, arrived at independently.
- Source: [Angles and others, G-CORE: a core for future graph query languages, 2018](https://arxiv.org/abs/1712.01550)

**Cypher and GQL.** The pattern language everyone knows, now an ISO standard.
- Why these ratings: Text. Queries return tables; writing to a graph is a side effect, not a result.
- What Sattle can learn: The pattern notation is the common tongue. Note 10 covers why it was not chosen.
- Source: [GQL standards (ISO/IEC 39075:2024)](https://www.gqlstandards.org/)

**KQL with graph models.** Kusto's graph operators and persistent graph models: a named graph built by ordered steps, each a query that adds nodes or edges.
- Why these ratings: Text. The model's definition is stored as JSON, not as a graph. Runs on a cluster. A snapshot is rebuilt by hand, never kept current.
- What Sattle can learn: Where Sattle is today. The engine adds what Kusto lacks: results kept current. Your doubt is right in one respect: graph models are steps over tables.
- Source: [Kusto graph semantics overview](https://learn.microsoft.com/en-us/kusto/query/graph-semantics-overview), [Kusto graph models](https://learn.microsoft.com/en-us/kusto/management/graph/graph-model-overview)

**Gremlin.** A traversal language: walk out, filter, repeat. Traversals travel between programs as data.
- Why these ratings: A traversal is a list of steps sent as data, a pipeline more than a graph, and version 4 goes back to sending text. A parallel mode runs one program per vertex with messages.
- What Sattle can learn: Even the project that shipped programs-as-data is returning to text with a grammar, for tooling reasons.
- Source: [Apache TinkerPop reference](https://tinkerpop.apache.org/docs/current/reference/)

**CQL (categorical).** A data migration language where a schema is a graph with equations, and a mapping between two schemas moves data forward or pulls it back.
- Why these ratings: Schemas and mappings are graph-like objects with exact meaning. One machine, in memory.
- What Sattle can learn: It gives exact names to what projections do: collapse along a mapping (Sattle's old lift), pull back, join. Composition of mappings is guaranteed by the mathematics.
- Source: [CQL, categorical query language](https://www.categoricaldata.net/)

**GSQL accumulators.** TigerGraph's language: walk edges, and add values into accumulators that sit on vertices.
- Why these ratings: Text. Edges are processed by many threads; an accumulator is combined under its vertex.
- What Sattle can learn: An accumulator on a vertex is a reduce keyed by that vertex, written in a way analysts accept.
- Source: [TigerGraph GSQL accumulators](https://www.tigergraph.com/docs/gsql-ref/current/querying/accumulators)

### Dataflow on changes, split by key

**Datalog.** Rules of the form 'this is true if those are true', applied until nothing new follows.
- Why these ratings: Text. Tables in, tables out. The standard evaluation only looks at what was new in the last round, which is working on changes. Engines run in parallel.
- What Sattle can learn: Recursion with a guarantee: rules that only add always end and give one answer. The guarantee is lost the moment a rule negates or counts inside the loop.
- Source: [Datalog (semi-naive evaluation)](https://en.wikipedia.org/wiki/Datalog), [Souffle](https://souffle-lang.github.io/)

**Differential dataflow.** A library that keeps any dataflow current under changes, including loops inside loops.
- Why these ratings: The program is a graph of operators but built in Rust code. Rows carry +1 or -1. Workers split by key.
- What Sattle can learn: The model the engine follows. It shows recursion on changes is possible, and what it costs: it remembers the changes of every round.
- Source: [McSherry, Murray, Isaacs, Isard, Differential dataflow, 2013](https://www.microsoft.com/en-us/research/publication/differential-dataflow/), [Differential dataflow, source](https://github.com/TimelyDataflow/differential-dataflow)

**DBSP and Feldera.** A theory that turns any query, recursion included, into one that works on changes, by a mechanical rule.
- Why these ratings: Programs are circuits of operators, produced from SQL. Rows with signed weights.
- What Sattle can learn: The theory Sattle's proofs cite. Any new language that lowers to its operators inherits the correctness proof.
- Source: [Budiu, Chajed, McSherry, Ryzhyk, Tannen, DBSP: automatic incremental view maintenance for rich query languages, 2023](https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf)

**Materialize.** A database that keeps SQL views current, built on differential dataflow.
- Why these ratings: Text (SQL). Tables.
- What Sattle can learn: A product-grade example of the split Sattle has now: a familiar text language on top, a change-driven engine below.
- Source: [Materialize](https://materialize.com/docs/overview/what-is-materialize/)

**Pregel and GraphX.** Think like a vertex: each vertex gets messages, updates its value, sends messages. All vertices do this in rounds.
- Why these ratings: A program is code. Graph in, graph with new values out. Split by vertex across machines. Each run starts from scratch.
- What Sattle can learn: The cleanest picture of a rule that needs only a part: a vertex sees itself and its mail. Recursion is rounds, and the number of rounds is the length of the longest chain. GraphX showed it is joins and group-bys underneath.
- Source: [Malewicz and others, Pregel: a system for large-scale graph processing, 2010](https://research.google/pubs/pregel-a-system-for-large-scale-graph-processing/), [Gonzalez and others, GraphX: graph processing in a distributed dataflow framework, 2014](https://www.usenix.org/conference/osdi14/technical-sessions/presentation/gonzalez), [Gonzalez and others, PowerGraph: distributed graph-parallel computation on natural graphs, 2012](https://www.usenix.org/system/files/conference/osdi12/osdi12-final-167.pdf)

### Programs drawn as nodes and wires

**Blender Geometry Nodes.** Artists wire operator nodes to build and change 3D geometry. A group of nodes becomes a reusable node.
- Why these ratings: The program is a node graph stored in the file. It computes geometry. Evaluation is lazy and can use several cores.
- What Sattle can learn: Evidence that non-programmers adopt wired dataflow at scale, when every node shows its result at once and groups hide detail. It is reading 2 of 'program is a graph'.
- Source: [Blender manual, Geometry Nodes](https://docs.blender.org/manual/en/latest/modeling/geometry_nodes/introduction.html)

**Node-RED.** Wire message-handling nodes in a browser. Used widely for device and service glue.
- Why these ratings: A flow is a JSON document of nodes and wires. Messages stream through one at a time.
- What Sattle can learn: Its program-as-data is what makes sharing and versioning flows easy. Flows beyond a screen get hard to follow.
- Source: [Node-RED, importing and exporting flows](https://nodered.org/docs/user-guide/editor/workspace/import-export)

**Unreal Blueprints.** Visual scripting for game logic, compiled to bytecode.
- Why these ratings: A program is a graph stored with the game's assets.
- What Sattle can learn: Blueprints draw control flow (do this, then that), not data flow. Large ones are famously tangled. A drawn language for rules should draw data, not order.
- Source: [Unreal Engine, Blueprint compiler overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/compiler-overview-for-blueprints-visual-scripting-in-unreal-engine)

**Enso.** A data preparation language where the text and the node graph are two views of one program, both editable.
- Why these ratings: The same program as text or as a graph, kept in step. Works on tables.
- What Sattle can learn: The existence proof for 'both, kept in step'. Its cost is that every construct must have a faithful form in both views.
- Source: [Enso](https://github.com/enso-org/enso)

**Apache NiFi.** Wire processors with queues between them to route data between systems; runs on a cluster and records where every item came from.
- Why these ratings: The flow is a stored graph. It routes items; it does not build graphs.
- What Sattle can learn: Lineage recorded by the runtime for every item, shown on the same canvas as the program.
- Source: [Apache NiFi overview](https://nifi.apache.org/docs/nifi-docs/html/overview.html)

**JetBrains MPS.** A workbench for languages that are never parsed. The program is stored as a tree of nodes and edited through forms, tables or diagrams.
- Why these ratings: The stored program is a structure, not text. It is a tool for making languages, not for graphs.
- What Sattle can learn: The industrial case for 'not text'. Its known costs are the ones to expect: comparing, merging and reviewing need their own tools.
- Source: [JetBrains MPS](https://www.jetbrains.com/mps/)

### The direct answer
Does any existing language combine (a) the program is a graph, (b) the output is a graph and (c) partitioned, incremental, map and reduce execution? **No.**
- (a) and (b) together are common: every graph grammar tool, ATL, QVT, triple graph grammars, SPIN.
- (a), (b) and partitioned: Henshin on Giraph, and interaction nets. Neither works on changes.
- (a), (b) and incremental: triple graph grammars in eMoflon, on one machine; its triple-graph part was discontinued in 2024.
- (b) and (c) in full: none in the list. The engines that have (c) work on tables (Datalog, differential dataflow, DBSP, Materialize); Sattle's engine is the one that adds graphs, identity and lineage on top of that model.
- The reason is historical more than technical: graph grammars grew up in modelling tools on one machine, and change-driven engines grew up in databases on tables. Section 3.2 shows that a rule graph lowers to maps and keyed reduces in a fixed way.

## 3. Solution space

### 3.1 Axes and options
Options are listed from least to most powerful. "Today" marks where Sattle stands with KQL.

| Axis | Options |
|---|---|
| Shape of a rule | steps in a pipeline (today); template: a pattern and what to build per match, with other rules reached by reference (XSLT, ATL); rewrite rule: left graph and right graph, applied in place; wired operators; a program per element with messages (Pregel) |
| What a rule may see | one element; an element and its neighbours; a pattern of several elements (today); paths up to a fixed length (today, the two ends only); anything reachable (removed with `fixpoint`) |
| Recursion | none inside a projection, depth by stacking (today); down a declared hierarchy; to a fixed depth (today, as bounded paths); until nothing changes, for rules that only add; general |
| Effect on the graph | builds a new graph (today); adds values to the same graph (attribute grammars); rewrites in place |
| Identity of created elements | a function of the match (today, `identity()`); a recorded link from source to created element (ATL's trace, the correspondence graph of triple graph grammars); a fresh id per run |
| Order of rules | none, all matches at once (today); layers (today, stacked projections); rounds; a strategy written by the author (Stratego, GrGen) |
| The truth | text, with pictures as views (today, note 11); the graph, with text printed from it; both kept in step (Enso) |
| Place beside the engine | nothing new: show and edit the existing operator graph; a second front end beside KQL that lowers to the same operators; in place of KQL; rules as data in a graph, read by a projection that produces projections (note 08, parked) |

### 3.2 How a rule graph lowers to maps and keyed reduces
1. Each edge of the pattern is a map over the changed edges of that type.
2. Each node that two pattern edges share is a reduce keyed by that node: the join of the two sides.
3. The key of the created element is a map: its id computed from the match.
4. The created element is a reduce keyed by that id, which counts the matches that support it.
5. The output is a change only when that count goes from 0 to 1 or from 1 to 0.

This is what the engine does today for a KQL `graph-match` followed by an `edges` step. A rule graph would be another notation for the same five steps, so it would inherit the engine's proofs. An aggregate on the created element is a reduce under the same key. A reference to "what another rule built from this element" is a join on the lineage of that rule. Recursion down a hierarchy is a reduce keyed by the parent, repeated per level.

What lowers at which cost:
| What a rule sees | Lowering | Cost |
|---|---|---|
| one element | a map | none; splits any way |
| an element and its neighbours | one reduce keyed by the element | one exchange |
| a tree-shaped pattern (chain, star, fork) | one reduce per shared node, in sequence | grows with the pattern, not with the graph |
| a pattern with a cycle | no single key brings all edges together | rows copied to several workers, or several rounds |
| a path of at most k edges | k rounds, or a copy of everything within k steps of each part | around a hub the copy is most of the graph |
| anything reachable | rounds until nothing changes | rounds grow with the longest path; a removal can undo conclusions many rounds away |

## 4. What is known to be impossible or hard

- **Rules without recursion see a fixed distance.** Gaifman's locality theorem: a first-order question depends only on what lies within a fixed number of steps of the elements it names. Consequence: such a rule can run on a part plus a border of that width. Reachability is not local in this sense (Libkin, chapter 4), so no border is enough for it. Sources: [Gaifman](https://cris.huji.ac.il/en/publications/on-local-and-non-local-properties/), [Libkin](https://homepages.inf.ed.ac.uk/libkin/fmt/fmt.pdf).
- **Reachability costs rounds.** Transitive closure cannot be computed in a constant number of rounds in the massively parallel model ([Beame, Koutris, Suciu](https://arxiv.org/abs/1306.5972)). For machines that each hold a small share of the graph, it is conjectured, not proven, that telling one ring from two rings needs a number of rounds that grows with the logarithm of the size ([Nanongkai, Scquizzato](https://arxiv.org/abs/2001.02191)). The same pair of graphs is the textbook witness that connectivity is not local.
- **Patterns with a cycle cannot be split by one key.** In one round, the share of the input each worker must receive is bounded from below by a number that depends on the pattern's shape (its fractional vertex cover); for the triangle this means every edge is copied to about the cube root of the number of workers (computed here from the bound of Beame, Koutris and Suciu; the method of shares is [Afrati and Ullman](https://dblp.dagstuhl.de/rec/conf/edbt/AfratiU10.html)). Tree-shaped patterns have no such cost.
- **Only monotone rules run without coordination.** The CALM theorem: a program has a coordination-free distributed implementation exactly when it is monotone, meaning more input never removes output ([Hellerstein, Alvaro](https://arxiv.org/abs/1901.01930); [Ameloot, Neven, Van den Bussche](https://arxiv.org/abs/1012.2858)). Counts and "there is no ..." are not monotone. They need to know that the input of a key is complete; the engine knows this through its logical frontier.
- **In-place rewriting cannot be checked in general.** Termination of graph rewriting is undecidable (Plump 1998, cited from memory, not opened for this note). Confluence is undecidable even for terminating systems ([Plump](https://eprints.whiterose.ac.uk/195071/1/Plump.ECEASST.10.pdf), found by search, not opened). Tools check a sufficient condition on pairs of overlapping rules.
- **Recursion together with removals is costly.** A removed edge under a recursive rule withdraws everything concluded through it. Differential dataflow handles this by keeping the changes of every round ([McSherry and others](https://www.microsoft.com/en-us/research/publication/differential-dataflow/)), which costs memory in proportion to rounds. This is why the engine's unbounded `fixpoint` was removed.
- **Fresh ids break determinism and incrementality.** If the id of a created element is not a function of what it was made from, two runs give different graphs and a change cannot be matched to what it replaces.
- **Borders grow with hubs.** Natural graphs have a few nodes of very high degree, and balanced cuts of such graphs replicate much of the data ([PowerGraph](https://www.usenix.org/system/files/conference/osdi12/osdi12-final-167.pdf)). In an estate, a cluster or a namespace is such a hub. No source was found for the size of a border of width k; the statement in 3.2 is this note's inference.
- **The graph as the truth loses the text tools.** Comments, layout, review by diff and Kusto's parser and checker (notes 10 and 11) work on text. JetBrains MPS and Enso show the alternatives exist and what they cost. TinkerPop 4 goes back from programs sent as data to text with a grammar.

## 5. What Sattle already has
- Definitions are a graph: the catalog, with the kernel as its ontology, served, compared and versioned like data.
- Every projection is a graph of operators, kept up to date on changes and split by key.
- Ids as a function of keys (`identity()`), lineage per element, stacked projections.
- Ontologies declare which edges are a hierarchy.
- Planned views over the KQL text (note 11): "By example" (draw a pattern, get a `graph-match`), "Wires" (the dataflow).
- Not there: templates that refer to each other's results, recursion along the hierarchy, a graph form that a person edits as the truth. The two-way rules planned in the paper are triple graph grammars, which are rule graphs.

## 6. What the user has said
Asked on 14 November 2026, before the second round. These are his statements, taken as given.
- **What "a graph itself" must give:** all of it. Rules drawn and edited visually, the program projected by the engine like any graph, versioned and compared as a graph, and rules that look like the data. His addition: rules "might define a target structure with placeholders".
- **Visual editing is a hard requirement.**
- **Name and relation to KQL:** "It can be a truly graph based kql. gkql". Later: gkql "will not convert from kql", and "it might just have similarity in namings for the moment". So KQL is one source of names; its meaning, pipeline model and operator set are not assumed, and no round trip with KQL text is a goal.
- **"Without the whole graph":** bounded memory, many machines and computing only what changed, all three equally.
- **Recursion:** any edges, bounded depth.
- **Vocabulary:** no invented terms; every term from an existing language with its source, reviewable by him.
- **A claim he wants examined:** "If its a graph base language, visual editing will come easy" (section 8).
- **Open:** who writes projections and how far the substance moves from KQL. He wants to see more first.

Second round, asked interactively on 14 November 2026:
- **Main picture of a program:** "Show me all three first". Not chosen.
- **What travels on a wire:** "Not sure yet". Not chosen.
- **The KQL work while this is explored:** "Everything continues". This settles decision D7.
- **Next step:** "All three": real projections redrawn in each picture, a clickable mock, and a prototype on the engine. The first two are a second page (in work). The third touches the engine and is handed to the orchestrator session.

Third round, asked interactively on 14 November 2026 after the second page:
- **Picture for the engine prototype:** "C: wired verbs, drawn inside". A dataflow of verbs whose match nodes hold a drawn pattern and whose sinks hold a drawn target with placeholders.
- **A child in state Unknown:** "Set per parent". A setting on each roll-up.
- **May a rule read what another rule built:** "No, stack programs instead". A rule reads input graphs only; what a program built is read by a second program on top. So the roll-up of a health model is a program stacked on the program that builds entities and states, one level per program or a bounded path.
- **Delivery to Azure Monitor:** "Explore both further".

Consequences drawn here, not by him:
- "All three equally" means the language can promise them only for rules whose cost class is known, as XSLT does with streamability. Whether to refuse other rules is decision D4.
- Bounded depth keeps the program graph free of loops: a path is a label on one pattern edge.
- The editor plan of note 11 treats KQL text as the only truth. A graph form edited as the truth does not share that model (decision D7).

## 7. The query verbs as a graph (gkql is the user's working name)

One possible picture of a rule, used on the page: a `graph-match` node that holds a drawn pattern (two pods, the services they back), a `where` node, a `summarize ... by` node that shows its key, and an `edges` node that holds the target, a small graph with placeholders in braces.

| Verb | Name from | As a node | What changes in a graph | Becomes in the engine |
|---|---|---|---|---|
| `graph("name")` | KQL function | A node with no input. It names one graph of the space and gives out its elements. | It becomes the only place where a name from outside appears. Everything else is connected by wires. | A map over the changes of that graph. |
| `graph-match` | KQL operator; MATCH in GQL | A node that holds a small pattern graph. It gives out one match per place the pattern fits, with one variable per pattern element. | The pattern stops being a line of characters like (a)-[c]->(b) and becomes a drawn subgraph. The program now has two levels: a graph of verbs, and a pattern graph inside one of them. | One map per pattern edge, one reduce per pattern node that two edges share. |
| `where` | KQL, SQL, Cypher | One wire in, one out, one condition. | The condition is an expression. Either it stays a short text inside the node, or it becomes a tree of small nodes, which is where node editors get large (Blender's math nodes). | A map. |
| `extend, project` | KQL | One wire in, one out, a list of named expressions. | If the target is drawn with placeholders, most of project's work (choosing and renaming what is kept) moves into the target. | A map. |
| `summarize ... by` | KQL; GROUP BY in SQL and GQL | One wire in, one out. Holds the aggregates and the 'by' key. | The 'by' key is the declared key of a reduce. On a node it can be shown as such, and it is exactly the key the work is split by. | A reduce keyed by the 'by' columns. |
| `join` | KQL, SQL | Two wires in, one out, and the values that must be equal. | In text one side is the pipeline and the other sits in parentheses. In a graph both are wires, so the two input ports need names (left, right). If patterns are drawn, most joins disappear: the same variable in two places is the join. | A reduce keyed by the join key, holding both sides. |
| `union` | KQL, SQL, SPARQL | Several wires in, one out. | It can vanish as a node: two wires into the same port. | A map. |
| `distinct` | KQL, SQL | One in, one out. | If wires carry sets and not bags, it is not needed at all. | A reduce keyed by the whole row, which counts how often it is supported. |
| `arg_max, max, count, sum` | KQL aggregation functions | Entries inside a summarize node, not nodes of their own. | Nothing changes. One point to state: arg_max must remember every candidate, because the current winner can be withdrawn. | Part of the reduce of their summarize node. |
| `nodes, edges (graph model steps)` | Sattle's text form of Kusto's AddNodes and AddEdges; CONSTRUCT in SPARQL and G-CORE | A sink that holds the target: a small graph with placeholders, and for each target node the key its identity is computed from. | Today one type is built by several steps (one makes the nodes, others extend them). Drawn, they are one target structure. This is your 'target structure with placeholders'. | A map that computes each id, then a reduce keyed by that id that counts support and merges properties. |
| `-[e*1..16]->` | KQL and Cypher variable-length edge; quantified path pattern in GQL | An edge of the pattern labelled with a range. | Recursion stays inside the pattern as a label on one edge. The program graph itself has no loop, which matches your answer: any edges, bounded depth. | A reduce keyed by the start element, recomputed when an element it visited changes. |
| `let` | KQL statement | Not a node. A name for a wire that several verbs read. | Fan-out is free in a graph: one output, several wires. The name becomes optional, but names are documentation, so a label on the wire is still wanted. | Nothing; a shared stream. |
| `function with a tabular parameter, invoke` | KQL; node group in Blender; subflow in Node-RED | A collapsed node with ports that opens into its own graph. | What is a text function becomes the standard way node editors fight size: pack a part into one node. | Expanded in place before lowering. |
| `summarize ... by bin(time, span)` | KQL | A summarize node whose key includes a time window. | Nothing in shape. The window is part of the key shown on the node. | A reduce keyed by element and window, closed by the engine's logical time. |
| `hysteresis` | Sattle's addition to KQL; 'for' in Prometheus alerting rules | A node holding thresholds and durations; gives a state per element. | Nothing in shape. | A reduce keyed by element, holding a small state machine. |

What changes meaning when the program is a graph:
- **A pipeline becomes a graph without loops.** Fan-out and fan-in are free; names (`let`) become optional. There is no reading order unless layout gives one.
- **Sides need names.** The two inputs of a join are wires, so ports are named.
- **Two levels of graph.** A pattern is a graph inside a node of the program graph; editing, comparison and storage must handle that.
- **Variables become connections.** Using a pattern variable in the target is a link between a pattern node and a target node. Graph rewriting calls the shared part the interface of a rule.
- **Recursion is a label, not a loop.** A loop wire would mean "until nothing changes", which was not asked for.
- **Order of verbs is not written.** Safe only because every verb builds and none changes its input.

Prior art for query verbs as nodes:
- **Query-by-Example (1977).** The user fills in an example of the answer in empty tables. Constants and example elements are told apart. The first query language that is not a line of text, and the root of 'by example'. [Zloof, Query-by-Example: a data base language, 1977](https://doi.org/10.1147/sj.164.0324)
- **G and GraphLog (1987, 1990).** Graph queries drawn as graph patterns. An edge of the query may stand for a whole path, described by a regular expression. Recursion without writing a recursive rule. [Cruz, Mendelzon, Wood, A graphical query language supporting recursion, 1987](https://doi.org/10.1145/38713.38749) [Consens, Mendelzon, GraphLog: a visual formalism for real life recursion, 1990](https://doi.org/10.1145/298514.298591)
- **GOOD (1990, 1994).** A database model in which schemas, data and the operations on them are all graphs, and every operation is a graph transformation. [Gyssens, Paredaens, Van den Bussche, Van Gucht, A graph-oriented object database model, 1994](https://documentserver.uhasselt.be/handle/1942/3752)
- **Azure Data Factory mapping data flows.** Query verbs as boxes on a canvas, translated by the service and run on scaled-out Spark clusters. The partitioning of each box is set on the box. A script form sits behind the picture. [Azure Data Factory, mapping data flows](https://learn.microsoft.com/en-us/azure/data-factory/concepts-data-flow-overview)
- **Power Query diagram view.** Each query is a chain of named steps shown as a diagram. Every step is M code that the advanced editor shows. [Power Query, diagram view](https://learn.microsoft.com/en-us/power-query/diagram-view)
- **Blender, Node-RED, NiFi, Enso.** Described under Prior art. They show wired verbs at industrial scale, for geometry, messages, routing and tables. [Blender manual, Geometry Nodes](https://docs.blender.org/manual/en/latest/modeling/geometry_nodes/introduction.html) [Node-RED, importing and exporting flows](https://nodered.org/docs/user-guide/editor/workspace/import-export) [Apache NiFi overview](https://nifi.apache.org/docs/nifi-docs/html/overview.html) [Enso](https://github.com/enso-org/enso)

## 8. "If it is a graph, visual editing comes easy"

What the graph form gives for free:
- No syntax errors: every edit goes from one well-formed program to another.
- Edits are structural and each is a small graph patch, which the engine already stores, replays and undoes.
- One model: the editor and the engine read the same graph; nothing is parsed or printed.
- A port knows what may connect to it, so the editor can offer only what fits.
- Each node can show the result after it.

What it does not give:
- **Layout.** Where a node sits means something to a reader and nothing to the engine. Someone has to keep it tidy. Epic's guidance for Blueprints tells authors to keep graphs tidy from the start and to mark sections with comment boxes. [Unreal Engine, Blueprint best practices](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprint-best-practices-in-unreal-engine)
- **Readability as programs grow.** Green and Petre examined two commercial dataflow languages and found resistance to local change (their word: viscosity): a small edit means moving boxes and re-routing wires. Petre found that reading a graphical program is a learned skill. [Green, Petre, Usability analysis of visual programming environments: a cognitive dimensions framework, 1996](https://doi.org/10.1006/jvlc.1996.0009) [Petre, Why looking isn't always seeing: readership skills and graphical programming, 1995](https://doi.org/10.1145/203241.203251)
- **Size.** Every tool answers the tangle the same way, by packing: functions and macros in Blueprints, node groups in Blender, subflows in Node-RED. Packing is a feature that must be built and learned. [Unreal Engine, Blueprint best practices](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprint-best-practices-in-unreal-engine) [Blender manual, node groups](https://docs.blender.org/manual/en/latest/interface/controls/nodes/groups.html) [Node-RED, importing and exporting flows](https://nodered.org/docs/user-guide/editor/workspace/import-export)
- **Comparing and reviewing.** A text diff of a stored graph tells a reviewer little. Epic built a graph diff for Blueprints, National Instruments built Compare VIs for LabVIEW, Eclipse built EMF Compare for models. Sattle has graph patches, which is the data of a diff. The picture a reviewer reads is not there yet. [Unreal Engine, diff tool](https://dev.epicgames.com/documentation/en-us/unreal-engine/ue-diff-tool-in-unreal-engine) [LabVIEW, comparing VIs](https://www.ni.com/docs/en-US/bundle/labview/page/comparing-vis.html) [EMF Compare, Eclipse](https://eclipse.dev/emf/compare/)
- **Keyboard editing.** A controlled experiment with an editor that has no text underneath (JetBrains MPS, 19 participants) found basic editing efficient, with fewer typing errors and more effort for complex edits. [Berger and others, Efficiency of projectional editing: a controlled experiment, 2016](https://doi.org/10.1145/2950290.2950315) [Voelter and others, Towards user-friendly projectional editors, 2014](https://doi.org/10.1007/978-3-319-11245-9_3)
- **Expressions.** Conditions and arithmetic either stay short texts inside a node, which brings a small parser back, or become many tiny nodes. 
- **A text form.** Azure Data Factory keeps a script behind its canvas, Power Query keeps M, Enso keeps text in step with the graph, and TinkerPop 4 returns to text for programs sent between machines. You ruled out converting from KQL. Whether the graph form needs a text form of its own, for search, review and exchange, is a separate question. [Azure Data Factory, mapping data flows](https://learn.microsoft.com/en-us/azure/data-factory/concepts-data-flow-overview) [Power Query, diagram view](https://learn.microsoft.com/en-us/power-query/diagram-view) [Enso](https://github.com/enso-org/enso) [Apache TinkerPop reference](https://tinkerpop.apache.org/docs/current/reference/)

Read together: a graph form removes parsing and makes every edit safe. It does not make programs readable, comparable or quick to type; each of those was built separately in every tool listed.

## 9. Terms
No term is coined. For each concept: the names existing languages use, and how the concept would show in a graph form. The page lets the user pick a name or question the concept, with a note; his marks are stored with the page (collections `terms` and `decisions` of its database) and are read back at the start of the next round. Both were empty when this note was written.

| Concept | In a graph form | Names in existing languages |
|---|---|---|
| The small shape a rule looks for in the input graph. | A drawn subgraph: nodes and edges with type labels and variable names. | graph pattern (GQL, Cypher, SPARQL); graph-match pattern (KQL); left-hand side (LHS) (graph rewriting); source pattern (ATL); match pattern (XSLT); body (Datalog) |
| What a rule builds for every match: your 'target structure with placeholders'. | A drawn subgraph whose nodes and edges carry variables of the pattern and expressions over them. | CONSTRUCT template (SPARQL, G-CORE); right-hand side (RHS) (graph rewriting); target pattern (ATL); sequence constructor (XSLT); head (Datalog); graph model step (AddNodes, AddEdges) (KQL) |
| A hole in the target that is filled from the match. | A label on a target node or edge that names a variable of the pattern or an expression. | variable (SPARQL, GQL, Cypher, Datalog); attribute value template (XSLT); binding (ATL); column reference (KQL) |
| One unit of a projection: a pattern together with what is built from it. | One connected piece of the program graph: a pattern, optional steps, a target. | rule (graph rewriting, Datalog); template rule (XSLT); matched rule (ATL); step (KQL graph models, Gremlin); production (graph grammars) |
| One operation on the rows or matches flowing through a program. | A node of the program graph with input and output ports. | operator (KQL, dataflow systems); node (Blender, Node-RED); instruction (XSLT); step (Gremlin); transformation (Azure Data Factory mapping data flows) |
| The connection that carries the output of one verb to the next. | An edge of the program graph. In text it is the pipe or a name. | pipe (KQL); link (Blender); wire (Node-RED); connection (Apache NiFi); stream (dataflow systems, DBSP) |
| Keep only the matches that meet a condition. | A verb node holding one condition, or a condition written on the pattern itself. | where (KQL, SQL, Cypher); FILTER (GQL, SPARQL); predicate (XPath, XSLT); application condition (graph rewriting); guard (ATL) |
| Add or change values computed from others. | A verb node holding named expressions, or expressions written into the target. | extend (KQL); project (KQL); LET, RETURN (GQL); BIND (SPARQL); select (SQL) |
| Combine all matches that share a key into one value: count, sum, worst state. | A verb node that shows its key; or an aggregate written on a target element, grouped by that element's identity. | summarize ... by (KQL); GROUP BY (SQL, GQL); xsl:for-each-group (XSLT); accumulator (ACCUM) (GSQL); reduce (MapReduce); GROUP in CONSTRUCT (G-CORE) |
| Bring together two sets of matches that agree on a value. | Two wires into one verb node; or no node at all: the same variable used in two places of a pattern. | join kind=... (KQL, SQL); shared variable (Cypher, SPARQL, Datalog); xsl:key and key() (XSLT); gluing (graph rewriting) |
| Treat the results of several branches as one. | Several wires into one input; or several rules that build the same type. | union (KQL, SQL, SPARQL); several rules with one head (Datalog) |
| Follow edges of one kind for up to a fixed number of steps. | An edge of the pattern labelled with a range, not a loop in the program graph. | variable-length edge (KQL, Cypher); quantified path pattern (GQL); property path (SPARQL); descendant axis (XPath, XSLT); recursive rule (Datalog) |
| Hand what a rule found to whichever rule knows how to treat it. | An edge from a placeholder in one rule's target to 'whatever was built from this element', resolved through lineage. | xsl:apply-templates (XSLT); resolveTemp (ATL); structural recursion (UnQL); superstep (Pregel) |
| The stable id of an element that a rule creates. | A key shown on each target node: the placeholders its id is computed from. | Skolem function (logic, data exchange, G-CORE); identity(domain, key, ...) (Sattle's addition to KQL); MERGE (Cypher); key (SQL, PG-Keys); generate-id() (XSLT) |
| The record of what each built element was made from. | Edges from a target element to the pattern elements of its matches, kept by the engine. | traceability link (ATL); correspondence (triple graph grammars); provenance (Apache NiFi, W3C PROV); lineage (Sattle, data engineering) |
| A step that needs no memory: each change in gives changes out on its own. | A verb node with no key shown. | map (MapReduce, functional programming); linear operator (DBSP); stateless operator (Apache Flink) |
| A step whose memory is filed under a key. | A verb node that shows its key; the key is also how the work is split. | reduce (MapReduce); keyed state (Apache Flink); arrangement (differential dataflow); summarize ... by (KQL) |
| One fact becoming true or ceasing to be true. | Not part of the notation; it is what flows on every wire at run time. | weight (in a Z-set) (DBSP); difference (differential dataflow); retraction (stream processing, Rete); delta (Datalog (semi-naive evaluation)); patch (Sattle) |
| The share of the keys that one worker holds. | Not drawn; derived from the keys shown on reduce nodes. | partition (Spark, Kafka, MapReduce); shuffle (hint.shufflekey) (KQL, Spark); exchange (dataflow systems); shard (databases) |
| What a worker must hold beyond its own share to apply a rule. | Derived from the largest pattern and the longest path range in the program. | replica (ghost) (PowerGraph, GraphLab); r-neighbourhood (finite model theory (Gaifman)); halo (parallel computing) |
| The named graph a set of rules builds. | The whole program graph, or one sink node per result graph. | graph model (KQL); materialized view (SQL, KQL); named graph (SPARQL); target model (ATL); projection (Sattle) |
| A piece of a program that is named and reused. | A collapsed node with ports that opens into its own graph. | function with a tabular parameter, invoke (KQL); node group (Blender); subflow (Node-RED); named template (XSLT); called rule (ATL); unit (Henshin) |
| A span of the data's time over which values are combined. | A key part shown on an aggregate node, next to the element key. | bin() (KQL); tumbling window (TUMBLE) (Flink SQL, stream processing); range vector (PromQL) |
| A rule that holds because something is not there. | A crossed-out part of a pattern. | negative application condition (NAC) (graph rewriting); NOT EXISTS (SQL, SPARQL); join kind=leftanti (KQL); negation (Datalog) |
| A set of rules that runs as a whole before the next set. | One program graph per stage; the result graph of one is a source of the next. | stratum (Datalog); mode (XSLT); layer (AGG (layered graph grammars)); stacked projection (Sattle) |
| The program stored in the same form as the data it works on. | The program graph conforms to an ontology of its own, like any graph of the space. | homoiconicity (Lisp); stylesheet as XML document (XSLT); transformation model (model-driven engineering, ATL); abstract syntax graph (compilers); catalog (Sattle) |
| The name of the language itself. |  | gkql (your working name); no name yet |

Shorthand used on the page and in this note, not proposed as terms:
- wish, and the letters G O P I: this page's shorthand for four properties; the standard words are homoiconic (G), composable or closed (O), partitioned (P), incremental (I)
- runs in parts: partitioned, data-parallel
- runs on changes: incremental view maintenance
- border: halo, ghost vertices, r-neighbourhood (reviewable below)
- rule graph, dataflow graph, syntax graph: 'rule graph' is GROOVE's word; 'dataflow graph' is standard; 'syntax graph' stands for abstract syntax graph
- wall: a known limit; in the literature a lower bound or an undecidability result

## 10. Decisions for the user
No recommendation is made. Each option lists what follows from it. The earlier questions on the meaning of "a graph", on text or graph as the truth, and on recursion are answered in section 6.

**D1. What is the main picture of a program?** You asked for both a graph of verbs and rules that look like the data. They can be one thing or two.
- *Verbs wired together.* A dataflow: sources, where, summarize, join, sinks. Patterns and targets are properties of nodes. Closest to KQL's verbs and to tools like Blender and Azure Data Factory.
- *Pattern-and-target rules.* Each rule is a pattern beside a target with placeholders. Counts and filters are written on the pattern or the target. Closest to XSLT, ATL and graph grammars.
- *Wired verbs with drawn patterns and targets.* A dataflow whose match nodes contain a drawn pattern and whose sinks contain a drawn target. Two levels of graph. The most expressive and the most to learn.

**D2. What travels on a wire?** This fixes what every verb means, and how far the language is from KQL in substance.
- *Rows with named columns.* As in KQL and every dataflow tool. Graph elements are values in columns.
- *Matches.* Each item is one binding of pattern variables to graph elements, as in SPARQL and Cypher. Verbs speak about elements, not columns.
- *Graphs.* Every verb takes a graph and gives a graph, as G-CORE proposed. Uniform, and furthest from KQL.

**D3. How much may one rule see?** This sets how large a part a worker must hold. You answered 'any edges, bounded depth' for recursion; this is the same question for patterns.
- *An element and its direct neighbours.* Every rule runs on one key. Larger shapes are several rules in a row.
- *A tree-shaped pattern.* Chains, stars and forks. Lowers to one reduce per shared node. Patterns with a cycle are refused.
- *Any pattern, cost shown.* Patterns with a cycle are allowed and marked with what they cost.

**D4. You want bounded memory, many machines and only-what-changed, all three equally. What happens to a rule that cannot promise them?** XSLT refuses a template that is not streamable before it runs.
- *Refuse it.* The language accepts only rules with a known bound. Simple promise, smaller language.
- *Accept it and mark the node.* The editor shows the cost on the verb. The author decides.
- *Accept it silently.* As most query languages do.

**D5. May a rule change a graph in place?** Building only is what keeps results independent of order.
- *Build only, as today.* A program reads graphs and builds a new one. No order, no conflicts, always ends.
- *Also rewrite in place.* Brings order, conflict analysis and the question of ending.

**D6. Who is the author you want to win?** You said undecided. The notation follows the reader.
- *An engineer who reads KQL.* Verb names and concepts stay familiar. Keyboard editing matters as much as drawing.
- *A domain person who does not program.* Drawing first. Needs a study with such people before any build.
- *Both equally.* Every construct needs to work by mouse and by keyboard.

**D7. What happens to the KQL work while this is explored?** The graph form will not convert from KQL, so the two do not share a notation. The editor plan of note 11 treats KQL text as the only truth.
- *Everything continues.* KQL front end, retirement of the old text and the editors of note 11 go on. The graph form is research beside it.
- *Engine and front end continue, editors pause.* The visual editors of note 11 wait, because a graph form would replace their model.
- *KQL work stops at its current state.* All effort goes to the graph form.

**D8. What should the next step be?** You asked to see more before choosing.
- *Redraw real projections on paper.* Take three shipped projections (service tree, deployment health, workload health) and draw each in the candidate pictures of the first question. Count what a reader must learn. No code.
- *A clickable mock in a page like this.* One projection as an editable program graph in the browser, with no engine behind it, to test how editing feels.
- *A throwaway prototype on the engine.* One projection as a program graph, lowered to the engine's existing operators and compared by digest with its KQL twin.
- *Stop here.* The map is enough for now.

## 11. Prototypes
Two small examples live in the page, not in the engine:
- **Is this part enough?** A workload, three services and five pods, one pod failing. Asking for the state of an element highlights what the answer needed. Following containment only, the part is the subtree. Following dependencies as well, most of the graph is needed, and a worker holding only the subtree gives a wrong answer.
- **One change, and what it touches.** Calls between pods are switched on and off. Each switch is shown as a change with weight +1 or -1 going through a map (pod to service), to the one worker that holds the key (a pair of services), through a reduce (the count of calls behind that pair), and out as a change of the service graph only when the count crosses zero.

### 11.1 Second page: four projections in three pictures
Published at https://claude.ai/artifact/AbxqUh2hdHQ3W9LcdPfn2T (private to its owner until shared). It draws a health model projection (section 1.5, invented names and input shapes), the service tree, deployment health and the roll-up slice of the workload health, each in three pictures generated from one data description per picture, and has one program graph that can be edited, with a small evaluator behind it.

The pictures:
- **A, verbs wired together.** A dataflow of verbs; patterns and targets are text inside nodes.
- **B, pattern-and-target rules.** A drawn graph pattern and a drawn target with placeholders per rule; counts, filters and keys are annotations.
- **C, wired verbs with drawn patterns and targets.**

Counts from the page (boxes, wires, drawn pattern and target elements, constructs a reader must know, maps, keyed reduces):

| Projection | A | B | C |
|---|---|---|---|
| health model | 21, 28, 0, 10, 8, 8 | 5, 3, 30, 8, 2, 3 | 20, 28, 20, 13, 8, 8 |
| service tree | 22, 30, 0, 10, 6, 10 | 3, 0, 20, 6, 0, 7 | 17, 22, 17, 12, 5, 8 |
| deployment health | 14, 17, 0, 9, 7, 4 | 3, 1, 16, 7, 1, 2 | 16, 19, 15, 12, 9, 4 |
| workload roll-up (slice) | 11, 11, 0, 8, 4, 4 | 2, 1, 4, 7, 0, 1 | 11, 11, 5, 12, 4, 4 |

What each picture could say only as text:
- **A:** the shape of every pattern; that two flows share one resource (visible only in the identity text); the bounded path; Unknown as a number set by a step.
- **B:** time (the window, sums per window, hysteresis are text on the target edge); the order of steps; a rule that reads what another rule built needs a second, dashed kind of wire; joins are hidden in shared variables; the join between the authored and the observed graph is a text condition.
- **C:** which input fills which placeholder; two levels of graph to read. Joins on built state and unions stay verbs.
- **A and C:** a roll-up that feeds itself one level up cannot be drawn in a dataflow without loops; the box says "repeated per level" in text. `WorstOf` could be one reduce over a bounded path, because impacts compose step by step; `MaxNotHealthy` cannot.

Assumptions made for the health model example, to be confirmed: the shape of the authored input, that a parent is Unknown only when every child is, and that `Average` is the mean of child severities.

The pictures were checked for overlapping and clipped labels by a geometry test and looked at once at phone width. The evaluator of the mock was run on its sample graph. No screen reader test was done.

## 12. What could not be verified
- Publisher pages of Springer, ACM and Elsevier refused access; the DOIs of those papers were confirmed through indexes only. They are marked in the sources.
- Termination of graph rewriting as undecidable (Plump 1998) and the details of XSLT's streamability classes were not read in a source for this note.
- The evaluation model of Blender Geometry Nodes and the Blender pages on frames and reroute nodes were not read.
- Query-by-Example, G, GraphLog and GOOD are described from index entries and search results; the papers were not opened. That GraphLog equals a class of Datalog, and GOOD's list of five operations, are not confirmed and not claimed here.
- The findings of Green and Petre, of Petre, and of the projectional editing experiment are taken from summaries, not from the papers.
- The KQL operator pages for where, project, extend, summarize, join, union and distinct were not opened for this note (note 10 opened several of them on 12 November).
- The term "retraction" in Flink, and "ghost" or "mirror" in the PowerGraph paper, were not confirmed in a source.
- Whether UnCAL results can be kept up to date under changes: no source found.
- TigerGraph's distributed execution of accumulators, and whether QVT specifications are stored as models, are not stated on the pages opened.
- The ratings of section 2 are a reading of documents; no tool was run.
- Saving marks from the page was not exercised in a real viewer; reading the page's database from this session works and returned no documents.

## Sources
Opened on 14 November 2026 unless marked.

- [XSLT 3.0, W3C Recommendation, 8 June 2017](https://www.w3.org/TR/xslt-30/)
- [Knuth, Semantics of context-free languages, 1968](https://doi.org/10.1007/BF01692511) (attribute grammars)
- [Reps, Teitelbaum, Demers, Incremental context-dependent analysis for language-based editors, 1983](https://research.cs.wisc.edu/wpis/abstracts/toplas83.abs.html)
- [Stratego reference, Spoofax](https://www.spoofax.dev/references/stratego/)
- [Buneman, Fernandez, Suciu, UnQL: a query language and algebra for semistructured data based on structural recursion, 2000](https://doi.org/10.1007/s007780050084)
- [Tung, Hu, Towards systematic parallelization of graph transformations over Pregel, 2017](https://doi.org/10.1007/s10766-016-0418-5) (publisher page not opened)
- [Ehrig, Ehrig, Prange, Taentzer, Fundamentals of Algebraic Graph Transformation, 2006](https://doi.org/10.1007/3-540-31188-2) (publisher page not opened)
- [Plump, Confluence of graph transformation revisited](https://eprints.whiterose.ac.uk/195071/1/Plump.ECEASST.10.pdf) (found by search, not opened; the termination result, Plump 1998, is cited from memory)
- [AGG, TU Berlin](https://user.tu-berlin.de/o.runge/AGG/WWW/agg.html)
- [GROOVE manual, University of Twente](https://groove.cs.utwente.nl/manual_introduction.html)
- [GP 2, University of York](https://github.com/UoYCS-plasma/GP2)
- [GrGen.NET](https://grgen.de/)
- [Henshin, Eclipse](https://projects.eclipse.org/projects/modeling.emft.henshin)
- [Krause, Tichy, Giese, Implementing graph transformations in the bulk synchronous parallel model, 2014](https://doi.org/10.1007/978-3-642-54804-8_23)
- [Lafont, Interaction nets, 1990](https://en.wikipedia.org/wiki/Interaction_nets) (secondary source; the paper was not opened)
- [HVM, Higher Order Company](https://github.com/HigherOrderCO/HVM)
- [eMoflon::IBeX](https://emoflon.org/ibex/)
- [ATL, Eclipse](https://eclipse.dev/atl/)
- [Tisi and others, On the use of higher-order model transformations, 2009](https://doi.org/10.1007/978-3-642-02674-4_3) (publisher page not opened)
- [Jouault, Tisi, Towards incremental execution of ATL transformations, 2010](https://doi.org/10.1007/978-3-642-13688-7_9) (publisher page not opened)
- [Benelallam and others, Distributed model-to-model transformation with ATL on MapReduce, 2015](https://doi.org/10.1145/2814251.2814258) (publisher page not opened)
- [OMG, MOF Query/View/Transformation 1.3, 2016](https://www.omg.org/spec/QVT/1.3)
- [VIATRA, Eclipse](https://eclipse.dev/viatra/)
- [Szarnyas and others, IncQuery-D: a distributed incremental model query framework in the cloud, 2014](https://doi.org/10.1007/978-3-319-11653-2_40) (publisher page not opened)
- [Rete algorithm (Forgy 1982)](https://en.wikipedia.org/wiki/Rete_algorithm) (secondary source)
- [SPARQL 1.1 Query Language, section 16.2 CONSTRUCT](https://www.w3.org/TR/sparql11-query/#construct)
- [SHACL Advanced Features, W3C Working Group Note, 2017](https://www.w3.org/TR/shacl-af/)
- [SHACL 1.2 Inference Rules, W3C Working Draft, November 2026](https://www.w3.org/TR/shacl12-inference-rules/)
- [SPIN overview, W3C Member Submission, 2011](https://www.w3.org/submissions/spin-overview/)
- [Angles and others, G-CORE: a core for future graph query languages, 2018](https://arxiv.org/abs/1712.01550)
- [GQL standards (ISO/IEC 39075:2024)](https://www.gqlstandards.org/)
- [Kusto graph semantics overview](https://learn.microsoft.com/en-us/kusto/query/graph-semantics-overview)
- [Kusto graph models](https://learn.microsoft.com/en-us/kusto/management/graph/graph-model-overview)
- [Apache TinkerPop reference](https://tinkerpop.apache.org/docs/current/reference/)
- [CQL, categorical query language](https://www.categoricaldata.net/)
- [TigerGraph GSQL accumulators](https://www.tigergraph.com/docs/gsql-ref/current/querying/accumulators)
- [Datalog (semi-naive evaluation)](https://en.wikipedia.org/wiki/Datalog) (secondary source)
- [Souffle](https://souffle-lang.github.io/)
- [McSherry, Murray, Isaacs, Isard, Differential dataflow, 2013](https://www.microsoft.com/en-us/research/publication/differential-dataflow/)
- [Differential dataflow, source](https://github.com/TimelyDataflow/differential-dataflow)
- [Budiu, Chajed, McSherry, Ryzhyk, Tannen, DBSP: automatic incremental view maintenance for rich query languages, 2023](https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf)
- [Materialize](https://materialize.com/docs/overview/what-is-materialize/)
- [Malewicz and others, Pregel: a system for large-scale graph processing, 2010](https://research.google/pubs/pregel-a-system-for-large-scale-graph-processing/)
- [Gonzalez and others, GraphX: graph processing in a distributed dataflow framework, 2014](https://www.usenix.org/conference/osdi14/technical-sessions/presentation/gonzalez)
- [Gonzalez and others, PowerGraph: distributed graph-parallel computation on natural graphs, 2012](https://www.usenix.org/system/files/conference/osdi12/osdi12-final-167.pdf)
- [Hellerstein, Alvaro, Keeping CALM: when distributed consistency is easy, 2019](https://arxiv.org/abs/1901.01930)
- [Ameloot, Neven, Van den Bussche, Relational transducers for declarative networking](https://arxiv.org/abs/1012.2858)
- [Gaifman, On local and non-local properties, 1982](https://cris.huji.ac.il/en/publications/on-local-and-non-local-properties/)
- [Libkin, Elements of Finite Model Theory, chapter 4 (locality)](https://homepages.inf.ed.ac.uk/libkin/fmt/fmt.pdf)
- [Beame, Koutris, Suciu, Communication steps for parallel query processing](https://arxiv.org/abs/1306.5972)
- [Nanongkai, Scquizzato, Equivalence classes and conditional hardness in massively parallel computations](https://arxiv.org/abs/2001.02191)
- [Afrati, Ullman, Optimizing joins in a map-reduce environment, 2010](https://dblp.dagstuhl.de/rec/conf/edbt/AfratiU10.html) (index entry; the paper was not opened)
- [Blender manual, Geometry Nodes](https://docs.blender.org/manual/en/latest/modeling/geometry_nodes/introduction.html) (evaluation details from the developer notes, not opened)
- [Node-RED, importing and exporting flows](https://nodered.org/docs/user-guide/editor/workspace/import-export)
- [Unreal Engine, Blueprint compiler overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/compiler-overview-for-blueprints-visual-scripting-in-unreal-engine)
- [Enso](https://github.com/enso-org/enso)
- [Apache NiFi overview](https://nifi.apache.org/docs/nifi-docs/html/overview.html)
- [JetBrains MPS](https://www.jetbrains.com/mps/) (not opened for this note)
- [Zloof, Query-by-Example: a data base language, 1977](https://doi.org/10.1147/sj.164.0324) (index entry; the paper was not opened)
- [Cruz, Mendelzon, Wood, A graphical query language supporting recursion, 1987](https://doi.org/10.1145/38713.38749) (index entry; the paper was not opened)
- [Consens, Mendelzon, GraphLog: a visual formalism for real life recursion, 1990](https://doi.org/10.1145/298514.298591) (index entry; the paper was not opened)
- [Gyssens, Paredaens, Van den Bussche, Van Gucht, A graph-oriented object database model, 1994](https://documentserver.uhasselt.be/handle/1942/3752) (found by search, not opened)
- [Azure Data Factory, mapping data flows](https://learn.microsoft.com/en-us/azure/data-factory/concepts-data-flow-overview)
- [Power Query, diagram view](https://learn.microsoft.com/en-us/power-query/diagram-view)
- [KQL quick reference](https://learn.microsoft.com/en-us/kusto/query/kql-quick-reference) (found by search; the operator pages where, project, extend, summarize, join, union and distinct were not opened for this note)
- [KQL graph-match operator](https://learn.microsoft.com/en-us/kusto/query/graph-match-operator)
- [KQL let statement](https://learn.microsoft.com/en-us/kusto/query/let-statement)
- [KQL bin()](https://learn.microsoft.com/en-us/kusto/query/bin-function)
- [KQL shuffle query (hint.shufflekey)](https://learn.microsoft.com/en-us/kusto/query/shuffle-query)
- [KQL materialized views](https://learn.microsoft.com/en-us/kusto/management/materialized-views/materialized-view-overview)
- [Cypher manual, patterns](https://neo4j.com/docs/cypher-manual/current/patterns/)
- [ATL user guide, the ATL language](https://github.com/eclipse-atl/atl/wiki/User_Guide_-_The_ATL_Language)
- [Differential dataflow book, arrangements](https://timelydataflow.github.io/differential-dataflow/chapter_5/chapter_5_1.html)
- [Apache Flink, working with state (keyed state)](https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/datastream/fault-tolerance/state/)
- [Green, Petre, Usability analysis of visual programming environments: a cognitive dimensions framework, 1996](https://doi.org/10.1006/jvlc.1996.0009) (found by search, not opened)
- [Petre, Why looking isn't always seeing: readership skills and graphical programming, 1995](https://doi.org/10.1145/203241.203251) (found by search, not opened)
- [Unreal Engine, Blueprint best practices](https://dev.epicgames.com/documentation/en-us/unreal-engine/blueprint-best-practices-in-unreal-engine)
- [Unreal Engine, diff tool](https://dev.epicgames.com/documentation/en-us/unreal-engine/ue-diff-tool-in-unreal-engine) (found by search, not opened)
- [Berger and others, Efficiency of projectional editing: a controlled experiment, 2016](https://doi.org/10.1145/2950290.2950315) (index entry; the paper was not opened)
- [Voelter and others, Towards user-friendly projectional editors, 2014](https://doi.org/10.1007/978-3-319-11245-9_3) (index entry; the paper was not opened)
- [EMF Compare, Eclipse](https://eclipse.dev/emf/compare/)
- [LabVIEW, comparing VIs](https://www.ni.com/docs/en-US/bundle/labview/page/comparing-vis.html) (found by search, not opened)
- [Blender manual, node groups](https://docs.blender.org/manual/en/latest/interface/controls/nodes/groups.html) (page reached, text not read)
