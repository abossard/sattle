# Ch. 5 Projection language

**Question.** How is a graph computed from other graphs and from the records of sources, and what can be checked about such a computation before it runs?

**What you can state at the end.** A *projection* is a definition: a typed network of operators taken from a fixed list of twenty kinds. It is checked when it is loaded. In a projection that passes the check, the value of a measurement can reach the structure of a graph only through one operator, the threshold; whether a measurement exists can reach it through the support of a windowed relation, and that channel is named and bounded. A projection that summarizes a graph is a level of abstraction over it: it maps nodes to nodes and lifts the edges with them, and lifting keeps totals and composes. One statement, `lift`, writes such a level.

This chapter uses identity (Ch. 1); types, the kernel, the catalog, modules, releases and ontologies (Ch. 2, Def. 2.1, 2.4 and 2.5); the graph space (Ch. 3) and patches (Ch. 4). As in Ch. 0 (Def. 0.7), a *measurement* is one value and *telemetry* the series of measurements attached to an element; a *telemetry property* holds such a series, and the typing rules write its role `tele`. What a projection means as a function, and how it is computed step by step, is Ch. 6. Which rules may be edited backwards is Ch. 8.

**Running example.** The master graph of the shop holds pods, services, and two relationship types: `Backs`, from a pod to the service it serves, and `Calls`, from a pod to a pod it called, with the number of calls per minute as a measurement `flows`. A projection computes the *service graph*: one node per service, and an edge `DependsOn` from service `x` to service `y` when a pod of `x` calls a pod of `y`.

## 5.1 Structure of a projection

A projection is a graph in the catalog (Ch. 2, Def. 2.1). Its nodes are ports, operators and rules. Its output is a derived graph (Ch. 3, Def. 3.11).

**Definition 5.1 (projection).** A projection has a *purpose*: `ingestion` (it builds the master graph from rows), `view`, `migration` or `verification`. It has a number `retain` of pieces of evidence kept per element (Ch. 9), an *output ontology* (the module whose types it writes), ports, operators and rules. A projection without rules is *forward*: its output can only be computed, never edited (Ch. 8). Operators and the wires between them form a directed acyclic graph, except inside the body of a `Fixpoint`. A projection may carry a *description*, a text written before it whose first sentence stands alone as a caption of the graph it produces; like value roles, kinds and labels (Ch. 2) it is for people and is not read by evaluation.

**Definition 5.2 (port, wire, port signature).** A *port* has a name, a *mode* `in` or `out`, and a *kind* `rows` or `graph`. A rows port reads records of a declared shape, a `RowType` (Ch. 2). A graph port reads a graph and names the module of types it expects. A *wire* joins a producing port (an `in` port of the projection, an `out` port of an operator, or the internal `rec` port of a `Fixpoint`) to a consuming port of the same kind. Each operator kind has a fixed **port signature** in the kernel:

| Operator | In ports | Out ports | Kind of computation (Ch. 6) |
|---|---|---|---|
| `Match` | `graph` (graph) | `out` | bilinear for edges |
| `Filter`, `Map`, `Distinct`, `Window` | `in` | `out` | linear (`Distinct` is not) |
| `Retain` | `in` | `out` | linear on arrival, withdrawal by the frontier (Def. 5.15) |
| `Join` | `left`, `right` | `out` | bilinear |
| `Union` | `in0 … in(n-1)`, `n ≥ 2` | `out` | linear |
| `Group`, `Rollup` | `in` | `out` | per key |
| `Traverse` | `graph` (graph), `in` | `out` | bilinear, bounded |
| `Fixpoint` | `seed`; internal `next` | `out`; internal `rec` | recursive |
| `Values` | none | `out` | constant |
| `Series` | `graph` (graph), `in` | `out` | reads measurements |
| `Delta`, `Threshold` | `in` | `out` | with state per key |
| `EmitEntity`, `EmitRelationship`, `Link` | `in` | none: they write the output | linear |

`Lift` (5.8) is not in the table, because it is replaced by operators of the table before it runs.

*Example:* the service graph in operators. A `Match` reads the master graph and returns one row per pair (pod `p`, service `s`) with `p Backs s`. An `EmitEntity` writes one `ServiceNode` per service `s`. A second `Match` returns one row per call `p₁ Calls p₂`, two `Join`s with the first rows add the services of `p₁` and `p₂`, a `Filter` drops calls inside one service, and an `EmitRelationship` writes `DependsOn` between the two services.

**Definition 5.3 (the operators and their parameters).** The operator kinds are the closed set of Ch. 2 (Def. 2.2). Their parameters are:
- `Union.conflict ∈ {requireEqual, preferFirst, preferLast, preserveEvidence}`;
- `Traverse.direction ∈ {out, in, either}`, `minDepth`, `maxDepth ≤ 16`;
- `Window.timestampField`, `width`, and what to do with a late row, `late ∈ {ignore, revise, reject}`;
- `Fixpoint.maxFacts`, `maxIterations`;
- `EmitEntity.mode` and `EmitRelationship.mode ∈ {create, update}`, and `EmitRelationship.key`, the rule that names the relationship (Def. 5.9);
- `Series.within`, the look-back;
- `Delta.target`;
- for `Threshold`: `above`, `enterAfter`, `high`, `below`, `exitAfter`, `low`, `initial`.

Inside operators, *expressions* compute values: a field reference, a literal, a call of a builtin function, or `IdOf` (Def. 5.9). The builtin functions are fixed: comparisons, `And`, `Or`, `Not`, `Add`, `Subtract`, `Multiply`, `Divide`, `Negate`, `ToDecimal`, `Concat`, `Coalesce`, `IsPresent`, `If`, `Rate`, `InList`, `LowerInvariant`, `Trim`. There are no functions written by users, no reading of the program's own structure, and no access to anything outside the inputs.

## 5.2 Types

Every value that flows between operators has a type, and every operator has a rule that says which input types it accepts and which output type it gives. A projection is accepted only if every operator satisfies its rule.

**Definition 5.4 (kinds and field types).** A *kind* is one of `Text`, `Integer⟨u⟩`, `Decimal⟨u⟩`, `Boolean`, `Instant`, `Ent⟨T⟩` (an entity of type `T`), `Rel⟨R⟩` (a relationship of type `R`), `List⟨κ⟩` or `Null`. Here `u` is a unit (`1` when there is none), and `⊤` stands for any entity type. A *field type* is a triple `φ = (κ, π, ρ)`:
- the kind `κ`;
- the *presence* `π`: `req` (always present) or `opt` (possibly absent), ordered `req ⊑ opt`;
- the *role* `ρ`: `data`, or `tele⟨τ, P⟩` for a measurement with time behavior `τ ∈ {gauge, delta, cumulative}` (Ch. 2, Def. 2.11) and period `P`, ordered `data ⊑ tele⟨τ, P⟩`. A `gauge` is a reading at a moment (a temperature), a `delta` an amount per period (calls in one minute), and a `cumulative` a running total since some start (calls since the program started).

A *row type* `R` is a finite map from field names to field types. A row type has a window width `W(R)` when it has a field `windowStart` produced by `Window`, `Series` or `Rollup`. *Subkinding* `κ₁ ≤ κ₂` is reflexive; `Ent⟨T₁⟩ ≤ Ent⟨T₂⟩` iff `T₁` is a subtype of `T₂`; `List` is covariant; there is no widening between numbers.

