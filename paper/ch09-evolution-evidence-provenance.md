# Ch. 9 Changing definitions, evidence and provenance

**Question.** When the definitions change, what does the new output graph contain? For any element of a graph, which inputs and rules explain why it is there? And how are observed events kept without the graph growing without bound?

**What you can state at the end.** A change of a definition is carried out by evaluating the recorded inputs again under the new definition, and committing the difference as one patch. This is not always the result of evaluating the full history from scratch; the chapter says exactly when it is. Every version records the definitions that produced it, so every version can be computed again. An element is present while its support is positive. For the operators that only combine rows, the inputs that explain a fact can be named, and the fact is reproduced from them alone. Observed events can be kept as elements of the graph, and a retention rule removes them after a fixed span of logical time.

This chapter uses commit records, causes and frontiers (Ch. 3), patches, `net` and signed deltas (Ch. 4), projections (Ch. 5), and the meaning of a projection and replay (Ch. 6).

**Running example.** The shop's master graph keeps pods. Its first definition keeps the pods whose phase is `Running`. Later the definition is changed to keep every pod whose phase is not `Failed`. A pod `p₁` that was reported once in phase `Pending` shows what can and cannot come back. Kubernetes also reports a warning about pod `api-7c9`, which is kept as evidence.

## 9.1 Three things change

- **Definitions** change: types, projections, graph spaces. Every version of an output records the definitions it was computed under, and a change of a projection produces the new output by an explicit transition.
- **Inputs** accumulate. The design keeps a bounded history of inputs per element, not an archive of all events.
- **Facts** have reasons. Support, contribution and lineage say to which inputs and rules a fact owes its presence.

## 9.2 Definitions

**Definition 9.1 (definitions are pinned).** The frontier `φ_r` of every commit records `cfg`, the version of the catalog graph (Def. 2.1) that was in force (Def. 3.10). The cause of the commit names that version. So every version of an output names the exact definitions that produced it, and `G_r` can be computed again from those definitions and the recorded inputs (Theorem 9.6).

**Definition 9.2 (qualifying input, history of an element).** At commit `c_r` with cause inputs `I_r` and edit set `E_r`, an input in `I_r` *qualifies for element `k`* iff `k` is the key of an effective edit in `E_r`. An input that leaves graph data and structure unchanged qualifies for nothing, also when it changes telemetry. The **history** `H_r(k)` is the last `N` qualifying inputs of `k`, in the order they were processed, among the commits since `k` last appeared, where `N` is the `retain` of the projection (Def. 5.1). When `k` disappears (an edit with `a = ⊥`), its history is removed, and a later element with the same key starts with an empty history. Histories are per projection, so an entity shared by two graphs has a separate history in each. Every cause input of a commit qualifies for every key the commit edits, so histories, and with them the result of a definition edit (Def. 9.4), depend on how inputs were grouped into commits; the output graph does not (Theorem 6.2).

*Example:* the warning about `api-7c9` creates an element `WarningEvent` and its relationship `About` to the pod (9.6). The input qualifies for those two keys, not for the pod, whose state does not change. A measurement of the pod's calls changes no graph data and qualifies for nothing.

**Definition 9.3 (retained set).** `R_r` is the union over keys `k` of `H_r(k)`, each input once even if it is in several histories, ordered by the order in which the inputs were processed and not by the times the sources gave them.

**Definition 9.4 (definition edit, edit replay).** A *definition edit* changes the definitions of graph `g` from catalog version `c` to `c'`, and so replaces projection `P` by `P'`. The engine then:
1. pauses ordinary processing of `g` and queues newly accepted inputs;
2. keeps the previous output graph `G = G_h` for the carry and for the difference;
3. replays `R_h` once, oldest to newest, through `P'`;
4. lets the **carry policy** decide what happens to elements of `G` whose creating inputs are no longer in `R_h`;
5. commits **one** patch, the net difference between `G` and the result, with `cfg = c'`; if the result equals `G`, it commits nothing and creates no version;
6. keeps the existing histories and processes the queued inputs in the order they were stored.

