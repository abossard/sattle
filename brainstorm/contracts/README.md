# Sattle — contract design 0.1

Compilable C# contracts and portable schemas for graph and document projections. Production behavior is intentionally unimplemented. A separate finite mathematical reference model tests selected laws and counterexamples.

Start with [the architecture](docs/ARCHITECTURE.md), [the visual](docs/architecture.svg), [verification evidence](docs/VERIFICATION.md), and [archived open-work notes](https://github.com/abossard/sattle/blob/main/brainstorm/dist/evidence/OPEN-WORK.md). The notes are hosted in the repository, not included in the downloadable contract package.

## Verify

Requires a .NET 10 SDK, Python 3.11+ and jsonschema 4.x. `global.json` pins SDK 10.0.100 and rolls forward only within 10.x; SDK 11 alone does not satisfy it. The separate engine targets net11.0. The C# contract solution has no NuGet dependencies.

```sh
python -m pip install -r requirements.txt
python tools/verify.py
```

Set `DOTNET` to an absolute SDK executable path if it is not on PATH. The script builds the solution, runs the finite reference model, regenerates JSON schemas from C# DTO reflection, validates examples and rejection cases, and compiles positive and intentionally invalid C# examples. It writes reports into docs/.

## Package

- src/: five assemblies containing records, enums and interfaces, with no production operational method bodies.
- schemas/: JSON Schema 2020-12 definitions generated from the same C# DTOs.
- examples/: graph/document definitions, ontology, changes, a rendered Markdown example and native source fixtures.
- tests/: finite mathematical oracle, schema and selected semantic checks, compiler rejection tests.
- docs/: architecture, decisions, source research, review results and verification limits.

## Deliberate boundaries

All operational service ports accept async streams. Stored and virtual graph access share a contract. Complete finite graph diffs and symbolic virtual-definition patches are distinct profiles; the symbolic extension is still a proposal to confirm. Runtime-loaded ontology and projection definitions require semantic validation beyond JSON Schema. Document structure has stable IDs and reversible changes; renderers produce a versioned flat representation.

This is a design iteration for review. It is not a runnable telemetry service, a complete graph language, a full TGG/lens implementation, or proof of production scaling.
