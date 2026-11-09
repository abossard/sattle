# Sattle graph projection atlas

The site presents the master graph, stacked and combined projections, graph changes, ontologies and document projections in 21 animated concept pages.

- `dist/`: self-contained static site, including C# reference pages and design download.
- `tools/story.py`: 21 concepts, 84 visual phases, semantic colors and change markers.
- `tools/build-atlas.py`: generates lesson and source-reference pages from actual contracts.
- `contracts/`: five .NET contract assemblies, generated schemas, fixtures and test-only reference model.
- `OPEN-WORK.md`: current site checklist. `contracts/OPEN-WORK.md`: open design work.

Production engine behavior is not implemented.

## Preview

Serve `dist` with any static web server. For example, from this folder:

```sh
python3 -m http.server 8000 --directory dist
```

## Regenerate and verify

Python 3, Node.js and .NET 10 are used. Install `contracts/requirements.txt` for schema checks.

```sh
python3 tools/package-design.py
python3 tools/build-atlas.py
node --test tests/atlas.test.cjs tests/content-projection.test.cjs
python3 contracts/tools/verify.py
```

The C# build treats warnings as errors. The finite reference model and selected schema/compiler cases establish the scope described in `contracts/docs/VERIFICATION.md`.
