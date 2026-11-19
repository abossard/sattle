const shopChapters = {
    origins: {
      figure:"The shop inside the master graph", context:"The shop on Azure",
      description:"A request to pg-shop sits inside a larger graph of the observed estate. The workload and health views draw from that graph.",
      example:"The shop’s database is one part of a larger estate.",
      principle:"Layers of projection", meaning:"One master graph can answer several different questions.",
      section:"0.2", reading:"ch00-introduction.md#02-the-idea-and-the-terms-it-needs",
      papers:["ch00-introduction","ch05-projection-language"]
    },
    rules: {
      figure:"How the alarm works", context:"Shop namespace",
      description:"A workload boundary, a two-state alarm and a required-call switch show how rules shape the model.",
      example:"Two good minutes clear the pg-shop alarm.",
      principle:"Thresholds with memory", meaning:"A rule can remember earlier readings before it changes state.",
      section:"7.2", reading:"ch07-telemetry-plane.md#72-definitions",
      papers:["ch07-telemetry-plane"]
    },
    layers: {
      figure:"The quotient graph", context:"Three web pods and two API pods",
      description:"Individual pod calls combine into one service edge. Azure records meet at a single server identity.",
      example:"Calls between pods become one web → API link.",
      principle:"Levels of abstraction", meaning:"A projection can group both the parts and their connections.",
      section:"5.8", reading:"ch05-projection-language.md#58-levels-of-abstraction-a-projection-lifts-the-edges-with-the-nodes",
      papers:["ch01-values-and-identity","ch02-ontologies-as-type-graphs","ch05-projection-language"]
    },
    incident: {
      figure:"The outage across time", context:"10:11–10:15 UTC",
      description:"Rows of a time chart align the meter, alarm and callers. The chart separates the first good reading from recovery.",
      example:"Web turns red with pg-shop. The cache stays healthy.",
      principle:"Health roll-up", meaning:"A failure can affect any caller that depends on that part.",
      section:"7.4", reading:"ch07-telemetry-plane.md#74-worked-example-the-workload-model",
      papers:["ch07-telemetry-plane"]
    },
    dependency: {
      figure:"The two-run experiment", context:"Two runs at 10:06:30 UTC",
      description:"One cache reading feeds two runs. An extra API dependency changes which callers inherit its warning.",
      example:"The gateway turns amber when the API gains a cache link.",
      principle:"Evidence for dependencies", meaning:"A new link can change health with the same meter readings.",
      section:"7.4", reading:"ch07-telemetry-plane.md#74-worked-example-the-workload-model",
      papers:["ch05-projection-language","ch09-evolution-evidence-provenance"]
    },
    azure: {
      figure:"Matching the cache host", context:"Azure Resource Graph",
      description:"The model matches a host in the API configuration to a Redis resource by name and type. A separate meter reading can establish health.",
      example:"Resource Graph lists offers-cache. Its health stays Unknown.",
      principle:"Missing measurements", meaning:"A resource record identifies the cache. A reading can establish health.",
      section:"7.5", reading:"ch07-telemetry-plane.md#75-retention-fill-and-no-data",
      papers:["ch09-evolution-evidence-provenance","ch10-sources-and-adapters"]
    },
    replay: {
      figure:"The revision history", context:"Workload revisions 15–18",
      description:"A snapshot and ordered patches reconstruct the outage. Two calculation paths lead to the same graph.",
      example:"Both ways of rebuilding revision 16 show the outage.",
      principle:"Incremental equivalence", meaning:"Updating a graph in steps can give the same result as a rebuild.",
      section:"6.3", reading:"ch06-semantics-and-incrementality.md#63-theorems",
      papers:["ch03-graphs-and-revisions","ch04-change-algebra","ch06-semantics-and-incrementality"]
    },
    inspect: {
      figure:"The pg-shop case file", context:"Canvas at workload revision 16",
      description:"Trace the web symptom to the database’s own alarm, inspect the records behind the link, then preview a query draft.",
      example:"The database link leads back to a hostname and an ID.",
      principle:"Lineage", meaning:"A result can point back to the records that support it.",
      section:"9.4", reading:"ch09-evolution-evidence-provenance.md#94-provenance-record-and-the-history-of-an-element",
      papers:["ch09-evolution-evidence-provenance","ch12-outputs-documents-and-canvas"]
    }
  };
const ShopMap = createChapterFigures(shopChapters, {...ModelScenes, ...ChangeScenes, ...ReviewScenes}, "Shop replay");
