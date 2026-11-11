# Code review (max), early note for the engine baseline

Review still running. Two verified points that affect the baseline being measured now. Review-only session: nothing changed.

- **`bench estate` measures a graph with about 300k phantom relationships.** A row builder marks every column present when a row is added (`Operators.cs:58`, `Columnar.cs:59`), so a column the writer never sets reads as present with code 0. `Synthetic.cs:22` never sets `nodeName`, `deployment`, `ready`, `restarts`: every synthetic pod gets `RunsOn` and `ControlledBy` to a dangling phantom id (consistent with PERF.md:58, 952,724 master relationships) and `ready=false`. `Bench.cs:75-76` leaves `parentId` unset: all 300k VMs sit in the `machineParents` join state of `acme.tree.sattle:63` (no phantom parent results, the bench loads no Node rows). Memory per element and close times in PERF.md include this.
- **`bench restart` with `--hours` above the capture span**: passes shift only `CapturedNs` (`Bench.Latency.cs:298`). From pass 2 on ARG and authoring rows reach multiplicity 2, 3, and flows and Prometheus samples are late and dropped. The "1 h history" restart time is not one hour of history.
- **Windows.** The benchmark ran 3 to 4 telemetry windows. Costs that grow with the look-back are not in the numbers: Series expiry walks every element and every retained window on each Advance (`Output.cs:108-110`), Threshold replays every retained window per touched key (`Operators.cs:641-658`), Rollup adds a slot per key per window that is never freed (`Operators.cs:602`, KeyTable has no remove), the telemetry change log keeps up to 4,000,000 records per graph (`Stores.cs:523, 560-563`).

Full findings follow in `07-code-review-findings.md`.