*Example:* the rows of the first `Match` of the service graph have the type `{p: (Ent⟨Pod⟩, req, data), s: (Ent⟨Service⟩, req, data)}`. A row of `Series` over `flows` has, among its fields, `flows: (Integer⟨1⟩, req, tele⟨delta, 60 s⟩)`.

**Definition 5.5 (roles of computed values).** When an expression combines values: `data ⊔ ρ = ρ` (a computation with a measurement is a measurement); two measurement operands need the same period; and the time behavior follows Def. 5.6. For example `delta + delta = delta`, `delta ÷ delta = gauge`, `delta × data = delta`, and any arithmetic on a `cumulative` value is an error.

**Definition 5.6 (time behavior of aggregates).** For aggregates over measurement fields: `sum`, `min` and `max` keep `gauge` and `delta` and refuse `cumulative` (apply `Delta` first); `mean` gives `gauge`; `weightedMean` and `last` are allowed on `gauge` only; `count` gives `gauge`. `Rate` turns a `delta` of unit `u` and period `P` into a `gauge` of unit `u/s`. `Delta` turns `cumulative` into `delta`, starting again when the value drops. `Threshold` accepts `gauge` or `delta`. A property declared with `combine = mean` may be rolled up again only through `weightedMean` with an explicit weight. The checker requires that the combine rule of a written measurement property equals the aggregate that produced its value.

*Example:* the calls per minute of two pods of `api` towards `db` are rolled up by `sum` into the calls of `api` towards `db`; both are `delta` with a period of one minute.

### Typing rules

`R ⊢ e : φ` reads "in rows of type `R`, the expression `e` has field type `φ`". `Γ ⊢ o : R̄in ⇒ Rout` reads "operator `o` takes inputs of types `R̄in` and gives outputs of type `Rout`". `Σ_p` is the closure of types of graph port `p` (Ch. 2, Def. 2.4), `Σ_out` that of the output.

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

`Traverse` and `Values` have rules of the same form, not printed here. Two rules are stated exactly as the prototype's checker has them, and Theorems 2.5 and 5.2 use both: `Union` compares kinds and roles and joins presence (it does not require equal presence), and an emit refuses a possibly absent value for a required property. The rules of the four operators that read measurements, `Series`, `Rollup`, `Delta` and `Threshold`, are what Ch. 7 builds on (5.5).

*Example:* in the service graph, the `Filter` that drops calls inside one service compares two `Ent⟨Service⟩` fields, so its predicate has role `data` and the rule (Filter) is satisfied. A filter "keep calls with more than 100 flows" written over the raw `flows` property would not pass E-Prop, because a measurement is read only through `Series`.

**Definition 5.7 (wires and projection checks).**
- W1 every consuming port has exactly one incoming wire; W2 a wire joins ports of the same kind; W3 the type of the consuming port is the type of the producing port; W4 outside `Fixpoint` bodies the operators form no cycle; W5 every operator that writes output can be reached from an `in` port or a `Values` operator, and an unused `out` port is a warning.
- P1 every type written is in `Σ_out`.
- P2 *ownership*: if a rule of an editable class (bijective, lens or sync, Ch. 8) sets a property `p` of a type `T` or writes a relationship type `Rt`, no other rule of the projection sets `(T, p)` or writes `Rt`; and such rules set no measurement property and contain no measurement operator on the paths to what they write.
- P3 each rule satisfies the direction check of Ch. 8.
- P4 *roles do not mix*: every structural position (filter predicates, join keys, group and roll-up keys, the rows of `Distinct`, identity components, the ids and ends of what is written) has role `data`, and measurement values reach only measurement properties, except through `Threshold`.
- P5 the edit rules of an editable rule cover every required property of the type it may create (Ch. 8).

**Definition 5.8 (checks of a graph space).** When a space is made active, that is put into use so that the engine evaluates it: S1 there is exactly one master graph, written by a projection with purpose `ingestion`; S2 every port of every projection is bound exactly once, a rows port to a `Source` that provides the same `RowType`, a graph port to a graph whose producer writes the module that the port expects; S3 the graphs and the projections that produce them form an acyclic network, and every graph can be reached from the master (Ch. 3, Def. 3.11); S4 the closure of the space holds one release of each module, and the release of the kernel is the program's own. The reader of every `Source` is known to the program and delivers rows of the declared shape.

## 5.3 Names of written elements

**Definition 5.9 (identity expression, relationship key).** The `id` of a written entity is either a matched entity, which keeps its timeless id (Ch. 1), or `IdOf(d, c̄)`, typed by E-Id. `IdOf` resolves the key `(t, d, c̄)` through the binding set (Ch. 1, Def. 1.10) or computes `alloc(ik)` (Ch. 1, Def. 1.9). The output of an aggregate is a new entity in a domain whose components are the group keys; it never takes over the identity of a member. Membership in a group is an ordinary relationship of the output. If a component is possibly absent, the `IdOf` is possibly absent, and the emit skips the row.

A relationship emit names a **key rule** `κ_r`. The relationship id is `rid(o, κ_r, s, t) = base32(H(canonical form of [customer, o, κ_r, s, t]))`, where `o` is the module of the projection's output ontology. **Neither the type of the relationship nor the projection or graph that writes it is an input.** So a retype keeps the id, and moving an emit to another projection or graph with the same output ontology keeps it too (Proposition 5.7). Two graphs of one ontology that hold the same key rule and ends hold the same id, which does no harm because the uniqueness rule `U` (Ch. 3, Def. 3.3) is per graph.

*Example:* `DependsOn` from `web` to `api` with the key rule `dependsOn` has the id `rid(svc, dependsOn, e:web, e:api)`. It is the same id whether the service graph or a second graph of the same ontology writes it.

That two keys of two domains denote one entity is evidence, not an automatic union. `Link` writes an `Evidence` node with edges `left` and `right` to two keys, under an `EquivalencePolicy` between two domains. In mode `assert`, with a confidence at least the policy's minimum confidence, the evidence is accepted at once and an edge `sameAs` is written; in mode `review` it stays proposed until a person decides. An accepted `sameAs` between keys bound to different identities is a merge in the sense of Ch. 1 (Theorem 1.7). A split happens only by an explicit decision. The confidence is a decimal in `[0, 1]` that is compared with the minimum confidence and means nothing else.

*Example:* Kubernetes names a pod by its *uid*, a unique id that Kubernetes gives each object; the network observer names it by namespace and pod name. A `Link` from the Kubernetes record, which carries both, merges the two ids of that pod with confidence 1. A merge is permanent, so the key must not be reused for another pod. A pod name can be reused when a pod is recreated, which Ch. 10 (10.4) states as a limit; an address alone is reused so often that Ch. 10 keys it with a time-bounded lease and never links it this way.

**Definition 5.10 (collision policy).** When retyping `r` from type `A` to `B` would give the triple `(s, t, B)` that another relationship `r'` already holds, nothing is to be chosen: Def. 6.4 decides, the relationship with the smaller id is present, and the other is absent and reported. For `Union.conflict`: `requireEqual`, `preferFirst` and `preferLast` are defined only for rows with a key. Under `requireEqual` two different values for one key give no value and a reported conflict; as in Def. 5.19 and 6.4, nothing fails. `preferFirst` and `preferLast` choose by the order of the ports. `preserveEvidence` keeps every input row, the sum of the inputs, so a conflict stays visible as evidence. A `Union` of rows without a key has no other policy. Inside the body of a `Fixpoint` (Theorem 6.5) the rows have no key, and `Union(requireEqual)` there is set union. Only `preserveEvidence` is built (5.9).

