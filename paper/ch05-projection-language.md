# Ch. 5 Projection language

**Claim.** A projection is a typed pipeline of operators from a closed set and is checked before it runs. Telemetry cannot influence structure except through a crossing operator. A projection that summarizes a graph is a higher level of abstraction over it: it lifts the edges together with the nodes (5.8).
**Status.** Built: 19 of the 20 operator kinds of Def. 2.2 (`Match` without quantified paths, `Filter`, `Map`, `Window`, `Group`, `Series`, `Rollup`, `Threshold`, `Join`, `Traverse`, `Fixpoint` as a bounded unrolling, `Values`, `Union` with the policy `preserveEvidence`, `Distinct`, `Retain`, `Lift`, `EmitEntity`, `EmitRelationship`, `Link`), parameters of projections (Def. 5.14), aggregates declared per context in `GET /palette` (Def. 5.6, D26), quantities and units for numeric parameters (a duration is nanoseconds on the wire and `24h` in text), refactoring (5.7), and levels with lifted edges written with the `lift` statement, which is lowered to existing operators (5.8). Tested on examples: typed errors with positions, refactoring on one module, retention on fixtures and both captures, the aggregate matrix at the checker, edge lifting and the `lift` statement (28 and 36 cases, run in set A at evaluation 10; set A is runs of the project that the authors did not repeat, Ch. 14, 14.1). Proved on the model: the properties of lifting (Propositions 5.10 to 5.12) and retention (Proposition 5.9). Proved under named premises at gate 1a, each restated with counterexamples to its first form: type soundness (Theorem 5.2, premise KS, with runtime faults local to their row, Def. 5.19), lossless syntax on canonical graphs (Theorem 5.6, premise RT), refactoring (Proposition 5.7, premises R0 to R5, of which the engine checks two). Not built: `Delta`, the `Union` policies `requireEqual`, `preferFirst` and `preferLast`, rule class `sync` (Ch. 8). Deviations D2, D5, D6, D11, D13, D22, D25, D26, D32.
**Gate.** 1b (flexible and expressive): the language is the subject of that gate, and the chapter does not yet state what the language can and cannot express. 1a (theory) for its typing theorems and the properties of lifting.
Uses: Ch. 1 (identity), Ch. 2 (kernel, closure, conformance, telemetry declaration), Ch. 3 (graph space), Ch. 4 (patches).
Used by: Ch. 6, 7, 8, 9.
Sources: [R03] sections 2.3, 2.5, 3, 5, 7, 8; [R01] findings 4, 13 to 20, gaps 1, 5, 8.

A **projection** is a definition: a typed graph in the catalog (Def. 2.1). Its output is a derived graph (Def. 3.11). This chapter fixes the structure of the definition, the type system that makes every accepted definition checkable at load time, the identity expressions, and the concrete text syntax. The semantics (what a definition denotes, and its incremental form) is Ch. 6. The direction classes of rules are Ch. 8.

## 5.1 Structure of a projection

**Definition 5.1 (projection).** A projection node has `purpose ∈ {ingestion, view, migration, verification}`, `retain ∈ ℕ` (evidence entries per element, Ch. 9), an output ontology (`outputOntology` edge to a module), ports, operators, and rules. A projection without rules is `forward` (Ch. 8). Operators and wires form a directed acyclic graph outside `Fixpoint` bodies.

**Definition 5.2 (port, wire, port signature).** A port has a name, a mode `in` or `out` (the *port mode*, not a direction in the sense of the glossary), and a kind `rows` or `graph`. A rows port has a `RowType` (Ch. 2, adapter-provided). A graph port has an `expects` edge to a module (the input ontology). A `wire` runs from a producing port (a projection in-port, an operator out-port, a `Fixpoint` internal `rec`) to a consuming port of the same kind. Each operator kind has a fixed **port signature** in the kernel (K9):

| Operator | In ports | Out ports | Class (Ch. 6) |
|---|---|---|---|
| `Match` | `graph` (graph) | `out` | bilinear for edges |
| `Filter`, `Map`, `Distinct`, `Window` | `in` | `out` | linear (`Distinct` non-linear) |
| `Retain` | `in` | `out` | linear on arrival, withdrawal by the frontier (Def. 5.15) |
| `Join` | `left`, `right` | `out` | bilinear |
| `Union` | `in0 … in(n-1)`, `n ≥ 2` | `out` | linear |
| `Group`, `Rollup` | `in` | `out` | per-key |
| `Traverse` | `graph` (graph), `in` | `out` | bilinear, bounded |
| `Fixpoint` | `seed`; internal `next` | `out`; internal `rec` | recursive |
| `Values` | none | `out` | constant |
| `Series` | `graph` (graph), `in` | `out` | telemetry read |
| `Delta`, `Threshold` | `in` | `out` | stateful per key |
| `EmitEntity`, `EmitRelationship`, `Link` | `in` | none (sinks) | linear |

**Definition 5.3 (operator kernel).** The operator kinds are the closed set of Def. 2.2. Their properties are: `Union.conflict ∈ {requireEqual, preferFirst, preferLast, preserveEvidence}`; `Traverse.direction ∈ {out, in, either}` (a traversal direction), `minDepth`, `maxDepth ≤ 16`; `Window.timestampField`, `width`, `late ∈ {ignore, revise, reject}`; `Fixpoint.maxFacts`, `maxIterations`; `EmitEntity.mode` and `EmitRelationship.mode ∈ {create, update}`; `EmitRelationship.key` (the relationship key rule) and `retype ∈ {reject, keepExisting, takeNew}`; `Series.within`; `Delta.target`; `Threshold` parameters `above, enterAfter, high, below, exitAfter, low, initial`. Expression kinds: `FieldRef`, `Literal`, `Call`, `IdOf`. `Call` names a builtin `Function` of the kernel: comparisons, `And`, `Or`, `Not`, `Add`, `Subtract`, `Multiply`, `Divide`, `Negate`, `ToDecimal`, `Concat`, `Coalesce`, `IsPresent`, `If`, `Rate`, `InList`, `LowerInvariant`, `Trim`. Function semantics, signatures and inverses are compiled (Ch. 2, gap 1). There are no user-written functions, no reflection, no ambient IO.

## 5.2 Types

**Definition 5.4 (kinds and field types).** Kinds `κ ::= Text | Integer⟨u⟩ | Decimal⟨u⟩ | Boolean | Instant | Ent⟨T⟩ | Rel⟨R⟩ | List⟨κ⟩ | Null`, with `u` a unit (`1` if absent), `T, R` type nodes and `⊤` any entity type. A field type is `φ = (κ, π, ρ)` with presence `π ∈ {req ⊑ opt}` and role `ρ ∈ {data} ∪ {tele⟨τ, P⟩}`, temporality `τ ∈ {gauge, delta, cumulative}` and period `P`, ordered `data ⊑ tele⟨τ, P⟩`. A row type `R` is a finite map from field names to field types. It carries a window width `W(R)` when it has a `windowStart` field produced by `Window`, `Series` or `Rollup`. Subkinding `κ₁ ≤ κ₂` is reflexive, `Ent⟨T₁⟩ ≤ Ent⟨T₂⟩` iff `T₁` is a descendant of `T₂` under `extends`, `List` is covariant, and there is no numeric widening.

**Definition 5.5 (role join).** For computed values: `data ⊔ ρ = ρ`; two telemetry operands need equal `P`; temporality follows the algebra of Def. 5.6 (for example `delta ± delta = delta`, `delta ÷ delta = gauge`, `delta × data = delta`, any arithmetic on `cumulative` is an error).

**Definition 5.6 (temporality algebra).** For aggregates over telemetry fields: `sum`, `min`, `max` preserve `gauge` and `delta` and reject `cumulative` (apply `Delta` first); `mean` gives `gauge`; `weightedMean` is allowed on `gauge` only; `last` on `gauge` only; `count` gives `gauge`. `Rate` maps `delta` of unit `u` and period `P` to `gauge` of unit `u/s`. `Delta` maps `cumulative` to `delta`, resetting on decrease. `Threshold` accepts `gauge` or `delta`. A property declared with `combine = mean` may only be rolled up again through `weightedMean` with an explicit weight. The checker enforces that `combine(p)` of an emitted telemetry property equals the roll-up function that produced the value.

### Judgments

`R ⊢ e : φ` for expressions and `Γ ⊢ o : R̄in ⇒ Rout` for operators. `Σ_p` is the input closure of graph port `p`, `Σ_out` the output closure.

