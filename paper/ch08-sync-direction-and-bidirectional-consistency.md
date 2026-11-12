# Ch. 8 Editing a derived graph

**Question.** A person looks at a derived graph and changes it. When can that change be carried back to the graph it was computed from, and what guarantees that the result is the change the person made?

**What you can state at the end.** Every rule declares in which direction its output may be edited: not at all, or backwards by an exact inverse, or backwards by a lens. A lens is a pair of a forward and a backward function that obey two laws: putting back an unchanged view changes nothing, and what was put back is what is seen. These laws hold for the basic steps a rule is made of, and they survive when views are stacked on views. The engine checks the second law on every edit before it accepts it. An edit only ever changes data that people typed in; it never reaches a measurement, and never reaches Azure or Kubernetes. A fourth class, synchronization through correspondences, is defined and its laws are proved on a model; it is not built.

This chapter uses graphs and versions (Ch. 3), patches and `toPatch` (Ch. 4), rules, ownership and operators (Ch. 5), and the meaning of a projection and replay (Ch. 6).

**Running example.** The shop's definitions hold *capabilities*: things the business does, such as Storefront, Ordering and Data platform. People type them in; no source observes them. A derived graph, the *capability view*, shows every capability that is not retired. A person renames Ordering to Checkout in that view. The chapter follows that edit back to the master graph.

## 8.1 Direction

The word direction has several meanings: the direction of a relationship, the direction of a traversal (Ch. 5, Def. 5.3), the direction in which data flows. This chapter is about one only: the direction in which a change of a projection's output may travel back. A projection need not be editable at all. Where it is, the direction is declared.

Two terms are needed first.
- *Authored data* are data that a person typed in, such as the capabilities. They enter the engine as rows of an internal source, the *authoring source*, like any other input (Ch. 6, Def. 6.1).
- A *view* of a rule is the part of the output that the rule writes: the facts of the sinks it governs (Ch. 5, Def. 5.7, P2).

**Definition 8.1 (sync class).** Every projection is declared `forward` or contains rules, and every rule declares a class:

| Class | Declared on | What happens to an edit of the output | Laws owed |
|---|---|---|---|
| `forward` | projection | none. Any edit of its output is rejected `NotEditable` | determinism (Theorem 6.3) |
| `readonly` | rule | an edit that touches an element or property the rule owns is rejected `ReadOnlyByRule` before anything is written | determinism |
| `bijective` | rule | the inverse is derived from the rule | `inv(get(s)) = s` and `get(inv(v)) = v` |
| `lens` | rule | the backward function `put` is derived from backward steps, one per operator | GetPut, and PutGet on the accepted edits; PutPut only if declared |
| `sync` | rule | a forward and a backward translation derived from one rule over three graphs | correctness, completeness on the first fragment, hippocraticness (8.4) |

The names in code font, such as `NotEditable`, are the reasons the engine gives when it rejects an edit.

The unit of direction is the rule. By ownership (P2, Def. 5.7) each pair `(entity type, property)` and each relationship type is owned by at most one rule of the three editable classes. So direction is in effect declared **per sink and per property**, without marking individual ports. Telemetry is forward only everywhere: a telemetry property is never the target of an edit (`TelemetryNotEditable`), the operators that read measurements have no inverse, and a rule that sets telemetry or derives data through `Threshold` is `readonly` or sits in a `forward` projection.

*Example:* the rule that writes the capability view is a `lens`. A second rule of the same projection counts the services under each capability with a `Group`; it is `readonly`, so the count cannot be edited.

## 8.2 Lenses

A *lens* is the standard mathematical form of an editable view ([foster2007]). It pairs the function that computes the view with a function that puts an edited view back.

