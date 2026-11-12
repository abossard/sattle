# 09. Visual editors for projections

Status: research note, 11 November 2026, written to decide from. Sections 0 to 8 were written before anything was built; section 9 (12 November 2026) reports what was built and measured, and where it differs from sections 5 and 8. Claims carry a tag: **[P]** primary source (the tool's documentation or the designers' own paper), **[S]** peer-reviewed empirical study, **[2]** secondary source, **[U]** the source could not be opened in full and the claim rests on an abstract, a search excerpt or memory, **[E]** an estimate made here with its assumptions stated, **[R]** a fact read from the repository. Companions: [03](03-projection-language-and-metagraph.md) (kernel and catalog), [08](08-definitions-as-projections.md) (statements as definitions), [07](07-code-review-findings.md) section 3 (defects of the existing editor).

The question: can a projection be built in a purely visual, graph-style way, in the manner of Scratch or Node-RED, and which kinds of editor are efficient for this language, where efficient means fast to use and cheap to render.

## 0. Findings

1. **No single notation is efficient for the whole language. The language has three shapes and each family fits one.** A projection is (a) a graph of named row sets with joins, unions and fan-out, (b) inside each row set a short chain of steps, (c) inside each step small expressions, of which 70% are a single field reference or literal [R, section 1]. Node-and-wire fits (a), a step list fits (b), text-like structure editing fits (c). Blocks fit (b) and prevent errors by shape, and are the costliest notation for (c).
2. **Estimated expert time for the structural actions of the five reference projections** (keystroke-level model, section 4.1, content typing excluded because it is the same in every editor): text-like structure editor 816 s, step pipeline 864 s, node-and-wire by keyboard 994 s, node-and-wire by pointer 1,448 s, blocks with typed expression slots 1,456 s, blocks with jigsaw expressions 1,861 s [E]. Starting a rule from a selection on the data saves a further 64 s and all typing of type names. These are estimates to be replaced by counts measured on the built editors (section 8).
3. **The published evidence is consistent on four points.** Visual dataflow is slower to modify than text and hides dependencies (Green and Petre: 508 s and 194 s against 63 s for one modification task) [2]. Structure editors are efficient for statements and weak for expressions unless the expression is typed linearly [S]. Shape and hole discipline removes syntax errors and is the only reason to prefer blocks for experts [P]. An inferred program is accepted only when it is shown in readable form and is easy to correct [S][P].
4. **Accessibility decides part of the design.** A node graph maps onto no ARIA pattern, and none of the four dataflow tools documents screen reader use [P]. Trees and grids have defined keyboard and screen reader behaviour [P]. Every drag needs a single-pointer alternative and a keyboard path (WCAG 2.2, 2.5.7 and 2.1.1) [P]. So each editor here is a tree or a grid first, and the graph drawing is one more view of that tree.
5. **The five editors to build** (section 5): a node-and-wire graph, a block editor with typed holes, a text-like structure editor, a step pipeline with the result after each step, and query by example on the data graph. All five are views of one definition graph in one draft. Tables are used inside them for row-shaped parts, not as a sixth editor. Direct manipulation of the output with the program inferred is not built: for this language the inference problem is intractable in general and the result is not trusted without a readable program [P].
6. **Three things are missing in the engine for this** (section 7): intermediate rows per operator in a preview, reference slots in the palette, and the row schema after each operator. The grammar graph of the user's main idea needs statement declarations, which note 08 specifies and the engine does not have.

---

## 1. What is edited

A projection is a set of definition nodes in the catalog graph: operators with typed ports, wires between ports, and per operator a set of named slots holding parameters, references and small expression trees. An editor changes it by sending patches (edits with before and after states) to a draft; the engine answers with diagnostics and the canonical text of the changed modules. The palette lists 20 operator kinds with their ports, parameters and slots [R].

The five reference projections, counted from their definitions and from the recorded command sequences that rebuild them [R]:

| | operators | named row sets | wires | of these inside a chain | slot entries | pattern nodes, edges | expressions | expression nodes | canonical lines | recorded commands |
|---|---|---|---|---|---|---|---|---|---|---|
| deployment health | 19 | 13 | 22 | 3 | 59 | 7, 4 | 53 | 92 | 39 | 112 |
| service tree with lift | 14 | 6 | 15 | 1 | 48 | 3, 1 | 31 | 49 | 38 | 82 |
| health roll-up | 39 | 16 | 44 | 10 | 96 | 14, 6 | 65 | 110 | 73 | 200 |
| series with threshold | 10 | 4 | 11 | 4 | 36 | 2, 0 | 22 | 27 | 21 | 60 |
| fixpoint closure | 7 | 4 | 8 | 2 | 13 | 2, 1 | 9 | 13 | 16 | 32 |

Three facts follow and are used below.

- **Most wires are references by name.** Of 100 wires, 20 connect a step to the step before it in the same chain. The other 80 connect a named row set to a later statement (a join, a union, a second reader, an emit). A linear notation gets the 20 for free and must show the 80 as names.
- **Chains are short.** 63 step operators in 43 named row sets: one and a half steps per chain on average.
- **Expressions are small.** 180 expressions with 291 nodes. 70% of the expressions are one field reference or one literal (between 62% and 81% per projection). The largest has six references.

## 2. The families

Each family is judged for what it is efficient at, how it prevents errors, how it behaves at 50 and at 500 constructs, keyboard and screen reader use, review of a change, and rendering.

### 2.1 Node-and-wire dataflow (Node-RED, Unreal Blueprints, Max and Pure Data, LabVIEW)

- **Efficient at** showing topology: which result feeds which statement, fan-out, joins. Epic states that the visual flow is easier to follow for people who do not program [P1].
- **Inefficient at** change and at size. Epic states that large text files are easier to modify than large graphs and that text is easier to diff and merge [P1]. Node-RED's own guide warns that unstructured flows become a mess of nodes and wires and prescribes tabs, subflows and link nodes [P2]. Puckette notes that text boxes carry more information per area than icons and that the notation is weak for control flow and for viewing data [P3]. In Green and Petre's study one modification task took 508 s in LabVIEW and 194 s in Prograph against 63 s in Basic; they attribute it to viscosity, hidden dependencies and missing secondary notation [2, G1]. Experienced LabVIEW users read graphical conditionals more slowly than text [U, G2]. The often quoted limit of about 50 visual primitives on a screen has no primary source [2].
- **Error prevention.** Unreal validates while a wire is dragged: a compatible pin shows a check, an incompatible one the reason, and some mismatches insert a conversion [P4]. Node-RED has no types on wires [P5].
- **Accelerators.** Dragging a wire into empty space opens a search filtered to compatible nodes and wires the chosen one (Unreal) [P6]; quick-add by typing and splice on a wire (Node-RED) [P7]; type to place and replace keeping wires (LabVIEW Quick Drop) [P8].
- **Keyboard and screen reader.** Node-RED binds every action to a key, but wiring is a pointer action [P9]. None of the four documents screen reader use; a study of low-code editors reports conflicts between editor shortcuts and screen readers [S, U, N1].
- **Review.** Unreal ships a visual diff of two graphs side by side with a list of differences and says a text form would not be useful there [P10]. Node-RED diffs the JSON file [P11].
- **Rendering.** Node-RED draws SVG. Its issue tracker records frame time rising from about 6 ms to about 100 ms at about 800 nodes, caused by forced layout [P12].

