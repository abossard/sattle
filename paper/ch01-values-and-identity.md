# Ch. 1 Values, canonical form and identity

**Claim.** A value has one canonical encoding and equality is equality of encodings. An entity id is a pure function of an identity key, and a binding never changes once made. Two identities that turn out to denote one entity are merged by an additive record, and the survivor does not depend on the order in which keys arrive.
**Status.** Built: canonical encoding, digests, identity domains, bindings, merge with an order-independent survivor. Tested on examples and on two real captures: the survivor rule (Proposition 1.8). Not built: split, migration of identity domains (B3, E10), an independent check of the encoder. Deviations D1, D4, D5 (resolved), D6 (resolved by a change of Def. 5.9); Ch. 14, 14.8.
**Gate.** 1a (theory): canonical form and identity, on which every digest and every determinism statement rests.
Uses: Ch. 0. Used by: Ch. 3 (graph equality, digests), Ch. 4 (facts), Ch. 11 (seals).
Sources: [R01] findings 3, 6, 7, 8, 24; [R02] section 4.3 (binding rows) and R10; `ARCHITECTURE.md` "Identity and ontology"; ADR-001, ADR-050, ADR-090, ADR-094.

## 1.1 Values

**Definition 1.1 (value).** The set `𝒱` of graph values is the closed tagged union

`v ::= text(s) | integer(n) | decimal(q) | boolean(b) | instant(i) | null | entity(e) | list(v₁, …, vₘ)`

with `s` a sequence of Unicode scalar values, `n ∈ ℤ`, `q ∈ ℚ` with finite decimal expansion, `b ∈ {true, false}`, `i` an instant on the UTC timeline with nanosecond resolution, `e ∈ 𝓔`, `m ≥ 0`.

**Definition 1.2 (value kind).** `kind : 𝒱 → {text, integer, decimal, boolean, instant, null, entity, list}`. An ontology declares one kind per property, plus the item kind for lists, a nullability flag and a unit (Ch. 2). Equality of values of different kinds is false. In particular `integer(1)` and `decimal(1)` differ.

**Definition 1.3 (normalization).** The function `norm : 𝒱 → 𝒱` is defined by the following rules. A value is *normal* iff `norm(v) = v`.

| Rule | Kind | Normal form |
|---|---|---|
| N1 | integer | decimal numeral `0` or `-?[1-9][0-9]*`. No `+`, no leading zeros, `-0` becomes `0`. Arbitrary precision. |
| N2 | decimal | `0` or `-?(0\|[1-9][0-9]*)(\.[0-9]*[1-9])?` with `-0` becoming `0`. No exponent, trailing fractional zeros removed. |
| N3 | instant | `YYYY-MM-DDThh:mm:ss` with optional `.f` of 1 to 9 digits (no trailing zero, omitted when all zero) followed by `Z`. Offsets are converted to UTC. Leap seconds are rejected. |
| N4 | text | Sequence of Unicode scalar values as given. No normalization form is applied to graph data. Lone surrogates are rejected. |
| N5 | boolean, null, entity | As given. |
| N6 | list | Order is significant and preserved. Items are normalized recursively. There is no set kind. |
| N7 | unit | Values are kept in the unit declared by the property. Canonical form never converts units. Conversion is a projection concern. |
| N8 | property map | A finite map from `Π` to `𝒱`. Duplicate keys are invalid. Order is canonical (see 1.2). Absence of a key is not `null`. |

**Rule 1.1 (identifier alphabet, proposed).** `PropertyId` and `TypeId` use the ASCII alphabet `[A-Za-z0-9_.:-]` with length 1 to 200. For such ids, UTF-16 code unit order (used by JCS) equals byte order, so canonical orders agree across encodings. `EntityId` and `RelationshipId` are opaque Unicode strings.

## 1.2 Canonical encoding

