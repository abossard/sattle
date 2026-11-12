# Ch. 4 Algebra of changes

**Question.** How do changes of a graph combine, how is a change undone, and how does a change relate to an arithmetic of differences that can be added in any order?

**What you can state at the end.** There are two kinds of change, and they are kept apart on purpose. A *patch* is ordered, partial and checked against what it expects to find; every patch has an inverse. A *signed delta* is a difference that can be added in any order and always exists, but may not describe a graph. A map from patches to deltas respects composition and inversion. Editing definitions uses the same algebra: an edit is a patch, and undo is the inverse patch.

This chapter uses edits, `apply`, versions and well-formed graphs from Ch. 3, and canonical form from Ch. 1.

**Running example.** In the shop, one key `k` holds the service `db`. Its states are `a` (`db` with no owner), `b` (`db` owned by team `t1`) and `c` (`db` owned by team `t2`). When the graph and the versions are clear, a patch is written by its edits alone: `(k, a, b)` is the patch with the one edit that gives `db` the owner `t1`.

## 4.1 Patches

**Definition 4.1 (patch).** A *patch* is `p = (g, r, r', E, cause)`: a graph `g`, two versions `from(p) = r` and `to(p) = r'`, a finite set `E` of edits with pairwise different keys (Ch. 3, Def. 3.6), and a cause (the inputs that led to it and the version of the definitions). An edit in a patch may be *guard-only*, `b = a`: it checks a state and changes nothing. A committed patch, the edit set of a commit record with `r' = r + 1`, holds effective edits only. Two patches are equal iff their graph, versions and edits are equal; the cause is carried along and is not part of equality.

`apply(p, G) = apply(E, G)` (Ch. 3, Def. 3.7). The edits of a patch are a **set**: the order in which they are listed means nothing.

*Example:* `p = (shop, 4, 5, {(k, a, b)})` gives `db` an owner.

**Definition 4.2 (composition, `then`).** For patches `p = (g, r, r', E_p)` and `q = (g, r', r'', E_q)`, `p then q` is defined iff for every key `k` that both edit, the after-state of `p` on `k` equals the before-state of `q` on `k` by `≈` (Ch. 1, Def. 1.6). Then `p then q = (g, r, r'', E_p ⊕ E_q)`, where `⊕` keeps the edits of keys that only one patch edits, and joins the two edits of a shared key as `(k, b, a) ⊕ (k, a, c) = (k, b, c)`. **A joined edit `(k, b, b)` is kept, as a guard-only edit.** The causes are concatenated.

*Example:* `(k, a, b) then (k, b, c)` is `(k, a, c)`. `(k, a, b) then (k, c, a)` is not defined: the second patch expects `c` where the first leaves `b`.

**Definition 4.3 (inverse, guard identity).** `inverse(p) = (g, r', r, {(k, a, b) | (k, b, a) ∈ E_p})`, with the cause "undo of `p`". The *guard identity* of `p` is `ε(p) = (g, r, r, {(k, b, b) | (k, b, a) ∈ E_p})`: it checks the states that `p` expects and changes nothing. An undo is committed as a new version and never moves the log back.

*Example:* `inverse((k, a, b)) = (k, b, a)`, and `ε((k, a, b)) = (k, a, a)`.

**Definition 4.4 (net form).** `net(p)` is `p` without its guard-only edits. It loses information: which keys were checked (Theorem 4.8).

## 4.2 Facts and signed deltas

**Definition 4.5 (fact).** A *fact* is a pair `f = (k, s)` of a key and a state (never `⊥`), taken in canonical form. `F` is the set of facts.

**Definition 4.6 (signed delta).** `ℤ^(F)` is the set of maps `z : F → ℤ` that are zero except on finitely many facts, with addition fact by fact. A value `z(f)` is the *weight* of `f`.