### 2.2 Blocks (Scratch, Blockly)

- **Efficient at** preventing syntax errors and at discovery: a block fits only where it makes sense, and placing a block also connects it [P13][P14].
- **Inefficient at** expressions and at size. Blockly's guidance: block languages target novices, deep nesting is hard to read, do not convert a whole API into blocks, and plan an exit to text [P15][P16]. Published block studies use programs of one screen; in a study with 92 novices most never decomposed a larger program unaided [S, B1].
- **Error prevention** is local: a connection check compares two lists of strings on the two ends, the default accepts anything, there is no rule that depends on context and no generic types [P14].
- **Keyboard and screen reader.** Blockly is the only tool in the two visual families with documented keyboard-only editing and screen reader support: a cursor moved with the arrow keys, a move mode that steps a picked-up block through the legal positions, labels and a live region for the drop target; it keeps its own accessibility tree because it draws SVG [P17][P18]. Published evaluations with blind users are small [P, A1].
- **Review.** JSON serialisation, no visual diff [P19].
- **Rendering.** SVG; the maintainers report no freezes at 5,000 blocks with time spent in browser rendering [P20].

### 2.3 Projectional and structure editors (JetBrains MPS, Hazel, Lamdu, Stride, tylr)

- **Efficient at** statements, and at never being in an invalid state. Hazelnut proves that every edit state is well typed, with holes standing for what is missing or inconsistent [P21]; Hazel evaluates around the holes, so a partial program still shows results [P22]. Stride's frames cannot be mismatched, and one key inserts a frame; in a classroom comparison with Java, students spent significantly less time on syntax errors and reached more objectives [S, F1][P23].
- **Inefficient at** expressions and at edits across the tree, unless the editor imitates text. The MPS studies name entering expressions, selecting and copying as the problem areas and report that basic editing is efficient while complex edits need knowledge of the tree [S, U, V1, V2]. The remedies are known: actions at the edges of a cell, completion that substitutes a node, grammar cells that generate these actions so linear typing works [P24][P25]; gradual structure editing, which lets text-like edits pass through invalid states and was competitive with text in a lab study [S, T1]; plain text with structural selection, 36% faster on a second encounter [S, D1].
- **Scale.** Linear, so it scrolls and can be virtualised by line. No source measures 50 against 500 constructs.
- **Keyboard and screen reader.** Keyboard-first by design. MPS lists the screen readers it supports without saying how cells are read [P26]. A structure editor that speaks the tree improved orientation for blind programmers [S, U, X1].
- **Review.** MPS diffs the model and hides changes that carry no meaning [P27]. Here the canonical text of a module is already the review form.

### 2.4 Tables and decision tables (DMN, spreadsheets)

- **Efficient at** many rules of one shape. A decision table with a hit policy has a decidable completeness and overlap check [P28][S, C1]. Readability evidence is mixed: better than trees and rules in one study, worse in an older one [S, U, H1][S, W1].
- **Inefficient at** anything with a flow: formulas and dependencies sit behind the cells, the textbook case of hidden dependencies [P29].
- **Keyboard and screen reader.** The grid pattern is the best supported of all [P30].
- **For this language:** the row-shaped parts (constant rows, signal rules, bindings, lists of assignments and aggregates) are tables. A projection is not.

### 2.5 Step pipelines (Power Query, OpenRefine, Wrangler, Tableau Prep)

- **Efficient at** understanding: every step is named and selecting it shows the data at that point, on a sample [P31][P32]. Wrangler's study is the main evidence that previews with suggested steps reduce specification time against a spreadsheet [S, K1].
- **Inefficient at** non-linear structure. Power Query puts a join or a branch into a second query that the first refers to, and added a diagram view of queries as nodes to show those references [P33][P34]. Tableau Prep draws the flow as a graph for the same reason [P35].
- **Error prevention.** Errors are attached to the step that causes them, apart from errors in single values [P36].
- **Review.** The text behind the steps (M code, operation history as JSON) is the review and replay form [P37][P38].
- **For this language:** the unit is the named row set, which is a chain of one or two steps. The pipeline is the natural editor of one row set, and it needs a list of row sets with their references beside it, which is the Power Query arrangement.

### 2.6 Query by example on the data

- **Relational origin.** Zloof's Query by Example: fill example values into skeleton tables; an example element is a variable, a constant is a condition [U, Z1]. Non-programmers learned it in about a third of the training time of a conventional query language [S, U, Z2]; later comparisons with SQL found an advantage on paper and none online [S, U, Z3].
- **On graphs.** Exemplar queries return the structures congruent to an example: the shape is kept and the identities change [P39]. GQBE infers a query graph from one tuple of example entities: example nodes become variables, edge labels become constraints, rare labels are preferred, more examples sharpen it [P40]. VISAGE lets a pattern range from a concrete example over wildcards to pure structure [S, U, Q1]. Neo4j Bloom builds a pattern step by step and offers only relationship types that exist for the chosen label [P41].
- **Limit.** Finding a query that explains given examples is intractable in general for conjunctive queries [P42]. No study was found that measures the errors users make when a selection is generalised into a pattern.
- **For this language** the generalisation can be fixed and deterministic, because the data graph is typed: each selected entity becomes a pattern node of its type, each relationship between selected entities a pattern edge of its type, and no property value is kept unless the user asks.

### 2.7 Direct manipulation with the program inferred

- **What works.** A readable form of the inferred program beside the result, alternatives listed or the user asked about their own data (both reduced errors at no cost in time) [S, M1]; a default generalisation that the user edits afterwards, not questions during the demonstration [P43]; suggested steps with a preview of their effect [S, K1].
- **What does not.** Users were uncomfortable handing control to a predictor [P44]; many near-identical candidates could not be told apart [P45]; the field's own retrospective names usability and trust as the reason these systems were not adopted [P46].
- **For this language:** used only in the bounded form of 2.6. Inferring groups, joins or thresholds from an edited result is not attempted.

## 3. Cognitive dimensions

The dimensions are those of Green and Petre [G1]. Rated for this language, from the sources of section 2 and the counts of section 1: + helps, 0 neutral, - hurts.

| dimension | node and wire | blocks | text-like structure | step pipeline | example on data |
|---|---|---|---|---|---|
| viscosity (cost of a change) | - rewiring and layout | - drag per construct | + one statement | + one step | n/a (starts a rule) |
| visibility at 50 operators | - one screen at most, slots hidden | - three screens | 0 two screens | 0 one chain visible, list of the rest | + shown on the data |
| hidden dependencies | + wires drawn; - expressions in a panel | 0 by name | 0 by name | - other chains by name only | + pattern drawn on the instance |
| error-proneness | 0 typed ports | + shapes and holes | + holes | + per step | 0 over-specific pattern |
| premature commitment | - place, then connect | - top-down nesting | 0 holes allow any order | - order of steps | + none |
| progressive evaluation | 0 | 0 | 0 | + result after each step | + matches shown at once |
| diffuseness (space per construct) | - | - | + | 0 | + |
| hard mental operations | - following long wires | - deep nesting | 0 | 0 | 0 |
| secondary notation | + position, notes | 0 | 0 comments | 0 | 0 |
| closeness to the domain | 0 | 0 | 0 | + rows | + the estate itself |