**Definition 1.4 (canonical encoding).** `tree(v)` is the JSON value `{"kind": κ, "value": x}` where `x` is the normalized string (integer, decimal, instant, text, entity), a JSON boolean, or an array of `tree` of the items (list). `null` has no `value` member. `enc(v)` is the RFC 8785 serialization of `tree(norm(v))` ([rfc8785]): object members sorted by UTF-16 code units recursively, no insignificant whitespace, array order kept, ECMAScript string serialization. Element states, facts and graphs are encoded in the same way (Def. 3.5, Def. 4.5).

**Rule 1.2 (no JSON numbers on the wire).** Every integer-typed field of every record (counts, revisions, positions, nanoseconds, weights) is a decimal string in the canonical form of N1. RFC 8785 requires JSON numbers to be expressible as IEEE 754 doubles and recommends strings for larger integers ([rfc8785]). A 2026 Unix time in nanoseconds (about 1.79e18) exceeds 2^53. Consequently canonical encodings contain no JSON numbers, and the ECMAScript number serialization of JCS is never exercised.

**Definition 1.5 (digest).** `H` is SHA-256. `digest(x) = "sha256:" + hex(H(enc(x)))`. Collision resistance of `H` is an assumption of the paper, not a result.

## 1.3 Canonical equality

**Definition 1.6 (value equality).** `v ≈ w` iff `norm(v) = norm(w)` as abstract syntax trees. By the rules above this is equality of the denoted mathematical values: integers as integers, decimals as rationals, instants as points of the UTC timeline, texts as code point sequences, lists componentwise.

**Theorem 1.1 (canonical equality).** For all `v, w ∈ 𝒱`: `v ≈ w` iff `enc(v) = enc(w)` (byte equality). Equality of property maps is insensitive to the order in which members are supplied.
Status: **proved**.

*Proof.* (⇒) `v ≈ w` gives `norm(v) = norm(w)`, hence the same `tree`, hence the same JCS output, since JCS is a function on JSON values. (⇐) JCS output determines the JSON value by parsing, so `tree(norm(v)) = tree(norm(w))`. The tree determines `kind` and then `norm(v)`: for each kind the normal-form grammar (N1 to N5) admits exactly one numeral or string per denoted value, and lists are determined by their ordered items by induction. Hence `norm(v) = norm(w)`. For property maps, JCS sorts members (RFC 8785 section 3.2.3) so that two supplied orders of the same map give the same bytes. ∎

**Corollary 1.2 (decidability and cost).** Equality of values is decidable in time linear in the sizes of `v` and `w`: compute `enc` and compare.
Status: **proved** (Theorem 1.1).

**Corollary 1.3 (digest compatibility).** `v ≈ w` implies `digest(v) = digest(w)`. The converse holds unless `H` has a collision.
Status: **proved** under the collision-resistance assumption.

## 1.4 Identity

**Definition 1.7 (identity domain).** An identity domain `d` fixes a name and an ordered list of components `(c₁:κ₁, …, cₙ:κₙ)` (component name and value kind). Richer content (normalization options per component, scope, permitted entity types, merge policy) belongs to the typed-graph definition (`IdentityDomain`, `KeyComponent`, `EquivalencePolicy` in Ch. 2, used by `IdOf` in Ch. 5). Only name and components are used here.

**Definition 1.8 (identity key, canonical key).** An identity key is `ik = (t, d, c)` with tenant `t`, domain `d` and a tuple `c = (v₁, …, vₙ)` with `kind(vⱼ) = κⱼ`. Its canonical key bytes under canonicalization version `ν` are

`ck_ν(ik) = JCS([t, name(d), enc(v₁), …, enc(vₙ)])`.

The canonicalization version `ν` names the rule set of 1.1 and 1.2 (this chapter is version `ν₁`). It is **not** a component of the key (finding 8).

**Definition 1.9 (entity id allocation).** `alloc_ν(ik) = base32(H(ck_ν(ik)))` (lowercase letters and digits `2` to `7`, no padding). An entity id is a value of `alloc` or an id introduced by a binding of Def. 1.10 (alias, migration).