**Definition 8.2 (asymmetric lens with complement).** For a rule with source `S` (the integrated input graphs and rows it reads) and view `V` (the facts of the sinks it governs):
- `get : S → V` is the meaning of the rule (Def. 6.4, restricted to its sinks);
- `put : V × S → S` takes an edited view and the old source and returns the new source;
- the **complement** is the part of `S` that `get` leaves out. The engine keeps the source graph, and it matches view and source by ids that never change (Ch. 1). So a lens over a stored source needs no separate store for the complement: the complement is the source at the version the edit was based on, the old source argument of `put`, as in [foster2007]. In the form with a constant complement of [bancilhon1981] this is `put : V × X → S`, with `X` the part left out; a created element takes the rule's `on create` default for it. Symmetric lenses ([hofmann2011]) keep the complement as a state of their own.
- Laws, where the functions are defined:
  - **GetPut**: `put(get(s), s) = s`;
  - **PutGet**: `get(put(v, s)) = v` for `v` in the *accepted edit domain* `Acc(s)`;
  - **PutPut** (optional): `put(v₂, put(v₁, s)) = put(v₂, s)`.
A lens rule has three clauses: `accept` names the edits it takes back, `on create … default` gives the values a created element lacks, and `on delete set` gives the values a deleted element receives. A delete is carried back only as that update; a rule without `on delete set` refuses deletes `NotAccepted`.

A lens is *well-behaved* if GetPut and PutGet hold, and *very well-behaved* if PutPut holds as well. The accepted domain is given by the rule's `accept` clauses (`create`, `delete`, `update(p, …)`).

In words: GetPut says that putting back an unchanged view changes nothing. PutGet says that after an accepted edit, the view shows exactly the edit. PutPut says that two edits in a row have the same effect as the second alone.

*Example:* `get` of the capability view keeps the capabilities whose status is not `retired` and shows their names. Renaming Ordering to Checkout is an edited view `v`. `put(v, s)` changes the name in the authored row of Ordering and keeps its other fields, owner and status, from `s`: they are the complement.

**Definition 8.3 (backward edit).** A *backward edit* is a patch `p_V` on a derived graph `G_k`. It is a proposal, not a commit. It names the version it was based on for every graph on its path: the view, each graph in between, and the master. It is matched to the source by identity: element ids never change (Ch. 1), so the difference between the view before and after is known. (The literature on *delta lenses* separates finding that difference from carrying it back, [diskin2011].) The backward result is `p_S = toPatch(χ(put(apply(p_V, V), S)) − χ(S), S)` (Def. 4.9).

**Definition 8.4 (information class).** `η ∈ {iso < lens < none}` says how much of its input an operator keeps. `iso` means the input can be recovered from the output alone, `lens` that it can be recovered with the complement, and `none` that it cannot. This is different from the cost classes of Ch. 6 (linear, bilinear, per key): `Filter` is linear but cannot be inverted without the complement.

| Operator | η | Reason |
|---|---|---|
| `Match` | lens | complement: the unmatched part of the input graph |
| `Filter` | lens | complement: the rows that fail. An edited row must still satisfy the predicate |
| `Map` | iso if every input field appears exactly once, as a field reference or as an argument of a one-to-one function with an inverse, and no output is a literal; lens if fields are dropped or repeated, or outputs are literals (edits must keep them); none if an output uses a function that is not one-to-one | a function that is not one-to-one loses information |
| `Union` | lens for `preferFirst` or `preferLast`, and for any policy when the inputs have different types; none otherwise | the input a row came from must be known |
| `EmitEntity`, `EmitRelationship` | iso if the id is a field reference or `IdOf` over field references and every set value is a field reference; lens if some set values are literals (edits must keep them); none otherwise | identity is never edited. Computed values lose information |
| `Join` | none in version 1 | an update through a join is ambiguous unless the keys are analysed |
| `Distinct`, `Group`, `Rollup`, `Traverse`, `Window`, `Fixpoint`, `Values`, `Link`, `Series`, `Delta`, `Threshold` | none | many rows give one, derived paths, constants, evidence, measurements over time |

