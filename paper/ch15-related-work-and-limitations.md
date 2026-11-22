# Ch. 15 Related work and limitations

**Question.** Where does this design come from, what does it take from earlier work and what does it leave, and what can it not do?

**What you can state at the end.** The design combines incremental computation, graph schemas, lenses and provenance under a log per graph, with definitions kept as data. Reproducibility depends on the recorded inputs, control events and stated domains; it is not an unconditional property of every proposed feature. This chapter separates implementation limits, evidence limits and gaps found in the manual proof review. The combination is the subject of the research, not a demonstrated claim of priority over all earlier systems.

This chapter uses all earlier chapters.

## 15.1 Related work

Each entry says what this design takes and what it does not.

**Incremental computation.**
- DBSP and Z-sets give the algebra, the classes of operators (linear, bilinear, not linear) and the theorem of incremental evaluation used in Ch. 4, 6 and 13 ([budiu2023]). This design does not treat every signed delta as a graph (Theorem 4.9).
- Differential dataflow indexes differences by versions that are only partially ordered, the reference answer for incremental recursion with retractions ([mcsherry2013]). This design restricts recursion to positive, monotone rules over a finite domain (Theorem 6.5) and evaluates affected recursions again on retraction.
- Timely dataflow and Naiad are the origin of the word frontier ([murray2013]). The engine's frontier shares its meaning as a partial order only for the input streams of the sources (Def. 6.6).
- Incremental maintenance of views by counting and by "delete and derive again" ([gupta1993]). Counting fails on cyclic derivations (Theorem 6.6(b)).
- The Dataflow model gives windows, watermarks and late data ([akidau2015]). Here windows close by explicit logical time, not wall-clock time (Def. 7.6).

**Graph systems over streams and time.** Raphtory analyses graphs that change over time ([steer2020]); it is about analysis, not about derivation that can be replayed. Streaming model transformations have been built on processing of complex events ([david2016]). This design differs by determinism from recorded input (Theorem 6.3) and by typed definitions kept in the graph.

**Graph schemas, ontologies and meta-models.**
- PG-Schema ([pgschema]) and PG-Keys ([pgkeys2021]) are the closest earlier work for typed property graphs and keys. This design takes the shape of declarations, abstract and closed types, and key constraints, and drops loose validation. GQL ([gql]) and Cypher ([cypher-patterns], [opencypher]) give the vocabulary of patterns. `Match` here has no paths of variable length and no imperative clauses.
- RDFS, OWL 2 and SHACL ([rdfs], [owl2], [shacl]) separate vocabulary from validation in a closed world. This design follows SHACL for reports of validation and for shapes that describe shapes, and does no inference.
- MOF and Ecore ([mof], [emf]) are the model for a top level that describes itself (Ch. 2). This design has three levels and one edge of containment.
- The usual property graph model ([angles2018]) requires both ends of an edge to be nodes. Here a graph may hold dangling relationships (Def. 3.2).
- Kubernetes custom resources ([k8s-crd]), extension of GraphQL types ([graphql]), Backstage ([backstage]) and Dynatrace Smartscape ([smartscape]) contribute types loaded while the program runs, versions, qualified references, ids from keys, and edges that hold always against edges that hold for a time. Custom kinds written as program code, conversion by outside programs and opaque ids are not taken.
- Management of ontology change ([mahfoudh2015]) and functorial migration of data ([spivak2012]) inform the classes of compatibility and the migrations of Ch. 2. Migrations here are projections, and the table of compatibility classifies structure; it is not a construction of category theory. Data exchange that invents values ([fagin2005]) is related to projections across ontologies. This design has no recursion over rows (Ch. 6), which avoids the question of whether a computation that invents identities ends.

**Bidirectional transformations.** Asymmetric lenses ([foster2007]), symmetric lenses and complements ([hofmann2011]), the constant complement ([bancilhon1981]), delta lenses ([diskin2011]), lenses for graph transformations ([hidaka2010]), bijective lenses ([miltner2018]). Triple graph grammars ([anjorin2016]), conditions for correct and complete derived translations ([hermann2013]), triple graph grammars over several versions ([barkowsky2024]), consistency of several views ([gottmann2016]). This design takes the laws as declared duties, derives backward steps from the operators, and restricts `sync` to a first fragment without negative conditions (Ch. 8). A backward function written by a user is not accepted. Graph transformation as reconfiguration of a software architecture is in [wermelinger2002].

**Provenance and patches.** Provenance semirings ([green2007]) unify support, lineage and the weights of Z-sets (Ch. 9). The theory of patches in categories ([mimram2013]) frames patches (Ch. 4); here patches are guarded and form an inverse category (Proposition 4.5). Conflict-free replicated data types ([shapiro2011]) are the contrast of a merge in any order with ordered patches (Ch. 4, 4.5).