**Definition 4.7 (embedding, delta of a patch).** The *embedding* of a graph is `χ(G) = Σ_{k ∈ dom G} 1·(k, G(k))`: each of its facts with weight 1. With `χ₁(⊥) = 0` and `χ₁(s) = 1·(k, s)`, the *delta* of a patch is

`δ(p) = Σ_{(k,b,a) ∈ E_p} (χ₁(a) − χ₁(b))`.

A guard-only edit contributes `0`.

*Example:* `δ((k, a, b)) = +1·(k, b) − 1·(k, a)`.

**Definition 4.8 (materialization).** `z` is *materializable* iff every weight is 0 or 1, at most one fact per key has weight 1, and the map `k ↦ s` that the facts of weight 1 give is a graph, each state under its own key (Ch. 3, Def. 3.2), that satisfies the uniqueness rule `U` (Ch. 3, Def. 3.3). For the delta of a valid patch this holds (Theorem 4.8); for an arbitrary `z` it need not. For a well-formed graph `B`, `m_B(z) = χ⁻¹(χ(B) + z)` is defined iff `χ(B) + z` is materializable.

**Definition 4.9 (conversions, squash, loss).**
- `toDelta(p) = δ(p)`.
- `toPatch(z, B, r, r')` is defined iff `m_B(z)` is defined. It then has, for each key `k` whose facts occur in `z`, the edit `(k, B(k), m_B(z)(k))`, except edits whose before and after are equal. It is the net transition, not the history that led to it.
- `squash(p₁, …, pₙ) = net(p₁ then … then pₙ)`, together with three flags of what was lost: the order (true iff `n > 1`), intermediate states (true iff some key is edited by two or more of the patches), and detail of the causes (true iff causes were merged).

## 4.3 Theorems on patches

Write `keys(p)` for the keys that `p` edits, and `bef_p(k)` and `aft_p(k)` for the before- and after-state of `p` on `k`.

**Theorem 4.1 (associativity).** `(p then q) then u` is defined iff `p then (q then u)` is defined, and then they are equal. Here "defined" includes the inner composition.