## 4. Measurable criteria for this language

### 4.1 Actions to build the five reference projections

Method: the keystroke-level model of Card, Moran and Newell [KLM]: a keystroke K = 0.2 s (average skilled typist), pointing P = 1.1 s, a button press or release B = 0.1 s, homing between devices H = 0.4 s, mental preparation M = 1.35 s, one M per construct. Typing of names, values and expressions is the same in every editor and is left out; what differs is the structural work. Per construct [E]:

| construct | node, pointer | node, keyboard | blocks | text-like structure | step pipeline |
|---|---|---|---|---|---|
| add an operator | type to add, two homings: 3.15 | type to add: 2.35 | drag and snap: 3.65 | keyword and accept: 2.15 | keyword and accept: 2.15 |
| wire inside a chain | drag: 3.65 | wiring mode: 2.15 | 0 (the snap) | 0 (position) | 0 (position) |
| wire by name | drag: 3.65 | wiring mode: 2.15 | choose a name: 3.65 | complete a name: 2.15 | complete a name: 2.15 |
| slot entry | click a field: 2.75 | tab to a field: 1.95 | click a field: 2.75 | next hole: 1.55 | next field: 1.55 |
| pattern node or edge | 2.75 | 1.95 | drag and snap: 3.65 | next hole: 1.55 | pattern builder: 2.75 |
| expression node beyond the first | 0 (typed) | 0 (typed) | 0 if typed, 3.65 if a block | 0 (typed) | 0 (typed) |

Result in seconds of structural work [E]:

| | node, pointer | node, keyboard | blocks, typed expressions | blocks, jigsaw expressions | text-like structure | step pipeline |
|---|---|---|---|---|---|---|
| deployment health | 333 | 228 | 341 | 483 | 190 | 203 |
| service tree with lift | 242 | 167 | 249 | 314 | 141 | 146 |
| health roll-up | 602 | 412 | 603 | 768 | 337 | 361 |
| series with threshold | 176 | 121 | 168 | 187 | 95 | 98 |
| fixpoint closure | 95 | 65 | 94 | 109 | 53 | 56 |
| sum | 1,448 | 994 | 1,456 | 1,861 | 816 | 864 |

Reading: the keyboard matters more than the notation (the same graph editor is 31% cheaper by keyboard). Linear notations win because 20% of the wires cost nothing and a hole is cheaper than a panel field. Jigsaw expressions cost 28% over typed ones. A match taken from a selection on the data (one click per example node, one command) saves 64 s over the five projections against a pattern builder, and the typing of every type name.

The model prices an expert without errors. It does not price finding the right operator, which is where blocks and a palette help a beginner, or reading, which is 4.2.

### 4.2 What stays visible at 50 operators

Canonical text takes two lines per operator [R]. At 20 px per line and a 900 px high editing area (45 lines) [E]:

- text-like structure: 100 lines, 2.2 screens; every expression visible;
- blocks: about 1.4 times the height of text for the same content, 3 screens;
- node and wire: a card of 220 by 90 px with gaps needs about 33,600 px² per operator, 1.7 million for 50, against 1.44 million of a 1600 by 900 area: 50 cards do not fit at full size, and the slot contents are not on the cards;
- step pipeline: one chain (one to three steps with its result) and the list of all row sets (4 to 16 names in the reference projections) fit on one screen; other chains are a click away;
- example on data: shows a pattern, not a projection.

### 4.3 Hidden dependencies

Three kinds exist in the language: a row set read by a later statement (80% of wires), a column of an earlier step used in an expression, and a parameter of the projection used in an expression. Wires show the first kind and only in the graph. No notation shows the second; it needs the schema after each operator (section 7) to complete and to check field names in place. All notations can highlight definition and uses on selection, and the shared core provides that once.

### 4.4 Error prevention against the engine's checker

The engine's checker is the only authority; the editors must not re-implement it. What an editor can prevent before a patch is sent comes from the palette alone: the kind and direction of ports, one wire per input, no cycle, required slots, the child types of a slot, enumerated parameter values, where an aggregate is allowed. Everything else (types of expressions, roles of telemetry and data, names in scope) is a diagnostic from the engine, attached to the element it names. Holes make the difference between the families: with holes an unfinished statement is a valid edit state that the engine reports as incomplete, without holes it is a state the editor has to forbid or to fake.

### 4.5 Keyboard and screen reader

Requirements taken from WCAG 2.2 and the ARIA practices [P47][P48]: every function by keyboard (2.1.1); every drag with a single-pointer alternative, which a keyboard path does not replace (2.5.7); state not by colour alone (1.4.1); structure available to assistive technology (1.3.1); one tab stop per composite with arrow keys inside. Trees, tree grids and grids have defined behaviour; a free graph has none. Consequence for the designs: each editor is built on a tree or a grid, and the graph drawing is an additional rendering of the same tree with the same commands.

### 4.6 Diff and review of a change

The engine returns the canonical text of every changed module with each patch, and a draft is a list of patches. The review form is therefore the text diff plus the list of changed elements, the same for every editor, with each editor able to mark the changed elements in its own notation (the visual diff of Unreal, without a separate tool).

### 4.7 Rendering cost at 500 operators

Estimates of live elements, to be measured [E]:

- node and wire in SVG: about 10 elements per card and one path per wire, 5,500 elements at 500 operators unless only the cards in view are mounted; Node-RED's record of 10 frames per second at 800 nodes is the warning [P12];
- text-like structure: 1,000 lines; rows of constant height can be windowed, so about 60 lines are live at any size;
- blocks: nested boxes of varying height, 15 to 25 elements per block, about 10,000 at 500 operators; windowing is harder because heights depend on content;
- step pipeline: one chain and a windowed list, constant;
- example on data: the existing data view.

## 5. The five editors and the shared core

### 5.1 One core

Data: the definition graph of the draft, the palette, the diagnostics, the canonical texts, a selection. Calculations: a command (add an operator, wire, set a slot, set an expression, add a pattern element, remove, move) applied to the graph gives edits or a reason why not; validity comes from the palette; the inverse of applied edits gives undo. Actions: one draft service that sends a patch and returns the engine's answer, with requests in sequence. Each editor is a projection of the same graph to a tree of rows or cells plus a mapping from its gestures to commands. A change made in one editor is a change of the draft and shows in the others.

### 5.2 The editors