**Definition 9.5 (replay result).** Let `Eval(P', R)` be the batch meaning (Def. 6.4) of `P'` on the integrated state of the ordered inputs `R`, starting from the empty state. The replay result is `Replay(P', G, R) = Eval(P', R) ⊕_γ Carry_γ(G, R)`. Here `Carry_γ(G, R)` depends on the carry policy `γ ∈ {keep, drop}` of the changed projection: `Carry_keep(G, R)` is the set of elements of `G` that `Eval(P', R)` does not produce and none of whose *creating inputs*, the cause inputs of the commit at which the element last appeared, is in `R`; `Carry_drop(G, R)` is empty; and `⊕` takes the union under the uniqueness rule, with `Eval` winning on equal keys. The policy is a parameter of each projection, by default `drop`.

**Definition 9.6 (rebuild).** For a projection whose inputs are all graph ports, the **rebuild** of `P'` is `⟦P'⟧(χ(G_in))`: the batch meaning on the integrated state of the input graphs at the pinned frontier. This is what a new projection computes when it starts from graphs that exist.

**Definition 9.7 (support, contribution, withdrawal).** A fact of the output graph is produced by *fragments* (Def. 6.4), each with a weight from a *contribution* (Def. 10.3): the fragments that one input causes through one rule. The **support** `σ(k)` is the sum of the weights. An element is present iff `σ(k) > 0` and a create fragment exists. When a source replaces or withdraws a contribution (Kubernetes reports an object deleted, a person withdraws an authored row), that is a negative weight for exactly that contribution. The element remains iff other support remains: a withdrawal removes the source's contribution, not all evidence for the entity.

A *commutative semiring* is a set with an addition and a multiplication, each with a neutral element, both associative and commutative, where multiplication distributes over addition and multiplying by 0 gives 0. The natural numbers are one.

**Definition 9.8 (provenance semiring).** For a commutative semiring `(K, +, ·, 0, 1)`, the evaluation of the positive operators (`Match`, `Filter`, `Map`, `Join`, `Union`) on inputs annotated with elements of `K` reads a join as `·`, and a union or the dropping of columns as `+`. A `Group` is not one of them: the row it emits changes with the weights (Theorem 9.7(b)). Instances: `ℤ` (the weights of Ch. 4), `ℕ` (counting support, `σ`), `ℕ[X]` (polynomials whose variables are the ids of inputs) and the sets of input ids together with a least element (the lineage semiring of [green2007]). The **lineage** `L(f)` of a fact `f` is the set of input ids that occur in the `ℕ[X]` polynomial of `f`.

*Example:* a dependency `web → api` is the join of a call `c` with the two `Backs` facts `b₁` and `b₂` of its ends. Its polynomial is `c · b₁ · b₂`, and its lineage is `{c, b₁, b₂}`. If a second call `c'` gives the same dependency, the polynomial is `c·b₁·b₂ + c'·b₁·b₂`.

## 9.3 Theorems

**Theorem 9.1 (edit replay).** The result of a definition edit is a deterministic function of `(G, R_h, P', γ)`: the committed patch is `toPatch(χ(Replay(P', G, R_h)) − χ(G), G)`, and it is empty iff `Replay(P', G, R_h) = G`.

*Proof.* `R_h` is a function of the commit history (Theorem 9.4). `Eval` is a function of its ordered inputs (Theorem 6.3). Carry is a function of `G`, `R_h` and `γ`. Step 5 applies Theorem 4.8. Queued inputs enter after the edit, in the order they were stored, and do not affect the replay. ∎

**Theorem 9.2 (an edit is not recomputation from scratch).** In general `Replay(P', G, R_h) ≠ Eval(P', full input history)`.

*Proof.* The running example. `P` keeps pods with `phase = "Running"`, and `P'` keeps pods with `phase ≠ "Failed"`. Input `i₁` is a pod `p₁` in phase `Pending`, and it is the only input of its commit. Under `P` it changes nothing, so it qualifies for no element and is not retained (Def. 9.2). Recomputation over the full history under `P'` creates `p₁`. `R_h` does not contain `i₁`, so `p₁` is absent after the replay: an input that was missed does not gain an effect in the past. ∎

