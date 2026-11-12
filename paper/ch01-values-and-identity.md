# Ch. 1 Values, canonical form and identity

**Claim.** Every value has exactly one written form, so two values are equal exactly when their written forms are equal byte for byte. The name of an entity inside the model is computed from a key and from nothing else, and a name once given is never changed. When two names turn out to mean one entity, they are joined by an added record, and which name survives does not depend on the order in which the keys arrived.

Everything later rests on this chapter. A graph is compared with another graph by comparing written forms. A result is called reproducible when its written form is the same in two runs. This needs no earlier chapter except the terms of Ch. 0.

## 1.1 Values

A property of an entity holds a value: a name, a count, a time. Sources write the same value in many ways. One writes `7`, another `7.0`, a third `+007`. Before two values can be compared, each needs one agreed form.

**Definition 1.1 (value).** The set `𝒱` of values is the smallest set built by these eight forms:

`v ::= text(s) | integer(n) | decimal(q) | boolean(b) | instant(i) | null | entity(e) | list(v₁, …, vₘ)`

- `s` is a sequence of Unicode characters;
- `n` is an integer, of any size;
- `q` is a rational number with a finite decimal expansion;
- `b` is `true` or `false`;
- `i` is a point in time, on the UTC time scale, to the nanosecond;
- `e` is the name of an entity (Def. 1.9);
- a list has `m ≥ 0` items, in order.

*Example:* `text("api")`, `integer(3)`, `decimal(0.25)`, `instant(2026-11-10T10:57:00Z)`, `list(text("a"), text("b"))`.

**Definition 1.2 (kind).** The kind of a value is the form it was built with: one of text, integer, decimal, boolean, instant, null, entity, list. Two values of different kinds are never equal.
*Example:* `integer(1)` and `decimal(1)` are different values. The types of Ch. 2 fix one kind for each property, so this never surprises: a count is always an integer.

**Definition 1.3 (normal form).** The function `norm : 𝒱 → 𝒱` rewrites a value into its *normal form* by the rules below. A value is *normal* when `norm(v) = v`.

| Rule | Kind | Normal form | Example |
|---|---|---|---|
| N1 | integer | decimal digits, no `+`, no leading zeros, and `-0` is `0` | `+007` becomes `7` |
| N2 | decimal | as N1 before the point; after it no trailing zeros; no exponent | `1.50` becomes `1.5`, `2.0` becomes `2` |
| N3 | instant | `YYYY-MM-DDThh:mm:ss`, then a fraction of 1 to 9 digits without trailing zeros if it is not zero, then `Z`. Other time zones are converted to UTC. A leap second is rejected | `12:00:00+01:00` becomes `11:00:00Z` |
| N4 | text | the characters as given. No character is replaced by a similar one. A broken character code is rejected | `"Café"` stays `"Café"` |
| N5 | boolean, null, entity | as given | |
| N6 | list | the items in their order, each in normal form. There is no kind for unordered sets | |
| N7 | unit | a value stays in the unit that its property declares. Normal form never converts units | 500 in milliseconds stays 500 |
| N8 | property map | a finite map from property names to values. A name occurs at most once. A missing name is not the same as the value `null` | |

**Rule 1.1 (alphabet of names for types and properties).** The names of types and properties use only the characters `A` to `Z`, `a` to `z`, `0` to `9` and `_ . : -`, and have 1 to 200 characters. With this alphabet every usual way of sorting text gives the same order, which Def. 1.4 needs. Names of entities and relationships are not restricted. This rule is a proposal of this paper.

## 1.2 Canonical encoding

A normal form is still an abstract object. To compare and to fingerprint values they must be written as bytes, in exactly one way.

The writing uses **JSON**, a common text format for nested data: objects with named members, arrays, strings. JSON alone does not fix the order of members or the spacing. The **JSON Canonicalization Scheme**, a published standard ([rfc8785]), does: members sorted by name, no optional spaces, one way to write each string.