1. **Graph (node and wire).** Purpose: topology and review. Typed ports with validation while connecting, connect by click on two ports as the single-pointer alternative to the drag, a wire dropped on empty space opens the palette filtered to compatible operators, splice on a wire. Slots are shown on the card as text, edited in place. Its accessible form is the outline of statements with their inputs. Only cards in view are mounted.
2. **Blocks.** Purpose: discovery and error prevention for a newcomer; the answer to "like Scratch". Statements and steps are blocks that nest and snap; a slot is a hole shaped by what fits it; only what the palette allows can be dropped or picked. Expressions are blocks too, and typing into an expression hole parses the text into blocks, which removes the 28% penalty for those who type. Move mode by keyboard through the legal positions, as in Blockly. Built on a tree, in the DOM, with no free-floating blocks.
3. **Structure (looks like text).** Purpose: speed for the practised author. The canonical text, where every statement and slot is a cell; typing a keyword inserts a statement with holes, tab moves to the next hole, expressions are typed linearly and parsed, completion offers what the palette and the scope allow. Rows of constant height, windowed.
4. **Pipeline.** Purpose: understanding and debugging. The list of row sets with their readers on one side; the chosen row set as steps on the other, each with its diagnostics and the rows after it. Needs the engine's rows per operator (section 7); without them the step shows that the engine does not provide them.
5. **Example on data.** Purpose: starting a rule from what is seen. Select entities in the data view; the selection becomes a match with one pattern node per entity of its type and one pattern edge per relationship between them; the pattern is shown as text and on the graph at once, with the number of matches from a preview; a property value becomes a filter only on request. Then the pipeline continues the rule.

What each must pass: the five reference projections built through it give canonical text and preview digests equal to the originals, driven from one recorded sequence of commands per projection replayed through the gestures of each editor.

### 5.3 The sixth mode: a grammar graph

The user's main idea: a statement kind defined as a graph (its slots and its lowering to existing operators), edited with the same editors, rendered into a statement that a projection then uses. Note 08 gives the definition of such a statement declaration and shows that `lift` is one already, written in code. In the editors a declared statement is one more palette entry, and its declaration is itself a definition graph, so the block editor and the structure editor edit it without a new mechanism: the slots are a table, the lowering is a projection fragment with the slots as holes. The engine has no statement declarations; until it has, the expansion runs in a marked local stand-in that rewrites a use of the statement into its lowering before the patch is sent.

## 6. What is not built and why

- **A table editor for projections.** Tables fit rows of one shape; a projection is a flow. Tables are used inside the editors for constant rows, assignments, aggregates and bindings.
- **Inference from an edited result.** Intractable in general [P42], ambiguous in practice [P45], and not trusted without the program in view [P46]. The language already has lens rules for edits of a view, which is a different question.
- **A separate diff tool.** The canonical text and the patch list are the diff (4.6).
- **Blockly, a node graph library or another component kit.** The editors here are trees with commands; a second rendering stack with its own state, accessibility tree and styling would duplicate the core. To be reconsidered if a measured number says otherwise.

## 7. What the engine must provide

| need | today | used by |
|---|---|---|
| rows after each operator in a draft preview (a sample with the row schema and the count) | preview returns results per output graph only | pipeline, example on data |
| reference slots of an operator in the palette (type of an emit, edge type of a traverse, the relation of a fixpoint) | not in the palette; the existing editor keeps its own table | every editor, for validity from the palette alone |
| the row schema after each operator (column, type, role) with a check | not returned | completion and checking of field names in every editor |
| statement declarations | not built (note 08) | the sixth mode |

## 8. How the built editors will be measured

- Use: the recorded command sequence of each reference projection is replayed through each editor's gestures; counted are key presses, pointer actions and device changes, priced with the model of 4.1 and reported beside the estimates.
- Rendering: live elements and time of the first render and of one edit at 50 and at 500 operators, in a browser, for each editor.
- Accessibility: automated rule checks per editor, and a keyboard-only replay of one reference projection per editor.
- Equality: canonical text and preview digests against the originals, per editor and per projection.

## 9. What was built and measured (12 November 2026)

Tags as above; **[T]** is a result of an automated test in the repository, **[B]** an observation in a browser.

### 9.1 What exists

| part | what it is | lines |
|---|---|---|
| shared core (`canvas/src/editing`) | the read model (a projection as row sets, steps, slots and entries, built from the definition graph and the palette, no operator kind named), an expression printer that is the inverse of the parser, the gestures (add, remove and move a step, rename, connect, add an entry, a match from picked data) composed into one patch on a scratch copy, the draft interface; in it also the pattern from an example (81) and the statement kinds (265) | 1,081 |
| shared view parts (`canvas/src/editors`) | a projection as rows, each with what it can become, as data; the inline editor of a row; the windowing calculation | 327 |
| structure editor | the outline of the whole projection, close to the canonical text, keyboard first | 124 |
| step pipeline | one row set at a time, a card per step | 105 |
| blocks | a palette by role, a stack per row set, a block is picked up and put down on the places that take it | 155 |
| node and wire | cards and wires laid out by data flow, a wire by two clicks, the selected card edited in a side panel | 254 |
| by example | elements picked from the data, the pattern they are an example of, conditions on request | 130 |
| statement kinds panel (section 9.6) | make a kind, give it parameters, open its template, expand a use | 72 |
| workbench | the switch between notations and the host on a draft | 93 |

2,351 lines in all, with 1,498 lines of tests.

All five notations take the same properties (a document, the palette, a selection, a way to emit a gesture) and none holds draft state, so they edit one draft and a change made in one shows in the others [T].

### 9.2 Differences from the design of section 5

- **Blocks.** Expressions are typed text in a hole, not blocks: section 4.1 prices jigsaw expressions 28% above typed ones and 70% of the expressions are one reference or literal. A drag is the same pick-up and put-down as the click and the key.
- **Node and wire.** No splice on a wire and no free positions: the layout is a calculation, so there is nothing to arrange or store. A step is added from an output (the list of kinds that can read it) or in the side panel.
- **Structure.** Rows are edited in place with Enter; there is no typed-keyword insertion and no completion of field names (that needs the row schema per operator, section 7).
- **Pipeline.** The engine returns no rows per step, and the editor says so in place of the table.
- **By example.** The count of matches is not shown (it needs a preview per pattern). The example grows along the relationships of what is picked, which section 5 did not have.

### 9.3 Consistency

- The same piece of work (a new row set that reads another, a condition, a second step, a rename, a changed condition, a step added and removed) done through the interface of the structure editor, the pipeline, the blocks and the node-and-wire editor leaves the definition graph that the gestures leave when performed directly, element for element [T].
- On the catalog a current engine serves for the synthetic estate (14 projections, among them the deployment health, service tree with lift and health roll-up of section 1): a step added at every position of every row set and removed again restores the graph (249 row sets); every expression prints as text the parser reads; every projection, deleted and built again from nothing with the gestures the editors emit, reads as the original (12 compared in full; two hold constant rows, for which the editors have no gesture yet) [T].
- Every notation renders every one of the 14 projections with no finding of the automated accessibility rules (56 cases) [T]. Colour contrast is not covered by that check.
- On a live engine (built from the working tree, serving the synthetic estate, state digest `sha256:815751aa…`), the ten recorded command sequences were replayed through the gesture layer, each as one patch. All ten are accepted with no diagnostics beyond those a sequence allows. The four that rebuild an existing projection print the canonical text of the original, and the preview of the rebuilt graph has the digest of the original: deployment health `sha256:ed8be69e6c98` (3 entities, 2 relationships), health roll-up `sha256:852d33ba2d03` (40, 45), service tree with lift `sha256:83b1337105cc` (4, 5), capability lift `sha256:5ace9c2246cc` (4, 4). The six that build a new projection (fixpoint closure, series with threshold, link, retain, union, constant rows) preview with the data their sequence requires [T, with an engine]. Sent command by command the same replay is correct but slow: once a projection is whole the engine evaluates after every patch, about 5 s each on that build.

