# 13. Rattle in Sattle: a puzzle game for projections

Design note, 13 November 2026. Research and design only; nothing is built. It asks what the Kusto Detective Agency does well when it teaches a query language, and sketches a small game for this repository in the same spirit: a timeline of graph puzzles, solved by writing a projection in KQL, that ends in a live phase where the input keeps changing and a correct projection keeps up.

Terms used here:

- **Projection**: a rule that derives a smaller graph from a larger one. Here it is written in KQL (Kusto Query Language) with the few additions of note 10 (`graph-match`, the steps `nodes`, `edges`, `extend nodes`, `identity()`, `telemetry()`, `hysteresis()`).
- **Revision**: one numbered version of a graph. Each change to the input makes a new revision of every graph it reaches.
- **Digest**: a short fingerprint (a hash) of a graph's whole content. Two graphs with the same digest have the same elements and properties.
- **Reference projection**: the puzzle author's own solution, kept hidden from the player.

## Summary

1. The Kusto Detective Agency teaches through a story told in letters, one concrete question per case, real data in the player's own free database, a short training page per case that names the tools, optional hints, and a single answer that is either right or not.
2. Rattle in Sattle keeps all of that and changes one thing: the answer is a graph, not a number. The player writes a projection; the game compares the graph it produces with the graph of a hidden reference projection, by digest.
3. The new part is the live phase. After a case is solved, its input graph keeps changing (animals arrive and leave, bonds form and break). The engine recomputes both graphs at every revision and compares the digests. A projection that states the rule keeps matching; one that only describes today's snapshot breaks at a named revision, and the game shows the first element where it went wrong.
4. Seventeen cases run from "keep the oxygen atoms" to "which station is to blame". Each adds one or two KQL ideas.
5. Most of it exists: the KQL front end, the engine, digests, ids derived from keys, replay. Missing: a generic puzzle source (today every source is a fixed adapter), a per-graph digest per revision in the output, `serve` with KQL projections, and the KQL editor of the canvas. Section 7 lists them.

## 1. What the Kusto Detective Agency does

Facts below come from Microsoft's announcement of Season 2 and from community write-ups (sources at the end). The live site is a script-only page and could not be read; nothing below comes from it.

- **What it is.** A free online game by the Azure Data Explorer team. Each case is a crime in an invented town; the player solves it by querying data with KQL. Season 1 ("New Shadows Over Digitown", 5 cases, 2022) had over 10,000 players by Microsoft's count. Season 2 ("Echoes of Deception", 10 cases) started on 24 May 2023 with a new case every two weeks. Later there were holiday challenges and "Call of the Cyber Duty".
- **Onboarding without friction.** The first step is a free Kusto cluster: a Microsoft account, no subscription, no credit card, one year. Season 2 also allowed a Fabric trial. The onboarding case is a warm-up a beginner solves with one `where` and one `count`.
- **Data comes to the player.** Each case gives a short script that creates the tables and ingests compressed files from public storage into the player's own cluster, in about 20 seconds to a minute. The data is already cut to the case.
- **Story in letters.** A case opens as a letter ("Dear Detective, ...") and ends in one plain question. The story gives a reason to care about the number.
- **Training per case.** Each case has a training section that names the functions the case needs and shows them on small examples: `getmonth` in case 1, `series_decompose_anomalies` in case 4, `parse`, `countif` and `sumif` in case 5, `extract_all` in case 6, S2 cells in case 7 (Season 2, per the write-ups). Microsoft's own advice in the Season 2 post: "familiarize yourself with the tools at your disposal".
- **Hints on request.** Hints are optional and escalate from a nudge to a near solution. Players report using them rarely, and finding the vague ones frustrating.
- **One answer, checked exactly.** The answer is a single value: a number, a name, an address, a word. Right or wrong, nothing in between. The checker never shows the query; any query that finds the value wins.
- **Difficulty rises steadily.** Early cases need `where`, `summarize`, `join`. Middle cases add time series and anomaly detection, IP ranges, parsing. Late cases add geo cells, user functions and riddles that go past KQL. Both seasons are rated at about 20 hours.
- **Badges and prizes.** Per-case badges on Credly (the pages no longer resolve), and prizes and merchandise in Season 2.

