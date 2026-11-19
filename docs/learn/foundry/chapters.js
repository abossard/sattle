// The figure, the replay fact and the paper section stay together on both screens.
const retailChapters = {
  estate:{figure:"From source records to five views",context:"Order O-104 · refund",
    description:"Source records start as an ungrouped graph. Exact identifiers connect evidence across sources. Two projections group the graph by technology and by capability. Five views show the same shop from different angles.",
    example:"Two sources use the same storage account ID.",principle:"Projection",meaning:"A projection defines how to build a graph from its inputs.",
    section:"0.2",reading:"ch00-introduction.md#02-the-idea-and-the-terms-it-needs"},
  rollup:{figure:"How a database alarm reaches checkout",context:"Recorded pg-shop outage",
    description:"Follow a database alarm through the services that depend on it. Compare the meter, the alarm rule and the health state in the graph.",
    example:"The database alarm reaches the API and web.",principle:"Health roll-up",meaning:"Each service reflects the dependencies that can affect it.",
    section:"7.4",reading:"ch07-telemetry-plane.md#74-worked-example-the-workload-model"},
  discovery:{figure:"When the system changes",context:"pg-shop · new API call to Redis",
    description:"An API pod starts using Redis. Follow the new observation through the existing rules to see the health model update automatically. Inspect the resulting patch and its evidence.",
    example:"The API starts calling Redis. The model picks it up.",principle:"Patches and provenance",meaning:"The rules update the graph and keep the evidence for the link.",
    section:"9.4",reading:"ch09-evolution-evidence-provenance.md#94-provenance-record-and-the-history-of-an-element"},
  gkql:{figure:"Reading a GKQL program",context:"X12 · deployment health",
    description:"Follow the wired operations of a GKQL program. Match ready pods to deployments, count them, then trace two pod calls to one link between workloads.",
    example:"Two pod calls support one Talks link.",
    principle:"Levels of abstraction",meaning:"A projection can lift a link to the workloads at its ends.",
    section:"5.8",reading:"ch05-projection-language.md#58-levels-of-abstraction-a-projection-lifts-the-edges-with-the-nodes"}
};
const ShopMap = createChapterFigures(retailChapters, {estate:ShopJourneyScene, ...ShopReplayScenes, gkql:ShopGkqlScene}, "Sattle", story);
