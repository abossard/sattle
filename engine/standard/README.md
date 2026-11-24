# Follow a submission and correct its receiver

The trace for `submit-42` shows two calls from `web.submit`, one to `queue.enqueue` and one to `audit.write`. The ingress receipt names `web` as the receiver. The project rule names `worker`. You can correct the receiver and check what the evidence says about the expected downstream call.

## Try it in Canvas

Use Python 3, the .NET 11 SDK and Node.js for Canvas. From the repository root, run these commands in separate terminals. The first command stages the standard projections, two project rules and nine synthetic records in a temporary directory. Published edits last for the life of this example server.

```sh
python3 scripts/review-flow.py serve
```

```sh
npm --prefix canvas install
npm --prefix canvas run dev
```

Canvas connects to the submission replay on port 5085. To use a different port, pass `--port` to the replay and set `FLOW_REVIEW_PORT` to the same value when starting Canvas.

| Canvas chapter | Try it | Check the result |
| --- | --- | --- |
| [Follow one submission](http://localhost:5173/#guide=trace-submission) | Select each handoff in the guide. | Both arrows start at `web.submit`. The children are `queue.enqueue` and `audit.write`. The separate count of four calls has no individual span IDs. |
| [Find what the rules missed](http://localhost:5173/#guide=inspect-coverage) | Inspect **Receiver scope**, then the observed receiver and declared downstream role. | One eligible receiver in a sampled source. The receipt names `web`. The downstream declaration is `unverified`. |
| [Correct the receiver](http://localhost:5173/#guide=review-receiver) | Choose **Inspect receiver conflict → Review rule**. Set **Target** to `web` and choose **Preview change**. | The preview shows `conflict → mapped`. **Publish rule** applies it. **Discard draft** keeps the published graph. |

From a conflict, follow **Derived from** to the coverage finding and expand **BasedOn** in Inspector. The receipt at `trace/42/ingress` identifies the receiver. The worker call still needs its own receipt. Note the current `coverage` revision to revisit it after publishing.

To retrieve the original finding after publication, open `/graphs/coverage/snapshot?revision=N` at the engine, replacing `N` with the revision you recorded. The revision selects the snapshot even when two edits share a logical time.

For an exclusion, select an **observation finding** in `coverage`, choose **Review rule** and then **Exclude with reason**. Enter the owner and reason. Preview the eligible, excluded and supported-binding counts. An explicit exclusion can move an ambiguous item to `excluded` while keeping it in the eligible population. **Leave unresolved** closes the form before a draft is created.

**Bind observation** assigns a classification. **Declare role** compares the named target with the receipt. Use the receiver declaration to check who accepted the submission.

When the finding shows a source reference, you can paste an assistant suggestion with the same `sourceRef`. Applying it fills the role and target fields. You decide whether to preview and publish.

## What a project owner supplies

| Question | Where to make the change | What the reader can inspect |
| --- | --- | --- |
| Which operations handed work to each other? | Feed source-scoped spans, correlated counts or versioned claims into the [flow module](flows/definitions/sattle.flow.module.gkql.json). | Directed occurrences, shared patterns, cited lineage and unresolved parents. |
| What does my ontology explain? | Put the project's binding, declaration or exclusion in the [rules table](coverage/sattle.coverage.rules.gkql.json). Select the source, role and time interval on the [coverage projection](coverage/sattle.coverage.coverage.gkql.json). | Each eligible item, each declaration, disagreements, exclusions and source limits at the selected revision. |
| When should health change? | Bind the metric and edit the [kiln policy](health/workshop.rating.gkql.json). Its [impact policy](health/workshop.impact.gkql.json) chooses how each dependency affects its consumer. | The input window and the catalog revision for a transition, and both uses of the same instrument. |
| Did the work finish? | Match the submission and terminal receipt on the [batch identity](health/workshop.progress.gkql.json). | Submission and completion for each task. |

## Replay coverage with an empty policy

This capture contains one ingress receipt and one sampling report. With an empty rules table, expect one eligible receiver, one unmapped item and zero supported bindings. The scope reports `sampled`. The combined walkthrough adds the two owner declarations to this same evidence.

Run `python3 scripts/review-flow.py empty` for the counts. To keep the output graphs, run the CLI directly.

```sh
dotnet run --project engine/src/Sattle.Cli -- replay "$PWD/engine/examples/coverage/capture" \
  --defs "$PWD/engine/standard/coverage" --space coverageReview --out /tmp/sattle-empty-coverage
```

Inspect `coverage.final.snapshot.json` in the output directory. Each `BasedOn` edge links a finding to a receipt. Two receipts for the same source, item and role count once in the eligible population, even if they arrived at different times inside the selected interval. In a record row contract, the reserved Text field `$recordKey` reads the envelope’s `payload.key`. Coverage stores it as `receiptKey`, so distinct records can cite the same source reference.

Set parameters on the coverage graph’s space binding to narrow the query. For example, `"scopeSource": {"lit":"requests"}` and `"scopeRole": {"lit":"receiver"}` select ingress observations. The interval runs from `fromTime` inclusive to `untilTime` exclusive in Unix milliseconds. Two adjacent complete reports can certify the full interval, including a population of zero. A gap in the complete reports gives `partial`. An unavailable source or a named scope with no report gives `unknown`. The [source contract](../../paper/ch10-sources-and-adapters.md) defines the precedence of overlapping reports.

<a id="replay-the-kiln-health-policy"></a>

## Follow a kiln reading through a health policy

This synthetic capture has 30 local records, one chamber probe and two batches. Start the example on port 5086 from the repository root. It uses a temporary copy of the GKQL definitions.

```sh
python3 scripts/kiln-guide.py serve
```

| Open in Canvas | Try it | Check |
| --- | --- | --- |
| [Change when a reading expires](http://localhost:5173/#guide=kiln-policy) | Open the projection editor → **GKQL**. Change `freshness` from `2m` to `10m`, choose **Check & store**, then **Preview**. | One rating changes. The latest chamber state is `No reading` in the published graph and `Ready` in the draft. Discard the draft. |
| [Trace one reading to two consumers](http://localhost:5173/#guide=kiln-impact) | At impact revision 2, inspect both `Uses` edges. | Both end at the same `Hot` probe. The firing line is `Hot` and the display is `Ready`. |
| [Prove a batch finished](http://localhost:5173/#guide=kiln-completion) | Compare progress revisions 1 and 2. Follow batch-A’s `SupportedBy` edges. | B finishes first. A’s completion stays `Unknown` until its own `cooled` receipt at 10:05:15 UTC. |

Replay the same readings against two entry policies from a second terminal.

```sh
python3 scripts/kiln-guide.py compare
```

| High windows required | First hot window UTC | Recovery window UTC | First stale window UTC |
| --- | --- | --- | --- |
| 2 | 10:01 | 10:06 | 10:09 |
| 4 | 10:03 | 10:06 | 10:09 |

Each timestamp is the start of the one-minute window that established the new state. Three low windows restore `Ready`. In a rating patch, `changedIn` identifies the evidence window and `cause.configurationVersion` identifies the policy revision. [Recorded input](health/capture/kiln.ndjson) · [Rating](health/workshop.rating.gkql.json) · [Consumer impact](health/workshop.impact.gkql.json) · [Batch completion](health/workshop.progress.gkql.json)

## Where this fits in the paper

The span key comes from canonical identity (Ch. 1). Joins, grouping and signed changes explain why a late parent resolves a handoff and why its retraction removes that support (Chs. 4–6). Chapter 9 connects source evidence, owner declarations and a retained graph revision in one worked example. The source contract determines what an empty result means (Ch. 10). Canvas reads and edits those definitions through the existing viewer and draft interfaces (Ch. 12). [Worked example](../../paper/ch09-evolution-evidence-provenance.md#worked-example--reviewing-a-receiver) · [Source contracts](../../paper/ch10-sources-and-adapters.md) · [Viewer](../../paper/ch12-outputs-documents-and-canvas.md)

Health belongs at the measurement-to-graph crossing (Ch. 7). A later reading in the same state leaves the crossing window unchanged. An admissible correction can move that window while leaving the final state label unchanged. A graph that exposes the window will then revise its evidence. [Measurements and thresholds](../../paper/ch07-telemetry-plane.md)

## Design and verification plan

Ousterhout’s deep-module principle determines what the owner edits here. One row names a role and target. The standard projection owns the joins, counting and conflict detection. In Normand’s terms, receipts and rules are data, projection evaluation is a calculation, and receiving input or publishing a draft is an action. The Karpathy-inspired coding guidelines informed the harness. Each step below has a counterexample that would expose a wrong result. [Ousterhout](https://web.stanford.edu/~ouster/cgi-bin/book.php) · [Normand](https://www.manning.com/books/grokking-simplicity) · [Karpathy-inspired coding guidelines](https://github.com/multica-ai/andrej-karpathy-skills/blob/main/CLAUDE.md)

1. Give the owner one durable identity for an operation pattern and separate identities for its executions. Join only on source-provided correlation. Replay shuffled arrivals, retries, duplicates, withdrawals, aggregates and late parents. Compare directed edges, source references and the closed-window digest. [Flow tests](../tests/Sattle.Tests/FlowDiscoveryTests.cs)
2. Count the scoped population before applying project rules. Compute findings in both directions and record the source's reporting limits. Verify the five-item reconciliation, two sources, conflicting roles and a historical query after correction. [Coverage tests](../tests/Sattle.Tests/CoverageTests.cs)
3. Let the viewer read the findings and edit one small GKQL rule table. Keep evidence ingestion, draft replay and publication at their existing boundaries. Drive the form with an unrelated source, then use a real engine to preview, publish, read the old revision and compare the new graph with an independent CLI replay. [Form tests](../../canvas/tests/coverage-review.test.tsx) · [Live test](../../canvas/tests/engine/coverage-review.test.ts)
4. Keep project health decisions in definitions. Feed the same recorded windows to two policies and check exactly which transition moves. Verify expiry, dependency impact and terminal evidence by identity. [Health tests](../tests/Sattle.Tests/HealthPolicyTests.cs)

Run the acceptance harness from the repository root.

```sh
dotnet test engine/tests/Sattle.Tests --filter 'FullyQualifiedName~FlowDiscoveryTests|FullyQualifiedName~CoverageTests|FullyQualifiedName~HealthPolicyTests'
npm --prefix canvas test -- --project pure coverage-review.test.tsx engine-source.test.ts
npm --prefix canvas test -- --project engine tests/engine/coverage-review.test.ts
```

To keep published edits after stopping the example server, prepare a directory of your own and serve its definitions. Run from the repository root.

```sh
review_dir="$(mktemp -d)"
python3 scripts/review-flow.py prepare "$review_dir"
dotnet run --project engine/src/Sattle.Cli -- serve "$review_dir/capture" \
  --defs "$review_dir/definitions" --space flowReview --write-definitions --port 5085
```

Published rule changes are saved under `$review_dir/definitions`. Pass the same capture, definitions and space to `replay` to read the saved result.
