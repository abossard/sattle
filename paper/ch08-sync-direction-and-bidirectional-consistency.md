# Ch. 8 Sync direction and bidirectional consistency

**Claim.** The direction in which a rule may be edited is declared per rule (`forward`, `readonly`, `bijective`, `lens`, `sync`). Lens laws survive stacking, and a backward edit changes the internal graph only.
**Status.** Built: `readonly`, `bijective` and `lens` rules, backward edits with routing to the owning rule, a staged forward evaluation and the PutGet check of Theorem 8.5, typed rejections. One lens exists, the capability view of `acme.biz`. Tested on examples and on generated edit sequences for that one lens (8.5). Proved at gate 1a on a model: the laws of a formal `sync` fragment (Def. 8.7, 8.8, Theorem 8.7). Not built: `sync` rules and correspondences, because the engine has no stored correspondence graph and no FWD and BWD translators; the palette lists `sync` as unsupported and a `sync` rule is a typed error. The inverse of the ingestion rule is derived only for one entity sink and one relationship sink over one authored rows port; otherwise the graph is `NotEditable`. Deviation D12.
**Gate.** 1a (theory): direction classes and lens laws. It bears on 1b (what can be edited backwards) without a stated claim.
Uses: Ch. 3 (graph), Ch. 4 (patches, `toPatch`, squash), Ch. 5 (rules, ownership P2, operators), Ch. 6 (denotation, T5, T7).
Used by: Ch. 9, 15.
Sources: [R03] sections 3.3, 4, 8.4; [R01] gap 5, finding 10, section 2.3 (complement attribution), T11, T12; [foster2007], [hofmann2011], [diskin2011], [hermann2013].

The word direction has four meanings in the contracts (finding 10). This chapter concerns **sync direction** only: the direction in which changes of a projection may propagate. Edge orientation, traversal direction (`Traverse.direction`) and flow direction are different things (glossary). A projection need not be bidirectional. Sync direction is declared.

## 8.1 The five classes

**Definition 8.1 (sync class).** Every projection declares `forward` or contains rules, and every rule declares a class:

| Class | Declared on | Backward behaviour | Laws owed |
|---|---|---|---|
| `forward` | projection | none. Any edit addressed to its output is rejected `NotEditable` | determinism (Theorem 6.3) |
| `readonly` | rule | edits touching elements or properties owned by the rule are rejected `ReadOnlyByRule` before any write | determinism |
| `bijective` | rule | the inverse is derived from the rule | `inv(get(s)) = s` and `get(inv(v)) = v` |
| `lens` | rule | `put` is derived from per-operator backward steps | GetPut, PutGet on the accepted edit domain; PutPut only if declared |
| `sync` | rule | FWD and BWD synchronizers derived from one triple rule | correctness, completeness on the v1 fragment, hippocraticness |

The unit of direction is the rule. By ownership (P2, Def. 5.7) each pair `(entity type, property)` and each relationship type is owned by at most one rule of the three editable classes, so direction is in effect declared **per binding of a sink and per property**, which answers gap 5 of [R01] without annotating individual ports. Telemetry is forward-only everywhere: telemetry properties are never edit targets (`TelemetryNotEditable`), telemetry operators have no inverse, and a rule that sets telemetry or derives data through `Threshold` is `readonly` or sits in a `forward` projection.

## 8.2 Lenses

**Definition 8.2 (asymmetric lens with complement).** For a rule with source `S` (the integrated input graphs and rows it reads) and view `V` (the facts of the sinks it governs):
- `get : S → V` is the denotation of the rule (Def. 6.4, restricted to its sinks);
- `put : V × S → S` returns the new source for an edited view and the old source;
- the **complement** is the part of `S` that `get` omits. Because Sattle keeps the source graph and aligns by timeless identity, a lens over a stored source needs no complement store: the complement is the pinned source revision (the old source argument of `put`, the formulation of [foster2007]). In the symmetric form of [hofmann2011] this is `put : V × C → S` with `C` the omitted part and `missing` the create default, which is the rule's `on create` default.
- Laws, where the functions are defined:
  - **GetPut**: `put(get(s), s) = s`;
  - **PutGet**: `get(put(v, s)) = v` for `v` in the accepted edit domain `Acc(s)`;
  - **PutPut** (optional): `put(v₂, put(v₁, s)) = put(v₂, s)`.