## 5.4 Theorems

Six statements about the language follow from its definition. The first says that checking always ends. The second says what a projection that passes the check guarantees about its output. The third says how far measurements are kept away from structure. The other three concern the language itself.

**Theorem 5.1 (checking is decidable).** Checking a release of a module when it is loaded (conformance to the kernel, the rules K1 to K11, the well-formedness of its types, and all the checks of 5.2) is decidable, and each check takes a bounded number of lookups per definition element. Whether a `Fixpoint` is admissible is a check of the kinds of its nodes.

*Proof.* The rules for expressions and operators are syntax-directed: each has conditions on the parts of the expression or on the inputs of the operator, and on finitely many lookups in the types. The types of a port are fixed by the port signature, so one pass over the acyclic network of operators, in topological order, computes all row types. K1 to K11 are finite conditions on the graph. The conditions on a `Fixpoint` (which operators its body may use, no `IdOf`, a finite port) are conditions on the kinds of nodes. ∎

**Definition 5.19 (runtime fault).** A *runtime fault* is an evaluation that has no value: a division by zero, an overflow of `Integer` or `Decimal`, a canonicalizer that fails on its argument, or a row without a column that its operator requires (Def. 5.15). A fault is local to its row. The value is **absent** and the fault is counted by kind; a builtin applied to an absent argument is absent, except `Coalesce` and `IsPresent`; the rows around it are evaluated as usual. An emit whose id or endpoint is absent skips the row and counts it, as for an optional `IdOf` (Def. 5.9). An emit whose set value is absent does not set the property. A fault does not fail the projection event. The reason is Theorem 6.2(b): if a fault failed the event, the outcome would depend on which inputs share a step; a fault that is local to its row is a function of the row. A conflict (Def. 6.4) is the same kind of outcome at a sink. The bound of a `Fixpoint` and the depth of a `Traverse` cut the result short and do not fail (Ch. 6).

*Example:* a ratio `drops / flows` in a window where `flows` is 0. That row's ratio is absent and one division by zero is counted; the other rows of the window are computed as usual. (The shop's definitions write `if(flows > 0, drops / flows, 0.0)` and avoid the fault.)

**Theorem 5.2 (type soundness).** Let a projection be well-typed. Let every input row conform to its port's `RowType` (checked per record at ingestion, the record being rejected otherwise), let every input graph satisfy (a) and (b) for the closure that its port expects, and let premise KS hold. Then for every commit of the output graph:
- (a) *Types, ownership, kinds.* Every element has a non-abstract type of `Σ_out` (C2 and C5 of Def. 2.6). Every property it carries is declared for its type, has role GraphData and holds a value of the declared kind (C3). A telemetry value is written only to a telemetry property of the element's type, with the period of its row.
- (b) *Required properties, up to counted faults and reported conflicts.* If an element lacks a property that its type requires (C4), then a runtime fault (Def. 5.19) was counted on a row from which the value derives, or a conflict on that element was reported (Def. 6.4), in this graph or in an input graph. In a run whose fault and conflict counters are zero in the whole graph space, every committed element satisfies C2 to C5.
- (c) *Not consequences of typing.* The endpoint condition C6 and the constraints C7 (`key`, `cardinality`, `acyclic`, `enum`, `range`, `pattern`). They are properties of the committed graph, checked by a conformance check and not established by the type system.

**Premise KS (signatures of the fixed functions).** Every builtin function, canonicalizer of an identity component and aggregate returns a value of the result kind of its signature, or faults (Def. 5.19), and a read of a measurement returns values of the declared kind (Ch. 7, Rule 7.1). KS is a property of the program's fixed functions (Def. 5.3).

The theorem is proved below under KS. KS is a property of the program, not of the model. The prototype's checker applies the typing rules, which mechanizes the check and not this proof.

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

*Why the theorem is not stronger.*
- *A fault cannot fail the whole change.* Let input `i₁` add a pod and input `i₂` carry a row that divides by zero. If a fault failed the event, the step `{i₁, i₂}` would commit nothing and the steps `{i₁}`, `{i₂}` would commit the pod. Theorem 6.2(b) requires both to end in the same state.
- *Required properties can be missing.* Two create sinks give `name` the values `"a"` and `"b"` for one key. By Def. 6.4 the element is present without `name`, the event does not fail, and C4 does not hold. Statement (b) says exactly when this can happen.
- *The types of the ends are not known in advance.* `relationship Runs (Pod) -> (Node)`, `Pod : Resource`, and an emit of `Runs` whose source is typed `Ent⟨Resource⟩`. The checker's test `compat(Resource, {Pod})` holds, because a declared source type is a descendant of the static type. At run time the source is an entity that another sink of the graph emits as `Disk`. C6 fails. The checker's test excludes patterns that can never conform and proves nothing more.

*Consequence for the commit rule.* A commit can carry an element that misses a required property after a counted fault or a reported conflict. The commit rule (Def. 3.9) therefore enforces the guards and WF1 to WF3; WF4 holds as far as (a) and (b) say.

**Definition 5.20 (windowed relation, support).** A *windowed relation* is the output of a `Series`, a `Window` or a `Rollup`, and of every `Map`, `Filter`, `Join` or `Distinct` that a row of it reaches. Its *data part* is the set of its rows with the fields of role `tele` dropped: the key fields, the window's start and end, and any other data field. The *support* of the relation at a frontier is its data part there. It says which keys have a closed window within the look-back, and when; it says nothing of the values measured. The *key support* is the support with the window times dropped as well: the set of keys that have at least one closed window within the look-back.

*Example:* a `Series` of the flows of each pod over the last hour. Its support is the set of pairs (pod, window) for which a closed window exists; its key support is the set of pods that sent at least one flow in that hour.

**Theorem 5.3 (role non-interference).** In a well-typed projection, the graph data of every commit is a function of (i) the `data`-role fields of the inputs, (ii) the outputs of `Threshold` operators, and (iii) the supports of the windowed relations of the projection. The values of measurements reach graph data through (ii) only, and the supports do not depend on them. In particular two runs on inputs that differ only in telemetry values produce equal graphs unless a `Threshold` state changes, and a `Threshold` state is a function of the ordered closed windows of its key.

*Proof.* By P4 every structural position (filter predicates, join and group keys, the rows of `Distinct`, identity components, the ids and ends of what is written) is typed `data`, and by E-Prop a telemetry property is read only through `Series`, whose outputs carry role `tele` for the values read. By the role join (Def. 5.5), a value computed from any telemetry operand is telemetry, so by P4 it can reach only telemetry properties of an emit, which are outside `G` (WF4, Ch. 3), or a `Threshold`. The one rule that produces a `data` field from telemetry is `Threshold` (output `(Text, req, data)`), and its state transition depends only on the sequence of input values (Def. 7.10). What else a windowed row brings to a data position is its data part, and whether the row exists at all; both are the support (iii). The supports do not depend on telemetry values: whether a closed window exists depends on which samples arrived, not on their values, and every operator that could drop a row by a value (a `Filter`, a `Join`, a group) compares only `data` fields (P4). For the second sentence: with equal data inputs and equal supports, (i) and (iii) are equal; a `Threshold` gives a key a row exactly while the key has a window in the look-back (Def. 6.7), which is part of (iii), so its outputs are equal unless a state changes. ∎