### 9.4 Use, measured

The work of 9.3, counted on the built editors: keys pressed, pointer clicks, and changes sent to the draft. A choice from a list counts as two clicks in every notation [T].

| | keys | clicks | changes of the draft | priced with K = 0.2 s and a click at 1.3 s (section 4.1) |
|---|---|---|---|---|
| structure | 38 | 8 | 8 | 18.0 s |
| step pipeline | 30 | 17 | 8 | 28.1 s |
| blocks | 31 | 14 | 9 | 24.4 s |
| node and wire | 31 | 17 | 8 | 28.3 s |

The order agrees with the estimate of 4.1 (structure cheapest, pointer notations dearest). The counts are for one small task on one projection; the replay of the five reference projections through each interface, which section 8 planned, is not done (9.7).

### 9.5 Rendering, measured

Elements in the page and time in a test DOM (not a browser), for the demo's service projection grown to about 50 and about 500 operators, in a view of 1,200 by 700 [T]:

| | elements at 51 operators | elements at 501 | first render at 501 | one change to the page at 501 |
|---|---|---|---|---|
| structure | 367 | 367 | 42 ms | 13 ms |
| step pipeline | 346 | 1,156 | 70 ms | 10 ms |
| blocks | 527 | 527 | 40 ms | 11 ms |
| node and wire | 167 | 220 | 33 ms | 8 ms |

In a browser, at 501 operators in a window of 1,440 by 900, development build [B]: changing to a notation takes 73 ms for the structure editor (428 elements in the page), 89 ms for the pipeline (1,179), 69 ms for the blocks (554), 60 ms for node and wire (446). The browser showed what the test DOM could not: before the page is laid out a view has no size, and the first render mounted everything (11,493 elements and 4.4 s for the structure editor). The editors now assume a usual window until the page reports its size.

Against the estimates of 4.7: the node graph holds 220 elements where 5,500 were feared, the blocks 527 where 10,000, because only what is in view is mounted in both. The pipeline grows with the number of row sets (its list is not windowed). One cost was found by this measurement and removed: the options of every choice were computed for every row at once, which made the first render at 501 operators 380 ms; they are now computed when a choice is opened. Each change copies the draft graph once to compose on; that copy is in the 8 to 13 ms. 
### 9.6 The sixth mode: statement kinds, on a local stand-in

What the user asked for: a graph that defines a piece of the language, a renderer that turns it into something a projection can use, and its use. Built as follows.

- **The grammar graph.** A statement kind is a declaration node, its parameter nodes (each points at the place of the template it fills: an expression slot or a parameter of a step) and a template. The template is an ordinary projection: its input ports are what a use connects, its row set `result` is what a use gives. So the five editors edit a template with no new mechanism [T].
- **Made from an example.** "Make a kind" takes a row set of a projection: its steps become the template, what it read becomes the ports, and one use of the kind stands where the steps stood.
- **Rendered.** The palette is computed from the declarations: one entry per kind with the template's ports, and the parameters as slots and fields. Every notation then offers the kind like any operator, because all of them are driven by the palette alone [T].
- **Used.** A use is a step. Before a change is sent on, every use is replaced by a copy of its template with the values the use gives its parameters, so the engine receives kernel operators only. For every one of the 249 row sets of the estate catalog: made a kind and used once, it lowers to exactly the operators it was made from [T]. A use with its own value for a parameter lowers to the template with that value; a change of the template reaches every use that does not say otherwise; a kind that would use itself is refused; a diagnostic of a lowered operator is reported at the use [T].

**This is a stand-in and is marked as one in the interface.** Declarations, templates and uses live in the browser's memory; a reload shows the lowered operators. For it to be real the engine has to provide:

1. node types for a statement declaration, its parameters (with the place each fills) and a use, in the kernel of the catalog, so a draft can hold them and a commit keeps them;
2. the declared kinds in the palette, with ports, parameters and slots;
3. the skeleton of a use;
4. lowering at check and at plan time, recording which use each operator came from, so that diagnostics, lineage and the plan name the use and its parameter;
5. a text form of a declaration and of a use, parsed and printed (a use has no canonical text today);
6. names a template brings in kept apart from the author's (the stand-in prefixes them with the name of the use, as `lift` does).

Parameter sorts in the stand-in are two of the eleven of note 08: an expression and a literal parameter of a step. Rows and graph inputs come from the template's ports. Repetition (`Each`), conditions (`When`) and queries over the catalog are not there.

### 9.7 Not done or not verified

- The editors are not in the application. They run on a page of their own over a draft in memory. Three additive changes to the application's editor state connect them (an action that carries a gesture, its effect, and the view); they were handed to the session that owns those files.
- The replay of the five reference projections through each notation's interface is done (9.10). The script that replays through the application is unchanged.
- Constant rows (`Values`) and the body of a `Fixpoint` cannot be edited in the new notations.
- No frame times or memory in a browser, no production build, no screen reader pass, no user study. The time estimates of 4.1 remain estimates.
- Three defects of the existing command code surfaced and were reported to its owner: an author's aggregate `n = count()` is taken for the skeleton's placeholder and removed when another aggregate is added; a pattern edge is given another id and an empty variable where the engine has none; two patches that differ only in the order of property keys are treated as different.

### 9.8 Addendum, 12 November 2026: in the application, three themes, names, the drawn graph

- **In the application.** The editor view shows a projection as its node canvas (the default) or as the five notations, on the same engine draft. A gesture is one editor action; it is composed on a copy of the draft and sent through the draft session, so it is one patch and one step of the 99 steps of undo [T]. On a live engine, in the application: a block put into a row set was accepted (no diagnostics, canonical text printed), the row set's steps took the names of the longer chain, Undo restored them, Redo was offered [B].
- **What the notations have of the existing editor's functions, and what not.** From the surrounding view, unchanged: undo and redo, the problems list, the text of the module, preview, releases and their diff, commit and discard, the properties panel on the selected step. From the shared core: problems per step and per row, every edit as one undo step. Missing in the notations: selecting several operators (so copy, paste, extract and the other refactorings start from the canvas), comments and groups, rules and their parts, bindings and parameters of the projection, a new projection, constant rows, the body of a fixpoint. The node canvas stays until these are there.
- **The page showed an older catalog.** The reviewer saw `match`, `filter` and `series` where the definition has one `lift` statement: the page read a catalog snapshot from before `lift` was a statement, not lowered operators. It now reads the catalog a current engine serves, where the lift is one operator, and it is one step, block and card in every notation [T][B].
- **Themes.** The notations use only theme tokens and follow the light, dark and high contrast themes of the application. In real Chrome, the automated rules with color contrast pass on every notation and on four states an author gets into (a row being edited, a block in hand, a wire in hand, an element picked), in the three themes: 27 cases [T, in a browser]. That check found what the test DOM had not: buttons whose accessible name did not contain the text they show (WCAG 2.5.3). They are named by their text now, with what the row is as the description.
- **By example.** Elements are named as everywhere in the application, never by internal id. The data graph is drawn with the application's data canvas, and a click on an element picks it; the lists do the same without a pointer [T][B]. The drawing marks only the last pick.