Well-behaved means GetPut and PutGet, very well-behaved adds PutPut. The accepted domain is given by the rule's `accept` clauses (`create`, `delete`, `update(p, …)`).

**Definition 8.3 (backward edit).** A backward edit is a patch `p_V` on a derived graph `G_k` (a proposal, not a commit), with base revisions for every graph on its path (the view, each intermediate graph, the master). Its alignment with the source is by identity: element ids are timeless (Ch. 1), so the delta between the view before and after is known (delta lenses separate delta discovery from delta propagation, [diskin2011]). The backward result is `p_S = toPatch(χ(put(apply(p_V, V), S)) − χ(S), S)` (Def. 4.9).

**Definition 8.4 (information class).** `δ ∈ {iso < lens < none}` classifies how much of its input an operator preserves (an *information* classification). It is distinct from the cost class (linear, bilinear, non-linear) of Ch. 6: `Filter` is linear but not invertible without a complement.

| Operator | δ | Reason |
|---|---|---|
| `Match` | lens | complement: the unmatched part of the input graph |
| `Filter` | lens | complement: failing rows. An edited row must still satisfy the predicate |
| `Map` | iso if every input field appears exactly once as a field reference or an injective call with an inverse. lens if fields are dropped. none if an output uses a non-injective expression | lossy |
| `Union` | lens for `preferFirst`, `preferLast` or type-disjoint inputs. none for `requireEqual`, `preserveEvidence` | the owner of a row must be known |
| `EmitEntity`, `EmitRelationship` | iso if the id is a field reference or `IdOf` over field references and every set value is a field reference. lens if some set values are literals (edits must keep them). none otherwise | identity is never edited. Computed values are lossy |
| `Join` | none in v1 | view update through a join is ambiguous without key analysis (Q9) |
| `Distinct`, `Group`, `Rollup`, `Traverse`, `Window`, `Fixpoint`, `Values`, `Link`, `Series`, `Delta`, `Threshold` | none | many-to-one, derived paths, constants, evidence, time-indexed measures |

`IdOf` over field references inverts through the identity catalog (`Key ← boundTo ← Identity`). If an identity has several keys in that domain, the proposal is rejected `AmbiguousKey`.

**Definition 8.5 (direction judgment).** `δ(rule) = max{δ(o)}` over operators `o` on any path from a projection in-port to a sink governed by the rule. `bijective` requires `δ = iso`, one input stream, every input field recovered by some emit, every emit in mode `create`, and P2. `lens` requires `δ ≤ lens` and that the sink paths reach exactly one projection in-port (so an edit never has to be split across inputs). `sync` requires the v1 fragment (8.4). `readonly` has no condition. A rule claiming more than its `δ` fails with `DirectionOverclaim(rule, operator)` naming the first operator that raised `δ`.

## 8.3 Laws and composition

**Theorem 8.1 (bijective rules).** If `inv(get(s)) = s` and `get(inv(v)) = v`, then with `put(v, s) := inv(v)` all three laws hold: GetPut, PutGet, PutPut.
Status: **proved**.

*Proof.* GetPut: `put(get s, s) = inv(get s) = s`. PutGet: `get(put(v, s)) = get(inv v) = v`. PutPut: `put(v₂, put(v₁, s)) = inv(v₂) = put(v₂, s)`. ∎