**Time and consistency.** SQL since 2011 separates system time and application time ([kulkarni2012]); this design indexes system time only (Ch. 3). Consistent snapshots of distributed systems ([chandy1985]) motivate the aligned cut that remains open (Def. 6.6).

**Storage and scale.** Ch. 11 cites the literature on storage, partitioning and evaluation by columns ([gonzalez2012], [stanton2012], [zhu2020], [deleo2021], [boncz2005], [pelkonen2015], [mcsherry2020], [berkholz2017], [henzinger2015]) and the documentation of Azure.

**Health models.** Azure Monitor health models have components, signals with thresholds and a roll-up by the worst state. The example of Ch. 7 (7.4) follows that vocabulary as a stack of projections. The paper does not evaluate Azure Monitor itself.

**Operations assisted by language models.** Recent work on language models and agents for handling incidents is out of scope (Ch. 0). Rules written by a language model appear in no definition and no chapter.

## 15.2 Limitations and non-goals

**What the prototype does not do, and why.** The table groups limitations by research topic. Most rows describe the historical engine and viewer states evaluated in Chapter 14; the scale row includes later measurements. A dated observation is not automatically a fact about every later build.

| Gate | Not done | Why | What it needs |
|---|---|---|---|
| 1a | The image of a level checked when data arrive (Ch. 5, 5.8) | The mapping of services to capabilities is declared to have at most one image; a check of the graph reports a second image, and an edit that adds one is refused, but ingestion does not check it, so recorded data can hold two, and the capability level then leaves that service out by a join with the number of images. The service level counts a pod that backs two services for both, so conservation does not hold for it | a check of the number of images on ingested rows |
| 1a | Retention of measurements declared in the definitions (Ch. 7, Def. 7.13) | Retention follows the logical frontier, but its age is a setting of the running engine (48 hours by default), not a declaration | a declaration of retention in the language |
| 1b | Identity expressions outside what is written (Ch. 5, Def. 5.9) | An identity expression stands only in the id or the ends of an emit and in a link; elsewhere it is refused, in the text also as a group key or a join key. Grouping by an element must read it through a match on a graph, so that a merge re-keys the group; a group by the key components stayed split in a test run before the text refused it, and the group by the matched element did not (Ch. 5, E9) | nothing, unless a use needs an identity inside a computation |
| 1b | Forms of KQL outside the profile (Ch. 5, 5.9) | A form is supported when a case needs it and it lowers onto the kernel without a new operator, aggregate or kind of value. `arg_min` is the one form added this way, for a case that picks the member with the least number of each group. Estimates (`dcount`), arbitrary values (`take_any`), arrays (`make_set`, `mv-expand`, `mv-apply`), an order over rows (`take`, `sort`, `top`, `scan`), outer and anti joins, a path with a minimum above one edge or a second path in a pattern, and an element as the value of `arg_max` stay refused, each with a message that says what to write | a case that needs the form, and a lowering onto the kernel |
| 1a | Levels shown correct in general (Ch. 5, 5.8) | A level is ordinary operators in the text of its projection, so its properties are those of the operators. That the two levels implement Def. 5.17 and 5.18 is tested on variants of one estate and in composition; it is not proved for a given text and not tested on generated graphs | a test that compares a level with the definitions directly on generated graphs |
| 1a | A typing rule that keeps the existence of a window out of structure (Ch. 5, rule P6) | Theorems 5.3 and 7.1 are restated with a second channel, the supports of windowed relations, and Theorem 7.1 bounds it for key supports. A typing rule P6 would close that channel, but it would refuse six rules of the shipped definitions, among them the edges discovered from flows, so it is an optional stricter profile | nothing, unless a use needs the stricter profile |
| 1b | Rule class `sync` (Ch. 8) | The engine stores no correspondence graph and has no forward and backward translations; the other four classes cover every rule of the definitions. A `sync` rule is refused with its position. Theorem 8.7 has no evidence from the engine | the correspondence graph, the translations, and an example that needs them |
| 1b | Operator `Delta` (Ch. 5) | The increase of a running total is written without it, with a shifted copy of the rows (Ch. 5, signals over time) and tested on a synthetic counter. A restart inside a window is counted from the highest reading before it, and samples at irregular times are not covered. No source in the recordings delivers a running total | a source of running totals, and an order over rows if irregular samples must be counted exactly |
| 1c | The full million-node target of Ch. 13 (13.1) | The 19 November serial baseline reports 7.69 KB live memory per node, 38.61 µs writer CPU and 43.72 µs process CPU per record against 37 µs. Nine later closes took at most 4.531 s, but do not establish p99; the first populated close took 69.82 s. No result establishes the reference-hardware and durable-store target | CPU within budget, representative long-run latency, and runs on the reference hardware and durable stores |
| 1c | A real Cosmos account with figures of cost (Ch. 11, 11.6) | No deployment of the engine's store in Azure exists. The emulator charges 1 per request, so counts of requests are exact, and cost, times, refusals for load and the effects of a region are not measured. H3, H5 and H7 on Cosmos are open | an account in the engine's region with throughput bought in advance; the cost of each commit |
| 1c | A machine without other load (Ch. 13, 13.5) | The historical ten-run figures were measured on a shared machine. Their statuses apply to those configurations; they are not established bounds for an idle or different machine | controlled repeated runs, and runs on the hardware of Def. 13.4 for Cosmos |
| 2a | Results on the real recordings that anyone can repeat (Ch. 14) | The two recordings and their local-name overlay are not published. The reviewed checkout also lacks the capture-derived samples; it contains builders and the synthetic estate. Archived output is evidence of reported runs, not independent reproduction | publish scrubbed inputs and the pinned definitions needed to reproduce them |
| 2a | Capabilities on a real recording (Ch. 5, 5.8) | Capabilities are typed in by people. The real recordings hold none, so the capability level is empty on them and exists only on the synthetic estate | a recording with authored records |
| 2b | Complete lineage through operators with windows (Ch. 9, 9.5) | Lineage names every source for emits row by row and for a `Group` without windows. For an element from a windowed `Group`, a `Rollup`, a `Threshold` or a `Series`, and for rows reduced to plain values before the emit, it holds the elements of the group key and says it is incomplete | the sources of each window kept with the aggregate, at a cost in memory that was not measured |
| 2b | The viewer beyond its scripted checks (Ch. 12) | In its default content a document is the head only, and measurements arrive all at once after the history. The stubs and the folding on a real recording were seen in a browser and are not checked by a script; Chrome is the only browser tried, and no pass with a screen reader was made | measurements aligned in time; a scripted check on a served recording; a person outside the project reading the model |
| 2c | Resume against a real stream of Event Hubs (Ch. 10, Propositions 10.7 and 10.8) | No deployment in Azure exists, and the cluster of the recordings no longer exists (its group of resources was deleted on 2026-11-10, not by this project), so no further recording and no run against a stream is possible until a cluster exists. Resuming, and the order receive, journal, record, process, are tested against a fake source; the program that reads Event Hubs is code that no test and no run reads from, and it reads one partition | a cluster, the test environment set up again, a live run with a forced kill and a restart from the store |
| 2c | A restart after a kill during input (Ch. 13, H10) | The benchmark restarts after a clean stop. The tests simulate a kill at each step of an envelope by raising an exception on the store in memory, which is not a kill of the process and not a cut inside a journal entry | kill the process during input, restart, compare the digest |
| 2c | One hour of steady real input of the current shape (Ch. 13, H10) | The histories of 1 and 4 hours in the restart runs are the newest recording (1,017 s) repeated with shifted times. The first recording spans 5,679 s, but its flows cover 725 s, so its journal is not a stand-in for an hour of input | a recording of an hour or more from all current sources at their steady rate |
| 2c | Input from the past on a live source (Ch. 7) | A live source keeps a late horizon of three window widths to bound its state. Input with old times (the log of management actions read from the past, a query of a measurement system over a range) is dropped as late and can change derived states. A recording, processed or served, uses the long horizon | a horizon per source, or input from the past delivered as a recording before the live stream |