*A sufficient condition for equality.* If `R_h` is a *sufficient subset* for `P'`, that is `Eval(P', R_h) = Eval(P', full history)`, and the carry set is empty, the two are equal by definition. This holds when `R_h` is the full history. It also holds when `P'` keeps no state across inputs that an input not retained could have contributed to. "Has no effect on its own" is not enough: an input not retained can be the partner of a later join.

**Theorem 9.3 (rebuild is exact for projections that read graphs).** For a projection whose inputs are all graph ports and whose operators are functions of the integrated input and the frontier (so without a `Threshold`, whose state is a fold over the recorded sequence, Theorem 6.1), the rebuild of Def. 9.6 equals the output of a fresh projection `P'` started on the same input graphs, for any retention `N`. If `P' = P` (the same digest) and `G = ⟦P⟧(χ(G_in))`, the committed patch is empty.

*Proof.* The input state is the graph itself, which is fully available at a pinned frontier, so no input is missing. The result is the meaning on the integrated state (Def. 6.4, Theorem 6.1, Theorem 6.3). An edit that changes nothing gives the same meaning, hence the same graph, hence an empty net patch. ∎

*Consequence.* For views that read graphs and have no `Threshold`, rebuild is exact and does not have the limit of Theorem 9.2. A view with a `Threshold` is different. A rebuild starts every key in `initial` and folds only the windows that are still retained, so a state that was entered by windows behind the retained range is lost, even by an edit that changes nothing. The health signals of Ch. 7 (7.4) are such a view. The edit by full replay that the prototype has (9.5) does not have this limit, because it folds the whole recorded sequence. The limit of Theorem 9.2 is real only for ingestion projections whose input rows are no longer available. This paper therefore proposes rebuild for projections that read graphs and have no `Threshold`, and replay for all others. An edit that changes nothing is not guaranteed to give an empty patch under replay: a bounded history, or a property aggregated over inputs that are no longer retained, can make `Eval(P, R_h) ⊕ Carry ≠ G`.

**Theorem 9.4 (evidence is a derived index of the log).** `H_r(k)` and `R_r` are functions of the commit records `c_1 … c_r` alone. They can be rebuilt by one scan of the log and need not be a source of truth.

*Proof.* Def. 9.2 uses only the cause inputs and the edited keys of each commit, both recorded in `c_j` (Def. 3.8), in commit order. Removal is an edit with `a = ⊥`. ∎

The bytes of an input are not part of the evidence. They stay in the engine's log of inputs under its own retention, named by the id of the input.

**Theorem 9.5 (support).** After an edit, an element of `G` remains iff its support under `P'` on the replayed inputs is positive and a create fragment exists for it (or the carry policy retains it), and is removed otherwise. When the replayed inputs hold insertions only, that is: it remains iff at least one replayed input still produces it.

*Proof.* Present iff `σ(k) > 0` and a create fragment exists (Def. 9.7, Def. 6.4). `σ` is evaluated on `R_h` under `P'`. If two inputs produced `k` and only one still does, the support is `1` and `k` remains. If none does, `σ = 0` and `k` is removed unless carried. Starting from `G` does not by itself retain `k` (Def. 9.5). ∎

**Theorem 9.6 (definitions are pinned, the past can be computed again).** For every version `r`, `G_r` is a function of the recorded input sequence up to the frontier `φ_r` and of the definitions of the catalog at `φ_r.cfg`.

*Proof.* Theorem 6.3, with the definitions named by Def. 9.1. ∎

**Theorem 9.7 (lineage soundness).** Let `S` be an integrated input state, a family of Z-sets of rows and facts, and `S|_L` its restriction to a set `L` of input rows.
- (a) *Plans of positive operators.* Let `Q` be a composition of `Match`, `Filter`, `Map`, `Join` and `Union`. For every output row `f`, `Q(S)(f) = Σ_m c_m · Π_{x ∈ m} S(x)`: a sum over multisets `m` of input rows, finite at each `S`, with coefficients `c_m ∈ ℕ` that depend on `Q` and `f` only. Let the lineage `L_S(f)` be the set of input rows that occur in a monomial that does not vanish at `S`. Then `Q(S|_L)(f) = Q(S)(f)` for every `L ⊇ L_S(f)`. In particular `f ∈ Q(S)` iff `f ∈ Q(S|_{L_S(f)})`.
- (b) *A per-key operator last.* Let `Q = h ∘ Q₀` with `Q₀` as in (a) and `h` a `Group` with any aggregates or a `Distinct`, and let `S` have no negative weight. For an output row `f` with key `κ` let `L_S(f)` be the union of `L_S(x)` over the rows `x` of `Q₀(S)` with key `κ`. Then `Q(S|_L)(f) = Q(S)(f)` for every `L ⊇ L_S(f)`.
- (c) *`Threshold`.* The row of a key at a frontier is a function of the closed windows of its current episode (Def. 7.10): removing the windows of the key outside that episode leaves the row unchanged.