**Theorem 8.2 (the lens steps).** (a) `Filter` with predicate `φ`: with `get(S) = {r ∈ S | φ(r)}`, `put(V, S) = V ∪ {r ∈ S | ¬φ(r)}` and `Acc(S) = {V | every row of V satisfies φ and no new row of V has the key of a failing row of S}`, the lens is very well-behaved. (b) `Map` that drops fields and keeps the key: with the complement the dropped fields of `s`, aligned by key, `put` merges edited projected fields with the old dropped fields, gives a created row the `on create` default for the dropped fields, and removes a source row whose key is not in the view. It is well-behaved. It is very well-behaved on the sequences of edits in which no key is deleted and created again, and not in general. (c) A `Match` of one pattern element is a `Filter` over the facts of the input graph, and `EmitEntity` and `EmitRelationship` whose identity is an injective function of the row and whose other sets are literals are lenses that are bijections on their accepted domain, which excludes edits of the literal values and elements whose identity has more than one key in the domain: after a merge or an alias two rows give one element, the map from rows to elements is not injective, and an edit of such an element is rejected `AmbiguousKey` (Def. 8.4).
Status: (a), (b) and (c) **proved** below; read only, checked by no tool. Tested on one lens with generated edit sequences (`LensTests.cs`, 8.5); a generated test over the lens steps themselves is experiment **Q3** (test T-LENS, [inventory.md](inventory.md)). The earlier form of (b) said "very well-behaved" without the condition, which is false: see the counterexample. The earlier form of (c) did not restrict the pattern; a pattern of several elements is a join, and `Join` is not a lens step, so for it PutGet is checked at run time (Theorem 8.5) and not proved.

*Proof of (a).* GetPut: `put(get S, S) = get S ∪ failing(S) = S`. PutGet: for `V ∈ Acc(S)`, `get(V ∪ failing(S)) = V ∪ ∅ = V`, since the failing rows do not satisfy `φ`. PutPut: the failing rows of `put(V₁, S)` are those of `S`, because `V₁` satisfies `φ`, so `put(V₂, put(V₁, S)) = V₂ ∪ failing(S) = put(V₂, S)`. ∎

*Proof of (b).* Rows have a key that the map keeps, and keys are unique in `S` and in every accepted `V`. Write `π` for the projection to the kept fields and `d(r)` for the dropped fields of `r`. `get(S) = {π(r) | r ∈ S}`. `put(V, S)` holds, for each `v ∈ V`, the row `v ⊕ d(r)` if `S` has a row `r` with the key of `v`, and `v ⊕ default` otherwise. GetPut: every `v = π(r)` meets its own `r`, so `put(get S, S) = {π(r) ⊕ d(r)} = S`. PutGet: `π(v ⊕ x) = v` for any `x`, so `get(put(V, S)) = V`. PutPut, when `V₁` holds every key of `S` that `V₂` holds: for such a key the dropped fields in `put(V₁, S)` are `d(r)`, the same as in `S`, and for a key of `V₂` that is not in `S` both sides give the default, so `put(V₂, put(V₁, S)) = put(V₂, S)`. *Counterexample without the condition:* `S = {(k, a: 1, d: 7)}` with `d` dropped and default 0, `V₁ = ∅`, `V₂ = {(k, a: 1)}`. Then `put(V₂, put(V₁, S)) = {(k, 1, 0)}` and `put(V₂, S) = {(k, 1, 7)}`. A rule over such a step must therefore declare `putput` false unless it does not accept `delete`. ∎

*Proof of (c).* A `Match` of one pattern element selects the facts of the input graph that have the type of the element and satisfy its property predicates. That is `get` of (a) with `φ` the conjunction of the type test and the predicates, over the set of facts, so (a) applies. An emit maps a row to an element by an injective renaming of fields to properties, an identity that is an injective function of the row's key fields (Theorem 1.4) as long as no two keys are forwarded to one representative, and constants `c` for the literal sets. On the accepted domain, the elements whose literal properties equal `c` and whose identity has one key, this map has an inverse: read the fields back from the properties. A map with an inverse satisfies GetPut, PutGet and PutPut with `put(V, S) = get⁻¹(V)` (Theorem 8.1). ∎

**Theorem 8.3 (laws survive stacking, T11).** Let `(get₁, put₁)` and `(get₂, put₂)` be lenses and `get = get₂ ∘ get₁`, `put(v, s) = put₁(put₂(v, get₁ s), s)`, `Acc(s) = {v ∈ Acc₂(get₁ s) | put₂(v, get₁ s) ∈ Acc₁(s)}`. If both are well-behaved, so is the composition. If both are very well-behaved, so is the composition. PutPut of the composition needs PutPut of every link.
Status: **proved**. This is the composition result for asymmetric lenses of [foster2007]; the proof here is stated for the accepted domains.