*Why (iii) is needed.* Read a series of each pod, keep only the pod from each row, remove duplicates, and set a data property on the pods that remain. Every typing rule accepts this: the kept field is data, and the emit sets a data property. The property is set exactly while the pod has a closed window in the look-back, so graph data depend on the key support, without a `Threshold`. This channel is used on purpose: the shipped definitions create `Calls`, `Sends`, `Destination` and `Meter` in the master graph from windows of flows and readings, and the workload model creates `TalksTo` and the dependencies it finds from calls and flows the same way (Ch. 7, 7.4).

*A stricter profile.* Rule P6, not adopted, would keep a row that comes from a `Series`, a `Window` or a `Rollup` windowed through `Map` and `Filter` and let it enter only a `Rollup` or a `Threshold`. Under P6 the supports reach graph data only through the rows of `Threshold` outputs, and an earlier, shorter form of the theorem holds: graph data are a function of the data fields and of the outputs of `Threshold` operators only. P6 would refuse six rules of the shipped definitions, among them the edges discovered from flows, so it is an optional profile, not part of the language.


**Proposition 5.4 (closed language).** No operator or builtin function reads the clock on the wall, the network, files, a source of randomness, or a shared value that changes, and the set of functions is fixed with the program. The only time is logical time, delivered by explicit inputs (Ch. 3, 3.4; Ch. 7, Def. 7.6).

*Proof.* By inspection of the list of operators and functions (Def. 5.3): each is a function of its inputs. ∎

This is the premise of replay determinism (Ch. 6, Theorem 6.3).

**Theorem 5.5 (relationship identity is retype-stable).** For `rid` of Def. 5.9, `rid(o, κ_r, s, t)` does not depend on the relationship type, so a retype keeps the id. If the retyped relationship meets another one on the same `(source, target, type)`, Def. 6.4 decides: the one with the smaller id is present, the other is absent and reported, and `U` (Def. 3.3) holds after every committed event.
There is no policy to choose here: policies that failed the change or kept whichever relationship was there first would make the result depend on arrival order.

*Proof.* The type is not an argument of `rid`. A retype edit has equal ids in its before and after states (Def. 3.6). When two relationships with positive support hold one `(source, target, type)`, Def. 6.4 keeps the one with the smaller id and leaves the other out, as a function of the fragments and of no order, so exactly one relationship holds the tuple and `U` holds. ∎

**Theorem 5.6 (lossless syntax).** Let `Can` be the set of *canonical* definition graphs: the graphs `parse(t)` of the texts `t` that parse without error. Under premise RT: (a) `parse` and `print` are mutually inverse bijections between the canonical texts `print(Can)` and `Can`; (b) `fmt := print ∘ parse` is idempotent, and `fmt(t) = t` exactly for the canonical texts; (c) two texts have one canonical form iff they parse to one graph.

**Premise RT (round trip).** `parse` and `print` are functions of their argument alone, and `parse(print(G)) = G` for every `G ∈ Can`.

RT is a property of two programs, the parser and the printer. It is tested on the example modules, not on generated graphs. The theorem does not claim `parse(print(G)) = G` for every well-formed definition graph: see the remark.

*Proof.* (a) By RT, `parse ∘ print` is the identity on `Can`, so `print` is injective on `Can`. For a canonical text `t' = print(G)` with `G ∈ Can`: `print(parse(t')) = print(parse(print(G))) = print(G) = t'`. So `print ∘ parse` is the identity on `print(Can)`. (b) `parse(t) ∈ Can`, so `fmt(fmt(t)) = print(parse(print(parse(t)))) = print(parse(t)) = fmt(t)`. If `fmt(t) = t` then `t = print(parse(t)) ∈ print(Can)`; the converse is (a). (c) If `fmt(t) = fmt(t')` then `print(parse(t)) = print(parse(t'))`, and `print` is injective on `Can`, so `parse(t) = parse(t')`. The converse is immediate. ∎

*Remark (why not every well-formed graph).* `parse` gives generated names to the nodes that the text does not name: the steps of `let x` are `x#0, x#1, …` and an unnamed sink of rule `r` is `<r>.<k>` (5.6). The kernel constraints ask only for unique sibling names (K3). A well-formed graph whose step nodes carry other names is therefore in `Can` only if the printer writes it in a form that reproduces those names, which is not known. A draft can also hold a graph that the printer refuses because a part is missing (Ch. 4, 4.6). A committed catalog module is canonical, given RT, because a commit prints every changed module and activates what the check of that text produced (Def. 4.13).

## 5.5 Operators that read measurements

Measurements live on their own plane and create no versions (Ch. 0, Def. 0.7). Four operators read them, and one of them, the threshold, is the only way the value of a measurement can become graph data. Chapter 7 gives their meaning in full; here is what a projection writes.

**Definition 5.11 (the path from measurements to structure).** The measurement path of a projection is `Series → Rollup → Threshold`, with `Delta` first to turn a running total into amounts per period.
- `Series(read v.p₁…pₖ, within d)` reads, for each row, the **closed windows** of the telemetry properties of the element `v` in the input graph, for windows in `[frontier − d, frontier)`, where the frontier is the logical-time frontier of the projection. It reads closed windows in the order they close and never goes back (Def. 7.7); a revised window enters as a retraction followed by an insertion (Ch. 6). With `fill` or `ttl` it also yields filled records and no-data records (Def. 7.15, 7.16).
- `Rollup(by keys, combine aggregates)` aggregates over elements and windows with the declared roll-up function (additive functions roll up exactly, Theorem 7.2).
- `Delta` converts `cumulative` to `delta` per key, starting again when the value drops (Def. 5.6, 7.8).
- `Threshold` is the state machine of Def. 7.10, a threshold with *hysteresis*: two limits with a band between them, so that a value inside the band changes nothing: state `low` becomes `high` after `enterAfter` consecutive windows with value `≥ above`, `high` becomes `low` after `exitAfter` consecutive windows with value `≤ below`, with `above > below` and an `initial` state. It emits one row per key and changes only when the state changes. It is the only operator whose `data` output depends on the value of a measurement.

Editable rules (bijective, lens or sync) contain none of these operators on a path to what they write (P2). Measurements flow forward only (Ch. 8).

*Example:* the drop ratio of calls from `api` to `db`, per minute. With `above 0.05 for 3` and `below 0.02 for 3`, the edge becomes `degraded` after three minutes at or above 5 percent, and `ok` again after three minutes at or below 2 percent. A ratio that jumps between 3 and 4 percent changes nothing.

**Fill, time-to-live and no data.** A source that reports only events, such as warnings, leaves a window without a record when nothing happened. Ch. 7 (Def. 7.14 to 7.16) defines how such a window can be filled with a value, and how a subject that is silent too long gets a state of its own, *no data*. The text:

```
|> series G x.p [fill c], x.q [fill c] within D [ttl T]
|> rollup by { ... } combine { ... } [nodata any]
|> threshold m by { ... } above A for N then "x" below B for M then "y" initial "z" [nodata then "n" | nodata ignore] as s
```

`fill c` gives a covered window without a record the constant `c`. `ttl T` is the time-to-live after which a subject without a record is in no data. A roll-up key is in no data when every member that has a record, stored, filled or marked no data, is in no data (Def. 7.16), or with `nodata any` when one is. `nodata then "n"` is the value a `Threshold` gives such a key, and `nodata ignore` gives it no row. The checker refuses `fill` without `ttl`, `fill` on a gauge or a `cumulative` property, a `ttl` below one window width or above `within` less one window width, a `Threshold` over a series with `ttl` that has no `nodata` clause, and a `nodata` clause without a `ttl` upstream (Ch. 7, after Def. 7.16). The `lift` statement does not have these clauses yet.

