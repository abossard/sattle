# Ch. 9 Evolution, evidence and provenance

**Claim.** A definition edit is replayed and not patched in place, so its result is the one a fresh evaluation gives. Every fact has support and lineage. Observations are evidence: events are kept as graph elements for a bounded span of logical time.
**Status.** Built: the configuration edit as activation by full replay (Def. 9.4 with the whole input log retained, up to 40 M rows, and the carry policy `drop`): the candidate catalog replays the retained inputs on a fresh engine and emits only the final difference per graph as one patch, with no revision for a no-op. Also built: releases (list, diff, restore), bounded preview, lineage at the sinks (complete for row-level emits, with a reverse route), evidence elements with retention by the `Retain` operator (9.6). Tested on examples. Proved at gate 1a: lineage soundness in three parts (Theorem 9.7, restated). Not built: the per-element evidence history `retain N` (parsed and printed, not applied), the carry policy, rebuild as a separate mode, contributions and withdrawal with several supporting sources, provenance semirings. Deviations D18, D25, D27.
**Gate.** 1a (theory) for edit replay and provenance; 1b for evolving definitions without a rebuild. Lineage and evidence elements are early evidence for gate 2b (a state explained from the model).
Uses: Ch. 3 (commit records, cause, frontier), Ch. 4 (patches, net, signed deltas), Ch. 5 (projection definitions), Ch. 6 (denotation, T5, T7).
Used by: Ch. 13 (gaps P-d, P-f), Ch. 15.
Sources: [R01] findings 22, 23, 27, T13; [R03] sections 2.9, 6.4; ADR-012, 014, 020, 046, 064 to 082; ARCHITECTURE.md "Configuration edits and evidence"; `ADAPTERS.md` (contribution and withdrawal); [green2007].

## 9.1 Idea

Three things change over time, and each needs its own rule.
- **Definitions** (ontologies, projections, graph spaces) change. Every output revision pins the definitions it was computed under, and a change of a projection produces a new output graph by an explicit transition.
- **Inputs** accumulate. The engine retains a bounded evidence history per element, not an event archive.
- **Facts** have reasons. Support, contribution and lineage say which inputs and rules a fact owes its presence to.

## 9.2 Definitions

**Definition 9.1 (definition pinning).** The frontier `φ_r` of every commit records `cfg`, the revision of the catalog graph (Def. 2.1) that was in force (Def. 3.10). `ChangeCause.ConfigurationVersion` becomes this revision reference, so every output revision pins the exact definitions that produced it, and `G_r` can be recomputed from the pinned definitions and the recorded inputs (Theorem 9.6).

**Definition 9.2 (qualifying input, element history).** At commit `c_r` with cause inputs `I_r` and edit set `E_r`, an input in `I_r` *qualifies for element `k`* iff `k` is a key of an effective edit in `E_r`. An input that leaves graph data and structure unchanged qualifies for nothing, including when it changes telemetry (ADR-014, Theorem 7.1). The **history** `H_r(k)` is the last `N` qualifying inputs of `k`, in processing order, among commits since `k` last appeared (ADR-012, ADR-077, ADR-079), where `N` is the `retain` of the projection (ADR-020). When `k` disappears (an edit with `a = ⊥`), its history is removed (ADR-082), and a later element with the same key starts with an empty history. Histories are per projection, so a shared entity has separate histories in separate graphs (ADR-081).

**Definition 9.3 (retained set).** `R_r = ` the union over keys `k` of `H_r(k)`, each input once even if it appears in several histories (ADR-078), ordered by original processing order and not by source timestamps (ADR-071).

**Definition 9.4 (configuration edit and edit replay).** A configuration edit changes the definitions of graph `g` from catalog revision `c` to `c'`, thereby replacing projection `P` by `P'` (ADR-046, ADR-064). The engine performs:
1. pause ordinary processing of `g` and queue newly accepted inputs (ADR-070);
2. take the previous output graph `G = G_h` as the starting state (ADR-067);
3. replay `R_h` once, oldest to newest, through `P'` (ADR-068, ADR-071);
4. let the **carry policy** decide the fate of elements of `G` whose creating inputs fell out of `R_h` (ADR-080);
5. commit **one** patch, the net difference between `G` and the result (ADR-069), with `cfg = c'`; if the result equals `G` no commit and no revision are created (ADR-064);
6. keep the existing histories (ADR-072) and drain the queued inputs in ingestion order.