What makes it teach well, in short:

- **A question before a tool.** The player wants the answer first, then looks for the operator. The training page is there when the need arises.
- **Real-sized data.** Millions of rows make a careless query wrong or slow; the data teaches, not only the text.
- **Exact checking.** A right-or-wrong verdict with no partial credit forces the player to be precise. Off-by-one mistakes and duplicate rows are the traps.
- **Low setup cost, own environment.** The tools the player learns are the real ones, in their own cluster, and stay after the game.
- **Weak points to avoid.** Riddles that need outside knowledge, vague hints, and menus whose order does not match the order of play.

## 2. Rattle in Sattle: the idea

- **Same shape.** A letter, a graph, a question, a training page, optional hints, an exact check, a badge.
- **The answer is a graph.** The player writes a projection. The game evaluates it and the hidden reference projection on the same input and compares the two output graphs.
- **The live phase.** Once a case is solved, the input keeps changing for a while. Both graphs are recomputed at every revision. A match at every revision earns the "keeps up" mark of the case.
- **Why that is the right test.** The engine itself is incremental and deterministic: a projection gives the same graph whether its input arrives at once or change by change (batch invariance, chapter 6 of the paper). So the live phase does not test the engine. It tests whether the player wrote the rule or a description of the first snapshot: a filter on a name that happens to be the answer today, a count that is right only for today's species, a join that assumes no animal is ever reported twice.
- **The story.** The Rattle is the workshop's old machine that shakes when something in a graph is wrong. Each letter comes from someone in an invented town whose graph rattles. The player quiets it.

## 3. The timeline of cases

Each case names the story, the input graph, the question, the KQL it teaches, how the answer is checked, and what changes in the live phase. "Check by digest" means: the output graph of the player and of the reference projection have the same digest. Every case fixes the output types and the key of each output node in its letter, so the same answer gives the same ids (section 4).

### Chapter A: the chemistry bench (one input graph, small)

**0. Onboarding: the lab bench**
- Story: the chemistry teacher wants to know what is on the bench.
- Input: 200 `Atom` nodes (`symbol`: H, O, C, N; `tag`) and `Bond` edges.
- Question: make one `Atom` node for every atom, unchanged.
- KQL: `graph("bench") | graph-match (a) where labels(a) has "Atom"`, the step `nodes Atom on id = ...`, `project`.
- Check: digest. Live: atoms are added and removed.

**1. Keep the oxygen**
- Question: a graph of the oxygen atoms only.
- KQL: `where` inside `graph-match`, a filter after it.
- Live: new oxygen atoms appear; an oxygen atom is relabelled as an isotope `O18` (a `symbol` that starts with O but is not `O`). Traps `has "O"`.

**2. Weigh the atoms**
- Question: every atom with its mass, from a fixed table (H 1, C 12, N 14, O 16).
- KQL: `extend`, `case()`, computed columns.
- Live: a new element (sulphur) arrives; the letter said "unknown elements have mass 0". A `case` without a default fails here.

**3. Keep the bonds**
- Question: the oxygen atoms and the bonds between two oxygen atoms.
- KQL: an edge pattern `(a)-[b]->(c)`, the step `edges Bond from a to c = ...`.
- Live: bonds break and form between oxygen and other atoms.

**4. Make water (H2O)**
- Question: one `Water` node for every oxygen atom bonded to exactly two hydrogen atoms and nothing else, with `Contains` edges to its three atoms.
- KQL: `summarize count(), countif()` by the oxygen, `join`, `identity("water", o.tag)` to name the new node, two steps writing nodes and edges.
- Check: digest; the letter fixes "a water molecule is named by its oxygen's tag".
- Live: a hydrogen breaks away (the molecule disappears), a third hydrogen bonds (it is no longer water: H3O), a carbon bonds to the oxygen. A projection that tests "at least two hydrogens" fails on H3O at a named revision.