Not listed here, because they are listed where they arise: truncation of documents (Ch. 12), the page seal of Def. 11.8, readers that follow the cloud store's stream of changes and the splitting of hubs (Ch. 11), the database for time series (Ch. 7, 11), the history of an element and provenance semirings (Ch. 9), splitting an identity (Ch. 1), and the open items of Ch. 14 (14.10).

**Evidence, in brief.**
- The historical evaluation exercised 17 of the 18 operator kinds, stores, one lens, minimum-id merge, full-replay definition edits, evidence, documents and live-input examples. Their scopes and exclusions are in Chapter 14. The proofs were not machine-checked, and the later manual audit found additional gaps. Tests of the built features do not verify unbuilt `sync`, provenance-semiring or carry-policy designs. A seal restart does not recompute the journal prefix, by design (Proposition 11.10).
- Performance figures are one run each (ten for H5, H7, H8 and H10, under load) on one laptop, with synthetic workloads for scale and small real recordings. Three hypotheses are supported for those configurations only. H7 has two labels on the emulator and none for a real account. The figures of Ch. 11 for Cosmos and the database for time series are assumptions.
- The incremental argument imports results from [budiu2023]. Their use remains conditional; the review did not independently reprove the cited literature. The original inventory's nine named-premise labels are not an exhaustive list of conditions after the audit.

