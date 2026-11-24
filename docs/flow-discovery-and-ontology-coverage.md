# Discover flows and show what the ontology missed

The local submission example follows two handoffs and checks the receiver against an owner’s rule. The kiln example follows a temperature change through its consumers and checks completion for the same batch. [Run the examples and tests](../engine/standard/README.md)

| Verified in the local checkout | Result |
| --- | --- |
| [Discover flows](http://localhost:5173/#guide=trace-submission) | Two parent–child handoffs for one submission. The separately reported count of four stays an aggregate. |
| [Inspect coverage](http://localhost:5173/#guide=inspect-coverage) | With the rules removed, the receipt still yields one eligible receiver, one unmapped item and zero supported bindings. The source is sampled. |
| [Correct a receiver](http://localhost:5173/#guide=review-receiver) | Publishing the correction matches an independent CLI replay. The old revision still shows the conflict. |
| [Edit health policy](http://localhost:5173/#guide=kiln-policy) | Changing the entry streak from two windows to four moves the first hot window from 10:01 to 10:03 UTC. |

**The Azure capture has a different scope.** The project’s own projections found two apps and four authored flows. Ten of twelve steps had a measurement. All four flow states were `Unknown`. Removing the flow declarations kept the two apps and produced zero named flows. Its Kusto query filters and groups dependencies before import. The standard trace and role-coverage projections run in the separate submission exercise. [Azure flow projection](../lab/health-mirror/definitions/mirror.workloadFlows.gkql.json) · [Captured dependency query](../lab/health-mirror/projects/ahm-model-demo/request-dependencies.kql)

**Why endpoint scoring fails.** In a local replay, four authored flows matched four of five reference flow-to-app links. Changing the required app class for *Submit a task* from web to MCP raised the score to five of five. The observed ingress was still web. “Requires” leaves the receiving and downstream roles unspecified. [Example comparison](../lab/health-mirror/compare-discovery.py) · [Current gap projection](../lab/health-mirror/definitions/mirror.ontologyGaps.gkql.json)

| Question in Canvas | Evidence that answers it |
| --- | --- |
| Who received this submission? | The request’s ingress identity resolves to web. |
| Did web then call the tool server? | A linked call identifies the caller and callee. Both apps appearing in one journey leaves that call unproven. |
| Did the task finish? | A completion event or verified postcondition for the same task. An accepted submission proves acceptance. |

## 1. Discover operations before anyone names the journey

The flow projection joins child spans to parents within the same source and trace. Each invocation has a source-scoped occurrence ID when the source supplies one. Recurring operation patterns have separate stable IDs. A pattern transition records whether its evidence comes from a correlated count, an inference rule or an owner’s declaration. [Flow discovery #148](https://github.com/abossard/sattle/issues/148) · [Discovery projection](../engine/standard/flows/definitions/sattle.flow.discover.gkql.json)

**Acceptance**

- Given a parent span with two children, emit both directed parent–child edges. Siblings do not acquire an edge from their timestamps. Keep shared steps and branches visible.
- Two unrelated operations with matching names, timestamps or service classes must yield zero observed handoffs. A suggested link identifies its rule and evidence. Accepting it records the owner’s assertion.
- Preserve the grain of the input. A counted operation stays a counted operation. An aggregate grouped by an explicitly correlated parent and child can support a pattern edge with a count. Neither result creates individual executions.
- Replay the same facts and rule version to the same closed event-time window. Shuffling admissible arrivals, redelivering an event or disabling AI yields identical semantic IDs, edges and counts. Two distinct retry attempts stay distinct where the source identifies them.
- A missing parent produces an unresolved observation. A later admissible parent or retraction updates that result and its lineage. The configured late-data policy accounts for events outside the window.

## 2. Make ontology coverage a native graph

Select a graph revision and a source, role and event-time window, such as receivers of requests in `[0,20)`. The projection selects receipts first and counts each distinct `(source, item, role)` once. Two receipt keys with times 10 and 11 yield one eligible request and two inspectable receipts. Canvas, the CLI and downstream projections read the same findings. [Ontology coverage #149](https://github.com/abossard/sattle/issues/149) · [Coverage fixtures](../engine/tests/Sattle.Tests/CoverageTests.cs)

**Acceptance**

- Inject a previously unseen operation into the adapter input with an empty project ontology. Its normalized observation must reach the master graph and appear as **unmapped**. Repeat with a new entity and a new relationship. A collector restricted to known operation names fails this test. Report upstream sampling and filtering limits.
- Count each eligible item once per role. A fixture with two mapped, one unmapped, one ambiguous and one explicitly excluded must show all five. Distinct targets for a single-valued role are ambiguous. Two rules agreeing on one target, or multiple memberships where allowed, are valid.
- Show the reason, owner and effective rule revision for an exclusion. Excluding an unmapped or ambiguous item moves it into the excluded count. Its place in the eligible population and its zero supported bindings stay the same.
- Also check the reverse direction. A declared step without qualifying evidence is **unverified**. A declared receiver and an observed, incompatible receiver for the same step and scope are **in conflict**. A receiver and a downstream participant can both be correct.
- Report input completeness separately. Adjacent or overlapping complete reports for the same source and role can cover a whole window. A gap in those reports yields `partial`. A missing report or an unavailable source yields `unknown` for a named scope. Overlapping filtered or sampled reports retain those limits. A complete window with zero eligible items is known empty. [Source reporting intervals](../paper/ch10-sources-and-adapters.md#reporting-intervals-in-the-coverage-graph)
- Follow a finding’s `BasedOn` edges to its receipts, including each available source reference. Each declaration records how its target compares with each receipt. Multiple participants can have distinct supported bindings for the same eligible item. After a correction, the earlier finding remains inspectable at its retained revision.

## 3. Review and correct a flow in Canvas

In the Inspector, open **Review rule** on a finding. The owner can leave it unresolved, bind it, correct its role or target, or record an exclusion with a reason. **Preview change** compares a private draft with the published graph. Follow the finding to its receipt, then publish or discard the draft from the open review. [Canvas review #150](https://github.com/abossard/sattle/issues/150) · [Review tests](../canvas/tests/coverage-review.test.tsx)

**Acceptance**

- Reproduce the *Submit a task* mutation. Changing a generic required class leaves observed receiver coverage unchanged. Declaring MCP as the sole receiver conflicts with observed web ingress for that invocation. Declaring MCP as a downstream participant allows both roles.
- Correcting the receiver removes that conflict while preserving the source observation. With evidence only for web ingress, a proposed downstream MCP call stays unverified.
- An optional assistant suggestion supplies a role, target and matching `sourceRef`. Accepting the suggestion fills the form. The owner previews the versioned rule and chooses **Publish rule**. The same review works with AI disabled.
- A preview stays open while the owner follows a receipt. Once the owner publishes, closing the Inspector lets that publication finish. Rebinding the rules input updates the file Canvas uses for the next review. After publication, Canvas and a fresh CLI replay agree on the findings and their evidence. [Live replay test](../canvas/tests/engine/coverage-review.test.ts)

## 4. Configure how evidence affects health

The kiln policy binds a metric series, entry and recovery thresholds, consecutive-window counts and a freshness limit. Its impact projection applies each dependency role to its consumer. Its progress projection checks terminal evidence against the batch identity. [Health policies #151](https://github.com/abossard/sattle/issues/151) · [Kiln policies](../engine/examples/kiln)

**Acceptance**

- On four consecutive breaching windows, changing the entry count from two to four moves the transition from the second window to the fourth. Recovery requires its own configured streak. Silence past the configured freshness limit produces the declared missing-data state. [Existing hysteresis replay](../scripts/compare-tutorial-health.mjs)
- A required and an optional dependency may reference the same app. A degraded app affects each consumer according to its impact rule. Both refer to the same observed metric.
- A successful request can satisfy the submission step. Completion requires terminal evidence for the same task. Another task finishing leaves the first task’s outcome unknown.
- Each state change identifies its input window and policy revision. With the clock at 10:10, deliver admissible readings for 10:08 and 10:09 after silence. Either arrival order restores `Ready` with `changedIn` at 10:08. [Health policy tests](../engine/tests/Sattle.Tests/HealthPolicyTests.cs)

## Where it belongs

| Layer | Responsibility |
| --- | --- |
| Adapter | Preserve source identity, correlation, event time, aggregation grain and available completeness information. |
| Engine and standard GKQL | Compute the versioned flow and coverage graphs, identities, retractions and lineage. Reuse existing graph matching, set difference, drafts and hysteresis. |
| Project ontology and rules | Name journeys, bind roles, declare legitimate exceptions, define success and choose impact policies. |
| Canvas | Inspect and edit the engine’s results. A tree is an optional navigation view of the graph. |

The printshop fixture feeds press tickets through its own adapter contract and reuses the same coverage projection and rules. The coverage graph retains each receipt alongside the rule that reviewed it. The owner edits that rule through the engine’s draft interface. [Printshop fixture](../engine/tests/Sattle.Tests/CoverageTests.cs) · [Receiver example in the white paper](../paper/ch09-evolution-evidence-provenance.md#worked-example--reviewing-a-receiver) · [Draft interface](../engine/src/Sattle.Runtime/Drafts.cs)

## How to test the 80% ambition

Freeze the discovery rules before opening an unseen workload. Compare the first draft and the owner-corrected graph with independently reviewed **typed transitions**, including direction, role and supporting evidence. Report precision, recall, missed critical edges, unknown outcomes and time spent correcting the draft. Score observed operations and named business flows separately.

For the captured demo, assigning web to every named journey already matches four of five endpoint pairs. An 80% claim needs a stated denominator and a result better than that simple baseline on the same task. Test health policies separately on incident and recovery windows.