The proof below has been read, and no program has checked it. The polynomial of (a) is the `ℕ[X]` provenance of [green2007]; the proof is direct and does not use a theorem of that paper. The theorem describes the design. The prototype's lineage is the contributing rows of one step of one projection (9.5), which is (a) or (b) for one step, and nothing in the prototype depends on more.

*Proof.* (a) One application of each operator has the stated form: `Filter(a)(f) = [φ(f)] · a(f)`; `Map(a)(f) = Σ_{x : map(x) = f} a(x)`; `Union(a, b)(f) = a(f) + b(f)`; `Join(a, b)(f) = a(f_left) · b(f_right)` when the join condition holds on `f` and `0` otherwise; a node pattern of `Match` is a `Filter` on entity facts and an edge pattern a `Join` of a relationship fact with its endpoint facts, followed by a `Map` to the binding row (Def. 6.3). Sums of products with coefficients in `ℕ` are closed under substitution, so by induction on the plan `Q` has the form; the sums are finite because `S` has finite support. Let `L ⊇ L_S(f)`. A monomial that does not vanish at `S` has all its rows in `L_S(f) ⊆ L`, where `S|_L` agrees with `S`, so it keeps its value. A monomial that vanishes at `S` holds a row `x` with `S(x) = 0`, and `S|_L(x) = 0`, so it vanishes at `S|_L`. The two sums are equal. (b) Let `a = Q₀(S)` and `a' = Q₀(S|_L)`. The weight `h(a)(f)` depends only on `a` restricted to the rows with key `κ` (the per-key case of Theorem 6.1). For a row `x` of key `κ` with `a(x) ≠ 0`, `L ⊇ L_S(x)` and (a) give `a'(x) = a(x)`. For a row `y` of key `κ` with `a(y) = 0`: `S` and the coefficients are non-negative, so every monomial of `y` vanishes at `S`, hence at `S|_L`, and `a'(y) = 0`. So `a` and `a'` agree on the key, and `h` gives `f` the same weight. (c) By Def. 7.10 the state of a key is the fold over the windows of its current episode from `initial`, and the key has a row iff it has a window in the look-back. The current episode contains the newest window of the key. Removing earlier episodes changes neither the fold nor the newest window, and it opens no gap inside the episode, since an episode is delimited by the gaps between consecutive windows. ∎

*Why the conditions are needed.*
- *A `Group` in the middle.* Rows `r₁ = (k₁)`, `r₂ = (k₂)`, `r₃ = (k₂)`. Plan: `g = S |> group by k count n`; `one = g |> where n == 1 |> select { tag: "x" }`; `two = S |> where row is r₂ |> select { tag: "x" }`; `f` is the row of `union one, two |> group by tag count m`. On `S`: `g = {(k₁, 1), (k₂, 2)}`, the union has `x` with weight 2, and `f = (x, 2)`. The contributing rows of `f` are `r₁` (through `one`) and `r₂` (through `two`). On `S` restricted to `{r₁, r₂}`: `g = {(k₁, 1), (k₂, 1)}`, the union has weight 3, and the output is `(x, 3)`. So `f` is not in the result on its own lineage. Restricting the input of a `Group` creates rows that were not there, and an operator after it sees them. Hence (b) asks for the per-key operator to come last.
- *`Threshold` and the run that decided.* A key enters `high` by three windows at or above `above`, stays `high` through later windows inside the band, and the three windows have left the look-back. Restricted to those three windows, the key has no window in the look-back and no row. Hence (c) names the episode, not the run.
- *Negative weights in (b).* Rows `x₁` with weight `+1` and `x₂` with weight `−1` map to one row `y` of key `κ`, and `x₃` maps to `z` of key `κ`; `f = (κ, count 1)` and `L_S(f) = {x₃}`. For `L = {x₃, x₁} ⊇ L_S(f)` the count is 2.