**Language and meaning** (gate 1b).
- The kernel is closed. A new operator, builtin function, kind of value or adapter needs a new release of the engine (Ch. 2, 5, 10). Ontologies, identity domains and projections do not.
- Recursion is positive, monotone and over a finite domain (Theorem 6.5, condition F5). General recursion, negation inside recursion, new identities in recursion and well-founded semantics are out of scope. `Join` is not a lens step. `sync` has no negative conditions.
- Efficient maintenance of recursive views under retraction is cited, not established (Theorem 6.6). Patterns of several steps cannot in general be maintained in constant time (Fact 11.2).
- Histograms and distributions as a kind of measurement are open. A `mean` does not decompose and must be rolled up as a weighted mean, a sum of value times weight divided by a sum of weight.
- Graphs computed on demand and changes described by a formula are not modelled (Ch. 0).

**Operation** (gate 1c and Phase 2).
- One writer per graph. Several writers of one master graph and merge in any order are out of scope (Ch. 4).
- Backward edits change data inside the engine only; nothing is written back to Azure or Kubernetes (Theorem 8.6).
- A definition edit is not computation from scratch once the history of inputs is bounded (Theorem 9.2); the engine keeps the whole log and processes all of it again (Ch. 9, 9.5). Aligned cuts are open (Def. 6.6).
- A graph space must fit in the memory of one machine (Ch. 11). The structural plane is not indexed by observed time.
- Merge of identities is built as added records with the smaller id surviving, and tested on examples (Theorem 1.7); split is not built. Stability across projections that run at the same time, and under generated orders of arrival, is open.
- Refactoring keeps outputs equal (Proposition 5.7) under six premises that no program checks, and no test exercises it. The health model follows `Affects` to depth 16 and gives no message for a longer chain (Proposition 7.8).
- The engine consumes five markers of the forwarder, tested on fixtures only. A marker of dropped events marks a window, not the graph. Evidence elements grow with the rate of events times the age of retention (24 hours in the definitions, a choice and not a result); at the audit rate of the second recording that would be about 3.6 million elements, which is arithmetic, not a measurement (Ch. 9, 9.6).

**Assumptions the paper leans on.** Collision resistance of SHA-256 (Ch. 1); the properties S1 to S5 of a store (Ch. 11); the closed language (Proposition 5.4); the recorded order of inputs as the boundary of determinism (Theorem 6.3); and coverage declarations being true (Theorem 10.3).

## 15.3 Research review and reading quality

Separate AI-assisted source reviews examined every numbered proof in Chapters 1 to 13 by informal reasoning. This was not an external human review, machine proof check, implementation certification or independent proof of the imported results. Tests and benchmark reruns preceded the text edits (14.11). The original inventory is retained as a historical record, not relabelled as a new proof certificate.

The strongest evidence is for the guarded change algebra and the tested replay paths. The main weaknesses are missing domains or premises, incomplete arguments for proposed features, unavailable real inputs, and performance claims drawn from different builds or configurations. The revised statements narrow the conclusions where justified. Remaining obligations include:

| Topic | Conditions or gaps that must not be hidden |
|---|---|
| Identity (Ch. 1) | fixed normalization domain; append-only migration; minimum-id allocation/forwarding without arbitrary aliases; no general split result |
| Schemas (Ch. 2, 3) | unambiguous effective property names and bootstrap encoding; incident-edge and hierarchy checks; conformance is separate from structural commit validity |
| Evaluation (Ch. 5–7) | valid integrated operator domains, deterministic tie rules, recorded control events, and final-state rather than log equality across batch cuts |
| Measurements (Ch. 7) | counters converted per element in time order before combination; late-sample successor effects and retention bounds need implementation evidence |
| Editable views (Ch. 8) | restricted one-key and accepted-edit domains; no pending unprocessed input for the staged guarantee; proposed translations do not certify the built lens |
| Definition edits and lineage (Ch. 9) | complete replay arguments, explicit frontiers/cuts/carry policy, and materialization conflicts; statelessness alone does not make truncated replay complete |
| Sources (Ch. 10) | remembered delivery identities, intervening replacements, truthful scoped coverage and complete ordered transport; loss is not itself a deletion signal |
| Storage and clients (Ch. 11, 12) | ordered retries, journal and publication premises, applicable guarded patches and the stated feed scope; not every key kind or failure path is proved |
| Scale (Ch. 13, 14) | later million-node data are reported, not reproduced here; CPU misses its budget and long-run p99 remains open |

**Editorial method.** Clark, Martin and Mahowald [clark2026] study language features associated with processing difficulty: unusual word combinations, noun stacks, long dependencies and omitted connecting words. Their corpus study and small training intervention do not measure comprehension of this paper, UI text or animation dialogue.

The review therefore uses those features as prompts for a source-based reading check, not automatic penalties. Entry points explain a concrete example before introducing a term; formal sections retain necessary vocabulary and explicit premises. Before/after review checks that shorter phrasing preserves claims and conditions. No reader study, listening study, authorship detector or numerical readability score is claimed.