*Proof.* GetPut: `put(get s, s) = put₁(put₂(get₂ get₁ s, get₁ s), s) = put₁(get₁ s, s) = s` by GetPut₂ then GetPut₁. PutGet: let `w = put₂(v, get₁ s) ∈ Acc₁(s)`. Then `get(put(v, s)) = get₂(get₁(put₁(w, s))) = get₂(w) = v` by PutGet₁ then PutGet₂. PutPut: with `g = get₁ s` and `w₁ = put₂(v₁, g)`, the state `s₁ = put₁(w₁, s)` has `get₁ s₁ = w₁` (PutGet₁), so `put(v₂, s₁) = put₁(put₂(v₂, w₁), s₁)`. PutPut₂ gives `put₂(v₂, put₂(v₁, g)) = put₂(v₂, g)`, and PutPut₁ gives `put₁(put₂(v₂, g), put₁(w₁, s)) = put₁(put₂(v₂, g), s) = put(v₂, s)`. Without PutPut of both links the last two steps fail. ∎

**Theorem 8.4 (class of a stack).** The class of an edit along a path of stacked projections is the worst link: `bijective ∘ bijective = bijective`, `lens ∘ lens = lens`, `bijective ∘ lens = lens`, and any `forward` or `readonly` link on the path rejects the edit and names that link. Version 1 allows at most one `sync` link per edit path. PutPut holds only if every link declares it. An edit on `G_k` is routed to the rule that owns the touched element or property (P2, unique), applied as a proposal on `G_{k-1}`, and recursively to the master.
Status: **proved** (Theorems 8.1 and 8.3; routing is unique by P2).

**Theorem 8.5 (runtime law check).** Before an edit is accepted, the engine evaluates forward ingestion and the view on a staged copy and compares the resulting view with the edited view (as graphs, Def. 3.4). Any difference rejects the edit with `PutGetViolation`, and nothing is accepted or committed. Hence every accepted edit satisfies PutGet on its own instance, even when a derived backward step is defective.
Status: **proved**.

*Proof.* The check is the statement `get(put(v, s)) = v` evaluated on the instance. Acceptance requires it to hold. ∎

**Theorem 8.6 (scope of backward edits).** No backward edit reaches a telemetry property, and no backward edit reaches Azure or Kubernetes.
Status: **proved**, given activation rule S5 below (a fifth space judgment after S1 to S4 of Def. 5.8, not the store property S5 of Ch. 11).

*Proof.* A telemetry property is owned by a `readonly` rule or lies in a `forward` projection (8.1, P2), so routing rejects the edit at that link. A chain ends at the authored partition of the master, whose ingestion rules over rows from the internal `sattle.authoring` source are `bijective`. Observed facts (Kubernetes, Hubble, Azure) are owned by `readonly` rules because activation rule **S5** requires every rule whose in-port is bound to a `Source` whose adapter is not `sattle.authoring` to be `readonly`. ∎

*S5 is new.* [R03] states that observed facts are owned by `readonly` rules by authoring convention. S5 makes the convention a checked activation rule.

## 8.4 The write path, and what is not an edit

- **Target.** Backward edits change the internal graph only. The proposal lands as authored row upserts or retractions into a keyed table, so a Z-set delta. After the law check, those rows are durably accepted and flow through normal forward ingestion.
- **Replay.** Because the edit becomes recorded input, replay determinism is unchanged (Theorem 6.3).
- **Concurrency.** An edit carries base revisions of every graph on its path. Any stale base rejects it. *Remark:* a base of revisions alone is not enough, since accepted inputs not yet projected are not visible in revisions (acceptance means queued durably, ARCHITECTURE.md). The base must also pin the ingestion sequence of the last accepted input of the master, and acceptance must be ordered immediately after exactly that input, otherwise the staged check of Theorem 8.5 may no longer describe the state that processes the rows. This is a proposed refinement of [R03] section 4.4.
- **No PutPut by default.** Edits apply one at a time against exact bases. Squashing several view edits before `put` is allowed only on chains where every link declares `putput`.