*Proof.* Let `aft_{pq}(k) = aft_q(k)` if `k ∈ keys(q)` and `aft_p(k)` otherwise, and `bef_{qu}(k) = bef_q(k)` if `k ∈ keys(q)` and `bef_u(k)` otherwise. Because joined guard-only edits are kept, `keys(p then q) = keys(p) ∪ keys(q)`.
The left side is defined iff (i) `aft_p = bef_q` on `keys(p) ∩ keys(q)`, and (ii) `aft_{pq}(k) = bef_u(k)` for `k ∈ (keys(p) ∪ keys(q)) ∩ keys(u)`.
The right side is defined iff (i') `aft_q = bef_u` on `keys(q) ∩ keys(u)`, and (ii') `aft_p(k) = bef_{qu}(k)` for `k ∈ keys(p) ∩ (keys(q) ∪ keys(u))`.
Split (ii) by `k ∈ keys(q)`, which gives `aft_q = bef_u`, or `k ∈ keys(p) \ keys(q)`, which gives `aft_p = bef_u`. Split (ii') by `k ∈ keys(q)`, which gives `aft_p = bef_q`, or `k ∈ keys(u) \ keys(q)`, which gives `aft_p = bef_u`. Both sides come down to the same conditions: `aft_p = bef_q` on `keys(p) ∩ keys(q)`, `aft_q = bef_u` on `keys(q) ∩ keys(u)`, and `aft_p = bef_u` on `(keys(p) ∩ keys(u)) \ keys(q)`. The edit of each side on a key joins the consecutive edits of that key, and joining is associative: `(b, a) ⊕ (a, c) ⊕ (c, d) = (b, d)` in either bracketing. The versions `from` and `to` agree. ∎

Keeping joined guard-only edits matters here. If `(k, b, b)` were dropped, then after `p` edits `k` from `b` to `a` and `q` edits it back to `b`, a patch `u` with a wrong before-state on `k` would compose with `p then q` but not with `q`, so `(p then q) then u` would be defined and `p then (q then u)` would not. Dropping it would also break Theorem 4.2: the shortened composite would apply to graphs on which `p` does not.

**Theorem 4.2 (applying a composition).** If `p then q` is defined, then `apply(p then q, G)` is defined iff `apply(p, G)` and `apply(q, apply(p, G))` are defined, and then they are equal.

*Proof.* The guards of `p then q` on `G` are `G(k) ≈ bef_p(k)` for `k ∈ keys(p)` and `G(k) ≈ bef_q(k)` for `k ∈ keys(q) \ keys(p)`. Let `G' = apply(p, G)`. The guards of `q` on `G'` are: for `k ∈ keys(q) ∩ keys(p)`, `G'(k) = aft_p(k) ≈ bef_q(k)`, which holds because the composition is defined; for `k ∈ keys(q) \ keys(p)`, `G'(k) = G(k) ≈ bef_q(k)`. So the two sets of guards are the same. Both results put the last after-state on each key of `keys(p) ∪ keys(q)` and leave the other keys as in `G`. ∎

**Theorem 4.3 (inverse law).** If `apply(p, G)` is defined, then `apply(inverse(p), apply(p, G))` is defined and equals `G`. Moreover `p then inverse(p) = ε(p)`, `inverse(p) then p = ε(inverse(p))`, `p then inverse(p) then p = p`, `inverse(inverse(p)) = p`, and `inverse(p then q) = inverse(q) then inverse(p)`.

*Proof.* Let `G' = apply(p, G)`. For `(k, b, a)` in `E_p`, `G'(k) = a`, so the guard of `inverse(p)` holds, and it puts `b` back (removing `k` when `b = ⊥`). Other keys are unchanged. This gives `G`. On each key `aft_p = a = bef_{inverse(p)}`, so `p then inverse(p)` is defined, and the joined edit is `(k, b, b)`; this is `ε(p)`. The next two equations are the same computation with `p` and `inverse(p)` exchanged, together with `(k, b, b) ⊕ (k, b, a) = (k, b, a)`. `inverse(inverse(p)) = p` holds by definition. For the last equation, `(k, b, a) ⊕ (k, a, c) = (k, b, c)` inverts to `(k, c, b)`, which is `(k, c, a) ⊕ (k, a, b)`; keys of only one patch invert directly; and the right side is defined because `aft_{inverse(q)} = bef_q = aft_p = bef_{inverse(p)}` on shared keys, which is the condition for `p then q`. ∎

*Example:* `apply((k, a, b), G)` gives `db` the owner `t1`; applying `(k, b, a)` takes it away again.

**Theorem 4.4 (the order of edits does not matter).** The result of `apply(p, G)`, and whether `p` may be committed (Ch. 3, Def. 3.9), do not depend on the order in which the edits of `p` are listed. In particular an edit of an entity and the edits of relationships that name it need no order among them, because a relationship may point to an absent entity.

*Proof.* The edits of `p` have different keys and are applied together (Ch. 3, Def. 3.7). The conditions WF1 to WF3 concern the result only, and none of them requires the ends of a relationship to be present. ∎

A written form of a patch still needs one order of its edits, so that its digest is defined. That order puts the ontology key first, then entity keys, then relationship keys, and within each group orders ids by their UTF-16 code units, as in Ch. 3, Def. 3.5.

**Proposition 4.5 (what algebraic structure patches form).** Patches under `then` are partial, and as maps on graphs they are *partial bijections*. They are not a group, and not a groupoid by themselves: `p then inverse(p)` is the identity only on the graphs to which `p` applies (it is the guard identity, Theorem 4.3).
- They form an *inverse category* once a zero is added. `then` is not defined for some pairs with `to(p) = from(q)`, namely when their guards disagree, so patches alone are not a category. Add, for each pair of versions, one patch "defined on no graph" as the value of those compositions. Then composition is total and associative, every patch `p` has exactly one `q` with `p then q then p = p` and `q then p then q = q`, namely `inverse(p)`, and the idempotents (patches `e` with `e then e = e`) are the guard identities and the zeros, which commute.
- Restricted to segments of one log (patches between versions `i` and `j` taken from the log) and taken in net form, they form a *groupoid* (a category in which every morphism has an inverse) with the versions as objects and exactly one morphism between any two versions. Its composition is `net(p then q)`, because composing two net forms can create guard-only edits again, on a key changed and changed back. Without the net form they do not form a groupoid: `p then inverse(p)` is the guard identity `ε(p)`, which has edits, and not the empty patch.

*Proof.* A patch is its graph and its two versions together with the partial injection `G ↦ apply(p, G)` (Theorem 4.3). The action determines the edit set: the domain fixes the keys and before-states, and the values fix the after-states. Two patches with the same edits and different versions act alike, so the versions are kept as the objects of the category, and the argument below runs between each pair of versions separately. `p then q` is defined iff the composite partial injection is defined on some graph (take `G` with the before-states of `p` and, on the other keys of `q`, the before-states of `q`), and then it represents the composite (Theorem 4.2). So patches with the zero are a set of partial injections that is closed under composition and inverse. In the partial injections of a set, composition is associative, inverses in the stated sense are unique, and the idempotents are the partial identities, which commute. For the restriction: the net form of a segment from `i` to `j` is `toPatch` of `χ(G_j) − χ(G_i)` (Theorem 4.8), a function of the two states (Ch. 3, Theorem 3.1), and the net form of a segment followed by its inverse is the empty patch. ∎

The framing of patches as morphisms follows [mimram2013]. An earlier description of this design called patches a groupoid without restriction; that is true only in the restricted sense above.

## 4.4 Theorems on signed deltas

**Theorem 4.6 (signed deltas form a group).** `(ℤ^(F), +, 0, −)` is an abelian group.

*Proof.* The sum of two maps that are zero almost everywhere is zero almost everywhere. Addition is fact by fact, so it is associative and commutative. The zero map is neutral, and `−z`, the map with every weight negated, is the inverse. ∎

The same group is the basis of [budiu2023], under the name Z-sets, together with two operators on sequences of deltas: integration, the running sum, and differentiation, the difference between neighbours. Their laws are proved there with a machine-checked proof. Chapter 6 uses them.

**Theorem 4.7 (delta respects patches).** Let `apply(p, G)` be defined. Then
1. `χ(apply(p, G)) = χ(G) + δ(p)`;
2. `δ(p then q) = δ(p) + δ(q)` whenever `p then q` is defined;
3. `δ(inverse(p)) = −δ(p)`;
4. `δ(ε(p)) = 0`.

*Proof.* (1) Key by key: for `(k, b, a)` in `E_p` with `G(k) = b`, `χ` loses `(k, b)` and gains `(k, a)` (nothing is lost for `b = ⊥`, nothing gained for `a = ⊥`). Other keys are unchanged. The keys of the edits are different, so the contributions add up. (2) On a shared key the joined edit contributes `χ₁(c) − χ₁(b) = (χ₁(a) − χ₁(b)) + (χ₁(c) − χ₁(a))`; keys of one patch contribute directly. (3) and (4) follow from Def. 4.7. ∎

**Theorem 4.8 (from deltas back to graphs).** For a well-formed graph `B` and a patch `p` with `apply(p, B)` defined and well-formed:
1. `m_B(δ(p))` is defined and equals `apply(p, B)`;
2. `toPatch(δ(p), B, from(p), to(p)) = net(p)`;
3. `δ(net(p)) = δ(p)`;
4. `δ` is not injective: it forgets guard-only edits, the order and number of intermediate edits, and the cause. These are the losses that `squash` reports (Def. 4.9).

*Proof.* (1) By Theorem 4.7 (1), `χ(B) + δ(p) = χ(apply(p, B))`, the embedding of a well-formed graph, which is materializable. (2) For an effective edit `(k, b, a)` the facts of `δ(p)` on `k` are `−(k, b)` (if `b ≠ ⊥`) and `+(k, a)` (if `a ≠ ⊥`), so `toPatch` gives `(k, B(k), apply(p, B)(k)) = (k, b, a)`; a key with only guard-only edits has no facts and gives no edit. (3) Guard-only edits contribute `0`. (4) `(k, ⊥, a) then (k, a, b)` and `(k, ⊥, b)` have the same delta but different intermediate states and causes; `ε(p)` and the empty patch have the same delta. ∎

**Theorem 4.9 (two things that fail).**
1. *Patches do not commute.* With `p = (k, a, b)` and `q = (k, b, c)` on consecutive versions, `p then q` is defined and `q then p` is not. Deltas always commute: `δ(p) + δ(q) = δ(q) + δ(p)`.
2. *Deltas need not describe a graph.* `z₁ = +(k, b)` and `z₂ = +(k, c)` with `b ≠ c` have a sum with two facts of weight 1 on `k`, which no graph has. That deltas add in any order does not make conflicting changes mergeable.

*Proof.* The two examples. ∎

## 4.5 Why not merge in any order

A *conflict-free replicated data type* (CRDT) lets several writers change copies of one piece of data, kept on several computers, at the same time and merges the copies by an operation that is commutative, associative and idempotent, so no agreed order is needed ([shapiro2011]). This model has one writer and one total order per graph, so concurrent changes of the master graph do not arise. Signed deltas commute, but they need not describe a graph (Theorem 4.9). If the master graph ever had several writers, conflicts between states would have to be settled by a declared policy before an edit enters the log. That is out of scope (Ch. 0).

## 4.6 Editing definitions

Definitions are graphs (Ch. 2, Def. 2.1), so editing a definition is applying patches to a graph. No second mechanism is needed.

Four words from Ch. 2 are needed here. The *catalog* of a customer is the graph of its definitions, and the *kernel* the fixed definitions built into the program (Def. 2.1). A *release* is a numbered fixed state of a module, and a *module* a named unit of definitions (Def. 2.4). A *refactoring* is a change of definitions that leaves every computed graph as it was (Ch. 5, 5.7).

**Definition 4.10 (draft).** A *draft* over version `c` of the catalog is a sequence of patches `ξ = (p₁, …, p_n)` on the catalog graph, with draft versions `0, 1, …, n` (0 is the base). Its state is `D_n = apply(p₁ then … then p_n, K_c)`, where `K_c` is the catalog graph at version `c` (not the kernel `K`). A draft is kept beside the catalog: it creates no version of the catalog, no record in its log, and changes nothing that runs. It lives until it is thrown away. A patch `p_i` is any patch whose guards hold on `D_{i-1}`. An edit by a person, a restore of an old release, a refactoring (Ch. 5, 5.7) and an undo are all such patches.

*Example:* a draft over the shop's definitions whose first patch adds the property `owner` to `Service`.

**Definition 4.11 (undo, redo).** The *undo* of the last patch `p_n` is `inverse(p_n)`, applied to the draft as `p_{n+1}`. The *redo* of that undo is `inverse(p_{n+1})`. An undo never removes a patch from `ξ`.

**Proposition 4.10 (undo is a patch).** (a) If `p_n` led from `D_{n-1}` to `D_n`, then `apply(inverse(p_n), D_n) = D_{n-1}`: an undo of the last patch restores the state before it exactly. (b) For an earlier patch `p_i` with later patches after it, `inverse(p_i)` applies to `D_n` iff its guards, which are the after-states of `p_i`, hold in `D_n`. Otherwise the draft rejects the undo, names the element whose guard failed, and changes nothing. (c) The redo of an undo returns to the state before the undo.

*Proof.* (a) is Theorem 4.3 with `G = D_{n-1}`. (b) `apply` is defined iff every before-state of an edit equals the current state (Ch. 3, Def. 3.7); the before-states of `inverse(p_i)` are the after-states of `p_i`. (c) is (a) for `p_{n+1}`, with `inverse(inverse(p)) = p` up to the cause. ∎

*Consequence.* An undo of an old edit after later edits is not guaranteed. It succeeds exactly when every element that `p_i` edited still has the state `p_i` left it in. This is the partiality of `then` (Theorems 4.1, 4.9), seen from an editor.

*Limits.* The proposition is tested on examples in the prototype and in its viewer: undo and redo as inverse patches, and a rejected patch that changes nothing. It is not tested on generated sequences of edits.

**Definition 4.12 (text view and graph view).** Definitions can also be written as text, in the syntax of Ch. 5, 5.6. `print` turns a module into its text, and `parse` turns a text back into a graph of definitions. Every module has a *canonical text* `print(M)`, the one text that `print` produces for it. An edit of the text from `t` to `t'` is the patch `toPatch` of the difference between the facts of the module `M` in the draft and the facts of `parse(t')`. Definition nodes have path ids (Ch. 2, Def. 2.3), so the difference is taken id by id. After every patch the canonical text of the changed modules is produced again, so text and graph are two presentations of one definition graph.

*Example:* in the text of the shop module, the line `name: Text;` declares the property `name` of kind text. Adding the word `label`, which marks the property as the one shown as the element's name, gives `name: Text label;`. That change of text is a patch with one edit on the node of the property `name`.

**Proposition 4.11 (two views).** Under the premise RT of Theorem 5.6 (printing and parsing are inverse on canonical graphs), for a draft whose state `D` is canonical, that is in `Can`, the set of graphs that `parse` can return: (a) the text view `print(D)` and the graph view `D` determine each other; (b) an edit of the text to a text `t'` that parses leaves the edited module in the canonical state `parse(t')`, and the inverse of its patch returns the draft to `D` and the text to `print(D)`. A draft whose state is not canonical has a graph view and may have no text view.

*Proof.* (a) `print(D)` is a function of `D`, and `D = parse(print(D))` by RT, so each view is a function of the other. (b) The text edit is the patch `q = toPatch(z, M)`, where `M` is the facts of the module in the draft and `z` the difference between the facts of `parse(t')` and `M` (Def. 4.12). By Theorem 4.8 (1), applying `q` gives the module the facts of `parse(t')`, which is canonical by definition. By Theorem 4.3, `inverse(q)` applies to the result and gives back `D`, whose text view is `print(D)`. ∎

*Limits.* RT is a property of two programs, the printer and the parser. It is tested on the example modules and not on generated graphs. A draft with a missing required part or reference cannot be printed; the prototype says which element is incomplete.

**Definition 4.13 (committing a draft).** Committing a draft prints every changed module, checks them together, and makes them active in one change of the definitions (Ch. 9, Def. 9.4). If the base of the draft is no longer the head of the catalog, the patch from the head to the draft's final graph is computed for the modules the draft touched, and the commit is rejected if one of those modules changed in between. The rule works by module. A rule by guards (accept iff the guards of the draft's patches hold at the head) would accept more drafts and is not built.

## 4.7 What the prototype does differently

- **One stack for undo.** The program in which a person edits definitions keeps at most 99 inverse patches. A new edit clears the list of undos that could be redone. The engine (Ch. 0, 0.4) keeps no undo state beyond the draft itself.
- **Tests.** Theorems 4.1 to 4.9 are tested as properties on 4,000 random cases each, including retypes, removals and guard-only edits. The cases are drawn only among patches the prototype accepts, so a patch it refuses wrongly would not be seen (Ch. 14, 14.6). The drafts and undo of 4.6 are tested on examples only.