```text
(E-Field)  R(f) = φ                                            ⟹  R ⊢ f : φ
(E-Prop)   v : (Ent⟨T⟩ or Rel⟨T⟩, π, ρ) bound by a Match, p ∈ props_Σp(T),
           role(p) = graphData                                  ⟹  R ⊢ v.p : (kind(p), π ⊔ (required ? req : opt), ρ)
           (a telemetry property is never read as a scalar; only through Series)
(E-Lit)    literal ℓ of kind κ                                  ⟹  ⊢ ℓ : (κ, req, data)
(E-Call)   sig(f) = (κ₁..κₙ) → κ₀ under θ, R ⊢ eᵢ : (κᵢ', req, ρᵢ), κᵢ' ≤ θκᵢ
                                                                ⟹  R ⊢ f(e̅) : (θκ₀, req, ⊔ρᵢ)
           (an optional argument is a type error, except in Coalesce and IsPresent)
(E-Id)     domain d with components c₁..cₖ of kinds κ₁..κₖ, R ⊢ eᵢ : (κᵢ', πᵢ, data), κᵢ' ≤ κᵢ
                                                                ⟹  R ⊢ IdOf(d, e̅) : (Ent⟨identifies(d) or ⊤⟩, ⊔πᵢ, data)

(Match)    node types in Σp; each edge type compatible with its endpoint types
           (compat(T,S) = S = ∅ ∨ ∃s ∈ S. T ≤* s ∨ s ≤* T); pattern connected
           ⟹  () ⇒ {vᵢ : Ent⟨Tᵢ⟩} ∪ {e : Rel⟨Rt⟩} ∪ read set (every v.p used downstream)
(Filter)   R ⊢ e : (Boolean, req, data)                          ⟹  R ⇒ R refined by top-level IsPresent
(Map)      R ⊢ eᵢ : φᵢ, targets distinct                          ⟹  R ⇒ {aᵢ : φᵢ}
(Join)     dom Rl ∩ dom Rr = ∅, on = ⋀ lᵢ == rᵢ of equal kinds, req, data   ⟹  Rl ∪ Rr
(Union)    all Rᵢ have the same fields with equal kinds and roles  ⟹  those fields; a field is opt if it is opt in some Rᵢ
(Distinct) every field has role data                              ⟹  R ⇒ R
(Group)    keys and aggregated fields have role data              ⟹  keys ∪ aggregates
           (telemetry aggregates use Rollup)
(Window)   R(ts) : (Instant, req, data), width > 0                ⟹  R ∪ {windowStart, windowEnd}, W = width
(Retain)   R(ts) : (Instant, req, data), age > 0, W(R) = 0         ⟹  R ⇒ R
(Series)   reads v.pᵢ name telemetry properties of one period P, within ≥ P
           ⟹  R ∪ {windowStart, windowEnd} ∪ {pᵢ : (kind, req, tele⟨τᵢ, P⟩)}, W = P
(Rollup)   W(R) = P, keys data, aggregates f allowed for τ by 5.6
           ⟹  keys ∪ {windowStart, windowEnd} ∪ {aⱼ : tele⟨τ', P⟩}
(Delta)    value : (N⟨u⟩, req, tele⟨cumulative, P⟩), N ∈ {Integer, Decimal}               ⟹  keys ∪ {target : tele⟨delta, P⟩}
(Threshold) W(R) = P, keys data, value : tele⟨gauge or delta, P⟩, above > below,
           enterAfter ≥ 1, exitAfter ≥ 1, high, low, initial text literals
           ⟹  keys ∪ {target : (Text, req, data)}
(Fixpoint) seed : R_rel; body over rec : R_rel yields next : R_rel; body operators in
           {Match, Filter, Map, Join, Union(requireEqual), Distinct, Traverse}; no IdOf in the body;
           every Ent field originates from the finite port `over`; maxFacts, maxIterations > 0
(EmitEntity) T ∈ Σout, not abstract; id : Ent⟨T'⟩; each set p := e has p ∈ props_Σout(T), κ ≤ kind(p),
           every required property set for create, a required property takes a req value,
           ρ ⊑ role(p); for telemetry p: W(R) = period(p)
(EmitRel)  Rt ∈ Σout, not abstract; endpoints typed Ent; endpoint types compatible with Rt where known;
           create: key unique among create emitters; update: key used by a create emitter of the same Rt
(Link)     policy q with between(q) = {d₁, d₂}; left = IdOf(d₁, ..), right = IdOf(d₂, ..); confidence literal in [0,1]
```

`Traverse` and `Values` follow the same pattern ([R03] section 3.2 has all rules). Two rules are stated more exactly than in the first form, as the engine's checker has them: `Union` compares kinds and roles and joins presence (it does not ask for equal presence), and an emit refuses an optional value for a required property. Theorems 2.5 and 5.2 use both. The *telemetry operators* `Series`, `Rollup`, `Delta` and `Threshold` close the forward reference of Ch. 7 (5.5).

**Definition 5.7 (wires and projection judgments).**
- W1 every consuming port has exactly one incoming wire; W2 wires join a producing port to a consuming port of the same kind; W3 the consuming port's type is the producing port's type; W4 outside fixpoint bodies the operator graph is acyclic; W5 every sink is reachable from a projection in-port or a `Values` operator, and unused out-ports are warnings.
- P1 every emitted type is in `Σ_out`; P2 *ownership*: if a rule of class bijective, lens or sync sets `(T, p)` or emits a relationship type `Rt`, no other rule of the projection sets `(T, p)` or emits `Rt`, and rules of those classes set no telemetry property and contain no telemetry operator on their sink paths; P3 each rule satisfies the direction judgment (Ch. 8); P4 *role non-interference*: every structural position (filter predicates, join keys, group and rollup keys, distinct rows, identity components, emit ids and endpoints) is typed `data`, and telemetry values reach only telemetry properties, except through `Threshold`; P5 edit policies cover every required property of the source entity type (Ch. 8).

**Definition 5.8 (graph space judgments).** At activation: S1 exactly one master graph, produced by a projection with purpose `ingestion`; S2 every projection port is bound exactly once, a rows port to a `Source` whose `provides` is the same `RowType` node, a graph port to a `Graph` whose producer's output ontology is the port's `expects` module; S3 graph production is a DAG and every graph is reachable from the master (Def. 3.11); S4 the space closure holds one release per module and the kernel release equals the engine's. Every `Source.adapter` is registered and its declared contract digest equals the pinned row-contract digest.

## 5.3 Identity expressions

**Definition 5.9 (identity expression, relationship key).** An emit's `id` is either a reference to a matched entity (reuse its timeless id, ADR-001) or `IdOf(d, c̄)`, typed by E-Id. `IdOf` resolves the key `(t, d, c̄)` through the binding set (Def. 1.10) or mints `alloc(ik)` (Def. 1.9). An aggregate output is a new entity in a domain whose components are the group keys. It never reuses a member's identity (ADR-094). Membership is an ordinary output relationship, not an identity edge. An optional component makes the `IdOf` optional, and the consuming emit skips the row.
A relationship emit names a **key rule** `κ_r` (the `key` property). The relationship id is `rid(o, κ_r, s, t) = base32(H(JCS([tenant, o, κ_r, s, t])))`, where `o` is the module of the output ontology of the projection. **Neither the type nor the producing projection or graph is an input.** A retype keeps the id (ADR-086), and moving an emit to another projection or graph of the same output ontology keeps it (Proposition 5.7). Two graphs of one ontology that hold the same key rule and endpoints hold the same id, which is harmless because uniqueness `U` (Def. 3.3) is per graph.
Cross-domain equivalence is evidence, not union. `Link` emits an `Evidence` node with `left` and `right` edges to two `Key`s under an `EquivalencePolicy` between two domains. In mode `assert` with confidence at least the threshold the evidence is accepted at once and a `sameAs` edge is materialized; in mode `review` it stays `proposed` until a configuration decision. An accepted `sameAs` between keys bound to different identities is a merge in the sense of Theorem 1.7 (smaller-id survivor, `mergedInto` forwarding). Splits only by explicit decision. `IdentityEvidence.Confidence` is a decimal in `[0,1]` compared with the threshold and is otherwise uninterpreted (finding 30).

**Definition 5.10 (collision policy).** When retyping `r` from type `A` to `B` would produce the uniqueness tuple `(s, t, B)` already held by `r'`, there is no policy to choose: Def. 6.4 decides, the relationship with the smaller id is present and the other is absent and reported. An earlier form of this definition offered the policies `reject`, `keepExisting` and `takeNew`; they are withdrawn (Theorem 5.5). For `Union.conflict`: `requireEqual` fails on unequal values, `preferFirst` and `preferLast` choose by port order, `preserveEvidence` emits conflict structure defined in the output ontology (ADR-032). `Union` of rows has no other conflict policy.

## 5.4 Theorems

**Theorem 5.1 (checking is decidable).** Load-time checking of a module release (structural conformance, K1 to K11, ontology well-formedness, all projection judgments) is decidable, and each judgment is evaluated by a bounded number of lookups per definition element. Fixpoint admissibility is a syntactic check.
Status: **proved**.

*Proof.* The expression and operator judgments are syntax-directed: each rule has premises on children and on finitely many ontology lookups. Types of a port are fixed by the port signature, so a topological pass over the acyclic operator graph computes all row types. K1 to K11 are finite conditions on the graph. The conditions on `Fixpoint` (body operator set, no `IdOf`, finite port) are conditions on node kinds. ∎

**Definition 5.19 (runtime fault).** A *runtime fault* is an evaluation that has no value: a division by zero, an overflow of `Integer` or `Decimal`, a canonicalizer that fails on its argument, or a row without a column that its operator requires (Def. 5.15). A fault is local to its row. The value is **absent** and the fault is counted by kind; a builtin applied to an absent argument is absent, except `Coalesce` and `IsPresent`; the rows around it are evaluated as usual. An emit whose id or endpoint is absent skips the row and counts it, as for an optional `IdOf` (Def. 5.9). An emit whose set value is absent does not set the property. A fault does not fail the projection event. The reason is Theorem 6.2(b): if a fault failed the event, the outcome would depend on which inputs share a step; a fault that is local to its row is a function of the row. A conflict (Def. 6.4) is the same kind of outcome at a sink. The bound of a `Fixpoint` and the depth of a `Traverse` truncate and do not fail (Ch. 6). The engine's counters are `DivisionByZero`, `Overflow`, `MissingRequired` and `SkippedEmits`.