## 5.6 Concrete syntax

Definitions are written in one text language: declarations of types in the style of PG-Schema ([pgschema]), patterns in the style of Cypher and GQL ([opencypher], [gql]), which are languages for describing and querying graph databases, and computations as named pipelines, `let x = … |> … ;`. Between programs, definitions travel as the canonical JSON of their facts (Ch. 1). Each construct of the text is one or a few nodes of the definition graph:

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
| `let u = union a, b;` and `\|> distinct` | `Union` needs the same columns of the same types. `Distinct` is set semantics over signed rows, rows with a weight as in Ch. 4 (Def. 4.6) |
| `\|> traverse g via Rel out\|in\|either depth 0 .. 8 from c as d` | adds the columns `d`, `d_depth` and the demanded read columns `d.prop`. Depth at most 16. Rejected: a bad depth, an unknown start |
| `let r = fixpoint seed via <pipeline that reads r> max 8;` | the step must produce the columns of the relation. `max` is the unrolling bound (Ch. 6) |
| `values { a: 1 }, { a: 2 }` | constant rows of one shape, emitted once when the graph first evaluates |
| `\|> retain on time for 24h` | `Retain` on the required data Instant column `time`, age a duration. Rejected: a field that is not a required data Instant, an age that is not positive, windowed input rows |
| `mean(x)`, `any(b)`, `all(b)`, `last(value, order)`, `weightedMean(value, weight)` | aggregates; `last` takes an ordering expression |
| `in n: param Integer = 0;` | a parameter port (Def. 5.14) |
| `state: Text roles { "Healthy": ok };` | value roles (Ch. 2, Def. 2.13) |

**Definition 5.14 (parameter).** A projection may declare an input port of kind `param` with a value kind `κ ∈ {Text, Integer, Decimal, Boolean}` and an optional default constant. In an expression a parameter is a constant of kind `κ`, role data, requirement `req`. A graph of the space that binds the projection gives a literal of kind `κ` for every parameter that has no default (`graph g = m.p(master: master, minReady: 4)`), and the projection is checked again with the values per binding. A parameter is definition data: a `Port` with kind `param` and a `Binding` with a `value`, so the editor creates and edits it with the same patches as any other definition fact. The same projection bound twice with different values is two graphs.

*Example:* the deployment health of Ch. 2 has the parameter `in minReady: param Integer = 0;`. The space binds it as `minReady: 1` for one graph; the same projection bound with `minReady: 2` is a second graph.

**Proposition 5.8 (parameters are constants of the binding).** The output of a bound graph is a function of its input graphs, its recorded rows, its definitions and its argument literals. A binding that omits a parameter without default, gives a literal of another kind, or names a port the projection does not have, is rejected when the space is made active and changes nothing.

*Status.* The first part follows from Def. 5.14 (the binding gives a constant) and Theorem 6.3, with the literal as one more input of the definitions; no proof is written out, and the proposition counts as tested. Tests: one projection bound with a threshold of 5 and of 1 gives the groups `a` and `a, b`; each of the three wrong bindings is rejected with its message.

**Definition 5.15 (retention).** `Retain(ts, age)` has a required data `Instant` column `ts` of its input (not windowed) and a positive `age` in nanoseconds (the text `retain on time for 24h`). Let `λ` be the logical-time frontier (the time up to which windows are closed, Def. 7.6 and Def. 7.11) and `time(r)` the value of `ts` in row `r`. A row `r` with weight `w > 0` passes unchanged if `time(r) + age > λ` at its recorded position, and is counted and dropped otherwise. A held row is withdrawn, that is emitted again with weight `−w`, when an advance moves `λ` to a value with `time(r) + age ≤ λ`. A retraction of a held row passes and releases it. A retraction of a row that has been withdrawn is absorbed. A row in which `ts` is absent breaks its row contract, because the column is required: it is dropped at the `Retain` and counted as a runtime fault. It neither passes nor is held, so it never has to expire. It is not passed, because a row without a time could never be withdrawn and the retained set would no longer be a function of the frontier (Proposition 5.9); and it does not fail the input, because one malformed row must not stop the rows around it (Def. 5.19). Whatever is derived from a withdrawn row is retracted by the support counts of Ch. 6, so retention is not a second removal mechanism for elements.

**Proposition 5.9 (retention is a function of the frontier).** For every recorded input and every position `n` of it, the integrated output of `Retain` equals the integrated input restricted to the rows that have a time and are *live* at the frontier `λ_n` of that position: `I(out)[n] = I(in)[n] restricted to { r | time(r) + age > λ_n }`. The right side does not mention how the input is cut into steps.
Theorem 6.1 uses this proposition for its `Retain` case, and the proof below does not use Theorem 6.1.

*Example:* evidence of the shop kept for 24 hours. At logical time `λ` an event of time `λ − 25 h` is not in the output, an event of time `λ − 23 h` is, and it leaves the output by itself when an advance moves `λ` past `time + 24 h`.

*Proof.* Write `live(λ) = { r | time(r) + age > λ }`. The frontier does not decrease (Theorem 3.5), so `live(λ') ⊆ live(λ)` for `λ ≤ λ'`: a row that is expired at one position is expired at every later one. Rows without a time are dropped at arrival with either sign of weight (Def. 5.15) and are left out of both sides. Let `H_n` be the weights that the operator holds after position `n`. Claim: `H_n = I(in)[n]` restricted to `live(λ_n)`, and `I(out)[n] = H_n`. For `n = 0` both are empty. A position is one of two kinds.
- *A row `r` with weight `w` arrives, the frontier stays.* If `r ∈ live(λ_n)`, the row passes with weight `w`, for an insertion and for a retraction alike, and is added to `H`; a retraction that precedes its insertion leaves a negative held weight, which the insertion cancels (Def. 6.4). Both sides of the claim gain `w` at `r`. If `r ∉ live(λ_n)`, nothing is emitted and `H` is unchanged: an insertion is expired on arrival, counted and dropped, and a retraction is absorbed. The right side is unchanged too, because it is restricted to live rows.
- *The frontier advances from `λ_n` to `λ_{n+1}`, no row arrives.* The operator emits `−H_n(r)` for every held `r` with `time(r) + age ≤ λ_{n+1}` and releases it. So `H_{n+1}` is `H_n` restricted to `live(λ_{n+1})`, which is `I(in)[n+1]` restricted to `live(λ_{n+1})` by the induction hypothesis and `live(λ_{n+1}) ⊆ live(λ_n)`; and `I(out)[n+1] = H_{n+1}`.
A step that carries several positions is their composition, so the claim holds at the end of every step whatever the cut. ∎

*Remarks.* (1) The revisions in between depend on the cut; the proposition is about the state at a position. (2) A row that an operator derives while an advance is processed is judged against the frontier before the advance (premise B2 of Theorem 6.2). Judging it after the advance gives the same integrated output, because a row that passes at `λ_n` and is expired at `λ_{n+1}` is withdrawn by the same advance; only the count of dropped rows differs, and B2 fixes that count. (3) Whatever is derived from a withdrawn row is retracted by the support counts of Ch. 6.

