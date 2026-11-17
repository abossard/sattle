# 11. The visual editors on KQL

Design note, 13 November 2026. Status, 17 November 2026: stages a to d of section 7 are built, Blocks is folded into Steps, and the old node canvas (the editor of a projection's operator graph) is removed with the old projection path. It answers how the five editors of note 09 move from the engine's graph of operators to projections written in KQL (note 10).

## Summary

1. **The KQL text of a projection is the only truth.** A visual edit replaces one stretch of the text; nothing else is rewritten, so comments, layout and the author's names survive, and text to picture to text is the identity by construction. No printer from operators back to KQL is built.
2. **The engine reads the text for the editors.** It gives an outline: every `let` and step, every operator inside it, and every part of each operator, each with its place in the text, and the columns after each operator. It is computed with Kusto.Language, the parser the engine already uses, so there is one reading of KQL, not two.
3. **The engine calls are agreed with the language session**: analyse (errors and the outline, fast or full), complete, hover, related names, and draft calls that store, preview and commit the `.kql` text. All of them are built, with their routes (protocol 3.41 to 3.44). They replace the skeleton, patch, palette, text and refactoring calls for projections. Ontologies keep today's draft calls. A new projection is declared by its text alone, with a twelfth addition to KQL that the orchestrator accepted: an `input` line per graph or table it reads.
4. **The text editor is CodeMirror 6** with completion, hover and errors from the engine. monaco-kusto is MIT itself, but the Kusto language service it embeds ships no license file and its compiled code says "All rights reserved"; it would also mark every step line of ours as an error, because steps are not plain KQL.
5. **The text is always visible** beside each visual notation, and a click in one selects the same place in the other.
6. **Steps, Outline and By example fit KQL well.** KQL is pipelines, `graph-match` is patterns.
7. **Wires fits for reading** how tables feed each other and for changing what a join reads; edits inside an operator happen in its card.
8. **Blocks is the same as Steps on KQL**, a stack of operator cards. I recommend folding its palette into Steps and dropping it as a notation of its own, one thing fewer to learn and to keep; the Text editor becomes the fifth.
9. **One editing module sits behind every notation** (section 5). A notation is a pure view of the text and its outline and hands back text changes; it knows nothing of the engine or of the text editor. The module turns set, add, remove and move into text changes with pure functions and hides separators, indentation and comments. Three ideas (place, reading, change) and four edits replace today's operator model with its twelve kinds of gesture. About 2,000 lines go, about 500 stay, about 800 are rewritten and about 800 are new.
10. **Four stages, each with a test**: the text editor; the outline and Steps; the other notations; the draft on text with the old path removed. Every stage proves that all twelve shipped projections open in every notation and that unchanged text stays byte for byte the same.

Two decisions for the user, both taken and done: Blocks is folded into Steps (its palette is the adding of operators in the steps), and the old node canvas is removed after stage (d).

## 1. Source of truth

**Accepted: the text is the truth.** A projection is its `.kql` file. The editors keep no model of their own. They ask the engine for the outline of the current text, draw it, and turn an author's action into a text edit: replace the stretch from one position to another with new text. After every edit the engine reads the text again.

Why this and not a model of operators with a printer to KQL:
- **Comments and layout.** The shipped texts explain themselves in comments (`// a pod that backs two services counts for both`) and break long pipelines where a reader expects. A printer loses comments unless it carries every one through its model, and it lays out text its own way, so one edit would reformat the whole file and a review of a change would show everything changed.
- **Round trip.** With a printer, text to model to text must be the identity for 67 constructs and 11 additions, and every new KQL construct needs a model, a printer and a form. With text edits, untouched text is never rewritten, so the identity holds by construction for everything the author did not touch.
- **One reading of KQL.** The outline comes from the same parse that the engine runs, so the editor can never show a projection the engine reads differently.
- **What it costs.** A form must produce valid text. When it does not (a condition typed wrongly), the edit still goes in, and the engine's error shows at its place in the text and on the card, as for any typing error. Undo is undo of text edits, which is simpler than today's undo of graph patches.

**Rejected: today's model of operators.** It is what the editors have now. It was right while the engine's own text was printed from the operators; for KQL it would need a second, lossy reading of KQL in the browser and a printer the engine does not have.

## 2. What the engine must offer (agreed with the language session)

The server side belongs to the language session, which writes it as one class in the KQL front end; engine-core only adds the routes and keeps its draft part small. The canvas side is mine. Agreed on 13 November 2026. Built since, in the KQL front end and not committed: analyse, complete, hover and related, with a test on all twelve projections that every engine operator belongs to an operator, a step's sink or a function call of the outline. Not built: their routes, the draft calls, and the engine's own column types at full depth; until then a column carries Kusto's type, the element type and the telemetry mark. The full contract, with a sketch of the analyse answer, is the language session's file `kql-editor-contract.md` in the shared notes; this section summarises it.

**Common to every answer.** A place is a stretch of the author's text, `[start, end)`, counted in UTF-16 units as JavaScript and Kusto.Language both count. Kusto and the profile see the text as sent, position for position, because the step lines are rewritten to lines of the same length before Kusto reads them. Every answer carries the digest of the text it read, so an answer to an older text is recognised and dropped.

**a. Analyse a text** (`POST /kql/analyse`, with the projection and the text). Two depths: *fast*, for every pause in typing (parse, Kusto's check, the profile, the lowering, the outline), and *full*, which also loads the projection as the engine does (about 1 to 2 seconds) and adds the engine's own errors, mapped back to places, and the engine's column types. The answer:
- errors and warnings with place, line, column, where they come from (Kusto, the profile, the engine) and the message;
- the graph line, the inputs with the places that use them, the parameters with the place of their declaration, the functions with their parameters and body;
- per `let` and step: kind, name or type, `on`, `from`, `to`, each setting in `with (...)` with its place, the place of the statement and of its query, what it reads (`let`s, graphs, tables of rows, functions), and the sink it makes;
- per operator of its pipeline: kind, place, the name of what a source reads (a `let`, a graph, a table), the places of its parts (the condition of a `where`; each item of a `project`, `extend` or `summarize` as its name, the place of the whole item, the place of the name and the place of the expression; the kind, right side and keys of a join; the legs of a union; the arguments of an invoke; each pattern, the condition and the projection of a `graph-match`), the pipelines inside it (the right side of a join, each leg of a union) as operators of the same shape, the columns after it (name, type, the element type for a column that holds an element, whether it is telemetry), and the ids of the engine operators it becomes;
- two marks where the text says something indirectly: a step's last `project` or `extend` has no operator of its own, its expressions belong to the sink (`inSink`); and `invoke f()` of a table function is written out at the call, so the call lists the engine operators of its body (`expands`).

**b. Complete at a place** (`POST /kql/complete`). Kusto's items, without what the profile refuses (`take`, `sample`, `sort`, `top`, `scan`, `evaluate`, `mv-expand`, `dcount`, `count_distinct`, `take_any`; `now()` and `ago()` only in `where t > ago(...)`), and from the ontology what Kusto cannot know: the step forms at the start of a statement, the types of the result after `nodes` and `edges`, the columns of the query after `on`, `from` and `to`, the settings, rule names and function names in `with (`, the types of a graph in `labels(x) has "`, the identity domains in `identity("`, the telemetry properties in `property == "`.

**c. Hover at a place** (`POST /kql/hover`). Kusto's description of a name, and from the ontology a type's description, a property's kind, unit and role, and the engine's type of a column when the answer is full.

**d. The places that name a `let`, function or parameter** (`POST /kql/related`), from Kusto's related elements, for a rename; a step's own column names come from the outline.

**e. Rows after each operator.** The preview stays as engine-core has it (3.21). The canvas joins the operator ids of a preview with the engine ids of (a), so the preview needs no places.

**Draft calls** (`/drafts/{id}/kql/...`, engine-core wires them, the KQL front end does the work): read a projection's `.kql` text as the file holds it and store it in a draft (the editors never read the module's printed text, which shows a KQL projection in the engine's own notation); check and preview a draft through the path that already loads KQL projections; commit by writing `module.projection.kql` exactly as stored, never printing a KQL projection into the module's `.sattle` text. The engine's core stays free of the KQL parser: the host takes the translation as a step handed to it by the command line tool and the tests. The catalog holds no places; the KQL front end returns, with the loaded files, a map from each operator to its place in the text, and the draft keeps that map beside its text.

**f. A projection declared by its text alone** (accepted by the orchestrator as the twelfth addition to KQL): one line per input, `input master : acme.ops;` for a graph and `input pods : k8s.PodRow;` for a table of rows; the line may be left out when the graph is named after an ontology of the module or its imports (`graph("ops")` in `acme.svc`), and otherwise the error says what to write; parameters stay `declare query_parameters`; description and history as settings on the graph line, `graph serviceDeps : acme.svc with (description = "...", history = 4);`; ingestion when it reads rows; the module from the file name; the imports, with the digests that pin the ontologies, stay in the module's `.sattle` file for now, and the binding to the space where it is today. The twelve shipped `.kql` files get their input lines, description and history in the same change. The language session drafts it first; the analyse calls follow.

**What goes for projections**: `/drafts/{id}/skeleton`, `/drafts/{id}/patches`, `/drafts/{id}/text/{module}`, `/drafts/{id}/refactor` (extract, inline and moving a rule become text edits; a rename uses d), the operator lists and `references` of `/palette`, and the `schemas` of a check (replaced by the columns of a). Ontology modules keep today's draft calls.

## 3. The text editor

**Recommended: CodeMirror 6, with completion, hover and errors from the engine.**

| | CodeMirror 6 | monaco-kusto |
|---|---|---|
| License | MIT, every package (`@codemirror/view`, `state`, `language`, `autocomplete`, `lint`, `@lezer/highlight`), per their package files | the wrapper is MIT (`LICENSE.md` of `Azure/monaco-kusto`, npm field). It depends on `@kusto/language-service-next` 12.4.1 and `@kusto/language-service` 0.0.297: npm says MIT for both, but neither ships a license file, the compiled `Kusto.Language.Bridge.js` begins "© Microsoft Corporation. All rights reserved.", and its runtime `bridge.js` (Bridge.NET 17.10.1) points to a `license.txt` that is not in the package. Unclear, so a risk for anything we give away |
| Our additions | the engine reads them, so steps, `with (...)` and the graph line are understood | runs Kusto.Language in the browser on plain KQL: every `nodes`, `edges` and `graph` line is an error unless its checking is switched off, which leaves completion only |
| Schema | none in the browser; the engine checks against the catalog it loads anyway | a second copy of the schema in Kusto's format; whether it takes graph models was not checked |
| Size | about 2.4 MB of packages, roughly 150 KB in the built page | the language services weigh 4.2 MB and 3.2 MB minified plus 1 MB each for their runtime; `monaco-editor` is 72 MB of packages |
| Effort | a small highlighter (keywords, our additions, strings, comments), completion and hover by calling the engine, errors from the analyse call: about 250 lines | wiring the schema, switching off false errors, hiding two versions of the service |

What CodeMirror costs: completion and errors need the engine. Without it the text editor still highlights and edits; the canvas already needs the engine for drafts.

## 4. Each notation

The text sits beside every notation. Selecting a card, block or row selects its stretch of text, and putting the cursor in the text selects its card. Every notation is a pure view (section 5): it is given a reading, the selected place and whether an edit is under way, and it hands back a selection or text changes. The choices a form offers (types, columns, functions, operators) come from the engine's complete call at that place, so a form and the text offer the same.

**Outline** (today's Structure): a tree of the graph line, parameters, functions, `let`s and steps; under each its operators; under each operator its parts as editable lines. Keyboard first, as now. Fits fully.

```
graph serviceDeps : acme.svc
let services =  graph("master")
  graph-match   (s) where labels(s) has "Service"
                project service = s.id, cluster = s.cluster, ...
let calls =     graph("master")
  graph-match   (a)-[c]->(b) where labels(c) has "Calls"
  join inner    backing (fromPod = pod, src = service) on fromPod
  join inner    backing (toPod = pod, dst = service) on toPod
  where         src != dst
  + operator
nodes ServiceNode on service = services
edges DependsOn from src to dst = calls
```

**Steps**: one `let` or step at a time, a card per operator with the columns after it and, on request, the rows after it. The palette of operators (from Blocks) adds a `where`, `project`, `summarize`, `join` and so on with a form; cards move within a pipeline and between pipelines. This is the best fit: KQL is pipelines of operators.

```
let flows                                   read by 4 steps: DependsOn (2), ServiceNode, ServiceGroup
[calls]                       gives call, fromPod, toPod, src, dst
[join inner services on src]  + cluster, namespace
[join inner telemetry(master, 24h) on call]  + windowStart, property, value
[summarize by src, dst, cluster, namespace, windowStart]  gives src, dst, cluster, namespace, windowStart, flows, drops
[project ..., dropRatio = iff(flows > 0, drops / flows, 0.0)]   (rows after each card on request)
```

**Wires**: the tables and steps as cards, wired by what each reads: `let` names, `graph("...")`, the right side of a join, the legs of a union. Good for seeing a projection whole and for changing what a join reads by connecting another output. Editing inside an operator happens in its card in the side panel. In the workload health text, 26 `let`s feed 41 steps, and eight of its types have three steps each (one that makes the nodes, two that add to them), so the layout groups the steps of one type.

```
graph(master) ─┬ services ── nodes ServiceNode, nodes ServiceGroup, edges Contains, ...
               ├ backing ─── extend nodes ServiceNode (pods); read twice by calls
               └ calls ───── edges DependsOn
                   └ flows (also reads services, telemetry(master, 24h))
                        └─── extend edges DependsOn (flows; status), extend nodes ServiceNode, ServiceGroup (flowsOut)
```

**By example**: picking elements of a graph writes a `let` with a `graph-match` pattern, with a condition only on request. KQL's `graph-match` is the pattern language, so this is the most natural of all. Picking a pod, the service it backs and the `Backs` edge writes:

```
let picked = graph("master")
    | graph-match (p)-[b]->(s) where labels(p) has "Pod" and labels(b) has "Backs" and labels(s) has "Service"
      project p, s;
```

**Blocks**: on KQL a block stack is a pipeline of operator cards, which is what Steps shows. Recommended: its palette and its pick up and put down move into Steps, and it goes as a notation. If the user wants it kept, it can stay as a second look of Steps at little cost. Done: Blocks is gone, and operators are added in the steps.

**Statement kinds** (the stand-in of note 09): dropped. KQL's own functions with a table parameter (`let windowsOf = (R:(...), p:string) { ... }` and `invoke windowsOf(...)`) do what the stand-in did, and the engine already reads them. A later text action can turn a pipeline into such a function.

## 5. One editing module behind the notations

The design held against the repository's rules: deep modules, data and calculations kept apart from actions, every concept a cost.

**Data**, plain and never changed in place:
- a *place*: a stretch of the text;
- a *reading*: a text together with the engine's outline of exactly that text, so the two cannot disagree. The outline is the engine's answer as it comes; the canvas builds no second model from it;
- a *change*: replace a place by new text. An edit is a list of changes.

**Calculations**, the whole interface of the module:

| | From | To |
|---|---|---|
| read | the engine's answer and the text it was asked with | a reading |
| set | a reading, one or more places, new text (a rename sets every place that names the thing) | changes |
| add | a reading, a list, a position, text | changes |
| remove | a reading, an item | changes |
| move | a reading, an item, a list, a position | changes |
| starter | a reading, a list, a kind (a `where`, a `join`, a `nodes` step, a pattern from picked elements) | text that reads without error for the columns before it |
| item at | a reading, a position in the text | the innermost item there; and from an item, its place |
| rows | a reading, a preview | the rows after each operator |

A list is anything the text separates: the statements of the file, the operators of a pipeline, the items of a `project`, `extend` or `summarize`, the keys of a join, the legs of a union, the patterns of a `graph-match`, the settings in `with (...)`.

**What the module hides:**
- **Separators and layout.** One rule serves every list: an item owns the text between the item before it and itself. That text holds the separator (`;`, `|`, `,`), the line break and indentation, and a comment written above the item. Remove takes it along, move carries it, add copies it from a neighbour. So no edit is written per kind of operator, and a KQL construct that the outline gains later is editable without new code in the canvas. The places of the built analyse call end at an item's own text and leave pipes and comments outside, which is what the rule needs.
- **What the rule does not cover**, in one small table: the separator of a list with a single item, and parts that may be absent (the condition of a `graph-match`, the `by` of a `summarize`, the `with (...)` of a statement).
- **The engine's marks.** That a step's last `project` belongs to its sink, and that a function call is written out where it is called, matters only for putting rows under the right card. `rows` uses the marks; no notation sees them.
- **Late answers.** An edit is computed on a reading and applied only while that reading's text is still the text. After an edit the notations show the old reading as busy until the next one arrives.

**Actions**, three, each thin:
- the engine port: read the text, analyse, complete, hover, related, preview, store, commit;
- the text view: CodeMirror shows the text, takes typing, and holds the text with its undo history as one value that is replaced on every change. Typing and visual edits go the same way in, so there is one undo for both;
- the effect in the app's state: apply changes, ask for a new reading (fast after a pause in typing, full when idle; no notation knows of the two depths), keep the selected place.

**A notation** is a function from a reading, the selected place and the busy mark to a picture, with two ways out: select a place, hand back changes. It uses the module's calculations and nothing else. Its tests need neither engine nor text editor: a recorded reading in, a picture and changes out.

**Counted against today.** A notation today sees an operator model of four levels, twelve kinds of gesture, an edit specification per row, the palette, the catalog, and a draft port with placeholders. After: place, reading, change and four edits. The canvas's own palette of operators and its rules for which names are in scope go, because the complete call answers both.

## 6. The old pieces

Today's editing code in the canvas: 4,840 lines (`src/editor` 1,468, `src/editing` 1,257, `src/editors` 1,430, `src/ui/editor` 685).

| | Lines | What |
|---|---|---|
| Stays as it is | about 500 | the rows after an operator (`results`), the tree with windowing (`Outline`), the row line and inline editor (`parts`), card, layout of cards and wires (`graph-layout`), the data drawing, window and view size |
| Rewritten on the outline | about 800 | the rows of a notation (`rows`), Steps, Wires, By example and its pattern (`example`), the workbench and its place in the app |
| Dropped | about 2,000 | the operator read model and its gestures (`doc`, `gestures`, `work`, `plan`, `shapes`, `exprtext`, `draft`, `session-draft`, `scope`), the statement-kinds stand-in (`statements`, its panel), Blocks and the page host, and with the old canvas the command builders, expression parser, reference table and incomplete check (`commands`, `expr`, `refs`, `incomplete`, `slotkinds`, `layout`) |
| Stays for ontologies | about 1,000 | drafts, sessions and service (they gain the KQL calls), declarations of a module (`structures`, `palette`, `patches`, `stack`, the Definitions panel and the new-projection dialog), owned by the canvas session |
| New | about 800 | the text editor (about 250), the editing module of section 5 (readings and finding an item, 150; the four edits and the starters, 300), keeping text and notation selected together (80) |

Tests: the operator tests in `tests/editing` (919 lines) and most of `tests/editors` (1,352) go or are rewritten. The ten walkthroughs, lists of operator commands (12,276 lines of JSON), are replaced by the twelve `.kql` files themselves and by edit scripts made of text edits. For tests without an engine, the engine's answers for the twelve files (outline, columns) are recorded as fixtures, as the catalog is today.

## 7. Stages, each with its test

| Stage | What | Done when |
|---|---|---|
| a | Engine calls a, b, c; the Text editor in the canvas | all twelve files open without errors; a seeded `dcount(x)` shows the profile's message at its place; after `\| ` completion offers only the profile's operators, after `s.` the properties of the type of `s`; hover on a column gives its type |
| b | The outline; Steps on it; edits as text | for each of the twelve files, opening and changing nothing leaves the text byte for byte the same; every operator's place lies inside its statement's and none overlap; for every item of every list in the twelve files, removing it and adding it back, and moving it away and back, give the text byte for byte; each form edit changes only its own stretch, and the result reads without errors; rows after each step appear under the right card |
| c | Outline, Wires, By example, the palette in Steps | the same edit made in each notation gives the same text; no notation uses the engine port or the text editor; the twelve files open in every notation without accessibility findings; picking a pod, a service and its `Backs` edge writes the `graph-match` of `backing` in the service tree |
| d | The draft calls (store, preview, commit the text); the old projection path removed | edit, commit and reload give the edited file exactly; unchanged files keep their state digests; the five reference projections, built in each notation from an empty `graph x : y;`, give the original's state digest |

**Built** (17 November 2026, checked against `canvas/` and the git log):
- a: the editor routes (analyse fast and full, complete, hover, related, the `.kql` text of a projection and of a draft; protocol 3.41, b9a279f) and the text editor on CodeMirror 6 (7a0f3ce).
- b: the outline with the places for text edits (protocol 3.42, 440c8ed), the editing module and Steps beside the text (b742000).
- c: adding operators in the steps, checked by a commit and a reload against a live engine (8a5b651); By example, where a picked edge writes its pattern (2bedc0c); Outline and Wires as pure views that hand back text changes (3b8c857); the outline lists only the columns the rows have (protocol 3.44, 4b6edf3).
- d: the text stored in the draft (da6c05b); a statement added through the list of statements, and the reference projections built from an empty graph in Steps, Outline and Wires giving the originals' digests (9caa132); the old projection path removed, with the operator model, Blocks (`BlockEditor`), the graph editor of operators (`GraphEditor`), the statement-kinds stand-in, parameter bindings and the refactor dialog (0d0b47e); a new projection stores its KQL text and binds its graph in the space (6426646).

What the canvas has now, in `canvas/src/editor` and `canvas/src/ui/editor`: the text (`KqlText`), Steps, Outline, Wires, By example (`kql-example`), the editing module (`kql-edit`), the new-projection dialog, and the declarations of a module, which keep their own draft calls.
