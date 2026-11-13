# Design rules for this repository

- **Deep modules (John Ousterhout, A Philosophy of Software Design).** A module has a narrow, simple interface and hides a lot of work behind it. Prefer fewer, deeper modules over many shallow ones. Do not leak internals through the interface; do not add pass-through layers or configuration a caller must understand.
- **Grokking Simplicity (Eric Normand), as much as possible.** Separate data, calculations (pure functions) and actions (anything that depends on time or changes the world). Keep actions thin and at the edges; put the logic in calculations; treat data as immutable and copy on write where state must change.
- **Finite state machines where applicable.** When behaviour depends on a mode or a sequence of events (a connection, a draft, a session, a window, a health state, a UI flow), model it as an explicit state machine: a closed set of states, a pure transition function `(state, event) -> state`, and the actions run at the edges. No scattered flags whose combinations mean a state.
- **Map/reduce shape, incremental and partitionable.** The engine computes on changes, not on whole graphs: every step takes a batch of changes (rows with a weight of +1 or -1) and returns the changes to its output. Steps without state are maps; steps with state are reduces keyed by a declared key, so their state can be split by key across threads or machines. Each step keeps only what it needs to turn the next input change into an output change, and knows exactly which keys, elements and windows a change touches; nothing is recomputed that no change reached. Shared indexes (arrangements) are the cache.
- Count every new concept, type, setting or language addition as a cost. If two designs work, pick the one with fewer concepts.

# Model use

- Default: Claude Opus 5.5 or Sonnet 5.5 at the effort the task needs.
- Do not use Fable unless the user asks for it in words for that task.