**Definition 8.6 (sync rule, correspondence).** A `sync` rule relates a source pattern and a target pattern through a correspondence node, as in a triple graph grammar: a triple `S ← C → T` of source, correspondence and target ([anjorin2016]). For each `sync` rule, activation derives an entity type `<projection>#<rule>` extending the kernel type `Correspondence` with one property per `keep` path (Def. 2.12). It exists for the case where the target owns information that no input has, and in a graph space every non-master graph is derived, so that information lives in the stored correspondence graph. The v1 fragment: each rule relates one created source node to one created target node and one correspondence, context nodes and edges are allowed, there are no negative application conditions, attribute conditions are `equate` equalities of field references only, and source patterns of different rules range over disjoint types.

Def. 8.6 names the fragment. The next two definitions make it exact enough to prove something about it; the rule class is not built (D12), so they define a model and no program.

**Definition 8.7 (v1 sync grammar, triple, language).** A v1 grammar is a finite set of rules. A rule `ρ` has a source entity type `S_ρ`, a target entity type `T_ρ`, a finite list of *context slots* `j = 1 … m_ρ` and a finite set `eq_ρ` of pairs `(p, p')` of a source and a target property (its `equate` clauses). Slot `j` is `(a_j, σ_j, b_j)`: a source relationship type, a rule, and a target relationship type. Two conditions: (V1) different rules have different source types and different target types; (V2) within one rule the `a_j` are pairwise distinct and the `b_j` are pairwise distinct.
A *triple* is `(S, C, T)`: a source graph `S` whose entities have types `S_ρ`, a target graph `T` whose entities have types `T_ρ`, both without dangling relationships, and a set `C` of correspondences `(ρ, x, x')` with `x` in `S` and `x'` in `T`.
Rule `ρ` *applies* to a triple at context entities `y_1 … y_m` of `S`, where `y_j` has type `S_{σ_j}` and a correspondence `(σ_j, y_j, y'_j)`. It adds an entity `x` of type `S_ρ` with the relationships `x -[a_j]-> y_j`, an entity `x'` of type `T_ρ` with the relationships `x' -[b_j]-> y'_j` and with `p'(x') = p(x)` for every `(p, p') ∈ eq_ρ`, and the correspondence `(ρ, x, x')`. Properties that no `equate` names are free on both sides. The language `L(G)` is the set of triples reachable from the empty triple by rule applications.

**Definition 8.8 (forward translation).** A source graph `S'` is *translatable* iff (F1) every entity of `S'` has a type `S_ρ`; (F2) every relationship of `S'` has its source in `S'`, and for every entity `x` of type `S_ρ` the relationships with source `x` are exactly one of type `a_j` for each slot `j` of `ρ`, each to an entity of `S'` of type `S_{σ_j}`; (F3) the relation "`x` has a relationship to `y`" on `S'` has no cycle.
For `(S, C, T) ∈ L(G)` and a source graph `S'`: if `S'` is not translatable, `FWD` rejects and changes nothing. Otherwise `FWD(S', C, T) = (S', C', T')`, where
- `C'` has one correspondence `(ρ, x, x')` for every entity `x` of `S'`, with `ρ` the rule of its type; `x'` is the target that `C` gives `x` if `C` has a correspondence of `x` under the same rule `ρ`, and otherwise the entity with the id `tid(ρ, x)` of the rule's `correspond … identity` expression. `tid` is an injective function of `(ρ, x)` (Theorem 1.4), and (V3) its value is not the id of a target entity that corresponds to another source entity;
- `T'` has, for every `(ρ, x, x') ∈ C'`, the entity `x'` of type `T_ρ` with `p'(x') = p(x)` for `(p, p') ∈ eq_ρ`, its other properties as in `T` if `x'` is in `T` and at the `on create` defaults otherwise, and the relationships `x' -[b_j]-> y'_j` for `x -[a_j]-> y_j` in `S'` and `(σ_j, y_j, y'_j) ∈ C'`. It has nothing else: the target of a source entity that is no longer in `S'` is removed with its free properties.
`BWD` is the same definition with the two sides exchanged.