**Theorem 5.2 (type soundness).** Let a projection be well-typed. Let every input row conform to its port's `RowType` (checked per record at ingestion, the record being rejected otherwise), let every input graph satisfy (a) and (b) for the closure that its port expects, and let premise KS hold. Then for every commit of the output graph:
- (a) *Types, ownership, kinds.* Every element has a non-abstract type of `Σ_out` (C2 and C5 of Def. 2.6). Every property it carries is declared for its type, has role GraphData and holds a value of the declared kind (C3). A telemetry value is written only to a telemetry property of the element's type, with the period of its row.
- (b) *Required properties, up to counted faults and reported conflicts.* If an element lacks a property that its type requires (C4), then a runtime fault (Def. 5.19) was counted on a row from which the value derives, or a conflict on that element was reported (Def. 6.4), in this graph or in an input graph. In a run whose fault and conflict counters are zero in the whole graph space, every committed element satisfies C2 to C5.
- (c) *Not consequences of typing.* The endpoint condition C6 and the constraints C7 (`key`, `cardinality`, `acyclic`, `enum`, `range`, `pattern`). They are properties of the committed graph, checked by a conformance check and not established by the type system.

**Premise KS (kernel signatures).** Every builtin function, canonicalizer and aggregate returns a value of the result kind of its signature or faults (Def. 5.19), and a telemetry read returns values of the declared value kind (Rule 7.1). KS is a property of the compiled kernel (Def. 5.3).

Status: **proved under the named premise KS**, restated. Read only, checked by no tool. The first form said that a commit conforms "or the projection event fails atomically"; that is false under Def. 6.4 and contradicts Theorem 6.2(b), see the counterexamples. The checker implements the rules, which mechanizes the judgment and not this proof. Wanted from the engine: test T-SOUND for KS and for (a) and (b) on generated projections ([inventory.md](inventory.md), obligation **O5.1**).

*Proof.* A row is *sound* for a row type `R` if every present field holds a value of a kind `≤` the kind of its field type, and every absent field typed `req` is absent after a counted fault or a reported conflict, in this graph or in an input graph. A reference of kind `Ent⟨T⟩` or `Rel⟨T⟩` is an element id; its type argument is used by E-Prop only, on a variable that a `Match` bound.

*Expressions.* If `R ⊢ e : (κ, π, ρ)` and the row is sound for `R`, the value of `e` is sound for `(κ, π)`. By induction on the derivation. E-Field: the hypothesis. E-Prop: the variable is bound to an element of the input graph whose type `T'` is `T` or a descendant, by the type test of the pattern, and `p ∈ props(T) ⊆ props(T')`; by (a) for the input graph a present value has the kind of `p`; by (b) for the input graph a required `p` is absent only after a counted fault or a reported conflict; an optional `p` is typed `opt`. E-Lit: a literal is a value of its kind. E-Call: the arguments are sound for `req` types by induction; if one is absent, a fault or conflict was counted and the result is absent (Def. 5.19); otherwise the result has the kind `θκ₀` or is a counted fault, by KS. E-Id: an absent component makes the id absent, and the presence of the rule is the join of the presences of the components, so an absent id is `opt` or follows a counted fault; otherwise `IdOf` resolves or mints an entity id (Def. 5.9).

*Operators.* Each operator maps sound rows to rows that are sound for its output type.
- `Match`: a binding gives each node variable an element whose type is the pattern type or a descendant, and each edge variable a relationship of the pattern type; the read columns are E-Prop.
- `Filter`: an output row is an input row. A row passes iff the predicate is present and true, so a top-level `IsPresent(f)` conjunct holds for it and typing `f` as `req` after the filter is sound.
- `Map`: each target is the value of its expression.
- `Join`: an output row is the union of a left row and a right row, whose field names are disjoint.
- `Union`: an output row is a row of one input. Kinds and roles are equal, and a field of the output is `opt` when it is `opt` in some input, so a row sound for its input type is sound for the output type.
- `Distinct`, `Retain`: an output row is an input row. `Retain` drops a row without its time, a counted fault.
- `Window`: adds `windowStart` and `windowEnd`, instants computed from the `ts` of the row.
- `Group`, `Rollup`: a key is the value of its expression; an aggregate has the result kind of its signature (KS).
- `Traverse`: adds a reached element, an integer depth, and read columns by E-Prop.
- `Series`: adds the two window bounds and one value per read property, of the declared kind (KS).
- `Delta`: a difference of two values of one kind (KS). `Threshold`: the target is one of the two text literals `high` and `low`.
- `Values`: rows of literals. `Fixpoint`: the unrolling is a composition of the operators above and a `Union` (Ch. 6). `Lift`: replaced by the operators above before typing (5.8).

By induction over a topological order of the operator graph (W4), every row that reaches a sink is sound for the sink's input type.

*Sinks.* A fragment of `EmitEntity` with type `T` has a present id (otherwise the row is skipped), the type `T`, which is in `Σ_out` and not abstract, and for each set `p := e` whose value is present an assignment with `p ∈ props_Σout(T)` and a value of a kind `≤ kind(p)`. A data value goes to a property of the graph only if the property has role GraphData; a telemetry value goes to a telemetry property with `W(R) = period(p)`, which is outside the graph (WF4). `EmitRelationship` is the same with endpoints.

*(a).* By Def. 6.4 a present element has a create fragment, and its create fragments agree on the type, or it is absent. So its type is the `T` of a create emit: C2, C5. Its property map merges the assignments of its fragments and drops a property whose values disagree, so every property it carries comes from an assignment: C3 and ownership.

*(b).* Every create fragment of a present element comes from a create emit of its type `T`, and such an emit sets every required property of `T` with an expression typed `req`. If the element lacks a required property `p`, then either the value of `p` was absent in a create fragment, and by soundness a fault or conflict was counted; or every create fragment had a value and the merge dropped `p`, which is a reported property conflict. With all counters zero neither occurs.

*(c).* Counterexample for C6 below. C7 quantifies over the graph and no rule of 5.2 consults a constraint. ∎

*Counterexamples to the first form.*
- *"Or the event fails atomically."* Let input `i₁` add a pod and input `i₂` carry a row that divides by zero. If a fault failed the event, the step `{i₁, i₂}` would commit nothing and the steps `{i₁}`, `{i₂}` would commit the pod. Theorem 6.2(b) requires both to end in the same state.
- *Required properties.* Two create sinks give `name` the values `"a"` and `"b"` for one key. By Def. 6.4 the element is present without `name`, the event does not fail, and C4 does not hold. Statement (b) says exactly when this can happen.
- *"Statically known endpoint types."* `relationship Runs (Pod) -> (Node)`, `Pod : Resource`, and an emit of `Runs` whose source is typed `Ent⟨Resource⟩`. The checker's test `compat(Resource, {Pod})` holds, because a declared source type is a descendant of the static type. At run time the source is an entity that another sink of the graph emits as `Disk`. C6 fails. The checker's test excludes patterns that can never conform and proves nothing more.

*Consequence for the commit rule.* A commit can carry an element that misses a required property after a counted fault or a reported conflict. The commit rule (Def. 3.9) therefore enforces the guards and WF1 to WF3; WF4 holds as far as (a) and (b) say.

**Theorem 5.3 (role non-interference).** In a well-typed projection, the graph data of every commit is a function of the `data`-role fields of the inputs and of the outputs of `Threshold` operators only. In particular two runs on inputs that differ only in telemetry values produce equal graphs unless a `Threshold` state changes, and a `Threshold` state is a function of the ordered closed windows of its key.
Status: the second sentence is **proved**. **The first sentence is false as stated, and its restatement is pending.** *Counterexample.* Read a series of each pod, keep only the pod from each row, remove duplicates, and set a data property on the pods that remain. Every typing rule accepts this: the kept field is data, and the emit sets a data property. The property is set exactly while the pod has a closed window in the look-back. So graph data depend on which windows exist, without a `Threshold`. *Two ways out, not yet decided.* Restate the theorem: graph data are a function of the data fields, of the set of closed windows that exist (their keys and times, not their values) and of the outputs of `Threshold` operators. Or add a typing rule under which a row that comes from a `Series`, a `Window` or a `Rollup` stays *windowed* through `Map` and `Filter` and may only enter a `Rollup` or a `Threshold`, so that the first sentence holds as written.

*Proof.* By P4 every structural position is typed `data`, and by E-Prop a telemetry property is read only through `Series`, whose outputs carry role `tele`. By the role join (Def. 5.5), a value computed from any telemetry operand is telemetry, so by P4 it can reach only telemetry properties of an emit, which are outside `G` (WF4, Ch. 3). The one rule that produces a `data` field from telemetry is `Threshold` (output `(Text, req, data)`). Its state transition depends only on the sequence of input values (Def. 7.10). ∎

This is the typed form of ADR-021 and ADR-023 and makes the obligation O7.2 of Ch. 7 a consequence of the type system: the only path from telemetry to structure runs through `Series`, `Rollup`, `Delta` and `Threshold`, whose inputs are closed windows (Def. 7.7).

**Proposition 5.4 (closed language).** No operator or builtin reads the wall clock, the network, the filesystem, a random source, or a mutable global, and the function set is fixed at engine release. The only time is logical time delivered by explicit inputs (Ch. 3, Def. 7.6).
Status: **proved** by inspection of the kernel (Def. 5.3). This is the premise of replay determinism (T7, Theorem 6.3).

**Theorem 5.5 (relationship identity is retype-stable).** For `rid` of Def. 5.9, `rid(o, κ_r, s, t)` does not depend on the relationship type, so a retype keeps the id. If the retyped relationship meets another one on the same `(source, target, type)`, Def. 6.4 decides: the one with the smaller id is present, the other is absent and reported, and `U` (Def. 3.3) holds after every committed event.
Status: **proved**, restated. The first form rested on retype policies (`reject`, `keepExisting`, `takeNew`) that Def. 6.4 superseded: one of them failed the event, which Def. 6.4 excludes, and two chose by which relationship was there first, which is arrival order.

*Proof.* The type is not an argument of `rid`. A retype edit has equal ids in its before and after states (Def. 3.6). When two relationships with positive support hold one `(source, target, type)`, Def. 6.4 keeps the one with the smaller id and leaves the other out, as a function of the fragments and of no order, so exactly one relationship holds the tuple and `U` holds. ∎