**Definition 1.4 (canonical encoding).** For a value `v`, let `tree(v)` be the JSON object with the member `kind` and, except for `null`, the member `value`: the normal form as a string for integer, decimal, instant, text and entity; a JSON boolean for boolean; an array of the `tree` of the items for a list. The *canonical encoding* `enc(v)` is the byte string that the JSON Canonicalization Scheme gives for `tree(norm(v))`. Property maps, states of elements and whole graphs are encoded in the same way (Ch. 3, Ch. 4).
*Example:* `enc(integer(+007))` is the 30 bytes `{"kind":"integer","value":"7"}`.

**Rule 1.2 (numbers are written as text).** Every whole number in every record, such as a count, a version number or a time in nanoseconds, is written as a string of digits in the form N1, never as a JSON number. The reason: a JSON number is in practice a floating-point number with 53 bits of precision, and a time in nanoseconds is larger than `2^53` ([rfc8785] recommends strings for such numbers). So a canonical encoding contains no JSON numbers at all.

**Definition 1.5 (digest).** Let `H` be SHA-256, a standard function that maps any byte string to 256 bits. The *digest* of an object `x` is `digest(x) = "sha256:" + hex(H(enc(x)))`. A digest is a short fingerprint.

**Assumption.** No two different byte strings with the same `H` are ever found (collision resistance). This is an assumption of the paper, shared with most of today's software, and not a result.

## 1.3 Equality

**Definition 1.6 (equality of values).** `v ≈ w` iff `norm(v) = norm(w)`. By the rules N1 to N8 this is equality of what the values denote: integers as integers, decimals as rational numbers, instants as points in time, texts as sequences of characters, lists item by item.

**Theorem 1.1 (canonical equality).** For all values `v` and `w`: `v ≈ w` iff `enc(v) = enc(w)` as byte strings. The encoding of a property map does not depend on the order in which its members were supplied.

*Proof.* (⇒) If `v ≈ w` then `norm(v) = norm(w)`, so the two trees are equal, and the canonicalization scheme is a function of the tree. (⇐) The scheme's output can be parsed back, so equal bytes give equal trees. A tree determines the kind, and then the normal form: for each kind the rules N1 to N5 admit exactly one string per denoted value, and a list is determined by its items in order, by induction on the nesting. So `norm(v) = norm(w)`. For a property map the scheme sorts the members by name, so every order of supply gives the same bytes. ∎

**Corollary 1.2 (cost).** Whether two values are equal can be decided in time proportional to their sizes: write both and compare the bytes.

**Corollary 1.3 (digests).** If `v ≈ w` then `digest(v) = digest(w)`. The converse holds under the assumption on `H`.

*Limits.* The definitions allow integers and decimals of any size and any instant. The prototype is narrower: integers and instants in nanoseconds use 64 bits (years 1678 to 2262), decimals have 28 digits. The prototype's encoder has not been compared with an independent implementation of the scheme.

## 1.4 Identity

Sources name things in their own ways. Kubernetes knows a pod by cluster, namespace and name. The network observer knows the same pod by an address. Inside the model each entity needs one name that does not change when the pod changes state, when the definitions change, or when time passes.

**Definition 1.7 (identity domain).** An identity domain `d` is a name together with an ordered list of components `(c₁:κ₁, …, cₙ:κₙ)`: a name and a kind for each. It says which values identify a kind of thing.
*Example:* the domain `pod` with the components `(cluster: text, namespace: text, name: text)`.

**Definition 1.8 (identity key, canonical key).** An identity key is `ik = (t, d, c)`: a customer `t` (the *tenant*), a domain `d`, and a tuple `c = (v₁, …, vₙ)` of values of the kinds that `d` asks for. Its *canonical key* under the rule set `ν` is the byte string

`ck_ν(ik) = canonical JSON of the array [t, name(d), enc(v₁), …, enc(vₙ)]`.

The rule set `ν` stands for the rules of 1.1 and 1.2; this chapter is rule set `ν₁`. If the rules ever change, there is a new `ν`. The rule set is not a part of the key.
*Example:* the key `(shop, pod, ("c1", "store", "api-7c9"))` names one pod of the shop of Ch. 0.

**Definition 1.9 (name of an entity).** `alloc_ν(ik) = base32(H(ck_ν(ik)))`: the hash of the canonical key, written with 32 symbols (lowercase letters and the digits 2 to 7). An *entity id* is such a value, or an id that a binding introduces (Def. 1.10).
*Example:* the pod above gets an id that begins `mz4q2a7k…` (invented here for illustration). Anyone who knows the key can compute it, and nobody has to hand it out.