`IdOf` over field references is inverted through the binding set of Ch. 1 (Def. 1.10): from the id back to the key that names it. If an entity has several keys in that domain, the edit is to be rejected `AmbiguousKey`; the prototype does not yet check the number of keys.

**Definition 8.5 (direction judgment).** `η(rule) = max{η(o)}` over the operators `o` on any path from an input port of the projection to a sink governed by the rule.
- `bijective` requires `η = iso`, one input stream, every input field recovered by some emit, every emit in mode `create`, and P2.
- `lens` requires `η ≤ lens`, and that the paths of its sinks reach exactly one input port of the projection, so an edit never has to be split across inputs.
- `sync` requires the first fragment (8.4).
- `readonly` has no condition.
A rule that claims more than its `η` is refused with `DirectionOverclaim(rule, operator)`, naming the first operator that raised `η`.

*Example:* in the capability view, `Match` is lens, the `Filter` on status is lens, and the `EmitEntity` with `identity c` and a set value that is a field reference is iso. So `η = lens`, and the claim `lens` holds.

## 8.3 Laws and composition

**Theorem 8.1 (bijective rules).** If `inv(get(s)) = s` and `get(inv(v)) = v`, then with `put(v, s) := inv(v)` all three laws hold: GetPut, PutGet, PutPut.

*Proof.* GetPut: `put(get s, s) = inv(get s) = s`. PutGet: `get(put(v, s)) = get(inv v) = v`. PutPut: `put(v₂, put(v₁, s)) = inv(v₂) = put(v₂, s)`. ∎

**Theorem 8.2 (the lens steps).**
- (a) `Filter` with predicate `φ`: with `get(S) = {r ∈ S | φ(r)}`, `put(V, S) = V ∪ {r ∈ S | ¬φ(r)}` and `Acc(S) = {V | every row of V satisfies φ and no new row of V has the key of a failing row of S}`, the lens is very well-behaved.
- (b) `Map` that drops fields and keeps the key: the complement is the dropped fields of `s`, matched by key. `put` merges the edited kept fields with the old dropped fields, gives a created row the `on create` default for the dropped fields, and removes a source row whose key is not in the view. This lens is well-behaved. It is very well-behaved on the sequences of edits in which no key is deleted and then created again, and not in general.
- (c) A `Match` of one pattern element is a `Filter` over the facts of the input graph. `EmitEntity` and `EmitRelationship` whose identity is a one-to-one function of the row and whose other set values are field references or literals are lenses that are bijections on their accepted domain. That domain excludes edits of the literal values, and elements whose identity has more than one key in the domain: after a merge or an alias (Ch. 1) two rows give one element, the map from rows to elements is not one-to-one, and an edit of such an element is to be rejected `AmbiguousKey` (Def. 8.4).

The proofs below have been read, and no program has checked them. One lens is tested with generated sequences of edits (8.5); a generated test of the steps themselves does not exist yet. A pattern of several elements is a join, and `Join` is not a lens step, so for it PutGet is checked when the edit is made (Theorem 8.5) and not proved.

*Proof of (a).* GetPut: `put(get S, S) = get S ∪ failing(S) = S`. PutGet: for `V ∈ Acc(S)`, `get(V ∪ failing(S)) = V ∪ ∅ = V`, since the failing rows do not satisfy `φ`. PutPut: the failing rows of `put(V₁, S)` are those of `S`, because `V₁` satisfies `φ`, so `put(V₂, put(V₁, S)) = V₂ ∪ failing(S) = put(V₂, S)`. ∎

