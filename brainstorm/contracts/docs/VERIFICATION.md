# Verification evidence — iteration 0.1

Latest verification: 10 November 2026 with .NET SDK 10.0.100, targeting net10.0 / C# 14. Warnings are errors. C# uses only the base class library. Reproduce with `python tools/verify.py` after installing requirements.txt.

| Check | Result | What this establishes |
|---|---|---|
| Build | 0 errors, 0 warnings | All five contract assemblies and test runner compile together. |
| Caller example project | 0 errors, 0 warnings | 19 source excerpts call the actual contract types. They do not execute a production engine. |
| Finite mathematical oracle | 14 groups; 1,921 assertions passed | Selected laws and counterexamples hold for this finite model and these generated cases. |
| Schema and selected semantic checks | 47 passed | Examples fit generated wire schemas; tested invalid shapes and selected semantic mistakes are rejected. |
| C# compile cases | 8 passed | Two valid cases compile; six invalid ID, property, predicate, row, patch/delta and edge-type combinations fail compilation. |
| Independent review | Architecture, contracts, master graph and site story | Concrete missing contracts and misleading evidence claims were challenged and corrected. |
| Diagram | Rendered and inspected | Labels and relationships fit the exported diagram. |

## Model coverage

Ordered patch apply/invert/compose; noncommutativity and stale preconditions; signed-delta identity, inverse, associativity and commutativity; explicit-base materialization; graph-invalid negative/conflicting weights; chronology loss; identity-preserving edge retyping; unique edge tuples and dangling endpoints; finite positive reachability; withdrawal and alternate support; finite-scope stored/virtual agreement; illustrative lens GetPut/PutGet; stable document reordering and inverse; Markdown escaping; polymorphic wire round trips; property-map order invariance; 200 generated graph transitions and 300 generated delta triples.

Seeds are fixed (63421 for delta vectors, 73411 for graph transitions). Detailed names and outcomes are in reference-test-results.json, schema-test-results.json and compiler-test-results.json.

## Strictness and its limits

The generated JSON Schemas are closed tagged unions with required fields, rejection of unknown properties, tagged value types and selected scalar constraints. They are generated from DTO reflection, so schema shape and C# shape share a source. This does not make a deserialized proposal semantically valid. Reference resolution, uniqueness, units, field ownership, cross-record consistency and recursion admissibility still need validation.

Twelve strict schemas are generated from the DTOs, including GraphSpaceDefinition. Composition checks exercise source-local ontology lookup, target emission, master reachability, unique graph/projection IDs, input bindings and source pin invalidation. They are selected semantic cases, not full compiler coverage.

The Python semantic oracle checks the provided input/match/map/emit examples and selected invalid cases. Its recursion checks inspect a small operator whitelist, finite-domain binding name, result references and union policy. They do not prove finite-domain closure, monotonicity or compiler correctness. The separate reachability model demonstrates convergence on finite positive examples.

The support-retention test demonstrates a finite support set. Lens tests use one simple complement-preserving lens. Virtual equivalence tests compare a finite scope and demonstrate a symbolic membership change. None is a general theorem prover.

Native adapter fixtures and mapping-scenarios.yaml are specifications only: no parser or engine is executed for them. Markdown reference rendering covers headings, paragraphs and tables. Other document nodes and renderer formats have contracts only.

## Unverified production properties

No production engine is implemented. No evidence is claimed for distributed transactions, persistence, arbitrary source compatibility, all TGGs or lenses, arbitrary virtual graphs, general recursion, runtime DSL compilation, generated ontology code, throughput or bounded memory. OPEN-WORK.md tracks those obligations.

The schemas for symbolic patches, identity bindings and ontology transitions express proposed contracts; the finite oracle does not implement every variant. A passing schema check is not proof of a lawful transformation.