**Proposition 9.8 (cost of evidence).** The entries of the histories of a graph number at most `N · |dom G|`. Provenance polynomials are not stored. A question about lineage is answered from the history (the last `N` qualifying inputs per element) or, beyond it, by replaying the log of inputs through the pinned definitions (Theorem 9.6).

*Proof.* The bound is Def. 9.2: at most `N` entries per key. ∎ Lineage by replay is a design; it is not built and not tested.

## 9.4 Provenance record and the history of an element

**Definition 9.9 (provenance record).** For each emitted fact the design answers, from the commit record and the history: the inputs that caused its latest change (the cause), the rule that governs the sink (Ch. 5), the facts upstream (graph, key, version) read through `gin` of the frontier, and the lineage of Def. 9.8 on demand. That an element of one graph stands for an element of another is shown by the cause, the rule and the pinned upstream versions. `Correspondence` stays the explicit link of `sync` rules (Ch. 8).

A reader asks for the history of an element with `History(g, k)`: the ordered `H(k)` with, for each entry, the id of the input, the version of the commit and the rule. It is a function of the log (Theorem 9.4).

## 9.5 Definition edits, releases and preview in the prototype

**Definition 9.10 (release).** Every committed version of the catalog that changes a module creates a release, named by the digest of the module (Def. 2.8), with the digest it replaces. The engine lists them oldest first. The facts of a release never change. The difference of two releases is a patch on the catalog graph whose before-states come from one release and after-states from the other. Ids are path ids (Def. 2.3), which contain the name; a rename appears as a changed property only when the text marks it `was <old name>`, and otherwise as a removal and an addition. **Restore** applies, to a draft, the patch from the draft's current facts of a module to the facts of a release: an ordinary draft patch, undone by its inverse (Ch. 4, Prop. 4.10).

**Proposition 9.9 (releases).** (a) A restore of release `ρ` followed by a commit gives a module whose digest equals the digest of `ρ`. (b) The difference of releases `ρ₁, ρ₂` equals, on the facts of the module, `toPatch(χ(ρ₂) − χ(ρ₁), ρ₁)` (Def. 4.9, with `ρ₁` as the base).

Part (a) is tested on one example: the original digest comes back, and the list holds three releases. Part (b) follows from Theorem 4.8 in outline; the test checks one changed property. The proposition counts as tested.

**Definition 9.11 (bounded preview).** `Preview(D, n)` evaluates a draft `D` without committing it. A fresh engine, with stores of its own in memory, replays the first `n` retained input rows in order (by default 500,000, at most 5,000,000) through the draft's definitions. It returns the messages of the checker, counts per graph, a digest, and on request the graphs and their measurements. The answer states how much was covered: the rows read, the rows retained, whether the preview is *complete*, and the logical time. The difference against the live output is given iff `n` covers all retained rows.

**Proposition 9.10 (preview).** (a) `Preview` changes nothing observable on the live engine: the digest of the state, the number of the last event and the heads of the graphs are unchanged. (b) A complete preview computes `Eval(P', R)` for the full retained input `R`, which is the result of the activation that a commit would perform if no input arrives in between (Theorem 6.3). (c) An incomplete preview is not comparable with the live output and carries no difference.

Parts (a) and (c) are tested on examples. Part (b) holds for the prototype's full replay (9.5), not for Def. 9.4 with a carry. It follows from Theorem 6.3 in outline and is not tested: no test compares the difference that a preview gives with the patch of the commit that follows. The proposition counts as tested.