*Proof of (b).* Rows have a key that the map keeps, and keys are unique in `S` and in every accepted `V`. Write `keep(r)` for the kept fields of `r` and `d(r)` for its dropped fields. `get(S) = {keep(r) | r ∈ S}`. `put(V, S)` holds, for each `v ∈ V`, the row `v ⊕ d(r)` if `S` has a row `r` with the key of `v`, and `v ⊕ default` otherwise. GetPut: every `v = keep(r)` meets its own `r`, so `put(get S, S) = {keep(r) ⊕ d(r)} = S`. PutGet: `keep(v ⊕ x) = v` for any `x`, so `get(put(V, S)) = V`. PutPut, when `V₁` holds every key of `S` that `V₂` holds: for such a key the dropped fields in `put(V₁, S)` are `d(r)`, the same as in `S`, and for a key of `V₂` that is not in `S` both sides give the default, so `put(V₂, put(V₁, S)) = put(V₂, S)`. ∎

*Counterexample to PutPut without the condition.* `S = {(k, a: 1, d: 7)}` with `d` dropped and default 0, `V₁ = ∅`, `V₂ = {(k, a: 1)}`. Then `put(V₂, put(V₁, S)) = {(k, 1, 0)}` and `put(V₂, S) = {(k, 1, 7)}`. A rule over such a step must therefore declare `putput` false unless it does not accept `delete`.

*Proof of (c).* A `Match` of one pattern element selects the facts of the input graph that have the type of the element and satisfy its property predicates. That is `get` of (a) with `φ` the conjunction of the type test and the predicates, over the set of facts, so (a) applies. An emit maps a row to an element by a one-to-one renaming of fields to properties, an identity that is a one-to-one function of the row's key fields (Theorem 1.4) as long as no two keys are forwarded to one representative, and constants `c` for the literal set values. On the accepted domain, the elements whose literal properties equal `c` and whose identity has one key, this map has an inverse: read the fields back from the properties. A map with an inverse satisfies GetPut, PutGet and PutPut with `put(V, S) = get⁻¹(V)` (Theorem 8.1). ∎

Views can be stacked: a projection may read the output of another one. An edit of the upper view is then carried back through each level in turn.

**Theorem 8.3 (laws survive stacking).** Let `(get₁, put₁)` and `(get₂, put₂)` be lenses and `get = get₂ ∘ get₁`, `put(v, s) = put₁(put₂(v, get₁ s), s)`, `Acc(s) = {v ∈ Acc₂(get₁ s) | put₂(v, get₁ s) ∈ Acc₁(s)}`. If both are well-behaved, so is the composition. If both are very well-behaved, so is the composition. PutPut of the composition needs PutPut of every link.

This is the composition result for asymmetric lenses of [foster2007]; the proof here is stated for the accepted domains.

*Proof.* GetPut: `put(get s, s) = put₁(put₂(get₂ get₁ s, get₁ s), s) = put₁(get₁ s, s) = s` by GetPut₂ then GetPut₁. PutGet: let `w = put₂(v, get₁ s) ∈ Acc₁(s)`. Then `get(put(v, s)) = get₂(get₁(put₁(w, s))) = get₂(w) = v` by PutGet₁ then PutGet₂. PutPut: with `g = get₁ s` and `w₁ = put₂(v₁, g)`, the state `s₁ = put₁(w₁, s)` has `get₁ s₁ = w₁` (PutGet₁), so `put(v₂, s₁) = put₁(put₂(v₂, w₁), s₁)`. PutPut₂ gives `put₂(v₂, put₂(v₁, g)) = put₂(v₂, g)`, and PutPut₁ gives `put₁(put₂(v₂, g), put₁(w₁, s)) = put₁(put₂(v₂, g), s) = put(v₂, s)`. Without PutPut of both links the last two steps fail. ∎

**Theorem 8.4 (class of a stack).** The class of an edit along a path of stacked projections is that of its weakest link: `bijective ∘ bijective = bijective`, `lens ∘ lens = lens`, `bijective ∘ lens = lens`, and any `forward` or `readonly` link on the path rejects the edit and names that link. Version 1 allows at most one `sync` link per path. PutPut holds only if every link declares it. An edit on `G_k` is sent to the rule that owns the touched element or property (P2, unique), applied as a proposal on `G_{k-1}`, and so on down to the master.

