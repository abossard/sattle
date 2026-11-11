# Notation

Fixed once here. Chapters add no symbols without adding them here. Terms are defined in [glossary.md](glossary.md).

## Universes

| Symbol | Meaning |
|---|---|
| `T`, `t` | tenants, a tenant |
| `Γ`, `g` | graph ids, a graph id (a graph is scoped by `(t, g)`) |
| `𝓔`, `𝓡` | entity ids, relationship ids |
| `K = 𝓔 ⊎ 𝓡 ⊎ {κ_O}` | element keys. `κ_O` is the single ontology slot key of a graph |
| `Ty`, `Π` | type ids, property ids |
| `𝒱` | graph values (Ch. 1) |
| `S` | element states: entity state, relationship state, ontology reference |
| `⊥` | absence of an element. `null` is a value, `⊥` is not |

## Values, canonical form, identity (Ch. 1)

| Symbol | Meaning |
|---|---|
| `v ≈ w` | value equality after normalization |
| `enc(v)` | canonical encoding of `v`: RFC 8785 (JCS) applied to the normalized value tree |
| `H` | SHA-256. `digest(x) = "sha256:" + hex(H(enc(x)))` |
| `d` | identity domain |
| `ik = (t, d, c)` | identity key: tenant, domain, component tuple `c` |
| `ck(ik)` | canonical key bytes of `ik` under canonicalization version `ν` |
| `B` | binding set, an append-only partial map from `(ν, ck)` to `𝓔` |
| `alloc(ik)` | derived entity id for a first binding |
| `o = (id, version, digest)` | ontology reference |

## Graphs and revisions (Ch. 3)

| Symbol | Meaning |
|---|---|
| `G : K ⇀fin S` | graph (finite partial map). `dom G` is its set of present keys |
| `G[k ↦ s]` | update. `G[k ↦ ⊥]` removes `k` |
| `U(G)` | relationship uniqueness invariant on `(source, target, type)` |
| `r ∈ ℕ`, `succ(r) = r + 1` | revision: position in the per-graph log. `r = 0` is the empty genesis state |
| `G_r` | state of graph `g` at revision `r` |
| `h(g)` | head revision of `g` |
| `c_r = (E_r, φ_r, cause_r)` | commit record of revision `r`: edit set, frontier and cause (Def. 3.8) |
| `φ` | frontier: source positions, input graph revisions, configuration revision |
| `s` | ingestion sequence number |
| `λ` | logical time (explicit input, never the wall clock) |
| `τ` | observed time (valid time of a sample or event) |

## Change algebra (Ch. 4)

| Symbol | Meaning |
|---|---|
| `e = (k, b, a)` | edit: key, before state, after state. Effective iff `b ≠ a` |
| `p = (g, r, r', E, cause)` | patch from revision `r` to `r'` with edit set `E` (distinct keys) |
| `from(p)`, `to(p)` | the two revisions of `p` |
| `apply(p, G)` | partial application (guarded) |
| `p then q` | composition, defined iff `to(p) = from(q)` and guards agree (Def. 4.2) |
| `inverse(p)` | inverse patch: swaps `from`/`to` and each edit's `b`/`a` |
| `net(p)` | net form: guard-only edits removed (lossy, Def. 4.4) |
| `f = (k, s)` | fact: a canonical element state under its key |
| `F` | set of facts |
| `ℤ^(F)` | signed deltas: finite-support maps `F → ℤ`. `z`, `z₁`, `z₂` are deltas |
| `χ(G)` | embedding of a graph into `ℤ^(F)` |
| `δ(p)` | signed delta of a patch |
| `m_B(z)` | materialization of `z` at base `B` |

## Telemetry (Ch. 7)