**What a definition edit does in the prototype.** The prototype replays the full retained log on a fresh engine and takes the difference against the previous output. So it computes `Eval(P', full history)`, which is the sufficient condition of Theorem 9.2, and a test checks that the result equals a fresh engine fed the same inputs under the new definition. The gap of Theorem 9.2 does not arise as long as the whole log is retained. It would arise with a bounded history. The only bound of the prototype is 40 million input rows per space; what an edit does beyond it was not examined. Dependent graphs are computed again by the same replay, so the prototype does not distinguish replay for row inputs from rebuild for graph inputs (Def. 9.6). An edit that changes nothing creates no version.

**Lineage in the prototype.** For an emitted element the engine answers with the sink, the rule, the chain of operators and its sources.
- For an element emitted row by row from a `Match` the answer is **complete**: the sources of every row that supports the element, in pages, with the number of distinct sources and of supporting rows. A lifted edge (Ch. 5, 5.8) therefore names every edge of its fibre, both end nodes and both images, each with its role.
- For an element emitted from a `Group` without windows the answer is complete as well: it names every input row of the group. An edge written by `group by` names every lower edge and node it is made of.
- The answer is partial, holding the elements of the group key only and marked incomplete, for an element emitted from a windowed `Group`, a `Rollup`, a `Threshold` or a `Series`, and for rows that were reduced to plain values before the emit.
- The projections of the prototype emit nodes and edges row by row and put counts on them by an update, so their nodes and edges are complete.
- A reverse question is answered too: of which elements of other graphs an element is a part. Graphs that read the element through a windowed aggregate are named, and there the answer is incomplete.

Tests: a lifted capability edge names the service edge, the services and the capabilities it is made of; a group node names every service; an edge that stands for two parallel edges names both, and the pages give the same list; a lower element names the higher elements it is part of; an edge emitted from a group names both pod calls of each service dependency, the same elements as the row-by-row emit, forward and reverse; an unknown element is reported as unknown in both directions. This is lineage at the sink for one step of one projection. It is not the `ℕ[X]` lineage of Theorem 9.7 across a stack of graphs; a reader follows it level by level.

## 9.6 Evidence elements

The word evidence has two meanings in this paper. The *history* of Def. 9.2 is an index of retained inputs per element, inside the engine, and it is not built. An **evidence element** is a graph element that stands for one observed event and has an edge to what the event concerns. It is built, and it is ordinary graph data: it has an identity, a type, properties and versions, and any projection can match it.

**Definition 9.12 (evidence element).** Let `ε` be a source event with a delivery key (Def. 10.2) and a subject element `x` of the master or of a derived graph. An evidence element for `ε` is an entity `e_ε` whose identity is derived from the delivery key (or from a stable id inside the event), whose properties are the fields of the event that a rule keeps, together with a relationship `e_ε → x` (`Concerns`, `About`, `EvidenceFor`). A projection may also keep only the count of such events as telemetry on `x` (Ch. 7). The two forms are independent, and the prototype keeps both for Kubernetes warnings and for Azure's log of management actions.

*Example:* the warning about `api-7c9` becomes an entity `WarningEvent` with the reason and message of the warning, and a relationship `About` to the pod.

**Proposition 9.11 (evidence elements are idempotent and can be withdrawn).** Delivering `ε` again with the same delivery key leaves the graph unchanged (Theorem 10.1). Withdrawing the source record withdraws `e_ε` and its edge when no other contribution supports them (Theorem 10.2).

It follows from Theorems 10.1 and 10.2 for ingestion rules. No test delivers one evidence event twice. Withdrawal by age (Proposition 5.9) is tested; withdrawal because a source retracts a record is not. The proposition counts as tested.

The evidence elements that exist (Ch. 10, 10.6 gives the counts):
- `ActivityEvent`, an action on an Azure resource from the log of management actions, read both from Azure's interface for past actions and from its stream of diagnostic records, with `Concerns` to the resource. The id of the resource is compared without regard to upper and lower case.
- `WarningEvent`, a Kubernetes event of type `Warning`, with `About` to its pod. Events of type `Normal` are left out.
- `AuditEvent`, a completed request to the Kubernetes interface that changes something, as the cluster's audit log reports it; renewals of leases are left out by the rule. It has no relationship to a subject, so it is an event kept as an element but not an evidence element in the sense of Def. 9.12.
- In the workload graph, a `DependencyEvidence` element for each dependency and each way it was found, whose lineage names the master facts it came from (Ch. 7, 7.4).

