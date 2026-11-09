# Language counter-review

- [x] Reviewed all 21 lessons, 84 frame sequences and generated page copy.
- [x] Replaced slogan headings with descriptions of the operation being explained.
- [x] Removed “promises”, “honestly”, vague “meaning” claims and repeated marketing instructions.
- [x] Rewrote rhetorical “this, not that” contrasts as direct rules, required inputs or validation outcomes.
- [x] Preserved mathematical order sensitivity, rejection cases and other distinctions needed to understand the contracts.
- [x] Kept implementation status once on the Contracts page and removed its repetition beneath every lesson excerpt.
- [x] Clarified replay determinism requires the same initial state, rules, ordered inputs and time controls.
- [x] Clarified generated ontology-specific C# types are planned, and general correctness and production performance remain unverified.
- [x] Verified both Python files compile and the story still contains 21 lessons with four frames each.

Edits are limited to user-facing prose strings in `tools/story.py` and `tools/build-atlas.py`. The parent will regenerate the pages. No contract symbol, test count, diagram data key or generation behavior changed.

The diagram for the five input families still needs the parent's semantic check against adapter schemas: the Hubble example displays a `verdict` property, while the principal ontology fixture may use `health`. The example should either declare its illustrative property mapping or use the field emitted by the checked-in fixture.
