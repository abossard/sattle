// The figure, the replay fact and the paper section stay together on both screens.
const retailChapters = {
  estate:{figure:"From source records to five views",context:"Order O-104 · refund",
    description:"Source records start as an ungrouped graph. Exact identifiers connect evidence across sources. Two projections group the graph by technology and by capability. Five views show the same shop from different angles.",
    example:"Two sources use the same storage account ID.",principle:"Projection",meaning:"A projection defines how to build a graph from its inputs.",
    section:"0.2",reading:"ch00-introduction.md#02-the-idea-and-the-terms-it-needs",tint:"#202020",captionInk:"#f4f4f4"},
  rollup:{figure:"How a database alarm reaches checkout",context:"Recorded pg-shop outage",
    description:"Follow a database alarm through the services that depend on it. Compare the meter, the alarm rule and the health state in the graph.",
    example:"The database alarm reaches the API and web.",principle:"Health roll-up",meaning:"Each service reflects the dependencies that can affect it.",
    section:"7.4",reading:"ch07-telemetry-plane.md#74-worked-example-the-workload-model",tint:"#fbf7ec"},
  discovery:{figure:"A new Redis dependency",context:"Controlled pg-shop replay",
    description:"Follow a recorded network call from its source through the structure and health projections. Inspect the patch and its evidence.",
    example:"A new call connects two services.",principle:"Patches and provenance",meaning:"A patch records the edit. Its evidence explains the link.",
    section:"9.4",reading:"ch09-evolution-evidence-provenance.md#94-provenance-record-and-the-history-of-an-element",tint:"#112b3a",captionInk:"#e4f1ef"}
};
const ShopMap = createChapterFigures(retailChapters, {estate:ShopJourneyScene, ...ShopReplayScenes}, "Sattle");