- **Placeholders.** The defect named in 9.7 (an author's aggregate `n = count()` taken for the skeleton's placeholder and removed by the next aggregate) is closed. The engine now names the children a skeleton creates only so that an operator prints; the draft remembers those ids, the first real entry of a slot replaces exactly them, and nothing else is ever taken for a placeholder. In the notations a slot that holds only a placeholder reads as an empty slot that is needed. The ids follow a step when its row set is renamed or grows, and an author's entry equal to the placeholder is the author's from then on [T]. On a live engine: a new roll-up held the named placeholder, the first aggregate replaced it, and `n = count()` written by hand stayed when a second aggregate was added [T, with an engine]. The live run also found that the client refused every skeleton answer carrying the new key; its owner fixed that the same hour.
- **New parameters of the kernel.** When the kernel gained `ttl`, `fill` and the `nodata` parameters, the notations showed them without a change. Two gaps showed with them and are closed: an optional parameter that is not set had no row and could not be set (it is now a place to add it), and a parameter on an expression entry was left out (it is now said after the entry) [T].

### 9.9 The stand-in for statement kinds against note 08

| | note 08 | the stand-in |
|---|---|---|
| declaration | `StatementDecl` with keyword, category, a `shape` (the production), `requires`, a `card`, and what it `realizes` | a name and its parameters; no shape, no checks before expansion, no card |
| template | a slot of the declaration holding operators, with `Hole`, `Fresh`, `When`, `Each`, `Query` | an ordinary projection the declaration points at; its input ports are the rows and graph parameters, its row set `result` is the output; no conditions, repetition or queries; names it brings in are prefixed with the name of the use |
| parameters | twelve sorts, required, repeated, default, the graph a type is resolved in | two sorts (an expression, a literal parameter of a step); the default is what stands in the template; the parameter points at the place it fills, where note 08 has the place point at the parameter |
| use | `StatementUse` with an edge to its declaration and one `ParamValue` per argument | a node whose type is the name of the kind, with the template's ports; an argument is an expression child or a property named after the parameter |
| text | one clause shape for every derived statement, parsed and printed | none |
| provenance | edges from each generated node to the use and the parameter | a map from operator to use, in memory; not per parameter |
| where it lives | the catalog keeps the use, never the expansion | the same for the author; the engine receives only the expansion |

### 9.10 The five reference projections, built through each notation and checked by the engine

Method. For each reference projection an empty twin is made, and the twin is filled in through the interface of one notation by the actions an author takes there: a row set started from the form or from the palette, each step appended, each read, parameter, reference and entry put in through its row. A projection that the walkthrough builds new (series with threshold, fixpoint closure) is first built by its recorded commands, and that result is what the notation must reach. The edits are sent to a live engine as one patch (the engine built from the working tree, kernel 3.19, the synthetic estate), which checks the draft, prints the canonical text and previews both graphs [T, with an engine]. The same builds run without an engine against what the original says (every step, read, field, reference and entry); the two small ones run with every test run [T].

| projection | notation | accepted | canonical text | preview digest | keys | clicks |
|---|---|---|---|---|---|---|
| deployment health | structure | yes | equal | equal (3 entities, 2 relationships) | 1,005 | 124 |
| | steps | yes | equal | equal | 897 | 232 |
| | blocks | yes | equal | equal | 889 | 226 |
| | wires | yes | equal | equal | 897 | 237 |
| service tree with lift | structure | yes | equal | equal (4, 5) | 787 | 120 |
| | steps | yes | equal | equal | 707 | 200 |
| | blocks | yes | equal | equal | 664 | 192 |
| | wires | yes | equal | equal | 707 | 202 |
| health roll-up | structure | yes | equal | equal (40, 45) | 1,765 | 280 |
| | steps | yes | equal | equal | 1,578 | 467 |
| | blocks | yes | equal | equal | 1,499 | 444 |
| | wires | yes | equal | equal | 1,578 | 482 |
| series with threshold | structure | yes | equal | equal (4, 0) | 394 | 66 |
| | steps | yes | equal | equal | 340 | 120 |
| | blocks | yes | equal | equal | 330 | 114 |
| | wires | yes | equal | equal | 340 | 126 |
| fixpoint closure | structure | yes | equal | equal (0, 26) | 161 | 36 |
| | steps | yes | equal | equal | 132 | 65 |
| | blocks | yes | equal | equal | 129 | 62 |
| | wires | yes | equal | equal | 132 | 68 |

Sums over the five, and the same priced with K = 0.2 s and a click at 1.3 s (section 4.1, with no M): structure 4,112 keys and 626 clicks (1,636 s), steps 3,654 and 1,084 (2,140 s), blocks 3,511 and 1,038 (2,052 s), wires 3,654 and 1,115 (2,180 s). These counts include the typing of names, values and expressions, which the estimate of 4.1 left out because it is the same everywhere; the typing is most of the keys. The order agrees with the estimate: the structure editor is the cheapest because it is reached by keys, and the three that are reached by pointer cost about a third more, close to one another. The blocks need about 4% more changes of the draft than the others, because a new row set is put down under a provisional name and renamed.

What the replay found and what was changed for it:

- An expression printed a decimal literal without its point (`0` for `0.0`), and the printed text typed back became an integer. The engine refused the lift built that way ("If branches differ: Decimal and Integer"). A decimal now prints with its point. A test types every expression of the estate catalog back as the text it prints and finds no change.
- A sink is named by its position among the sinks (`emit.3`), so a sink made in another order gets another number. The comparison reads sinks by position, as the engine does.
- A fixpoint refers to a row set by the id of one of its steps, and a lowered row set names its steps by position; the build reads ids by position.

### 9.11 Not done

- The notations do not yet have everything of the node canvas (9.8), so both stay in the editor view.
- A screen reader pass, frame times in a browser, and a user study are not done.

### 9.12 The same on the workload model (12 November 2026)

The engine replaced the example health model by a workload model: two projections, the structure of one workload (what it is made of, what it talks to, what each part depends on and how that was found) and its health (an abstract component type with subtypes, a lift, many joins, unions and distinct steps, series with a fill and a time to live, thresholds with parameters from the rows and a "no data" state, a traverse). The catalog fixtures were written again from it, and the walkthroughs that read the removed modules moved: the health roll-up is replaced by the workload health (140 operators, 828 commands, made from the definition in the catalog), and the six that each exercise one operator are projections of the workload module now.