**Theorem 5.6 (lossless syntax).** Let `Can` be the set of *canonical* definition graphs: the graphs `parse(t)` of the texts `t` that parse without error. Under premise RT: (a) `parse` and `print` are mutually inverse bijections between the canonical texts `print(Can)` and `Can`; (b) `fmt := print ∘ parse` is idempotent, and `fmt(t) = t` exactly for the canonical texts; (c) two texts have one canonical form iff they parse to one graph.

**Premise RT (round trip).** `parse` and `print` are functions of their argument alone, and `parse(print(G)) = G` for every `G ∈ Can`.

Status: **proved under the named premise RT**, restated. RT is a property of two programs, the parser and the printer; it is tested on the example modules and wanted on generated graphs (test T-ROUNDTRIP, [inventory.md](inventory.md), obligation **O5.2**, experiments **Q1** and **Q8**). The first form claimed `parse(print(G)) = G` for every well-formed definition graph. That cannot be proved on the model and is not expected to hold: see the remark.

*Proof.* (a) By RT, `parse ∘ print` is the identity on `Can`, so `print` is injective on `Can`. For a canonical text `t' = print(G)` with `G ∈ Can`: `print(parse(t')) = print(parse(print(G))) = print(G) = t'`. So `print ∘ parse` is the identity on `print(Can)`. (b) `parse(t) ∈ Can`, so `fmt(fmt(t)) = print(parse(print(parse(t)))) = print(parse(t)) = fmt(t)`. If `fmt(t) = t` then `t = print(parse(t)) ∈ print(Can)`; the converse is (a). (c) If `fmt(t) = fmt(t')` then `print(parse(t)) = print(parse(t'))`, and `print` is injective on `Can`, so `parse(t) = parse(t')`. The converse is immediate. ∎

*Remark (why not every well-formed graph).* `parse` gives generated names to the nodes that the text does not name: the steps of `let x` are `x#0, x#1, …` and an unnamed sink of rule `r` is `<r>.<k>` (5.6). The kernel constraints ask only for unique sibling names (K3). A well-formed graph whose step nodes carry other names is therefore in `Can` only if the printer writes it in a form that reproduces those names, which is not known. A draft can also hold a graph that the printer refuses (`Unprintable`, Ch. 4, 4.6). A committed catalog module is canonical, given RT, because a commit prints every changed module and activates what the check of that text produced (Def. 4.13). Test T-ROUNDTRIP generates graphs inside and outside the naming scheme and reports which round-trip.

## 5.5 Telemetry operators and the crossing operator

**Definition 5.11 (crossing operator, realization of Def. 7.10).** The telemetry path of a projection is `Series → Rollup → Threshold`, with `Delta` to convert cumulative series first.
- `Series(read v.p₁…pₖ, within d)` reads, for each row, the **closed windows** of the telemetry properties of the element `v` in the input graph, for windows in `[frontier − d, frontier)`, where the frontier is the logical-time frontier of the projection. It is an append-only scan of closed windows (Def. 7.7); a revised window enters as a retraction followed by an insertion.
- `Rollup(by keys, combine aggregates)` aggregates over elements and windows with the declared roll-up function (additive functions roll up exactly, Theorem 7.2).
- `Delta` converts `cumulative` to `delta` per key, reset-aware (Def. 7.8).
- `Threshold` is the state machine of Def. 7.10: state `low` becomes `high` after `enterAfter` consecutive windows with value `≥ above`, `high` becomes `low` after `exitAfter` consecutive windows with value `≤ below`, with `above > below` and an `initial` state. It emits one row per key and changes only on state change. It is the only operator whose `data` output depends on telemetry.

Rules of class bijective, lens or sync contain none of these operators on a sink path (P2). Telemetry is forward only (Ch. 8).

**Fill, time-to-live and no data.** The semantics are Def. 7.14 to 7.16. The text:

```
|> series G x.p [fill c], x.q [fill c] within D [ttl T]
|> rollup by { ... } combine { ... } [nodata any]
|> threshold m by { ... } above A for N then "x" below B for M then "y" initial "z" [nodata then "n" | nodata ignore] as s
```

`fill c` gives a covered window without a record the constant `c`. `ttl T` is the time-to-live after which a subject without a record is in no data. A roll-up key is in no data when every member is, or with `nodata any` when one is. `nodata then "n"` is the value a `Threshold` gives such a key, and `nodata ignore` gives it no row. The checker refuses `fill` without `ttl`, `fill` on a gauge or a `cumulative` property, a `ttl` below one window width or above `within` less one window width, a `Threshold` over a series with `ttl` that has no `nodata` clause, and a `nodata` clause without a `ttl` upstream (Ch. 7, after Def. 7.16). The `lift` statement does not have these clauses yet.

## 5.6 Concrete syntax

A single text language, file extension `.sattle`: declarations in the style of PG-Schema, patterns in the style of Cypher and GQL, and dataflow as named `let` pipelines. Machine encoding stays canonical JSON of the definition facts (Ch. 1). The full EBNF is in [R03] section 7.3 and is not repeated. Constructs map one to one onto kernel nodes:

| Construct | Graph |
|---|---|
| `module m;` and `import m @ v sha256:h as a;` | `Module`, `imports{alias}` to a `Release` |
| `entity T : U { p: K; q?: K telemetry delta sum every 60s; }` | `EntityType`, `extends`, `PropertyType`, `hasProperty{required}` |
| `relationship R (A \| B) -> (C) {…}` | `RelationshipType`, `sourceType`, `targetType` |
| `identity d for T version "1" { c: K by f; }` | `IdentityDomain`, `KeyComponent` with canonicalizer |
| `rows N {…}` | `RowType`, `Field` |
| `projection P purpose [forward] output M {…}` | `Projection`, `outputOntology` |
| `in p: rows N;` and `in p: graph a;` | `Port`, `rowType` or `expects` |
| `let x = src \|> step \|> …;` | one operator per step, wires between their ports |
| `rule r class "reason" {…}` | `Rule`, `governs` each sink written in it |
| `emit`, `link` | `EmitEntity`, `EmitRelationship`, `Link` |
| `space S {…}` | `GraphSpace`, `Source`, `Graph`, `Binding`, `master` |

The following operators have this text, which the printer writes canonically (tests print and reparse each of them to the same digest):

| Construct | Notes |
|---|---|
| `let j = join a, b on <expr>;` | equalities between a left and a right expression, other conjuncts are a residual filter. Both inputs are data rows with distinct column names. Rejected: clashing columns, a condition with no equality |
| `let u = union a, b;` and `\|> distinct` | `Union` needs the same columns of the same types. `Distinct` is set semantics over signed rows |
| `\|> traverse g via Rel out\|in\|either depth 0 .. 8 from c as d` | adds the columns `d`, `d_depth` and the demanded read columns `d.prop`. Depth at most 16. Rejected: a bad depth, an unknown start |
| `let r = fixpoint seed via <pipeline that reads r> max 8;` | the step must produce the columns of the relation. `max` is the unrolling bound (Ch. 6) |
| `values { a: 1 }, { a: 2 }` | constant rows of one shape, emitted once when the graph first evaluates |
| `\|> retain on time for 24h` | `Retain` on the required data Instant column `time`, age a duration. Rejected: a field that is not a required data Instant, an age that is not positive, windowed input rows |
| `mean(x)`, `any(b)`, `all(b)`, `last(value, order)`, `weightedMean(value, weight)` | aggregates; `last` takes an ordering expression |
| `in n: param Integer = 0;` | a parameter port (Def. 5.14) |
| `state: Text roles { "Healthy": ok };` | value roles (Ch. 2, Def. 2.13) |

**Definition 5.14 (parameter).** A projection may declare an input port of kind `param` with a value kind `κ ∈ {Text, Integer, Decimal, Boolean}` and an optional default constant. In an expression a parameter is a constant of kind `κ`, role data, requirement `req`. A graph of the space that binds the projection gives a literal of kind `κ` for every parameter that has no default (`graph g = m.p(master: master, minReady: 4)`), and the projection is checked again with the values per binding. A parameter is definition data: a `Port` with kind `param` and a `Binding` with a `value`, so the editor creates and edits it with the same patches as any other definition fact. The same projection bound twice with different values is two graphs.

**Proposition 5.8 (parameters are constants of the binding).** The output of a bound graph is a function of its input graphs, its recorded rows, its definitions and its argument literals. A binding that omits a parameter without default, gives a literal of another kind, or names a port the projection does not have, is rejected at activation and changes nothing.
Status: **example tests** (`A_parameter_is_a_constant_of_the_space_binding`: one projection bound with `minScore` 5 and with 1 gives the groups `a` and `a,b`; `A_parameter_binding_must_give_every_parameter_a_constant_of_its_kind`: the three rejections with their messages). The first half follows from Theorem 6.3 with the literal as a definition input. **Why it is not closed:** Nothing is missing: it follows from Def. 5.14 (the binding gives a constant) and Theorem 6.3, and is listed as tested because no proof is written out ([inventory.md](inventory.md)).

**Definition 5.15 (retention).** `Retain(ts, age)` has a required data `Instant` column `ts` of its input (not windowed) and a positive `age` in nanoseconds (the text `retain on time for 24h`). Let `λ` be the logical-time frontier (the time up to which windows are closed, Def. 7.6 and Def. 7.11) and `time(r)` the value of `ts` in row `r`. A row `r` with weight `w > 0` passes unchanged if `time(r) + age > λ` at its recorded position, and is counted and dropped otherwise. A held row is withdrawn, that is emitted again with weight `−w`, when an advance moves `λ` to a value with `time(r) + age ≤ λ`. A retraction of a held row passes and releases it. A retraction of a row that has been withdrawn is absorbed. A row in which `ts` is absent breaks its row contract, because the column is required: it is dropped at the `Retain` and counted as a runtime fault. It neither passes nor is held, so it never has to expire. It is not passed, because a row without a time could never be withdrawn and the retained set would no longer be a function of the frontier (Proposition 5.9); and it does not fail the input, because one malformed row must not stop the rows around it (Ch. 6, Theorem 6.2(a)). Whatever is derived from a withdrawn row is retracted by the support counts of Ch. 6, so retention is not a second removal mechanism for elements.

