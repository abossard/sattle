# Appendix: Artifact

This appendix is the only part of the paper that names programs, files and commands. It says where the software is, which part of it implements which chapter, and how to repeat each experiment of chapter 14.

## A.1 The release

- **Repository:** the Sattle repository in which this paper lives (the folder `paper/`).
- **Release:** *to be named at publication.* The experiments of chapter 14 are to be repeated on that release; its name will be entered here.
- **License:** see the file `LICENSE` at the root of the repository.

## A.2 What is in the repository

| Folder | What it is | Chapters it implements |
|---|---|---|
| `engine/src/Sattle.Core` | values and their canonical form, names of entities, the fixed core of types | 1, 2 |
| `engine/src/Sattle.Language` | the text form of definitions, its parser, printer and checker | 2, 5 |
| `engine/src/Sattle.Runtime` | graphs and versions, changes, step-by-step evaluation of projections, measurements, editable views, change of definitions, readers of sources, stores, restart | 3, 4, 6, 7, 8, 9, 10, 11 |
| `engine/src/Sattle.Cli` | the command line program `sattle` and the server that the viewer talks to | 10, 12 |
| `engine/definitions` | the definitions used in the experiments: types, naming rules, projections, the levels and the health model | 2, 5, 7 |
| `engine/tests/Sattle.Tests` | tests of the engine | 14 |
| `engine/tests/Sattle.Reference` | the independent, simple implementation written from the definitions of the paper | 6, 14 |
| `engine/tests/Sattle.Differential` | the comparison of the engine with that implementation on generated inputs | 6, 14 |
| `canvas/` | the viewer, a browser application | 12 |
| `lab/` | the description of the recording environment, the recording tools, and small scrubbed samples of recordings (`lab/fixtures`) | 10, 14 |
| `paper/evidence/` | the output of the runs that chapter 14 cites, one text file per run | 14 |
| `paper/inventory.md` | every numbered statement of the paper with its proof status | all |
| `paper/research/` | working documents: reviews, investigations, earlier states of the text | none |

## A.3 What can be repeated, and what cannot

- **Can be repeated from the repository:** every test; every experiment on the samples in `lab/fixtures` and on the synthetic estate in `engine/tests/Sattle.Tests/fixtures/estate`; the comparison with the independent implementation; the synthetic benchmarks; the checks of the viewer.
- **Cannot be repeated from the repository:** the experiments on the two real recordings. The recordings name real resources and are not published, and the cluster they were taken from no longer exists. For these the paper rests on the output files in `paper/evidence/`, in which real names are removed.

## A.4 Requirements

- .NET 10 for the engine.
- Node.js and a Chrome browser for the viewer and its checks.
- Optional: the Azure Cosmos DB emulator for the tests of that store.

## A.5 Commands

All engine commands run in the folder `engine/`; `sattle` stands for `dotnet src/Sattle.Cli/bin/Release/net10.0/sattle.dll`. All viewer commands run in the folder `canvas/`.

**Build.**

```bash
dotnet build -c Release
```

**Check the definitions** (chapters 2 and 5):

```bash
sattle check
```

**Tests of the engine** (chapter 14). The first command runs the default set; the others add the slow tests and the tests that need a real recording.

```bash
dotnet test -c Release
```

```bash
dotnet test -c Release --settings tests/slow.runsettings
```

```bash
dotnet test -c Release --settings tests/capture.runsettings
```

**Comparison with the independent implementation** (chapter 6, chapter 14):

```bash
dotnet test tests/Sattle.Differential -c Release
```

**Replay of a recording** (chapter 14). `DIR` is a recording; `lab/fixtures` and the synthetic estate work without further files. The second form replays in batches of `N` records, which must give the same fingerprint. The third writes the resulting graphs as files.

```bash
sattle replay DIR --cluster NAME
```

```bash
sattle replay DIR --cluster NAME --chunk N
```

```bash
sattle replay DIR --cluster NAME --out OUTDIR
```

For the synthetic estate: `DIR` is `tests/Sattle.Tests/fixtures/estate` and `NAME` is `synth`.

**Serve a recording to the viewer** (chapter 12):

```bash
sattle serve DIR --cluster NAME --port 5080
```

**Restart from a store** (chapter 11): serve with a store, stop, and start again with the store alone.

```bash
sattle serve DIR --cluster NAME --store sqlite=space.db
```

```bash
sattle serve --store sqlite=space.db
```

**Benchmarks** (chapter 13, chapter 14). The file `engine/PERF.md` lists each benchmark with its options.

```bash
sattle bench
```

```bash
sattle bench estate
```

**Viewer: tests and checks** (chapter 12).

```bash
npm ci
```

```bash
npm test
```

```bash
npm run typecheck
```

The next two need a served engine (see above) and a development server of the viewer.

```bash
npm run walkthroughs
```

```bash
npm run a11y
```

## A.6 Where each figure of chapter 14 comes from

Each run that chapter 14 cites has an output file in `paper/evidence/`. The first line of a file says what was run. The file names carry a number that orders the runs in time; a later number is a later state of the software. Figures that were measured by others in the project and not repeated by the authors of the paper are marked as such in chapter 14.