**Definition 1.10 (binding set).** A binding set `B` is a finite partial map from pairs `(ν, ck)` to entity ids, together with the component tuple that produced `ck`. It is built only by these three append rules; no rule modifies or deletes an entry.

- **B1 allocate.** If `B(ν, ck_ν(ik))` is undefined, add `(ν, ck_ν(ik)) ↦ alloc_ν(ik)`.
- **B2 alias.** Given an existing entity `e` and a key `ik` with `B(ν, ck_ν(ik))` undefined, and an evidence record that `ik` denotes `e`, add `(ν, ck_ν(ik)) ↦ e`.
- **B3 migrate.** For a new version `ν'` and every entry `(ν, ck) ↦ e` with components `c`, add `(ν', ck_{ν'}(t, d, c)) ↦ e`.

`resolve(B, ik)` returns `B(ν, ck_ν(ik))` for the current version `ν` if defined, else rejects. Merge and split of entities are not rules of this chapter (see Theorem 1.7).

## 1.5 Identity theorems

**Theorem 1.4 (allocation is a pure function, shared identity).** If `ik ≈ ik'` (same tenant, domain name and componentwise `≈` values) then `alloc_ν(ik) = alloc_ν(ik')`. Two projections, two graphs or two replays that allocate from equivalent keys obtain the same entity id with no coordination, and concurrent first bindings of the same key write the same entry.
Status: **proved** (modulo collision resistance of `H`). Realizes ADR-001. Two projections that choose different domains or different components obtain different ids, as ADR-094 intends.

*Proof.* `alloc_ν` is the composition of `ck_ν`, `H` and `base32`, each a function. By Theorem 1.1, `ck_ν` is constant on `≈` classes. Concurrent writers evaluate the same function on the same bytes. ∎

**Theorem 1.5 (timeless binding).** For every binding set `B` built by rules B1 to B3 and every extension `B' ⊇ B` built by further applications of B1 to B3: `B'(ν, ck) = B(ν, ck)` whenever `B(ν, ck)` is defined. The value of `resolve(B, ik)` is a function of `(B, ik)` only. It has no dependence on any entity state, entity type, ontology version, revision or wall-clock time.
Status: **proved** for B1 to B3. Merge and split semantics are an **obligation** (Theorem 1.7).

*Proof.* Each rule adds an entry for an argument where `B` is undefined, so earlier entries are never changed. The signature of `resolve` takes only `B` and `ik`. ∎

**Theorem 1.6 (canonicalization change keeps ids).** If every binding stores the component tuple that produced its key, then for every new canonicalization version `ν'` the migration B3 computes a new key for every entry, and, unless a new key is already bound to another entity (a conflict, which rejects the migration), for every entry `(ν, ck) ↦ e` the entry `(ν', ck') ↦ e` exists afterwards. No entity id changes. Resolution of an old-version key and of its new-version image return the same `e`.
Status: **proved** for the mechanical migration. Whether a particular rule change `ν → ν'` should also identify keys that were distinct before is a domain decision recorded as B2 evidence. Experiment **E10** (R02) tests replay across a canonicalization change.

*Proof.* B3 recomputes `ck_{ν'}` from the stored components, a total computation. It adds an entry unless one exists. If one exists for `ck'` with a different entity, the migration is rejected and reported as a conflict (not silently resolved). Otherwise the new entry maps to the same `e`. ∎

**Theorem 1.7 (merge and split).** Merge of two entities is an additive forwarding record `e₂ → e₁` that requires `e₁ < e₂` in the total order of entity ids (the survivor is the smaller id, [R03] section 5). A record is added only from an id that has none, that is from a current representative, so at most one record leaves an id and `rep` is a function (the rule of Proposition 1.8 forwards a representative to a representative). The representative `rep(e)` follows forwarding records until none applies. Then: (a) forwarding chains are acyclic and finite, (b) `rep` is idempotent, (c) resolution `rep(resolve(B, ik))` is unchanged for every key whose entity is not involved, (d) no binding of `B` is modified. A split is an explicit additive record `splitFrom`, never inferred.
Status: (a) to (d) **proved**, and (a) to (d) **tested by examples** in the engine (D5). Stability of the survivor choice under concurrent merges in different projections, and the semantics of split, are an **obligation** (Q10 of [R03]; O1.2). The engine tests cover one projection, all orders of three merges and of three keys, one synthetic order case at four chunk sizes and two real captures, not concurrent projections and not generated key orders.