| Symbol | Meaning |
|---|---|
| `M_{g,k,p}` | telemetry series of property `p` on element `k` in graph `g` |
| `(τ, w, x, i)` | sample: observed time, window width (0 = instant), value, input id |
| `W = [a, b)` | window |
| `ω` | closed-window record |
| `view(k, r, [τ₁, τ₂))` | element view: structure at `r` joined with series over a time range |
| `f : V_B ⇀ V_L` | image of a level `L` over the graph below `B` (partial function) |
| `E_L`, `E_B(x, y)` | lifted edge set under `f`; fibre of the lifted edge `(x, y)` |
| `c_L`, `ρ_L` | lifted counter (sum over the fibre); ratio recomputed from lifted counters |

## Performance (Ch. 11, 13)

| Symbol | Meaning |
|---|---|
| `Δ`, `\|Δ\|` | a change and its size |
| `Hn` | hypothesis `n` (Ch. 13). Each has a benchmark `Bn` |
| `En` | experiment `n` from research 02 that settles a risk |

## Conventions

- Law names: `Definition c.n`, `Theorem c.n`. `Theorem c.n (Tm)` cross-references the review's table of laws.
- Proof status labels: **proved** (proof in the chapter), **proof sketch + citation**, **obligation** (statement with the experiment or proof that would discharge it).
- Evidence labels for numbers (Ch. 11, 13): **doc** (stated by cited documentation), **lit** (published measurement on another workload), **assumption** (input to an experiment), **hypothesis** (target to be tested), **measured** (measured in this project by the prototype, Ch. 13 and 14).
- Text in canonical examples uses `then` and `inverse`, never "addition" and "negation" for patches (finding 11). "Addition" and "negation" belong to signed deltas only.

## Added for Ch. 2 to 10

| Symbol | Meaning |
|---|---|
| `Σ_X`, `props_Σ(T)` | ontology closure of module `X`; properties of type `T` in the closure (Ch. 2) |
| `G ⊨ Σ` | conformance of a graph to an ontology closure (Def. 2.6) |
| `K` | the kernel graph (Ch. 2). Not to be confused with the key set `K` of Ch. 3, which is written in the same letter: the kernel is always called "the kernel" in text |
| `κ` | value kind (Ch. 1, 5). `κ_O` is the ontology slot key, `κ_r` a relationship key rule |
| `φ = (κ, π, ρ)` | field type: kind, presence, role (Def. 5.4); in Ch. 3 `φ` is a frontier |
| `⊔` | role join (Def. 5.5) |
| `rid(o, κ_r, s, t)` | relationship id, `o` the module of the output ontology (Def. 5.9) |
| `Q`, `Q^Δ = D ∘ Q ∘ I` | denotation and incremental form (Ch. 6). `I` integral, `D` difference |
| `σ(k)` | support of element `k` (Def. 6.4, 9.7) |
| `tw` | telemetry watermark in a frontier entry `gin[g'] = (r, tw)` (Def. 6.8) |
| `get`, `put`, `Acc(s)` | lens, accepted edit domain (Ch. 8) |
| `δ(rule)` | information class `iso < lens < none` of a rule (Def. 8.4). Not the signed delta of a patch `δ(p)` of Ch. 4; the two never occur together, and the rule form always takes a rule as argument |
| `H_r(k)`, `R_r` | element history and retained set (Def. 9.2, 9.3) |
| `Replay(P', G, R)`, `Eval(P', R)` | edit replay and batch evaluation (Def. 9.5) |
| `π` | carry policy of a changed projection (Def. 9.5). Presence `π` in field types (Def. 5.4) is a different symbol in a different chapter |
| `L(f)` | lineage of a fact (Def. 9.8) |

## Added for Ch. 12 and 14

| Symbol | Meaning |
|---|---|
| `rev_of(τ)` | largest revision whose logical time is at most `τ` (Def. 12.4) |
| `D = (order, blocks)` | document (Def. 12.5) |
| `bid` | block id (Def. 12.5) |
| `D1` to `D36` | deviations found by the prototype (Ch. 14, 14.8) |
| `W = (E, ρ, Σ, f, d)`, `α`, `β`, `μ_i`, `Q(k, r)` | symbols local to Ch. 13 (workload model of Def. 13.1, cost coefficients, rates) and Ch. 11 (a point read); inside those chapters they replace the general meanings of `E`, `ρ`, `Σ`, `f` and `Q` |
| `F1`, `F5` | premises: F1 of Theorem 3.5, F5 of Theorem 6.5 (finite domain of a fixpoint) |
| A to E | evidence classes (Ch. 14, 14.1) |

