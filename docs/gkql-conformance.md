# gkql conformance note

The conformance note of gkql, in the form of Section 7.6 of the requirements chapter (`paper/core/ch07-requirements-on-a-projection-language.md`). Classes, keys and requirements (R1 to R8) are those of Ch. 7, Def. 7.3 and 7.4. Issue: #159.

```mermaid
flowchart LR
  P["program graph<br/>(JSON)"] -->|"check ⊢ p"| C["checked program"]
  C -->|"lowering"| K["kernel graph<br/>= presentation"]
  K --> S["steps<br/>L, J, K, T, F"]
  S --> B["construction<br/>(nodes, edges)"]
  B --> M["merge<br/>per id"]
  M --> G["output graph"]
```

## The language

| For | gkql |
|---|---|
| `⟦p⟧` (R1) | A program is a finite acyclic graph of verbs; wires carry weighted rows with named columns. `⟦p⟧` is the composition of the functions of its verbs along the wires (a loop in the wires is refused: recursion is the `fixpoint` verb). Every expression has a value on every row: a failing expression (division by zero, overflow, a missing property) is absent, absent propagates, a filter drops a row whose predicate is absent, and only `coalesce` and `isPresent` take an optional argument. |
| `⊢ p` (R3) | Lowering refuses a malformed program; the checker then types the schema of every step, checks the wiring, and checks the sinks against the output ontology: declared, non abstract types and properties, required properties, edge endpoint types, one rule per property or relationship type where a rule is editable, window keys no finer than the identity. A program with an error is not run. |
| presentation (R6) | The lowering writes the kernel graph (one kernel operator per step, its ports and wires) from the program alone; the engine compiles it to one physical operator per step. The key functions below are read from the verb's fields. |
| values | Decimals are numbers: 2.5 and 2.50 are one value and one key, and an operator that keeps one of several equal decimals keeps the number without trailing zeros (G5, which is #161). |
| time (R8) | The clock is the only "now". A rows input may declare its `time` column and its `lateness` (Def. 6.1). |

## Verbs

One row per verb. "Fails" is the requirement that fails if the row is wrong.

| Verb | Steps and class | Keys, or `s` and `u` | Stores | Fails |
|---|---|---|---|---|
| `values` | a constant wire (Def. 7.2) | none | nothing | R1 |
| `rows` | an input wire | none | nothing | R1 |
| `graph-match` | L (a type filter per pattern node and edge), then a chain of J | edge `u → w`: `κ₁ = id(n_u)`, `κ₂ = source(e)`; then `κ₁ = target(e)`, `κ₂ = id(n_w)`. A reference node binds by id alone. | nothing; the input graph's adjacency is the arrangement | R5, R6 |
| `where` | L: `f(1·r) = 1·r` if the condition is true, else 0 | none | nothing | R5 |
| `project` | L: a function of each row, weight kept; the named columns only | none | nothing | R5 |
| `extend` | L: a function of each row, weight kept; the input columns and the named ones | none | nothing | R5 |
| `union` | L: the sum of its wires | none | nothing | R5 |
| `distinct` | K, not summarised | `κ(r) = r` | net weight per row | R1, R6 |
| `join` | J (inner; at least one equality) | `κ₁` = the left `on` columns, `κ₂` = the right `on` columns; a key with an absent part joins nothing | each side arranged by key | R5, R6 |
| `summarize` | K; per aggregate below | `κ` = the `by` columns; a row with an absent key is dropped | per key the state of each aggregate | R1, R6 |
| `window` | L: a row goes to every window of `period`, one every `hop`, that holds `time(e)` | none | nothing | R8 |
| `summarize` after `window` | K, then T | `κ` = (`by`, window start); `s` = window end, `u = ∞` | per key and window | R6, R8 |
| `series` | J of element rows and windows, then T; with `fill`: K, J, L, T (see time) | `κ₁` = the element column, `κ₂` = the window's element; `s = −∞`, `u` = window start + `lookback` | per element its rows and `since` | R1, R6, R8 |
| `hysteresis` | K, not summarised: a state machine over the windows of a key, in window order | `κ` = the `by` columns; a window two rows of one key hold has no reading until one row holds it (G2) | per key its windows in the look-back and one carried state | R1, R6 |
| `retain` | T | `s = −∞`, `u = time(e) + age`, against the clock | the rows by time | R8 |
| `traverse` | J unrolled at most `max` times, then K | J: rows keyed by start, edges keyed by the frontier id; K: (start, reached id), keeping the least depth; `0 ≤ min ≤ max ≤ 64` and `max ≥ 1`, else refused | rows by start; depth per (start, id), both ways | R5, R6 |
| `fixpoint` | F, the least solution (cycles allowed), then J with the node rows | `κ_F`: a node row names its key, an edge row its `from`; J: the keys with an own row of positive weight and a value above `⊥` | per node its rows, its edges both ways, its value | R7 |
| `link` | L (weights summed per pair of ids), then the classes of the present links | the pair of own ids | weight per pair; classes; a sparse own-id table in the sinks | R1, R4 |
| `nodes` | construction step, then merge | ids: see elements; merge keyed by id | per element its fragments | R2, R4 |
| `edges` | construction step, then merge | ids: see elements; merge keyed by id | per edge its fragments | R2, R4 |

### Aggregates of `summarize`

| Aggregate | Summarised | `φ` and `d`, or the state per group | Result |
|---|---|---|---|
| `count` | yes | `d = 1`, `Σ w` | a row only if `Σ w > 0` |
| `countif` | yes | `d = 1`, `Σ w·[c]` | |
| `sum` | yes | `d = 1`, `Σ w·v`, exact: 128 bit integers, 256 bit decimals at scale 28 | absent and counted under `overflow` outside the type |
| `avg` | yes | `d = 2`, (`Σ w·v`, `Σ w`) exact | `g = s / n`; absent at `n = 0` or overflow |
| `min`, `max` | no | the multiset of values | absent with no value |
| `count_distinct` | no | the multiset of values | |
| `arg_max`, `arg_min` | no | the multiset of rows (order, values) | the greatest order, ties broken by the values |
| `percentile(v, p)` | no | the multiset of values (Theorem 7.4 b) | nearest rank `max(1, ⌈p·n/100⌉)` over the rows of positive weight |

Over windows only `count`, `sum`, `min`, `max` and `avg` are allowed.

## Elements (R4, R2)

| Construct | Ids |
|---|---|
| `graph-match`, `traverse`, `fixpoint` | copy the id of an input element into a column; no new id |
| `nodes` | a copied element column, or `identity(domain, values)`: `ι(domain, canonical values)`, a 130 bit hash of the tenant, the domain and the canonical components, the same for all programs of a tenant; the canonical form (lower case, trimmed, canonical addresses) is part of `v_j`; the id written is the representative of its class under the present links (Ch. 2, Prop. 2.3) |
| `edges` | `ι(c, source, target)` with the constant `c` = (program, edge key); the type is not part of the id |
| other functions | none returns an id: `id(e)` reads an element as text; `identity` is allowed only in `nodes`, `edges` and `link` |

Merge (Def. 7.4), per graph (one program writes a graph): fragments of positive weight count. Creates that disagree on type or endpoints: the element is absent (case 0). Two values of one property: that property is dropped, the rest stays (case `s′`). Several edges claim one (source, target, type): the smallest id is present. Each conflict is recorded.

## Recursion (R7)

| Construct | Order `V`, `⊥` | Monotone | `V_a`, `ht_a` |
|---|---|---|---|
| `fixpoint` | `{⊥}` and the closed list `order`, least first; a value outside the list at run time is `⊥` and counted. Refused: an empty order, a repeated value, a value of another type than the value column, a value column that is not Text, Integer, Decimal or Boolean | `h_{a,k}` = the greatest of the node's own values and of the images of its dependencies' values; cycles allowed, the result is the least solution. Refused: an image list without one entry per value of the order, an entry outside the order, a list that goes down, a list chooser that is not a Text column of the edge rows | `V_a = V`, a chain; `ht_a = |order|` |
| `traverse` | not F: a bounded J and K, at most 64 rounds | | |

`traverse` stays (D26 in #122): fixpoint expresses reach only by keying on (start, node), one edge row per start per edge, so its work grows with starts times edges, where `traverse` walks only the starts a change reaches: an added edge lowers depths from its far end on, a lost edge walks again each start whose walk visited an end of it. There is no `rollup` verb: a roll-up over windows is `summarize`, and a roll-up over a tree is `fixpoint`.

The checker also refuses a `fixpoint` (R3) whose inputs are not data rows or are windowed, that names a key, value, `from` or `to` column its inputs lack, whose key is a list, a null or a relationship, or whose `from` and `to` are not of the key's type.

After a change `fixpoint` evaluates only the changed keys and the nodes that reach them over present edges (Theorem 7.6).

## Time (R8)

| Construct | Function of `time(e)` and `arr(e)`, or clock selection |
|---|---|
| rows input with `time` and `lateness` | a row is late if `arr(e) > time(e) + lateness` (Def. 6.1); a late row or a late withdrawal is left out and counted under `lateRows` |
| `window` | of `time(e)` only |
| windowed `summarize` | T, `s` = window end, `u = ∞`; a window is kept until its end plus the largest lateness of its upstream rows inputs, or the host horizon (a number of window widths) when one of them declares none or none is upstream (D1 on #159), at most the window retention; a row for a window past that is left out and counted under `lateRows` |
| `retain` | T, `s = −∞`, `u = time(e) + age` |
| `series` | T, `u` = window start + `lookback`; `since` is the clock at the end of which the element first had rows, sampled at the end of each clock (D3 on #159) |
| `series` with `fill` | the progress of a source is a recorded input, records (source, until), not a time: the greatest `until` per source (K), joined with the sources that feed the window (J), window end at or before `until` less the hop lag (L), and window end at or before the clock (T) |
| `series` with `freshness` | the no-data record of an element: T with `s = S + freshness`, `S` the later of `since` and the end of its newest record, until a newer record; it sits at the window that holds `S + freshness`. A roll-up in no data: K by the roll-up key, at the greatest window of its members' records |
| `hysteresis` | reads the window start of its rows; the look-back edge comes from the series upstream |

## Gaps

Fixed in #159: G1 link withdrawal (D4), G2 hysteresis rows per key and window, G3 series `since` (D3), G4 exact sums.

Found by this note (second pass, listed on #159 before code):

| # | Construct | What was wrong | Requirement | Now |
|---|---|---|---|---|
| G5 | decimals in `min`, `max`, `arg_max`, `arg_min`, keys, `distinct` | the output kept the digits of the first equal value that arrived | R1 | fixed with #161 (D7) |
| G6 | `series` with `fill` | the progress of a source looked like a second time | R8 | not a gap: progress is a recorded input (D7); see time |
| G7 | `series` no-data record | it sat at a window made from the clock | R8 | fixed (D8); no output changed, the window is not observable |
| G8 | windowed `summarize`, `hysteresis` without a declared lateness | the state is dropped at a host horizon, not at a lateness of the program | R1 | moved to #142 (D1 (b), D10): a lateness is declared only on rows inputs, and a `hysteresis` reads windows, which have none |