**Definition 1.10 (binding set).** A binding set `B` is a finite partial map from pairs `(ν, ck)` to entity ids. With each entry it stores the components that produced the key. It grows only by three rules, and no rule changes or removes an entry.

- **Allocate.** If `B` has no entry for `(ν, ck_ν(ik))`, add the entry `(ν, ck_ν(ik)) ↦ alloc_ν(ik)`.
- **Alias.** Given an entity `e`, a key `ik` without an entry, and a record of evidence that `ik` denotes `e`, add `(ν, ck_ν(ik)) ↦ e`.
- **Migrate.** For a new rule set `ν'` and every entry `(ν, ck) ↦ e` with components `c`, add `(ν', ck_{ν'}(t, d, c)) ↦ e`.

`resolve(B, ik)` is the entry of `B` for the key under the current rule set if there is one, and a rejection otherwise.
*Example:* the pod is first seen by Kubernetes and gets its id by *allocate*. Later a network record names it by an address, and a rule that knows the address belongs to this pod adds the address key by *alias*, pointing to the same id.

## 1.5 Theorems on identity

**Theorem 1.4 (the name is a function of the key).** If two keys are equal (same tenant, same domain name, components equal by `≈`), then `alloc_ν` gives them the same id. Two projections, two graphs or two replays that compute an id from equal keys get the same id without talking to one another, and two writers that bind the same key at the same time write the same entry.

*Proof.* `alloc_ν` is the composition of `ck_ν`, `H` and the base-32 writing, each a function. By Theorem 1.1 the canonical key is the same for equal keys. Two writers evaluate the same function on the same bytes. ∎

Keys from different domains, or with different components, get different ids unless `H` collides. That is intended: two domains name different things.

**Theorem 1.5 (a binding is timeless).** Let `B` be built by the three rules and let `B'` extend it by further uses of the rules. Then every entry of `B` has the same value in `B'`. The value of `resolve(B, ik)` depends on `B` and `ik` only: not on the state of any entity, its type, the version of the definitions, a version of a graph, or the clock.

*Proof.* Each rule adds an entry where `B` had none, so no earlier entry changes. `resolve` reads only `B` and `ik`. ∎

**Theorem 1.6 (a change of the rules keeps the names).** Suppose every entry stores the components that produced its key. For a new rule set `ν'`, *migrate* computes a new key for every entry. Unless a new key is already bound to another entity, which is a conflict that rejects the migration, the entry `(ν', ck') ↦ e` exists afterwards for every old entry `(ν, ck) ↦ e`. No entity id changes, and the old key and its new image resolve to the same entity.

*Proof.* *Migrate* recomputes the key from the stored components, which is always possible. It adds an entry unless one exists. If one exists with another entity, the migration is rejected and the conflict is reported; nothing is resolved silently. Otherwise the new entry maps to the same `e`. ∎

*Limits.* The theorem is about the mechanics. Whether a new rule set should also treat two formerly different keys as one is a decision about the subject matter, recorded as an *alias* with its evidence. A replay across a change of the rule set has not been tried on the prototype.

Sometimes two ids were given to what is in truth one entity: the pod was seen under its address before anyone knew which pod has that address. The two must become one, without changing any binding.

**Theorem 1.7 (merge).** A *merge* of two entities is an added *forwarding record* `e₂ → e₁`, allowed only if `e₁ < e₂` in the order of ids as text: the smaller id survives. A record is added only from an id that has none, that is from a current representative, so at most one record leaves any id. The *representative* `rep(e)` is found by following records from `e` until none applies. Then:

- (a) chains of records have no cycle and are finite;
- (b) `rep(rep(e)) = rep(e)`;
- (c) for a key whose entity is not involved in a merge, `rep(resolve(B, ik))` is unchanged by it;
- (d) no entry of `B` is modified.

The opposite of a merge, a *split*, is an explicit added record as well and is never inferred.