*Proof.* The first two cases are Theorems 8.1 and 8.3; a bijective link is a very well-behaved lens by Theorem 8.1, which gives the mixed case. The owning rule is unique by P2. ∎

**Theorem 8.5 (the law is checked on every edit).** Before an edit is accepted, the engine evaluates the master graph and the view again on a copy that is not yet in use (a *staged* copy), and compares the view it gets with the edited view, as graphs (Def. 3.4). Any difference rejects the edit with `PutGetViolation`, and nothing is accepted or committed. Hence every accepted edit satisfies PutGet on its own instance, even when a derived backward step is defective.

*Proof.* The check is the statement `get(put(v, s)) = v` evaluated on the instance. Acceptance requires it to hold. ∎

One more rule is checked when a space is made active, in addition to S1 to S4 of Def. 5.8. It is called S5 here; it is not the property S5 of a store in Ch. 11.
- **S5.** Every rule whose input port is bound to a `Source` other than the authoring source is `readonly`.

That observed facts are owned by `readonly` rules was a convention of authors. S5 makes it a rule that the checker enforces.

**Theorem 8.6 (scope of backward edits).** No backward edit reaches a telemetry property, and no backward edit reaches Azure or Kubernetes. It holds given rule S5.

*Proof.* An edit that touches a telemetry property is rejected `TelemetryNotEditable` (8.1). A chain ends at the authored part of the master, whose ingestion rules over rows from the authoring source are `bijective`. Observed facts (from Kubernetes, the network observer, Azure) are owned by `readonly` rules by S5. ∎

## 8.4 Where an edit lands, and synchronization

- **Target.** A backward edit changes only data inside the engine. The proposal becomes authored rows that are added, changed or withdrawn in a table keyed by the rows' keys, so it is a signed delta (Ch. 4). After the check of Theorem 8.5 those rows are stored so that they cannot be lost, and they flow forward like any other input.
- **Replay.** Because the edit becomes recorded input, replay determinism is unchanged (Theorem 6.3).
- **Concurrent edits.** An edit carries the base version of every graph on its path, and any base that is no longer the head rejects it. A base of versions alone is not enough: an input that the engine has stored but not yet processed is not visible in any version. So the base must also name the ingestion sequence (Ch. 3, 3.4) of the last stored input of the master, and the edit must be accepted directly after that input; otherwise the staged check of Theorem 8.5 may not describe the state that processes the rows. This refinement is proposed here and not built.
- **No PutPut by default.** Edits apply one at a time against exact bases. Several edits of a view may be combined into one before `put` only on chains where every link declares `putput`.

*Example:* the rename of Ordering to Checkout is `update(name)`, which the rule accepts. It is sent to the capability rule (P2), inverted through the emit to the authored row of Ordering, whose other fields are the complement, checked on a staged copy (Theorem 8.5), and stored as a changed authored row. Forward evaluation then changes the master graph and the view. The rule's clause `on delete set { status: "retired" }` turns a delete into an update of the authored row. So a deleted capability leaves the view by the `Filter`, and PutGet holds on a delete because the values of `on delete set` make the row fail the predicate. The removal of a source row of Theorem 8.2 (a) never runs here, so this lens is not a composition of the steps of 8.2; its PutGet on deletes rests on that condition of the rule, which no check enforces and the staged check of Theorem 8.5 catches. That delete is why this lens is not very well-behaved, unlike a pure `Filter` (Theorem 8.2(a)): creating a capability and then deleting it leaves a retired row in the master, while putting only the final view, which lacks it, leaves none. The rule declares `putput` false, and consecutive edits on this chain are not combined.

The other classes keep no state of their own. Synchronization does: it is for a target that holds information that no input has.

A *triple graph grammar* relates two graphs through a third one, the *correspondence* graph: a triple `S ← C → T` of source, correspondences and target, where each correspondence links an element of `S` to an element of `T` ([anjorin2016]). In a graph space every graph except the master is derived, so information that only the target has must live in a stored correspondence graph.

