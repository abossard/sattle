// These definitions stay in view while the chapter figure changes.
const ShopTerms = {
  origins: [
    ["Dependency", "An arrow points from something that needs a service to the service it needs."],
    ["Master graph", "The graph of entities and links that Sattle builds from its sources. Each dot represents one entity."],
    ["Projection", "A projection is a rule that builds one graph from other graphs. The health model is built this way from the workload map, readings and rules."]
  ],
  rules: [
    ["Scope", "The cluster and namespace set the shop’s boundary. Its services can depend on resources beyond it."],
    ["Signal rule", "People can choose and refine the failure and recovery rules. A rule with memory uses earlier readings too."],
    ["Required call", "A health rule can pass trouble back to a caller when that caller needs the service to finish its work."]
  ],
  layers: [
    ["Identity", "Two sources can describe the same thing. A resource row from Azure Resource Graph and a reading from Azure Monitor use the same server ID."],
    ["Projection", "One service link can stand for several calls. The query groups pods by the service they belong to."],
    ["Ontology", "People can maintain this vocabulary. It defines the kinds of things in a graph and the links that can connect them."]
  ],
  incident: [
    ["Sample time", "The chart places a reading at the time the meter took it. The recorder can receive it later."],
    ["Impact", "A failure can reach distant callers along a chain of required dependencies."],
    ["Recovery", "A reading describes one moment. The database alarm needs two good minutes before it clears."]
  ],
  dependency: [
    ["Degraded", "An amber triangle marks a warning. The busy cache and the services that rely on it show that warning."],
    ["Evidence", "The API’s cache setting supplies evidence for a new dependency."],
    ["Controlled comparison", "The two runs use the same readings and rules. Changing one setting shows the effect of the new link."]
  ],
  azure: [
    ["Unmatched address", "An address in the API’s settings names a destination. Its Azure resource still needs a match."],
    ["Resource match", "The configured host matches the cache name and Redis suffix in the replay’s resource query."],
    ["Unknown", "A resource record identifies the cache. Without a meter reading, its health stays Unknown."]
  ],
  replay: [
    ["Patch", "A list of edits carries the graph from one version to the next."],
    ["Revision", "A revision identifies a version of one graph."],
    ["Replay", "The same recorded inputs, clock events and rules let Sattle reproduce a past graph."]
  ],
  inspect: [
    ["Origin", "A component whose own signal raised an alarm. Other components may show its effects."],
    ["Lineage", "The trail from a result, through the queries that produced it, back to its source records."],
    ["Query draft", "A draft can test another rule against the same evidence before a change takes effect."]
  ]
};