All twenty builds are accepted by the engine, print the canonical text of the original and preview to the same digest [T, with an engine]:

| projection | structure | steps | blocks | wires |
|---|---|---|---|---|
| deployment health (2 entities, 1 relationship) | 1,005 keys, 124 clicks | 897, 232 | 889, 226 | 897, 237 |
| service tree with lift (4, 4) | 787, 120 | 707, 200 | 664, 192 | 707, 202 |
| workload health (55, 66) | 6,113, 1,012 | 5,352, 1,773 | 5,111, 1,711 | 5,352, 1,813 |
| series with threshold (4, 0) | 424, 72 | 367, 129 | 357, 123 | 367, 135 |
| fixpoint closure (0, 28) | 210, 64 | 161, 113 | 162, 108 | 161, 120 |

Sums, priced as in 9.10: structure 8,539 keys and 1,392 clicks (3,517 s), steps 7,484 and 2,447 (4,678 s), blocks 7,183 and 2,360 (4,505 s), wires 7,484 and 2,507 (4,756 s). The three notations that are reached by pointer cost 28% to 35% more than the structure editor, as on the old model.

What the move found:

- A property to set on an emitted entity was offered only from the type itself, not from the types it extends; a workload service could not be given the key every component has. The choice now follows the supertypes.
- A fixpoint relation must be plain rows: the closure of the roll-up edge carries keys, and the two ends of each edge are found again by key.
- The previews of three walkthroughs hold fewer elements than before (the deployment health 2 entities and 1 relationship where it had 3 and 2), because the estate's data changed with the model; their floors were lowered, text and digests are equal.

### 9.13 The engine's answers per operator (engine 3.21)

Three of the four needs of section 7 are now answered by the engine, and the notations use them [T][B]:

- **Rows after each step.** The Steps notation runs the projection on the space's data on request and shows under each step its rows: how many there are, the first twenty, and whether that is all of them. An element is shown by its name, never by its id: by the name the match read from it, else by the name the data the workspace holds gives it. A point in time is shown as a date. A statement that the engine plans as several operators (a lift) shows the rows of its own output. The rows of an earlier version of the draft are marked as such. On the service tree, the lift shows its 24 rows per window and service pair, named `web` and `api`.
- **Columns after each step.** Every step in the Steps notation says what it gives (`src: infra.Service, windowStart: Instant, drops: Integer (telemetry), ...`), from the engine's check of the draft, before anything runs.
- **References in the palette.** Which references an operator has, whether each is required and where its target is found come from the palette. A choice now offers only what the scope allows: the types of the output ontology for what an emit creates, the types of the graph a step reads for what a lift or a traversal follows. On every projection of the catalog the target each reference has is among its choices. The editor's own table remains for the wording, and for the one kind of definition an emit points at where the palette allows two.

Still open: completion of field names while an expression is typed (the columns are known now, the typing is not yet guided by them), and the number of matches of a pattern in By example (a preview per pattern).

## References

