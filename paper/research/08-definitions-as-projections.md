# 08. Definitions as projections, statements as definitions

Status: research note, 11 November 2026, written to decide and build from. Nothing in sections 2 to 4 is built. Claims carry a status: **built** (in the engine as it stands in the working tree), **proved** (argument here), **sketch** (argument here with a named gap), **design** (a proposal with no evidence yet), **open**. Companions: [03](03-projection-language-and-metagraph.md) (kernel, catalog), [04](04-compute-and-memory-efficiency.md) (cost, section 6.7 already budgets plan size and re-activation for this note), [05](05-expressiveness-and-flexibility.md) (claims F8, F9), `paper/inventory.md` (theorem statuses).

The user's two questions, answered in one line each:
- *A projection on a projection to make a larger projection, with the ontology coming in as a graph?* Stacking data graphs works today. Reading and writing **definitions** through a projection does not: no projection can take the catalog as input or emit into it. Section 2 adds that with one port kind, one purpose and a staging rule.
- *Grammar that generates the final grammar?* Today every statement kind is code in four places. Section 3 makes a statement a catalog declaration from which parser, checker, printer, palette and editor card are derived; `lift` would have been one, while `fill`, `retain` and the row-driven `threshold` would not. Sections 3.6 to 3.9 take the follow-up: the text and the grammar are renderings of graphs, the grammar is a graph in the catalog, and a purely visual editor can author it.

## 0. Findings

1. **Half of the machinery exists and is already used by `lift`.** The catalog is a graph with the kernel as its ontology, served, diffed and edited like data (ch. 12, Proposition 12.5). `lift` is a statement whose meaning is generated text: `LiftExpander` reads the ontology (to decide the default carried properties and whether the mapping is a declared function), fills a template, parses it with the ordinary front end and splices the result (`LiftExpander.cs`, 229 lines). What is missing is a way to say this in definitions instead of C#: a catalog **port** for projections and a **statement declaration** for templates.
2. **A finite, acyclic staging keeps every theorem.** If generated definitions live in their own modules, are committed at recorded generation points, and the dependency graph of modules (generated ones included) is acyclic, then each stage is an ordinary space activation and Theorems 5.1, 5.2, 6.1 to 6.3 and 9.1 apply per stage without change. Three new statements are needed (stage well-foundedness, determinism of generation, expansion faithfulness), each short. Self-reference is excluded, not bounded.
3. **What cannot be a derived statement is exactly what needs new operator state or new knowledge of coverage.** Of the four test cases, `lift` would have been a definition (it is a template today). `retain`, the row-driven `threshold` and `fill` with its TTL would not: each needs state or knowledge that no combination of existing operators carries (frontier-driven withdrawal, per-row parameters of a state machine, the set of closed windows without a sample). The health tree's three copied signal blocks are the other gain: a generator over the type graph writes them, one per target type of `MeterOf`.
4. **The text and the grammar can be renderings, the parser cannot be a document.** The canonical text can be a document projection over the catalog, but only with a code renderer that the engine lacks (tree-structured blocks, token classes, literal quoting, layout), because `L` cannot build nested order keys or escape strings. Parsing is then the backward direction of a lens that is well-behaved on canonical texts (GetPut is Theorem 5.6). The parser itself must come from one invertible description, the grammar graph of 3.7. That graph (15 node types in `K`, plus five template constructs) is what a visual editor authors; the irreducible core is `K`, the primitive operators, the rule-class derivation, the adapters and four interpreters of the grammar graph.

---

## 1. What exists today

| Claim | Status | Evidence |
|---|---|---|
| E1. Projections stack: the output graph of one projection is a graph input of the next. The space has chains of length four (`master` → `treeStructure` → `treeRollup` → `healthTree` → `healthReport`; `master` → `healthTopology` → `healthState` → `health`; `master` → `serviceTree` → `capabilities`). | built | `engine/definitions/acme.space.sattle`; S3 of Def. 5.8 (production is a DAG) |
| E2. Definitions are a graph: graph `catalog`, ontology `sattle.kernel`, served by snapshot and patches, edited through drafts, with releases and release diffs as catalog patches. | built | `engine/PROTOCOL.md` "Definitions are graphs", "v3: drafts", "v3: releases"; ch. 12, Proposition 12.5 |
| E3. The editor surface is derived from the kernel table: `GET /palette` lists operators, slots, parameters, declarations and aggregates from `Kernel.cs` and `Support.cs`; `POST /palette/skeleton` creates a node with its kernel-fixed ports. | built | `PROTOCOL.md` "v3: palette from the kernel", "Version 3.2: palette describes declarations" |
| E4. One statement is already a template: `lift` is a kernel operator node that the planner replaces, in a copy of the graph, by generated operators; its defaults are computed by reading the ontology. | built | `engine/src/Sattle.Language/LiftExpander.cs`; ch. 5, 5.8 |
| E5. The text syntax is hand-written per statement kind: lexer and parser (`Syntax.cs`, 835 lines), lowering (`Lowering.cs`), checker (`Checker.cs`), printer (`Printer.cs`, 557 lines). Adding `lift` touched all four plus the expander. | built | the files named |
| E6. **Not built:** a projection that reads the catalog. Port kinds are `rows`, `graph` and `param` (`Checker.cs`, `Lowering.cs`); a space binding names graphs of the space, and `catalog` is not one of them. | built (absence) | the files named |
| E7. **Not built:** a projection that emits definitions. Emits write into the projection's output graph under its output ontology; no output ontology is `sattle.kernel`, and the catalog is written only by `POST /definitions/{module}` and drafts. | built (absence) | `PROTOCOL.md` "Checking and editing definitions" |
| E8. **Not built:** statement declarations. A new statement kind is an engine change (05, F8). | built (absence) | 05 section 3 |

---

## 2. Projections over definitions

### 2.1 The smallest closure

Three additions, nothing else:

| Addition | Kernel change | Meaning |
|---|---|---|
| A1. `catalog` port kind | `Port.kind` gains `catalog`; a catalog port has an `expects` edge to a module | `in defs: catalog acme.ops;` reads the definition facts of the closure of `acme.ops` (the module and its imports) at the release pinned by this activation, as an ordinary graph with ontology `sattle.kernel`. `Match`, `Traverse`, `Join` work on it unchanged. |
| A2. `generation` purpose | `Projection.purpose` gains `generation`; its `outputOntology` must be `sattle.kernel` | The projection's emits produce definition facts. They are not committed to a data graph. |
| A3. generated module | a `Module` property `generatedBy` (edge to the projection) | The facts emitted by a generation projection form one module, named in the space binding (`module gen.estate = acme.healthgen.signals(defs: ops);`). Generated modules are read-only in the catalog: an edit goes to the generator's inputs. |

Why a module and not a patch into existing modules: a module is the unit of release, digest, import pin, check and activation (ch. 2, Theorem 2.4; ch. 9, Def. 9.1). Writing into a hand-written module would make its digest depend on data and would mix authored and derived definitions in one release.

### 2.2 Semantics: stages

**Definition (stage).** Let `M` be the modules of a space, `G ⊆ M` the generated ones, each produced by one generation projection `p(g)`. The **dependency graph** has an edge `m → m'` when `m` imports `m'`, when a projection in `m` reads a graph produced by a projection in `m'`, when a catalog port in `m` expects `m'` (closure included), and `g → module(p(g))` for every generated `g`. The space is **stageable** iff this graph is acyclic. The **stage** of a module is the length of the longest path from it to a hand-written module with no generated dependency; stage 0 is the hand-written closure.

**Claim S1 (well-founded staging).** A stageable space has finitely many stages, at most `|G| + 1`, and activating the stages in increasing order activates every module after everything it depends on. Self-reference (a generator that reads, directly or through data, a module it produces) is a cycle and is rejected at activation with the cycle named.
Status: **proved**. A finite acyclic graph has a topological order; the stage is its longest-path layering; a generator reading its own product closes the cycle `g → module(p(g)) → … → g`.