**Definition 9.5 (replay result).** Let `Eval(P', R)` be the batch denotation (Def. 6.4) of `P'` on the integrated state of the ordered inputs `R` from the empty state. The replay result is `Replay(P', G, R) = Eval(P', R) ⊕_π Carry_π(G, R)` where `Carry_π(G, R)` is the set of elements of `G` that are not produced by `Eval(P', R)` and that the policy `π ∈ {keep, drop}` of the changed projection retains, and `⊕` takes the union under the uniqueness rule, with `Eval` winning on equal keys. ADR-080 leaves the policy to the changed projection. It is a per-projection parameter of this paper, default `drop`.

**Definition 9.6 (rebuild mode).** For a projection whose inputs are all graph ports, the **rebuild** of `P'` is `⟦P'⟧(χ(G_in))`, the batch denotation on the integrated state of the pinned input graphs (their snapshot at the frontier). This is what a new projection initialized from available graphs computes (ARCHITECTURE.md).

**Definition 9.7 (support, contribution, withdrawal).** A fact of the output graph is produced by *fragments* (Def. 6.4), each carrying a weight from a contribution: an input `i`, a rule, and a weight. The **support** `σ(k)` is the sum of the weights. An element is present iff `σ(k) > 0` (and a create fragment exists). A contribution replaced or withdrawn by its source (a Kubernetes `DELETED`, a retracted authored row) is a negative weight for exactly that contribution, and the element remains iff other support remains (`ADAPTERS.md`: withdrawal removes the source's contribution, not all evidence for a canonical entity).

**Definition 9.8 (provenance semiring).** For a commutative semiring `(K, +, ·, 0, 1)`, the evaluation of the positive operators (`Match`, `Filter`, `Map`, `Join`, `Union`) on `K`-annotated inputs interprets join as `·` and union and projection as `+`. A `Group` is not one of them: the row it emits changes with the weights (Theorem 9.7(b)). `ℤ` (the Z-set weights of Ch. 4), `ℕ` (bag support, `σ`), `ℕ[X]` (polynomials over input ids) and the powerset semiring (sets of input ids) are instances. The **lineage** `L(f)` of a fact `f` is the set of input ids occurring in the `ℕ[X]` polynomial of `f`.

## 9.3 Theorems

**Theorem 9.1 (edit replay, T13).** The result of a configuration edit is a deterministic function of `(G, R_h, P', π)`: the committed patch is `toPatch(χ(Replay(P', G, R_h)) − χ(G), G)` and is empty iff `Replay(P', G, R_h) = G`.
Status: **proved**, given T7.

*Proof.* `R_h` is a function of the commit history (Theorem 9.4). `Eval` is a function of its ordered inputs (Theorem 6.3). Carry is a function of `G`, `R_h` and `π`. Step 5 applies Theorem 4.8. Queued inputs enter after the edit in ingestion order and do not affect the replay (ADR-070). ∎

**Theorem 9.2 (an edit is not recomputation from scratch, ADR-073).** In general `Replay(P', G, R_h) ≠ Eval(P', full input history)`.
Status: **proved** (counterexample), with a sufficient condition for equality.

*Proof.* Let `P` keep pods with `phase = "Running"` and let `P'` keep pods with `phase ≠ "Failed"`. Input `i₁` is a pod `p₁` in phase `Pending`. Under `P` it changes nothing, so it qualifies for no element and is not retained (Def. 9.2). Recomputation over the full history under `P'` creates `p₁`. Replay of `R_h` does not contain `i₁`, so `p₁` is absent (ADR-073: missed inputs do not acquire a past effect). *Sufficient condition:* if `R_h` is a *sufficient subset* for `P'`, that is `Eval(P', R_h) = Eval(P', full history)`, and the carry set is empty, then equality holds by definition. This is the case when `R_h` is the full history. It is also the case when `P'` keeps no state across inputs that a non-retained input could have contributed to (a non-retained input has no effect on its own, but it can be the partner of a later join, so "no effect" alone is not sufficient). ∎

**Theorem 9.3 (rebuild is exact for graph-input projections).** For a projection whose inputs are all graph ports, the rebuild of Def. 9.6 equals the output of a fresh projection `P'` started on the same input graphs, for any retention `N`. If `P' = P` (same digest) and `G = ⟦P⟧(χ(G_in))`, the committed patch is empty.
Status: **proved** (Theorems 6.1 and 6.3).

*Proof.* The input state is the graph itself, which is fully available at a pinned frontier, so no input is missing. The result is the denotation on the integrated state (Def. 6.4). A no-op edit gives the same denotation, hence the same graph, hence an empty net patch. ∎

*Consequence.* ADR-067 to ADR-071 specify replay of retained inputs for all projections. For graph-input views, rebuild is exact and has no ADR-073 limitation. The limitation is real only for ingestion projections whose inputs are rows that are no longer available. This paper proposes **rebuild for graph-input projections** and **replay for row-input projections** as a refinement of the ADRs ([R01] T13: state the non-theorem, then the exact cases). A no-op edit is not guaranteed to produce an empty patch under replay: bounded retention (eviction) or aggregate properties that depend on evicted inputs make `Eval(P, R_h) ⊕ Carry ≠ G` possible. A conformance test is required.

**Theorem 9.4 (evidence is a derived index of the log).** `H_r(k)` and `R_r` are functions of the commit records `c_1 … c_r` alone. They can be rebuilt by one scan of the log and need not be a source of truth.
Status: **proved**.

*Proof.* Def. 9.2 uses only the cause inputs and the edited keys of each commit, both recorded in `c_j` (Def. 3.8), in commit order. Removal is an edit with `a = ⊥`. ∎

The bytes of an input are not part of the evidence. They stay in the engine's input log under its own retention, referenced by input id. This replaces `RetainedInput.OriginalBytes` held per element (P-d of Ch. 13).

**Theorem 9.5 (support semantics, ADR-075, ADR-076).** After an edit, an element of `G` is removed iff no replayed input produces it (and the carry policy does not retain it), and it remains iff at least one replayed input still produces it.
Status: **proved**.

*Proof.* Present iff `σ(k) > 0` (Def. 9.7). `σ` is evaluated on `R_h` under `P'`. If two inputs produced `k` and only one still does, the support is `1` and `k` remains (ADR-076). If none does, `σ = 0` and `k` is removed unless carried (ADR-075). Supplying `G` first does not by itself retain `k` (Def. 9.5). ∎

**Theorem 9.6 (definitions are pinned, replay of history).** For every revision `r`, `G_r` is a function of the recorded input sequence up to the frontier `φ_r` and the definitions of the catalog at `φ_r.cfg`.
Status: **proved** (Theorem 6.3 and Def. 9.1).

**Theorem 9.7 (lineage soundness).** Let `S` be an integrated input state, a family of Z-sets of rows and facts, and `S|_L` its restriction to a set `L` of input rows.
- (a) *Plans of positive operators.* Let `Q` be a composition of `Match`, `Filter`, `Map`, `Join` and `Union`. For every output row `f`, `Q(S)(f) = Σ_m c_m · Π_{x ∈ m} S(x)`: a finite sum over multisets `m` of input rows, with coefficients `c_m ∈ ℕ` that depend on `Q` and `f` only. Let the lineage `L_S(f)` be the set of input rows that occur in a monomial that does not vanish at `S`. Then `Q(S|_L)(f) = Q(S)(f)` for every `L ⊇ L_S(f)`. In particular `f ∈ Q(S)` iff `f ∈ Q(S|_{L_S(f)})`.
- (b) *A per-key operator last.* Let `Q = h ∘ Q₀` with `Q₀` as in (a) and `h` a `Group` with any aggregates or a `Distinct`, and let `S` have no negative weight. For an output row `f` with key `κ` let `L_S(f)` be the union of `L_S(x)` over the rows `x` of `Q₀(S)` with key `κ`. Then `Q(S|_L)(f) = Q(S)(f)` for every `L ⊇ L_S(f)`.
- (c) *`Threshold`.* The row of a key at a frontier is a function of the closed windows of its current episode (Def. 7.10): removing the windows of the key outside that episode leaves the row unchanged.
Status: **proved**, restated. Read only, checked by no tool. The polynomial of (a) is the `ℕ[X]` provenance of [green2007]; the proof below is direct and does not use a theorem of that paper. The first form included `Group` among the positive operators with "the set of contributing rows" as lineage, and gave `Threshold` "the windows of the deciding run"; both are false, see the counterexamples. The theorem describes the design: the engine's lineage is the contributing rows of one projection step (D18), which is (a) or (b) for one step, and no engine behaviour depends on more.

*Proof.* (a) One application of each operator has the stated form: `Filter(a)(f) = [φ(f)] · a(f)`; `Map(a)(f) = Σ_{x : map(x) = f} a(x)`; `Union(a, b)(f) = a(f) + b(f)`; `Join(a, b)(f) = a(f_left) · b(f_right)` when the join condition holds on `f` and `0` otherwise; a node pattern of `Match` is a `Filter` on entity facts and an edge pattern a `Join` of a relationship fact with its endpoint facts, followed by a `Map` to the binding row (Def. 6.3). Sums of products with coefficients in `ℕ` are closed under substitution, so by induction on the plan `Q` has the form; the sums are finite because `S` has finite support. Let `L ⊇ L_S(f)`. A monomial that does not vanish at `S` has all its rows in `L_S(f) ⊆ L`, where `S|_L` agrees with `S`, so it keeps its value. A monomial that vanishes at `S` holds a row `x` with `S(x) = 0`, and `S|_L(x) = 0`, so it vanishes at `S|_L`. The two sums are equal. (b) Let `a = Q₀(S)` and `a' = Q₀(S|_L)`. The weight `h(a)(f)` depends only on `a` restricted to the rows with key `κ` (the per-key case of Theorem 6.1). For a row `x` of key `κ` with `a(x) ≠ 0`, `L ⊇ L_S(x)` and (a) give `a'(x) = a(x)`. For a row `y` of key `κ` with `a(y) = 0`: `S` and the coefficients are non-negative, so every monomial of `y` vanishes at `S`, hence at `S|_L`, and `a'(y) = 0`. So `a` and `a'` agree on the key, and `h` gives `f` the same weight. (c) By Def. 7.10 the state of a key is the fold over the windows of its current episode from `initial`, and the key has a row iff it has a window in the look-back. The current episode contains the newest window of the key. Removing earlier episodes changes neither the fold nor the newest window, and it opens no gap inside the episode, since an episode is delimited by the gaps between consecutive windows. ∎

*Counterexamples to the first form.*
- *A `Group` in the middle.* Rows `r₁ = (k₁)`, `r₂ = (k₂)`, `r₃ = (k₂)`. Plan: `g = S |> group by k count n`; `one = g |> where n == 1 |> select { tag: "x" }`; `two = S |> where row is r₂ |> select { tag: "x" }`; `f` is the row of `union one, two |> group by tag count m`. On `S`: `g = {(k₁, 1), (k₂, 2)}`, the union has `x` with weight 2, and `f = (x, 2)`. The contributing rows of `f` are `r₁` (through `one`) and `r₂` (through `two`). On `S` restricted to `{r₁, r₂}`: `g = {(k₁, 1), (k₂, 1)}`, the union has weight 3, and the output is `(x, 3)`. So `f` is not in the result on its own lineage. The reason: restricting the input of a `Group` creates rows that were not there, and an operator after it sees them. Statement (b) therefore asks for the per-key operator to come last.
- *`Threshold` and the deciding run.* A key enters `high` by three windows at or above `above`, stays `high` through later windows inside the band, and the three windows have left the look-back. Restricted to the three windows of the deciding run, the key has no window in the look-back and no row.
- *Negative weights in (b).* Rows `x₁` with weight `+1` and `x₂` with weight `−1` map to one row `y` of key `κ`, and `x₃` maps to `z` of key `κ`; `f = (κ, count 1)` and `L_S(f) = {x₃}`. For `L = {x₃, x₁} ⊇ L_S(f)` the count is 2.

**Proposition 9.8 (cost of evidence).** The evidence entries of a graph number at most `N · |dom G|`. Provenance polynomials are not stored. Lineage queries are answered by the evidence history (last `N` qualifying inputs per element) or, beyond it, by replay of the input log through the pinned definitions (Theorem 9.6).
Status: **proved** (Def. 9.2) for the bound. Lineage by replay is an **obligation** (O9.3).

## 9.4 Provenance record and read port

**Definition 9.9 (provenance record).** For each emitted fact the engine can answer, from the commit record and the evidence history: the inputs that caused its latest change (cause), the rule that governs the sink (`governs`, Ch. 5), the upstream facts (graph, key, revision) read through `gin` of the frontier, and the lineage of Def. 9.8 on demand. This resolves finding 22: the lineage claim of the story (correspondence `e50 ↔ e90`) is realized by the cause, the rule id and the pinned upstream revisions, and `Correspondence` stays the explicit link of `sync` rules (Ch. 8).
The history read port is `History(g, k)`: the ordered `H(k)` with, per entry, the input id, the revision of the commit and the rule. It is a function of the log (Theorem 9.4) and resolves finding 23.

## 9.5 Activation, releases and preview in the engine

**Definition 9.10 (release).** Every committed catalog revision that changes a module creates a release, identified by the digest of the module (Def. 2.8), with the digest it supersedes. `GET /releases` lists them oldest first. The facts of a release are immutable. The diff of two releases is a patch on the catalog graph whose before-states come from one release and after-states from the other. Ids are definition path ids (Def. 2.3), so a rename appears as a property change. **Restore** applies, to a draft, the patch from the draft's current facts of a module to the facts of a release (an ordinary draft patch, undoable by its inverse, Ch. 4, Prop. 4.10).

**Proposition 9.9 (releases).** (a) A restore of release `ρ` followed by a commit gives a module whose digest equals the digest of `ρ`. (b) The diff of releases `ρ₁, ρ₂` equals, on the facts of the module, `toPatch(χ(ρ₂) − χ(ρ₁), ρ₁)` (Def. 4.9, with `ρ₁` as the base).
Status: (a) **example test** (`Committing_a_draft_activates_it_and_every_release_is_listed_diffed_and_restorable`: the original digest comes back and the release list has three entries). (b) **proof sketch** from Theorem 4.8. The test checks one changed property.

**Definition 9.11 (bounded preview).** `Preview(D, n)` evaluates a draft `D` without committing it: a fresh engine with its own in-memory stores replays the first `n` retained input rows in order (default 500,000, hard cap 5,000,000) through the draft's definitions, and returns diagnostics, per graph counts, a digest and optionally snapshots and telemetry. The scope (`rows`, `totalRows`, `complete`, logical time) is stated in the answer. The diff against the live output is present iff `n` covers all retained rows (`complete`).

**Proposition 9.10 (preview).** (a) `Preview` changes nothing observable on the live engine: state digest, event sequence number and graph heads are unchanged. (b) A complete preview computes `Eval(P', R)` for the full retained input `R`, which is the result of the activation that a commit would perform if no input arrives in between (Theorem 6.3). (c) An incomplete preview is not comparable with the live output and carries no diff.
Status: (a) and (c) **example test** (`Preview_evaluates_a_draft_with_a_stated_scope_and_touches_nothing_live`); (b) **proof sketch**, untested. The test does not compare the diff of the preview with the patch of the commit. **Why it is not closed:** Part (b) is a property of the preview's scope that no test exercises. It closes with test T-PREVIEW ([inventory.md](inventory.md)).

**Engine semantics of the configuration edit.** Because the engine replays the full retained log on a fresh engine and diffs against the previous output, it computes `Eval(P', full history)`, which is the sufficient condition of Theorem 9.2, and the test `A_definition_edit_reactivates_the_space_and_emits_only_the_final_diff` checks that the result equals a fresh engine with the new definition fed the same inputs. The non-equivalence of ADR-073 does not arise as long as the whole log is retained. It arises again when retention is bounded (`retain N`). The engine's only bound is a cap of 40 M input rows per space (`InputRowCap`); what an edit does beyond that cap was not examined. Dependent graphs are recomputed by the same replay, so the engine does not distinguish replay for row inputs from rebuild for graph inputs (Def. 9.6). A no-op edit creates no revision (`A_no_op_definition_edit_creates_no_revision`).

**Lineage in the engine.** `GET /graphs/{g}/lineage?element=` answers, for an emitted element, the sink, the rule, the operator chain and its sources. For an element emitted row by row from a `Match` the answer is **complete**: the sources of every row that supports the element, paged, with the number of distinct sources and of supporting rows. A lifted edge (Ch. 5, 5.8) therefore names every edge of its fibre, both end nodes and both images; for an edge written with `lift` the sources carry the roles `edge`, `source`, `target`, `sourceMapped`, `targetMapped`, `sourceImage` and `targetImage`, and the sinks are named after the statement. For an element emitted from a `Group` without windows the answer is complete as well: it names every input row of the group, so an edge written by `group by` names every lower edge and node it is made of, paged, forward and reverse. The answer is partial (the elements of the group key only, `complete: false`) for an element emitted from a windowed `Group`, a `Rollup`, a `Threshold` or a `Series`, and for rows that a `select` reduced to scalar columns before the emit. The projections of the prototype emit nodes and edges row by row and put counts on top by an update, so their nodes and edges are complete. A reverse route, `GET /graphs/{g}/lineage/reverse?element=`, answers which elements of other graphs an element is part of, and names the graphs that read it through a windowed aggregate, where the answer is incomplete. `replay --out` writes both answers per graph as files. Tests (`LineageTests.cs`): a lifted capability edge names the service edge, the services and the capabilities it is made of; a group node names every service; an edge that stands for two parallel edges names both, and the pages give the same list; a lower element names the higher elements it is part of; an edge emitted from a group names both pod calls of each service dependency, the same elements as the row-level emit of the service tree, forward and reverse; an unknown element is a `404` in both directions; the files equal what the host serves. This is lineage at the sink for one projection step. It is not the `ℕ[X]` lineage of Theorem 9.7 across a stack, and a client follows it level by level.

## 9.6 Evidence elements

Two meanings of "evidence" are in this paper. The *evidence history* of Def. 9.2 is an index of retained inputs per element, internal to the engine and not built. An **evidence element** is a graph element that stands for one observed event and has an edge to what the event concerns. It is built, and it is ordinary graph data: it has an identity, a type, properties and revisions, and any projection can match it.

**Definition 9.12 (evidence element).** Let `ε` be a source event with a delivery key (Def. 10.2) and a subject element `x` of the master or of a derived graph. An evidence element for `ε` is an entity `e_ε` whose identity is derived from the delivery key (or from a stable id inside the payload), whose properties are the fields of the event that a rule chooses to keep, and a relationship `e_ε → x` (`Concerns`, `About`, `EvidenceFor`). A projection may also keep only the count of such events as telemetry on `x` (Ch. 7). The two forms are independent: the engine keeps both for Kubernetes warnings and Activity Log operations.

**Proposition 9.11 (evidence elements are idempotent and withdrawable).** Delivering `ε` again (same delivery key) leaves the graph unchanged (Theorem 10.1), and withdrawing the source record withdraws `e_ε` and its edge when no other contribution supports them (Theorem 10.2).
Status: **follows from** Theorems 10.1 and 10.2 for ingestion rules. No test of the engine delivers one evidence event twice. Withdrawal by expiry (Proposition 5.9) is tested; withdrawal by a source retraction is not.

What exists (`engine/src/Sattle.Runtime/Adapters.cs`, definitions `sattle.azure` and `sattle.infra`, `EvidenceAdapterTests.cs`, Ch. 10, 10.6 for the measured counts):
- `ActivityEvent` with `Concerns` to its Azure resource, from the management API backfill and from the Event Hubs diagnostic records. The resource id is grouped case-insensitively.
- `WarningEvent` with `About` to its pod, for Kubernetes events of type `Warning` only (the `Normal` events stay out).
- `AuditEvent` for completed AKS audit requests of category `kube-audit-admin`, with lease renewals left out by the rule. It has no relationship in the master graph of either capture.
- In the health graph an `Evidence` element with `EvidenceFor` to the component, for the operations on the cluster and its machines and the warnings of a service (Ch. 7, 7.5).

On the two real captures the evidence elements are 14,622 of 15,154 and 47,598 of 48,201 master entities at engine `ad83e63` (`paper/evidence/replay-head16-105738.txt`, `replay-head16-140542.txt`; before one activity event had one identity, Ch. 10, 10.6, they were 15,363 of 15,819 and 49,663 of 50,154, and the table below is from those runs; without the retention rule below they were 16,321 of 16,777 and 49,717 of 50,208). This is a consequence of keeping events as elements: the size of the master graph follows the event rate and not the size of the estate. The hot-graph figure of Ch. 11 (337 MB for 750,000 entities and 2.5 M relationships) is for a different mix and is not a statement about evidence elements.

**Retention.** Each of the three evidence rules of `acme.ops` reads its rows through `|> retain on time for 24h`, so an evidence element stays while the logical frontier is less than its event time plus 24 hours and is then retracted like any element whose support is gone (Def. 5.15, Proposition 5.9). Nothing in the engine knows about evidence: the age is text in the definition, the operator is generic, and the retained state is a function of the frontier, so it does not depend on the chunk size. What the age does to the master graph, with the same capture and only the age replaced in a copy of the definitions (`paper/evidence/retention-ages.txt`):

| Capture | Age | Master entities | Evidence elements |
|---|---|---|---|
| `20261110T105738Z` (5,679 s) | 24 h | 15,819 | 15,363 |
| | 10 min | 456 | 0 |
| | 2 min | 456 | 0 |
| `20261110T140542Z` (1,017 s) | 24 h | 50,154 | 49,663 |
| | 10 min | 503 | 12 |
| | 2 min | 491 | 0 |

At 24 h the rule removes 958 and 54 `ActivityEvent` elements and nothing else, so almost all evidence of these short captures is within 24 hours of the frontier and the master is nearly as large as it would be without a rule; the rule bounds the graph at about the event rate times the age. The capture rate of audit events is 42,244 in 1,017 s on the second capture. If that rate held for 24 hours the retained set would be about 3.6 M `AuditEvent` elements, which is more than the 750,000 entities of the H9 budget (arithmetic on one short capture, not measured, assumes a constant rate). The age is a parameter of the definition, and 24 h is a choice and not a result. The 10 minute and 2 minute rows show the mechanism, not a recommended age.

What retention does not remove (engine README): the identity binding table (append-only by Theorem 1.5, about 100 bytes per evidence key), the interner, the journal (Ch. 11, 11.7) and the retained input rows of a host. The memory saved by retention is therefore measured in master entities here and not in bytes. O9.8 is partly done: the rule is defined, built and tested, the memory in bytes is not measured. Open: O9.8.

## 9.7 Evolution of ontologies and identity

- Ontology changes are releases with a compatibility class (Def. 2.9). An incompatible change of the output ontology of a projection requires a migration projection in the same space release, which commits `OntologyEdit` plus state edits (Def. 2.10, Theorem 2.6). History is not reinterpreted.
- Identity-domain changes migrate keys with evidence decisions, so identities survive (Ch. 1, Theorem 1.6). Merge and split are additive records (Theorem 1.7, Def. 5.9): a merge sets the survivor to the smaller id and records `mergedInto` ([R03] section 5). References resolve through the chain.

## 9.8 Delta to iteration 0.1

- **Finding 22.** Provenance for forward projections: cause, rule id and pinned upstream revisions per commit, evidence history per element, lineage on demand (Def. 9.9).
- **Finding 23.** `History(g, k)` read port. `RetainedInput.OriginalBytes` is dropped from the element. Bytes live in the input log. `IdentityCandidate` and `PatchBatch` are either given a port or deleted.
- **Finding 27.** ADR-059 consequences must cite ADR-064, which supersedes ADR-045.
- **Gap 2.** `ChangeCause.ConfigurationVersion` is a catalog revision (Def. 9.1).
- **ADR-066 ("evaluation semantics open").** Closed by Def. 9.4 to 9.6 with two parameters that remain choices: the carry policy `π` (ADR-080) and the mode (replay for row inputs, rebuild for graph inputs).
- **Conformance.** Scenarios for ADR-075 and ADR-076 (Theorem 9.5), the no-op edit, an edit with more than `N` qualifying inputs and a shared entity in two projections (ADR-081).

## 9.9 Deviations found by the prototype

Ids refer to Ch. 14 (14.8).

- **D18.** The configuration edit is full replay (Def. 9.4 with the whole log retained, carry policy `drop`). There is no evidence history, no carry policy, no separate rebuild mode and no support-counted withdrawal across sources. The lineage query is complete for row-level emits and for a `Group` without windows, and holds the group key only for windowed aggregates; it is one projection step, not the lineage of Def. 9.8.
- **D25.** Evidence elements are bounded by the operator `Retain` in the rules that emit them (`retain on time for 24h`, 9.6, Ch. 5, Def. 5.15), not by a retention declared on an entity type.
- **D27.** The engine's source of truth for a restart is a journal of recorded inputs kept in the store (Ch. 11, Def. 11.10); the configuration edit replays the retained input log (9.5). Drafts are not stored.
- **Catalog.** Definitions are loaded from `.sattle` files in dependency order and committed as one catalog revision per module (`Catalog.cs`). A change of an active projection is a configuration edit by full replay (9.5). `--write-definitions` writes committed edits back to the files.

## 9.10 Obligations

| Id | Statement | Discharged by |
|---|---|---|
| O9.1 | Theorems 9.1, 9.4, 9.5 on the engine | conformance corpus (ADR-064 to ADR-082 scenarios), property tests over random edit sequences |
| O9.2 | No-op edit yields an empty patch under replay, or the conditions are documented | metamorphic test with eviction and aggregates |
| O9.3 | Lineage by replay (Prop. 9.8) | lineage query on a lab capture, compare with on-demand `ℕ[X]` evaluation |
| O9.4 | Carry policy `π` and rebuild mode are accepted as refinements of ADR-066 to ADR-071 | decision of the project, then Ch. 14 |
| O9.5 | Theorem 9.7 per operator, including `Group` and `Threshold` | semiring evaluation test against the batch evaluator |
| O9.6 | Proposition 9.10 (b): the diff of a complete preview equals the patch of the commit that follows | test comparing both on the fixtures and on the real capture |
| O9.7 | Cost of the full-replay edit on the real capture and on the large estate (H11) | measure `POST /definitions/{module}` and a preview on the capture; the protocol document (`engine/PROTOCOL.md`) shows 3,100 ms in an example response for 243,457 rows, which is a documented example and not a measurement of this paper |
| O9.8 | Evidence elements: idempotence and withdrawal by a source retraction (Proposition 9.11) tested; the retention rule is defined, built and tested (Def. 5.15), its memory saving in bytes and the choice of age are open | a test that delivers one event twice and withdraws it; a measurement of heap with and without the rule on a long capture |