*Proof.* (a) Each step goes to a strictly smaller id, so no id repeats, and there are finitely many ids. (b) `rep(e)` has no record leaving it, by construction. (c) A record `e₂ → e₁` changes `rep` only for entities whose chain passes through `e₂`. Since `e₂` was a representative, these are exactly the entities it represented. Without the condition that a record leaves only a representative, an id could carry two records and `rep` would not be a function. (d) Records are kept in a set of their own. "All keys now point to the survivor" means that `rep` is applied when a key is resolved, not that `B` is rewritten: Theorem 1.5 requires bindings to stay as they are. References from the past resolve through the chain. ∎

*Example:* ids `e:b…` and `e:f…` are found to be one pod. The record `e:f… → e:b…` is added. Every key that resolved to `e:f…` now has the representative `e:b…`.

*Limits.* How a split behaves is not defined further in this paper. Merges that two projections make at the same time have not been studied.

It remains to say which ids are merged, and to show that the outcome does not depend on the order in which the keys and the evidence arrive. That matters for Ch. 6: a result that depended on arrival order would not be reproducible.

**Proposition 1.8 (the survivor does not depend on the order).** Let `K` be a set of identity keys of one domain, and `L` a set of *links*, each naming two keys of `K` that denote one entity of one type. Let `own(k)` be the id that *allocate* gives to `k` alone. Process the keys and links in any order by this rule: a key gets its own id when it is first used or first named by a link; a link between two keys forwards the larger of their two representatives to the smaller. Then for every class `C` of keys that the links connect, the representative is `min { own(k) : k ∈ C }`, whatever the order and however the input is cut into batches, and no binding is modified.

*Example:* three keys with own ids `e:c…`, `e:a…`, `e:k…`, and two links joining the first with the second and the second with the third. In every order of arrival the three end with the representative `e:a…`.

*Proof.* `own` depends on the key alone (Theorem 1.4), so it is fixed before anything is processed. Call a key *seen* once it was used or named by a processed link, and let `~` be the equivalence that the processed links generate on the seen keys. The following holds after every step: for every seen key `k`, `rep(k) = min { own(k') : k' seen, k' ~ k }`, and every forwarding record points from a larger id to a smaller one.

- *Start.* No key is seen.
- *First use of a key `k`.* Its class is `{k}`, and it gets `own(k)`, the minimum of a set of one. No other class changes.
- *A link between `k₁` and `k₂`.* A key not yet seen becomes seen with its own id, as before. If the two are already in one class, nothing changes. Otherwise their classes `C₁` and `C₂` have the representatives `m₁ = min own(C₁)` and `m₂ = min own(C₂)`, which differ unless `H` collides. The rule forwards the larger to the smaller, so the class `C₁ ∪ C₂` has the representative `min(m₁, m₂) = min own(C₁ ∪ C₂)`. The new record goes from larger to smaller, so chains still decrease and end at the minimum (Theorem 1.7).

When everything is processed, the seen keys are the keys used and named, and `~` is the equivalence generated by `L`, in any order. So the right-hand side is the same for every order, and so is `rep`. Cutting the input into batches does not reorder it (Ch. 6, Theorem 6.2), so the same holds after every initial part of the input. Only records were added, so no binding is modified (Theorem 1.7 (d)). ∎

*Limits.*
- The proof is about the rule. That the prototype follows the rule is tested by examples and on two recordings of a real cluster, which gave the same result at every batch size (Ch. 14). It is not yet tested on generated orders of arrival.
- A link between keys of two different types of entity is a conflict: it is counted and ignored.
- The minimum is over the keys that were seen. A key that never arrives plays no part.
- Measurements stored under an id that does not survive move to the survivor; how they combine is the subject of Ch. 7.

## 1.6 What the prototype does differently

Three points, so that no claim of this chapter is read into the prototype by silence.

- **Bounded numbers.** As said in 1.3: 64-bit integers and instants, 28-digit decimals.
- **Shorter ids.** The prototype keeps the first 130 bits of the hash, written as 26 symbols after the prefix `e:`. The chance of a collision is larger than with 256 bits and still negligible for any estate.
- **Bindings as a table.** The binding set is kept as a table that only grows. Ch. 2 describes a form in which keys, identities and evidence are themselves nodes of a graph; that form is not built.