## A.7 State of the software when the paper was last revised

These figures describe the software, not a result of the paper. They were obtained by the project's own checks before the revision of the text and were not repeated by the authors of the paper.

- Comparison with the independent implementation: 194 of 194 cases agree.
- Tests of the engine, every category: 755 passed, 2 skipped.
- A fresh copy of the repository, default test run: 153 and 603 tests passed in the two test projects.

## A.8 Statements and the tests that exercise them

The table maps each numbered statement to the tests of the engine that exercise it and to what remains open. Evidence classes: A, a proof in the paper; B, seeded random tests (4,000 cases per property); C, example or fixture tests; D, a benchmark; E, a replay of a real recording. Test names are those of the engine's test projects.

| Theorem | Paper | Test evidence | Open |
|---|---|---|---|
| 1.1 canonical equality | A | C: `Theorem_1_1_equality_iff_equal_encoding`, `Integers_and_decimals_of_equal_numeral_differ`, `Property_maps_encode_independent_of_supply_order`, `Rule_1_2_no_json_numbers_in_encodings`, `N4_lone_surrogates_are_rejected` (`ValueTests.cs`) | encoder against an independent JCS implementation (O1.1) |
| 1.4 allocation pure | A | C: `Theorem_1_4_allocation_is_a_pure_function_of_the_key`, `Identity_resolution_is_a_pure_function_of_the_key_and_aliases_share_one_entity` | collision resistance is an assumption |
| 1.5 to 1.7 timeless binding, migration, merge | A | bindings are an append-only table (`engine/README.md`). Merge is implemented (Ch. 1, D5): C: `Forwarding_chains_are_acyclic_finite_and_decrease_the_id_and_rep_is_idempotent` (2, 5, 12 identities), `Merge_changes_resolution_only_for_involved_keys_and_modifies_no_binding`, `The_survivor_does_not_depend_on_the_order_of_the_merges` (six orders), `Identities_of_different_entity_types_are_a_conflict_not_a_merge` (`IdentityMergeTests.cs`); `The_survivor_does_not_depend_on_the_order_of_the_merges` and `Forwarding_chains…` now count the own ids of the keys among the candidates (Proposition 1.8); `BatchInvarianceTests.cs`: `The_result_does_not_depend_on_the_order_in_which_identity_keys_are_bound` (one synthetic input, two orders, chunk sizes 1, 2, 5, 2,000, and an assertion that the merge count differs between the orders); E: one digest at five chunk sizes on both captures, with the same merge count at every size | O1.2 (concurrent projections, generated arrival orders), E10, Q10 |
| 2.1, 2.2, 2.4 conformance, kernel fixed point, digests | A (2.2 is a finite check, tested) | C: `Kernel_conforms_to_itself`, `Entity_type_is_its_own_type`, `A_broken_kernel_fails_conformance` (mutations), `Kernel_digest_equals_the_compiled_constant`, `Kernel_table_has_the_counts_of_section_2_3`, `Release_digests_are_reproducible_and_content_sensitive`. This discharges O2.1 (Q2) | cross-platform digests (Q13), Q1, Q7, Q11, Q12 |
| 3.1 to 3.7 | A | B: `Ch3_committed_states_are_well_formed_and_history_is_immutable`, `Theorem_3_3_equality_by_digest`. C: `Ch3_uniqueness_rejects_parallel_relationships_but_allows_other_types_and_orientations`, `Ch3_retype_keeps_the_relationship_id`, `Ontology_slot_is_an_ordinary_edit`, `Frontier_order` | |
| 4.1 to 4.9 (T1 to T4) | A | B: `T1_inverse_law`, `T1_inverse_reverses_composition`, `T2_associativity_and_application_respects_composition`, `T2_composition_is_partial_and_patches_do_not_commute`, `T3_signed_deltas_form_an_abelian_group`, `T4_delta_is_a_homomorphism_and_materializes`, `T4_4_edit_order_is_irrelevant`, `Squash_reports_loss_and_equals_the_net_composition`. C: `T4_delta_is_not_injective_and_not_closed_under_materialization`. The generator creates, changes, retypes and removes elements and guard-only edits (`Gen.cs`). Discharges O3.1 and O4.1 for the engine types (not the old contract types) | |
| 2.7 value roles are inert data | A, audit | C: `Nothing_about_health_is_in_the_engine_the_state_property_declares_value_roles_the_ontology_serves`, `Value_roles_parse_print_and_round_trip_and_reject_unknown_roles` (`DeployHealthTests.cs`); `Kernel_table_has_the_counts_of_section_2_3` asserts 54 concrete definition types and 20 operator kinds | the audit that no code reads a role is a source search done once (evaluation 2), not a test |
| 4.10, 4.11 drafts, undo as inverse patch | A | C: `Structural_edits_print_canonically_and_undo_is_an_inverse_patch_not_a_rewind` (`DraftTests.cs`); canvas `editor-session.test.ts` (undo and redo as inverse patches, 99 steps, rejected patch changes nothing) | O4.3 (generated sequences) |
| 5.7 refactoring preserves outputs | proved under premises R0 to R5 | C: 10 cases in `RefactorTests.cs`: 2 extractions with union equality and text round trip, one rule move, 7 of 8 rejection codes in 6 cases (Ch. 5, 5.7), one extraction that copies the parameters it uses. One module | O5.5, `CrossModule`, the condition (3) |
| 5.8 parameters are constants of the binding | example tests | C: `A_parameter_is_a_constant_of_the_space_binding` (two values, two results), `A_parameter_binding_must_give_every_parameter_a_constant_of_its_kind` (three rejections) (`OperatorTests.cs`); `deploymentHealth` bound with `minReady` 0 and 1 (`DeployHealthTests.cs`) | generated bindings |
| 7.7, 7.8 health roll-up and bounded reach | proved on the model, proved | C: `Each_scenario_gives_the_state_it_should` (five scenarios on the `estate`: an overloaded cache, a crash-looping pod, a database down, a failing gateway, a resource without data), `An_observed_call_carries_no_failure_API_Management_needs_api_and_not_the_other_way`, `The_workload_graphs_do_not_depend_on_the_chunk_size_and_conform_to_their_ontology` (`WorkloadTests.cs`), `The_workload_roll_up_has_a_component_that_is_not_healthy_because_of_something_it_depends_on` (`EstateTests.cs`), `Traverse_reaches_to_the_depth_and_follows_edge_node_and_property_changes` (`OperatorTests.cs`); E: both real captures build the workload graphs with no dangling edge (`On_a_real_capture_the_workload_graphs_build_with_no_dangling_edge`); with no authored signal rules every state there is Unknown | generated topologies |
| 5.1, 5.2 type checking, soundness | A, proved under premise KS | C: `Examples_parse_check_and_activate_cleanly`, `Faulty_definitions_report_typed_errors_with_positions` (typed errors with positions), `Kernel_checks_fire_on_broken_graphs`, `Import_cycles_are_rejected`, `Dependents_of_a_broken_module_are_not_activated`; `sattle check` exits 0 on the twelve modules (`paper/evidence/check.txt`) | generated well-typed projections (O5.1) |
| 5.6 lossless syntax | proved under premise RT, on canonical graphs | C: `Print_then_parse_reproduces_the_graph_and_printing_is_idempotent` on the example modules, `Was_clause_keeps_the_old_id`, `Ids_follow_the_containment_path_and_carry_no_version` | generated definition graphs (Q1, Q8) |
| 6.1 incremental correctness (T5) | sketch + citation | C: integrated output equals the final graph and equals the output under other chunkings (the T6 test), on fixtures and on the real capture (14.5) | random operator sequences with retractions per kernel operator (O6.1) |
| 6.2, 6.3 (T6, T7) | A, with premises B1 to B4 (B4 added at gate 1a, no targeted test) | see 14.5: C for B1 and B2 (`BatchInvarianceTests.cs`, in the default run; absent in evaluations 4 and 5); E for both at five chunk sizes on both captures and in the capture category of the test run (`Every_capture_gives_the_same_state_digest_at_chunk_sizes_1_50_and_100000`); B3 on examples, on one two-order synthetic input and on both captures, no generator | O6.2, O6.7 |
| 5.9 retention is a function of the frontier | proved | C: `The_master_keeps_exactly_the_evidence_younger_than_the_age_at_the_frontier_on_the_fixtures` (ages expiring 25, 50 and 90 percent), `Retained_state_and_digests_do_not_depend_on_the_chunk_size_on_the_fixtures` (1, 7, 50, 100,000); E: `Retention_is_batch_invariant_and_bounds_the_master_on_every_capture` (category `Capture`, chunk sizes 1, 50, 100,000, run by this paper); master size against age in `retention-ages.txt` (`RetentionTests.cs`) | a row that is expired on arrival and the retraction of a held row have no test of their own that this paper found; generated sequences |
| Def. 5.6 aggregates per context (D26) | engine rule | C: every aggregate in a `Group` over data and in a `Rollup` over a delta value is accepted exactly when the palette lists the context (`PaletteAggregateTests.cs`); the matrix of `sum`, `min`, `max`, `mean` over a delta, a gauge and a cumulative value through the checker, with the refusal messages (`RollupTemporalityTests.cs`) | values of a `Rollup` over a gauge or a cumulative value on data |
| 11.9 restart reproduces the state | proof sketch | C: digest, `/space` and sequence equal after a restart (memory, SQLite; Cosmos not run by this paper), resume by sequence number, definition commit survives, a store that disagrees with its journal is refused (`RestartTests.cs`); E: restart of the `deploy-health` fixture end to end with byte-equal `/space` and document (`restart-head3.txt`), without and with a seal (`restart-head4.txt`, `restart-head6.txt`), and of the journals of both real captures (`restart-real-head4.txt`, `restart-real-head6.txt`); a restart that says what it recomputed, and a smoke test of restart, seal and live resume in the default run (`RestartSmokeTests.cs`). D: H10 (13.6) | kill during ingestion, a journal that is cut in the middle of an entry, Cosmos at one hour |
| 11.10 restart from a state seal | proof sketch | C: a restart from a seal replays only the tail and gives the same space (memory, SQLite), the space continues after a seal restore as after a full replay, a seal with a wrong digest, from another build or with truncated bytes is refused and the journal replays, a definition commit before the seal survives, the commits after the seal are verified against the store (`SealTests.cs`); a snapshot loaded into a fresh engine saves to the same bytes and the engine continues like the original at five points of two inputs (`SnapshotTests.cs`); E: a fixture end to end with a seal (`restart-head4.txt`); **the journal before the seal is not recomputed** (the documented trust model), shown by a deleted entry that a seal restart does not notice and that `--verify-journal` finds (`restart-seal-prefix-head4.txt`, `restart-seal-prefix-head6.txt`; `An_entry_deleted_from_the_journal_before_the_seal_is_not_seen_by_a_seal_restart_and_is_found_by_the_verifying_one`). D: H10 with seals (13.6) | damage that keeps the state digest equal, a seal on Cosmos at scale |
| 10.8 a killed live run resumes to the state of an uninterrupted run | proved under premises LR1 to LR4 | C: a fake source that crashes after 60 percent of the shuffled envelopes of the `estate` fixture plus 7, resumed from memory and SQLite, with a seal every 30 entries and without: same digest and row count as the uninterrupted run, exactly the remaining events consumed, a second restart the same; 9 redelivered events dropped by key; a record that is not an envelope rejected with its position kept (`LiveResumeTests.cs`); a kill at each of the four steps of three envelopes, with the recording, the journal and the digest whole after the next start, and a recording completed from the journal (`LiveCrashTests.cs`) | the Event Hubs source and a real hub (no cluster exists), more than one partition, a redelivery of more than 20,000 envelopes, a process kill |
| 5.10 to 5.12 levels and edge lifting | A on the model, for an image that is a function: declared and enforced by the lift for `Realizes`, a premise for `Backs` | C: `ProjectionEdgesTests.cs` (28 cases): every graph whose ontology declares relationship types has relationships on the `estate`; lifted `callFlows` equal the service-level `flows`; six variants for self-loops, an end without an image, a retired capability and parallel edges with summed flows; the static files hold the edges; two lifts equal a one-step lift from the pods in edges and sums (2 cases); the ratio of a lifted edge is the lifted drops over the lifted flows in every window and not the mean of the ratios, and its state follows the thresholds; a service with two images is not lifted, no call is counted twice and conformance reports `Cardinality`; `LiftTests.cs` (36 cases, read and not run by this paper; in set A's run at evaluation 10): the `lift` statements give the entities, relationships, properties, telemetry values and state digest of the hand-written statements on the estate, `lab/fixtures` and the newest capture, the same digest at chunk sizes 1, 50 and 100,000, one case per clause, the printer round trip and sixteen refusals. E: three levels on the `estate` (`estate-head9.txt`: master 47 entities and 48 relationships, `serviceTree` 4 and 5, `capabilities` 4 and 4); the canvas regenerates the same counts as its default source and highlights a lifted capability edge as 3 elements of `serviceTree` and 9 of the master (`demo-estate-head8.txt`, `a11y-head8.txt`) | generated graphs and images, the lowering against Def. 5.17 and 5.18 beyond three inputs, the cardinality checked at ingest, more than two levels (O5.8) |
| 3.14 references | definition | C: `ReferencesTests.cs`: the `estate` has no relationship without an endpoint and so no references; a pod that a flow names and no source listed is `absent`, with the key it was minted from; an endpoint that another graph holds is `inOtherGraph` and names the graph; (capture category) on the newest capture the health graphs have no dangling edge and every master reference is explained. the references alone are paged by id and equal those of the snapshot, `snapshot?references=false` leaves them out, an earlier revision is read from the history; the static files hold the references and the meter declarations the host serves. E: 452 and 590 `absent` references in the master of the two captures at evaluation 12, none in a derived graph (`health-head16.txt`; 451 and 589 before meters were elements, `health-head7.txt`, `references-head9.txt`) | `inOtherGraph` occurs in a test only, not in the captures |
| 7.7 roll-up along dependency and composition | proved on the model | C: `WorkloadTests.cs` on the `estate`: in scenario B the database server, the database Service, api, web, API Management and the workload `Unhealthy`, the cache and the storage account `Healthy`; an observed call carries no failure (scenario D) | five scenarios on one fixture; no generated topology |
| serving a capture is a replay | engine rule | C: `ServeReplayTests.cs`: the late horizon is the replay value for a recorded capture served or replayed and 3 for a live source; serving the `estate`, `lab/fixtures` and (capture category) the newest real capture gives the state digest of replaying it; a stored run restarts with its horizon | a live source with backfilled input under horizon 3 (a stated limit, Ch. 7) |
| 6.4 (T8) | A | C (both stores): `Follow_returns_patches_in_order_with_their_frontiers`, `Commit_makes_all_revisions_visible_together_and_reads_follow_intervals`, `Seal_digest_equals_the_digest_of_the_model_at_every_revision_for_random_histories` (`StoreTests.cs`) | Cosmos |
| 6.5, 6.6 (T9, T10) | A | C for the bounded `Fixpoint`: `Fixpoint_derives_the_closure_within_its_bound_and_retracts_what_loses_its_support` (`max` 3 and 1, cut and restore of the middle edge of an acyclic chain; no cyclic input) and `A_fixpoint_step_that_changes_the_columns_is_rejected`; `Join_follows_inserts_and_retractions_on_both_sides_and_applies_the_residual`, `Join_prints_canonically_and_rejects_clashing_columns_and_conditions_without_an_equality`, `Traverse_prints_canonically_and_rejects_bad_depth_and_unknown_start`, `Mean_any_all_and_last_follow_inserts_and_retractions_and_are_null_over_no_value`, `Values_are_constant_rows_that_join_with_data_and_print_canonically`, `Values_rows_must_be_constants_of_one_shape`. C for `Union` and `Distinct`: `Union_adds_signed_rows_and_distinct_keeps_one_copy_while_any_copy_remains`, `Union_of_unequal_rows_and_an_unsupported_conflict_policy_are_rejected_with_a_typed_reason`, `Min_and_max_follow_inserts_and_retractions_and_are_null_over_no_value` (`OperatorTests.cs`) | T9 as stated (unbounded domain, F5 unchecked), T10 on cyclic input, `Delta` |
| 7.1 plane separation | A | C: `Plane_separation_telemetry_for_an_existing_relationship_creates_no_revision`, `Telemetry_never_touches_graph_revisions`, `Flows_between_unobserved_pods_make_dangling_calls_and_never_a_revision_per_window`; real capture: 674 master revisions for 235,858 flows and 175,265 flow tuples (`replay-head16-105738.txt`) | |
| 7.4 bounded churn | A | C: `Threshold_hysteresis_changes_the_structural_status_only_on_state_changes` | the bound `⌊N/m⌋` as a property; E16 on real latency series |
| 7.3 replayable telemetry-derived structure | conditional on T7 | see deviations D7 and D17 in 14.8 | |
| 8.x lens laws, T11, T12 | A (8.7 proved on a model, `sync` not built) | C and generated: GetPut, PutGet on 8 seeded groups of 12 random edit sequences, no PutPut, typed rejections, scope (`LensTests.cs`, Ch. 8, 8.5). T11 only for one lens link. T12: none (`sync` is not built) | O8.1 on a stack, O8.2 to O8.5 |
| 9.x edit replay, provenance | A | C for the full-replay configuration edit, the no-op edit, releases, preview and lineage at the sinks: `A_definition_edit_reactivates_the_space_and_emits_only_the_final_diff`, `A_no_op_definition_edit_creates_no_revision`, `Committing_a_draft_activates_it_and_every_release_is_listed_diffed_and_restorable`, `Preview_evaluates_a_draft_with_a_stated_scope_and_touches_nothing_live`, `Lineage_names_the_rule_sink_operators_and_source_elements`; an edge emitted from a `Group` without windows names every row of its group, forward and reverse (`LineageTests.cs`, 7 cases). None for the evidence history, the carry policy, rebuild mode and semiring provenance | O9.1 to O9.7 |
| 10.1, 10.2, 10.4, 10.6 | A | C: `Lost_event_records_mark_the_window_partial_and_are_counted`, `A_relist_replaces_the_known_set_of_its_kind` (four kinds), `A_relist_that_never_ends_deletes_nothing_and_a_relist_of_another_kind_leaves_this_kind_alone`, `A_loss_marker_is_evidence_on_the_telemetry_window_that_contains_it` (three markers), `Two_kinds_of_loss_in_one_window_are_both_named` (`ForwarderMarkerTests.cs`), `Diagnostic_activity_records_are_evidence_elements_linked_to_their_resource`, `Aks_audit_records_become_audit_events_and_lease_renewals_and_partial_stages_are_left_out`, `A_pod_warning_event_is_an_evidence_element_about_the_pod` (`EvidenceAdapterTests.cs`), `A_late_flow_revises_its_window_and_the_result_equals_the_in_order_run`, `Pod_lifecycle_from_watch_events_creates_updates_and_removes_the_entity_with_before_and_after`, `Contract_check_rejects_an_adapter_whose_row_contract_differs_from_the_catalog` | idempotent delivery by key and withdrawal with two supporting sources are not among the listed tests (O10.2, O9.8); forwarder markers only on fixtures. 10.7 live run reproduced by replay: C, `LiveReproducibilityTests.cs` (shuffled arrival, random flush points, the recording read in file order, the newest capture in the capture category), against a stand-in for the reader, not on Event Hubs |
| 11.1 to 11.5, 11.7, 11.8 | A (relative to S1 to S5) | C for in-memory and SQLite (the same cases run on the Cosmos emulator when an endpoint is given, set A reports 418 of 418 tests in a full run with the emulator at evaluation 4, 481 of 481 at evaluation 5 and 536 of 536 at evaluation 6; this paper ran 29 contract cases without it; the Cosmos cases were not run by this paper): `Stale_head_conflicts_and_writes_nothing`, `A_failing_patch_leaves_no_partial_batch`, `Uniqueness_of_source_target_type_is_enforced_atomically`, `Seal_digest_equals_the_digest…`, `Retype_keeps_the_relationship_id_and_history`, `Follow_returns_patches_in_order…`. Telemetry port: `Append_dedupes_replaces_and_reports_closed_windows_once_in_order`, `Delete_retracts_a_window…`, `Retention_evicts_old_windows` | the Cosmos protocol on a real account (chunks and the lease are tested on the emulator by set B: a 3.6 MB commit reads back whole on all three stores, orphan chunks are not a commit, a second writer is refused until the lease expires or is released), watermark and overlay (not exercised), E1 to E8, E12 |
| 12.10 tags and caching | (a) A, (b) engine rule | C: `EtagTests.cs` (different definitions at one catalog revision give different tags, equal content gives equal tags after a restart and in another host, a stale `If-None-Match` answers the body and a matching one `304`, the tag depends on the media type and the content only); E: a stale tag of the old form answers `200`, the current tag `304`, and the tag is the same after a restart from SQLite, on the `estate` fixture (`etag-head6.txt`) | a browser cache across a redeploy, end to end |
| 12.x | A | canvas: 643 tests (33 files) at the canvas of evaluation 8 including patch application and rejection, the engine source against a fake socket, the editor's undo and redo, color by role (`color-by.test.ts`, `ui/color.test.tsx`), the definitions structures (`structures.test.ts`), the URL state (`url.test.ts`), axe on every view in jsdom, the pure builders of the operator walkthroughs, the document view (parsing the engine's Markdown, immutable revisions, `304` on the head, following the timeline and a patch, axe), the aggregate contexts and the slot kinds of the palette (read from a synced copy of `/palette`), the durations, the default coloring from several hinted properties, the identity effects of a preview, the static source on the engine's files of the `estate` (`static-source.test.ts`, `ui/estate.test.tsx`), references and their stubs (`reference.test.ts`), the collapse of supporting elements (`view-model.test.ts`) and the refusal of the generator to write a graph without edges (`make-demo.test.ts`); engine: the served history is capped without changing heads, state or sequence, older revisions answer `Gap`, a resuming subscriber gets what it missed or `gap` (`RetentionOfHistoryTests.cs`), the ontology of `health` serves value roles and a preview of the `estate` reports the merge of its `Link` (`EstateTests.cs`); engine: every numeric parameter of the palette has a quantity and a unit, durations are nanoseconds on the wire (`PaletteQuantityTests.cs`), and the synthetic `estate` fixture gives every graph entities, a `Link` that merges identities, a union of two sources, retained evidence, node and cluster telemetry with windows and an unhealthy component that is so because of what it depends on, and the operator shapes of the walkthroughs produce output, with a closure larger than its base (`EstateTests.cs`); engine documents: `The_deployment_report_is_a_markdown_document_with_one_keyed_row_per_deployment`, `A_document_is_a_function_of_the_graph_state_and_has_a_digest_per_revision_that_follows_the_health_changes`, `Only_graphs_with_the_document_ontology_are_documents`, four escape cases, `Blocks_are_rendered_in_the_order_they_are_given_and_the_graph_order_does_not_matter` (`DocumentTests.cs`); engine: `The_event_stream_is_ordered_by_seq_resumable_and_reports_gap_and_ahead`, `Definitions_are_served_as_a_graph_with_the_kernel_as_ontology` | replica digest end to end (O12.1) |

