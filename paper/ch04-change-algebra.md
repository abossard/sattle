# Ch. 4 Change algebra

**Claim.** Two structures describe change and are kept apart on purpose. Patches are ordered, partial and guarded, and every patch has an inverse. Signed deltas are commutative and total, and may not denote a graph. A homomorphism connects them. Editing definitions is the same algebra: an edit is a patch on a draft and undo is the inverse patch.
**Status.** Built. Tested as properties: Theorems 4.1 to 4.9 on 4,000 seeded cases each, including retype, removal and guard-only edits (Ch. 14, 14.6). Tested on examples only: drafts and undo (4.6), in the engine and in the canvas editor. Proposition 4.11 (two views) is proved under the premise RT of Theorem 5.6. The contract types of iteration 0.1 are not implemented; the engine has its own.
**Gate.** 1a (theory): the change algebra.
Uses: Ch. 1 (canonical form), Ch. 3 (graph, edit, apply, revision). Used by: Ch. 6, 8, 9, Ch. 11.
Sources: [R01] findings 11, 21, 33, 34, 35, T1 to T4; [R02] D7 to D14, L8, L9, L10; `ARCHITECTURE.md` "Changes and arithmetic".

## 4.1 Patches

**Definition 4.1 (patch).** A patch is `p = (g, r, r', E, cause)`: a graph `g`, revisions `r, r' ∈ ℕ` (written `from(p) = r`, `to(p) = r'`), a finite set `E` of edits with pairwise distinct keys (Def. 3.6) and a cause (an ordered list of input ids and a configuration revision). Edits may be guard-only (`b = a`). A committed patch (a commit record's edit set with `r' = succ(r)`) contains effective edits only. Two patches are equal iff `g, r, r', E` are equal. The cause is carried along and is not part of equality.

`apply(p, G) = apply(E, G)` (Def. 3.7). The edits of a patch form a **set**, so the order in which they are listed has no meaning.

**Definition 4.2 (composition, `then`).** For patches `p = (g, r, r', E_p)` and `q = (g, r', r'', E_q)`, `p then q` is defined iff for every key `k` edited by both, `after_p(k) ≈ before_q(k)`. Then `p then q = (g, r, r'', E_p ⊕ E_q)` where `⊕` takes the union of edits on distinct keys and combines the two edits of a shared key as `(k, b, a) ⊕ (k, a, c) = (k, b, c)`. **Guard-only results `(k, b, b)` are kept.** Cause lists are concatenated.

**Definition 4.3 (inverse, guard identity).** `inverse(p) = (g, r', r, {(k, a, b) | (k, b, a) ∈ E_p}, inverse-of cause)`. For a patch `p` the guard identity is `ε(p) = (g, r, r, {(k, b, b) | (k, b, a) ∈ E_p})`. An undo is committed as a new forward revision and never rewinds the log (ARCHITECTURE.md).

**Definition 4.4 (net form).** `net(p)` removes the guard-only edits of `p`. It is lossy: it discards which keys were read (Theorem 4.8).

## 4.2 Facts and signed deltas

**Definition 4.5 (fact).** A fact is a pair `f = (k, s)` of an element key and a state `s ∈ S` (never `⊥`), in canonical form `enc`. `F` is the set of facts.

**Definition 4.6 (signed delta).** `ℤ^(F)` is the set of maps `z : F → ℤ` with finite support, with pointwise addition. Weights are decimal strings on the wire (Rule 1.2).

**Definition 4.7 (embedding and delta of a patch).** `χ(G) = Σ_{k ∈ dom G} 1·(k, G(k))`. With `χ₁(⊥) = 0` and `χ₁(s) = 1·(k, s)`,

`δ(p) = Σ_{(k,b,a) ∈ E_p} (χ₁(a) − χ₁(b))`.

Guard-only edits contribute `0`.

**Definition 4.8 (materialization).** `z ∈ ℤ^(F)` is *materializable* iff every weight of `z` is in `{0, 1}`, at most one fact per key has weight `1`, and the induced map `k ↦ s` satisfies `U` (Def. 3.3). For a well-formed base `B`, `m_B(z) = χ⁻¹(χ(B) + z)` is defined iff `χ(B) + z` is materializable.

**Definition 4.9 (conversions, squash, loss).**
- `toDelta(p) = δ(p)`.
- `toPatch(z, B, r, r')` is defined iff `m_B(z)` is defined. It then has, for each key `k` whose facts occur in `z`, the edit `(k, B(k), m_B(z)(k))`. Edits with equal before and after do not occur. It is the canonical net transition, not the original history.
- `squash(p₁, …, pₙ) = net(p₁ then … then pₙ)`, with a loss report of three flags: `chronologyLost` (true iff `n > 1`), `intermediateStatesLost` (true iff some key is edited by two or more of the `pᵢ`) and `causeDetailLost` (true iff causes were merged).

## 4.3 Theorems on patches

**Theorem 4.1 (associativity, T2).** If `p then q` and `q then u` are both defined, then `(p then q) then u` is defined iff `p then (q then u)` is defined, and when defined they are equal.
Status: **proved**.

*Proof.* Write `keys(·)` for edited keys, `bef` and `aft` for before and after states, and `aft_{pq}(k) = aft_q(k)` if `k ∈ keys(q)` else `aft_p(k)`; likewise `bef_{qu}(k) = bef_q(k)` if `k ∈ keys(q)` else `bef_u(k)`. Because guard-only edits are kept (Def. 4.2), `keys(p then q) = keys(p) ∪ keys(q)`.
Left side defined iff (i) for `k ∈ keys(p) ∩ keys(q)`: `aft_p = bef_q`, and (ii) for `k ∈ (keys(p) ∪ keys(q)) ∩ keys(u)`: `aft_{pq}(k) = bef_u(k)`.
Right side defined iff (i') for `k ∈ keys(q) ∩ keys(u)`: `aft_q = bef_u`, and (ii') for `k ∈ keys(p) ∩ (keys(q) ∪ keys(u))`: `aft_p(k) = bef_{qu}(k)`.
Split (ii) by `k ∈ keys(q)` (giving `aft_q = bef_u`) or `k ∈ keys(p) \ keys(q)` (giving `aft_p = bef_u`). Split (ii') by `k ∈ keys(q)` (giving `aft_p = bef_q`) or `k ∈ keys(u) \ keys(q)` (giving `aft_p = bef_u`). Both sides reduce to the same set of conditions: `aft_p = bef_q` on `keys(p) ∩ keys(q)`, `aft_q = bef_u` on `keys(q) ∩ keys(u)`, `aft_p = bef_u` on `(keys(p) ∩ keys(u)) \ keys(q)`. The edit set of each side is, per key, the composition of the consecutive edits touching it, which is associative since `(b,a) ⊕ (a,c) ⊕ (c,d) = (b,d)` in either bracketing. `from` and `to` agree. ∎

**Theorem 4.2 (application respects composition, T2).** If `p then q` is defined, then `apply(p then q, G)` is defined iff `apply(p, G)` is defined and `apply(q, apply(p, G))` is defined, and then `apply(p then q, G) = apply(q, apply(p, G))`.
Status: **proved**.

*Proof.* Guard of `p then q` on `G`: `G(k) ≈ bef_p(k)` for `k ∈ keys(p)`, and `G(k) ≈ bef_q(k)` for `k ∈ keys(q) \ keys(p)`. Let `G' = apply(p, G)`. Guard of `q` on `G'`: for `k ∈ keys(q) ∩ keys(p)`, `G'(k) = aft_p(k) ≈ bef_q(k)` holds by composability; for `k ∈ keys(q) \ keys(p)`, `G'(k) = G(k)` must be `≈ bef_q(k)`. The two guard sets coincide given composability. The results agree on `keys(p) ∪ keys(q)` (the last edit wins, and it is `aft_{pq}`) and elsewhere are `G`. ∎

**Theorem 4.3 (inverse law, T1).** If `apply(p, G)` is defined, then `apply(inverse(p), apply(p, G))` is defined and equals `G`. Moreover `p then inverse(p) = ε(p)`, `inverse(p) then p = ε(inverse(p))`, `p then inverse(p) then p = p`, `inverse(inverse(p)) = p` and `inverse(p then q) = inverse(q) then inverse(p)`.
Status: **proved**.

*Proof.* Let `G' = apply(p, G)`. For `(k, b, a) ∈ E_p`, `G'(k) = a`, so the guard of `inverse(p)` holds, and its effect sets `k` back to `b` (removing `k` when `b = ⊥`). Keys outside `E_p` are unchanged. This gives `G`. Composability of `p` and `inverse(p)` on each key is `aft_p = a = bef_{inverse(p)}`, and the merged edit is `(k, b, b)`, so `p then inverse(p) = ε(p)`. The next equalities are the same computation with `p` and `inverse(p)` exchanged, and `ε(p) then p`: `(k, b, b) ⊕ (k, b, a) = (k, b, a)`. Involution is by definition. For the last identity, `(k,b,a) ⊕ (k,a,c) = (k,b,c)` inverts to `(k,c,b)`, which equals `(k,c,a) ⊕ (k,a,b)`; keys of only one patch invert directly; definedness of the right side is `aft_{inverse(q)} = bef_q = aft_p = bef_{inverse(p)}` on shared keys, which is composability of `p then q`. ∎

**Theorem 4.4 (edit order is irrelevant; dangling edges remove intra-patch ordering).** The result of `apply(p, G)` and the committability of `p` (Def. 3.9) do not depend on any enumeration order of `E_p`. In particular an entity edit and the relationship edits that reference it need no relative order, because relationships may dangle.
Status: **proved**.

*Proof.* `E_p` is a set of edits with distinct keys, applied simultaneously (Def. 3.7). WF1 to WF3 are properties of the result only, and no condition refers to the presence of an endpoint entity (ADR-084). ∎

A canonical serialization order of edits is still required for digests and signed artifacts. It is by `(kind, id)` with `κ_O` first. This supersedes ADR-004 ("independent changes may vary in replay order") for the encoding, in agreement with ADR-074 (finding 21).

**Proposition 4.5 (algebraic structure, correction of the review).** Patches under `then` are partial and are *partial bijections on graphs*, not a group or groupoid by themselves. `p then inverse(p)` is the identity only on the domain of `p` (a guard identity, Theorem 4.3). The structure is an inverse category: composition is associative where defined, every patch has an inverse with `p then inverse(p) then p = p`, and idempotents are guard identities. Restricted to committed segments of one log (patches between revisions `i` and `j` taken from the log) the structure is a groupoid on the revisions as objects, with exactly one morphism between any two revisions.
Status: **proved** (Theorems 4.1 and 4.3; the restriction is by Theorem 3.1, since `G_j` is determined by the log). The categorical framing of patches follows [mimram2013]. The statements of [R01] section 2.1 and [R02] L9 ("groupoid") hold only in the restricted sense.

## 4.4 Theorems on signed deltas

**Theorem 4.6 (group of signed deltas, T3).** `(ℤ^(F), +, 0, −)` is an abelian group.
Status: **proved**. See also [budiu2023], where the group of Z-sets and its integration and differentiation operators are proved and machine-checked (as reported in [R02] L10).

*Proof.* The sum of two finite-support maps is finite-support. Addition is pointwise, hence associative and commutative. The zero map is neutral and `−z` is the pointwise negation. ∎

**Theorem 4.7 (delta is a homomorphism, T4).** Let `apply(p, G)` be defined. Then
1. `χ(apply(p, G)) = χ(G) + δ(p)`;
2. `δ(p then q) = δ(p) + δ(q)` whenever `p then q` is defined;
3. `δ(inverse(p)) = −δ(p)`;
4. `δ(ε(p)) = 0`.
Status: **proved**.

*Proof.* (1) Key by key. For `(k, b, a) ∈ E_p` with `G(k) = b`, `χ` loses `(k, b)` and gains `(k, a)` (nothing lost for `b = ⊥`, nothing gained for `a = ⊥`). Keys outside `E_p` are unchanged. Edit keys are distinct, so the contributions add. (2) On a shared key the merged edit contributes `χ₁(c) − χ₁(b) = (χ₁(a) − χ₁(b)) + (χ₁(c) − χ₁(a))`; keys of one patch contribute directly. (3) and (4) are immediate from Def. 4.7. ∎

**Theorem 4.8 (materialization and conversion, T4).** For a well-formed base `B` and `p` with `apply(p, B)` defined and well-formed:
1. `m_B(δ(p))` is defined and equals `apply(p, B)`;
2. `toPatch(δ(p), B, from(p), to(p)) = net(p)`;
3. `δ(net(p)) = δ(p)`;
4. `δ` is not injective: it forgets guard-only edits, the order and number of intermediate edits, and the cause (this is the loss report of Def. 4.9).
Status: **proved**.

*Proof.* (1) By Theorem 4.7(1) `χ(B) + δ(p) = χ(apply(p, B))`, which is the embedding of a well-formed graph, hence materializable. (2) For each key of an effective edit `(k, b, a)` the facts of `δ(p)` on `k` are `−(k, b)` (if `b ≠ ⊥`) and `+(k, a)` (if `a ≠ ⊥`), so `toPatch` yields `(k, B(k), apply(p,B)(k)) = (k, b, a)`; keys with only guard-only edits have no facts and yield no edit. (3) Guard-only edits contribute `0`. (4) Counterexample: `p: k ⊥→a` then `q: k a→b` and `p': k ⊥→b` have the same `δ` but different intermediate states and causes; `ε(p)` and the empty patch have the same `δ`. ∎

**Theorem 4.9 (non-commutativity and non-closure).**
1. Patches do not commute: for `p: k a→b` and `q: k b→c` (consecutive revisions), `p then q` is defined and `q then p` is not (adjacency and guard fail). "Noncommutativity" in the oracle test shows partiality (finding 35). `δ(p) + δ(q) = δ(q) + δ(p)` always.
2. `ℤ^(F)` is not closed under materializability: `z₁ = +(k, a)` and `z₂ = +(k, b)` with `a ≠ b` have a sum with two facts of weight `1` on `k`, which no graph denotes. Arithmetic on conflicting updates is not commutative merging merely because delta addition is.
Status: **proved** (by the examples).

## 4.5 Contrast with CRDTs

A CRDT merges concurrent updates through commutative, associative, idempotent operations and needs no coordinated order ([shapiro2011]). Sattle's log has a single writer and a total order per graph, so concurrent updates do not arise in the master. The signed-delta group is commutative, but its elements need not denote graphs (Theorem 4.9). If the master were ever multi-writer, state conflicts would have to be resolved by a declared policy before an edit enters the log. This is out of scope (Ch. 0).

## 4.6 Editing definitions as patches

Definitions are graphs (Def. 2.1), so editing a definition is applying patches to a graph. The engine and the canvas use no second mechanism for it (protocol version 3, `engine/PROTOCOL.md`, "drafts").

**Definition 4.10 (draft).** A draft over catalog revision `c` is a sequence of patches `ξ = (p₁, …, p_n)` on the graph `catalog`, with draft revisions `0, 1, …, n` (revision 0 is the base). Its state is `D_n = apply(p₁ then … then p_n, K_c)`, where `K_c` is the catalog graph at revision `c` (not the kernel `K`). A draft is an overlay: it creates no catalog revision, no log record in the sense of Ch. 3, and no activation. It lives until it is discarded. A patch `p_i` is any patch whose guards hold on `D_{i-1}` (Def. 3.7). The engine does not distinguish a user edit, a restore, a refactoring (Ch. 5, 5.7) and an undo.

**Definition 4.11 (undo, redo).** The undo of the last patch `p_n` is the patch `inverse(p_n)` (Def. 4.3), applied to the draft as `p_{n+1}`. The redo of that undo is `inverse(p_{n+1})`. Undo never removes entries of `ξ`. A client keeps a stack of at most 99 inverse patches and clears the redo stack on a new edit; the server keeps no undo state beyond `ξ`.

**Proposition 4.10 (undo is a patch).** (a) If `p_n` produced `D_n` from `D_{n-1}`, then `apply(inverse(p_n), D_n) = D_{n-1}`, so undo at the head restores the previous state exactly. (b) For an earlier patch `p_i` with later patches after it, `inverse(p_i)` applies to `D_n` iff the guards of `inverse(p_i)`, which are the after-states of `p_i`, hold in `D_n`. Otherwise the draft rejects the patch (`GuardFailed`, with the element id) and nothing changes. (c) Redo of an undo returns to the state before the undo, by (a) applied to the undo.
Status: **proved** from Theorem 4.3 and Def. 3.7 for (a) and (c), by the definition of `apply` for (b). Tested as an example on the engine and the client: `Structural_edits_print_canonically_and_undo_is_an_inverse_patch_not_a_rewind` (`DraftTests.cs`), and in the canvas the tests "applies edits to the draft and the browser copy, undo and redo are inverse patches", "a new edit clears the redo stack", "keeps 99 steps" and "a rejected patch changes nothing and reports the engine's code" (`canvas/tests/editor-session.test.ts`). The tests are examples. There is no generated edit sequence on drafts (O4.3).

*Proof.* (a) is Theorem 4.3 with `G = D_{n-1}`. (b): `apply` is defined iff every edit's before-state equals the current state (Def. 3.7); the before-states of `inverse(p_i)` are the after-states of `p_i`. (c) is (a) for `p_{n+1}`, and `inverse(inverse(p)) = p` up to the cause field. ∎

Consequence: undo of an old edit after later edits is not guaranteed. It succeeds exactly when every element that `p_i` edited still has the state `p_i` left it in. This is the partiality of `then` (Theorem 4.1, 4.9) seen from the editor.

**Definition 4.12 (text view and graph view).** Every module has a canonical text `print(M)` (Ch. 5, 5.6). A text edit from `t` to `t'` is the patch `toPatch` of the difference between the facts of the draft's module `M` and the facts of `parse(t')`. Definition ids are path ids (Def. 2.3), so the difference is by id. After every patch the engine returns the canonical text of the touched modules, so text and graph are two presentations of one definition graph. The engine holds the graph, and the browser may keep a copy.

**Proposition 4.11 (two views).** Under premise RT of Theorem 5.6, for a draft whose state `D` is canonical (in `Can`, the image of `parse`): (a) the text view `print(D)` and the graph view `D` determine each other; (b) a text edit to a text `t'` that parses leaves the edited module in the canonical state `parse(t')`, and the inverse of its patch returns the draft to `D` and the text view to `print(D)`. A draft whose state is not canonical has a graph view and may have no text view.
Status: **proved under the named premise RT** (Theorem 5.6), restated. RT is a property of the printer and the parser, tested on the example modules (`Print_then_parse_reproduces_the_graph_and_printing_is_idempotent`, `LanguageTests.cs`) and wanted on generated graphs (test T-ROUNDTRIP, [inventory.md](inventory.md)). A draft with a missing required slot or reference is reported as `Unprintable` with the element id (`A_wired_filter_without_a_predicate_is_unprintable_and_names_the_filter`, `ProtocolFixTests.cs`): the graph view is a superset of the text view. The first form was conditional on `parse(print(G)) = G` for every well-formed graph, which Theorem 5.6 no longer claims.

*Proof.* (a) `print(D)` is a function of `D`, and `D = parse(print(D))` by RT, so each view is a function of the other. (b) The text edit is the patch `q = toPatch(z, M)` where `M` is the module's facts in the draft and `z` the difference between the facts of `parse(t')` and `M` (Def. 4.12). By Theorem 4.8(1) applying `q` gives the module the facts of `parse(t')`, which is canonical by definition. By Theorem 4.3 `inverse(q)` applies to the result and gives back `D`, whose text view is `print(D)`. ∎

**Definition 4.13 (draft commit).** Committing a draft prints every changed module, checks them together, and activates them as one configuration edit (Ch. 9, Def. 9.4). If the draft's base is not the catalog head, the engine computes the patch from the head to the draft's final graph for the modules the draft touched, and rejects with `StaleRevision` when a touched module changed in between. The rule is module-level. A rule at the level of guards (accept iff the guards of the draft's patches hold at the head) would accept more drafts and is not built.

## 4.7 Mapping to the contracts

In the contracts of iteration 0.1 a C# type error already prevents mixing patches and signed deltas (`ChangeAlgebra.cs`). The operations map as follows.

| Paper | `ChangeAlgebra.cs` | Note |
|---|---|---|
| `p then q` | `PatchThen` | defined iff Def. 4.2, else `AlgebraRejected` |
| `inverse(p)` | `PatchInverse` | |
| `δ(p)`, `toDelta` | `ConvertPatchToDelta` | |
| `toPatch` | `ConvertDeltaToPatch` | needs `DeltaAtBase`, target version, ontology |
| `squash` | `SquashPatches` | returns `LossReport` |
| `z₁ + z₂`, `−z` | `DeltaAdd`, `DeltaSubtract`, `DeltaNegate` | |

## 4.8 Delta to iteration 0.1

- **Finding 11.** `then` and `inverse` replace "addition" and "negation" for patches. "Endpoint swap" is replaced by "swap of `from` and `to` revisions".
- **Finding 21.** Edits are a set; canonical serialization order is `(kind, id)`. ADR-004 is superseded for encoding purposes.
- **Finding 35.** The oracle must assert "`q then p` is undefined" and prove associativity of `apply` composition (Theorem 4.2), not list concatenation.
- **Def. 4.2 retains guard-only edits.** The oracle and `ARCHITECTURE.md` drop `(k, b, b)`. Dropping makes definedness non-associative: after `p` edits `k` from `b` to `a` and `q` edits it back to `b`, a following `u` with a wrong before state on `k` would compose with `(p then q)` but not with `q`. `net` and `squash` drop guards explicitly and report the loss.
- **Proposition 4.5.** Patches are an inverse category, groupoid only on a log segment. The review's word "groupoid" is refined.
- **Finding 33.** The oracle currently tests stand-ins (tuples, sets). Obligation O4.1 moves the tests to the contract types.
- **Symbolic patches** (`DiffProfile.Symbolic`) are out of scope (Ch. 0, finding 26).
- **Definition editing (new against iteration 0.1).** The contracts say nothing about editing definitions. Protocol version 3 edits them as patches on a draft overlay with undo as inverse patch (4.6). The 99-step undo stack, the module-level rebase rule and the canonical text returned with every answer are engine and client decisions recorded here.

## 4.9 Obligations

| Id | Statement | Discharged by |
|---|---|---|
| O4.1 | Theorems 4.1 to 4.3 and 4.7 to 4.8 hold on the contract types, including relationship edits with retype, dangling endpoints and the ontology slot | property tests on `GraphPatch` and `SignedDelta`; at least 10,000 generated cases per property, reported as properties, not assertion counts (finding 34) |
| O4.2 | Canonical edit order `(kind, id)` is used by every encoder and digest | conformance test against the Ch. 1 corpus |
| O4.3 | Proposition 4.10 on generated draft edit sequences (patches, undo, redo, rejected guards) | seeded property tests on `Drafts.cs` in the style of `AlgebraTests.cs` |
