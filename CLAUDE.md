# Design rules for this repository

- **Deep modules (John Ousterhout, A Philosophy of Software Design).** A module has a narrow, simple interface and hides a lot of work behind it. Prefer fewer, deeper modules over many shallow ones. Do not leak internals through the interface; do not add pass-through layers or configuration a caller must understand.
- **Grokking Simplicity (Eric Normand), as much as possible.** Separate data, calculations (pure functions) and actions (anything that depends on time or changes the world). Keep actions thin and at the edges; put the logic in calculations; treat data as immutable and copy on write where state must change.
- Count every new concept, type, setting or language addition as a cost. If two designs work, pick the one with fewer concepts.

# Model use

- Default: Claude Opus 5.5 or Sonnet 5.5 at the effort the task needs.
- Do not use Fable unless the user asks for it in words for that task.