*Measured.* On the small samples of recordings, with an age that expires the oldest 25, 50 and 90 percent of the evidence, the master graph keeps exactly the evidence whose time plus age is above the frontier, and the digest of the state is the same for batches of 1, 7, 50 and 100,000 records. On each of the two real recordings, with an age that expires the older half, the same holds for batches of 1, 50 and 100,000. The proposition is not tested on generated inputs.


Generated names: an unnamed sink in rule `r` is `<r>.<k>`; pipeline steps of `let x` are `x#0, x#1, …` with the last named `x`. Layout is canonical, `///` comments are kept as descriptions and `//` comments are dropped.

**Worked example.** The service graph of the running example, written out: a forward view that derives dependencies between services from calls between pods, and turns the drop ratio of closed one-minute windows into a `status` with hysteresis. The one `match` of three edges is another way to write the two `Match`es and two `Join`s of the example in 5.1; both give the same rows.

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

A *refactoring* changes how definitions are arranged without changing what they compute. A projection is a graph (Ch. 2, Def. 2.1), so a refactoring is a patch on the catalog. Three refactorings are defined, each as a function of the canonical text of the projections of **one module** that returns a patch for a draft and the new text of the module (Ch. 4, 4.6). Nothing changes until the patch is applied to a draft and the draft is committed.

**Definition 5.12 (refactoring).**
- `extract(P, S, n)` moves the statements `S` of projection `P` into a new projection `n`, with its own output graph in the space (named after the parent graph and `n`) bound to the parent's arguments. `S` names top-level `let` chains, sinks (`emit.k`, `k` counting the emits of `P` in canonical order) and whole rules. Lets used only by `S` move with it. The ports `n` uses are copied. An update sink moves only together with the create sink of its type.
- `inline(n, P)` is the reverse. The graph of `n` is removed from the space.
- `moveRule(P, Q, r)` moves rule `r` and the lets only it uses between two existing projections.

*Example:* in the service graph, `extract` of the statements that write `DependsOn` moves them into a new projection with its own graph. The services stay in the old graph, the dependencies are now in the new one.

**Definition 5.13 (output union).** For the state of a graph space, `Out` is the multiset of all element facts `(id, type, endpoints, properties)` and all telemetry windows `(element id, property id, window start, value)` of all graphs of the space, with the graph name forgotten.

**Proposition 5.7 (refactoring preserves outputs).** Let a refactoring of Def. 5.12 be accepted, its patch applied to a draft and the draft committed. Call the sinks it moves `M`, the other sinks of the giving projection `K_P`, and the sinks that the receiving projection had before `K_Q` (empty for `extract`). Under the premises R0 to R5, `Out` after the commit equals `Out` before it.
- **R0 (full replay).** Making the change active evaluates the changed projections on the whole recorded input (Ch. 9, 9.5), not on a bounded retained part of it.
- **R1 (closed selection).** No statement that stays reads a moved one, and no moved statement reads one that stays. The prototype enforces it.
- **R2 (copied ports and parameters).** The receiving projection has the ports and parameters that the moved statements use, bound to the same graphs and literals. The prototype copies them.
- **R3 (disjoint keys).** No element key, no uniqueness tuple `(source, target, type)` and no telemetry key receives a fragment from a sink of `M` and also from a sink of `K_P` or of `K_Q`. The prototype enforces only one part, at the level of types: an update sink moves only with the create sink of its type. **It does not check R3.**
- **R4 (no reader of what moves).** No graph of the space reads, through a graph port, an element that a sink of `M` emits: not from the giving graph before the commit and not from the receiving graph after it. The prototype does not check it: a reader of the parent graph is not rewired.
- **R5 (one output ontology).** The giving and the receiving projection have the same output ontology module. True for `extract` and `inline` by construction; for `moveRule` it is a premise.

Separately, `inline` after `extract` of the same selection restores the text of the module; that is a property of the two functions, tested on samples and not proved.

The proposition is proved below under R0 to R5. R1 and R2 are enforced by the prototype, R5 holds by construction for two of the three refactorings, and R3 and R4 are premises on the definitions and their data that nothing checks.

*Proof.* Let `F_s` be the Z-set of fragments of a sink `s` (Def. 6.3), that is the signed delta (Ch. 4, Def. 4.6) of the facts it writes.
*Step 1: every sink keeps its fragments.* The fragments of a sink are a function of the rows at the in-ports of its operator subtree and of its parameters (Def. 6.3, Proposition 5.4). By R1 the subtree of a moved sink moves whole, and by R2 its ports are bound to the same graphs and its parameters to the same literals. By R4 no graph that a subtree reads changes its content, and graphs upstream of the two projections are untouched. By R0 and Theorem 6.3 the evaluation after the commit is the evaluation of the recorded input, so by induction along the graph space (S3) every sink `s` of the space has the same `F_s` at every position as before. Ids inside the fragments are unchanged: entity ids come from identity domains (Def. 1.9), and `rid` has the output ontology module as its only scope (Def. 5.9), which R5 keeps. Closed windows are computed inside the subtree, so the telemetry fragments are unchanged too.
*Step 2: materialization splits over disjoint keys.* Write `mat(F)` for the facts and telemetry windows that Def. 6.4 gives a fragment family. If two families `F`, `F'` share no element key, no uniqueness tuple and no telemetry key, then `mat(F + F') = mat(F) ⊎ mat(F')`: presence, type conflict and property conflict are decided per element key from the fragments of that key; a uniqueness conflict is decided per tuple from the relationships with that tuple; a telemetry value per telemetry key (premise B1 of Theorem 6.2).
*Step 3.* Before the commit the two graphs hold `mat(F_{K_P} + F_M)` and `mat(F_{K_Q})`; after it `mat(F_{K_P})` and `mat(F_{K_Q} + F_M)`. By R3 and step 2 both pairs have the union `mat(F_{K_P}) ⊎ mat(F_M) ⊎ mat(F_{K_Q})`. The other graphs are unchanged by step 1. `Out` forgets the graph name (Def. 5.13), so it is the same multiset. ∎