## A.9 Evaluation log

The paper was evaluated twelve times. In evaluations 1 to 9 the authors ran the experiments themselves on a clean copy of the recorded state. From evaluation 10 on they ran no test suite: the figures are those of set A, and the authors read the code. Each row says what the software contained, what the authors ran, and what they did not. The files are in `paper/evidence/`.

| No. | Date, and what the engine and the canvas contained | Run by this paper | Files | Not run |
|---|---|---|---|---|
| 1 | 2026-11-10. Engine: the first slice, 12 definition modules, in-memory and SQLite stores. Canvas: the viewer of that day | 259 engine and 362 canvas tests; `check` (12 modules, 10 projections); replay of the first capture, at several chunk sizes (D17 found); health sensitivity | `tests.txt`, `check.txt`, `replay-latest.txt`, `replay-chunks.txt`, `chunk-50-defect.txt`, `health-sensitivity.txt` | not recorded |
| 2 | 2026-11-10. Engine: 13 modules, evidence elements and Deployments read, the fixes of D17. Canvas: the editor on drafts | 323 engine tests (those that need a capture returned early, the captures were not in the export); 456 canvas tests, one of which timed out under load and passed alone; `check` (13 modules, 8 projections, 0 errors, 0 warnings); replays of both captures and at several chunk sizes (D21 found); health and deployment health | `tests-head.txt`, `check-head.txt`, `replay-head-105738.txt`, `replay-head-140542.txt`, `replay-chunks-head.txt`, `chunk-1-defect.txt`, `health-head.txt`, `deployment-health-head.txt` | not recorded |
| 3 | 2026-11-10. Engine: 15 modules with document projections, identity merge. Canvas: 489 tests | 338 engine tests, 23 storage contract cases, 489 canvas tests; `check` (15 modules, 9 projections, 0 errors, 0 warnings); replays; five chunk sizes on both captures; a document served and rendered | `tests-head2.txt`, `check-head2.txt`, `replay-head2-105738.txt`, `replay-head2-140542.txt`, `replay-chunks-merge.txt`, `health-head2.txt`, `document-head.txt` | Cosmos emulator, benchmarks, accessibility script, walkthroughs, live Event Hubs input, azd |
| 4 | 2026-11-10. Engine: the order-independent survivor, journal and restart, chunked commits and a lease on Cosmos, retention of evidence. Canvas: the document view | 384 engine tests and 18 in the capture category (captures linked into the export from here on), 29 storage contract cases, 547 canvas tests, clean `tsc`; replays; five chunk sizes; the report document at two chunk sizes; a restart from a SQLite store; master size against retention age; walkthroughs on `lab/fixtures` (8 of 8, four preview no entities); ten runs each of H5, H7 (memory and SQLite), H8, H10 | `tests-head3.txt`, `check-head3.txt`, `replay-head3-105738.txt`, `replay-head3-140542.txt`, `replay-chunks-head3.txt`, `health-head3.txt`, `document-head3.txt`, `restart-head3.txt`, `retention-ages.txt`, `walkthroughs-head3.txt`, `perf-h5-memory-paper.txt`, `perf-h5-sqlite-paper.txt`, `perf-h7-paper.txt`, `perf-h8-paper.txt`, `perf-h10-paper.txt` | Cosmos cases and replay, accessibility script, live document, live Event Hubs input, azd |
| 5 | 2026-11-10. Engine: the state seal, batched Cosmos writes, live resume, the synthetic `estate` fixture. Canvas: the live document, walkthroughs that assert data | 443 engine tests and 18 capture, 29 storage contract cases, 583 canvas tests, clean `tsc`; replays; five chunk sizes (the digests of evaluation 4); restart without and with a state seal, on a fixture and on the journals of both captures; the synthetic `estate`; walkthroughs strict on the `estate` and with `--allow-empty` on the newest capture; accessibility script; live document; ten runs each of H5, H8, H10 | `tests-head4.txt`, `check-head4.txt`, `replay-head4-105738.txt`, `replay-head4-140542.txt`, `replay-chunks-head4.txt`, `health-head4.txt`, `document-head4.txt`, `restart-head4.txt`, `restart-real-head4.txt`, `restart-seal-prefix-head4.txt`, `retention-ages-head4.txt`, `estate-head4.txt`, `walkthroughs-head4.txt`, `a11y-head4.txt`, `document-live-head4.txt`, `perf-h5-memory-head4-paper.txt`, `perf-h5-sqlite-head4-paper.txt`, `perf-h8-head4-paper.txt`, `perf-h10-{1h,4h}-{seal,noseal}-head4-paper.txt`, `perf-h10-4h-seal38000-head4-paper.txt` | Cosmos, H7, live Event Hubs input, azd |
| 6 | 2026-11-11. Engine: content tags, a capped served history, journal verification, the tests of B1 and B2. Canvas: default coloring from several properties, identity effects in the preview. Lab: names from the azd environment, fixtures scrubbed further | 403 engine tests, 96 in the slow category, 18 capture, 29 storage contract cases, 599 canvas tests, clean `tsc` (from here on a copy of the local space overlay is linked with the captures); replays; five chunk sizes (the digests of evaluation 4); restart without and with a seal, with `--verify-journal`, a deleted journal entry before a seal, the journals of both captures; content tags (a stale tag, the current tag, a restart, a `410 Gap`); the newest capture without and with the overlay; the tests of B1 and B2 with each fix reverted; retention ages; the `estate`; walkthroughs as in 5; accessibility script with the roles check; live document; ten runs each of H5, H8, H10 | `tests-head6.txt`, `check-head6.txt`, `replay-head6-105738.txt`, `replay-head6-140542.txt`, `replay-chunks-head6.txt`, `health-head6.txt`, `document-head6.txt`, `restart-head6.txt`, `restart-seal-prefix-head6.txt`, `restart-real-head6.txt`, `etag-head6.txt`, `bench-overlay-head6.txt`, `mutation-head6.txt`, `retention-ages-head6.txt`, `estate-head6.txt`, `walkthroughs-head6.txt`, `a11y-head6.txt`, `document-live-head6.txt`, `perf-h5-memory-head6-paper.txt`, `perf-h5-sqlite-head6-paper.txt`, `perf-h8-head6-paper.txt`, `perf-h10-{1h,4h}-{seal,noseal}-head6-paper.txt` | Cosmos, H7, live Event Hubs input, azd (no cluster exists) |
| 7 | 2026-11-11. Engine: edge lifting across levels, references, lineage in both directions, a served capture as a replay. Canvas not run | 456 engine tests, 96 slow, 20 capture; `check` (15 modules, 9 projections, 0 errors, 0 warnings); replays; five chunk sizes (one digest per capture, new values, 14.4); the health model and the references of both captures; the three levels and the health model on the `estate` | `tests-head7.txt`, `check-head7.txt`, `replay-head7-105738.txt`, `replay-head7-140542.txt`, `replay-chunks-head7.txt`, `health-head7.txt`, `estate-head7.txt` | the canvas; benchmarks (the H5, H8 and H10 figures are those of evaluation 6); the restart, tag and retention checks of evaluation 6; Cosmos |
| 8 | 2026-11-11. Canvas: default source from engine output, abstraction highlight, reference stubs, collapsed supporting elements. Engine of evaluation 7 | canvas only: 643 tests in 33 files, clean `tsc`; walkthroughs strict on the `estate` (8 of 8); accessibility script (32 page and dialog checks, 12 keyboard and highlight checks, role colors in 3 themes); the default source regenerated from the engine, byte for byte the committed files; the live document (revisions 2 and 3, the head digest equal to the engine's) | `canvas-tests-head8.txt`, `walkthroughs-head8.txt`, `a11y-head8.txt`, `demo-estate-head8.txt`, `document-live-head8.txt` | the engine runs (the engine is that of evaluation 7); walkthroughs on a real capture; a browser on the served newest capture (set A's observations, Ch. 12, 12.4) |
| 9 | 2026-11-11. Engine: the references route and file, meter declarations in the static output, lineage of grouped emits, the single image of `Realizes`. Canvas of evaluation 8 | 463 engine tests, 96 slow, 20 capture; `check` (15 modules, 9 projections, 0 errors, 0 warnings); replays and five chunk sizes (the output of evaluation 7 apart from wall times, the same digests); the `estate` with the 94 static files of protocol 3.12; the references files of both captures (451 and 589 lines for the master); canvas: 643 tests and clean `tsc` again, walkthroughs strict on the `estate` (8 of 8, the same previews) and the accessibility script (output identical to evaluation 8) against this engine | `tests-head9.txt`, `check-head9.txt`, `replay-head9-105738.txt`, `replay-head9-140542.txt`, `replay-chunks-head9.txt`, `estate-head9.txt`, `references-head9.txt`, `canvas-head9.txt` | benchmarks; the restart, tag and retention checks of evaluation 6; a recount of the health model (the digests are unchanged); Cosmos; walkthroughs on a real capture; the live document |
| 10 | 2026-11-11. Engine: the `lift` statement as the operator kind `Lift`. Canvas of evaluation 8 | nothing run by this paper; read: the `lift` statements of the definitions, the tests of the `lift` statement and of the kernel, the kernel table, and the description of the protocol, version 3.13. From set A: 639 of 639 engine tests in the full run with the Cosmos emulator; the state digests of the newest capture and of the estate equal before and after the rewrite with `lift`; `serviceTree` 19 and 29 on the capture and 4 and 5 on the estate, `capabilities` 4 and 4 on the estate | none | everything; the canvas `Lift` card was verified in evaluation 11 |
| 11 | 2026-11-11. Canvas: the `Lift` card, the references route, telemetry in the default source. Engine of evaluation 10 | nothing run by this paper; read: the canvas README and the committed default source (94 files, with meter declarations and references). From set A: clean `tsc`, 672 canvas tests, the accessibility script with all checks passing, walkthroughs strict on the `estate` 10 of 10, among them `lift-service-tree` (82 commands) and `lift-capabilities` (67 commands), both equal to the originals | none | everything |
| 12 | 2026-11-11. Engine: the fixes of the defects found by the review and the differential harness, threshold episodes, the conflict rule, one identity per activity event. Canvas of evaluation 11 | one replay of each real capture and one with `--out` for the health model and the references, with the Release build of the working tree (nothing built by this paper). From set A: `Sattle.Differential` 149 of 149 (the engine's incremental result equals the reference evaluator's batch result in all 32 worlds, under every batch cut and under reordering inside a batch); `Sattle.Tests` 670 of 670 with the slow and capture categories; all 21 known-defect tests pass; the served engine answers a foreign origin with 403. Reported by set B and repeated by nobody: the 150-seed run of the harness, and the digests at chunk sizes 1, 50 and 100,000 | `replay-head16-105738.txt`, `replay-head16-140542.txt`, `health-head16.txt` | every test suite; chunk sizes other than the default; the estate and `lab/fixtures` digests; benchmarks; the canvas |

Evaluation 11a is not an evaluation by this paper: it names the engine of evaluation 10 with the reference evaluator and the differential harness added and the defects they found not yet fixed (2026-11-11).

Figures from other contributors, copied unchanged (`turn8` in a file name is set A's own name for its run at evaluation 5): set A's ten-run output at evaluation 4 (`perf-h5-memory-orch.txt`, `perf-h5-sqlite-orch.txt`, `perf-h7-orch.txt`, `perf-h8-orch.txt`, `perf-h10-orch.txt`) and at evaluation 5 (`perf-h8-orch-turn8.txt`, `perf-h10-1h-orch-turn8.txt`, `perf-h10-4h-orch-turn8.txt`, `perf-h10-1h-seal-orch-turn8.txt`, `perf-h10-4h-seal-orch-turn8.txt`, `cosmos-cap-orch-turn8.txt`). Figures of sets A and B at evaluation 6 are cited from `engine/PERF.md`.
The cluster name is redacted in the evidence files. The captures and the space overlay they need are gitignored.