**Theorem 8.7 (TGG laws for the v1 fragment, T12).** For a v1 grammar (Def. 8.7), `FWD` of Def. 8.8 and `(S, C, T) ∈ L(G)`:
- (a) *determinism*: whether `FWD(S', C, T)` rejects, and its result, are functions of `(S', C, T)`; no step chooses among rules or among matches;
- (b) *correctness*: if `FWD` accepts, its result is in `L(G)` and has the source `S'`;
- (c) *completeness*: if some triple `(S', C*, T*)` is in `L(G)`, then `FWD` accepts `S'`;
- (d) *hippocraticness*: `FWD(S, C, T) = (S, C, T)`.
`BWD` has the same four properties with the sides exchanged.
Status: **proved** on the model, restated. Read only, checked by no tool. There is no engine evidence and there can be none: the rule class `sync` is not built (D12), so the theorem is a statement about Def. 8.7 and 8.8 and experiment **Q4** (a toy `Service ↔ Component` grammar with three rules, all source graphs up to four nodes, `FWD` success against brute-force consistent completions) is the first test once it is. The first form spoke of a fragment (Def. 8.6) for which the paper defined no `FWD`, sketched (a) and left (b) to (d) as obligations; under Def. 8.6 alone (a) is false, see the counterexample. General conditions for correctness and completeness of derived operations are in [hermann2013]; negative application conditions are hard to carry into derived operations ([anjorin2016]), which is why v1 excludes them. Neither is used in the proof.

*Lemma (shape of the language).* `(S, C, T) ∈ L(G)` iff (L1) every entity of `S` and every entity of `T` is in exactly one correspondence, and `(ρ, x, x') ∈ C` has `x` of type `S_ρ` and `x'` of type `T_ρ`; (L2) `S` is translatable; (L3) for `(ρ, x, x') ∈ C` the relationships of `T` with source `x'` are exactly `x' -[b_j]-> y'_j`, one per slot of `ρ`, where `x -[a_j]-> y_j` in `S` and `(σ_j, y_j, y'_j) ∈ C`, and every relationship of `T` has its source in `T`; (L4) `p'(x') = p(x)` for `(ρ, x, x') ∈ C` and `(p, p') ∈ eq_ρ`.

*Proof of the lemma.* (Only if.) The empty triple satisfies L1 to L4. An application of `ρ` keeps them: the new `x` and `x'` are in the one new correspondence; `x` gets exactly its slot relationships and no existing entity gains a relationship that starts at it, so F2 holds; `x` has no incoming relationship, so no cycle appears; L3 and L4 hold for the new correspondence by the definition of the application and are untouched for the others. (If.) By induction on the number of entities of `S`. With none, `T` and `C` are empty by L1 and there is no relationship by F2 and L3: the empty triple. Otherwise F3 and finiteness give an entity `x` without incoming relationship. Let `(ρ, x, x')` be its correspondence. Then `x'` has no incoming relationship: by L3 a relationship of `T` into `x'` is `z' -[b]-> x'` for a relationship `z -[a]-> x` of `S`. Remove `x`, `x'`, the correspondence and the relationships that start at `x` and at `x'`. No remaining relationship pointed at `x` or `x'`, so nothing dangles, F2 and L3 hold for the remaining entities as before, L1 and L4 are restrictions, and a part of an acyclic relation is acyclic. By the induction hypothesis the rest is in `L(G)`. Rule `ρ` applies to it at the context entities `y_j` (present, of the right types and with correspondences by F2 and L1) and gives back `(S, C, T)` by F2, L3 and L4, the free properties being free. ∎