**Proposition 5.9 (retention is a function of the frontier).** For every recorded input and every position `n` of it, the integrated output of `Retain` equals the integrated input restricted to the rows that have a time and are *live* at the frontier `λ_n` of that position: `I(out)[n] = I(in)[n] restricted to { r | time(r) + age > λ_n }`. The right side does not mention how the input is cut into steps.
Status: **proved** by induction over the positions of the input; read only, checked by no tool. Tested by examples and on two captures; wanted on generated inputs (test T-RETAIN, [inventory.md](inventory.md)). Theorem 6.1 uses this proposition for its `Retain` case and is not used here.

*Proof.* Write `live(λ) = { r | time(r) + age > λ }`. The frontier does not decrease (Theorem 3.5), so `live(λ') ⊆ live(λ)` for `λ ≤ λ'`: a row that is expired at one position is expired at every later one. Rows without a time are dropped at arrival with either sign of weight (Def. 5.15) and are left out of both sides. Let `H_n` be the weights that the operator holds after position `n`. Claim: `H_n = I(in)[n]` restricted to `live(λ_n)`, and `I(out)[n] = H_n`. For `n = 0` both are empty. A position is one of two kinds.
- *A row `r` with weight `w` arrives, the frontier stays.* If `r ∈ live(λ_n)`, the row passes with weight `w`, for an insertion and for a retraction alike, and is added to `H`; a retraction that precedes its insertion leaves a negative held weight, which the insertion cancels (Def. 6.4). Both sides of the claim gain `w` at `r`. If `r ∉ live(λ_n)`, nothing is emitted and `H` is unchanged: an insertion is expired on arrival, counted and dropped, and a retraction is absorbed. The right side is unchanged too, because it is restricted to live rows.
- *The frontier advances from `λ_n` to `λ_{n+1}`, no row arrives.* The operator emits `−H_n(r)` for every held `r` with `time(r) + age ≤ λ_{n+1}` and releases it. So `H_{n+1}` is `H_n` restricted to `live(λ_{n+1})`, which is `I(in)[n+1]` restricted to `live(λ_{n+1})` by the induction hypothesis and `live(λ_{n+1}) ⊆ live(λ_n)`; and `I(out)[n+1] = H_{n+1}`.
A step that carries several positions is their composition, so the claim holds at the end of every step whatever the cut. ∎

*Remarks.* (1) The revisions in between depend on the cut; the proposition is about the state at a position. (2) A row that an operator derives while an advance is processed is judged against the frontier before the advance (premise B2 of Theorem 6.2). Judging it after the advance gives the same integrated output, because a row that passes at `λ_n` and is expired at `λ_{n+1}` is withdrawn by the same advance; only the count of dropped rows differs, and B2 fixes that count. (3) Whatever is derived from a withdrawn row is retracted by the support counts of Ch. 6.

Tests: on the fixtures, with an age that expires the oldest 25, 50 and 90 percent of the evidence, the master keeps exactly the evidence whose time plus age is above the frontier; the state digest is equal at chunk sizes 1, 7, 50 and 100,000; on each capture present, with an age that expires the older half, the kept set is exactly the predicate and the digest is equal at chunk sizes 1, 50 and 100,000 (category `Capture`, full run).


Generated names: an unnamed sink in rule `r` is `<r>.<k>`; pipeline steps of `let x` are `x#0, x#1, …` with the last named `x`. Layout is canonical, `///` comments are kept as descriptions and `//` comments are dropped.

**Worked example.** A forward view that derives service dependencies from pod-to-pod calls and turns the drop ratio of closed 60-second windows into a `status` with hysteresis (adapted from [R03] section 8.3):

```text
projection depHealth view forward output acme.svc {
  in master: graph ops;
  let deps = match master (a: infra.Pod)-[c:infra.Calls]->(b: infra.Pod),
                          (a)-[:infra.Backs]->(s: infra.Service),
                          (b)-[:infra.Backs]->(t: infra.Service)
    |> where s != t;
  emit relationship DependsOn from deps key dependsOn: s -> t;
  let win = deps
    |> series master c.flows, c.drops within 24h
    |> rollup by { s: s, t: t } combine { flows: sum(flows), drops: sum(drops) };
  let ratio = win |> select { s: s, t: t, windowStart: windowStart,
                              r: if(flows > 0, drops / flows, 0.0) };
  let health = ratio |> threshold r by { s: s, t: t }
      above 0.05 for 3 then "degraded" below 0.02 for 3 then "ok" initial "ok" as status;
  emit update relationship DependsOn from health key dependsOn: s -> t set { status: status };
}
```

Reading with the judgments: `Match` types `a, b` as `Ent⟨Pod⟩`, `c` as `Rel⟨Calls⟩`; `where s != t` is `(Boolean, req, data)`; the first emit is a create with key `dependsOn`; `Series` reads two `delta` telemetry properties of period 60 s, so `W = 60 s`; `Rollup` with `sum` on `delta` is allowed and the output stays `tele⟨delta, 60 s⟩`; `drops / flows` is `tele⟨gauge⟩` by the role join, and `Threshold` accepts it and outputs `status : (Text, req, data)`; the second emit is an update of the `DependsOn` created above and sets a `data` property, which P4 allows because the value came from `Threshold`. A `where` on `flows` outside `Threshold` would violate P4. A `Rollup` input of a `cumulative` property would violate Def. 5.6. A projection with a rule of class `lens` containing this path would violate P2.

## 5.7 Refactoring

A projection is a graph (Def. 2.1), so a restructuring of projections is a patch on the catalog. Protocol version 3 offers three refactorings as pure functions on the canonical text of the projections of **one module**, each returning a draft patch and the new module text (Ch. 4, 4.6). Nothing is applied until the client applies the patch to a draft.

**Definition 5.12 (refactoring).**
- `extract(P, S, n)` moves the statements `S` of projection `P` into a new projection `n`, with its own output graph in the space (named after the parent graph and `n`) bound to the parent's arguments. `S` names top-level `let` chains, sinks (`emit.k`, `k` counting the emits of `P` in canonical order) and whole rules. Lets used only by `S` move with it. The ports `n` uses are copied. An update sink moves only together with the create sink of its type.
- `inline(n, P)` is the reverse. The graph of `n` is removed from the space.
- `moveRule(P, Q, r)` moves rule `r` and the lets only it uses between two existing projections.

**Definition 5.13 (output union).** For the state of a graph space, `Out` is the multiset of all element facts `(id, type, endpoints, properties)` and all telemetry windows `(element id, property id, window start, value)` of all graphs of the space, with the graph name forgotten.