This is the line drawn against reflective towers, where an interpreter can be interpreted by the next one without a fixed top ([Amin and Rompf], "Collapsing towers of interpreters", 2018: a tower is "a sequence of multiple interpreters interpreting one another"). Sattle's tower has a fixed height per space, the length of the longest generator chain, and the kernel is its compiled top (03, section 2.8).

**Definition (generation point).** A generated module is recomputed only at a **generation point**: a recorded input of the generator's projection (like `Advance(λ)`), written by the engine when (a) a catalog module in the generator's closure is committed, or (b) an authored trigger record arrives, or (c) for a generator with data-graph inputs, at a declared cadence of logical time (`regenerate every 1h`). At a generation point the generator's current output is printed canonically, digested, and if the digest differs from the active release, committed as a new release of the generated module and the dependent stages are re-activated (a configuration edit, ch. 9).

**Claim S2 (determinism of generation).** The release of a generated module at a generation point is a function of the pinned definitions of its generator and the recorded input sequence up to that point. Consequently the whole log of a stageable space, generated releases included, is a function of the hand-written definitions and the recorded inputs.
Status: **sketch**. The generator is a projection, so its output graph is a function of its recorded inputs and pinned definitions (Theorem 6.3); the canonical printer makes the module text a function of that graph (Theorem 5.6, under premise RT); the release digest is a function of the text (Theorem 2.4); generation points are recorded inputs, so they are at the same positions in every replay. The induction over stages uses Theorem 9.1 (edit replay) for each re-activation. The gap: Theorem 5.6 is proved under RT on canonical graphs, and a generator can emit a definition graph that is not in the image of `parse` (an unprintable graph, `Unprintable` in the engine today); such a graph must be rejected at the generation point, which makes it a runtime diagnostic, not a typing guarantee.

**Claim S3 (batch invariance across regeneration).** Chunking of the input does not change the generated releases nor the outputs of the generated projections.
Status: **sketch**. Per stage it is Theorem 6.2. Across stages it needs that a generation point never falls inside a chunk boundary differently in two runs: it is a recorded input with its own sequence number, so chunking moves it with the records around it, and premise B4 of Theorem 6.2 (a step is its inputs in order) applies to it as to a logical-time advance. Not argued for generation points of kind (c) if they are derived from the wall clock: they must be logical time.

### 2.3 Typing: what is checked once, what per generated module

