// The figure, the replay fact and the paper section stay together on both screens.
const retailChapters = {
  estate:{figure:"From source records to five views",context:"Order O-104 · refund",
    description:"Source records start as an ungrouped graph. Exact identifiers connect evidence across sources. Two projections group the graph by technology and by capability. Five views show the same shop from different angles.",
    example:"Two sources use the same storage account ID.",principle:"Projection",meaning:"A projection defines how to build a graph from its inputs.",
    section:"1.2",reading:"core/ch01-problem-and-model.md#12-the-model"},
  rollup:{figure:"Health Tree",context:"pg-shop · web service",
    description:"Explore the live health model as a tree rooted at the web service. Follow its branches back to the master graph, inspect the database alarm rule, and see how the query finds the worst contributing state.",
    example:"PostgreSQL first missed its check at 10:11 UTC.",principle:"Health roll-up",meaning:"A service reports the worst severity it can reach.",
    section:"7.4",reading:"ch07-telemetry-plane.md#74-worked-example-the-workload-model"},
  discovery:{figure:"System Changes",context:"pg-shop · new API call to Redis",
    description:"Compare the API’s dependencies before and after its first observed call to Redis. The health model adds a branch to the existing cache. Trace that branch to the endpoint and DNS records that identify it.",
    example:"Redis now appears under both web and the API.",principle:"Provenance",meaning:"A derived link can be traced to the records that explain it.",
    section:"9.4",reading:"ch09-evolution-evidence-provenance.md#94-provenance-record-and-the-history-of-an-element"},
  gkql:{figure:"Reading a GKQL program",context:"X12 · deployment health",
    description:"Follow the wired operations of a GKQL program. Match ready pods to deployments, count them, then trace two pod calls to one link between workloads.",
    example:"Two pod calls support one Talks link.",
    principle:"Levels of abstraction",meaning:"A projection can lift a link to the workloads at its ends.",
    section:"5.8",reading:"ch05-projection-language.md#58-levels-of-abstraction-a-projection-lifts-the-edges-with-the-nodes"}
};
const ShopMap = createChapterFigures(retailChapters, {estate:ShopJourneyScene, ...ShopReplayScenes, gkql:ShopGkqlScene}, "Sattle", story);