**Proposition 5.7 (refactoring preserves outputs).** Let a refactoring of Def. 5.12 be accepted, its patch applied to a draft and the draft committed. Call the sinks it moves `M`, the other sinks of the giving projection `K_P`, and the sinks that the receiving projection had before `K_Q` (empty for `extract`). Under the premises R0 to R5, `Out` after the commit equals `Out` before it.
- **R0 (full replay).** The activation evaluates the changed projections on the whole recorded input (the engine's configuration edit, Ch. 9, 9.5), not on a bounded retained set.
- **R1 (closed selection).** No statement that stays reads a moved one, and no moved statement reads one that stays. The engine enforces it (`SharedOperator`, `DependsOnUnmoved`).
- **R2 (copied ports and parameters).** The receiving projection has the ports and parameters that the moved statements use, bound to the same graphs and literals. The engine copies them.
- **R3 (disjoint keys).** No element key, no uniqueness tuple `(source, target, type)` and no telemetry key receives a fragment from a sink of `M` and also from a sink of `K_P` or of `K_Q`. The engine enforces only the type-level part for updates (`UpdateWithoutCreate`) and **does not check R3**.
- **R4 (no reader of what moves).** No graph of the space reads, through a graph port, an element that a sink of `M` emits: not from the giving graph before the commit and not from the receiving graph after it. The engine does not check it: a reader of the parent graph is not rewired.
- **R5 (one output ontology).** The giving and the receiving projection have the same output ontology module. True for `extract` and `inline` by construction; for `moveRule` it is a premise.

Separately, `inline` after `extract` of the same selection restores the module text; that is a property of the two functions and is **tested on fixtures**, not proved.

Status: **proved under the named premises R0 to R5**, restated. Read only, checked by no tool. R1 and R2 are enforced, R5 holds by construction for two of the three refactorings, and R3 and R4 are premises on the definitions and their data that nothing checks. The first form had no premises and is false: counterexamples below. The engine reports `preserves: {outputs: "union"}` with an accepted plan, which is correct only under R3 and R4. Wanted: test T-REFACTOR with one negative case per premise ([inventory.md](inventory.md), obligation **O5.5**).

*Proof.* Let `F_s` be the Z-set of fragments of a sink `s` (Def. 6.3).
*Step 1: every sink keeps its fragments.* The fragments of a sink are a function of the rows at the in-ports of its operator subtree and of its parameters (Def. 6.3, Proposition 5.4). By R1 the subtree of a moved sink moves whole, and by R2 its ports are bound to the same graphs and its parameters to the same literals. By R4 no graph that a subtree reads changes its content, and graphs upstream of the two projections are untouched. By R0 and Theorem 6.3 the evaluation after the commit is the evaluation of the recorded input, so by induction along the graph space (S3) every sink `s` of the space has the same `F_s` at every position as before. Ids inside the fragments are unchanged: entity ids come from identity domains (Def. 1.9), and `rid` has the output ontology module as its only scope (Def. 5.9), which R5 keeps. Closed windows are computed inside the subtree, so the telemetry fragments are unchanged too.
*Step 2: materialization splits over disjoint keys.* Write `mat(F)` for the facts and telemetry windows that Def. 6.4 gives a fragment family. If two families `F`, `F'` share no element key, no uniqueness tuple and no telemetry key, then `mat(F + F') = mat(F) ⊎ mat(F')`: presence, type conflict and property conflict are decided per element key from the fragments of that key; a uniqueness conflict is decided per tuple from the relationships with that tuple; a telemetry value per telemetry key (premise B1 of Theorem 6.2).
*Step 3.* Before the commit the two graphs hold `mat(F_{K_P} + F_M)` and `mat(F_{K_Q})`; after it `mat(F_{K_P})` and `mat(F_{K_Q} + F_M)`. By R3 and step 2 both pairs have the union `mat(F_{K_P}) ⊎ mat(F_M) ⊎ mat(F_{K_Q})`. The other graphs are unchanged by step 1. `Out` forgets the graph name (Def. 5.13), so it is the same multiset. ∎

*Counterexamples to the first form* (each is accepted by the engine's checks).
- *Without R3, one key.* Two sinks of `P` emit the same service with the same properties, and one is extracted. Before, the graph holds the fact once. After, two graphs hold it, and `Out` holds it twice.
- *Without R3, a conflict.* Two sinks of `P` give `owner` the values `"a"` and `"b"` for one key. Before, the element has no `owner` (Def. 6.4). After the extraction each graph holds the element with its own value.
- *Without R3, a uniqueness tuple.* Two relationship sinks with different key rules emit `(s, t, Calls)`. Before, only the smaller `rid` is present. After, both graphs hold one.
- *Without R4.* A third graph reads `DependsOn` from the parent graph, and the sink that emits `DependsOn` is extracted. The third graph loses its input and its output changes.
- *Without R0.* With a bounded retained set (Def. 9.4), the new graph is built from the retained inputs only and lacks what older inputs produced (Theorem 9.2).
- *Without R5.* `moveRule` into a projection with another output ontology changes every `rid` of the moved relationship sinks.

**Rejections.** A rejected refactoring returns `422` with a code and the element, and changes nothing.

| Code | Case | Test |
|---|---|---|
| `SharedOperator` | a kept statement consumes a moved one | `Unsafe_extractions_are_rejected_with_a_typed_reason_and_change_nothing` |
| `DependsOnUnmoved` | a moved statement consumes one that stays | same |
| `UpdateWithoutCreate` | an update sink is selected without the create sink of its type | same |
| `UnknownSelection` | a selected name does not exist | same |
| `IngestionProjection` | an ingestion projection cannot be split (a second graph must derive from the master) | same |
| `SplitRule` | the selection is a statement inside a rule | `Selecting_a_statement_inside_a_rule_is_a_split_rule_and_an_unknown_projection_is_named` |
| `NotAProjection` | the named projection does not exist | same |
| `CrossModule` | the refactoring would touch two modules | **no test** |

**What the tests establish.** (a) On the fixture world, `extract` of `tree` and `extract` of `capabilities` and `counts` out of the `capabilityView` projection of the module `acme.biz` leave `Out` equal after commit, and `inline` restores both `Out` and the module text exactly. (b) `moveRule` of `tree` between the two stacked projections leaves `Out` equal. (c) Seven of the eight rejection codes are raised as typed errors, and five of them (`SharedOperator`, `DependsOnUnmoved`, `UpdateWithoutCreate`, `UnknownSelection`, `IngestionProjection`) also leave the draft without a patch. These are 9 test cases in total, on one module apart from the ingestion rejection. **What they do not establish:** other modules, in particular the ingestion projection, `serviceDeps` and the stacked health model; refactoring of generated projections (O5.5); preservation of lineage records, revision numbers and patch counts (the activation emits the new graph's content and the old graph's loss of elements as patches); the edit path of a lens rule after it has moved into a new graph (Ch. 8); a violation of R3, R4 or R5; `CrossModule`.

**Parameters and readers of the parent.** Since protocol 3.1 a refactoring copies the parameters that the moved statements use and binds them in the space (Def. 5.14). The guarantee stays the union of the output graphs: a graph that reads the parent graph is not rewired, so it sees only what the parent kept (`engine/PROTOCOL.md`, 3.1). A refactoring can therefore be accepted and still change what a downstream graph computes. That is premise R4 of Proposition 5.7, and no test of its violation is recorded.

**Why the relationship id changed.** A relationship id that contained the graph id, as iteration 0.1 had it, would change when `extract` creates a new graph, so `Out` would differ by construction. The engine hashes the output ontology module and the key rule and not the projection (`engine/README.md`, "Identity"), and Def. 5.9 has that scope (D6).

## 5.8 Levels of abstraction: a projection lifts the edges with the nodes

A projection that summarizes a graph is an abstraction of it. Several nodes of the graph below become one node of the level above, and the edges between the nodes below become edges between their images. A level that lifted only the nodes would be a list, not a graph: the service tree has dependencies because pods call pods, and a capability map has dependencies because services depend on services. The language has one statement for this, `lift`. This section fixes what it means, so that a level can be stated, tested and composed, and shows that the statement adds no semantics of its own: the planner lowers it to operators that the language already has.

**Definition 5.16 (level, image).** Let `B` be a graph, the graph below, with nodes `V_B` and a set `E_B` of directed edges of one relationship type; an edge `e` has a source `s(e)` and a target `t(e)`. A *level* over `B` is a graph `L` with nodes `V_L` and an *image*, a partial function `f : V_B ⇀ V_L`. A node outside `dom f` has no image. The image is data and not code: a relationship of some graph (a pod backs a service, a service realizes a capability) or a computed key (a service belongs to the group of its namespace). A computed key is a function by construction. A relationship is a function only if no node has two of them; where the data may hold two, the statement must say what happens to such a node (below, "The image must be a function").

**Definition 5.17 (lifted edges, fibre).** The lifted edge set of `E_B` under `f` is

```
E_L = { (f(s(e)), f(t(e))) | e ∈ E_B, s(e) ∈ dom f, t(e) ∈ dom f, f(s(e)) ≠ f(t(e)) }
```

For a lifted edge `(x, y)` its *fibre* is `E_B(x, y) = { e ∈ E_B | f(s(e)) = x, f(t(e)) = y }`, the edges below that it stands for. Three rules are consequences of the definition and not options. An edge whose two ends have one image would be a self-loop and is not lifted: traffic inside an image is not a dependency between images. Edges with the same pair of images are one edge. An edge with an end outside `dom f` is not lifted, and no catch-all node is invented for it. The lifted edge is a relationship keyed by the pair of images (Def. 5.9), so its id does not change while its fibre changes, and it is retracted when its fibre becomes empty (support counts, Ch. 9).

**Definition 5.18 (lifted telemetry).** Let `c` be a counter on the edges below (a `delta` series, Ch. 7) with closed windows `w`. The lifted counter is the sum over the fibre, window by window: `c_L((x, y), w) = Σ_{e ∈ E_B(x, y)} c(e, w)`. A ratio is not lifted: it is recomputed from lifted counters, `ρ_L = d_L / c_L` (and 0 where `c_L = 0`). A state is not lifted either: it is derived from the lifted ratio by the crossing operator with the same thresholds (5.5). The state of a lifted edge therefore describes the traffic the edge carries and is not the worst state of its parts. Tested with values in one case (below).

**Proposition 5.10 (lifting is a quotient).** Let `B_f` be `B` restricted to the nodes of `dom f`, without the edges whose two ends have one image. Then (a) `f` is a graph homomorphism from `B_f` onto `(f(dom f), E_L)`: every edge of `B_f` maps to a lifted edge and every lifted edge has a non-empty fibre; (b) `E_L` is the smallest edge set on `V_L` for which (a) holds; (c) the fibres of the lifted edges partition the edges of `B_f`.
Status: **proved**, directly from Def. 5.17, and tested on six examples (below). (a): an edge of `B_f` has both ends in `dom f` and distinct images, so its pair of images is in `E_L` by definition; a pair is in `E_L` only through some edge, which lies in its fibre. (b): any edge set that makes `f` a homomorphism must contain the image pair of every edge of `B_f`, which is `E_L`. (c): an edge of `B_f` has exactly one pair of images.

**Proposition 5.11 (conservation).** For a counter `c` and every window `w`: `Σ_{(x,y) ∈ E_L} c_L((x, y), w) = Σ_{e ∈ E(B_f)} c(e, w)`. What a level does not count is exactly the traffic inside one image and the traffic with an end that has no image; nothing is counted twice.
Status: **proved** from Proposition 5.10 (c) and Def. 5.18, and tested on examples: the lifted flows equal the flows below, also when a node has two images in the data, because that node is then outside `dom f`. It is false for a ratio that is averaged over the fibre, which is why Def. 5.18 recomputes ratios (the same reason as Def. 5.6 for `mean`).

**Proposition 5.12 (lifting composes).** Let `f : V_B ⇀ V_L` and `g : V_L ⇀ V_M`. Lifting `E_B` under `f` and the result under `g` gives the edge set that lifting `E_B` under `g ∘ f` gives. The fibre of an edge `(u, v)` of the upper level under `g ∘ f` is the disjoint union of the fibres `E_B(x, y)` over the edges `(x, y)` in its fibre under `g`, so lifted counters, the ratios recomputed from them and, for equal thresholds and window width, the states derived from them are the same on both routes.
Status: **proved** on the model, and tested in two cases against a one-step lift written for the test. An edge below is dropped on the first route iff `f` gives its ends one image or `g` gives the images one image, or an end leaves the domain on the way; that is iff `g ∘ f` gives its ends one image or is undefined on an end. Sums over a disjoint union are sums of sums. Consequence: a level may read the level below it and need not read the master again, which is how the capability level is written. Def. 5.18 has no look-back; written as `lift` statements the two routes agree for equal `within`, because a level holds the windows that its own look-back reads.

**The statement.** One `lift` per level, in a forward projection or a `readonly` rule:

```
lift NAME as REL from GRAPH EDGETYPE via GRAPH MAPPING
  [when e]  [carry p = q, ...]  [keep c = src.p, ...]  [derive p = e, ...]
  [state p of m above A for N then "x" below B for M then "y" initial "z"]
  [images reject|each]  [selfloops keep]  [within D];
```

| Part | Meaning in the terms of Def. 5.16 to 5.18 | Default |
|---|---|---|
| `from GRAPH EDGETYPE` | `E_B`: the relationship type of the lower graph that is lifted. `GRAPH` is a graph port of the projection | required |
| `via GRAPH MAPPING` | the image `f`: a relationship type whose target is the image. Nodes of the two graphs are matched by identity (the same entity id) | required |
| `as REL`, `NAME` | the lifted relationship type of the output ontology; one relationship per pair of images, keyed by `NAME` (Def. 5.9) | required |
| `when e` | restricts `dom f` to images that satisfy `e` (`image.status != "retired"`) | every image counts |
| `images reject` or `images each` | what happens to a node with several images: it is outside `dom f`, or it counts for each image | `reject`; see the checker rule below |
| `selfloops keep` | keeps the pairs with `f(s(e)) = f(t(e))`, which Def. 5.17 drops | dropped |
| `carry p = q` | the counter `q` of the edges below is combined over the fibre per window into `p`, with the combine rule that `p` declares (`sum`, `min`, `max`, `mean`) | telemetry properties that `REL` and `EDGETYPE` share by name and kind are carried |
| `derive p = e` | a property computed per window from the carried ones: the recomputed ratio of Def. 5.18, a rate | none |
| `state p of m above … below … initial …` | the state of Def. 5.18: a threshold with hysteresis over a carried or derived property `m` (5.5) | none |
| `keep c = src.p` (or `dst.p`) | a data property of the source or target image kept on the windows, so that a later `rollup` can group by it | none |
| `within D` | the look-back of the series the windows are read from | 24 h |

The statement lifts edges. The nodes of a level are emitted by ordinary `emit entity` statements, because a level may hold nodes that no edge reaches (a service that no pod backs). Its result is also a relation `NAME` with the columns `src`, `dst`, `windowStart` and the kept, carried and derived properties, which later statements read.

The two levels of the prototype (`engine/definitions/acme.svc.sattle` and `acme.biz.sattle`):

```
lift dependsOn as DependsOn from master infra.Calls via master infra.Backs
  keep cluster = src.cluster, namespace = src.namespace
  derive dropRatio = if(flows > 0, drops / flows, 0.0), flowRate = rate(flows)
  state status of dropRatio above 0.05 for 3 then "degraded" below 0.02 for 3 then "ok" initial "ok"
  images each;

lift dependsOn as DependsOn from tree svc.DependsOn via master ops.Realizes
  when image.status != "retired"
  carry callFlows = flows, callDrops = drops
  derive callDropRatio = if(callFlows > 0, callDrops / callFlows, 0.0)
  state callStatus of callDropRatio above 0.05 for 3 then "degraded" below 0.02 for 3 then "ok" initial "ok";
```

The first lifts pod calls to service dependencies, the second lifts service dependencies to capability dependencies and reads the first level, not the master (Proposition 5.12). The lifted properties of the second have their own names (`callFlows`, not `flows`), because a property name that two modules declare is served qualified and the names of the level below must not change. Written as ordinary statements the two levels took 46 lines; as `lift` statements they take 18 (the definitions lost 56 lines and gained 21 in the change, engine README).

**The image must be a function, or the statement must say otherwise.** Propositions 5.10 (c) and 5.11 need `f` to be a function: if a node below has two images, each pair of images gets an edge and the edge below is counted in each. The checker enforces a choice.
- *Declared.* If the ontology declares the mapping a function (`constraint cardinality Realizes per source 0 .. 1`), no `images` clause is needed. A conformance check reports data that breaks the constraint and an edit that adds a second image is rejected. Ingestion does not check it, so recorded data can still hold a node with two images; the default `images reject` then leaves that node out of `dom f`. The image the lift uses is a function by construction, and the propositions hold.
- *Stated.* If the mapping is not declared a function, the statement must carry an `images` clause, or the checker refuses it. `images reject` gives the same guarantee as above. `images each` gives up conservation on purpose: the service level says `images each` for `Backs`, so a pod that backs two services counts for both, each image gets the full count, and sums over the images can exceed the total below. Propositions 5.10 (c) and 5.11 hold for that level only on data in which every pod backs at most one service.

**Lowering.** `Lift` is an operator kind of the kernel (the twentieth, Ch. 2, Def. 2.2), so a `lift` statement is a node of the definition graph with the slots `when`, `carry`, `keep`, `derive` and `state`, the ports `edges`, `map` and `out`, and the edges `liftEdge`, `liftVia` and `ofType`. The catalog keeps the node, the printer prints it back with only the clauses that differ from the defaults, and `GET /palette` lists it (protocol 3.13). It has no evaluation rule of its own. When the projection is checked, the planner replaces the node, in a copy of the graph, by the operators it stands for:

| Step | Operators |
|---|---|
| edges below, and the mapping | `Match` of `EDGETYPE`; `Match` of `MAPPING`, with a `Filter` for `when` |
| `images reject` | `Group` that counts the images per node, `Join`, `Filter` on a count of one |
| both ends to their images | two `Join`s; a `Filter` that drops the self-loops |
| the lifted edge | `EmitRelationship`, row by row |
| lifted telemetry | `Series` over the carried properties within `within`, `Rollup` by the pair of images and the kept values, `Map` for the derived properties, `EmitRelationship` as an update |
| each `state` | `Threshold` and an update |

The generated operators are written as text and go through the ordinary parser, checker and lowering. Two consequences.
- *What follows from the lowering, with no new argument.* Everything that is stated for those operators holds for a lift, because after planning nothing else is there: the typing rules of 5.2, the incremental strategies and batch invariance (Theorems 6.1 and 6.2), replay determinism (Theorem 6.3), retention (Proposition 5.9), the crossing from telemetry to data through `Threshold` only (5.5), and lineage, which is complete because the lifted edge is a row-level emit (Ch. 9, 9.5). Each holds as strongly as it does for the operators, no more.
- *What does not follow and is tested.* That the lowering implements Def. 5.17 and 5.18 is not proved: it is a property of the planner's generated text. It is tested by equality with the statements it replaced (below) and by one test per clause, and wanted as a generated test that compares a lift with the two definitions directly (test T-LIFT, [inventory.md](inventory.md)). Propositions 5.10 to 5.12 are statements about the definitions and stay proved whatever the lowering does; they describe the engine exactly as far as the lowering is correct.

**What the checker refuses**, naming the statement: a mapping that is neither declared a function nor given an `images` policy; an edge type, mapping type or lifted type that is not in the ontology of its graph; a `carry` whose target or source is not a telemetry property, or whose kinds differ; a combine rule other than `sum`, `min`, `max` or `mean`; a `derive` target that is not telemetry; a `state` that names a measure as its target, or a source that is neither carried nor derived; a `keep` that is not `src.p` or `dst.p` of a data property of the image; a lift in a `lens` or `bijective` rule, because its edges cannot be edited back; and whatever the generated operators fail to check. A refactoring treats a lift like a `let` that reads only graph ports (5.7).

**In the canvas.** The editor has a `Lift` card built from the palette entry, and two walkthroughs rebuild the two levels with it: `lift-service-tree` (82 commands) and `lift-capabilities` (67 commands), both equal to the originals (from set A at the canvas of evaluation 11: a clean type check, 672 tests, the accessibility script passing, and the walkthroughs strict on the estate, 10 of 10; Ch. 12, 12.4). This paper did not run them.

**Three levels on the synthetic estate** (`paper/evidence/estate-head9.txt`, at evaluation 9; set A confirms the same state digest and the counts of the two levels at evaluation 10; names invented): the master graph has 47 entities and 48 relationships, among them pods with `Calls` edges; the service level `serviceTree` has 4 entities and 5 relationships, with `DependsOn` from `web` to `api` and from `api` to `db`; the capability level `capabilities` has 4 entities and 4 relationships, 2 `Under` (the authored hierarchy) and 2 `DependsOn` lifted from the service level (Storefront to Ordering, Ordering to Data platform). On the two real captures `capabilities` is empty: capabilities are authored data, and no source of those captures recorded any.

**Tests.** The engine suite at evaluation 10 was run in set A (639 of 639 with the Cosmos emulator, Ch. 14); this paper read the tests and did not run them.
- *The statement equals what it replaced* (36 cases, one in the capture category). The modules with `lift` give the same entities, relationships, properties, telemetry values and state digest as the hand-written modules, which are kept as a fixture, on the estate, on `lab/fixtures` and on the newest real capture. The state digest of the lifted graphs is the same at chunk sizes 1, 50 and 100,000. Set A confirms the digests of the newest capture and of the estate before and after the rewrite (`sha256:2a0b72c2…be93e`, `sha256:ad12a898…1c06`).
- *Each clause:* the lifted flows are exactly the flows of the edges whose ends have one image each and different ones (four variants); `images` chooses between leaving out a node with two images and counting it for each; `selfloops` drops or keeps the calls inside one image; `when` decides which images count; `within` sets the look-back; kept values ride on the windows. The printer gives back the text it parsed and names only what differs from the defaults. Sixteen refusals of the checker, each naming the statement and the reason.
- *The properties* (28 cases, written before the statement and unchanged by it): every graph of the space whose output ontology declares relationship types has relationships on the estate, with two exemptions that are checked and reasoned (`healthSignals`, `deploymentReport`); six variants of the estate for Def. 5.17 and 5.18; composition against a one-step lift in two cases (Proposition 5.12); the ratio and the state of a lifted edge with values; a service with two images.
- *Around it:* the palette entry, the skeleton, a draft edit and a preview of a lift; a rule with a lift moves between stacked projections, and a lift alone cannot be extracted away from its readers; a lifted edge names every edge of its fibre, both ends and both images, forward and reverse (Ch. 9).

**What is tested and what is not.** Tested on examples: Proposition 5.10 (a) and (c) and Proposition 5.11, Proposition 5.12 in two cases, the recomputed ratio and the derived state, every clause of the statement, and the equality of the lowering with hand-written statements on three inputs. Not tested: anything on generated graphs or images; composition over more than two levels; a pod that backs two services under `images each` on the service level itself (the clause is tested on the capability level).

## 5.9 Delta to iteration 0.1

- **Finding 4.** Relationship identity is a key rule name plus endpoints (Def. 5.9). `RetypeCollision` becomes `retype ∈ {reject, keepExisting, takeNew}` (Def. 5.10). `IdentityRule` as a free string is replaced by `IdOf` with a declared domain (gap 8).
- **Finding 13.** Field paths are `FieldRef` with `path` of the form `f` or `v.p`, typed by E-Prop.
- **Finding 14.** Port signatures are kernel data (Def. 5.2, K9). The unused `Port` record is the kernel `Port` node.
- **Findings 15 and 16.** One fixture per operator is required, including relationship emission. Recursion in `Fixpoint` goes through the internal ports `rec` and `next`, and transitive closure is the admitted fixture (Ch. 6).
- **Finding 17.** One binding list at graph-space level: `Binding` nodes (Def. 5.8). Row contracts are `RowType` nodes.
- **Finding 18.** There is no `Outputs` list. The sinks are the emits, and their facts are unioned into the single output graph under the uniqueness rule and P2 ownership.
- **Finding 19.** An operator with no consumer is a warning (W5). A sink unreachable from an input is an error.
- **Finding 20.** `Scaffold` is replaced by the `Values` operator feeding emits. Scaffold entities are keyed by identity expression, not raw ids.
- **Gap 5.** Direction is declared per rule (Ch. 8).
- **Entity id derivation.** [R03] section 5 derives ids by `e:` plus base32 of SHA-256 over the concatenation of tenant, domain id and length-prefixed components, truncated to 26 characters. Def. 1.9 hashes the JCS array, untruncated. Either is a pure injective-up-to-collision function (Theorem 1.4). A choice must be made once and fixed in the encoder specification. Truncation to 26 base32 characters (130 bits) raises the collision bound.
- **Relationship id scope.** [R03] hashes the projection id and finding 1 of [R01] proposed the graph id. Def. 5.9 hashes the module of the output ontology, as the engine does, so that a refactoring keeps ids (Proposition 5.7, D6).
- **Refactoring.** Extract, inline and move rule are new in protocol version 3 (5.7). The contracts have none.
- **Closed language.** Adding an operator, function or value kind remains an engine release (Ch. 15).

## 5.10 Deviations found by the prototype

Ids refer to Ch. 14 (14.8).

- **D5 (resolved).** A `Link` whose key is already bound to another identity of the same entity type merges the two identities; different types are a conflict and the link is ignored (Ch. 1, Theorem 1.7, Proposition 1.8).
- **D6 (resolved by changing the paper).** The engine hashes `[tenant, output ontology module, key rule, source id, target id]`. The paper now does the same (Def. 5.9). Confirmed by the project.
- **D11.** `Join`, `Traverse`, `Fixpoint`, `Values`, `Union`, `Distinct` and the aggregates `min`, `max`, `mean`, `weightedMean`, `any`, `all`, `last` are built. Their typing rules have negative example tests (clashing join columns, a join without an equality, a bad traverse depth, an unknown traverse start, a fixpoint step with other columns, `Values` rows of different shapes or with a non-constant, `Union` of unequal rows). `Delta` parses or checks to a typed error with a position and is `supported: false` in `GET /palette` with the reason: no property in the definitions or the captures is cumulative, so it would ship without data to test it on. `Union` has only the conflict policy `preserveEvidence`, because rows have no key; the other three policies are `supported: false` for the same reason. `Match` has no quantified paths. The fixpoint typing condition F5 (Ch. 6) is not checked: the engine bounds the unrolling instead (Ch. 6, D11).
- **D13.** `v.p` read columns are not carried past `Group`, `Rollup` or `Threshold`. This agrees with the typing rules (Group outputs keys and aggregates only).
- **D22.** The kernel gained `ValueRole` (Ch. 2, Def. 2.13), an optional `order` slot on `Aggregate`, a `target` property on `Traverse`, an optional `Fixpoint.relation` (inferred from the seed), ports of kind `param` with a `valueKind` and a constant `Binding.value`, and a palette that describes the declarations of a module (protocol 3.1 and 3.2). Each changed the kernel digest. The paper's Def. 2.2 and 5.3 did not have them. Where the new aggregates may be used over telemetry is D26.
- **D25.** The kernel gained the operator kind `Retain` ([R03] had 18 kinds, and the retention of evidence was only a question, O9.8). It has a `timestampField` and an `age`, ports `in` and `out`, and changed the kernel digest. It is a generic operator, not a property of an ontology: the three evidence rules of `acme.ops` read their rows through `|> retain on time for 24h`. The engine README records a design alternative that was not taken, a retention declared on the entity type of an ontology, because it would have removed elements by a second mechanism next to the support counts. Its `age` is a duration with a quantity and a unit in `/palette`, and the canvas editor takes `36h` (Ch. 12, 12.4).
- **D26.** The engine decides where an aggregate may be used and says so in `/palette`: `count` in `Group` and `Rollup`; `countDistinct`, `any`, `all`, `weightedMean`, `last` in `Group` over data only; `sum`, `min`, `max` in `Group` over numeric data and in `Rollup` over numeric telemetry that is not cumulative; `mean` in `Group` and in `Rollup` over a gauge only. Def. 5.6 differs in two places: it allows `weightedMean` and `last` over a `gauge` (the engine has them for data only, so a `Rollup` with a weighted mean is a typed error), and it allows `mean` over `delta` (the engine requires a gauge). The engine's rule is the stricter one and answers the open point of D22 about the temporality of the new aggregates. Tests: every aggregate in a `Group` over data is accepted exactly when the palette lists `Group`, and in a `Rollup` over a delta value exactly when it lists `Rollup` without a `gauge` requirement. A test runs the whole matrix of `sum`, `min`, `max` and `mean` over a delta, a gauge and a cumulative value through the checker and compares each outcome with what the palette declares (a cumulative value is refused by every one, with a message that says to apply `Delta` first; `mean` over a delta is refused with "use sum" and accepted over a gauge; `count` has no requirement). These tests are at the checker: a `Rollup` over a gauge or a cumulative value is accepted or refused, and its values were not evaluated on data in a test.
- **D32 (resolved).** A level was written as about twenty ordinary statements and nothing required the image to be a function. The `lift` statement replaces them (5.8): it is an operator kind of the kernel that the planner lowers to the same operators, and the checker requires the mapping to be declared a function or the statement to carry an `images` policy. Cardinality is still not checked at ingest (O5.8).
- **Syntax.** The text syntax is the grammar of [R03] with the optional module version (D2). Errors print as `file:line:col: error Code: message`.

## 5.11 Obligations

| Id | Statement | Discharged by |
|---|---|---|
| O5.1 | Premise KS of Theorem 5.2, and (a) and (b) on generated projections | test T-SOUND: property tests on the checker and a reference evaluator |
| O5.2 | Premise RT of Theorem 5.6, and which well-formed graphs are canonical | test T-ROUNDTRIP, Q1, Q8 |
| O5.3 | Every operator has a fixture and a negative fixture per judgment | conformance corpus (Ch. 14) |
| O5.4 | Compatibility between `Series` over another graph's telemetry and replay | closed-window records in the input frontier (Ch. 6, Def. 6.8) |
| O5.5 | Proposition 5.7 on generated projections; R3, R4 and (for `moveRule`) R5 checked by the engine or reported as not preserved | test T-REFACTOR: extract a random selection, commit, compare `Out`; one negative case per premise |
| O5.6 | Relationship id scope (Def. 5.9) confirmed by the project. Open: whether `o` is the module name or its digest | identity corpus of Ch. 1 |
| O5.7 | Def. 5.6 replaced by the per-context rule of D26 (or the engine extended to `weightedMean` and `last` over a gauge), and F5 as a checker rule | checker tests with `mean` over each temporality (done) and an arithmetic fixpoint body; values of a `Rollup` over a gauge evaluated on data |
| O5.8 | Levels (5.8). Discharged: the `lift` statement with a printer round trip; the checker rule that the mapping is a function or the statement has an `images` policy; `Realizes` declared `0 .. 1`; a stated policy for `Backs` (`images each`). Open: the cardinality checked at ingest; Propositions 5.10 to 5.12 and the lowering on generated graphs and images; composition over more than two levels | a cardinality check on rows as they are ingested; property tests with random graphs and images that compare a lift with Def. 5.17 and 5.18 directly |