- [G1] T. R. G. Green, M. Petre. Usability analysis of visual programming environments: a cognitive dimensions framework. Journal of Visual Languages and Computing 7(2), 131-174, 1996. doi:10.1006/jvlc.1996.0009. Times as summarised at https://neverworkintheory.org/2012/06/25/usability-analysis-of-visual-programming-environments-a-cognitive-dimensions-framework.html
- [G2] T. R. G. Green, M. Petre. When visual programs are harder to read than textual programs. ECCE-6, 167-180, 1992.
- [KLM] S. K. Card, T. P. Moran, A. Newell. The keystroke-level model for user performance time with interactive systems. Communications of the ACM 23(7), 396-410, 1980. Button time from D. Kieras, Using the keystroke-level model to estimate execution times, 2001.
- [P1] Epic Games. Coding in Unreal Engine: Blueprint vs. C++. https://dev.epicgames.com/documentation/unreal-engine/coding-in-unreal-engine-blueprint-vs-cplusplus
- [P2] Node-RED. Flow structure. https://nodered.org/docs/developing-flows/flow-structure
- [P3] M. Puckette. Max at seventeen. Computer Music Journal 26(4), 2002. https://msp.ucsd.edu/Publications/dartmouth-reprint.dir/
- [P4] Epic Games. Connecting nodes. https://dev.epicgames.com/documentation/en-us/unreal-engine/connecting-nodes-in-unreal-engine
- [P5] Node-RED. Working with messages. https://nodered.org/docs/user-guide/messages
- [P6] Epic Games. Placing nodes. https://dev.epicgames.com/documentation/unreal-engine/placing-nodes-in-unreal-engine
- [P7] Node-RED. Nodes; Wires. https://nodered.org/docs/user-guide/editor/workspace/nodes , https://nodered.org/docs/user-guide/editor/workspace/wires
- [P8] NI. Boost LabVIEW productivity with Quick Drop. https://www.ni.com/en/support/documentation/supplemental/08/boost-labview-productivity-with-quick-drop.html
- [P9] Node-RED. Keyboard shortcuts. https://nodered.org/docs/user-guide/editor/workspace/keyboard
- [P10] Epic Games. UE Diff Tool. https://dev.epicgames.com/documentation/en-us/unreal-engine/ue-diff-tool-in-unreal-engine
- [P11] Node-RED. Projects. https://nodered.org/docs/user-guide/projects/
- [P12] Node-RED issue 4563, 2024. https://github.com/node-red/node-red/issues/4563
- [P13] J. Maloney, M. Resnick, N. Rusk, B. Silverman, E. Eastmond. The Scratch programming language and environment. ACM Transactions on Computing Education 10(4), 2010. doi:10.1145/1868358.1868363 [U]
- [P14] Blockly. Connection checks. https://docs.blockly.com/guides/create-custom-blocks/inputs/connection-checks/
- [P15] Blockly. Block- vs. text-based languages; Block design. https://docs.blockly.com/guides/design/languages/ , https://docs.blockly.com/guides/design/blocks/
- [P16] Blockly. Best practices. https://docs.blockly.com/guides/app-integration/best-practices
- [P17] Blockly. Using Blockly with the keyboard. https://docs.blockly.com/guides/configure/keyboard-nav/
- [P18] Blockly. Screen reader; Accessibility principles. https://docs.blockly.com/guides/configure/screen-reader/ , https://docs.blockly.com/guides/design/accessibility/
- [P19] Blockly. Save and load. https://docs.blockly.com/guides/configure/serialization/
- [P20] N. Fraser, Blockly group, 2022. https://groups.google.com/g/blockly/c/7bLSB4zVo44
- [P21] C. Omar, I. Voysey, M. Hilton, J. Aldrich, M. Hammer. Hazelnut: a bidirectionally typed structure editor calculus. POPL 2017. https://arxiv.org/abs/1607.04180
- [P22] C. Omar, I. Voysey, R. Chugh, M. Hammer. Live functional programming with typed holes. POPL 2019. https://arxiv.org/abs/1805.00155
- [P23] Greenfoot. Frame-based editing. https://www.greenfoot.org/frames/
- [P24] JetBrains. MPS: transformation menu language. https://www.jetbrains.com/help/mps/2023.3/transformation-menu-language.html
- [P25] M. Voelter, T. Szabó, S. Lisson, B. Kolb, S. Erdweg, T. Berger. Efficient development of consistent projectional editors using grammar cells. SLE 2016.
- [P26] JetBrains. MPS: accessibility features. https://www.jetbrains.com/help/mps/accessibility.html
- [P27] JetBrains. MPS: differences viewer. https://www.jetbrains.com/help/mps/2022.3/differences-viewer.html
- [P28] Camunda. Decision table hit policy. https://docs.camunda.io/docs/components/modeler/dmn/decision-table-hit-policy/
- [P29] Microsoft. Display the relationships between formulas and cells. https://support.microsoft.com/en-us/excel/display-the-relationships-between-formulas-and-cells
- [P30] W3C. ARIA authoring practices: grid. https://www.w3.org/WAI/ARIA/apg/patterns/grid/
- [P31] Microsoft. Power Query: applied steps. https://learn.microsoft.com/en-us/power-query/applied-steps
- [P32] Microsoft. Power Query: data profiling tools. https://learn.microsoft.com/en-us/power-query/data-profiling-tools
- [P33] Microsoft. Power Query: merge queries overview. https://learn.microsoft.com/en-us/power-query/merge-queries-overview
- [P34] Microsoft. Power Query: diagram view. https://learn.microsoft.com/en-us/power-query/diagram-view
- [P35] Tableau. Aggregate, join, or union data. https://help.tableau.com/current/prep/en-us/prep_combine.htm
- [P36] Microsoft. Power Query: dealing with errors. https://learn.microsoft.com/en-us/power-query/dealing-with-errors
- [P37] Microsoft. Power Query user interface. https://learn.microsoft.com/en-us/power-query/power-query-ui
- [P38] OpenRefine. History. https://openrefine.org/docs/manual/running#history-undoredo
- [P39] D. Mottin, M. Lissandrini, Y. Velegrakis, T. Palpanas. Exemplar queries: give me an example of what you need. PVLDB 7(5), 2014. https://people.cs.aau.dk/~matteo/exemplar/
- [P40] N. Jayaram, A. Khan, C. Li, X. Yan, R. Elmasri. Querying knowledge graphs by example entity tuples. IEEE TKDE, 2015. https://arxiv.org/abs/1311.2100
- [P41] Neo4j. Graph pattern search. https://neo4j.com/docs/aura/preview/explore/explore-features/graph-pattern-search
- [P42] P. Barceló, M. Romero. The complexity of reverse engineering problems for conjunctive queries. ICDT 2017. https://arxiv.org/abs/1606.01206
- [P43] D. C. Halbert. SmallStar: programming by demonstration in the desktop metaphor. In Watch What I Do, MIT Press, 1993. http://acypher.com/wwid/Chapters/05SmallStar.html
- [P44] A. Cypher. Eager: programming repetitive tasks by demonstration. In Watch What I Do, MIT Press, 1993. http://acypher.com/wwid/Chapters/09Eager.html
- [P45] B. Hempel, J. Lubin, R. Chugh. Sketch-n-Sketch: output-directed programming for SVG. UIST 2019. https://arxiv.org/abs/1907.10699
- [P46] T. Lau. Why programming-by-demonstration systems fail. AI Magazine 30(4), 2009. doi:10.1609/aimag.v30i4.2262
- [P47] W3C. Web Content Accessibility Guidelines 2.2, 2024. https://www.w3.org/TR/WCAG22/ ; Understanding 2.5.7 Dragging movements. https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements.html
- [P48] W3C. ARIA authoring practices: tree view, treegrid, listbox. https://www.w3.org/WAI/ARIA/apg/patterns/treeview/ , https://www.w3.org/WAI/ARIA/apg/patterns/treegrid/ , https://www.w3.org/WAI/ARIA/apg/patterns/listbox/
- [A1] R. Mollik et al. An extension-based accessibility framework for making Blockly accessible to blind and low-vision users. 2026. https://arxiv.org/abs/2601.10688
- [B1] N. Ritschel, F. Fronchetti, R. Holmes, R. Garcia, D. C. Shepherd. Can guided decomposition help end-users write larger block-based programs? OOPSLA 2022. doi:10.1145/3563296
- [C1] D. Calvanese, M. Dumas, Ü. Laurson, F. M. Maggi, M. Montali, I. Teinemaa. Semantics and analysis of DMN decision tables. BPM 2016. https://arxiv.org/abs/1603.07466
- [D1] B. Hempel, J. Lubin, G. Lu, R. Chugh. Deuce: a lightweight user interface for structured editing. ICSE 2018. https://arxiv.org/abs/1707.00015
- [F1] T. W. Price, N. C. C. Brown, D. Lipovac, T. Barnes, M. Kölling. Evaluation of a frame-based programming editor. ICER 2016.
- [H1] J. Huysmans, K. Dejaeger, C. Mues, J. Vanthienen, B. Baesens. An empirical evaluation of the comprehensibility of decision table, tree and rule based predictive models. Decision Support Systems 51(1), 2011. [U]
- [K1] S. Kandel, A. Paepcke, J. Hellerstein, J. Heer. Wrangler: interactive visual specification of data transformation scripts. CHI 2011. https://idl.uw.edu/papers/wrangler
- [M1] M. Mayer et al. User interaction models for disambiguation in programming by example. UIST 2015. https://www.microsoft.com/en-us/research/publication/user-interaction-models-disambiguation-programming-example/
- [N1] W. Anderson et al. Node-Read: a visually accessible low-code software development extension. MODELS 2022 Companion. [U]
- [Q1] R. Pienta et al. VISAGE: interactive visual graph querying. AVI 2016. [U]
- [T1] D. Moon, A. Blinn, C. Omar. Gradual structure editing with obligations. VL/HCC 2023.
- [V1] M. Völter, J. Siegmund, T. Berger, B. Kolb. Towards user-friendly projectional editors. SLE 2014. [U]
- [V2] T. Berger, M. Völter, H. P. Jensen, T. Dangprasert, J. Siegmund. Efficiency of projectional editing: a controlled experiment. FSE 2016. [U]
- [W1] I. Vessey, R. Weber. Structured tools and conditional logic: an empirical investigation. Communications of the ACM 29(1), 1986.
- [X1] E. Schanzer, S. Bahram, S. Krishnamurthi. Accessible AST-based programming for visually-impaired programmers. SIGCSE 2019. [U]
- [Z1] M. M. Zloof. Query-by-Example: a data base language. IBM Systems Journal 16(4), 324-343, 1977. [U]
- [Z2] J. C. Thomas, J. D. Gould. A psychological study of Query by Example. AFIPS NCC 44, 439-445, 1975. [U]
- [Z3] M. Yen, R. Scamell. A human factors experimental comparison of SQL and QBE. IEEE Transactions on Software Engineering 19(4), 1993. [U]