*Why the premises are needed* (each counterexample passes the prototype's checks).
- *Without R3, one key.* Two sinks of `P` emit the same service with the same properties, and one is extracted. Before, the graph holds the fact once. After, two graphs hold it, and `Out` holds it twice.
- *Without R3, a conflict.* Two sinks of `P` give `owner` the values `"a"` and `"b"` for one key. Before, the element has no `owner` (Def. 6.4). After the extraction each graph holds the element with its own value.
- *Without R3, a uniqueness tuple.* Two relationship sinks with different key rules emit `(s, t, Calls)`. Before, only the smaller `rid` is present. After, both graphs hold one.
- *Without R4.* A third graph reads `DependsOn` from the parent graph, and the sink that emits `DependsOn` is extracted. The third graph loses its input and its output changes.
- *Without R0.* With a bounded retained set (Def. 9.4), the new graph is built from the retained inputs only and lacks what older inputs produced (Theorem 9.2).
- *Without R5.* `moveRule` into a projection with another output ontology changes every `rid` of the moved relationship sinks.

**What the prototype refuses.** A refactoring is refused, and nothing changes, when a kept statement reads a moved one or the reverse (R1); when an update sink is selected without the create sink of its type; when a selected name does not exist; when the projection that builds the master graph would be split; when the selection is a statement inside a rule; when the named projection does not exist; or when the refactoring would touch two modules.

*Measured.* Ten test cases on the sample definitions: two extractions out of the projection of the capability level, after which the output union is equal and `inline` restores the output union and the text exactly; one move of a rule between two stacked projections, after which the output union is equal; seven of the eight refusals above are raised (the one for two modules is not tested); and one extraction that copies the parameters it uses. All on one module. Not tested: other modules, generated projections, a violation of R3, R4 or R5, and what happens to lineage records and version numbers (the activation writes the new graph's content and the old graph's loss as patches).

*Readers of the parent graph.* A refactoring copies the parameters that the moved statements use and binds them in the space (Def. 5.14). A graph that reads the parent graph is not rewired: it sees only what the parent kept. A refactoring can therefore be accepted and still change what a downstream graph computes. That is premise R4.

*Why the relationship id has no graph in it.* If the id of a relationship contained the id of its graph, `extract` would change the ids of every moved relationship, and the output union would differ by construction. Def. 5.9 therefore scopes relationship ids by the module of the output ontology and the key rule.

## 5.8 Levels of abstraction: a projection lifts the edges with the nodes

A projection that summarizes a graph is an abstraction of it. Several nodes of the graph below become one node of the level above, and the edges between the nodes below become edges between their images. A level that lifted only the nodes would be a list, not a graph: the service graph has dependencies because pods call pods, and a map of business capabilities has dependencies because services depend on services. The language has one statement for this, `lift`. This section fixes what a level means, so that it can be stated, tested and composed, and shows that the statement adds nothing new: before it runs, it is replaced by operators the language already has.

**Definition 5.16 (level, image).** Let `B` be a graph, the graph below, with nodes `V_B` and a set `E_B` of directed edges of one relationship type; an edge `e` has a source `s(e)` and a target `t(e)`. A *level* over `B` is a graph `L` with nodes `V_L` and an *image*, a partial function `f : V_B ⇀ V_L`. A node outside `dom f` has no image. The image is data and not code: a relationship of some graph (a pod backs a service, a service realizes a capability) or a computed key (a service belongs to the group of its namespace). A computed key is a function by construction. A relationship is a function only if no node has two of them; where the data may hold two, the statement must say what happens to such a node (below, "The image must be a function").

**Definition 5.17 (lifted edges, fibre).** The lifted edge set of `E_B` under `f` is

```
E_L = { (f(s(e)), f(t(e))) | e ∈ E_B, s(e) ∈ dom f, t(e) ∈ dom f, f(s(e)) ≠ f(t(e)) }
```

For a lifted edge `(x, y)` its *fibre* is `E_B(x, y) = { e ∈ E_B | f(s(e)) = x, f(t(e)) = y }`, the edges below that it stands for. Three rules are consequences of the definition and not options. An edge whose two ends have one image would be a self-loop and is not lifted: traffic inside an image is not a dependency between images. Edges with the same pair of images are one edge. An edge with an end outside `dom f` is not lifted, and no catch-all node is invented for it. The lifted edge is a relationship keyed by the pair of images (Def. 5.9), so its id does not change while its fibre changes, and it is retracted when its fibre becomes empty (support counts, Ch. 9).

*Example:* `B` is the master graph with the edges `Calls` between pods, and `f` maps each pod to the service it backs. A call from a pod of `web` to a pod of `api` lifts to the edge `web → api`, whose fibre holds every such call. A call between two pods of `api` would be a self-loop and is not lifted. A call to a pod that backs no service is not lifted either.

**Definition 5.18 (lifted telemetry).** Let `c` be a counter on the edges below (a `delta` series, Ch. 7) with closed windows `w`. The lifted counter is the sum over the fibre, window by window: `c_L((x, y), w) = Σ_{e ∈ E_B(x, y)} c(e, w)`. A ratio is not lifted: it is recomputed from lifted counters, `ρ_L = d_L / c_L` (and 0 where `c_L = 0`). A state is not lifted either: it is derived from the lifted ratio by the crossing operator with the same thresholds (5.5). The state of a lifted edge therefore describes the traffic the edge carries and is not the worst state of its parts. Tested with values in one case (below).

**Proposition 5.10 (lifting is a quotient).** Let `B_f` be `B` restricted to the nodes of `dom f`, without the edges whose two ends have one image. Then (a) `f` is a graph homomorphism from `B_f` onto `(f(dom f), E_L)`: every edge of `B_f` maps to a lifted edge and every lifted edge has a non-empty fibre; (b) `E_L` is the smallest edge set on `V_L` for which (a) holds; (c) the fibres of the lifted edges partition the edges of `B_f`.
*Proof.* Directly from Def. 5.17. (a): an edge of `B_f` has both ends in `dom f` and distinct images, so its pair of images is in `E_L` by definition; a pair is in `E_L` only through some edge, which lies in its fibre. (b): any edge set that makes `f` a homomorphism must contain the image pair of every edge of `B_f`, which is `E_L`. (c): an edge of `B_f` has exactly one pair of images. ∎

**Proposition 5.11 (conservation).** For a counter `c` and every window `w`: `Σ_{(x,y) ∈ E_L} c_L((x, y), w) = Σ_{e ∈ E(B_f)} c(e, w)`. What a level does not count is exactly the traffic inside one image and the traffic with an end that has no image; nothing is counted twice.
*Proof.* By Proposition 5.10 (c) the fibres partition the edges of `B_f`, and by Def. 5.18 each lifted counter is the sum over its fibre, so the sum over all lifted edges is the sum over all edges of `B_f`. ∎

The same statement is false for a ratio averaged over a fibre, which is why Def. 5.18 recomputes ratios from summed counters (the same reason as for `mean` in Def. 5.6).

*Example:* `api` has two pods. Pod 1 sent 100 calls to `db` with 10 drops, pod 2 sent 900 with 9 drops. The lifted edge `api → db` carries 1,000 calls and 19 drops, ratio 1.9 percent. The mean of the two ratios, 10 and 1 percent, would be 5.5 percent and would mark the edge degraded although almost all traffic is fine.

**Proposition 5.12 (lifting composes).** Let `f : V_B ⇀ V_L` and `g : V_L ⇀ V_M`. Lifting `E_B` under `f` and the result under `g` gives the edge set that lifting `E_B` under `g ∘ f` gives. The fibre of an edge `(u, v)` of the upper level under `g ∘ f` is the disjoint union of the fibres `E_B(x, y)` over the edges `(x, y)` in its fibre under `g`, so lifted counters, the ratios recomputed from them and, for equal thresholds and window width, the states derived from them are the same on both routes.
*Proof.* An edge below is dropped on the first route iff `f` gives its ends one image or `g` gives the images one image, or an end leaves the domain on the way; that is iff `g ∘ f` gives its ends one image or is undefined on an end. Sums over a disjoint union are sums of sums. ∎

Consequence: a level may read the level below it and need not read the master again, which is how the capability level is written. Def. 5.18 has no look-back; written as `lift` statements the two routes agree for equal `within`, because a level holds the windows that its own look-back reads.

**The statement.** One `lift` per level, in a forward projection or a `readonly` rule, a rule whose output cannot be edited back (Ch. 8):

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

The two levels of the prototype's definitions:

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

The first lifts pod calls to service dependencies, the second lifts service dependencies to capability dependencies and reads the first level, not the master (Proposition 5.12). The lifted properties of the second have their own names (`callFlows`, not `flows`), because a property name that two modules declare is served qualified and the names of the level below must not change. Written as ordinary statements the two levels take 46 lines; as `lift` statements they take 18.

**The image must be a function, or the statement must say otherwise.** Propositions 5.10 (c) and 5.11 need `f` to be a function: if a node below has two images, each pair of images gets an edge and the edge below is counted in each. The checker enforces a choice.
- *Declared.* If the ontology declares the mapping a function (`constraint cardinality Realizes per source 0 .. 1`), no `images` clause is needed. A conformance check reports data that breaks the constraint and an edit that adds a second image is rejected. Ingestion does not check it, so recorded data can still hold a node with two images; the default `images reject` then leaves that node out of `dom f`. The image the lift uses is a function by construction, and the propositions hold.
- *Stated.* If the mapping is not declared a function, the statement must carry an `images` clause, or the checker refuses it. `images reject` gives the same guarantee as above. `images each` gives up conservation on purpose: the service level says `images each` for `Backs`, so a pod that backs two services counts for both, each image gets the full count, and sums over the images can exceed the total below. Propositions 5.10 (c) and 5.11 hold for that level only on data in which every pod backs at most one service.

**Lowering.** `Lift` is an operator kind of the kernel (the twentieth, Ch. 2, Def. 2.2), so a `lift` statement is a node of the definition graph with the slots `when`, `carry`, `keep`, `derive` and `state`, the ports `edges`, `map` and `out`, and the edges `liftEdge`, `liftVia` and `ofType`. The catalog keeps the node, and the printer writes it back with only the clauses that differ from the defaults. It has no evaluation rule of its own. When the projection is checked, the node is replaced, in a copy of the graph, by the operators it stands for (this is called *lowering*):

| Step | Operators |
|---|---|
| edges below, and the mapping | `Match` of `EDGETYPE`; `Match` of `MAPPING`, with a `Filter` for `when` |
| `images reject` | `Group` that counts the images per node, `Join`, `Filter` on a count of one |
| both ends to their images | two `Join`s; a `Filter` that drops the self-loops |
| the lifted edge | `EmitRelationship`, row by row |
| lifted telemetry | `Series` over the carried properties within `within`, `Rollup` by the pair of images and the kept values, `Map` for the derived properties, `EmitRelationship` as an update |
| each `state` | `Threshold` and an update |

The generated operators are written as text and go through the ordinary parser, checker and lowering. Two consequences.
- *What follows from the lowering, with no new argument.* Everything that is stated for those operators holds for a lift, because after lowering nothing else is there: the typing rules of 5.2, the incremental strategies and batch invariance (Theorems 6.1 and 6.2), replay determinism (Theorem 6.3), retention (Proposition 5.9), the crossing of the value of a measurement to data through `Threshold` only (5.5), and the supports of Theorem 5.3, and lineage, which is complete because the lifted edge is a row-level emit (Ch. 9, 9.5). Each holds as strongly as it does for the operators, no more.
- *What does not follow and is tested.* That the lowering implements Def. 5.17 and 5.18 is not proved: it is a property of the text that the lowering generates. It is tested by equality with the statements it replaced and by one test per clause (below). A test that compares a lift with the two definitions directly on generated graphs does not exist yet. Propositions 5.10 to 5.12 are statements about the definitions and stay proved whatever the lowering does; they describe the prototype exactly as far as the lowering is correct.

**What the checker refuses**, naming the statement: a mapping that is neither declared a function nor given an `images` policy; an edge type, mapping type or lifted type that is not in the ontology of its graph; a `carry` whose target or source is not a telemetry property, or whose kinds differ; a combine rule other than `sum`, `min`, `max` or `mean`; a `derive` target that is not telemetry; a `state` that names a measure as its target, or a source that is neither carried nor derived; a `keep` that is not `src.p` or `dst.p` of a data property of the image; a lift in a `lens` or `bijective` rule, because its edges cannot be edited back; and whatever the generated operators fail to check. A refactoring treats a lift like a `let` that reads only graph ports (5.7).

**In the viewer.** The editor of the viewer can build a `lift` statement without text, and rebuilds the two levels with it, in 82 and 67 editing steps, equal to the written definitions (Ch. 12, 12.4).

**Three levels on the synthetic estate.** The synthetic estate is a small recording written by a program, with invented names, in which every source and every operator has data (Ch. 14). On it the master graph has 47 entities and 48 relationships, among them pods with `Calls` edges; the service level `serviceTree` has 4 entities and 5 relationships, with `DependsOn` from `web` to `api` and from `api` to `db`; the capability level `capabilities` has 4 entities and 4 relationships, 2 `Under` (the authored hierarchy) and 2 `DependsOn` lifted from the service level (Storefront to Ordering, Ordering to Data platform). On the two real captures `capabilities` is empty: capabilities are authored data, and no source of those captures recorded any.

**Tests.** The authors read these tests and did not run them; the runs are among those of Ch. 14 that they did not repeat.
- *The statement equals what it replaced* (36 cases). The definitions with `lift` give the same entities, relationships, properties, measurement values and state digest as the definitions written out by hand, on the synthetic estate, on the small samples and on the 17-minute real recording. The digest of the lifted graphs is the same for batches of 1, 50 and 100,000 records.
- *Each clause:* the lifted flows are exactly the flows of the edges whose ends have one image each and different ones (four variants); `images` chooses between leaving out a node with two images and counting it for each; `selfloops` drops or keeps the calls inside one image; `when` decides which images count; `within` sets the look-back; kept values ride on the windows. The printer gives back the text it parsed and names only what differs from the defaults. Sixteen refusals of the checker, each naming the statement and the reason.
- *The properties* (28 cases, written before the statement and unchanged by it): every graph of the space whose types include relationship types has relationships on the estate, with two reasoned exceptions; six variants of the estate for Def. 5.17 and 5.18; composition against a one-step lift in two cases (Proposition 5.12); the ratio and the state of a lifted edge with values; a service with two images.
- *Around it:* the editor's description of the statement, a draft edit and a preview of a lift; a rule with a lift moves between stacked projections, and a lift alone cannot be extracted away from its readers; a lifted edge names every edge of its fibre, both ends and both images, forward and reverse (Ch. 9).

**What is tested and what is not.** Tested on examples: Proposition 5.10 (a) and (c) and Proposition 5.11, Proposition 5.12 in two cases, the recomputed ratio and the derived state, every clause of the statement, and the equality of the lowering with hand-written statements on three inputs. Not tested: anything on generated graphs or images; composition over more than two levels; a pod that backs two services under `images each` on the service level itself (the clause is tested on the capability level).

## 5.9 What the prototype does differently

- **Operators.** 19 of the 20 operator kinds are built. `Delta` is not, because no property in the definitions or the recordings is a running total. `Match` has no patterns of variable length. `Fixpoint` is computed as a bounded unrolling of its body (Ch. 6). `Union` has only the conflict policy `preserveEvidence`; the other three are refused with an error that names the rule and its position.
- **Aggregates over measurements.** The prototype is stricter than Def. 5.6: `weightedMean` and `last` are allowed only over data, not over a `gauge`, and `mean` in a `Rollup` needs a `gauge`. The checker refuses the other cases with a message.
- **Retention** of evidence uses `Retain` with an age of 24 hours in the rules that write evidence. The age is a choice, not a result.
- **The `lift` statement** has no clauses for fill and no data yet.
- **Tests** of the typing rules are examples of refused projections, each with the rule it breaks and its position; type soundness on generated projections is not tested.
