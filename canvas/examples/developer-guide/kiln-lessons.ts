import type { Lesson } from "../../src/tutorial/lessons";

export const kilnLessons: readonly Lesson[] = [
  {
    id: "kiln-policy", space: "kiln", source: "kiln-example", module: "workshop",
    title: "Change when a reading expires", goal: "Decide how long to trust the chamber temperature after the probe falls silent.",
    action: "Start the kiln replay. Open the projection editor → GKQL. Change freshness from 2m to 10m. Choose Check & store, then Preview.",
    check: "The rating row shows one changed node. The longer limit keeps the chamber Ready at the final checkpoint. Choose Discard draft to restore the exercise.",
    view: "data", graph: "rating", projection: "rating",
    flow: [[["Temperature readings", "input"]], [["One-minute mean", "rule"]], [["Entry and recovery windows", "rule"], ["Freshness limit", "rule"]], [["Chamber state", "projection"]]],
    files: ["workshop.rating.gkql.json"],
    commands: [
      { label: "Start the kiln replay", code: "python3 scripts/kiln-guide.py serve" },
      { label: "Compare two and four high windows", code: "python3 scripts/kiln-guide.py compare" },
    ],
    prompt: "Compare the two kiln policies on the same capture. Which UTC window first went Hot in each run? Find the recovery window and explain why the chamber later reports No reading.",
  },
  {
    id: "kiln-impact", space: "kiln", source: "kiln-example", module: "workshop",
    title: "Trace one reading to two consumers", goal: "Explain why a hot chamber changes the firing line while the display stays Ready.",
    action: "Move the timeline to impact revision 2. In Elements, set List to Edges. Select each Uses edge and read its role and observed state.",
    check: "Both edges point to the same chamber and record Hot. The firing line is Hot. The display stays Ready.",
    view: "data", graph: "impact", projection: "impact",
    flow: [[["One chamber probe", "input"]], [["Controls firing", "rule"], ["Display only", "rule"]], [["Firing line · Hot", "projection"], ["Display · Ready", "projection"]]],
    files: ["workshop.impact.gkql.json"],
    commands: [{ label: "Read the hot checkpoint", code: "curl -fsS 'http://127.0.0.1:5086/graphs/impact/snapshot?revision=2' | python3 -m json.tool" }],
    prompt: "At impact revision 2, compare the two Uses edges. Cite their target IDs, roles and observed state. Which consumer inherits Hot, and which GKQL expression makes the decision?",
  },
  {
    id: "kiln-completion", space: "kiln", source: "kiln-example", module: "workshop",
    title: "Prove a batch finished", goal: "Find the receipt that proves batch-A finished.",
    action: "At progress revision 1, inspect batch-A and batch-B. Move to revision 2. Follow batch-A’s SupportedBy links to its cooled receipt.",
    check: "At revision 1, A is Accepted with completion Unknown while B is Finished. A becomes Finished on its own receipt at 10:05:15 UTC.",
    view: "data", graph: "progress", projection: "progress",
    flow: [[["Accepted receipt", "input"], ["Cooled receipt", "input"]], [["Match batch ID", "identity"]], [["Submission", "projection"], ["Completion", "projection"]]],
    files: ["workshop.progress.gkql.json"],
    commands: [{ label: "Read the batches before A finished", code: "curl -fsS 'http://127.0.0.1:5086/graphs/progress/snapshot?revision=1' | python3 -m json.tool" }],
    prompt: "Trace batch-A’s completion to the receipt with its batch ID and time. Why did batch-B's earlier cooled receipt leave A's completion Unknown?",
  },
];