## Added for the revision of the prototype status (Ch. 4, 5, 7, 9, 10, 12)

| Symbol | Meaning |
|---|---|
| `ξ = (p₁, …, p_n)`, `D_n` | a draft as a sequence of catalog patches and its state at draft revision `n` (Def. 4.10). `ξ` is not the carry policy `π` of Ch. 9 |
| `Out` | the output union of a graph space (Def. 5.13). Not the uniqueness invariant `U` of Ch. 3 |
| `A`, `A^{≤k}` | the reflexive relation `Affects` and its paths of at most `k` edges (Def. 7.12) |
| `D` | the depth bound of the roll-up traversal (16 in the health model, Def. 7.12, Proposition 7.8) |
| `B1`, `B2`, `B3`, `B4` | the premises of Theorem 6.2 (one writer per telemetry key, late rows by logical time, identity binding independent of arrival order, a step is its inputs in order for carried threshold state). Not the binding rules B1 to B3 of Def. 1.10, which are used in Ch. 1 only |
| `sev`, `own`, `w`, `state` | severity of a signal, worst own severity of a component, worst over the reach, health state (Def. 7.12) |
| `seq` | server-wide sequence number of an event of the protocol stream (Def. 12.7). Not the capture envelope's `seq`, which orders the records of one file (Def. 10.2) |

## Added at the gate 1a pass

Named premises. A premise is a property that the model does not give; the statement that uses it is "proved under premises".

| Symbol | Meaning |
|---|---|
| `KS` | kernel signatures: every builtin, canonicalizer and aggregate returns its declared kind or faults (Theorem 5.2) |
| `RT` | round trip: `parse(print(G)) = G` on the canonical graphs (Theorem 5.6, Proposition 4.11) |
| `R0` to `R5` | premises of Proposition 5.7 (full replay, closed selection, copied ports, disjoint keys, no reader of what moves, one output ontology) |
| `KW1` to `KW3` | the list-and-watch contract of the Kubernetes API (Theorem 10.5) |
| `LV1` to `LV4` | premises of Proposition 10.7 (live run and replay) |
| `LR1` to `LR4` | premises of Proposition 10.8 (live resume) |
| `J1` to `J3`, `SL1` | premises of Propositions 11.9 and 11.10 (journal, state seal). `SL1` was called S1 before; S1 to S5 are the store properties of Def. 11.9 and S1 to S4 the space judgments of Def. 5.8 |
| `V1` to `V3`, `F1` to `F3`, `L1` to `L4` | conditions local to Ch. 8, 8.4: of a v1 grammar, of a translatable source graph, of a triple of the language. `F1` there is not the premise F1 of Theorem 3.5 |
| (N), (Q1) to (Q4) | the name condition and the four forms of consulting a closure, local to Theorem 2.5 |

| Symbol | Meaning |
|---|---|
| `Can` | the canonical definition graphs: the image of `parse` (Theorem 5.6) |
| `live(λ)` | the rows with `time(r) + age > λ` (Proposition 5.9) |
| `mat(F)` | the facts and telemetry windows that Def. 6.4 gives a family of fragments (Proposition 5.7) |
| `L_S(f)` | lineage of an output row at input state `S` (Theorem 9.7); `S\|_L` the restriction of `S` to the rows in `L` |
| `(S, C, T)`, `L(G)`, `FWD`, `BWD`, `tid` | triple of source, correspondences and target; language of a v1 grammar; forward and backward translation; target id of a correspondence (Def. 8.7, 8.8). Local to Ch. 8, 8.4, where `S` and `T` are graphs |
| as-if value | the value a window of the survivor of a merge would hold if every sample had been written under the survivor (Law 7.5) |
