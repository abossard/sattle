# Sattle graph projection atlas

The site presents the master graph, stacked and combined projections, graph changes, ontologies and document projections in 21 animated concept pages.

- `dist/`: self-contained static site, including C# reference pages and design download.
- `tools/story.py`: 21 concepts, 84 visual phases, semantic colors and change markers.
- `tools/build-atlas.py`: generates lesson and source-reference pages from actual contracts.
- `contracts/examples/Sattle.Examples/`: compile-checked callers of the public interfaces; excerpts appear in every lesson and on `code-examples.html`.
- `contracts/`: five .NET contract assemblies, generated schemas, fixtures and test-only reference model.
- `dist/evidence/OPEN-WORK.md`: the checklist of completed work, remaining decisions and deferred implementation (the only copy in the repository).

Production engine behavior is not implemented.

## Preview

Serve `dist` with any static web server. For example, from this folder:

```sh
python3 -m http.server 8000 --directory dist
```

## Regenerate and verify

Python 3, Node.js and a .NET 10 SDK (not the engine's .NET 11; `contracts/global.json` pins 10.0.100) are used. Install `contracts/requirements.txt` for schema checks.

```sh
python3 tools/package-design.py
python3 tools/build-atlas.py
node --test tests/atlas.test.cjs tests/content-projection.test.cjs
python3 contracts/tools/verify.py
```

The C# build treats warnings as errors. The finite reference model and selected schema/compiler cases establish the scope described in `contracts/docs/VERIFICATION.md`.