*Proof of the theorem.* (a) Translatability is a property of `S'`. For an entity `x`, the rule is the one with `S_ρ = type(x)`, unique by V1; the slot of each relationship of `x` is given by its type, by V2; `x'` is given by `C` or by `tid`; the properties are given by `eq_ρ`, by `T` or by the defaults. Every clause of Def. 8.8 is a function. (b) L1: `C'` has one correspondence per entity of `S'`; the targets are pairwise distinct, for kept entities by L1 of `(S, C, T)`, for new ones by the injectivity of `tid` and V3; `T'` has no other entity. L2 is translatability. L3 and L4 are the definition of `T'`. By the lemma the result is in `L(G)`. (c) The lemma gives L2 for `(S', C*, T*)`, so `S'` is translatable and `FWD` accepts. (d) `S` is translatable by L2. Every entity of `S` has a correspondence in `C` under the rule of its type (L1) and keeps its target, so `C' = C`. For each target the equated properties are already equal (L4), the others are kept, and its relationships in `T'` are those that L3 describes, which are its relationships in `T`. By L1 `T` has no other entity. ∎

*Counterexample to the first form.* Under Def. 8.6 alone, a rule may have two context slots of one relationship type: `b -[uses]-> a₁` and `b -[uses]-> a₂`, with the target relationships `primary` and `secondary`. For a new `b` with `uses` to `x` and to `y`, two translations exist, one with `primary` to `x'` and one with `primary` to `y'`, and both are in the language. `FWD` is not deterministic. The sketch of the first form argued that only one rule matches a created element and did not consider the choice of the match. V2 excludes the case.

## 8.5 Example

The business-capability view of [R03] section 8.4 (compact). Capabilities are authored in the master (a `bijective` ingestion rule over `sattle.authoring` rows). The view is a lens over active capabilities:

```text
rule capabilities lens {
  let active = match master (c: ops.Capability) |> where c.status != "retired";
  emit entity BizCapability from active identity c set { name: c.name };
  accept create, delete, update(name);
  on create key ops.capability default { capId: key.id, owner: "unassigned", status: "active" };
  on delete set { status: "retired" };
}
```

`δ`: `Match` is lens, `Filter` is lens, `EmitEntity` with an `identity c` and a field-reference set is iso, so `δ = lens` and the claim `lens` holds. An edit "rename capability `c₁` to `X`" is `update(name)`, accepted. It is routed to this rule (P2), inverted through the emit to the authored row of `c₁` (the other fields of the row, `owner` and `status`, are the complement, the pinned source), staged, checked (Theorem 8.5) and accepted as an authored-row upsert. Forward ingestion then updates the master and the view. Deleting a capability sets `status = retired`, so it leaves the view by the `Filter`, and `get(put(V, S)) = V` holds because retired rows fail `φ`. An edit of `serviceCount` is rejected `ReadOnlyByRule`, because the `counts` rule that owns it is `readonly` (it derives from a `Group`). By Theorem 8.2(a) a pure `Filter` lens is very well-behaved. This lens is not: its delete sets `status = retired`, so PutPut fails (shown below) and `putput` is declared false. Consecutive edits on this chain are therefore not squashed.

**Evidence from the prototype.** The engine runs this lens (`engine/definitions/acme.biz.sattle`; the `tree` rule of the same module is a second lens rule over `PartOf`, the `counts` rule is `readonly`). What the tests establish (`engine/tests/Sattle.Tests/LensTests.cs`):
- *Round trip.* `Create_rename_reparent_and_retire_flow_through_the_master_as_authored_rows`: create, rename, re-parent and retire reach the master as authored rows and come back in the view.
- *GetPut* (`GetPut_putting_back_the_unchanged_view_changes_nothing`): putting the unchanged view back leaves the state digest and the master revision unchanged.
- *PutGet and atomic rejection* (`PutGet_and_atomic_rejection_hold_on_generated_edit_sequences`): 8 seeded groups of 12 random sequences of up to 8 view edits (create, rename, add and delete an edge, delete with and without its edges). Every accepted edit is found in the new view, every rejected one changes nothing. This is the experiment Q3 of [R03] on one lens, with random edits and the engine as reference, not a finite reference model.
- *No PutPut* (`PutPut_does_not_hold_create_then_delete_leaves_a_retired_capability_but_the_final_view_state_alone_leaves_none`): create then delete leaves a `retired` capability in the master, while applying only the final view state to the original master leaves none. `putput` is declared false.
- *Rejections* (`Typed_rejections_name_the_rule_or_the_reason_and_leave_everything_unchanged`, `Master_edits_reject_readonly_elements_and_accept_the_authored_partition`, `Deleting_a_parent_without_its_edges_is_a_PutGet_violation_and_with_them_is_accepted`, `An_edge_to_a_retired_endpoint_and_a_cycle_are_rejected`): the codes asserted are `NotEditable`, `ReadOnlyByRule`, `StaleBase`, `PutGetViolation` and `ConstraintViolation`. Deleting a parent while its edges stay in the view is a `PutGetViolation`: the staged check of Theorem 8.5 catches it and no rule does. Deleting it together with its edges is accepted. A cycle and an edge to a retired endpoint are rejected. The other codes of `engine/PROTOCOL.md` (`NotAccepted`, `TelemetryNotEditable`, `AmbiguousKey`, `KeyMismatch`, `BadEdit`) are implemented but not asserted by these tests.
- *Scope* (`Edits_of_the_view_reproduce_in_the_view_and_the_master_is_changed_only_on_the_authored_partition`): the master changes only on the authored partition, which is Theorem 8.6 for this instance.
**Not established.** Theorem 8.3 on a chain of more than one lens link, a lens over a `Join`, the ingestion-sequence base of 8.4, activation rule S5 as a separate check, T12. The canvas has no edit UI for data views (`canvas/README.md`: "not built"); the protocol endpoint was exercised by tests, not by the canvas.