**Definition 8.6 (sync rule, correspondence).** A `sync` rule relates a source pattern and a target pattern through a correspondence node. For each `sync` rule, activation derives an entity type `<projection>#<rule>` that extends the kernel type `Correspondence` with one property per `keep` path (Ch. 2, Def. 2.12). The *first fragment*, called v1: each rule relates one created source node to one created target node and one correspondence; context nodes and edges are allowed; there are no negative conditions on when a rule applies; conditions on properties are equalities of field references only (`equate`); and source patterns of different rules range over disjoint types.

Def. 8.6 names the fragment. The next two definitions make it exact enough to prove something about it. The rule class is not built, so they define a model and no program.

**Definition 8.7 (v1 sync grammar, triple, language).** A *v1 grammar* is a finite set of rules. A rule `ρ` has a source entity type `S_ρ`, a target entity type `T_ρ`, a finite list of *context slots* `j = 1 … m_ρ`, and a finite set `eq_ρ` of pairs `(p, p')` of a source and a target property (its `equate` clauses). Slot `j` is `(a_j, σ_j, b_j)`: a source relationship type, a rule, and a target relationship type. Two conditions: (V1) different rules have different source types and different target types; (V2) within one rule the `a_j` are pairwise distinct and the `b_j` are pairwise distinct.
A *triple* is `(S, C, T)`: a source graph `S` whose entities have types `S_ρ`, a target graph `T` whose entities have types `T_ρ`, both without dangling relationships, and a set `C` of correspondences `(ρ, x, x')` with `x` in `S` and `x'` in `T`.
Rule `ρ` *applies* to a triple at context entities `y_1 … y_m` of `S`, where `y_j` has type `S_{σ_j}` and a correspondence `(σ_j, y_j, y'_j)`. It adds an entity `x` of type `S_ρ` with the relationships `x -[a_j]-> y_j`, an entity `x'` of type `T_ρ` with the relationships `x' -[b_j]-> y'_j` and with `p'(x') = p(x)` for every `(p, p') ∈ eq_ρ`, and the correspondence `(ρ, x, x')`. Properties that no `equate` names are free on both sides. The *language* `L(G)` is the set of triples reachable from the empty triple by applications of rules.

*Example:* a grammar with one rule relates each `Service` of a source to one `Component` of a target and equates their names. Applying it to an empty triple at no context adds a service `x`, a component `x'` with the same name, and the correspondence `(ρ, x, x')`.

**Definition 8.8 (forward translation).** A source graph `S'` is *translatable* iff (F1) every entity of `S'` has a type `S_ρ`; (F2) every relationship of `S'` has its source in `S'`, and for every entity `x` of type `S_ρ` the relationships with source `x` are exactly one of type `a_j` for each slot `j` of `ρ`, each to an entity of `S'` of type `S_{σ_j}`; (F3) the relation "`x` has a relationship to `y`" on `S'` has no cycle.
For `(S, C, T) ∈ L(G)` and a source graph `S'`: if `S'` is not translatable, `FWD` rejects and changes nothing. Otherwise `FWD(S', C, T) = (S', C', T')`, where
- `C'` has one correspondence `(ρ, x, x')` for every entity `x` of `S'`, with `ρ` the rule of its type. `x'` is the target that `C` gives `x` if `C` has a correspondence of `x` under the same rule `ρ`, and otherwise the entity with the id `tid(ρ, x)` of the rule's `correspond … identity` expression. `tid` is a one-to-one function of `(ρ, x)` (Theorem 1.4), and (V3) its value is not the id of a target entity that corresponds to another source entity;
- `T'` has, for every `(ρ, x, x') ∈ C'`, the entity `x'` of type `T_ρ` with `p'(x') = p(x)` for `(p, p') ∈ eq_ρ`, its other properties as in `T` if `x'` is in `T` and at the `on create` defaults otherwise, and the relationships `x' -[b_j]-> y'_j` for `x -[a_j]-> y_j` in `S'` and `(σ_j, y_j, y'_j) ∈ C'`. It has nothing else: the target of a source entity that is no longer in `S'` is removed with its free properties.
`BWD` is the same definition with the two sides exchanged.