**5. Name every molecule** (hard; end of the chapter)
- Question: one `Molecule` node per connected group of atoms, with its formula (`H2O`, `CO2`, `CH4`), named by the smallest tag in the group.
- KQL: variable-length paths `(a)-[b*0..12]-(c)`, `summarize min()` as the group's name, `make_set` or a sorted count per symbol, `strcat`.
- Live: a bond in the middle of a chain breaks, and one molecule splits into two. A projection keyed on something other than the smallest tag renames molecules it should not touch. This is the case that shows the engine at its best: only the two halves are recomputed.

### Chapter B: Noah's ark (thousands of nodes)

**6. Two of each**
- Story: Noah has 5,000 animals on the quay and room for a pair of each species.
- Input: `Animal` nodes (`species`, `sex`, `tag`, `arrived`), 300 species, invented names only.
- Question: for every species, the female and the male with the smallest tag. Exactly two per species where both sexes exist, one otherwise.
- KQL: `summarize arg_min(tag, *) by species, sex`.
- Check: digest; also the element count, which is shown as a first clue (section 5).
- Live: animals arrive and leave all day. When a chosen animal leaves, the next smallest tag takes its place. A projection that took "the first two rows" or that used `take` fails at once.

**7. The census**
- Question: one `Species` node per species with `count`, `females`, `males`, and a `Member` edge to every animal.
- KQL: `summarize count(), countif()`, `extend nodes` to add properties to nodes another step made.
- Live: arrivals and departures; a species vanishes when its last animal leaves (its node must go too).

**8. Two ships, one manifest**
- Story: two ships report the same animals under different tags; a vet's note says which tags are the same animal.
- Input: two reports and `SameAs` edges between tags.
- Question: the census of case 7, counting each animal once.
- KQL: `union`, `distinct`, and one identity for two keys. The case introduces how the engine merges two names of one thing (a `link` step), with the warning that it is the only place where order of arrival could matter, and that the engine makes it not matter.
- Live: a late `SameAs` merges two animals already counted; counts drop by one.

**9. Who eats whom**
- Input: a food web, `Eats` edges between species.
- Question: for every species, the species it depends on at any distance, as `DependsOn` edges.
- KQL: variable-length paths `[e*1..8]`, `distinct`.
- Live: a species leaves the ark; every chain through it breaks. Shows how a single removal travels.

### Chapter C: time (the same graph, with readings)

**10. The library's last word**
- Input: `Book` nodes and loan events with a time.
- Question: every book with its latest status (on the shelf, lent, lost).
- KQL: `summarize arg_max(time, status) by book`.
- Live: events arrive out of order; a late event older than the current latest must change nothing.

**11. The beehive scales**
- Input: `Hive` nodes and a weight reading per hive per minute (telemetry).
- Question: the honey gained per hive per hour.
- KQL: `telemetry("apiary", 1h)`, `windowStart`, `summarize ... by hive, windowStart`, `rate()`.
- Live: readings keep arriving; a window closes and its value is fixed.

**12. The lighthouse that does not flicker**
- Input: wind speed readings per lighthouse.
- Question: a `storm` or `calm` state per lighthouse: storm after three windows above 60, calm after three windows below 40.
- KQL: `invoke hysteresis(...)`, thresholds with a gap between them.
- Live: gusty data that crosses 50 every window. A plain `iff(wind > 50, ...)` flickers; the reference does not.

**13. The silent beehive**
- Question: case 11, and a hive that sent nothing for five minutes is "no data", not zero.
- KQL: `telemetry_filled(...)`, the difference between silence and a zero reading.
- Live: one scale goes quiet, then comes back.

### Chapter D: graphs built on graphs

**14. The postman's rounds**
- Input: two graphs: `letters` (who writes to whom) and `town` (who lives on which street).
- Question: `Writes` edges between streets, with the number of letters.
- KQL: two `input` lines, `graph("letters")` and `graph("town")`, a `join` across them.
- Live: people move house; the letters follow them to the new street.

**15. The ark's own answer**
- Question: a projection that reads the player's own answer to case 7 as its input: the species at risk (fewer than two animals) and who eats them.
- KQL: a projection whose input is another projection; the graph space.
- Live: as case 7 changes, this one follows without being told.