*Proof.* (a) Each forwarding step strictly decreases the id, so a chain has no repetition and is bounded by the finite set of ids. (b) `rep(e)` is a fixed point by construction. (c) A record `e₂ → e₁` changes `rep` only on entities whose chain passes through `e₂`; `e₂` was a representative, so these are the entities whose representative it was. Without the condition that a record leaves only a representative, an id could carry two records and `rep` would not be defined; the first form of the statement did not say so. (d) The record is appended to a separate forwarding set, and [R03]'s phrase "all keys rebind to the survivor" is read as `rep` applied at resolution, not as rewriting `B`, because Theorem 1.5 requires bindings to be immutable. Historical references resolve through the chain. ∎

**Proposition 1.8 (order-independent survivor).** Let `K` be a set of identity keys of one identity domain and `L` a set of links, each naming two keys of `K` that denote one entity (all of one entity type). Let `own(k)` be the id that `alloc` assigns to the key `k` on its own (Def. 1.9). Apply the keys and links in any order, binding each key when it is first used and merging when a link joins two identities. Then the representative of every connected class `C` of `K` under `L` is `min { own(k) : k ∈ C }` (ordinal order), whatever the order and whatever the chunking, and no binding is modified.
Status: **proved** for the rule stated here, by induction with an invariant; read only, checked by no tool. That the engine implements the rule is **tested by examples and on two real captures**, not on generated orders (test T-B3, [inventory.md](inventory.md)). The engine rule (`Identity.cs`, `Bind`): a key that is not yet bound becomes a candidate with its own id even when nothing used it before, and when its own id is smaller than the id of the entity it joins, the entity is forwarded to it; a link that finds the key already bound forwards the larger representative to the smaller one.

*Proof.* `own` is a function of the key alone (Theorem 1.4), so it is fixed before any operation. Call a key *seen* once it was used or named by a processed link, and let `~` be the equivalence closure of the processed links on the seen keys. Invariant after every operation: for every seen key `k`, `Rep(k) = min { own(k') : k' seen, k' ~ k }`, and every forwarding record points from a larger id to a smaller one.
- *Start.* No key is seen, and the invariant holds for none.
- *First use of a key `k`.* Its class is `{k}` and the rule binds it to `own(k)`, the minimum of a one-element set. No other class changes.
- *A link between `k₁` and `k₂`.* An endpoint that is not yet seen becomes seen with its own id, as in the previous case. If the two are already in one class nothing changes. Otherwise the classes `C₁` and `C₂` have the representatives `m₁ = min own(C₁)` and `m₂ = min own(C₂)` by the invariant, and `m₁ ≠ m₂` because `own` is injective up to collisions (Theorem 1.4). The rule forwards the larger to the smaller, so the representative of `C₁ ∪ C₂` is `min(m₁, m₂) = min own(C₁ ∪ C₂)`. The new record points from larger to smaller, so chains strictly decrease, are finite and end at the class minimum, which is what `Rep` returns (Theorem 1.7(b)).
After all keys and links are processed, the seen keys are the keys used and the endpoints of `L`, and `~` is the closure of `L`, in any order. The right-hand side of the invariant is therefore the same for every order, and so is `Rep`. Chunking does not reorder operations (Theorem 6.2(a)), so the same holds after every prefix. Forwarding only adds records, so no binding is modified (Theorem 1.7(d)). ∎

Not covered by the proof: a link between two keys of different entity types (a conflict, counted, the link ignored), keys that never reach the engine (the minimum is over the keys seen), concurrent merges in different projections (O1.2), and the telemetry written under a loser's id before the merge (moved by the engine to the survivor, tested on the captures only).