(F1 to F3 here are conditions on a source graph; they are not the premise F1 of Theorem 3.5.)

**Theorem 8.7 (laws of the first sync fragment).** For a v1 grammar (Def. 8.7), `FWD` of Def. 8.8 and `(S, C, T) ∈ L(G)`:
- (a) *determinism*: whether `FWD(S', C, T)` rejects, and its result, are functions of `(S', C, T)`; no step chooses among rules or among matches;
- (b) *correctness*: if `FWD` accepts, its result is in `L(G)` and has the source `S'`;
- (c) *completeness*: if some triple `(S', C*, T*)` is in `L(G)`, then `FWD` accepts `S'`;
- (d) *hippocraticness*: a triple that is already in the language is left as it is, `FWD(S, C, T) = (S, C, T)`.
`BWD` has the same four properties with the sides exchanged.

The proof below has been read, and no program has checked it. There can be no test on the engine, because the class `sync` is not built. The first test planned once it is: a grammar with three rules between services and components, every source graph of up to four nodes, and `FWD` compared with every consistent completion found by search. General conditions for correctness and completeness of derived translations are in [hermann2013]. Negative conditions are hard to carry into derived translations ([anjorin2016]), which is why v1 excludes them. Neither result is used in the proof.

*Lemma (shape of the language).* `(S, C, T) ∈ L(G)` iff (L1) every entity of `S` and every entity of `T` is in exactly one correspondence, and `(ρ, x, x') ∈ C` has `x` of type `S_ρ` and `x'` of type `T_ρ`; (L2) `S` is translatable; (L3) for `(ρ, x, x') ∈ C` the relationships of `T` with source `x'` are exactly `x' -[b_j]-> y'_j`, one per slot of `ρ`, where `x -[a_j]-> y_j` in `S` and `(σ_j, y_j, y'_j) ∈ C`, and every relationship of `T` has its source in `T`; (L4) `p'(x') = p(x)` for `(ρ, x, x') ∈ C` and `(p, p') ∈ eq_ρ`.

*Proof of the lemma.* (Only if.) The empty triple satisfies L1 to L4. An application of `ρ` keeps them: the new `x` and `x'` are in the one new correspondence; `x` gets exactly its slot relationships and no existing entity gains a relationship that starts at it, so F2 holds; `x` has no incoming relationship, so no cycle appears; L3 and L4 hold for the new correspondence by the definition of the application and are untouched for the others. (If.) By induction on the number of entities of `S`. With none, `T` and `C` are empty by L1 and there is no relationship by F2 and L3: the empty triple. Otherwise F3 and finiteness give an entity `x` without incoming relationship. Let `(ρ, x, x')` be its correspondence. Then `x'` has no incoming relationship: by L3 a relationship of `T` into `x'` is `z' -[b]-> x'` for a relationship `z -[a]-> x` of `S`. Remove `x`, `x'`, the correspondence and the relationships that start at `x` and at `x'`. No remaining relationship pointed at `x` or `x'`, so nothing dangles, F2 and L3 hold for the remaining entities as before, L1 and L4 are restrictions, and a part of an acyclic relation is acyclic. By the induction hypothesis the rest is in `L(G)`. Rule `ρ` applies to it at the context entities `y_j` (present, of the right types and with correspondences by F2 and L1) and gives back `(S, C, T)` by F2, L3 and L4, the free properties being free. ∎