**16. The rattling railway** (final case)
- Story: the Rattle itself is a small railway: stations, tracks, signals, invented names.
- Input: `Station`, `Track`, signal readings per track.
- Question: every station's state is the worst state over itself and every station it depends on, up to 16 tracks away; name the station to blame.
- KQL: all of it: `telemetry`, `hysteresis`, variable-length paths, `summarize max()`, `identity()`, several steps. It is the shape of the workload health model of this repository, on an invented world.
- Live: signals degrade and recover; a track is closed and the blame moves.

## 4. How an answer is checked without giving it away

- **Fixed output types.** Each case ships an output ontology (the node and edge types and their properties) and says in the letter how each new node is named (for example "a water molecule is named by its oxygen's tag", written `identity("water", tag)`). Ids in the engine are a hash of the identity key, so two projections that name the same thing the same way give the same id.
- **Digest per graph.** The engine already computes a digest of each graph's whole state. The player's graph and the reference graph are both in one space, reading the same input. Equal digests: solved.
- **What the player sees on a mismatch, in rising order.** The verdict; the element counts per type of both graphs ("you have 41 `Water`, the case expects 40"); after a second failed try, one wrong element: a node or edge that is missing, extra or has a wrong property, with its input lineage (which input rows it came from, already served by the engine). Never the reference text.
- **The reference projection stays hidden.** It lives in the case folder, not in the player's view. The repository is public, so anyone can read it; the game says so and asks the player not to look, like a solutions page.
- **Differential reference evaluator.** The engine has a second, simple evaluator (`Sattle.Reference`) that computes a graph from scratch, without increments. It checks the engine, not the player. The game can use it as a third opinion at the end of a live phase: player's graph incremental, player's graph from scratch, reference graph. If the first two differ, it is an engine bug, and the game says so instead of blaming the player.
- **Compile errors teach too.** The front end already refuses with a sentence that says what to do, for example that a step must set every required property, or that a column does not exist, with the line of the `.kql` file.

## 5. The live phase

- **What changes.** Each case folder has a change script: a generated list of input changes in time order (arrivals, departures, new bonds, late events, silences, merges). It is fixed per case and seed, so every player meets the same changes.
- **Detecting "keeps up".** After each revision of the input, compare the digest of the player's graph with the digest of the reference graph. The result is a strip of green and red revisions on the canvas timeline. The first red revision is the place to look; clicking it shows the wrong element as in section 4.
- **What makes a projection fall apart.** Hard-coded values, `take` or "first rows", a `case` with no default, counting with `>=` where the letter said exactly, keying a new node on a value that changes. None of these is an engine problem; they are rules that were true only of the first snapshot.
- **Batch invariance as a bonus check.** The same change script replayed one change at a time and in one block must give the same final digest. The engine guarantees it; showing it to the player makes the point of the paper visible.
- **Cost per change (later).** A second score: how many rows the engine touched per change. A projection that joins everything to everything still keeps up, but costs more. The engine does not report this per projection today; it is a later addition, not part of the first version.
- **Badges.** Per case: solved, kept up for the whole script, solved without hints. Stored locally in the browser; no accounts.

## 6. Where the data comes from

- **Generated, invented, small enough to read.** A generator script per chapter, seeded, writes the input and the change script: atoms and bonds by simple chemistry rules, animals with invented species names ("striped quoll", "blue marsh hare", made up where needed), books, hives, lighthouses, stations. No real data; the repository is public.
- **Sizes.** Chemistry 200 to 2,000 atoms, the ark 5,000 animals, the railway a few hundred stations with a reading per minute for a day. Large enough that a careless query is visibly wrong, small enough to run on a laptop in seconds.
- **Same format as today.** The generator writes capture files in the existing envelope format (`lab/CAPTURE-FORMAT.md`), so `replay` reads them.

## 7. What it needs, what exists, what is missing

What exists today:

- The KQL front end with `graph-match`, the steps, `identity()`, `telemetry()`, `telemetry_filled()`, `hysteresis()`, `arg_max`, `countif`, `sumif`, `dcount`, `rate()`, variable-length paths, and refusals that name the line and the fix.
- The engine: incremental, deterministic, ids derived from keys, a digest of the whole state, lineage per element, the reference evaluator.
- `replay --projections kql`: loads `.kql` projections from the definitions folder and prints element counts and the state digest.
- The canvas: graph view, timeline of revisions, highlighting one element across graphs.

What is missing, each named once:

- **A puzzle source.** Every source today is a fixed adapter for one system (Kubernetes, Hubble, Azure, Prometheus, authored rules). Puzzles need one generic source: rows that say "node of type T with key K and these properties" and "edge of type T from K1 to K2", and their removals. One adapter and one row contract module; this is the only new engine concept.
- **A per-graph digest per revision.** The engine computes it (each graph's state has a digest); `replay` prints only the digest of the whole space, once at the end. The game needs the digest of two named graphs after every revision.
- **`serve` with KQL projections.** `serve` refuses `--projections kql` today, because a draft prints a module back as `.sattle` text. The game can start on `replay` alone (a command-line game); the canvas version needs `serve`.
- **The KQL editor in the canvas.** Note 11 designs it; nothing is built. Without it the player edits a `.kql` file in their own editor and runs `replay`.
- **Operators a case may need that are not yet checked.** `arg_min`, `make_set`, `min()` in `summarize`, undirected patterns, `union` of two pattern results. Each case's reference projection is the test: if it does not compile, the case waits or is rewritten.
- **The game shell.** Letters, training pages, hints, the progress file, the timeline strip of green and red revisions. Static pages in the canvas, no server state.

Order of work, decided by Andre on 13 November 2026: planned, built only after the full KQL implementation is ready.

0. **Prerequisite: the full KQL implementation.** Ready means: every projection is written in KQL and the old `.sattle` projection text is retired; `serve` and drafts take KQL; the KQL editors of note 11 work in the canvas; and the operators the cases need compile and are proven (`arg_min`, `make_set`, `min()` in `summarize`, undirected patterns, `union` of two pattern results). Nothing of the game starts before.
1. The puzzle source and the per-graph digest line, then cases 0 to 6 as folders that `replay` runs. A command-line game with letters as Markdown.
2. The live phase on the command line: replay the change script and print the first revision where the digests differ.
3. The canvas shell, on the KQL editor of step 0.

## 8. Open questions

- Should the reference projection be in the public repository, or generated at play time from a hidden seed? Public is simpler and honest; the game asks players not to look.
- Should a case accept any output that is correct up to naming (a keyed comparison instead of a digest)? The digest is simpler and the letter fixes the names; a keyed comparison is friendlier but is a second checker.
- Is the generic puzzle source worth its cost beyond the game? It would also serve tests and examples that today need a full synthetic estate.

## Sources

- Microsoft Tech Community, "Introducing Kusto Detective Agency Season 2", 24 May 2023: https://techcommunity.microsoft.com/t5/azure-data-explorer-blog/introducing-kusto-detective-agency-season-2-bigger-better-and/ba-p/3829614
- Microsoft Learn, "Free Azure Data Explorer cluster": https://learn.microsoft.com/azure/data-explorer/start-for-free
- Microsoft Learn, KQL learning resources (lists the game): https://learn.microsoft.com/kusto/query/kql-learning-resources
- Straightforward SQL, "Kusto Detective Agency" series (seasons, case counts, ratings): https://straightforwardsql.com/posts/kusto-detective-agency-intro/ and https://straightforwardsql.com/posts/kda-eod-case-1/
- Parsiya, "Kusto Detective Agency: Echoes of Deception" (training per case, hints, functions per case, frustrations): https://parsiya.net/blog/2025-kda-echoes/
- pthoor, KDA Season 2 hints, case 1 (letter format, ingestion script): https://github.com/pthoor/KustoDetectiveAgencyHints-Season2/blob/main/Case_1.md
- The game itself: https://detective.kusto.io/ (script-only page, not readable by the tools used here)