*Measured.* On the two real recordings (Ch. 14), the 95-minute one and the 17-minute one, evidence elements are 14,622 of 15,154 and 47,598 of 48,201 entities of the master graph. This follows from keeping events as elements: the size of the master graph follows the rate of events, not the size of the estate. The figure of Ch. 11 for the graph held in memory (337 MB for 750,000 entities and 2.5 million relationships) is for a different mix of elements and says nothing about evidence elements.

**Retention.** Each of the three evidence rules of the shop's definitions reads its rows through `|> retain on time for 24h`. So an evidence element stays while the logical frontier is less than its event time plus 24 hours, and is then retracted like any element whose support is gone (Def. 5.15, Proposition 5.9). Nothing in the engine knows about evidence: the age is text in the definition, the operator is general, and the retained state is a function of the frontier, so it does not depend on the size of the batches.

What the age does to the master graph, with the same recording and only the age replaced in a copy of the definitions. The table was measured on an earlier state of the prototype, in which one activity event could get two identities (Ch. 10, 10.6), so its counts of evidence are higher than those above, and in which meters were not elements of the master graph, so its other entities are fewer (456 and 491 against 532 and 603, the difference being the 76 and 112 meters).

| Recording | Age | Entities of the master graph | Evidence elements |
|---|---|---|---|
| 95 minutes (5,679 s) | 24 h | 15,819 | 15,363 |
| | 10 min | 456 | 0 |
| | 2 min | 456 | 0 |
| 17 minutes (1,017 s) | 24 h | 50,154 | 49,663 |
| | 10 min | 503 | 12 |
| | 2 min | 491 | 0 |

At 24 hours the rule removes 958 and 54 `ActivityEvent` elements and nothing else. Almost all evidence of these short recordings is within 24 hours of the frontier, so the master graph is nearly as large as without a rule. The rule bounds the graph at about the rate of events times the age. The second recording holds 42,244 audit events in 1,017 s. If that rate held for 24 hours, about 3.6 million `AuditEvent` elements would be retained, more than the 750,000 entities of the memory budget of Ch. 13. This is arithmetic on one short recording under a constant rate, not a measurement. The age is a parameter of the definition, and 24 hours is a choice, not a result. The rows for 10 and 2 minutes show the mechanism, not a recommended age.

What retention does not remove: the binding set of identities (it only grows, Theorem 1.5, about 100 bytes per key of an evidence element), the table of interned strings, the journal of inputs (Ch. 11, 11.3) and the input rows a host retains. So the saving is measured here in entities of the master graph, not in bytes; the saving in bytes is not measured.

## 9.7 Changing types and identity

- A change of types is a release with a class of compatibility (Def. 2.9). An incompatible change of the output ontology of a projection needs a migration projection in the same release of the space, which commits the edit of the ontology together with the edits of the states (Def. 2.10, Theorem 2.6). The past is not read again under the new types.
- A change of an identity domain moves the keys with recorded decisions, so identities survive (Ch. 1, Theorem 1.6). Merge and split only add records (Theorem 1.7, Def. 5.9): a merge makes the smaller id the survivor and records the forwarding. References are resolved through the chain of forwarding records.

## 9.8 What the prototype does differently

- **Definition edits** are full replay: Def. 9.4 with the whole log retained and the carry policy `drop`. There is no history per element (`retain N` is parsed and printed, and not applied), no carry policy, no separate rebuild, and no withdrawal counted across several sources. Provenance semirings are not built.
- **Lineage** is complete for row-by-row emits and for a `Group` without windows, and holds the group key only for windowed aggregates. It covers one step of one projection, not the lineage of Def. 9.8.
- **Evidence elements** are bounded by the operator `Retain` in the rules that emit them (Ch. 5, Def. 5.15), not by a retention declared on an entity type.
- **Restart.** What the engine restarts from is a journal of recorded inputs kept in the store (Ch. 11, Def. 11.10). A definition edit replays the retained log of inputs. Drafts are not stored.
- **Catalog.** Definitions are loaded from text files in the order of their imports and committed as one catalog version per module. Committed edits can be written back to the files.