*Proof of the theorem.* (a) Translatability is a property of `S'`. For an entity `x`, the rule is the one with `S_ρ = type(x)`, unique by V1; the slot of each relationship of `x` is given by its type, by V2; `x'` is given by `C` or by `tid`; the properties are given by `eq_ρ`, by `T` or by the defaults. Every clause of Def. 8.8 is a function. (b) L1: `C'` has one correspondence per entity of `S'`; the targets are pairwise distinct, for kept entities by L1 of `(S, C, T)`, for new ones because `tid` is one-to-one and by V3; `T'` has no other entity. L2 is translatability. L3 and L4 are the definition of `T'`. By the lemma the result is in `L(G)`. (c) The lemma gives L2 for `(S', C*, T*)`, so `S'` is translatable and `FWD` accepts. (d) `S` is translatable by L2. Every entity of `S` has a correspondence in `C` under the rule of its type (L1) and keeps its target, so `C' = C`. For each target the equated properties are already equal (L4), the others are kept, and its relationships in `T'` are those that L3 describes, which are its relationships in `T`. By L1 `T` has no other entity. ∎

*Why V2 is needed.* Without it, a rule may have two context slots of one relationship type: `b -[uses]-> a₁` and `b -[uses]-> a₂`, with the target relationships `primary` and `secondary`. For a new `b` with `uses` to `x` and to `y`, two translations exist, one with `primary` to `x'` and one with `primary` to `y'`, and both are in the language. `FWD` is then not deterministic. An earlier argument for (a) said that only one rule matches a created element and did not consider the choice of the match. V2 excludes the case.

## 8.5 What the prototype does and does not do

**What is built.** The classes `readonly`, `bijective` and `lens`; backward edits sent to the owning rule; the staged check of Theorem 8.5; and rejections with their reasons. The class `sync` and its correspondences are not built: the engine stores no correspondence graph and has no forward and backward translations, and a `sync` rule is refused with an error. The inverse of an ingestion rule is derived only for one entity sink and one relationship sink over one authored rows port; any other ingestion graph is `NotEditable`. `Join` is not a lens step; inside a `readonly` rule it is built and does not touch the backward path. Every `Union` of the prototype is `none`, since only `preserveEvidence` is built, so no lens contains a `Union`. The base of an edit is the versions of the graphs on its path, without the ingestion sequence of 8.4.

**One lens exists**, the capability view of the running example. A second rule of the same module is a lens over the hierarchy of capabilities, and the rule that counts services is `readonly`.

**What the tests show.** The authors read these tests and did not run them; the runs are among those of Ch. 14 that they did not repeat.
- *Round trip.* Create, rename, move under another parent and retire reach the master as authored rows and come back in the view.
- *GetPut* holds by construction: an edit that leaves an element unchanged is dropped before any backward step runs. The test confirms that such an edit changes nothing; it does not exercise a backward step.
- *PutGet, and rejection that changes nothing.* 8 seeded groups of 12 random sequences of up to 8 edits of the view (create, rename, add and delete an edge, delete with and without its edges). Every accepted edit is found in the new view, and every rejected one changes nothing. The engine itself is the reference here, not a separate finite model.
- *No PutPut.* Create then delete leaves a retired capability in the master; putting only the final view on the original master leaves none.
- *Rejections.* The tests assert the reasons `NotEditable`, `ReadOnlyByRule`, `StaleBase` (a base that is no longer the head), `PutGetViolation` and `ConstraintViolation`. Deleting a parent while its edges stay in the view is a `PutGetViolation`: the staged check catches it, and no rule does. Deleting it together with its edges is accepted. A cycle and an edge to a retired capability are rejected. The reasons `TelemetryNotEditable`, `NotAccepted`, `KeyMismatch` and `BadEdit` are built and not asserted by these tests; `AmbiguousKey` is not built, and no shipped definition reaches its case.
- *Scope.* The master changes only in its authored part, which is Theorem 8.6 for this instance.

**Not established.** Theorem 8.3 on a chain of more than one lens; a lens over a `Join`; the ingestion-sequence base of 8.4; rule S5 as a check of its own; Theorem 8.7 on any program. The viewer has no way to edit the data of a view; the edits were made by tests through the engine's interface.