## 1.6 Delta to iteration 0.1

- **Finding 3.** Canonical encoding is RFC 8785 plus rules N1 to N8. Fixture digests must be computed, not `sha256:example`.
- **Finding 8.** `IdentityKey.CanonicalizationVersion` leaves the key. The version is recorded with the binding. Key equality is on `(tenant, domain, components)`.
- **Finding 24.** All 64-bit-valued wire fields are decimal strings (Rule 1.2). `SchemaWriter.cs` must stop mapping `long` to JSON `integer`.
- **Findings 6, 7.** The identity catalog needs commands allocate, bind (alias), migrate; merge and split follow Theorem 1.7. Two fixtures are required: two projections resolving one entity, and two domains that intentionally do not.
- **Finding 30.** `IdentityEvidence.Confidence` stays unspecified. It does not influence any binding in this chapter.
- **Rule 1.1** (identifier alphabet) is new and proposed.
- **Gap 3** (identity domains as typed graphs): Def. 1.7 is realized by the `IdentityDomain` node type (Ch. 2) and `IdOf` (Ch. 5).

## 1.7 Deviations found by the prototype

Ids refer to Ch. 14 (14.8).

- **D1.** Instants are nanoseconds in a signed 64-bit integer (years 1678 to 2262) and decimals are 28-digit .NET `decimal`, not unbounded (N1 to N3 allow arbitrary precision). The paper should state a bounded range with a typed error, or the engine should use arbitrary precision.
- **D4.** Bindings are an append-only table written with the master commit, not `Key`, `Identity` and `Evidence` graph nodes. Def. 1.10 is realized, the typed-graph form of Def. 2.12 is not.
- **D5 (resolved).** A `Link` whose right key is already bound to another identity of the same entity type merges the two identities. The survivor is the smallest id minted for any key of the entity, including keys that nothing had used yet (Proposition 1.8); a survivor that depended on arrival order was the cause of D21 (Ch. 6, 6.7). The loser gets an additive forwarding record, `Rep` follows the chain, and bindings are never modified. The engine remaps what it had emitted under the loser: the master graph gets a patch that moves the loser's relationships to the survivor, the telemetry windows of re-keyed relationships move with them, and derived graphs follow through their deltas. Two identities of different entity types are a conflict and the link is ignored. Tests (`IdentityMergeTests.cs`): forwarding chains over 2, 5 and 12 identities are acyclic and finite, every forwarded id decreases, `Rep` is idempotent; a merge changes resolution only for the involved keys and modifies no binding; the survivor is the same for all six orders of three merges and equals the minimum of the own ids of the keys; different types are a conflict and not a merge. On the two real captures every replay counts 35 and 43 merges and 0 conflicts, at every chunk size (Ch. 14, 14.5). Not kept: what each identity had emitted before a merge; the merge is realized by the remap patch and the forwarding record.
- **Entity id form** (not numbered in 14.8). Entity ids are `e:` plus 26 base32 characters of SHA-256 over the JCS of `[tenant, domain, component encodings]`: the preimage of Def. 1.9, truncated to 130 bits. The paper should adopt the truncated form or the engine the untruncated one.
- **D6 (resolved by changing the paper).** Relationship ids are scoped by the output ontology module and not by the graph (Ch. 5, Def. 5.9).

## 1.8 Obligations

| Id | Statement | Discharged by |
|---|---|---|
| O1.1 | Encoder conformance: `enc` equals a reference JCS implementation on a generated corpus including numerals in all forms, nanosecond instants, supplementary-plane text and nested lists | property test against an independent encoder, on the contract types |
| O1.2 | Theorem 1.7: survivor stability under concurrent merges in different projections, Proposition 1.8 on generated key sequences (it has one synthetic order case and two captures), split semantics | Q10 of [R03]: two projections link the same keys in different orders, replay with reordered independent inputs, compare final ids |
| O1.3 | E10: binding migration across canonicalization versions | experiment E10 in [R02] |