| When | What | Status |
|---|---|---|
| Once, for the generator | It is an ordinary projection: all judgments of ch. 5 against the kernel ontology (its catalog port reads `sattle.kernel` facts, its emits write them). Its emits must create definition facts with definition ids (a kernel identity domain `definition(module, path)` whose allocation is the path, an engine change, section 5), and every create emit of a node with a required slot must be accompanied by a create of its container edge (P5 against the kernel's K1, K2). | design |
| Per generated release, at its generation point | The full module check: K1 to K11, closure, all projection judgments, pins, digest. A failure leaves the previous release active and reports diagnostics whose focus is the generated element, with lineage back to the generator rows that produced it (ch. 9, lineage). | design |
| Never statically | That every generated release will check. The generator's output depends on catalog data and possibly on data graphs; whether the result type-checks is in general undecidable (05, C5: satisfiability of the calculus fragment). | proved by 05 C5 |

The output ontology of a generated projection is known before it is checked because stages are ordered (S1): if a generator also generates the output ontology module, that module is in an earlier or the same stage and is checked first within the stage (module order inside a stage is the import order, ch. 2, Theorem 2.3).

This is the Template Haskell position: generated code is "always type-checked before" it runs, and the generating program is typed separately ([Sheard and Peyton Jones], Template Meta-programming for Haskell, 2002, section 7). The stronger position, that well-typed generators produce well-typed code, is the modal typing of staged computation ([Davies and Pfenning], A Modal Analysis of Staged Computation). It would need a type of definitions with holes that the kernel does not have; section 3.4 adopts it only for derived statements, where holes are typed slots.

### 2.4 Revisions, releases, lineage

- A generated module has releases, digests and diffs like any module (`GET /releases?module=gen.estate`, `GET /releases/diff`). Its `generatedBy` edge and the generation point's sequence number are part of the release record.
- Lineage of a generated definition element (`GET /graphs/catalog/lineage`) names the generator rows that emitted it: for a generated `Signal` block, the `MeterOf` target type node of the catalog it was generated for.
- Downstream outputs pin the generated release in their frontier `cfg` (Def. 9.1), so Theorem 9.6 (replay of history under pinned definitions) holds for them unchanged.

### 2.5 Composition of projections as a generator

"A projection on a projection to create a larger projection" also reads as: fuse two stacked projections into one. That is a catalog-to-catalog transformation, so it is a generator, or the `inline` refactoring (ch. 5, Def. 5.12) generalized from "undo an extract" to "substitute the upper projection's graph port by the lower projection's sinks". **Claim S4.** Fusion is sound for an upper projection whose graph port is read only by `Match` patterns over types the lower projection creates with row-level emits, and not sound in general (a `Traverse` or `Series` over the port reads committed graph state, including elements created by other projections). Status: **sketch**; not proposed for the first stages.

### 2.6 Related notions

| Notion | Source | What Sattle takes | What it does not |
|---|---|---|---|
| Staged computation, modal types | [Davies and Pfenning] | stages as an explicit order; code of a later stage is data of an earlier one | a type of open definitions (holes) for whole generators |
| Compile-time metaprogramming with reification | [Sheard and Peyton Jones] | reification (`reifyDecl`: the generator inspects user-written declarations) is the catalog port; splices are checked after expansion | splices anywhere in a module; generated code mixed into hand-written modules |
| Reflective towers | [Amin and Rompf] | the kernel as the fixed top | unbounded towers; levels that reinterpret the kernel |
| Macros, hygiene | [Flatt], Binding as Sets of Scopes; [Tobin-Hochstadt et al.], Languages as Libraries | derived statements as macros, hygiene by scope sets, languages assembled from declarations | arbitrary procedural macro code (Sattle templates are definition graphs, not programs) |
| Language workbenches | [Kats and Visser], Spoofax | parser, printer and editor services generated from one declarative specification | a full grammar formalism; derived statements use one fixed clause shape |
| Schema as data | MOF reflection and SHACL shapes graphs (03, section 1) | definitions queried with the same language as data | changing the kernel (the meta level stays compiled) |

Higher-order Datalog (HiLog), SchemaSQL and MetaML are the usual further references; none of them could be opened (403 or 404), so none is cited.

---

## 3. Syntax from definitions: derived statements, the grammar as a graph, visual authoring

### 3.1 The declaration

A **statement declaration** is a catalog subgraph in a module:

| Kernel node | Properties and slots | Role |
|---|---|---|
| `StatementDecl` | `name` (the keyword), slot `param` (ordered `ParamDecl`), slot `expansion` (one `Template`) | the statement kind |
| `ParamDecl` | `name`, `sort` ∈ {graphPort, rowsInput, entityType, relationshipType, property, expression, fieldPath, name, literal, duration, enum}, `required`, `repeated`, `default`; edges for constraints: `ofGraph` (the param that gives the ontology a type param is resolved in), `roleOf` (telemetry or data), `values` (an enum) | one clause or argument |
| `Template` | a fragment of a projection: operators, wires, sinks, as ordinary kernel nodes, plus `Hole` nodes (a reference to a param), `Fresh` names (generated, scoped), `When` (a condition on params, keeps or drops a sub-fragment), `Each` (repeats a sub-fragment per value of a repeated param) and `Query` (a value computed by matching the catalog at expansion, for example "the telemetry properties that two relationship types share by name and kind") | the meaning |
| `Requires` | a check on params before expansion, as a message and an expression over params and `Query` results | errors in the statement's own terms |

A use of a derived statement is a `StatementUse` node in a projection with one `ParamValue` child per param.

### 3.2 What is generated from it

| Artifact | Today | From the declaration |
|---|---|---|
| Surface syntax | hand-written per statement | one clause shape for all derived statements: `KEYWORD NAME (param: value, …);` with the value syntax given by each param's sort (a type reference, a graph port name, an expression, a duration). A `lift` use: `lift dependsOn(as: DependsOn, edges: master.infra.Calls, via: master.infra.Backs, keep: [cluster = src.cluster], …);` |
| Parser | `Syntax.cs` | one generic clause parser for derived statements; primitives keep the hand-written parser |
| Checker | hand-written rules | param sorts and constraints checked against the closure (an `entityType` param in the ontology of its `ofGraph` param, a `property` with `roleOf telemetry`), then each `Requires`, then the expansion is checked like any operators |
| Printer | `Printer.cs` | the clause shape, printing only params that differ from their defaults (as the `lift` printer does) |
| Palette entry and editor card | kernel table plus `Support.cs` | one entry per `StatementDecl` with its params, sorts, defaults and enums (the shape `GET /palette` already uses for operators) |
| Plan listing (`check --plan`) | written for `lift` | from expansion provenance |

**Claim D1 (faithful expansion).** The semantics of a use is the semantics of its expansion. Every theorem that holds for the operators of the expansion holds for the use, no more: Theorems 5.2, 5.3, 6.1, 6.2, 6.3, Proposition 5.9, lineage (Theorem 9.7). Status: **proved** for the same reason as for `lift` (ch. 5, 5.8: after planning nothing else is there). What is not implied: that the declaration means what its author intended. That is tested per declaration, as `LiftTests` test `lift` against hand-written modules.

### 3.3 Errors, hygiene, round trip

- **Errors in the statement's terms.** A diagnostic of the expansion carries the generated element; the expansion records a `fromParam` provenance edge from each generated node to the `ParamValue` (or the `Template` node) it came from, so the message is reported at the use and the clause (`lift dependsOn, clause keep: cluster is not a data property of Service`). `Requires` checks run first, so the common errors never reach the expansion. Today `lift` prefixes the message (`lift NAME: …`) and focuses the `Lift` node; positions inside the clause are lost. Status: **design**.
- **Hygiene.** Names a template introduces (`Fresh`) are unique internal names derived from the use's node id, never from text the user typed; names the user passes (a `name` param, a field path) keep the user's spelling; a free name in a template resolves in the declaration's module, never at the use site. What a reader sees (diagnostics, the names a later statement may reference) comes from the provenance edges, which gives the effect of hygiene by scope sets ([Flatt]) without making the checker's name lookup scope-aware (engine-core's estimate: that would touch most of `Checker.cs`). Today `lift` uses a text prefix (`NAME__edges`), so a user `let dependsOn__edges` in the same projection collides with a generated one. Status: **design**; the collision is a reading of `LiftExpander.cs`, not a test.
- **Round trip.** The catalog keeps the `StatementUse` node and its `ParamValue`s, never the expansion (as for `Lift` today); the printer prints the use; `parse(print(G)) = G` holds for uses under the same premise RT as Theorem 5.6, with the clause shape as the only new production. Status: **sketch**.

### 3.4 What stays primitive, and why

**Claim D2.** A statement can be derived iff its meaning is a fixed plan of existing operators, possibly chosen by its params and the catalog (conditionals, repetition, catalog queries). It must be primitive if it needs (a) operator state that no existing operator keeps, (b) knowledge of coverage, that is of what did not happen (a closed window without a sample), or (c) a new evaluation rule (unbounded recursion, a new function). Status: **proved** in one direction (a fixed plan of existing operators has their semantics by D1), the converse is the definition of "primitive" used here.

The four test cases:

| Statement | Derivable? | Why | What the declaration would need |
|---|---|---|---|
| `lift` | **yes** | it is already a template; every check in `LiftExpander` is a param constraint (a relationship type of the edge graph, a telemetry property of the lifted type, kinds equal) or a catalog query (the mapping is declared a function by a `cardinality` constraint; the default carry list is the telemetry properties both types share) | `When` for `images reject`/`each` and `selfloops`, `Each` for `carry`, `keep`, `derive`, `state`, and two `Query`s |
| `retain` | **no** | rows are withdrawn when the logical frontier passes `time + age`: state ordered by time and driven by `Advance`, which no other operator keeps | would become derivable only if the frontier were a relation (a one-row input that changes at each advance): then `retain` is a `Join` with it and a `Filter`, at `O(held rows)` per advance instead of `O(expired rows)` (04, section 5); the primitive is the cheaper choice |
| `threshold` with row-valued parameters (the health tree's signal rules) | **no** for the parameter semantics, **yes** for the rule matching around it | the state machine reads `above`, `below`, `enterAfter`, `exitAfter` from the latest row of each key; a `Threshold` with constant parameters cannot express per-key parameters that change over time (a new form of operator state, turn 14) | the three copied blocks of `treeSignals` (join meters of one element kind with the rules, series, threshold, readings) are one derived statement or one generated block per kind (section 4) |
| `fill` and the "no data" TTL on `Series` and `Threshold` | **no** | both need to know which closed windows of a key had no sample: knowledge of coverage, kept by the window store, not by any row | primitive; once built, a derived statement can wrap it (for example "signal with Unknown after N empty windows") |

What derived statements would have saved: `lift` cost a kernel type, a parser production, a printer case, palette and support entries and a 229-line expander (E4, E5); as a declaration it would be one catalog module, with the same tests.

### 3.5 Grammar of the declarations (core)

```ebnf
statement_decl = "statement" , ident , "(" , param_decl , { "," , param_decl } , ")" ,
                 { "requires" , expr , "else" , string , ";" } , "expands" , "{" , template , "}" ;
param_decl     = ident , [ "?" ] , [ "*" ] , ":" , sort , [ "=" , literal ] ;
sort           = "graph" | "rows" | "entity" , "in" , ident | "relationship" , "in" , ident
               | "property" , "of" , ident , [ "telemetry" | "data" ] | "expr" , [ "over" , ident ]
               | "path" | "name" | "duration" | "enum" , "[" , ident , { "," , ident } , "]"
               | "text" | "integer" | "decimal" ;
template       = { stmt | "when" , expr , "{" , template , "}" | "each" , ident , "{" , template , "}" } ;
use            = ident , ident , "(" , [ arg , { "," , arg } ] , ")" , ";" ;
arg            = ident , ":" , ( value | "[" , value , { "," , value } , "]" ) ;
```

Inside a template, `$p` is a hole (the value of param `p`), `@x` a fresh name, and `query(…)` a catalog query that returns a list of names (the same `match` syntax, run against the catalog port the expansion receives). A sketch of `lift` as a declaration, abbreviated to the parts that differ from the operators it expands to:

```text
statement lift(name: name, as: relationship in out, edges: graph, edgeType: relationship in edges,
               via: graph, mapType: relationship in via, when?: expr over image,
               carry*: property of as telemetry, keep*: path, derive*: expr, state*: expr,
               images?: enum [reject, each], selfloops: enum [drop, keep] = drop, within: duration = 24h)
  requires images != absent or query(cardinality $mapType per source 0 .. 1) else "the mapping is not declared a function and no images policy is stated";
expands {
  let @edges = match $edges (source: $edgeType.source)-[edge: $edgeType]->(target: $edgeType.target);
  let @images = match $via (node: $mapType.source)-[:$mapType]->(image: $mapType.target);
  when $when != absent { let @images = @images |> where $when; }
  when $images == reject { let @counted = … ; let @single = … ; }
  …
  emit relationship $as from @lifted key $name: sourceImage -> targetImage;
  each c in $carry ++ query(shared telemetry properties of $as and $edgeType) { … rollup … }
  each s in $state { let @state = $name |> threshold … ; emit update relationship $as … ; }
}
```

---

### 3.6 Text as a rendering of the definition graph

The user's question: can the grammar and the text be rendered from a graph "with a document renderer"? Yes for the text, with a different renderer than the one that exists. And parsing then is the backward direction of a lens, with the laws stated below.

**What the document renderer offers today.** A document projection emits `sattle.doc` `Block` entities; one compiled renderer (`engine/src/Sattle.Runtime/Documents.cs`) writes Markdown. Blocks are **flat**: they are sorted by a text `order` key with ties broken by id. Their kinds are heading, paragraph, item, stat, header and row. Data text is **escaped as Markdown**: punctuation gets a backslash and line breaks become spaces. There is no nesting, no notion of a token and no layout.

**Claim T1 (canonical text as a document projection).** The canonical text of a module can be computed by a projection over the catalog into a code ontology `sattle.code`, rendered by a compiled code renderer, if and only if that renderer offers R-a to R-e. The projection computes everything else.
Status: **design**. The "only if" is shown per requirement.

| Requirement on the renderer | Why the projection cannot do it |
|---|---|
| R-a **Tree, not a global order.** A `Node` has a `childOf` edge to its parent and an integer `rank` among its siblings; the renderer walks the tree in pre-order. | A global order key for nested text is the path of sibling ranks from the root. Building it needs a string aggregate ordered along ancestors, which `L` does not have (05, section 4, the key limit of the example). |
| R-b **Tokens with classes.** A `Token` has a class (keyword, identifier, punctuation, literal, doc comment) and its text; the renderer applies a spacing table by class pair (none before `,`, `;`, `)`, one space between words). | Spacing is a function of neighbouring tokens across nodes, which would need ordered joins over the pre-order. |
| R-c **Quoting by literal kind.** String literals get canonical escaping (JCS, as `Printer.Quote` does today); decimals and durations get their canonical text (`24h`). | `L` has no string replace or escape function (the function set of `Kernel.cs`). |
| R-d **Layout by node kind.** A `line` node ends with a newline, a `block` node indents its children one level, an `inline` node does neither. | Indentation depends on depth in the tree (R-a). |
| R-e **No markup escaping, and a lexical check.** Code text is written verbatim; an identifier the lexer would not accept is an `Unprintable` diagnostic and not escaped text. | The Markdown escaping of today's renderer would corrupt code. |

What the projection computes, all in fragment C (joins, maps and filters, one hop at a time, 04 section 5):
- which elements print, and their keywords;
- the sibling `rank`, from the slot order of the kernel table (data rows) and the `ordinal` of the `contains` edge;
- omission of defaults, by a join with the declared defaults (which must then be catalog data: `ParamDecl.default`, not `LiftDefaults` in C#);
- parentheses. An expression child is wrapped iff its precedence is below the minimum its position in the parent requires. That is a one-hop join of each `Call` with its parent `Call` and a precedence table given as `values` rows.

**Claim T2 (parsing is the backward direction of a lens).** Let `get = render ∘ project : CanonGraph → Text` and `put(t, g) = parse(t)`, where `g` supplies only the context (imports, pins, the closure for name binding). Then:
- **GetPut**: `put(get(g), g) = g` for every canonical graph. This is Theorem 5.6 (`parse(print(G)) = G`) under premise RT.
- **PutGet**: `get(put(t, g)) = fmt(t)`. So PutGet holds exactly on canonical texts, the image of `get`. On any other text the lens is well-behaved only up to `fmt`.
- **PutPut**: holds, because `put` reads nothing from `g` but its context.

So it is a well-behaved lens ([Foster et al.], the laws of 03, section 1) on the domain of canonical texts.

Status: **proved** from Theorem 5.6 for GetPut and PutPut. PutGet is restricted as stated. Theorem 5.6 itself rests on RT, which the code review found violated for escaped text and for a negative literal after `above`.

**What the lens loses, and how a retentive lens would keep it.** `//` comments and layout are not in the graph, so `put ∘ get` drops them; `///` comments are graph data (`description`) and survive. A **retentive lens** ([Zhu et al.], Retentive Lenses) keeps them. Its `get` also returns links between regions of the text and elements of the graph. It owes three laws:
- **hippocraticness**: `put` of an unchanged view returns the source;
- **correctness**: `get ∘ put` gives back the view;
- **retentiveness**: the regions that the links name survive.

For Sattle the regions are trivia (comments and blank lines) attached to the element they precede. They would be kept in a non-semantic layer of the catalog that is excluded from release digests.
Status: **design**.

**Where the parser comes from.** Sattle's own lens rules invert operators over graph edits; they cannot invert a renderer into a parser. The parser and the printer must instead come from one description that relates abstract to concrete syntax. This is the approach of invertible syntax descriptions ([Rendel and Ostermann], Invertible Syntax Descriptions: "a relation between abstract and concrete syntax", built from partial isomorphisms). In Sattle that description is the grammar graph of 3.7, and the code renderer of T1 is its printing direction.

| | Hand-written printer and parser (today) | Rendered from the grammar graph |
|---|---|---|
| Consistency of the two directions | by tests; the review found two round-trip bugs | by construction, if the description is invertible ([Rendel and Ostermann]) |
| Adding a statement | four files of C# (E5) | a catalog declaration |
| The text as a served artifact | `--print` | a document with a digest per revision, diffable like any document (ch. 12, Proposition 12.11) |
| Speed | direct | a projection plus a renderer; the catalog is small (885 nodes in the canvas measurement, ch. 12) |
| Error positions in parse errors | hand-tuned messages | as good as the generated parser's error recovery, which is a known weakness of generated parsers |
| Comments and layout | `//` dropped | `//` dropped, or kept by the retentive variant |

### 3.7 The grammar itself as a graph: the meta-level ontology

**Claim G-1.** One more group of kernel types is enough to hold the grammar as catalog data, so that the textual grammar, the parser, the printer, the palette and the editor cards are renderings of it. This extends the declaration of 3.1 from derived statements to every statement kind, primitives included.
Status: **design**. This is the ontology the visual-editor session asked for.

Node types (each `contains`-owned, ids by path, 03 section 2.2):

| Type | Properties | Slots and edges | Role |
|---|---|---|---|
| `StatementDecl` | `keyword: T`, `category` ∈ {declaration, statement, step, clause, expression}, `primitive: B` | slot `shape` (one production), slot `param` (`ParamDecl`*, ordered), slot `requires` (`Requires`*), slot `expansion` (`Template`?, only when not primitive), slot `card` (`Card`?); edge `realizes` → `EntityType` (the kernel type a use becomes: `Window`, `Lift`, or `StatementUse` for a derived statement) | one statement kind |
| `ParamDecl` | `name: T`, `sort` ∈ {graphPort, rowsInput, entityType, relationshipType, property, expression, fieldPath, name, literal, duration, enum, statementList}, `required: B`, `repeated: B`, `default: Any?`, `values: L<T>?`, `role` ∈ {data, telemetry}? | edges `ofGraph` → `ParamDecl` (the graph whose ontology resolves a type param), `typedBy` → `ValueKind`, `fills` → `PropertyType` or `fillsEdge` → `RelationshipType` (for a primitive: which kernel property or edge of the realized node the param sets) | one argument or clause |
| `Seq` | | slot `item` (productions, ordered) | concatenation |
| `Choice` | | slot `alt` (productions, ordered: first match wins, as in a PEG) | alternatives |
| `Opt` | | slot `body` | optional part |
| `Rep` | `min: I`, `separator: T?` | slot `body` | repetition |
| `Tok` | `text: T`, `class` ∈ {keyword, punct} | | a fixed token |
| `Hole` | | edge `of` → `ParamDecl` | where a param's value is written |
| `Sub` | | edge `of` → `StatementDecl` or a category | a nested statement or expression |
| `Infix` | `precedence: I`, `assoc` ∈ {left, right, none}, `text: T` | edge `function` → `Function` | an expression operator (`==`, `and`, `++`) |
| `Template` | | slot `node` (any kernel operator, port and wire nodes of a projection fragment), plus `Fresh` {`name`}, `When` (slot `cond`, slot `body`), `Each` {`var`} with edge `over` → `ParamDecl` (slot `body`), `Query` (a `Match` fragment run on the catalog at expansion), `Ref` → `ParamDecl` where a property or edge of a template node comes from a param | the meaning of a derived statement |
| `Requires` | `message: T` | slot `cond` (an expression over params and queries) | a check in the statement's own terms |
| `Card` | `icon: T`, `color: T`, `summary: T` (a text template over params), `layout` ∈ {block, node, inline} | | editor presentation |
| `StatementUse` | `name: T` | edge `of` → `StatementDecl`; slot `arg` (`ParamValue`*) | a use in a projection |
| `ParamValue` | `value: Any?` | edge `param` → `ParamDecl`; edge `ref` → a catalog node, or slot `expr` | one argument of a use |

New edge types: `realizes`, `ofGraph`, `typedBy`, `fills`, `fillsEdge`, `of`, `over`, `ref`, `param`. `contains` and `function` already exist. The productions are an EBNF grammar written as a graph: the grammar of 03, section 7.3, has a production graph by construction, rule for rule.

**The three renderers of the user's pipeline** (grammar graph → renderer → projection language):

| Rendering | Input | Output | Built as |
|---|---|---|---|
| (a) textual grammar and parser | `StatementDecl` shapes and `Infix` | the EBNF reference (a document projection over the grammar graph, Markdown), and a parser: recursive descent over `Seq`, `Choice`, `Opt`, `Rep`, precedence climbing over `Infix` | the document is a projection; the parser generator is compiled core |
| (b) palette and editor cards | `StatementDecl`, `ParamDecl`, `Card`, the kernel port signatures of `realizes` targets or the boundary of a `Template` | one palette entry per statement kind; one card per entry with a widget per param sort (a type picker filtered by the ontology of `ofGraph`, a property picker filtered by `role`, an expression editor over the inferred row, an enum list, a duration with unit) | a projection over the grammar graph into a served `palette` graph. This replaces today's C# derivation from `Kernel.cs` and `Support.cs`, which already avoids hard-coding in the client (E3) |
| (c) checker rules | `ParamDecl` sorts, `Requires`, `Template` | binding and presence checks per param, then `Requires`, then the ordinary check of the expansion | sorts and `Requires` are interpreted by compiled core. The typing judgments of the primitive operators (row types, roles, temporality, ch. 5) stay compiled; the grammar graph does not describe them |

"Then use the result as projection itself" holds at the next catalog commit. A new `StatementDecl` is parsed, printed, offered in the palette and checked by the same commit that adds it, with no engine release. A projection may use it in the same space once the declaring module is imported.

### 3.8 The fixed point: what is irreducible

**Claim G-2 (irreducible core).** The smallest hand-built part is the following:
1. the kernel meta-ontology `K`, now with the grammar types of 3.7, together with its self-description (Theorem 2.2) and the generic conformance checker;
2. value kinds, canonical equality, builtin functions, canonicalizers and aggregates;
3. the primitive operators, each with evaluation, incremental strategy and typing judgment: `Match`, `Filter`, `Map`, `Join`, `Union`, `Group`, `Rollup`, `Window`, `Series`, `Threshold`, `Retain`, `Values`, `Traverse`, `EmitEntity`, `EmitRelationship`, `Link`, and later `Delta` and `fill`;
4. the derivation of the rule classes (`bijective`, `lens`) and the runtime law check (ch. 8);
5. four interpreters of the grammar graph: the parser generator, the code renderer of T1, the palette renderer, and template expansion;
6. the adapters.

Status: **design**. The list is minimal in the sense of 3.4: each item needs state, an evaluation rule or knowledge that no template has.

**The bottom of the grammar.** The grammar graph of the grammar's own textual form (`statement … expands { … }`, 3.5) is one more `StatementDecl` in `K`'s compiled table, encoded at start-up like the rest of `K` (03, section 2.8). Bootstrap is the check `parse(print(Ggrammar)) = Ggrammar` at start-up, which is Theorem 2.2's fixed point extended by one rendering. The visual route needs no text bootstrap at all: a grammar graph is created by catalog patches (drafts), validated by conformance against `K`, and has text only as a rendering.

**Claim G-3 (everything else is definable on top).** The syntax of today's whole language, and the meaning of three of its constructs, can be catalog data over the core.
- **Syntax:** every production of 03 section 7.3 and of the engine's grammar is an EBNF rule, so it has a production graph. Its realization is the kernel type that `Lowering.cs` builds today; module, import, entity, relationship, extend, constraint, identity, equivalence, rows, projection, rule, space, `let` pipelines, steps, emits, edit policies, expressions with precedence.
- **Meaning, derivable today:**
  - `Lift`: a template (3.4).
  - `Fixpoint`: the bounded unrolling is literally an `Each` over `1 .. max` of the step plus a `Distinct` and a `Union` per round (`Unroll` in `Checker.cs`).
  - `Distinct`: a `Group` by all columns followed by a `Map`, in the same cost class C+ (04, section 5).
- **Meaning, derivable but kept primitive:** `Traverse` equals a `Fixpoint` with a `Group` by `min` depth (shortest distance, as the paper ruling on traversal depth fixes it). As a template it would lose fragment C-tree (04, section 5) and cost a factor of the depth bound in state.
- **Not definable:** items 2 to 6 of G-2.

Status: **sketch**. The syntax part follows from the grammar being context-free EBNF with one lookahead rule (identity expression against call, by named arguments), which a PEG choice expresses. The engine's grammar has grown since 03 (`lift`, `threshold` with row parameters, `treerole`, `hierarchy`); that each addition is EBNF is a reading of `Syntax.cs`, not a check.

### 3.9 What purely visual authoring needs from an editor

The target for the session that prototypes visual editors on the canvas. Authoring happens at two levels: **projections** (operators, wires and uses of statements) and **grammar graphs** (statement declarations: shapes, params, templates).

**Operations.**

| Operation | Level | Maps to |
|---|---|---|
| place a node from the palette | both | `POST /palette/skeleton` (exists): the node with its kernel-fixed ports |
| connect a producing port to a consuming port; for grammars, a `Hole` to a `ParamDecl`, a `Sub` to a statement | both | a `wire`, or an `of` edge |
| set a param or property through the widget of its sort | both | a property or edge patch on the draft |
| **make statement**: select a subgraph of a projection; boundary wires in become `graphPort` or `rowsInput` params, literals the author marks become `literal` params, inner names become `Fresh`. The result is a `StatementDecl` with a `Template`, and the selection is replaced by a `StatementUse` | projection to grammar | the visual analogue of `extract` (ch. 5, Def. 5.12); its inverse is "expand use" |
| expand and collapse a use, showing the expansion read-only, coloured by provenance (3.3) | projection | the `expandedFrom` edges |
| arrange the shape of a statement: order `Seq` items, choose tokens and keywords | grammar | patches on `Seq`, `Tok`, `Hole` |
| draft, preview on data, commit, release diff | both | exists (protocol v3) |

**Visible at once.**
- the graph being edited;
- the selected node's params or slots;
- the inferred row type on every wire (columns, kinds, roles);
- diagnostics on the node and on the param they concern.

Additionally, when editing a statement:
- its template and param list;
- one sample use, rendered both as text (T1) and as its card;
- that sample's expansion.

When looking at a generated module, additionally:
- the module, read-only;
- the generator rows that produced the selected element.

**Checked while editing.** Every draft patch goes through the existing draft check. To that are added:
- port kinds and row types on connect: `commands.canWire` in the client today, again by the engine;
- K1 to K11 on the catalog patch;
- param sorts against the closure, and `Requires`;
- re-checking every use of a changed statement, together with the uses that depend on it;
- acyclic wires (W4) and acyclic stages (S1);
- the round trip: the rendered text of the draft parses back to the same graph, so each patch checks Theorem 5.6;
- a bounded preview on data.

**Against existing visual systems.**

| System | What it has | What Sattle takes or differs in |
|---|---|---|
| Node-RED ([Node-RED docs]) | node types registered in code (`RED.nodes.registerType`) with category, defaults with `required` and `validate`, input and output counts, and a hand-written HTML edit template per type | Sattle derives the card from the grammar graph instead of an HTML template per type |
| Blockly ([Blockly docs]) | blocks defined in JSON (`message0`, `args0` with input `check` types, `output` type) and one code generator per block and target language | the closest analogue of `StatementDecl` (typed params) plus `Template` (generator). Blockly's generators are JavaScript; Sattle's templates are definition graphs, so they can be checked, versioned and diffed |
| JetBrains MPS ([MPS docs]) | a structure aspect of concepts with properties, references and children with cardinalities, and one editor per concept built from cells; "you're effectively building the AST directly" | the catalog is the AST and the card is the cell editor; the text is one more editor of the same graph |

Scratch was the fourth reference asked for. Its paper could not be opened (404), so it is not cited.

---

## 4. The user's example: an estate health model generated from the type graph

Today the health tree is hand-written per element kind: `treeSignals` (`engine/definitions/acme.tree.sattle`) has three blocks of five statements that differ only in the element type (`azure.AzureResource`, `infra.Node`, `infra.Pod`), its key expression and a kind name; `treeStructure` has one block per level. A new element kind with meters (a `Deployment`, a `Service`) is three more hand edits.

**What generation buys.** The type graph already says which kinds can have health: `MeterOf (Meter) -> (azure.AzureResource | infra.Node | infra.Pod)` in `acme.ops` lists them as its target types, and each type has an identity domain that gives its key. A generator reads these facts from the catalog and writes the per-kind blocks; a fourth target type added to `MeterOf` adds a fourth block at the next generation point, with no edit to any projection.

The definitions as they would look (**design**: none of this parses today):

```text
/// Derived statement: the signals of one element kind, from its meters and the authored rules.
module acme.healthlib @ 1.0.0;
import acme.ops @ 1.0.0 sha256:… as ops;
import acme.tree @ 1.0.0 sha256:… as tree;

statement signals(name: name, master: graph, kind: entity in master, key: expr over e, kindName: text,
                  rules: rows, within: duration = 24h)
  requires query(relationship ops.MeterOf to $kind) else "$kind has no meters (it is not a target of MeterOf)";
expands {
  let @meters = match $master (m: ops.Meter)-[:ops.MeterOf]->(e: $kind)
    |> select { m: m, ekey: $key, etype: $kindName, meter: m.meter };
  let @ruled = join @meters, $rules on meter == rmeter |> where etype == rtype or rtype == "*";
  let @windows = @ruled |> series $master m.reading within $within
    |> select { ekey: ekey, rid: rid, severity: severity, meter: meter, invert: sign < 0.0, windowStart: windowStart,
                raw: reading, v: reading * sign, above: above, enterAfter: enterAfter, below: below, exitAfter: exitAfter };
  let $name = @windows |> threshold v by { ekey: ekey, rid: rid, severity: severity }
    above above for enterAfter then "on" below below for exitAfter then "off" initial "off" as state;
  emit entity tree.Signal from $name identity tree.signal(element: ekey, rule: rid)
    set { signalElement: ekey, signalRule: rid, signalSeverity: severity, signalState: state };
  emit update entity tree.Signal from @windows identity tree.signal(element: ekey, rule: rid)
    set { signalValue: raw, signalAbove: above, signalBelow: below, signalMeter: meter };
}
```

```text
/// Generator: one use of `signals` per element kind that has meters. Reads the type graph, writes module gen.estatesignals.
module acme.healthgen @ 1.0.0;
import sattle.kernel @ 1.0.0 sha256:… as k;
import acme.healthlib @ 1.0.0 sha256:… as hl;

projection signalsPerKind generation output sattle.kernel {
  in defs: catalog acme.ops;
  let kinds = match defs (r: k.RelationshipType)-[:k.targetType]->(t: k.EntityType)
    |> where r.name == "MeterOf"
    |> select { t: t, kindName: lowerInvariant(t.name) };
  // the projection that holds the uses, once
  emit entity k.Projection from kinds identity k.definition(module: "gen.estatesignals", path: "projection:treeSignals")
    set { name: "treeSignals", purpose: "view", retain: 4, forward: true };
  // one use per kind; the key is the element's timeless id, the label comes from the type's name
  emit entity k.StatementUse from kinds
    identity k.definition(module: "gen.estatesignals", path: concat("projection:treeSignals/use:", kindName))
    set { statement: "signals", name: kindName };
  emit entity k.ParamValue from kinds
    identity k.definition(module: "gen.estatesignals", path: concat("projection:treeSignals/use:", kindName, "/param:kind"))
    set { param: "kind" };
  emit relationship k.ref from kinds key kindRef:
    k.definition(module: "gen.estatesignals", path: concat("projection:treeSignals/use:", kindName, "/param:kind")) -> t;
  emit entity k.ParamValue from kinds
    identity k.definition(module: "gen.estatesignals", path: concat("projection:treeSignals/use:", kindName, "/param:key"))
    set { param: "key", text: concat(kindName, "/", "{entityId(e)}") };
  // container edges (contains, slot, ordinal) for each node: omitted here, one emit relationship each
}
```

```text
/// The space binds the generator and the generated module like any other.
space operations {
  …
  module gen.estatesignals = acme.healthgen.signalsPerKind(defs: ops);
  graph treeSignals = gen.estatesignals.treeSignals(master: master);
  …
}
```

The printed form of the generated module, as a reader would see it (`GET /releases?module=gen.estatesignals`, then the canonical text):

```text
/// Generated by acme.healthgen.signalsPerKind at generation point 812. Do not edit; change acme.ops or the generator.
module gen.estatesignals @ 1.0.3;
…
projection treeSignals view forward output acme.tree {
  in master: graph ops;
  retain 4;
  let rules = match master (u: ops.SignalRule) |> select { … };
  signals azureresource(master: master, kind: azure.AzureResource, key: concat("azureresource/", entityId(e)), kindName: "azureresource", rules: rules);
  signals node(master: master, kind: infra.Node, key: concat("node/", entityId(e)), kindName: "node", rules: rules);
  signals pod(master: master, kind: infra.Pod, key: concat("pod/", entityId(e)), kindName: "pod", rules: rules);
}
```

The same pattern generates the level ontology: a generator over relationship types marked `hierarchy` (the kernel already has `RelationshipType.hierarchy`) emits one `EntityType` per level and one `Contains` per hierarchy edge type, and one roll-up use per level. Two limits of the example, both **open**:
- **Keys.** The hand-written tree uses readable keys (`node/<cluster>/<name>`, the lower-cased resource id). A generated key needs the identity domain's components in order, and `L` has no ordered string aggregate to build `concat` of a variable number of parts from rows; the example uses `entityId(e)`, which is stable and opaque. A catalog `Query` in the template (section 3.1) can build the expression instead, because queries run at expansion, not in the data plane.
- **Emitting definitions is verbose.** A generator that emits raw kernel facts must emit every container edge with its slot and ordinal. Emitting **statement uses** (as above) instead of operators is what keeps generators short; generators and derived statements are therefore one design, not two.

---

## 5. Cost and risk

Cost side agreed with `storage-scale` (its answer to this note; 04, sections 1.4, 5 and 6.7). 04 sets no cap on plan size per space and leaves the cap to generators; the fixed cost per operator instance stays below 64 KB.

| Claim | Status |
|---|---|
| R1. In steady state a derived statement costs exactly its expansion: no new operator kind, so 04's per-operator budgets (section 5) apply to each generated operator. `lift` expands to 15 operators per statement today. Caveat: the engine today runs every operator of every graph in every epoch (code review 07, `Engine.cs`), and per-batch overhead may be half of engine time (04, X2); idle instances cost nothing per epoch only once operators with an empty input delta are skipped, which 04 states as a requirement. | proved (D1) for semantics; cost per 04, conditional on empty-delta skipping |
| R2. A generator multiplies plan size by its instances (the estate example: one `signals` use per meter target type, three today, about seven operators each after expansion; a level generator of 8 kinds and 3 levels would be 24 instances). Proposed cap: every generator declares `max instances N` and `max operators M` (default 32 instances, 1,000 operators); a generation point that exceeds either fails and keeps the previous release. Per-epoch cost scales with the operator count until empty-delta skipping exists (R1). | design |
| R3. Shared arrangements count `Match` state once only when instances read the same relation with the same key and columns. Instances that differ by a filter on the element kind share the base arrangement only if the planner hoists the common `Match` above the per-instance filters; otherwise `Match` state multiplies by the instance count. A derived statement should therefore put the instance-specific part after a common pattern (the `signals` template matches `Meter`-`MeterOf` and filters by kind after it). Generated plans cost nothing beyond 04's fragments only with hoisting and empty-delta skipping. | design, per 04 |
| R4. A regeneration is a re-activation, and its cost is per definition commit, not per data rate: bulk derivation of the affected projections over the current state of their inputs, plus a replay within the longest look-back of their windowed operators (`Window`, `Series`, `Retain`). That holds only once a re-activated projection initializes from the current state of its graph inputs. Today a re-activation replays every retained input (the host keeps up to 40 M rows), so its cost is O(retained history); at the X10 replay speed (2.9 µs per row) one day of the 10⁶ telemetry input (about 2.2 G rows, 04 section 7.1) takes hours. Budgets to hold it to (04): bulk derivation at 10⁶ ≤ 20 s (measured 174 s for the first window today, under question V1), peak memory ≤ 2× steady (4.4 GB at 10⁶) while the second engine replays. No re-activation at 10⁶ has been measured. A generated release whose digest is unchanged activates nothing. | design; today's cost measured in kind, not at 10⁶ |
| R5. Fixed costs per space build grow with modules and operators: catalog load and check 4.2 s for 19 modules today (about 0.2 s per module), plan compilation 0.35 s (`engine/PERF.md` baseline). Each generated release adds one module check at its generation point. | measured base, design |

Feasibility of the generated front end, from `engine-core`'s answer (design only, nothing scheduled; it had not read `LiftExpander.cs` in full):
- **Parser and printer: feasible.** The lexer is reused. Parsing stays context-free: a statement whose leading word is not a primitive parses into a generic node (name, then each clause keyword with its tokens up to the next keyword or `;`), and is bound to its declaration's params at lowering, where the catalog is known; otherwise parsing a file would need its imports first. Sizes: generic clause node and parser about 150 lines, param binding and its errors about 150, printer from declarations about 100, template instantiation as graph building about 300 (against `LiftExpander` today, 229 lines of text building). Primitives stay in `Syntax.cs` and `Printer.cs`.
- **Diagnostics by provenance: feasible, and smaller than what exists.** Diagnostics carry a focus node id; today the checker maps a generated operator back to its `lift` by matching name prefixes (`NAME`, `NAME__`, `NAME#`, `NAME.`). An `expandedFrom` edge (use node, param) on each generated node, followed from the focus node or its container, replaces that.
- **Hygiene: the larger part.** Checker, typed model and runtime key operators by text name inside a projection, so scope-aware name lookup would touch most of `Checker.cs` (1,200 lines). Proposed instead: generated names stay unique internal names, derived from the use's node id and not from typed text, and everything a reader sees (diagnostics, what may be referenced) comes from the provenance edges. Section 3.3 is amended accordingly.
- **A `catalog` port: possible, with two engine changes.** The graph-port machinery is generic (`Match` needs a graph and an ontology view, and the kernel describes itself). Within one activation the catalog is constant, so the port needs a snapshot at activation and no incremental path. The changes: (1) the space checker (S1 to S3: every graph produced by a projection and reachable from the master) must admit `catalog` as a bindable source with the kernel module as `expects`; (2) definition ids are paths (`d:module/kind:name`) that code reads back, while sink identities are hashed (`e:` plus a hash), so a generator needs an identity domain whose allocation is the path. Committing a generated module as printed text goes through the ordinary path, but depends on the printer round trip, which the code review found broken for escaped text and for a negative literal after `above` (Theorem 5.6 rests on premise RT). Definition commits are journaled already; the catalog revision becomes part of the recorded position.

Theorems against `paper/inventory.md`:

| Statement | Carries over by | New statement needed |
|---|---|---|
| Theorems 5.1, 5.2, 5.3, 5.5 (checking, typing, non-interference, relationship ids) | per generated module and per expansion (D1) | none |
| Theorems 6.1, 6.2, 6.3 (incremental correctness, batch invariance, replay) | per stage | S2, S3 across stages |
| Theorem 5.6 (lossless syntax) | for uses, with the clause shape added under RT | a generated graph outside the image of `parse` is rejected (S2 gap) |
| Proposition 5.7 (refactoring) | for hand-written modules | none; generated modules are not refactored (they are read-only) |
| Theorem 9.1, 9.6 (edit replay, pinned history) | each regeneration is a configuration edit | S1 (stages) for the order of re-activations |
| Theorem 9.7 (lineage) | for data; and for definitions, lineage from generated elements to generator rows | none beyond D1 |
| Theorem 2.2 (kernel self-describing) | unchanged; the types of 3.1 and 3.7 (`StatementDecl`, `ParamDecl`, `Seq`, `Choice`, `Opt`, `Rep`, `Tok`, `Hole`, `Sub`, `Infix`, `Template` with `Fresh`, `When`, `Each`, `Query`, `Ref`, `Requires`, `Card`, `StatementUse`, `ParamValue`) are new kernel types and change the kernel digest | the kernel table grows; Theorem 2.2's test reruns |

Risks:
- **Definition churn from data.** A generator over data graphs makes definitions change with data, and each change is a re-activation. Mitigation: generation points only from catalog commits and authored triggers in the first stages (decision G2).
- **Errors far from their cause.** A generated release that fails its check is diagnosed on generated elements. Mitigation: lineage to generator rows and `Requires` on statements; the previous release stays active.
- **Two places to read.** A reader of `gen.estatesignals` must follow it to the generator. Mitigation: the generated module's header names its generator and generation point, and the canvas can show the lineage.

---

## 6. Decisions for the user

**G1. Which first: derived statements or generators?**
- (a) Derived statements first: kernel declarations, the generic clause parser and printer, provenance-mapped errors; then re-express `lift` as a declaration and compare with `LiftTests`.
- (b) Generators first: the catalog port and generated modules, emitting raw kernel facts.
- Recommendation: **(a).** It retires the per-statement C# path, is testable against `lift` immediately, and makes generators short later (they emit uses, not operators).

**G2. What may a generator read?**
- (a) The catalog and authored records only; generation points at catalog commits and authored triggers.
- (b) Also data graphs (for example the Azure resource types observed), with a declared cadence.
- Recommendation: **(a) first, (b) later.** (a) covers the user's example (the type graph is in the catalog); (b) makes definitions change with data and should wait until re-activation meets its budget (04, 6.7).

**G3. Are generated modules editable?**
- (a) Read-only; an "eject" copies a generated release into a hand-written module that the user then owns.
- (b) Editable with the edits kept as an overlay over regeneration.
- Recommendation: **(a).** (b) is a merge problem with no stable answer when the generator's output changes shape.

**G4. How are templates typed?**
- (a) Check each expansion, as `lift` is checked today, plus typed params and `Requires` for good errors ([Sheard and Peyton Jones]).
- (b) Typed templates: a template that checks with typed holes expands to well-typed code for every well-sorted argument ([Davies and Pfenning]).
- Recommendation: **(a) now, (b) as research.** (b) needs a type system for open definition fragments that the kernel does not have.

**G5. Self-reference.**
- (a) Strictly acyclic stages (S1).
- (b) A fixed number of rounds of regeneration until the generated release stops changing.
- Recommendation: **(a).** (b) reintroduces the termination question for definitions with no use in the examples.

**G6. Fusion of stacked projections (S4).**
- (a) Later, as a generator over the catalog.
- (b) Never: stacking stays the only composition.
- Recommendation: **(a)**, after G1 and the catalog port exist.

**G7. How is the canonical text produced?**
- (a) A compiled printer and parser generated from the grammar graph (one invertible description, 3.6 and 3.7).
- (b) A document projection over the catalog with a new code renderer (T1), and a generated parser beside it.
- (c) Keep the hand-written printer and parser.
- Recommendation: **(a)**. (b) is possible (T1) but gives two descriptions of one syntax again; keep document projections for human documentation of the grammar and of modules, where they already fit.

**G8. How rich is the grammar graph?**
- (a) Derived statements only, with the one clause shape of 3.2 (stage 1 of the plan).
- (b) A full production graph (`Seq`, `Choice`, `Opt`, `Rep`, `Infix`) that regenerates today's whole syntax and lets primitives move out of `Syntax.cs`.
- Recommendation: **(a) first, (b) after the printer round trip is fixed**; (b) is what a visual grammar editor needs to show a statement's shape.

**G9. Comments and layout.**
- (a) Keep today's rule: `///` is data, `//` and layout are dropped.
- (b) A retentive lens with a trivia layer outside release digests (3.6).
- Recommendation: **(a)**. With visual authoring first, text is a rendering and trivia matter little; revisit if users author mostly in text.

### Staged plan

| Stage | Builds | Done when |
|---|---|---|
| 1 | kernel types `StatementDecl`, `ParamDecl`, `Template` with `Hole`, `Fresh`, `When`, `Each`, `Query`, `Requires`; `StatementUse`, `ParamValue`; the generic clause parser and printer for uses; palette entries from declarations (engine-core's estimate: about 700 lines beside the hand-written primitives) | `lift` written as a declaration in `sattle.std` passes every case of `LiftTests` with the same digests, and the hand-written `Lift` path is deleted |
| 2 | `expandedFrom` provenance edges from expansion to use and param; generated names derived from the use's node id | the `lift` refusals of the checker are reported at the clause; a user `let` that collides with a generated name is accepted |
| 2b | the grammar types of 3.7 in `K`; `/palette` and editor cards rendered from `StatementDecl` and `ParamDecl` instead of `Support.cs` | the palette served from the grammar graph equals today's palette for every operator, and a new declaration appears in it without an engine release |
| 3 | prerequisites: the printer round trip fixed for escapes and negative literals (Theorem 5.6, RT); an identity domain whose allocation is the definition path; the space checker admitting `catalog` as a source. Then the `catalog` port kind (read-only, closure of a named module, pinned release); purpose `generation`; generated modules with `generatedBy`; generation points at catalog commits and authored triggers; the stage DAG check | the estate example of section 4 generates `gen.estatesignals` and its `treeSignals` graph equals the hand-written one (same state digest on the estate and on a capture) |
| 4 | the level ontology generator (hierarchy relationship types to levels and roll-ups) | a new hierarchy edge type in `acme.ops` adds a level with no other edit |
| 5 | data-graph inputs to generators with a declared cadence (G2 b) | re-activation meets 04's budget at 10⁶ |
| 6 | full production graphs for the primitives and a generated parser and printer (G8 b); `Syntax.cs` productions removed as they move | `parse(print(G)) = G` on every module and on generated graphs (Theorem 5.6 as a test), the engine's parse errors keep their positions |

---

## Sources

Opened on 11 November 2026:
- [Davies and Pfenning] R. Davies, F. Pfenning, [A Modal Analysis of Staged Computation](https://www.cs.cmu.edu/~fp/papers/jacm00.pdf), Journal of the ACM: a type system from the modal logic S4 for computation stages.
- [Sheard and Peyton Jones] T. Sheard, S. Peyton Jones, [Template Meta-programming for Haskell](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/meta-haskell.pdf), Haskell Workshop 2002: compile-time metaprogramming, reification of declarations, splices type-checked after expansion.
- [Amin and Rompf] N. Amin, T. Rompf, [Collapsing Towers of Interpreters](https://www.cs.purdue.edu/homes/rompf/papers/amin-popl18.pdf), POPL 2018.
- [Flatt] M. Flatt, [Binding as Sets of Scopes](https://www.cs.utah.edu/plt/publications/popl16-f.pdf), POPL 2016: hygiene by scope sets.
- [Tobin-Hochstadt et al.] S. Tobin-Hochstadt, V. St-Amour, R. Culpepper, M. Flatt, M. Felleisen, [Languages as Libraries](https://www2.ccs.neu.edu/racket/pubs/pldi11-thacff.pdf), PLDI 2011.
- [Kats and Visser] L. Kats, E. Visser, [The Spoofax Language Workbench](https://eelcovisser.org/publications/2010/KatsV10.pdf), OOPSLA 2010: parser generation and IDE services from declarative specifications.
- [Rendel and Ostermann] T. Rendel, K. Ostermann, Invertible Syntax Descriptions: Unifying Parsing and Pretty Printing, Haskell Symposium 2010 ([PDF](https://www.informatik.uni-marburg.de/~rendel/rendel10invertible.pdf), read from the downloaded file).
- [Zhu et al.] Z. Zhu, Y. Yang, H.-S. Ko, Z. Hu, [Retentive Lenses](https://arxiv.org/abs/2001.02031) (full text read at https://arxiv.org/html/2001.02031v2): hippocraticness, correctness, retentiveness.
- [Foster et al.] J. N. Foster et al., [Combinators for Bidirectional Tree Transformations](https://www.cis.upenn.edu/~bcpierce/papers/lenses-full.pdf) (opened for research 03): GetPut, PutGet, PutPut.
- [Blockly docs] [Block definitions in JSON and JavaScript](https://docs.blockly.com/guides/create-custom-blocks/define/json-and-js/) and [code generation](https://docs.blockly.com/guides/create-custom-blocks/code-generation/overview/).
- [Node-RED docs] [Node HTML file](https://nodered.org/docs/creating-nodes/node-html) and [node properties](https://nodered.org/docs/creating-nodes/properties).
- [MPS docs] [Structure](https://www.jetbrains.com/help/mps/structure.html) and [Editor](https://www.jetbrains.com/help/mps/editor.html) (the editor page partly read).
- Not opened (403 or 404 on every link tried): Maloney et al., The Scratch Programming Language and Environment (TOCE 2010); Chen, Kifer, Warren, HiLog (J. Logic Programming 1993); Lakshmanan, Sadri, Subramanian, SchemaSQL (VLDB 1996); Taha, Sheard, MetaML (TCS 2000).

Repository: `engine/src/Sattle.Runtime/Documents.cs`, `engine/src/Sattle.Language/LiftExpander.cs`, `Syntax.cs`, `Printer.cs`, `Checker.cs`, `Lowering.cs`, `engine/src/Sattle.Core/Kernel.cs`, `engine/src/Sattle.Runtime/Support.cs`, `engine/PROTOCOL.md`, `engine/definitions/acme.space.sattle`, `acme.tree.sattle`, `acme.ops.sattle`, `paper/ch05`, `ch09`, `ch12`, `paper/inventory.md`, [04](04-compute-and-memory-efficiency.md) sections 5 and 6.7, [05](05-expressiveness-and-flexibility.md).