## 8.6 Delta to iteration 0.1

- **Gap 5.** Direction is declared by rule class, and per property through ownership. `ProjectionGraphInput` gets no direction mode, because the graph space stays a DAG and backward edits route against its edges without cycles.
- **`BidirectionalRule`.** Replaced by `Rule` with a class. Hand-written backward programs are removed because they cannot be checked against the laws. `LensLaw` is implied by the class. `CorrespondenceTemplate` with complement assignments is removed: identity-aligned lenses over stored sources need no complement, and `sync` rules declare `keep`.
- **`Correspondence`.** A kernel type with derived per-rule subtypes (typed graph data).
- **"Lens complement" index** of ARCHITECTURE.md exists only for `sync` rules and virtual sources.
- **`BackwardEdit` and `BackwardProposal`.** Kept, with a base revision per graph on the path and, proposed here, the ingestion sequence of the master (8.4).
- **S5** is a new activation rule.
- **Complement attribution** (R01 2.3): the contract text credits the source that [R01] lists as G2 ([foster2007]) for a stored complement. In [foster2007] asymmetric lenses take the old source in `put`. The stored-complement form comes from [hofmann2011] (and constant complement from Bancilhon and Spyratos, [bancilhon1981]). Sattle uses the old-source form for stored sources and the complement form for `sync`.
- **Terminology.** The contract's `Direction { Outgoing, Incoming, Either }` is traversal direction. `Port.direction` in [R03] is a port mode (`in`, `out`), not a sync direction.

## 8.7 Deviations found by the prototype

Ids refer to Ch. 14 (14.8).

- **D12.** `sync` rules and edit policies of a sync class are not built, for the reason in the status line. `Join` is not a lens step; its use inside a `readonly` rule is built and does not touch the backward path. `readonly`, `bijective` and `lens` are. The staged PutGet check (Theorem 8.5) is built and rejects with `PutGetViolation`. The base of an edit is the revisions of the graphs on the path (`baseRevisions`), without the ingestion sequence of 8.4. Only one lens link per edit path has been exercised.

## 8.8 Obligations

| Id | Statement | Discharged by |
|---|---|---|
| O8.1 | Theorem 8.2 (b), (c) and Theorem 8.3 on the contract types | Q3: FsCheck over random edit sequences on the capability view against the finite reference model |
| O8.2 | Theorem 8.7 (b) to (d) | Q4 |
| O8.3 | Which joins can be lens steps | Q9: key-preserving n:1 join puts, laws on a pod-to-node join |
| O8.4 | The ingestion-sequence base of 8.4 | design change, then concurrency test |
| O8.5 | S5 and `DirectionOverclaim` diagnostics | checker tests |
| O8.6 | Theorem 8.3 on a chain of two lens links, with the second view over the first | extend `LensTests.cs` with a view stacked on `capabilities`, generated edit sequences |
